using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// What the state report says, against a throwaway profiles root and login —
/// the Swift suite's "Reading the state of the stores".
/// </summary>
[Collection("GlobalState")]
public sealed class StateReportTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly Func<IReadOnlyList<(int pid, string command)>> _enumerate = ClaudeProcesses.Enumerate;
    private const string Owner = "AAAAAAAA-0000-0000-0000-000000000001";
    private const string Borrower = "BBBBBBBB-0000-0000-0000-000000000002";
    private const string Org = "CCCCCCCC-0000-0000-0000-000000000003";
    private const string Email = "someone@example.com";

    public StateReportTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        GraftPaths.ClaudeProjectsOverride = _t.Dir("projects");
        GraftPaths.ClaudeConfigFileOverride = Path.Combine(_t.Path, "claude.json");
        ClaudeProcesses.Enumerate = () => [];
        Graft.ResetCachesForTests();
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        GraftPaths.ClaudeProjectsOverride = null;
        GraftPaths.ClaudeConfigFileOverride = null;
        ClaudeProcesses.Enumerate = _enumerate;
        _t.Dispose();
    }

    private void LogIn(string account) => File.WriteAllText(GraftPaths.ClaudeConfigFileOverride!,
        $"{{\"oauthAccount\":{{\"accountUuid\":\"{account}\",\"organizationUuid\":\"{Org}\",\"emailAddress\":\"{Email}\"}}}}");

    private string MakeProfile(string name, string account, params string[] chats)
    {
        var profile = Path.Combine(GraftPaths.ProfilesRoot, name);
        var org = Path.Combine(profile, "claude-code-sessions", account, Org);
        Directory.CreateDirectory(org);
        File.WriteAllText(Path.Combine(profile, "config.json"), $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
        foreach (var chat in chats)
            File.WriteAllText(Path.Combine(org, $"local_{chat}.json"),
                $"{{\"cliSessionId\":\"{chat}\",\"title\":\"Private plans for {chat}\"}}");
        return profile;
    }

    [Fact(DisplayName = "the account every session on this machine is stamped with is read off the command line")]
    public void ReadsTheLogin()
    {
        LogIn(Owner);
        Assert.Equal((Owner, Org), Graft.CommandLineLogin());
    }

    [Fact(DisplayName = "the report says which profile holds the login, and that one shared login is not two profiles syncing")]
    public void NamesTheSharedLogin()
    {
        LogIn(Owner);
        MakeProfile("Report-Owner", Owner, "r-1");
        MakeProfile("Report-Borrower", Borrower, "r-2");

        var text = Graft.StateReportText(checkingRunning: false);

        Assert.Contains("held by Report-Owner", text);
        Assert.Contains("Report-Borrower is signed into BBBBBBBB", text);
        Assert.Contains("not two profiles syncing", text);
    }

    [Fact(DisplayName = "with no command line login the report says no session can be filed")]
    public void NoLogin()
    {
        MakeProfile("Claude", Owner, "r-1");
        var text = Graft.StateReportText(checkingRunning: false);
        Assert.Contains(@"Command line login (%USERPROFILE%\.claude.json): none", text);
        Assert.Contains("none can be filed", text);
    }

    [Fact(DisplayName = "a stash holding more than the folder beside it reads as a graft undone without handing them back")]
    public void CountsTheStash()
    {
        LogIn(Borrower);
        var profile = MakeProfile("Report-Borrower", Borrower, "r-2");
        var stash = Path.Combine(profile, "claude-code-sessions", Borrower, "." + Org + ".graft-own");
        Directory.CreateDirectory(stash);
        foreach (var chat in new[] { "r-3", "r-4", "r-5" })
            File.WriteAllText(Path.Combine(stash, $"local_{chat}.json"), "{}");

        var text = Graft.StateReportText(checkingRunning: false);

        Assert.Contains("1 record, 3 stashed beside it", text);
        Assert.Contains("the sidebar is short by 3", text);
    }

    [Fact(DisplayName = "the device a profile registered as is read back from its own file, and one never registered has none")]
    public void ReadsTheDevice()
    {
        var registered = MakeProfile("Claude", Owner);
        var never = MakeProfile("Claude-2", Borrower);
        File.WriteAllText(Path.Combine(registered, "ant-did"), "QUFBQS1CQkJC");

        Assert.Equal("AAAA-BBBB", Graft.DeviceIdentifier(registered));
        Assert.Null(Graft.DeviceIdentifier(never));
    }

    [Fact(DisplayName = "a profile whose config.json will not parse is named as one nothing may decide for")]
    public void UnreadableConfig()
    {
        LogIn(Owner);
        var profile = MakeProfile("Claude", Owner);
        File.WriteAllText(Path.Combine(profile, "config.json"), "{\"lastKnown");

        var text = Graft.StateReportText(checkingRunning: false);

        Assert.Contains("Claude  [not a shortcut, config.json unreadable]", text);
        Assert.Contains("Claude has a config.json that will not parse", text);
    }

    [Fact(DisplayName = "records remembered where far fewer stand are a finding")]
    public void RememberedButGone()
    {
        LogIn(Owner);
        var profile = MakeProfile("Claude", Owner, "kept");
        var org = Fs.Resolve(Path.Combine(profile, "claude-code-sessions", Owner, Org));
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(Path.Combine(GraftPaths.OwnData, "session-records.json"),
            System.Text.Json.JsonSerializer.Serialize(new { records = new Dictionary<string, string>
            {
                ["kept"] = org, ["gone-1"] = org, ["gone-2"] = org, ["gone-3"] = org,
            } }));

        var text = Graft.StateReportText(checkingRunning: false);

        Assert.Contains("1 record, 4 filed here", text);
        Assert.Contains("holds 1 record where 4 were filed", text);
    }

    [Fact(DisplayName = "a Claude open on a profile is marked running, asked of the processes once")]
    public void MarksRunning()
    {
        LogIn(Owner);
        MakeProfile("Claude", Owner);
        MakeProfile("Claude-2", Borrower);
        var asked = 0;
        ClaudeProcesses.Enumerate = () =>
        {
            asked++;
            return [(1, @"""C:\Users\u\AppData\Local\AnthropicClaude\app-1.0.0\claude.exe""")];
        };

        var text = Graft.StateReportText();

        Assert.Contains("Claude  [not a shortcut, running]", text);
        Assert.Contains("Claude-2  [not a shortcut]", text);
        Assert.Equal(1, asked);
    }

    [Fact(DisplayName = "a mirrored pair is written as one folder against the other, never with the byte between them")]
    public void WritesPairs()
    {
        LogIn(Owner);
        MakeProfile("Claude", Owner);
        var one = _t.Dir("one");
        var other = _t.Dir("other");
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(Path.Combine(GraftPaths.OwnData, "mirrored-chats.json"),
            System.Text.Json.JsonSerializer.Serialize(new { pairs = new Dictionary<string, object>
            {
                [Graft.PairKey(one, other)] = new Dictionary<string, string>(),
            } }));

        var text = Graft.StateReportText(checkingRunning: false);

        Assert.Contains("Mirrored folder pairs: 1", text);
        Assert.Contains($"  {Fs.Resolve(one)}\n    against {Fs.Resolve(other)}", text);
        Assert.DoesNotContain('\0', text);
    }

    [Fact(DisplayName = "neither the report nor its log line holds an email address, a chat title or a record's contents")]
    public void KeepsPrivateThingsOut()
    {
        LogIn(Owner);
        MakeProfile("Claude", Owner, "r-1");

        var text = Graft.StateReportText(checkingRunning: false);
        var log = File.ReadAllText(Diagnostics.LogFile);

        foreach (var written in new[] { text, log })
        {
            Assert.DoesNotContain(Email, written);
            Assert.DoesNotContain("Private plans", written);
        }
        Assert.Contains("\"event\":\"state\"", log);
    }

    [Fact(DisplayName = "a pass writes the report whole beside diagnostics.log in Graft's own folder")]
    public void APassWritesIt()
    {
        LogIn(Owner);
        var profile = MakeProfile("Claude", Owner, "r-1");

        Launcher.Sweep(new[] { profile });

        var file = Path.Combine(GraftPaths.OwnData, "state-report.txt");
        Assert.Equal(file, Diagnostics.ReportFile);
        Assert.Equal(Path.GetDirectoryName(Diagnostics.LogFile), Path.GetDirectoryName(file));
        Assert.StartsWith("Claude Graft — state of the chat stores\n", File.ReadAllText(file));
        Assert.Contains("Nothing to report.", File.ReadAllText(file));
        Assert.Empty(Directory.EnumerateFiles(GraftPaths.OwnData, "state-report.txt.tmp-*"));
    }

    [Fact(DisplayName = "a pass stopped by an unreadable state file still writes the report, and the report says why")]
    public void AStoppedPassStillWritesIt()
    {
        LogIn(Owner);
        var profile = MakeProfile("Claude", Owner, "r-1");
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(Path.Combine(GraftPaths.OwnData, "session-records.json"), "{\"records\":");

        Launcher.Sweep(new[] { profile });

        var text = File.ReadAllText(Diagnostics.ReportFile);
        Assert.Contains("session-records.json will not read", text);
    }

    [Fact(DisplayName = "notes from a test that leaves the profiles root alone never reach the real data folder")]
    public void NotesStayOutOfAppData()
    {
        GraftPaths.ProfilesRootOverride = null;
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeGraft");
        Assert.False(Diagnostics.LogFile.StartsWith(real, StringComparison.OrdinalIgnoreCase));
        Assert.False(Diagnostics.ReportFile.StartsWith(real, StringComparison.OrdinalIgnoreCase));
    }
}
