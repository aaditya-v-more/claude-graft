using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public sealed class ChimeHookTests : IDisposable
{
    private const string Launcher = @"C:\Users\x\AppData\Local\Programs\ClaudeGraft\GraftLaunch.exe";
    private readonly string _dir = Directory.CreateTempSubdirectory("chime-").FullName;
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, true);

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(File_))!;

    [Fact(DisplayName = "installing adds Graft's entry for a turn's end and a waiting permission, and keeps the rest")]
    public void InstallKeepsTheRest()
    {
        File.WriteAllText(File_, """{"model":"x","hooks":{"Stop":[{"hooks":[{"type":"command","command":"mine.cmd"}]}]}}""");
        Assert.True(ChimeHook.Install(File_, Launcher));
        var s = Read();
        Assert.Equal("x", (string?)s["model"]);
        Assert.Equal(2, s["hooks"]!["Stop"]!.AsArray().Count);
        Assert.Single(s["hooks"]!["Notification"]!.AsArray());
        Assert.True(ChimeHook.IsInstalled(File_, Launcher));
    }

    [Fact(DisplayName = "installing twice leaves one entry, and a moved launcher replaces the old one")]
    public void InstallReplaces()
    {
        ChimeHook.Install(File_, Launcher);
        ChimeHook.Install(File_, Launcher);
        ChimeHook.Install(File_, @"D:\elsewhere\GraftLaunch.exe");
        Assert.Single(Read()["hooks"]!["Stop"]!.AsArray());
        Assert.False(ChimeHook.IsInstalled(File_, Launcher));
    }

    [Fact(DisplayName = "removing takes out Graft's entry alone and puts the file back as it was")]
    public void RemoveRestores()
    {
        const string original = """{"model":"x","hooks":{"Stop":[{"hooks":[{"type":"command","command":"mine.cmd"}]}]}}""";
        File.WriteAllText(File_, original);
        ChimeHook.Install(File_, Launcher);
        Assert.True(ChimeHook.Remove(File_));
        Assert.Equal(JsonNode.Parse(original)!.ToJsonString(), Read().ToJsonString());
    }

    [Fact(DisplayName = "a file with no entry of Graft's is not rewritten when removing")]
    public void RemoveLeavesUntouched()
    {
        const string original = "{ \"model\" :   \"x\" }";
        File.WriteAllText(File_, original);
        Assert.True(ChimeHook.Remove(File_));
        Assert.Equal(original, File.ReadAllText(File_));
    }

    [Fact(DisplayName = "an unreadable settings file is never written over")]
    public void UnreadableIsLeft()
    {
        const string broken = "{ \"model\": ";
        File.WriteAllText(File_, broken);
        Assert.False(ChimeHook.Install(File_, Launcher));
        Assert.Equal(broken, File.ReadAllText(File_));
    }

    [Fact(DisplayName = "only a command naming the launcher and its flag is Graft's")]
    public void KnowsItsOwn()
    {
        Assert.True(ChimeHook.IsOurs(ChimeHook.Command(Launcher)));
        Assert.False(ChimeHook.IsOurs("node notify.js --signal"));
        Assert.False(ChimeHook.IsOurs($"\"{Launcher}\" --stop-sharing"));
    }
}

public class HookSignalTests
{
    [Fact(DisplayName = "a turn's end and a waiting permission are signals, an idle reminder is not")]
    public void ReadsTheEvents()
    {
        Assert.Equal(HookSignal.Kind.TurnEnded, HookSignal.From("""{"hook_event_name":"Stop","session_id":"s1"}""", 1)!.What);
        var asks = HookSignal.From("""{"hook_event_name":"Notification","notification_type":"permission_prompt","session_id":"s1"}""", 1);
        Assert.Equal((HookSignal.Kind.AwaitingPermission, "s1"), (asks!.What, asks.Session));
        Assert.Null(HookSignal.From("""{"hook_event_name":"Notification","notification_type":"idle_prompt","session_id":"s1"}""", 1));
        Assert.Null(HookSignal.From("""{"hook_event_name":"Stop"}""", 1));
        Assert.Null(HookSignal.From("not json", 1));
    }
}
