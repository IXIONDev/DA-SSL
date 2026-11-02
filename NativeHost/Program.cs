using System.IO;
using System.Text.Json;

namespace SSLExceptionManager;

internal static class Program
{
    private static readonly AllowlistStore Store = new();
    private static readonly AppLogger Logger = new();
    private static readonly SecurityChecks Checks = new(Store, Logger);

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--emergency-revoke-worker")
            return await EmergencyRevoker.RunWorkerAsync(args[1..]);
        using var cancellation = new CancellationTokenSource();
        var protocol = new NativeMessaging(Console.OpenStandardInput(), Console.OpenStandardOutput());
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                using JsonDocument? doc = await protocol.ReadAsync(cancellation.Token);
                if (doc is null) break;
                JsonElement root = doc.RootElement;
                string id = ReadString(root, "id") ?? Guid.NewGuid().ToString("N");
                string? command = ReadString(root, "command");
                try
                {
                    switch (command)
                    {
                        case "getState":
                        {
                            string extensionId = RequiredString(root, "extensionId");
                            if (!EmergencyRevoker.IsAuthorizedExtension(extensionId)) throw new ArgumentException("The extension ID does not match the installed helper.");
                            await protocol.WriteAsync(new { id, ok = true, origins = await Store.GetAsync(cancellation.Token), chromeDecisions = EmergencyRevoker.GetSavedDecisionOrigins(extensionId), emergencyStatus = EmergencyRevoker.ReadStatus() }, cancellation.Token);
                            break;
                        }
                        case "emergencyRevoke":
                        {
                            string origin = AllowlistStore.NormalizeOrigin(RequiredString(root, "origin"));
                            string extensionId = RequiredString(root, "extensionId");
                            EmergencyRevoker.QueueResult result = await EmergencyRevoker.QueueAsync(origin, extensionId, cancellation.Token);
                            await protocol.WriteAsync(new { id, ok = result.Ok, queued = result.Queued, origin = result.Ok ? origin : null, profileDirectory = result.ProfileDirectory, error = result.Error, message = result.Ok ? "Chrome will close to remove this saved decision." : null }, cancellation.Token);
                            if (result.Ok) Logger.Info("User-confirmed emergency SSL decision removal queued for Chrome shutdown.");
                            break;
                        }
                        case "addOrigin":
                        {
                            string origin = RequiredString(root, "origin");
                            var origins = await Store.AddAsync(origin, cancellation.Token);
                            Logger.Info("Origin added to explicit allowlist.");
                            await protocol.WriteAsync(new { id, ok = true, origins }, cancellation.Token);
                            break;
                        }
                        case "removeOrigin":
                        {
                            string origin = RequiredString(root, "origin");
                            string verifiedTabOrigin = RequiredString(root, "verifiedTabOrigin");
                            long timestamp = root.GetProperty("timestamp").GetInt64();
                            int tabId = root.GetProperty("tabId").GetInt32();
                            if (!IsFresh(timestamp, 60_000) || tabId < 0 || AllowlistStore.NormalizeOrigin(verifiedTabOrigin) != AllowlistStore.NormalizeOrigin(origin))
                                throw new ArgumentException("The tab confirmation expired or does not match the requested origin.");
                            var origins = await Store.RemoveAsync(origin, cancellation.Token);
                            Logger.Info("Origin removed from explicit allowlist.");
                            await protocol.WriteAsync(new { id, ok = true, queued = true, originRemoved = true, origins }, cancellation.Token);
                            _ = RevokeQueuedAsync(protocol, id, origin, true, verifiedTabOrigin, timestamp, tabId, cancellation.Token);
                            break;
                        }
                        case "removeOriginOnly":
                        {
                            string origin = RequiredString(root, "origin");
                            var origins = await Store.RemoveAsync(origin, cancellation.Token);
                            Logger.Info("Origin removed from explicit allowlist without changing Chrome SSL state.");
                            await protocol.WriteAsync(new { id, ok = true, queued = false, originRemoved = true, origins }, cancellation.Token);
                            break;
                        }
                        case "revokeChromeDecision":
                        {
                            string origin = RequiredString(root, "origin");
                            origin = AllowlistStore.NormalizeOrigin(origin);
                            string verifiedTabOrigin = RequiredString(root, "verifiedTabOrigin");
                            long timestamp = root.GetProperty("timestamp").GetInt64();
                            int tabId = root.GetProperty("tabId").GetInt32();
                            if (!IsFresh(timestamp, 60_000) || tabId < 0 || AllowlistStore.NormalizeOrigin(verifiedTabOrigin) != origin)
                                throw new ArgumentException("The tab confirmation expired or does not match the requested origin.");
                            await protocol.WriteAsync(new { id, ok = true, queued = true, originRemoved = false }, cancellation.Token);
                            _ = RevokeQueuedAsync(protocol, id, origin, false, verifiedTabOrigin, timestamp, tabId, cancellation.Token);
                            break;
                        }
                        case "executeUnsafe":
                        {
                            string origin = RequiredString(root, "origin");
                            string errorCode = RequiredString(root, "errorCode");
                            long timestamp = root.GetProperty("timestamp").GetInt64();
                            int tabId = root.GetProperty("tabId").GetInt32();
                            if (!IsFresh(timestamp))
                            {
                                Logger.Warn("Execution rejected at request-age check.");
                                await protocol.WriteAsync(new { id, ok = false, error = "The SSL request expired. Open the popup again and retry." }, cancellation.Token);
                                break;
                            }
                            if (tabId < 0)
                            {
                                await protocol.WriteAsync(new { id, ok = false, error = "The active tab could not be verified." }, cancellation.Token);
                                break;
                            }
                            await protocol.WriteAsync(new { id, ok = true, queued = true }, cancellation.Token);
                            Logger.Info("Explicit user request queued; verification will run after popup-close delay.");
                            _ = ExecuteQueuedAsync(protocol, id, origin, errorCode, timestamp, cancellation.Token);
                            break;
                        }
                        default:
                            await protocol.WriteAsync(new { id, ok = false, error = "This helper does not support the requested command." }, cancellation.Token);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Command failed: " + ex.GetType().Name);
                    await protocol.WriteAsync(new { id, ok = false, error = SafeError(ex) }, cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("Native messaging loop failed: " + ex.GetType().Name);
            return 1;
        }
        return 0;
    }

    private static async Task ExecuteQueuedAsync(NativeMessaging protocol, string id, string origin, string errorCode, long timestamp, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(2500), ct);
            if (!IsFresh(timestamp))
            {
                Logger.Warn("Execution rejected at delayed-age check.");
                await protocol.WriteAsync(new { id, ok = false, queued = false, error = "The SSL request expired. Open the popup again and retry." }, ct);
                return;
            }
            SecurityChecks.CheckResult? check = null;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                check = await Checks.VerifyAsync(origin, errorCode, ct);
                if (check.Passed) break;
                if (check.FailedStep is not ("ui-automation" or "interstitial" or "focus") || attempt == 15) break;
                await Task.Delay(750, ct);
            }
            if (check is null || !check.Passed)
            {
                Logger.Warn($"Execution stopped after verification attempt: {check?.FailedStep ?? "unknown"}.");
                await protocol.WriteAsync(new { id, ok = false, queued = false, error = check?.Message ?? "The SSL interstitial could not be verified." }, ct);
                return;
            }
            InputSimulator.InputResult result = InputSimulator.SendUnsafePhrase();
            Logger.Info(result.Accepted ? "SendInput accepted the complete approved key sequence." : "SendInput did not accept the complete key sequence.");
            await protocol.WriteAsync(new { id, ok = result.Accepted, queued = false, inputAccepted = result.Accepted, eventsAccepted = result.EventsAccepted, error = result.Accepted ? (string?)null : result.Message }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("Queued execution failed: " + ex.GetType().Name);
            try { await protocol.WriteAsync(new { id, ok = false, queued = false, error = "The SSL request failed: " + ex.GetType().Name }, ct); } catch { }
        }
    }

    private static async Task RevokeQueuedAsync(NativeMessaging protocol, string id, string origin, bool originRemoved, string verifiedTabOrigin, long timestamp, int tabId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
            SecurityChecks.RevokeResult result = await Checks.RevokeSavedDecisionAsync(origin, verifiedTabOrigin, timestamp, tabId, ct);
            Logger.Info(result.Revoked ? "Chrome saved SSL decision revoked through Page Info." : "Chrome saved SSL decision revocation was not confirmed.");
            await protocol.WriteAsync(new { id, ok = result.Revoked, queued = false, originRemoved, chromeDecisionRevoked = result.Revoked, error = result.Revoked ? (string?)null : result.Message }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("Chrome decision revoke task failed: " + ex.GetType().Name);
            try { await protocol.WriteAsync(new { id, ok = false, queued = false, originRemoved, chromeDecisionRevoked = false, error = "Chrome decision removal failed: " + ex.GetType().Name }, ct); } catch { }
        }
    }

    private static bool IsFresh(long timestamp)
        => IsFresh(timestamp, 90_000);

    private static bool IsFresh(long timestamp, long maxAgeMs)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return timestamp <= now && now - timestamp <= maxAgeMs;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RequiredString(JsonElement root, string name) => ReadString(root, name) is { Length: > 0 } value ? value : throw new ArgumentException($"Required field '{name}' is missing.");
    private static string SafeError(Exception ex) => ex is ArgumentException ? ex.Message : "The helper command failed: " + ex.GetType().Name;
}

internal sealed class AppLogger
{
    private readonly object _gate = new();
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSLExceptionManager", "logs");
    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                string path = Path.Combine(_directory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
