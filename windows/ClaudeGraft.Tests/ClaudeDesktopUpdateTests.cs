using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class ClaudeFeedTests
{
    [Fact(DisplayName = "the newest version in Claude's feed is the one offered, whatever order it lists them")]
    public void NewestInFeed()
    {
        const string releases = """
            0AB1 AnthropicClaude-5.1231.2-full.nupkg 264890967
            0AB2 AnthropicClaude-5.1232.0-full.nupkg 264890999
            0AB3 AnthropicClaude-5.1231.10-delta.nupkg 1000
            """;
        Assert.Equal(new Version(5, 1232, 0), ClaudeDesktopUpdates.LatestIn(releases));
    }

    [Fact(DisplayName = "a feed naming no package offers nothing")]
    public void EmptyFeed() => Assert.Null(ClaudeDesktopUpdates.LatestIn(""));

    [Fact(DisplayName = "a running Claude's version is read off the folder it was started from")]
    public void RunningVersion() =>
        Assert.Equal(new Version(5, 1231, 2), ClaudeDesktopUpdates.RunningVersion(
            "\"C:\\Users\\T\\AppData\\Local\\AnthropicClaude\\app-5.1231.2\\claude.exe\" --user-data-dir=C:\\x"));

    private const string Log = """
        [10/11/26 23:21:09] info: Program: Starting Squirrel Updater: --processStartAndWait claude.exe
        [10/11/26 23:21:09] info: Program: Want to launch 'C:\Users\T\AppData\Local\AnthropicClaude\app-5.1231.2\claude.exe'
        [10/11/26 23:21:09] info: Program: About to wait for parent PID 25096
        [10/11/26 23:21:10] info: Program: About to launch: 'C:\Users\T\AppData\Local\AnthropicClaude\app-5.1231.2\claude.exe':
        """;

    [Fact(DisplayName = "Squirrel's log says which Claude quit to be restarted, and when")]
    public void RestartRead()
    {
        var restart = Assert.Single(ClaudeDesktopUpdates.Restarts(Log));
        Assert.Equal(25096, restart.ParentPid);
        Assert.Equal(new DateTime(2026, 11, 10, 23, 21, 9), restart.At);
    }

    [Fact(DisplayName = "a Claude that quit with no restart in the log simply quit")]
    public void PlainQuit() =>
        Assert.False(ClaudeDesktopUpdates.QuitToUpdate(Log, 4242, new DateTime(2026, 11, 10, 22, 0, 0)));

    [Fact(DisplayName = "a restart logged for the same pid before this process started was another process's")]
    public void ReusedPid() =>
        Assert.False(ClaudeDesktopUpdates.QuitToUpdate(Log, 25096, new DateTime(2026, 11, 11, 9, 0, 0)));

    [Fact(DisplayName = "a restart logged for this process after it started is the update's")]
    public void UpdateRestart() =>
        Assert.True(ClaudeDesktopUpdates.QuitToUpdate(Log, 25096, new DateTime(2026, 11, 10, 22, 0, 0)));
}

[Collection("GlobalState")]
public sealed class ManualUpdatesTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly string _folder;

    public ManualUpdatesTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        _folder = Path.Combine(_t.Path, "Claude-3p");
        ManualUpdates.Folder = () => _folder;
        ManualUpdates.Managed = () => false;
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        ManualUpdates.Folder = () => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-3p");
        _t.Dispose();
    }

    private string Library => Path.Combine(_folder, "configLibrary");
    private JsonObject Json(string file) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Library, file)))!;
    private string AppliedId => Json("_meta.json")["appliedId"]!.GetValue<string>();

    [Fact(DisplayName = "turning manual updates on writes Claude's own switch into the configuration it applies")]
    public void TurnsOff()
    {
        ManualUpdates.SetEnabled(true);
        Assert.True(Json(AppliedId + ".json")["disableAutoUpdates"]!.GetValue<bool>());
    }

    [Fact(DisplayName = "turning it off again leaves no trace where there was nothing before")]
    public void LeavesNoTrace()
    {
        ManualUpdates.SetEnabled(true);
        ManualUpdates.SetEnabled(false);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact(DisplayName = "an existing configuration keeps everything else, and gets its own value back")]
    public void KeepsExisting()
    {
        var id = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Library);
        File.WriteAllText(Path.Combine(Library, "_meta.json"), $$"""{"appliedId":"{{id}}","entries":[{"id":"{{id}}","name":"Mine"}]}""");
        File.WriteAllText(Path.Combine(Library, id + ".json"), """{"disableAutoUpdates":false,"other":1}""");

        ManualUpdates.SetEnabled(true);
        Assert.True(Json(id + ".json")["disableAutoUpdates"]!.GetValue<bool>());
        ManualUpdates.SetEnabled(false);

        var config = Json(id + ".json");
        Assert.False(config["disableAutoUpdates"]!.GetValue<bool>());
        Assert.Equal(1, config["other"]!.GetValue<int>());
        Assert.Equal(id, AppliedId);
    }

    [Fact(DisplayName = "a value somebody changed after the write is theirs, and is not put back")]
    public void LaterEditKept()
    {
        ManualUpdates.SetEnabled(true);
        var file = Path.Combine(Library, AppliedId + ".json");
        File.WriteAllText(file, """{"disableAutoUpdates":false,"theirs":true}""");
        ManualUpdates.SetEnabled(false);
        Assert.True(File.Exists(file));
        Assert.False(Json(Path.GetFileName(file))["disableAutoUpdates"]!.GetValue<bool>());
    }

    [Fact(DisplayName = "an administrator's policy is refused rather than promised over")]
    public void ManagedRefused()
    {
        ManualUpdates.Managed = () => true;
        Assert.Throws<ManualUpdates.Refusal>(() => ManualUpdates.SetEnabled(true));
        Assert.False(Directory.Exists(_folder));
    }

    [Fact(DisplayName = "a configuration that cannot be read is kept, not written over")]
    public void UnreadableKept()
    {
        Directory.CreateDirectory(Library);
        File.WriteAllText(Path.Combine(Library, "_meta.json"), "{ not json");
        Assert.Throws<ManualUpdates.Refusal>(() => ManualUpdates.SetEnabled(true));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(Library, "_meta.json")));
    }

    [Fact(DisplayName = "a configuration Claude's settings rewrote is protected again at the next start")]
    public void Resynchronized()
    {
        ManualUpdates.SetEnabled(true);
        var file = Path.Combine(Library, AppliedId + ".json");
        File.WriteAllText(file, "{}");
        ManualUpdates.Synchronize();
        Assert.True(Json(Path.GetFileName(file))["disableAutoUpdates"]!.GetValue<bool>());
    }
}
