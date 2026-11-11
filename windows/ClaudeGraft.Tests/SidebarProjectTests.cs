using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public sealed partial class SidebarTests
{
    [Fact]
    public void ProjectOrdersTranslatePresetsKeepPrivateEntriesAndIgnoreTies()
    {
        JsonObject Preset(string id, string name, string cwd) => new() { ["id"] = id, ["name"] = name, ["target"] = new JsonObject { ["cwd"] = cwd } };
        var from = new[] { Preset("one", "App", @"C:\App"), Preset("missing", "Remote", "/unknown") };
        var to = new[] { Preset("two", "App", @"C:\App"), Preset("existing", "Private", @"C:\Private") };
        Assert.Equal(new[] { "preset::two", "/plain", "preset::existing" }, SidebarSync.TranslateProjectOrder(
            ["preset::one", "preset::missing", "/plain", "preset::existing", "/plain"], from, to));
        string Order(string[] value, int timestamp) => JsonSerializer.Serialize(new { value, timestamp });
        string Presets(JsonObject[] values) => JsonSerializer.Serialize(new { state = new { presets = values } });
        var plan = SidebarSync.PlanProjectOrders([
            new("a", Order(["preset::one", "/plain"], 500), Presets(from)),
            new("b", Order(["/private", "preset::existing"], 100), Presets(to)),
            new("tied", Order(["keep"], 500), "{\"state\":false}"), new("missing", null, null)]);
        Assert.Equal(new[] { "preset::two", "/plain", "/private", "preset::existing" }, JsonNode.Parse(plan["b"])!["value"]!.Deserialize<string[]>());
        Assert.False(plan.ContainsKey("tied"));
        Assert.Equal(new[] { "/plain" }, JsonNode.Parse(plan["missing"])!["value"]!.Deserialize<string[]>());
        Assert.Empty(SidebarSync.PlanProjectOrders([new("invalid", "no", "no")]));
        Assert.Equal(500, (int)JsonNode.Parse(SidebarSync.EffectiveProjectOrder(Order(["stale"], 500), ["displayed"])!)!["timestamp"]!);
    }

    [Fact]
    public void BackupPruningRetainsNewestTwentyOnly()
    {
        using var temp = new TempDir();
        for (var i = 0; i < 25; i++) { var file = Path.Combine(temp.Path, i + ".json"); File.WriteAllText(file, "{}"); File.SetLastWriteTimeUtc(file, DateTime.UnixEpoch.AddMinutes(i)); }
        File.WriteAllText(Path.Combine(temp.Path, "keep.txt"), "keep");
        SidebarBrowser.PruneBackups(temp.Path);
        Assert.Equal(20, Directory.GetFiles(temp.Path, "*.json").Length);
        Assert.False(File.Exists(Path.Combine(temp.Path, "0.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "24.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "keep.txt")));
    }
}
