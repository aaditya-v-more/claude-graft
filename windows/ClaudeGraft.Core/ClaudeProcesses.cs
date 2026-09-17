namespace ClaudeGraft.Core;

public static class ClaudeProcesses
{
    public static Func<IReadOnlyList<(int pid, string command)>> Enumerate = () =>
        OperatingSystem.IsWindows() ? WindowsProcessQuery.ClaudeProcesses() : Array.Empty<(int, string)>();

    public static bool IsClaudeDesktop(string command)
    {
        var args = WindowsCommandLine.Parse(command);
        if (args.Length == 0) return false;
        var exe = args[0].Replace('/', '\\');
        return exe.EndsWith(@"\claude.exe", StringComparison.OrdinalIgnoreCase)
            && (exe.Contains(@"\AnthropicClaude\app-", StringComparison.OrdinalIgnoreCase)
                || (exe.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)
                    && exe.EndsWith(@"\app\claude.exe", StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsHelper(string command) => WindowsCommandLine.Option(WindowsCommandLine.Parse(command), "--type") is not null;
    public static bool HasUserDataDir(string command) => WindowsCommandLine.Option(WindowsCommandLine.Parse(command), "--user-data-dir") is not null;
    public static bool IsDefaultInstance(string command) => IsClaudeDesktop(command) && !IsHelper(command) && !HasUserDataDir(command);

    public static bool CarriesDataDir(string command, string profilePath)
    {
        var value = WindowsCommandLine.Option(WindowsCommandLine.Parse(command), "--user-data-dir");
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try { return Fs.SamePath(value, profilePath); }
        catch (ArgumentException) { return false; }
    }

    public static bool IsRunning(string profile) => IsRunning(profile, Enumerate());
    public static bool IsRunning(string profile, IReadOnlyList<(int pid, string command)> processes) =>
        processes.Any(p => IsClaudeDesktop(p.command) && (CarriesDataDir(p.command, profile)
            || (Fs.SamePath(profile, GraftPaths.DefaultProfile) && IsDefaultInstance(p.command))));

    public static int? ProcessIdentifier(string profile) => ProcessIdentifier(profile, Enumerate());
    public static int? ProcessIdentifier(string profile, IReadOnlyList<(int pid, string command)> processes) =>
        processes.Where(p => IsClaudeDesktop(p.command) && !IsHelper(p.command)
            && (CarriesDataDir(p.command, profile) || (Fs.SamePath(profile, GraftPaths.DefaultProfile) && IsDefaultInstance(p.command))))
            .Select(p => (int?)p.pid).FirstOrDefault();
}
