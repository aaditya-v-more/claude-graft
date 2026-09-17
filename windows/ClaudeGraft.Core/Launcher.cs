using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGraft.Core;

public static class Launcher
{
    public static string? ClaudeExe() => ClaudeInstallation.Discover()?.Executable;

    public static void OpenByFolder(string folder, Func<IReadOnlyList<string>, bool>? confirmSharing = null)
    {
        if (Graft.ValidateFolder(folder) is string problem) throw new IOException(problem);
        var store = new ShortcutStore();
        if (store.LoadError is not null) throw new IOException(store.LoadError);
        var shortcut = store.Shortcuts.FirstOrDefault(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("This shortcut is no longer registered. Open Graft to recreate it.");
        Open(store.ConfigFor(shortcut), confirmSharing);
    }

    public static void Open(GraftConfig config, Func<IReadOnlyList<string>, bool>? confirmSharing = null)
    {
        using var stateLock = StateLock.Acquire();
        Graft.ValidateProfilePath(config.ProfileDir, allowDefault: true);
        if (config.SourceDir is not null) Graft.ValidateProfilePath(config.SourceDir, allowDefault: true);
        if (ClaudeExe() is null) throw new IOException("Install Claude Desktop for Windows before opening a profile.");
        if (ClaudeProcesses.IsRunning(config.ProfileDir))
        {
            if (ClaudeProcesses.ProcessIdentifier(config.ProfileDir) is int pid) Reveal(pid);
            return;
        }
        var conflicts = SharingConflicts(config);
        if (conflicts.Count > 0 && !(confirmSharing?.Invoke(conflicts) ?? false)) return;
        Graft.Apply(config);
        Graft.MirrorKnownPairs();
        var filing = new List<string> { config.ProfileDir, GraftPaths.DefaultProfile };
        if (config.SourceDir is not null) filing.Add(config.SourceDir);
        Graft.FileMissingSessionRecords(filing, ClaudeProcesses.IsRunning);
        Process.Start(StartInfo(ClaudeExe()!, config.ProfileDir));
        // Hold the interprocess lock until Windows sees the new process; a
        // second shortcut press otherwise races the first launch.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (ClaudeProcesses.IsRunning(config.ProfileDir)) return;
            Thread.Sleep(100);
        }
        throw new IOException("Claude started but its profile could not be verified. Check its window before trying again.");
    }

    public static List<string> SharingConflicts(GraftConfig config)
    {
        var store = new ShortcutStore();
        if (store.LoadError is not null) throw new IOException(store.LoadError);
        string Root(string profile)
        {
            var shortcut = store.Shortcuts.FirstOrDefault(s => Fs.SamePath(s.ProfileDir, profile));
            return shortcut is null ? profile : store.ChatRoot(shortcut);
        }
        var root = Root(config.SourceDir ?? config.ProfileDir);
        IEnumerable<string> profiles = store.Shortcuts.Select(s => s.ProfileDir).Append(GraftPaths.DefaultProfile);
        if (config.SourceDir is not null) profiles = profiles.Append(config.SourceDir);
        var processes = ClaudeProcesses.Enumerate();
        return profiles.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !Fs.SamePath(p, config.ProfileDir) && Fs.SamePath(Root(p), root) && ClaudeProcesses.IsRunning(p, processes))
            .Select(p => Path.GetFileName(p)).ToList();
    }

    public static ProcessStartInfo StartInfo(string exe, string profile, string? callback = null)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment.Remove("CLAUDE_USER_DATA_DIR");
        if (!Fs.SamePath(profile, GraftPaths.DefaultProfile))
        {
            start.ArgumentList.Add("--user-data-dir=" + profile);
            start.Environment["CLAUDE_USER_DATA_DIR"] = profile;
        }
        if (callback is not null)
        {
            if (!ValidCallback(callback)) throw new IOException("Paste the claude:// sign-in link from the browser.");
            start.ArgumentList.Add(callback);
        }
        return start;
    }

    public static bool ValidCallback(string value) => value.Length < 16384 && !value.Any(char.IsControl)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "claude"
        && (uri.Host == "claude.ai" || uri.Host == "claude.com") && string.IsNullOrEmpty(uri.UserInfo);

    public static void CompleteSignIn(GraftConfig config, string callback)
    {
        using var stateLock = StateLock.Acquire();
        Graft.ValidateProfilePath(config.ProfileDir, allowDefault: true);
        if (!ClaudeProcesses.IsRunning(config.ProfileDir))
            throw new IOException("Open this profile and start its browser sign-in first.");
        var exe = ClaudeExe() ?? throw new IOException("Claude Desktop could not be found.");
        // Electron forwards the link to this profile's existing process.
        // Callback contents never enter Graft's state or diagnostics.
        Process.Start(StartInfo(exe, config.ProfileDir, callback));
    }

    // MARK: - Reveal

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    /// Bring the Claude holding a profile back to the front. Its window is found
    /// by walking the process's own top-level windows rather than through
    /// <c>MainWindowHandle</c>: a Claude set to keep running in the tray answers a
    /// closed window by hiding it, not ending it, and a hidden window is no
    /// window at all as far as <c>MainWindowHandle</c> is concerned — which is
    /// how reopening a tray-resident profile from the menu did nothing. The HWND
    /// is still there, only hidden, so the walk still finds it and shows it.
    private static void Reveal(int pid)
    {
        try
        {
            var window = FindAppWindow((uint)pid);
            if (window == IntPtr.Zero) return;
            ShowWindow(window, SW_RESTORE);
            SetForegroundWindow(window);
        }
        catch { }
    }

    /// Electron's own top-level window is a Chromium widget host; every one of
    /// them, main window and popups alike, carries this class.
    private const string ElectronWindowClass = "Chrome_WidgetWin_1";

    /// The process's real window, hidden or shown. A title is not enough to pick
    /// it out: Chromium also owns an OleDdeWndClass window titled "DDE Server
    /// Window" for shell activation, and it can sit ahead of the real one in the
    /// enumeration — so matching the first titled window showed that instead, and
    /// SW_RESTORE dragged the hidden DDE window into the switcher beside Claude.
    /// The app window is the Chromium widget host, and its class is what says so;
    /// a title on top of that rules out the process's untitled background widgets.
    private static IntPtr FindAppWindow(uint pid)
    {
        var found = IntPtr.Zero;
        var className = new StringBuilder(64);
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner != pid || GetWindowTextLength(hWnd) == 0) return true;
            GetClassName(hWnd, className, className.Capacity);
            if (className.ToString() != ElectronWindowClass) return true;
            found = hWnd;
            return false;   // stop at the first real app window this process owns
        }, IntPtr.Zero);
        return found;
    }
}
