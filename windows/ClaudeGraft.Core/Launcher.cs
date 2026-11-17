using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGraft.Core;

/// <summary>
/// Opens a profile's Claude Desktop. A minimal stand-in for the Mac build's full
/// launch layer: bring the profile's storage in line with its configuration,
/// then either show the Claude already on it or start a new one. Records are not
/// yet swept here — that wiring comes with the launcher proper.
/// </summary>
public static class Launcher
{
    /// The Claude Desktop binary, newest installed version. Squirrel keeps each
    /// version in its own app-<version> folder under LocalAppData.
    ///
    /// Ordered by parsed version, never by the folder name as a string: an
    /// ordinal sort puts app-2.2553.1 above app-2.2553.13 — the path separator
    /// after the "1" outranks the "3" — so Graft launched a stale build and left
    /// Claude asking for an update its own updater had already installed.
    public static string? ClaudeExe() => ClaudeInstallation.Current?.Exe;

    internal static string? SquirrelExe()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");
        if (!Directory.Exists(root)) return null;
        var newest = NewestApp(Directory.EnumerateDirectories(root, "app-*")
            .Where(d => File.Exists(Path.Combine(d, "claude.exe"))));
        return newest is null ? null : Path.Combine(newest, "claude.exe");
    }

    /// The app-<version> directory with the highest parsed version. Anything not
    /// spelled app-<version> is ignored rather than guessed at.
    public static string? NewestApp(IEnumerable<string> appDirs) =>
        appDirs
            .Select(d => (dir: d, version: Version.TryParse(Path.GetFileName(d)["app-".Length..], out var v) ? v : null))
            .Where(x => x.version is not null)
            .OrderByDescending(x => x.version)
            .Select(x => x.dir)
            .FirstOrDefault();

    /// Open the profile a shortcut folder names, resolving its source from the
    /// shared list — which is what a desktop shortcut hands the launcher stub.
    /// An unknown folder still opens, but exactly as it stands: applying "no
    /// source" would ungraft it, and an unreadable list makes every folder unknown.
    public static void OpenByFolder(string folder)
    {
        if (Graft.ValidateFolder(folder) is not null) return;
        var store = new ShortcutStore();
        var shortcut = store.Shortcuts.FirstOrDefault(s => string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (shortcut is not null) { Open(store.ConfigFor(shortcut)); return; }
        Diagnostics.Note("launch.unknown-shortcut", new Dictionary<string, object?>
        {
            ["folder"] = folder, ["listUnreadable"] = store.LoadError is not null,
        });
        var profile = GraftPaths.Profile(folder);
        if (!Directory.Exists(profile)) return;
        Open(new GraftConfig { ProfileDir = profile, SourceDir = null }, applyGraft: false);
    }

    /// Bring the storage in line and open Claude. If one is already on the
    /// profile, show it rather than starting a second — two Claudes on one chat
    /// store both write it, which is the one loss this app exists to avoid.
    /// A <paramref name="link"/> goes to the Claude this starts; one already open is handed it by <see cref="ClaudeLinks"/> instead.
    public static void Open(GraftConfig config, bool applyGraft = true, string? link = null)
    {
        var filing = FilingProfiles(config);
        if (ClaudeProcesses.IsRunning(config.ProfileDir))
        {
            if (ClaudeProcesses.ProcessIdentifier(config.ProfileDir) is int pid) Reveal(pid);
            // A profile already open is the one case nothing else covers: no
            // launch to file records on the way to, and no timer behind it. So
            // the sweep runs on the way past instead, after the window is up.
            try { SquareUp(filing); }
            catch (IOException e) when (Skipped(e, "launch")) { }
            return;
        }
        // Apply first, so a record filed through a graft lands where the link now
        // points; sweep before launch, so the records are on disk before Claude
        // reads its sidebar as it comes up.
        try
        {
            // One pass, let go before the launch lock below is taken.
            StateLock.Run(() =>
            {
                if (applyGraft) Graft.Apply(config);
                SquareUp(filing);
            });
        }
        // Nothing in this pass can be trusted to tell new from deleted, so none
        // of it runs; the window still opens with whatever is already linked.
        catch (IOException e) when (Skipped(e, "launch")) { }
        // The sidebar sync and the launch are one critical section: releasing the
        // lock between them would let another launcher start syncing while this
        // one opens Claude into the very databases it is reconciling. The
        // running-check is rechecked here too, since a Claude may have started
        // during Apply/SquareUp.
        SidebarSync.WithLaunchLock(locked =>
        {
            // Never open into a synchronization we could not serialize against.
            // Acquisition only fails here on a broken lock, not on contention
            // (which blocks), so declining costs nothing in normal operation.
            if (!locked)
            {
                Diagnostics.Note("sidebar.launch-lock-unavailable",
                    new Dictionary<string, object?> { ["profile"] = Path.GetFileName(config.ProfileDir) });
                return;
            }
            if (ClaudeProcesses.IsRunning(config.ProfileDir))
            {
                if (ClaudeProcesses.ProcessIdentifier(config.ProfileDir) is int pid) Reveal(pid);
                return;
            }
            SidebarSync.Synchronize(config.ProfileDir);
            SidebarSync.SynchronizeProjectOrder(config.ProfileDir);
            PoolEnvironment.SettleBeforeOpening(config.ProfileDir,
                () => PoolEnvironment.EnsureRelay(PoolEnvironment.ReadJournal().Relay));
            Starter(config.ProfileDir, link);
        });
    }

    /// Mirror every known pair back into line, then file the records for any
    /// session whose transcript survived without one. The state report the Mac
    /// writes here is not ported yet; the mirror and the record sweep are what a
    /// launch has to do before a sidebar is built.
    private static void SquareUp(IReadOnlyList<string> filingInto)
    {
        Graft.MirrorKnownPairs();
        Graft.FileMissingSessionRecords(filingInto, ClaudeProcesses.AnyDesktopRunning);
    }

    /// The pass a Claude quitting calls for: whatever the mirror could not land
    /// while it ran lands now, before anything opens it again.
    public static void Sweep(IReadOnlyList<string> filingInto)
    {
        try { StateLock.Run(() => SquareUp(filingInto)); }
        catch (IOException e) when (Skipped(e, "sweep")) { }
    }

    /// Notes a pass that did not run for its state, and leaves every other failure to throw.
    private static bool Skipped(IOException e, string pass)
    {
        switch (e)
        {
            case UnreadableStateException unreadable:
                Diagnostics.Note(pass + ".state-unreadable", new Dictionary<string, object?> { ["file"] = Path.GetFileName(unreadable.Path) });
                return true;
            case StateBusyException:
                Diagnostics.Note(pass + ".state-busy", null);
                return true;
            default:
                return false;
        }
    }

    /// The profiles a sweep run from this launch may file into: the profile being
    /// opened, Claude's own, and the source it borrows from — the account that
    /// owns a recovered session is likely one of these.
    private static List<string> FilingProfiles(GraftConfig config)
    {
        var list = new List<string> { config.ProfileDir, GraftPaths.DefaultProfile };
        if (!string.IsNullOrEmpty(config.SourceDir)) list.Add(config.SourceDir!);
        return list;
    }

    /// Replaced by the suite, which may never start a Claude.
    public static Action<string, string?> Starter { get; set; } = (profile, link) => Start(profile, link);

    /// Starts Claude on a profile, or hands a link to the Claude already on it:
    /// Electron keys its single-instance lock by data directory, so a second
    /// start on a running profile passes its arguments over and exits.
    public static bool Start(string profile, string? link = null)
    {
        var claude = ClaudeInstallation.Current;
        if (claude is { Package: null, Exe: var exe })
        {
            try { using var process = Process.Start(StartInfo(exe, profile, link)); return true; }
            catch { return false; }
        }
        if (claude?.Package is not { } package) return false;
        // No link reaches here with a quote or a space in it (ClaudeLinks.Classify), so this string re-parses as it was built.
        var args = string.Join(' ', Arguments(profile, link).Select(a => a.StartsWith("--user-data-dir=")
            ? "--user-data-dir=\"" + a["--user-data-dir=".Length..] + "\"" : a));
        if (PackagedClaude.Activate(package, args)) return true;
        Diagnostics.Note("launch.activationFailed", new Dictionary<string, object?>
        {
            ["profile"] = Path.GetFileName(profile),
            ["app"] = package.AppUserModelId,
        });
        return false;
    }

    /// The main profile is the one started with no --user-data-dir: an explicit
    /// path spelled even slightly differently keys another single-instance lock.
    public static ProcessStartInfo StartInfo(string exe, string profile, string? link = null)
    {
        // Started anywhere else, Claude holds Graft's install folder as its own and an update cannot replace it.
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.Environment.Remove("CLAUDE_USER_DATA_DIR");
        if (!Fs.SamePath(profile, GraftPaths.DefaultProfile)) start.Environment["CLAUDE_USER_DATA_DIR"] = profile;
        foreach (var argument in Arguments(profile, link)) start.ArgumentList.Add(argument);
        return start;
    }

    private static IEnumerable<string> Arguments(string profile, string? link)
    {
        if (!Fs.SamePath(profile, GraftPaths.DefaultProfile)) yield return "--user-data-dir=" + profile;
        if (link is not null) yield return link;
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
