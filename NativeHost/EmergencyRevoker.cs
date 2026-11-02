using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace SSLExceptionManager;

internal static class EmergencyRevoker
{
    private static readonly string AppRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSLExceptionManager");
    private static readonly string StatusPath = Path.Combine(AppRoot, "emergency-revoke-status.json");
    private const string HostName = "com.ssl.exception.manager";
    private const uint WmClose = 0x0010;
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    internal sealed record QueueResult(bool Ok, bool Queued, string? Error, string? ProfileDirectory);

    public static async Task<QueueResult> QueueAsync(string origin, string extensionId, CancellationToken ct)
    {
        string normalized;
        try { normalized = AllowlistStore.NormalizeOrigin(origin); }
        catch { return new(false, false, "The origin is not valid.", null); }
        if (!IsAuthorizedExtension(extensionId)) return new(false, false, "The extension ID does not match the installed helper.", null);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        JsonNode? previous = ReadStatus();
        if (previous?["state"]?.GetValue<string>() == "waiting-for-chrome-close" &&
            previous["at"]?.GetValue<long>() is long previousAt && now - previousAt < 15 * 60_000)
            return new(false, false, "A previous removal is still waiting for Chrome to close.", null);

        string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
        IReadOnlyList<string> profiles = FindProfilesForExtension(userData, extensionId);
        string[] matchingProfiles = profiles.ToArray();
        if (matchingProfiles.Length == 0)
            return new(false, false, "No Chrome profile with this extension was found.", null);
        string? chromeExe = FindChromeExecutable();
        if (chromeExe is null) return new(false, false, "The Chrome executable could not be found.", null);

        string profileDirectory = string.Join(';', matchingProfiles);
        string requestId = Guid.NewGuid().ToString("N");
        await WriteStatusAsync(new { requestId, origin = normalized, state = "closing-chrome", message = "Chrome will close to remove this saved decision.", backup = (string?)null, at = now }, ct);

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Native Host executable path unavailable.");
        ProcessStartInfo start = new(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--emergency-revoke-worker");
        start.ArgumentList.Add(normalized);
        start.ArgumentList.Add(profileDirectory);
        start.ArgumentList.Add(userData);
        start.ArgumentList.Add(chromeExe);
        start.ArgumentList.Add(requestId);
        try
        {
            if (Process.Start(start) is null) throw new InvalidOperationException("Worker did not start.");
        }
        catch (Exception ex)
        {
            await WriteStatusAsync(new { requestId, origin = normalized, state = "failed", message = "The removal worker could not start: " + ex.GetType().Name, backup = (string?)null, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);
            return new(false, false, "The removal worker could not start.", null);
        }
        return new(true, true, null, profileDirectory);
    }

    public static JsonNode? ReadStatus()
    {
        try { return File.Exists(StatusPath) ? JsonNode.Parse(File.ReadAllText(StatusPath)) : null; }
        catch { return null; }
    }

    public static IReadOnlyList<string> GetSavedDecisionOrigins(string extensionId)
    {
        if (!IsAuthorizedExtension(extensionId)) return Array.Empty<string>();
        string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
        IReadOnlyList<string> profiles = FindProfilesForExtension(userData, extensionId);
        return profiles
            .SelectMany(profile => ReadSavedDecisionOrigins(Path.Combine(userData, profile)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadSavedDecisionOrigins(string profilePath)
    {
        try
        {
            string path = Path.Combine(profilePath, "Preferences");
            JsonNode? doc = JsonNode.Parse(File.ReadAllText(path));
            if (doc?["profile"]?["content_settings"]?["exceptions"]?["ssl_cert_decisions"] is not JsonObject entries)
                return Array.Empty<string>();
            List<string> origins = new();
            foreach (KeyValuePair<string, JsonNode?> entry in entries)
            {
                if (!entry.Key.StartsWith("https://", StringComparison.Ordinal) || !entry.Key.EndsWith(",*", StringComparison.Ordinal)) continue;
                if (entry.Value?["setting"]?["cert_exceptions_map"] is not JsonObject certMap) continue;
                bool allowed = certMap.Any(pair => pair.Value is JsonValue value && value.TryGetValue<int>(out int decision) && decision == 1);
                if (!allowed) continue;
                string url = entry.Key[..^2];
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) continue;
                try { origins.Add(AllowlistStore.NormalizeOrigin(uri.GetLeftPart(UriPartial.Authority))); } catch { }
            }
            return origins.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public static bool IsAuthorizedExtension(string extensionId)
    {
        try
        {
            string manifest = Path.Combine(AppContext.BaseDirectory, HostName + ".json");
            if (!File.Exists(manifest)) manifest = Path.Combine(AppRoot, "NativeHost", HostName + ".json");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (JsonElement item in doc.RootElement.GetProperty("allowed_origins").EnumerateArray())
            {
                string? value = item.GetString();
                if (string.Equals(value, $"chrome-extension://{extensionId}/", StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }

    private static IReadOnlyList<string> FindProfilesForExtension(string userData, string extensionId)
    {
        if (!Directory.Exists(userData)) return Array.Empty<string>();
        List<string> found = new();
        foreach (string directory in Directory.EnumerateDirectories(userData))
        {
            string name = Path.GetFileName(directory);
            if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
            string preferences = Path.Combine(directory, "Preferences");
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(preferences));
                if (ContainsExtensionId(doc.RootElement, extensionId)) found.Add(name);
            }
            catch { }
        }
        return found;
    }

    private static string? FindChromeExecutable()
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcessesByName("chrome"))
        {
            try { if (!string.IsNullOrWhiteSpace(process.MainModule?.FileName)) paths.Add(Path.GetFullPath(process.MainModule!.FileName)); }
            catch { }
            finally { process.Dispose(); }
        }
        if (paths.Count == 1) return paths.Single();
        if (paths.Count > 1) return null;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool ContainsExtensionId(JsonElement value, string extensionId)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, extensionId, StringComparison.Ordinal) || ContainsExtensionId(property.Value, extensionId)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) if (ContainsExtensionId(item, extensionId)) return true;
        }
        else if (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), extensionId, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsChromeRunning()
    {
        Process[] processes = Process.GetProcessesByName("chrome");
        try { return processes.Length > 0; }
        finally { foreach (Process process in processes) process.Dispose(); }
    }

    public static async Task<int> RunWorkerAsync(string[] args)
    {
        if (args.Length != 5) return 2;
        string origin = args[0], userData = Path.GetFullPath(args[2]), chromeExe = Path.GetFullPath(args[3]), requestId = args[4];
        string[] profileNames = args[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (profileNames.Length == 0 || profileNames.Any(name => name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal))) return 2;
        string[] profilePaths = profileNames.Select(name => Path.GetFullPath(Path.Combine(userData, name))).ToArray();
        if (profilePaths.Any(path => !path.StartsWith(userData.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return 2;

        await Task.Delay(1200); // let the extension page return its queued response before Chrome exits
        if (!await CloseChromeAsync())
        {
            await WriteStatusAsync(new { requestId, origin, state = "failed", message = "Chrome could not be closed.", backup = (string?)null, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, CancellationToken.None);
            return 3;
        }

        await Task.Delay(1500);
        List<string> backupPaths = new();
        List<string> removedProfiles = new();
        try
        {
            Uri uri = new(origin);
            string normalizedOrigin = AllowlistStore.NormalizeOrigin(origin);
            foreach (string profilePath in profilePaths)
            {
                string preferencePath = Path.Combine(profilePath, "Preferences");
                JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(preferencePath)) ?? throw new InvalidDataException("Preferences JSON was empty.");
                JsonObject? decisions = document["profile"]?["content_settings"]?["exceptions"]?["ssl_cert_decisions"] as JsonObject;
                if (decisions is null) continue;
                List<string> matchingKeys = new();
                foreach (string decisionKey in decisions.Select(pair => pair.Key).ToArray())
                {
                    if (!decisionKey.EndsWith(",*", StringComparison.Ordinal)) continue;
                    string decisionUrl = decisionKey[..^2];
                    if (!Uri.TryCreate(decisionUrl, UriKind.Absolute, out Uri? decisionUri)) continue;
                    string decisionOrigin;
                    try { decisionOrigin = AllowlistStore.NormalizeOrigin(decisionUri.GetLeftPart(UriPartial.Authority)); }
                    catch { continue; }
                    if (string.Equals(decisionOrigin, normalizedOrigin, StringComparison.Ordinal)) matchingKeys.Add(decisionKey);
                }
                if (matchingKeys.Count == 0) continue;
                foreach (string matchingKey in matchingKeys) decisions.Remove(matchingKey);
                string backupPath = preferencePath + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + requestId[..8];
                string tempPath = preferencePath + ".ssl-revoke-" + requestId + ".tmp";
                await File.WriteAllTextAsync(tempPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), new System.Text.UTF8Encoding(false));
                File.Replace(tempPath, preferencePath, backupPath);
                JsonNode verify = JsonNode.Parse(await File.ReadAllTextAsync(preferencePath)) ?? throw new InvalidDataException("Updated Preferences JSON was empty.");
                JsonObject? verifiedDecisions = verify["profile"]?["content_settings"]?["exceptions"]?["ssl_cert_decisions"] as JsonObject;
                if (verifiedDecisions is not null && matchingKeys.Any(verifiedDecisions.ContainsKey))
                    throw new IOException("The Chrome SSL decision still exists after replacement.");
                backupPaths.Add(backupPath);
                removedProfiles.Add(Path.GetFileName(profilePath));
            }

            string launchProfile = FindLastUsedProfile(userData, profileNames);
            ProcessStartInfo start = new(chromeExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(chromeExe)! };
            start.ArgumentList.Add("--user-data-dir=" + userData);
            start.ArgumentList.Add("--profile-directory=" + launchProfile);
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add("chrome://newtab/");
            if (Process.Start(start) is null) throw new InvalidOperationException("Chrome did not start.");
            bool removed = removedProfiles.Count > 0;
            string state = removed ? "completed" : "no-decision-found";
            string message = removed ? "The saved Chrome SSL decision was removed." : "No saved Chrome SSL decision was found.";
            await WriteStatusAsync(new { requestId, origin, state, message, profiles = removedProfiles, backup = string.Join(";", backupPaths), at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, CancellationToken.None);
            return 0;
        }
        catch (Exception ex)
        {
            foreach (string profilePath in profilePaths)
            {
                string temp = Path.Combine(profilePath, "Preferences.ssl-revoke-" + requestId + ".tmp");
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            await WriteStatusAsync(new { requestId, origin, state = "failed", message = "The removal failed: " + ex.GetType().Name, profiles = removedProfiles, backup = string.Join(";", backupPaths), at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, CancellationToken.None);
            return 1;
        }
    }

    private static string FindLastUsedProfile(string userData, IReadOnlyCollection<string> candidates)
    {
        try
        {
            using JsonDocument localState = JsonDocument.Parse(File.ReadAllText(Path.Combine(userData, "Local State")));
            string? lastUsed = localState.RootElement.GetProperty("profile").GetProperty("last_used").GetString();
            if (lastUsed is not null && candidates.Contains(lastUsed, StringComparer.Ordinal)) return lastUsed;
        }
        catch { }
        return candidates.First();
    }

    private static async Task<bool> CloseChromeAsync()
    {
        Process[] initial = Process.GetProcessesByName("chrome");
        if (initial.Length == 0) return true;
        HashSet<int> processIds = initial.Select(process => process.Id).ToHashSet();
        try
        {
            EnumWindows((window, state) =>
            {
                if (!IsWindowVisible(window)) return true;
                _ = GetWindowThreadProcessId(window, out uint processId);
                if (processIds.Contains((int)processId)) PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
        finally { foreach (Process process in initial) process.Dispose(); }

        long gracefulDeadline = Environment.TickCount64 + 3_000;
        while (Environment.TickCount64 < gracefulDeadline && IsChromeRunning()) await Task.Delay(200);
        if (!IsChromeRunning()) return true;

        long forceDeadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < forceDeadline)
        {
            Process[] remaining = Process.GetProcessesByName("chrome");
            if (remaining.Length == 0) return true;
            try
            {
                foreach (Process process in remaining)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { }
                }
            }
            finally { foreach (Process process in remaining) process.Dispose(); }
            await Task.Delay(250);
        }
        return !IsChromeRunning();
    }

    private static async Task WriteStatusAsync(object value, CancellationToken ct)
    {
        Directory.CreateDirectory(AppRoot);
        string temp = StatusPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value), new System.Text.UTF8Encoding(false), ct);
        File.Move(temp, StatusPath, true);
    }
}
