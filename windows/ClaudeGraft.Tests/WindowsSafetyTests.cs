using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class WindowsSafetyTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly TimeSpan _patience = StateLock.Patience;
    private readonly Action<string, string?> _starter = Launcher.Starter;
    private readonly Func<IReadOnlyList<(int pid, string command)>> _enumerate = ClaudeProcesses.Enumerate;
    public WindowsSafetyTests()
    {
        GraftPaths.ProfilesRootOverride = _temp.Dir("profiles");
        GraftPaths.ClaudeProjectsOverride = _temp.Dir("transcripts");
    }
    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        GraftPaths.ClaudeProjectsOverride = null;
        StateLock.Patience = _patience;
        Launcher.Starter = _starter;
        ClaudeProcesses.Enumerate = _enumerate;
        _temp.Dispose();
    }

    [Theory(DisplayName = "Windows aliases, device names and reserved characters cannot become profile folders")]
    [InlineData("claude")][InlineData("CLAUDE")][InlineData("claudegraft")]
    [InlineData("Claude.")][InlineData("work.")]
    [InlineData("NUL")][InlineData("nul.json")][InlineData("COM1")][InlineData("LPT9.txt")]
    [InlineData("CONIN$")][InlineData("a*b")][InlineData("a?b")]
    [InlineData("a|b")][InlineData("a<b")][InlineData("a>b")][InlineData("a\"b")]
    public void InvalidNames(string value) => Assert.NotNull(Graft.ValidateFolder(value));

    [Fact(DisplayName = "a profile whose name is a prefix of another's does not contain it")]
    public void SiblingIsOutside()
    {
        var one = _temp.Dir("Claude");
        var other = _temp.Dir("Claude-Work", "store");
        Assert.False(Junction.ResolvesInside(other, one));
    }

    [Fact(DisplayName = "a real folder reached through a linked store resolves outside the profile")]
    public void LinkedAncestor()
    {
        _temp.Dir("source", "account", "org");
        var profile = _temp.Dir("profile");
        var store = Path.Combine(profile, "store");
        Junction.Create(store, _temp.Dir("source"));
        var through = Path.Combine(store, "account", "org");
        Assert.False(Junction.ResolvesInside(through, profile));
    }

    [Fact(DisplayName = "a folder not made yet under a linked store resolves outside the profile")]
    public void MissingUnderLinkedAncestor()
    {
        _temp.Dir("source", "account");
        var profile = _temp.Dir("profile");
        var store = Path.Combine(profile, "store");
        Junction.Create(store, _temp.Dir("source"));
        Assert.False(Junction.ResolvesInside(Path.Combine(store, "account", "new-org"), profile));
    }

    [Fact(DisplayName = "a junction path holding percent signs is taken literally")]
    public void LiteralJunctionPaths()
    {
        var source = _temp.Dir("source %TEMP% & data");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "keep");
        var link = Path.Combine(_temp.Path, "link %TEMP% & data");
        Junction.Create(link, source);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(link, "keep.txt")));
    }

    [Fact(DisplayName = "a junction whose target has gone is still a link that can be removed")]
    public void DanglingJunction()
    {
        var source = _temp.Dir("vanishing");
        var link = Path.Combine(_temp.Path, "dangling");
        Junction.Create(link, source);
        Directory.Delete(source);
        Assert.True(Junction.IsLink(link));
        Junction.Remove(link);
        Assert.False(Fs.Exists(link));
    }

    [Fact(DisplayName = "a record another process holds open never costs the other side its copy")]
    public void LockedRecord()
    {
        var one = _temp.Dir("one");
        var other = _temp.Dir("other");
        foreach (var name in new[] { "local_locked.json", "local_other.json" })
        {
            File.WriteAllText(Path.Combine(one, name), "{}");
            File.WriteAllText(Path.Combine(other, name), "{}");
        }
        Graft.MirrorChatFolders(one, other);
        using (File.Open(Path.Combine(one, "local_locked.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Graft.MirrorChatFolders(one, other);
            Graft.MirrorChatFolders(one, other);
            Assert.True(File.Exists(Path.Combine(other, "local_locked.json")));
        }
    }

    [Fact(DisplayName = "a mirror state that will not parse is unreadable, never a first pass")]
    public void CorruptMirrorStateNotEmpty()
    {
        var state = Path.Combine(GraftPaths.OwnData, "mirrored-chats.json");
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(state, "{broken");
        Assert.ThrowsAny<Exception>(() => MirrorState.Load(state));
    }

    [Fact(DisplayName = "deleting a profile that is a junction leaves its target alone")]
    public void LinkedProfileDeletion()
    {
        var target = _temp.Dir("outside");
        File.WriteAllText(Path.Combine(target, "keep"), "keep");
        var link = GraftPaths.Profile("linked");
        Junction.Create(link, target);
        try { Graft.DeleteProfile(link, _ => false); } catch (ProfileException) { }
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep")));
    }

    [Fact(DisplayName = "an unreadable shortcut list is never saved over with an empty one")]
    public void CorruptShortcutState()
    {
        Directory.CreateDirectory(GraftPaths.OwnData);
        var path = Path.Combine(GraftPaths.OwnData, "shortcuts.json");
        File.WriteAllText(path, "{broken");
        var store = new ShortcutStore();
        try { store.Save(); } catch { }
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact(DisplayName = "the Claude Code copy inside a profile is not the desktop app")]
    public void StoreProcesses()
    {
        Assert.False(ClaudeProcesses.IsClaudeDesktop("\"C:\\profiles\\Claude-Work\\claude-code\\1.0\\claude.exe\""));
    }

    [Theory(DisplayName = "each way of spelling the data directory on a command line is recognised")]
    [InlineData("--user-data-dir=\"C:\\profiles\\Work account\"")]
    [InlineData("\"--user-data-dir=C:\\profiles\\Work account\"")]
    [InlineData("--user-data-dir \"C:\\profiles\\Work account\"")]
    public void ProfileArguments(string flags) =>
        Assert.True(ClaudeProcesses.CarriesDataDir("\"C:\\AnthropicClaude\\app-1.0\\claude.exe\" " + flags, @"C:\profiles\Work account"));

    [Fact(DisplayName = "a data-directory flag spelled inside another argument is not one")]
    public void MisleadingArgument() =>
        Assert.False(ClaudeProcesses.CarriesDataDir("claude.exe --other=--user-data-dir=C:\\profiles\\work", @"C:\profiles\work"));

    [Theory(DisplayName = "an unreadable session state is not an empty one")]
    [InlineData("{broken")][InlineData("null")]
    public void UnreadableSessionState(string content)
    {
        var path = _temp.Write("session-state.json", content);
        Assert.ThrowsAny<Exception>(() => SessionRecordState.Load(path));
    }

    [Fact(DisplayName = "a scoped Fable limit is read apart from the weekly one")]
    public void FableLimit()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
        {"five_hour":{"utilization":12},"seven_day":{"utilization":34},
         "limits":[{"kind":"weekly_scoped","scope":{"model":{"display_name":"Fable"}},"percent":56,"resets_at":"2026-10-01T00:00:00Z"}]}
        """);
        Assert.Equal(56, UsageApi.ReadingFrom(json.RootElement)!.Fable);
    }

    [Theory(DisplayName = "a record that will not read does not withdraw its session on repeated sweeps")]
    [InlineData("{broken")][InlineData("{}")][InlineData("{\"cliSessionId\":42}")]
    public void UnreadableRecordDoesNotWithdraw(string content)
    {
        var profile = GraftPaths.Profile("Work");
        var org = Path.Combine(profile, "claude-code-sessions", "account", "org");
        Directory.CreateDirectory(org);
        File.WriteAllText(Path.Combine(org, "local_record.json"), content);
        var stateFile = Path.Combine(GraftPaths.OwnData, "session-records.json");
        new SessionRecordState { Records = new() { ["remembered"] = Fs.Resolve(org) } }.Save(stateFile);
        Graft.FileMissingSessionRecords(new[] { profile }, () => false);
        Graft.FileMissingSessionRecords(new[] { profile }, () => false);
        var state = SessionRecordState.Load(stateFile);
        Assert.Empty(state.Withdrawn);
        Assert.Contains("remembered", state.Records.Keys);
    }

    [Fact(DisplayName = "not knowing which Claudes are running counts as one running")]
    public void UnknownProcessesCountAsRunning()
    {
        var saved = ClaudeProcesses.Enumerate;
        ClaudeProcesses.Enumerate = () => throw new IOException("fixture refusal");
        try { Assert.True(ClaudeProcesses.AnyDesktopRunning()); }
        finally { ClaudeProcesses.Enumerate = saved; }
    }

    [Fact(DisplayName = "the state lock can be taken again by the thread already holding it")]
    public void StateLockIsReentrant()
    {
        Assert.Equal(3, StateLock.Run(() => StateLock.Run(() => 3)));
    }

    [Fact(DisplayName = "a pass waits for another process holding the state lock")]
    public void StateLockSerializesThreads()
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => StateLock.Run(() => { inside.Set(); release.Wait(); }));
        inside.Wait();
        var second = Task.Run(() => StateLock.Run(() => 1));
        Assert.False(second.Wait(200));
        release.Set();
        Assert.Equal(1, second.Result);
        holder.Wait();
    }

    /// Holds the state lock on another thread until the returned handle is disposed.
    private static IDisposable HoldStateLock()
    {
        var inside = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = Task.Run(() => StateLock.Run(() => { inside.Set(); release.Wait(); }));
        inside.Wait();
        return new Released(() => { release.Set(); holder.Wait(); inside.Dispose(); release.Dispose(); });
    }

    private sealed class Released(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    [Fact(DisplayName = "a pass that cannot get the state lock runs nothing and says it was busy")]
    public void BusyLockRunsNothing()
    {
        StateLock.Patience = TimeSpan.FromMilliseconds(200);
        var ran = false;
        using (HoldStateLock())
            Assert.Throws<StateBusyException>(() => StateLock.Run(() => ran = true));
        Assert.False(ran);
    }

    [Fact(DisplayName = "a busy lock leaves the thread able to take it once it is free")]
    public void BusyLockLeavesThreadClean()
    {
        StateLock.Patience = TimeSpan.FromMilliseconds(200);
        using (HoldStateLock())
            Assert.Throws<StateBusyException>(() => StateLock.Run(() => 0));
        StateLock.Patience = TimeSpan.FromSeconds(20);
        StateLock.Run(() =>
        {
            // Only a lock really taken keeps another thread out.
            var other = Task.Run(() => StateLock.Run(() => 1));
            Assert.False(other.Wait(200));
            return 0;
        });
    }

    [Fact(DisplayName = "opening a profile while the state lock is held still opens it and touches no state")]
    public void OpenWhileBusyStillOpens()
    {
        StateLock.Patience = TimeSpan.FromMilliseconds(200);
        ClaudeProcesses.Enumerate = () => [];
        var started = new List<string>();
        Launcher.Starter = (profile, _) => started.Add(profile);
        Directory.CreateDirectory(GraftPaths.OwnData);
        var mirror = Path.Combine(GraftPaths.OwnData, "mirrored-chats.json");
        var records = Path.Combine(GraftPaths.OwnData, "session-records.json");
        File.WriteAllText(mirror, "{\"pairs\":[]}");
        File.WriteAllText(records, "{\"records\":{}}");
        var before = (File.ReadAllBytes(mirror), File.ReadAllBytes(records));
        var main = _temp.Dir("profiles", "Claude");
        var work = _temp.Dir("profiles", "Claude-Work");

        using (HoldStateLock())
            Launcher.Open(new GraftConfig { ProfileDir = work, SourceDir = main });

        Assert.Equal(new[] { work }, started);
        Assert.Equal(before.Item1, File.ReadAllBytes(mirror));
        Assert.Equal(before.Item2, File.ReadAllBytes(records));
    }

    [Fact(DisplayName = "a profile first opened while busy is shared on the next pass without losing what Claude wrote")]
    public void FirstOpenWhileBusyIsSharedLater()
    {
        StateLock.Patience = TimeSpan.FromMilliseconds(200);
        ClaudeProcesses.Enumerate = () => [];
        Launcher.Starter = (_, _) => { };
        Graft.ResetCachesForTests();
        var main = Profile("Claude", "AAAA", "ORG-A");
        File.WriteAllText(Path.Combine(main, "claude-code-sessions", "AAAA", "ORG-A", "local_shared.json"), "{}");
        var work = Profile("Claude-Work", "BBBB", "ORG-B");
        var config = new GraftConfig { ProfileDir = work, SourceDir = main };

        using (HoldStateLock())
            Launcher.Open(config);
        // What Claude makes on a first open: its own chat and a folder beneath it.
        var own = Path.Combine(work, "claude-code-sessions", "BBBB", "ORG-B");
        File.WriteAllText(Path.Combine(own, "local_mine.json"), "{}");
        Directory.CreateDirectory(Path.Combine(own, "local_mine"));
        File.WriteAllText(Path.Combine(own, "local_mine", "audit.jsonl"), "{}");

        Graft.Apply(config);

        Assert.True(File.Exists(Path.Combine(own, "local_shared.json")));
        Assert.True(File.Exists(Path.Combine(own, "local_mine.json")));
        var stash = Path.Combine(work, "claude-code-sessions", "BBBB", ".ORG-B.graft-own");
        Assert.True(File.Exists(Path.Combine(stash, "local_mine.json")));
        Assert.True(File.Exists(Path.Combine(stash, "local_mine", "audit.jsonl")));
    }

    private string Profile(string name, string account, string org)
    {
        var profile = _temp.Dir("profiles", name);
        File.WriteAllText(Path.Combine(profile, "config.json"), $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
        foreach (var store in GraftPaths.ChatStoreNames)
            Directory.CreateDirectory(Path.Combine(profile, store, account, org));
        return profile;
    }
}
