using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class WindowsSafetyTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly Func<IReadOnlyList<(int pid, string command)>> _processes = ClaudeProcesses.Enumerate;
    private readonly Func<ClaudeInstallation?> _discovery = ClaudeInstallation.Discover;
    public WindowsSafetyTests()
    {
        GraftPaths.ProfilesRootOverride = _temp.Dir("profiles");
        GraftPaths.ClaudeProjectsOverride = _temp.Dir("transcripts");
        ClaudeProcesses.Enumerate = () => Array.Empty<(int, string)>();
        Graft.ResetCachesForTests();
    }
    public void Dispose()
    {
        ClaudeProcesses.Enumerate = _processes;
        ClaudeInstallation.Discover = _discovery;
        GraftPaths.ProfilesRootOverride = null;
        GraftPaths.ClaudeProjectsOverride = null;
        _temp.Dispose();
    }

    [Theory(DisplayName = "Windows aliases and reserved names cannot become profile folders")]
    [InlineData("claude")][InlineData("CLAUDE")][InlineData("claudegraft")]
    [InlineData("Claude.")][InlineData("work ")][InlineData(" work")][InlineData("work.")]
    [InlineData("NUL")][InlineData("nul.json")][InlineData("COM1")][InlineData("LPT9.txt")]
    [InlineData("CONIN$")][InlineData("COM¹")][InlineData("a*b")][InlineData("a?b")]
    [InlineData("a|b")][InlineData("a<b")][InlineData("a>b")][InlineData("a\"b")]
    public void InvalidNames(string value) => Assert.NotNull(Graft.ValidateFolder(value));

    [Fact(DisplayName = "a profile prefix does not contain its sibling")]
    public void SiblingIsOutside()
    {
        var one = _temp.Dir("Claude");
        var other = _temp.Dir("Claude-Work", "store");
        Assert.False(Junction.ResolvesInside(other, one));
    }

    [Fact(DisplayName = "a linked parent resolves outside the profile even when the last folder is real")]
    public void LinkedAncestor()
    {
        var source = _temp.Dir("source", "account", "org");
        var profile = _temp.Dir("profile");
        var store = Path.Combine(profile, "store");
        Junction.Create(store, _temp.Dir("source"));
        var through = Path.Combine(store, "account", "org");
        Assert.True(Fs.SamePath(source, Fs.Resolve(through)));
        Assert.False(Junction.ResolvesInside(through, profile));
        Assert.False(Junction.ResolvesInside(Path.Combine(through, "not-yet-created"), profile));
    }

    [Fact(DisplayName = "junction paths containing percent signs are never expanded by a shell")]
    public void LiteralJunctionPaths()
    {
        var source = _temp.Dir("source %TEMP% & data");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "keep");
        var link = Path.Combine(_temp.Path, "link %TEMP% & data");
        Junction.Create(link, source);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(link, "keep.txt")));
        Junction.Remove(link);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, "keep.txt")));
    }

    [Fact(DisplayName = "a dangling junction is still a link that can be removed safely")]
    public void DanglingJunction()
    {
        var source = _temp.Dir("vanishing");
        var link = Path.Combine(_temp.Path, "dangling");
        Junction.Create(link, source);
        Directory.Delete(source);
        Assert.True(Junction.IsLink(link));
        Assert.True(Fs.Exists(link));
        Junction.Remove(link);
        Assert.False(Fs.Exists(link));
    }

    [Fact(DisplayName = "an unreadable record never deletes the readable copy")]
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
        var baseline = File.ReadAllBytes(Path.Combine(GraftPaths.OwnData, "mirrored-chats.json"));
        using (File.Open(Path.Combine(one, "local_locked.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, Graft.MirrorChatFolders(one, other));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(other, "local_locked.json")));
            Assert.Equal(baseline, File.ReadAllBytes(Path.Combine(GraftPaths.OwnData, "mirrored-chats.json")));
        }
    }

    [Fact(DisplayName = "corrupt mirror state is preserved and never treated as a first graft")]
    public void CorruptMirrorState()
    {
        var state = Path.Combine(GraftPaths.OwnData, "mirrored-chats.json");
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(state, "{broken");
        Assert.Throws<System.Text.Json.JsonException>(() => Graft.MirrorChatFolders(_temp.Dir("one"), _temp.Dir("other")));
        Assert.Equal("{broken", File.ReadAllText(state));
    }

    [Fact(DisplayName = "the app data directory and a linked profile cannot be deleted")]
    public void ProtectedDeletion()
    {
        Assert.Throws<ProfileException>(() => Graft.DeleteProfile(GraftPaths.Profile("ClaudeGraft"), _ => false));
        var target = _temp.Dir("outside");
        File.WriteAllText(Path.Combine(target, "keep"), "keep");
        var link = GraftPaths.Profile("linked");
        Junction.Create(link, target);
        Assert.Throws<ProfileException>(() => Graft.DeleteProfile(link, _ => false));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep")));
    }

    [Fact(DisplayName = "launch configuration cannot escape to another directory")]
    public void UnsafeConfig()
    {
        var outside = Path.Combine(_temp.Path, "outside");
        Assert.Throws<IOException>(() => Graft.Apply(new GraftConfig { ProfileDir = outside }));
        Assert.False(Directory.Exists(outside));
    }

    [Fact(DisplayName = "a missing legacy shortcut cannot silently ungraft a profile")]
    public void UnknownShortcut()
    {
        Assert.Throws<IOException>(() => Launcher.OpenByFolder("unknown"));
        Assert.False(Directory.Exists(GraftPaths.Profile("unknown")));
    }

    [Fact(DisplayName = "corrupt shortcut state cannot be overwritten by an empty list")]
    public void CorruptShortcutState()
    {
        Directory.CreateDirectory(GraftPaths.OwnData);
        var path = Path.Combine(GraftPaths.OwnData, "shortcuts.json");
        File.WriteAllText(path, "{broken");
        var store = new ShortcutStore();
        Assert.NotNull(store.LoadError);
        Assert.Throws<IOException>(store.Save);
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact(DisplayName = "a shortcut retains its source when the manager list is unavailable")]
    public void IndependentManifest()
    {
        var shortcut = Shortcut.New("Claude Work");
        var config = new GraftConfig { ProfileDir = shortcut.ProfileDir, SourceDir = GraftPaths.DefaultProfile };
        ShortcutManifest.Save(shortcut, config);
        var read = ShortcutManifest.Read(ShortcutManifest.FileFor(shortcut.Id));
        Assert.Equal(config.ProfileDir, read.ProfileDir);
        Assert.Equal(config.SourceDir, read.SourceDir);
    }

    [Fact(DisplayName = "version 1.10 wins over version 1.9 when finding the standalone install")]
    public void NumericVersions()
    {
        var paths = new[] { @"C:\AnthropicClaude\app-1.9.0\claude.exe", @"C:\AnthropicClaude\app-1.10.0\claude.exe" };
        Assert.Equal(paths[1], ClaudeInstallation.NewestSquirrelExecutable(paths));
    }

    [Fact(DisplayName = "Store Claude is recognized without mistaking its bundled CLI for the desktop")]
    public void StoreProcesses()
    {
        Assert.True(ClaudeProcesses.IsClaudeDesktop("\"C:\\Program Files\\WindowsApps\\Claude_1.2.0.0_x64__pzs8sxrjxfjjc\\app\\claude.exe\""));
        Assert.False(ClaudeProcesses.IsClaudeDesktop("\"C:\\profiles\\Claude-Work\\claude-code\\1.0\\claude.exe\""));
    }

    [Theory(DisplayName = "Windows argument parsing recognizes each supported data-directory spelling")]
    [InlineData("--user-data-dir=\"C:\\profiles\\Work account\"")]
    [InlineData("\"--user-data-dir=C:\\profiles\\Work account\"")]
    [InlineData("--user-data-dir \"C:\\profiles\\Work account\"")]
    public void ProfileArguments(string flags) =>
        Assert.True(ClaudeProcesses.CarriesDataDir("\"C:\\AnthropicClaude\\app-1.0\\claude.exe\" " + flags, @"C:\profiles\Work account"));

    [Fact(DisplayName = "a data-directory string inside another argument is not a profile flag")]
    public void MisleadingArgument()
    {
        Assert.False(ClaudeProcesses.CarriesDataDir("claude.exe --other=--user-data-dir=C:\\profiles\\work", @"C:\profiles\work"));
    }

    [Fact(DisplayName = "launching the main profile clears inherited profile overrides")]
    public void MainLaunch()
    {
        var start = Launcher.StartInfo("Claude.exe", GraftPaths.DefaultProfile);
        Assert.False(start.Environment.ContainsKey("CLAUDE_USER_DATA_DIR"));
        Assert.Empty(start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }

    [Fact(DisplayName = "a profile path with spaces is passed as one argument and an explicit environment override")]
    public void ExtraLaunch()
    {
        var profile = GraftPaths.Profile("Work account");
        var start = Launcher.StartInfo("Claude.exe", profile);
        Assert.Equal(new[] { "--user-data-dir=" + profile }, start.ArgumentList);
        Assert.Equal(profile, start.Environment["CLAUDE_USER_DATA_DIR"]);
    }

    [Theory(DisplayName = "the sign-in helper refuses links outside Claude")]
    [InlineData("https://claude.ai/login")][InlineData("file:///C:/thing")]
    [InlineData("claude://evil.example/callback")][InlineData("claude://user@claude.ai/callback")]
    [InlineData("claude://claude.ai/callback\r\n--flag")]
    public void BadCallback(string value) => Assert.False(Launcher.ValidCallback(value));

    [Fact(DisplayName = "a sign-in callback remains a single literal argument")]
    public void CallbackArgument()
    {
        const string callback = "claude://claude.ai/oauth?code=fixture&state=fixture";
        var start = Launcher.StartInfo("Claude.exe", GraftPaths.Profile("Work"), callback);
        Assert.Equal(callback, start.ArgumentList[1]);
        Assert.Equal(2, start.ArgumentList.Count);
    }

    [Fact(DisplayName = "a missing Claude installation does not create or modify a profile")]
    public void MissingClaude()
    {
        ClaudeInstallation.Discover = () => null;
        var profile = GraftPaths.Profile("Work");
        Assert.Throws<IOException>(() => Launcher.Open(new GraftConfig { ProfileDir = profile }));
        Assert.False(Directory.Exists(profile));
    }

    [Fact(DisplayName = "a failed process query prevents moving a profile")]
    public void UnknownProcesses()
    {
        var from = GraftPaths.Profile("Work");
        Directory.CreateDirectory(from);
        File.WriteAllText(Path.Combine(from, "keep"), "keep");
        ClaudeProcesses.Enumerate = () => throw new IOException("fixture refusal");
        Assert.Throws<IOException>(() => Graft.MoveProfileFolder("Work", "Renamed"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(from, "keep")));
    }

    [Fact(DisplayName = "sharing files preserves the original and carries later edits both ways")]
    public void SharedSettingsRoundTrip()
    {
        var source = _temp.Write("source/settings.json", "{\"source\":1}");
        var target = _temp.Write("target/settings.json", "{\"own\":1}");
        Assert.True(Graft.Relink(source, target));
        Assert.Equal("{\"source\":1}", File.ReadAllText(target));
        Assert.Equal("{\"own\":1}", File.ReadAllText(Graft.StashPath(target)));
        File.WriteAllText(target, "{\"edited\":2}");
        Graft.Relink(source, target);
        Assert.Equal("{\"edited\":2}", File.ReadAllText(source));
        File.WriteAllText(source, "{\"sourceEdited\":3}");
        Graft.Relink(source, target);
        Assert.Equal("{\"sourceEdited\":3}", File.ReadAllText(target));
        SharedSettings.Restore(target);
        Assert.Equal("{\"own\":1}", File.ReadAllText(target));
        Assert.Equal("{\"sourceEdited\":3}", File.ReadAllText(source));
    }

    [Fact(DisplayName = "conflicting settings edits keep a recoverable copy of the losing version")]
    public void SettingsConflict()
    {
        var source = _temp.Write("source/settings.json", "initial");
        var target = Path.Combine(_temp.Dir("target"), "settings.json");
        SharedSettings.Share(source, target);
        File.WriteAllText(source, "source edit");
        File.WriteAllText(target, "local edit");
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(1));
        SharedSettings.Share(source, target);
        Assert.Equal("local edit", File.ReadAllText(source));
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.graft-conflict-*"));
        Assert.Equal("source edit", File.ReadAllText(backup));
    }

    [Fact(DisplayName = "a scoped Fable limit is reported separately from the ordinary weekly limit")]
    public void FableLimit()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
        {"five_hour":{"utilization":12},"seven_day":{"utilization":34},
         "limits":[{"kind":"weekly_scoped","scope":{"model":{"display_name":"Fable"}},"percent":56,"resets_at":"2026-10-01T00:00:00Z"}]}
        """);
        var usage = UsageApi.ReadingFrom(json.RootElement)!;
        Assert.Equal(12, usage.FiveHour);
        Assert.Equal(34, usage.Week);
        Assert.Equal(56, usage.Fable);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), usage.FableReset);
    }

    [Fact(DisplayName = "an absent Fable limit does not invent a zero-percent reading")]
    public void NoFableLimit()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""{"five_hour":{"utilization":12},"limits":[{"kind":3}]}""");
        Assert.Null(UsageApi.ReadingFrom(json.RootElement)!.Fable);
    }

    [Theory(DisplayName = "unreadable session state cannot forget withdrawn chats")]
    [InlineData("{broken")][InlineData("null")][InlineData("{\"withdrawn\":null}")]
    public void UnreadableSessionState(string content)
    {
        var path = _temp.Write("session-state.json", content);
        Assert.ThrowsAny<Exception>(() => SessionRecordState.Load(path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact(DisplayName = "a null shortcut document is corrupt rather than an empty list")]
    public void NullShortcutList()
    {
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(Path.Combine(GraftPaths.OwnData, "shortcuts.json"), "null");
        var store = new ShortcutStore();
        Assert.NotNull(store.LoadError);
        Assert.Throws<IOException>(store.Save);
    }

    [Theory(DisplayName = "unreadable sidebar records do not withdraw remembered chats on repeated sweeps")]
    [InlineData("{broken")][InlineData("{}")][InlineData("{\"cliSessionId\":42}")]
    public void UnreadableRecordDoesNotWithdraw(string content)
    {
        var profile = GraftPaths.Profile("Work");
        var org = Path.Combine(profile, "claude-code-sessions", "account", "org");
        Directory.CreateDirectory(org);
        File.WriteAllText(Path.Combine(org, "local_record.json"), content);
        var stateFile = Path.Combine(GraftPaths.OwnData, "session-records.json");
        new SessionRecordState { Records = new() { ["remembered"] = Fs.Resolve(org) } }.Save(stateFile);
        Assert.DoesNotContain(Fs.Resolve(org), Graft.SessionStoreContents().Stores);
        Graft.FileMissingSessionRecords(new[] { profile }, _ => false);
        Graft.FileMissingSessionRecords(new[] { profile }, _ => false);
        var state = SessionRecordState.Load(stateFile);
        Assert.Empty(state.Withdrawn);
        Assert.Empty(state.Vanished);
        Assert.Contains("remembered", state.Records.Keys);
        Assert.Equal(content, File.ReadAllText(Path.Combine(org, "local_record.json")));
    }
}
