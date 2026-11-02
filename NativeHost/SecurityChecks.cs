using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Rect = System.Windows.Rect;

namespace SSLExceptionManager;

internal sealed class SecurityChecks(AllowlistStore allowlist, AppLogger logger)
{
    private static readonly Regex CertError = new(@"NET::ERR_CERT_[A-Z0-9_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> BrowserNames = new(StringComparer.OrdinalIgnoreCase) { "chrome", "chromium", "msedge" };
    private static readonly HashSet<string> BrowserClasses = new(StringComparer.Ordinal) { "Chrome_WidgetWin_1", "Chrome_WidgetWin_0" };

    public async Task<CheckResult> VerifyAsync(string origin, string errorCode, CancellationToken ct)
    {
        string normalized;
        try { normalized = AllowlistStore.NormalizeOrigin(origin); }
        catch { return Fail("origin", "Origin is invalid."); }
        if (!string.Equals(normalized, origin, StringComparison.Ordinal)) return Fail("origin", "Origin is not normalized.");
        if (!errorCode.StartsWith("net::ERR_CERT_", StringComparison.Ordinal))
            return Fail("error-code", "Certificate error is not eligible.");
        if (!await allowlist.ContainsAsync(normalized, ct)) return Fail("allowlist", "Origin is no longer approved.");

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return Fail("foreground-window", "No foreground window.");
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        Process process;
        try { process = Process.GetProcessById((int)pid); }
        catch { return Fail("foreground-process", "Foreground process could not be identified."); }
        using (process)
        {
            if (!BrowserNames.Contains(Path.GetFileNameWithoutExtension(process.ProcessName)))
                return Fail("foreground-process", "Foreground application is not an approved Chromium browser.");
            if (!BrowserClasses.Contains(GetClassName(hwnd)))
                return Fail("window-class", "Foreground window class is not a browser window.");
            if (!IntegrityIsSafe(process.Id)) return Fail("integrity", "Native Host integrity is higher than the browser or could not be verified.");
        }

        UiEvidence evidence;
        try { evidence = ReadInterstitial(hwnd); }
        catch (Exception ex) { return Fail("ui-automation", "UI Automation could not verify the interstitial: " + ex.GetType().Name); }
        if (!evidence.IsInterstitial) return Fail("interstitial", "Chrome SSL interstitial was not positively identified.");
        if (!evidence.ContentHasFocus) return Fail("focus", "Keyboard focus is not verified inside the interstitial document; no keys were sent.");
        if (!string.Equals(evidence.ErrorCode, errorCode, StringComparison.OrdinalIgnoreCase))
            return Fail("error-code-match", "Visible certificate error does not match the recorded error.");
        if (!string.Equals(evidence.Origin, normalized, StringComparison.Ordinal))
            return Fail("address-bar", "Visible address bar origin does not match the approved origin.");
        return new(true, "Verified foreground SSL interstitial.", null);
    }

    public async Task<RevokeResult> RevokeSavedDecisionAsync(string origin, string verifiedTabOrigin, long timestamp, int tabId, CancellationToken ct)
    {
        string normalized;
        try { normalized = AllowlistStore.NormalizeOrigin(origin); }
        catch { return new(false, "The origin is not valid."); }
        if (!string.Equals(normalized, origin, StringComparison.Ordinal)) return new(false, "The origin is not normalized.");
        try
        {
            if (AllowlistStore.NormalizeOrigin(verifiedTabOrigin) != normalized || tabId < 0 ||
                timestamp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp > 5_000)
                return new(false, "The active tab confirmation is expired or does not match the requested origin.");
        }
        catch { return new(false, "The extension did not confirm the active tab origin."); }

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new(false, "No browser window is in the foreground.");
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            if (!BrowserNames.Contains(Path.GetFileNameWithoutExtension(process.ProcessName)) || !BrowserClasses.Contains(GetClassName(hwnd)))
                return new(false, "The foreground window is not an approved Chromium browser.");
            if (!IntegrityIsSafe(process.Id)) return new(false, "The helper could not verify that its integrity level is safe.");
        }
        catch { return new(false, "The browser process could not be verified."); }

        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            string? observedOrigin = ReadAddressOrigin(root, normalized);
            if (observedOrigin is not null && !string.Equals(observedOrigin, normalized, StringComparison.Ordinal))
                return new(false, $"The active browser origin is {observedOrigin}, but removal was requested for {normalized}. Redirects can change the active origin.");
            if (observedOrigin is null) logger.Info("Chrome address bar was not exposed to UI Automation; using the extension's fresh active-tab origin confirmation.");

            AutomationElement? siteInfo = FindUniqueButton(root, IsSiteInformationButton);
            if (siteInfo is null) return new(false, "Chrome's site information button was not found.");
            if (siteInfo.GetCurrentPattern(InvokePattern.Pattern) is not InvokePattern openPattern)
                throw new InvalidOperationException("Site information button has no invoke pattern.");
            openPattern.Invoke();

            await Task.Delay(400, ct);
            root = AutomationElement.FromHandle(hwnd);
            string? afterOpenOrigin = ReadAddressOrigin(root, normalized);
            if (afterOpenOrigin is not null && !string.Equals(afterOpenOrigin, normalized, StringComparison.Ordinal))
                return new(false, "The browser origin changed after opening site information.");
            AutomationElement? turnOnWarnings = FindUniqueButton(root, IsTurnOnWarningsButton);
            if (turnOnWarnings is null)
                return new(false, "Chrome did not show the Turn on warnings option; the saved decision was not removed.");
            if (!turnOnWarnings.Current.IsEnabled) return new(false, "Chrome's revoke option is disabled.");
            ((InvokePattern)turnOnWarnings.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

            await Task.Delay(500, ct);
            root = AutomationElement.FromHandle(hwnd);
            AutomationElement? reopenedSiteInfo = FindUniqueButton(root, IsSiteInformationButton);
            if (reopenedSiteInfo is null) return new(false, "Chrome did not reopen site information to verify removal.");
            ((InvokePattern)reopenedSiteInfo.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            await Task.Delay(350, ct);
            root = AutomationElement.FromHandle(hwnd);
            string? afterReopenOrigin = ReadAddressOrigin(root, normalized);
            if (afterReopenOrigin is not null && !string.Equals(afterReopenOrigin, normalized, StringComparison.Ordinal))
                return new(false, "The browser origin changed while verifying removal.");
            if (FindUniqueButton(root, IsTurnOnWarningsButton) is not null)
                return new(false, "Chrome still shows Turn on warnings; removal was not confirmed.");
            return new(true, "Chrome no longer shows the revoke option.");
        }
        catch (Exception ex)
        {
            logger.Warn("Chrome SSL decision revocation could not be verified: " + ex.GetType().Name);
            return new(false, "Chrome could not confirm removal through site information. If needed, select Turn on warnings manually.");
        }
    }

    private UiEvidence ReadInterstitial(IntPtr hwnd)
    {
        AutomationElement root = AutomationElement.FromHandle(hwnd);
        var names = new List<string>(128);
        string? address = null;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;
        while (queue.Count > 0 && visited++ < 800)
        {
            var (element, depth) = queue.Dequeue();
            string name = Safe(() => element.Current.Name) ?? string.Empty;
            if (name.Length > 0) names.Add(name);
            if (name.Contains("address and search", StringComparison.OrdinalIgnoreCase) || name.Contains("address bar", StringComparison.OrdinalIgnoreCase))
            {
                string? value = null;
                try { value = element.GetCurrentPropertyValue(ValuePattern.ValueProperty) as string; } catch { }
                if (string.IsNullOrWhiteSpace(value)) value = name;
                if (value?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true) address = value;
            }
            if (depth >= 9) continue;
            AutomationElementCollection children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement child in children) queue.Enqueue((child, depth + 1));
        }

        string all = string.Join("\n", names);
        Match match = CertError.Match(all);
        bool marker = all.Contains("Your connection is not private", StringComparison.OrdinalIgnoreCase) ||
                      all.Contains("Privacy error", StringComparison.OrdinalIgnoreCase);
        string? visibleOrigin = null;
        if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https")
        {
            try { visibleOrigin = AllowlistStore.NormalizeOrigin(uri.GetLeftPart(UriPartial.Authority)); } catch { }
        }
        bool contentHasFocus = false;
        try
        {
            AutomationElement focused = AutomationElement.FocusedElement;
            if (focused.Current.ProcessId == (int)GetProcessId(hwnd))
            {
                string focusedName = focused.Current.Name ?? string.Empty;
                bool addressFocused = focusedName.Contains("address and search", StringComparison.OrdinalIgnoreCase) || focusedName.Contains("address bar", StringComparison.OrdinalIgnoreCase);
                if (!addressFocused)
                {
                    AutomationElement? current = focused;
                    for (int depth = 0; current is not null && depth < 12; depth++, current = TreeWalker.ControlViewWalker.GetParent(current))
                    {
                        if (current.Current.ControlType == ControlType.Document) { contentHasFocus = true; break; }
                    }
                }
            }
        }
        catch { contentHasFocus = false; }
        return new(marker && match.Success, match.Success ? match.Value : null, visibleOrigin, contentHasFocus);
    }

    private static AutomationElement? FindUniqueButton(AutomationElement root, Func<string, bool> predicate)
    {
        AutomationElementCollection all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        AutomationElement? found = null;
        int matches = 0;
        int limit = Math.Min(all.Count, 1600);
        for (int i = 0; i < limit; i++)
        {
            AutomationElement element = all[i];
            if (element.Current.ControlType != ControlType.Button || !predicate(element.Current.Name ?? string.Empty)) continue;
            found = element;
            matches++;
            if (matches > 1) return null;
        }
        return matches == 1 ? found : null;
    }

    private static bool IsSiteInformationButton(string name) =>
        name.Equals("View site information", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("View site information for this site", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Site information", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Site settings and permissions", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("View connection details", StringComparison.OrdinalIgnoreCase);

    private static bool IsTurnOnWarningsButton(string name) =>
        name.Equals("Turn on warnings", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Turn on warning", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Turn on warnings for this site", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Enable warnings", StringComparison.OrdinalIgnoreCase);

    private static string? ReadAddressOrigin(AutomationElement root, string? expectedOrigin = null)
    {
        AutomationElementCollection all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        int limit = Math.Min(all.Count, 1600);
        var labeledCandidates = new HashSet<string>(StringComparer.Ordinal);
        var fallbackCandidates = new HashSet<string>(StringComparer.Ordinal);
        Rect rootRect = root.Current.BoundingRectangle;
        for (int i = 0; i < limit; i++)
        {
            AutomationElement element = all[i];
            string name = element.Current.Name ?? string.Empty;
            bool labeledAddress = name.Contains("address and search", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("address bar", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("search Google or enter address", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("address and search", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("address bar", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("search Google or enter address", StringComparison.OrdinalIgnoreCase);
            bool topEdit = element.Current.ControlType == ControlType.Edit &&
                           element.Current.BoundingRectangle.Top >= rootRect.Top &&
                           element.Current.BoundingRectangle.Top <= rootRect.Top + 180;
            if (!labeledAddress && !topEdit) continue;
            string? value = null;
            try { value = element.GetCurrentPropertyValue(ValuePattern.ValueProperty) as string; } catch { }
            if (string.IsNullOrWhiteSpace(value)) value = name;
            if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https") continue;
            try
            {
                string candidate = AllowlistStore.NormalizeOrigin(uri.GetLeftPart(UriPartial.Authority));
                (labeledAddress ? labeledCandidates : fallbackCandidates).Add(candidate);
            }
            catch { }
        }
        HashSet<string> candidates = labeledCandidates.Count > 0 ? labeledCandidates : fallbackCandidates;
        if (expectedOrigin is not null && candidates.Contains(expectedOrigin)) return expectedOrigin;
        return candidates.Count == 1 ? candidates.Single() : null;
    }

    private static uint GetProcessId(IntPtr hwnd) { _ = GetWindowThreadProcessId(hwnd, out uint pid); return pid; }

    private bool IntegrityIsSafe(int browserPid)
    {
        try
        {
            using WindowsIdentity host = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            using Process browser = Process.GetProcessById(browserPid);
            using SafeTokenHandle token = OpenProcessToken(browser.Handle, 0x0008, out IntPtr handle)
                ? new SafeTokenHandle(handle) : throw new InvalidOperationException("OpenProcessToken failed.");
            int hostRid = GetIntegrityRid(host.AccessToken.DangerousGetHandle());
            int browserRid = GetIntegrityRid(token.DangerousGetHandle());
            return hostRid <= browserRid;
        }
        catch { return false; }
    }

    private static int GetIntegrityRid(IntPtr token)
    {
        GetTokenInformation(token, 25, IntPtr.Zero, 0, out int size);
        if (size <= 0 || size > 4096) throw new InvalidOperationException("Token integrity query failed.");
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 25, buffer, size, out _)) throw new InvalidOperationException("Token integrity read failed.");
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) throw new InvalidOperationException("Invalid integrity SID.");
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private string GetClassName(IntPtr hwnd)
    {
        char[] buffer = new char[256];
        int length = GetClassNameNative(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private CheckResult Fail(string step, string message) { logger.Warn($"Verification failed at {step}."); return new(false, message, step); }
    private static string? Safe(Func<string> read) { try { return read(); } catch { return null; } }

    private sealed record UiEvidence(bool IsInterstitial, string? ErrorCode, string? Origin, bool ContentHasFocus);
    internal sealed record CheckResult(bool Passed, string Message, string? FailedStep);
    internal sealed record RevokeResult(bool Revoked, string Message);
    private sealed class SafeTokenHandle(IntPtr handle) : SafeHandle(handle, true)
    {
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassNameNative(IntPtr hWnd, [Out] char[] className, int maxCount);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
