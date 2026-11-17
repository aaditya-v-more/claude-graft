using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class MirrorMergeTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly string _root;
    private readonly string _own, _src;
    private const string Name = "local_chat.json";

    public MirrorMergeTests()
    {
        _root = _t.Dir("root");
        GraftPaths.ProfilesRootOverride = _root;
        Graft.RunningClaudesOverride = () => new List<string>();
        _own = Org("Claude-2");
        _src = Org("Claude");
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        Graft.RunningClaudesOverride = null;
        _t.Dispose();
    }

    private string Org(string profile)
    {
        var dir = Path.Combine(_root, profile, "claude-code-sessions", "acct", "org");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string folder, JsonObject record, string name = Name) =>
        File.WriteAllText(Path.Combine(folder, name), record.ToJsonString());

    private static JsonObject Read(string folder, string name = Name) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(folder, name)))!;

    private static List<string> Chats(string folder) =>
        Directory.EnumerateFiles(folder, "local_*.json").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList()!;

    private static JsonObject Chat(bool archived = false, int focused = 1000, int activity = 1000, string reply = "reply-0", string title = "Plan the trip") =>
        new()
        {
            ["sessionId"] = "local_chat", ["title"] = title, ["isArchived"] = archived,
            ["lastFocusedAt"] = focused, ["lastActivityAt"] = activity, ["lastAssistantUuid"] = reply,
        };

    // Both sides agree once, so the next pass has a baseline to read their changes against.
    private void Agree(JsonObject record)
    {
        Write(_own, record);
        Write(_src, record);
        Graft.MirrorChatFolders(_own, _src);
    }

    [Fact(DisplayName = "an archive on one side survives a mere look at the chat on the other")]
    public void ArchiveSurvivesALook()
    {
        Agree(Chat());
        Write(_src, Chat(archived: true));
        Write(_own, Chat(focused: 5000));
        Graft.MirrorChatFolders(_own, _src);
        foreach (var side in new[] { _own, _src })
        {
            Assert.True(Read(side)["isArchived"]!.GetValue<bool>());
            Assert.Equal(5000, Read(side)["lastFocusedAt"]!.GetValue<int>());
        }
    }

    [Fact(DisplayName = "a chat archived on one side and brought back on the other comes out unarchived")]
    public void UnarchiveWinsAContest()
    {
        Agree(Chat(archived: true));
        var brought = Chat(archived: false);
        brought["resumedAt"] = 3000;
        Write(_own, brought);
        var looked = Chat(archived: true, focused: 4000);
        Write(_src, looked);
        Graft.MirrorChatFolders(_own, _src);
        Assert.False(Read(_src)["isArchived"]!.GetValue<bool>());
        Assert.False(Read(_own)["isArchived"]!.GetValue<bool>());
    }

    [Fact(DisplayName = "a chat both Claudes carried on becomes two, each titled with the Claude that carried it")]
    public void CarriedOnBothSidesSplits()
    {
        Agree(Chat());
        Write(_own, Chat(activity: 2000, reply: "reply-own"));
        Write(_src, Chat(activity: 2500, reply: "reply-src"));
        Graft.MirrorChatFolders(_own, _src);
        Assert.Equal(2, Chats(_own).Count);
        Assert.Equal(Chats(_own), Chats(_src));
        var other = Chats(_own).Single(n => n != Name);
        Assert.Equal("Plan the trip (Claude)", Read(_src)["title"]!.GetValue<string>());
        Assert.Equal("Plan the trip (Claude-2)", Read(_src, other)["title"]!.GetValue<string>());
        Assert.Equal("reply-own", Read(_src, other)["lastAssistantUuid"]!.GetValue<string>());
        Assert.Equal(0, Graft.MirrorChatFolders(_own, _src));
    }

    [Fact(DisplayName = "a chat carried on in both is left whole while either Claude is open")]
    public void SplitWaitsForClosedClaudes()
    {
        Agree(Chat());
        Graft.RunningClaudesOverride = () => new List<string> { Path.Combine(_root, "Claude") };
        Write(_own, Chat(activity: 2000, reply: "reply-own"));
        Write(_src, Chat(activity: 2500, reply: "reply-src"));
        Graft.MirrorChatFolders(_own, _src);
        Assert.Equal(new[] { Name }, Chats(_own));
        Assert.Equal(new[] { Name }, Chats(_src));
    }

    [Fact(DisplayName = "a record nobody can read never replaces one that reads")]
    public void ReadableBeatsTorn()
    {
        Agree(Chat());
        File.WriteAllText(Path.Combine(_own, Name), "");
        Graft.MirrorChatFolders(_own, _src);
        Assert.Equal("Plan the trip", Read(_src)["title"]!.GetValue<string>());
        Assert.Equal("Plan the trip", Read(_own)["title"]!.GetValue<string>());
    }

    [Fact(DisplayName = "an unfiled chat is filed under each profile's own scratch folder, and the alias is no edit")]
    public void UnfiledChatGetsALocalOrigin()
    {
        var cwd = Path.Combine(_root, "Claude", "scratch-workspaces", "a", "b", "scratch-2026-11-11-c418e8");
        Directory.CreateDirectory(cwd);
        var record = Chat();
        record["cwd"] = cwd;
        record["originCwd"] = cwd;
        Write(_src, record);
        Graft.MirrorChatFolders(_own, _src);
        var alias = Path.Combine(_root, "Claude-2", "scratch-workspaces", "a", "b", "scratch-2026-11-11-c418e8");
        Assert.Equal(alias, Read(_own)["originCwd"]!.GetValue<string>());
        Assert.Equal(cwd, Read(_own)["cwd"]!.GetValue<string>());
        Assert.True(Junction.IsLink(alias));
        Assert.Equal(cwd, Read(_src)["originCwd"]!.GetValue<string>());
        Assert.Equal(0, Graft.MirrorChatFolders(_own, _src));
    }

    [Fact(DisplayName = "a time both sides changed takes the later of the two")]
    public void ContestedTimeTakesTheLater()
    {
        var @base = Graft.FieldDigests(Chat());
        var merged = Graft.MergeRecordFields(Chat(focused: 7000), Chat(focused: 6000), @base, MirrorSide.Other);
        Assert.Equal(7000, merged.Record["lastFocusedAt"]!.GetValue<int>());
        Assert.Equal(new[] { "lastFocusedAt" }, merged.Contested);
    }

    [Fact(DisplayName = "a split chat keeps one mark however often it is split")]
    public void SplitTitleMarksOnce()
    {
        Assert.Equal("Plan (Claude)", Graft.SplitTitle(JsonValue.Create("Plan (Claude)"), "Claude"));
        Assert.Equal("Chat (Claude)", Graft.SplitTitle(null, "Claude"));
    }
}

public class ProjectOrderTests
{
    private static string Stored(double time, params string[] order) =>
        new JsonObject { ["value"] = new JsonArray(order.Select(o => (JsonNode?)o).ToArray()), ["tabId"] = "x", ["timestamp"] = time }.ToJsonString();

    private static List<string>? Order(string? text) =>
        text is null ? null : (JsonNode.Parse(text)!["value"] as JsonArray)!.Select(n => n!.GetValue<string>()).ToList();

    [Fact(DisplayName = "the newest order is copied, keeping its time and what only the other account lists")]
    public void NewestWins()
    {
        var plan = SidebarSync.PlanProjectOrders(new[]
        {
            new SidebarSync.ProjectInput("/A", Stored(200, "/b", "/a"), null),
            new SidebarSync.ProjectInput("/B", Stored(100, "/a", "/only-b"), null),
        });
        Assert.False(plan.ContainsKey("/A"));
        Assert.Equal(new[] { "/b", "/a", "/only-b" }, Order(plan["/B"]));
        Assert.Equal(200, JsonNode.Parse(plan["/B"])!["timestamp"]!.GetValue<long>());
    }

    [Fact(DisplayName = "orders saved at the same moment do not take turns overwriting each other")]
    public void TieIsLeftAlone() =>
        Assert.Empty(SidebarSync.PlanProjectOrders(new[]
        {
            new SidebarSync.ProjectInput("/A", Stored(100, "/a", "/b"), null),
            new SidebarSync.ProjectInput("/B", Stored(100, "/b", "/a"), null),
        }));

    [Fact(DisplayName = "a saved environment is matched by name and target, and dropped where none matches")]
    public void PresetsTranslate()
    {
        JsonObject Preset(string id, string name, string cwd) =>
            new() { ["id"] = id, ["name"] = name, ["target"] = new JsonObject { ["cwd"] = cwd } };
        var order = SidebarSync.TranslateProjectOrder(new[] { "preset::a-cv", "/x", "preset::a-gone", "/x" },
            new[] { Preset("a-cv", "CV", "/cv"), Preset("a-gone", "Gone", "/gone") },
            new[] { Preset("b-cv", "CV", "/cv") });
        Assert.Equal(new[] { "preset::b-cv", "/x" }, order);
    }

    [Fact(DisplayName = "a time too large for an integer is kept rather than stopping the pass")]
    public void HugeTimeStays()
    {
        var plan = SidebarSync.PlanProjectOrders(new[]
        {
            new SidebarSync.ProjectInput("/A", Stored(1e20, "/a", "/b"), null),
            new SidebarSync.ProjectInput("/B", Stored(1, "/b"), null),
        });
        Assert.Equal(new[] { "/a", "/b" }, Order(plan["/B"]));
    }

    [Fact(DisplayName = "what Claude displays from its preferences stands in for the stored list")]
    public void PreferencesWin() =>
        Assert.Equal(new[] { "/shown" }, Order(SidebarSync.EffectiveProjectOrder(Stored(700, "/old"), new List<string> { "/shown" })));
}
