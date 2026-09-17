using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class ProcessDetectionTests
{
    private const string App = @"C:\Users\T\AppData\Local\AnthropicClaude\app-1.40609.1\claude.exe";

    // The command lines a real machine shows: a browser process, its helpers,
    // and the same shapes carrying a --user-data-dir.
    private static string Browser(string? dataDir = null) =>
        dataDir is null ? $"\"{App}\"" : $"\"{App}\" --user-data-dir={dataDir}";

    private static string Helper(string? dataDir = null) =>
        dataDir is null
            ? $"\"{App}\" --type=renderer"
            : $"\"{App}\" --type=renderer --user-data-dir={dataDir} --other";

    [Fact(DisplayName = "a helper is told from the browser process by its type flag")]
    public void HelperVsBrowser()
    {
        Assert.True(ClaudeProcesses.IsHelper(Helper()));
        Assert.False(ClaudeProcesses.IsHelper(Browser()));
    }

    [Fact(DisplayName = "the default instance is the browser with no profile flag")]
    public void DefaultInstance()
    {
        Assert.True(ClaudeProcesses.IsDefaultInstance(Browser()));
        Assert.False(ClaudeProcesses.IsDefaultInstance(Browser(@"C:\some\profile")));
        Assert.False(ClaudeProcesses.IsDefaultInstance(Helper()));
    }

    // The Claude Code CLI copy Claude Desktop keeps inside a profile and spawns
    // for a bridged session: named claude.exe, run from a claude-code folder, and
    // carrying neither --type nor --user-data-dir, so by flags alone it wears the
    // exact shape of the main instance.
    private static string BridgedCli() =>
        $"\"{Cli}\" --output-format stream-json --model claude-opus-4-8 --resume=abc";

    private const string Cli =
        @"C:\Users\T\AppData\Roaming\Claude\claude-code\2.1.255\claude.exe";

    [Fact(DisplayName = "the bridged Claude Code CLI is not mistaken for the main instance")]
    public void BridgedCliIsNotDefault()
    {
        // Reopening the main profile revealed this windowless process instead of
        // the desktop app whenever a session was bridging, and did nothing: by
        // flags it wears the main instance's exact shape, so only the binary
        // tells them apart.
        Assert.False(ClaudeProcesses.IsClaudeDesktop(BridgedCli()));
        Assert.True(ClaudeProcesses.IsClaudeDesktop(Browser()));
        Assert.False(ClaudeProcesses.IsDefaultInstance(BridgedCli()));
        Assert.True(ClaudeProcesses.IsDefaultInstance(Browser()));
    }

    [Fact(DisplayName = "a profile's data-dir is matched only when the value ends there")]
    public void AnchoredMatch()
    {
        var profile = @"C:\Users\T\AppData\Roaming\Claude";
        // exact, followed by end of line
        Assert.True(ClaudeProcesses.CarriesDataDir(Browser(profile), profile));
        // exact, followed by a space and more args
        Assert.True(ClaudeProcesses.CarriesDataDir(Helper(profile), profile));
        // a longer-named sibling must not match the shorter one
        Assert.False(ClaudeProcesses.CarriesDataDir(Browser(profile + "-2"), profile));
    }

    [Fact(DisplayName = "a quoted data-dir, as a path with a space gets, still matches")]
    public void QuotedMatch()
    {
        var profile = @"C:\Users\T\AppData\Roaming\Claude Work";
        var cmd = $"\"{App}\" --user-data-dir=\"{profile}\" --other";
        Assert.True(ClaudeProcesses.CarriesDataDir(cmd, profile));
    }

    [Fact(DisplayName = "a grafted profile is running when any of its processes carries the data-dir")]
    public void GraftedRunning()
    {
        var profile = @"C:\Users\T\AppData\Roaming\Claude-Work";
        var procs = new List<(int, string)>
        {
            (100, Browser()),                 // the default instance, unrelated
            (200, Helper(profile)),           // a helper of the grafted profile
        };
        Assert.True(ClaudeProcesses.IsRunning(profile, procs));

        // With only the default instance up, the grafted profile is not running.
        Assert.False(ClaudeProcesses.IsRunning(profile, new List<(int, string)> { (100, Browser()) }));
    }

    [Fact(DisplayName = "the pid handed back for a profile is the browser process, never a helper")]
    public void PidIsBrowser()
    {
        var profile = @"C:\Users\T\AppData\Roaming\Claude-Work";
        var procs = new List<(int, string)>
        {
            (200, Helper(profile)),           // a renderer, listed first
            (201, Browser(profile)),          // the browser process
        };
        Assert.Equal(201, ClaudeProcesses.ProcessIdentifier(profile, procs));
    }
}
