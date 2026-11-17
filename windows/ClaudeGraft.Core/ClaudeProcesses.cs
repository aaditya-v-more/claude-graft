namespace ClaudeGraft.Core;

/// <summary>
/// Which Claude is running, and where. The Mac build reads <c>ps</c> and matches
/// on the app binary path; here the process is <c>claude.exe</c> and its command
/// line is read from WMI. The predicates that pick a profile out of a command
/// line are pure and tested; the process enumeration behind them is a hook, so
/// a test drives the predicates without any live processes.
/// </summary>
public static class ClaudeProcesses
{
    /// Returns every running <c>claude.exe</c> paired with its command line.
    /// Replaced in tests; defaults to the WMI query on Windows and to nothing
    /// anywhere else, so the cross-platform Core still compiles and a run off
    /// Windows reports no Claude rather than throwing.
    public static Func<IReadOnlyList<(int pid, string command)>> Enumerate = () =>
        OperatingSystem.IsWindows()
            ? WindowsProcessQuery.ClaudeProcesses()
            : Array.Empty<(int pid, string command)>();

    /// Whether this is the Electron desktop app rather than the Claude Code CLI.
    ///
    /// Claude Desktop keeps a copy of Claude Code inside every profile and spawns
    /// it for a bridged session — <c>…\<profile>\claude-code\<version>\claude.exe</c>,
    /// named claude.exe like the app, and carrying neither <c>--type=</c> nor
    /// <c>--user-data-dir</c>. By flags alone it is indistinguishable from the
    /// main instance, so a bridged session running with the main window closed
    /// made the profile look open, and reopening it revealed that windowless CLI
    /// process and did nothing — inconsistent, since it turned on whether a
    /// session happened to be bridging. Identity comes from the binary, the way
    /// the Mac build's isClaudeProcess reads it: the desktop app lives under
    /// <c>AnthropicClaude\app-<version>\</c>, where only the Electron process and
    /// its helpers run, and every one of them owns a window worth showing. The
    /// packaged install's WindowsApps folder holds the same.
    public static bool IsClaudeDesktop(string command) =>
        command.Contains(@"AnthropicClaude\app-", StringComparison.OrdinalIgnoreCase)
        || PackagedClaude.IsPackagePath(command);

    /// A helper process — a renderer, a GPU or utility process — rather than the
    /// browser process that owns the window. Electron marks these with --type;
    /// only the one without it answers to being shown.
    public static bool IsHelper(string command) =>
        WindowsCommandLine.Option(WindowsCommandLine.Parse(command), "--type") is not null;

    public static bool HasUserDataDir(string command) =>
        WindowsCommandLine.Option(WindowsCommandLine.Parse(command), "--user-data-dir") is not null;

    /// The main binary of a Claude on no profile in particular — no helper flag,
    /// and no --user-data-dir, which is the only mark the default profile has.
    public static bool IsDefaultInstance(string command) =>
        IsClaudeDesktop(command) && !IsHelper(command) && !HasUserDataDir(command);

    /// Whether a command line carries this exact profile's --user-data-dir.
    /// Compared as whole paths, never as text: <c>…\Claude</c> is a prefix of
    /// <c>…\Claude-2</c>, and every shorter-named profile would look open.
    public static bool CarriesDataDir(string command, string profilePath)
    {
        var value = WindowsCommandLine.Option(WindowsCommandLine.Parse(command), "--user-data-dir");
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try { return Fs.SamePath(value, profilePath); }
        catch (ArgumentException) { return false; }
    }

    public static bool IsRunning(string profile) => IsRunning(profile, Enumerate());

    /// The pure form the tests drive: a yes or no against a list of command
    /// lines already in hand.
    public static bool IsRunning(string profile, IReadOnlyList<(int pid, string command)> processes)
    {
        // A yes-or-no counts any process, a helper included, the way pgrep -f
        // does on the Mac.
        if (Fs.SamePath(profile, GraftPaths.DefaultProfile))
            return processes.Any(p => IsDefaultInstance(p.command));
        return processes.Any(p => IsClaudeDesktop(p.command) && CarriesDataDir(p.command, profile));
    }


    /// Whether any Claude Desktop window is up, on whatever profile. The record
    /// sweep's quiet window waits on this rather than on a Claude signed into the
    /// session's owner account, because on Windows the two are not the same
    /// question: a bridged session's owner is stamped from the machine-wide
    /// command line login, while the window that files its record — and the
    /// account folder it files under — is the one it was typed into. A session
    /// started in a window on one account while the command line was logged into
    /// another is owned by the second and recorded by the first, so asking only
    /// after the owner's account missed the window that was about to record it and
    /// filed a duplicate into a second profile. Helpers are left out; only a
    /// window-owning process records a session.
    public static bool AnyDesktopRunning()
    {
        // Not knowing is treated as yes: the sweep then waits rather than filing
        // a record a window is about to write.
        try { return AnyDesktopRunning(Enumerate()); }
        catch (IOException) { return true; }
    }

    public static bool AnyDesktopRunning(IReadOnlyList<(int pid, string command)> processes) =>
        processes.Any(p => IsClaudeDesktop(p.command) && !IsHelper(p.command));

    /// The shortcut whose Claude a process is: the window-owning process alone,
    /// since only it has a taskbar button. Null for the main Claude and anything
    /// else.
    public static Shortcut? ShortcutOf(string command, IEnumerable<Shortcut> shortcuts) =>
        IsClaudeDesktop(command) && !IsHelper(command)
            ? shortcuts.FirstOrDefault(s => CarriesDataDir(command, s.ProfileDir))
            : null;

    /// The profile a Claude's browser process runs on: the main one, a
    /// shortcut's, or null for a helper or a Claude on a profile no shortcut has.
    public static string? ProfileOf(string command, IEnumerable<Shortcut> shortcuts) =>
        IsDefaultInstance(command) ? GraftPaths.DefaultProfile : ShortcutOf(command, shortcuts)?.ProfileDir;

    public static int? ProcessIdentifier(string profile) => ProcessIdentifier(profile, Enumerate());

    /// The pid of the Claude holding this profile, if one holds it — the browser
    /// process alone, since every helper carries the same --user-data-dir and
    /// only the browser answers to being brought forward.
    public static int? ProcessIdentifier(string profile, IReadOnlyList<(int pid, string command)> processes)
    {
        if (Fs.SamePath(profile, GraftPaths.DefaultProfile))
            return processes.Where(p => IsDefaultInstance(p.command))
                            .Select(p => (int?)p.pid).FirstOrDefault();
        return processes.Where(p => IsClaudeDesktop(p.command) && !IsHelper(p.command) && CarriesDataDir(p.command, profile))
                        .Select(p => (int?)p.pid).FirstOrDefault();
    }
}
