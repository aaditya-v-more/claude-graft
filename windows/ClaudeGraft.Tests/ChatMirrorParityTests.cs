using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ChatMirrorParityTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly Func<IReadOnlyList<(int pid, string command)>> _processes = ClaudeProcesses.Enumerate;
    private const string Name = "local_chat.json";
    private const string Shared = """{"sessionId":"local_chat","cliSessionId":"original","title":"Plan the trip","titleSource":"auto","isArchived":false,"lastActivityAt":1000,"lastFocusedAt":1000,"lastAssistantUuid":"reply-0"}""";

    public ChatMirrorParityTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        ClaudeProcesses.Enumerate = () => Array.Empty<(int, string)>();
    }

    public void Dispose()
    {
        ClaudeProcesses.Enumerate = _processes;
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    private static string Profile(string name, string account, string org)
    {
        var profile = GraftPaths.Profile(name);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "config.json"), new JsonObject { ["lastKnownAccountUuid"] = account }.ToJsonString());
        foreach (var store in GraftPaths.ChatStoreNames) Directory.CreateDirectory(Path.Combine(profile, store, account, org));
        return profile;
    }

    private static string Org(string profile, string account = "ACCOUNT", string org = "ORG") =>
        Path.Combine(profile, "claude-code-sessions", account, org);
    private static JsonObject Read(string folder, string name = Name) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, name)))!.AsObject();
    private static void Write(string folder, string data, string name = Name) => File.WriteAllText(Path.Combine(folder, name), data);
    private static void Edit(string folder, Action<JsonObject> change, string name = Name)
    { var record = Read(folder, name); change(record); Write(folder, record.ToJsonString(), name); }
    private static List<string> Chats(string folder) => Directory.GetFiles(folder, "local_*.json").Select(Path.GetFileName).Order().ToList()!;

    private static (string one, string other) Pair()
    {
        var one = Org(Profile("Claude-Borrower", "ACCOUNT", "ORG"));
        var other = Org(Profile("Claude", "ACCOUNT", "ORG"));
        Write(one, Shared); Write(other, Shared);
        Graft.MirrorChatFolders(one, other);
        return (one, other);
    }

    [Theory(DisplayName = "an archive or unarchive survives the other profile merely showing the chat")]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchiveAndGlance(bool originallyArchived)
    {
        var (one, other) = Pair();
        Edit(one, r => r["isArchived"] = originallyArchived);
        Graft.MirrorChatFolders(one, other);
        Edit(other, r => r["isArchived"] = !originallyArchived);
        Edit(one, r => r["lastFocusedAt"] = 2000);
        Graft.MirrorChatFolders(one, other);
        foreach (var folder in new[] { one, other })
        { Assert.Equal(!originallyArchived, (bool)Read(folder)["isArchived"]!); Assert.Equal(2000, (int)Read(folder)["lastFocusedAt"]!); }
        Assert.Equal(0, Graft.MirrorChatFolders(one, other));
    }

    [Theory(DisplayName = "resuming a chat decides its unarchived state even when the flag did not change")]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumedArchiveConflict(bool resumeBorrower)
    {
        var (one, other) = Pair();
        Edit(resumeBorrower ? one : other, r => r["resumedAt"] = 3000);
        Edit(resumeBorrower ? other : one, r => r["isArchived"] = true);
        Graft.MirrorChatFolders(one, other);
        Assert.False((bool)Read(one)["isArchived"]!);
        Assert.False((bool)Read(other)["isArchived"]!);
        Assert.Single(Chats(one));
    }

    [Fact(DisplayName = "field removals persist, contested times take the later value and titles use activity")]
    public void FieldConflicts()
    {
        var (one, other) = Pair();
        Edit(one, r => { r["keptDirtyWorktree"] = true; r["nullable"] = "old"; });
        Graft.MirrorChatFolders(one, other);
        Edit(one, r => { r.Remove("keptDirtyWorktree"); r["nullable"] = null; r["lastFocusedAt"] = 5000; r["title"] = "borrower"; });
        Edit(other, r => { r["lastFocusedAt"] = 4000; r["title"] = "source"; });
        Graft.MirrorChatFolders(one, other);
        Assert.False(Read(other).ContainsKey("keptDirtyWorktree"));
        Assert.True(Read(other).ContainsKey("nullable"));
        Assert.Null(Read(other)["nullable"]);
        Assert.Equal(5000, (int)Read(other)["lastFocusedAt"]!);
        Assert.Equal("borrower", (string?)Read(other)["title"]);
        Edit(one, r => r["title"] = "borrower again");
        Edit(other, r => { r["title"] = "source again"; r["lastActivityAt"] = 9000; });
        Graft.MirrorChatFolders(one, other);
        Assert.Equal("source again", (string?)Read(one)["title"]);
    }

    [Fact(DisplayName = "a stale whole record cannot undo the archive of a newer conversation")]
    public void OldMemoryDoesNotMerge()
    {
        var (one, other) = Pair();
        Edit(other, r => { r["isArchived"] = true; r["lastActivityAt"] = 20000; });
        Graft.MirrorChatFolders(one, other);
        Edit(one, r => { r["isArchived"] = false; r["lastActivityAt"] = 9000; r["lastFocusedAt"] = 30000; });
        Edit(other, r => r["lastFocusedAt"] = 25000);
        Graft.MirrorChatFolders(one, other);
        Assert.True((bool)Read(one)["isArchived"]!);
        Assert.Equal(25000, (int)Read(one)["lastFocusedAt"]!);
        Assert.Equal(20000, (int)Read(one)["lastActivityAt"]!);
    }

    [Theory(DisplayName = "a torn record is repaired from the readable half instead of destroying it")]
    [InlineData(false)]
    [InlineData(true)]
    public void TornRecordCannotReplaceReadable(bool bothChanged)
    {
        var (one, other) = Pair();
        Write(one, "{");
        if (bothChanged) Edit(other, r => r["isArchived"] = true);
        Graft.MirrorChatFolders(one, other);
        Assert.Equal(File.ReadAllText(Path.Combine(other, Name)), File.ReadAllText(Path.Combine(one, Name)));
        Assert.Equal(bothChanged, (bool)Read(one)["isArchived"]!);
    }

    [Fact(DisplayName = "old pairs learn field baselines and stopping a mirror retires them")]
    public void LearnsAndRetiresFields()
    {
        var (one, other) = Pair();
        var key = Graft.PairKey(one, other);
        var fields = Graft.MirrorFieldsFile(key);
        File.Delete(fields);
        Assert.Equal(0, Graft.MirrorChatFolders(one, other));
        Assert.True(File.Exists(fields));
        var state = Graft.LoadMirrorState(); state.Pairs.Remove(key); Graft.SaveMirrorState(state);
        Assert.False(File.Exists(fields));
    }

    [Fact(DisplayName = "an older launcher's unmatched field baseline is not used to combine edits")]
    public void RejectsStaleFieldEvidence()
    {
        var (one, other) = Pair();
        var state = Graft.LoadMirrorState();
        state.Pairs[Graft.PairKey(one, other)][Name] = "written-by-an-older-launcher";
        Graft.SaveMirrorState(state);
        Edit(one, r => r["lastFocusedAt"] = 2000);
        Edit(other, r => r["isArchived"] = true);
        Graft.MirrorChatFolders(one, other);
        // Without matching field evidence, the original whole-record tie rule
        // remains in force rather than inventing a three-way merge base.
        Assert.False((bool)Read(other)["isArchived"]!);
    }

    [Fact(DisplayName = "a refused record replacement keeps its baseline until the write can land")]
    public void FailedWriteKeepsBaseline()
    {
        var (one, other) = Pair();
        var key = Graft.PairKey(one, other);
        var before = Graft.LoadMirrorState().Pairs[key][Name];
        Edit(other, r => r["isArchived"] = true);
        using (var locked = new FileStream(Path.Combine(one, Name), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(0, Graft.MirrorChatFolders(one, other));
            Assert.Equal(before, Graft.LoadMirrorState().Pairs[key][Name]);
        }
        Graft.MirrorChatFolders(one, other);
        Assert.True((bool)Read(one)["isArchived"]!);
        Assert.Equal(0, Graft.MirrorChatFolders(one, other));
    }

    [Fact(DisplayName = "one pass brings a late borrower's archive to every profile in the group")]
    public void ConvergesAcrossBorrowers()
    {
        var (one, other) = Pair();
        var late = Org(Profile("Claude-Z", "ACCOUNT", "ORG"));
        Graft.MirrorChatFolders(late, other);
        Edit(late, r => r["isArchived"] = true);
        Edit(one, r => r["lastFocusedAt"] = 2000);
        Graft.MirrorKnownPairs();
        foreach (var folder in new[] { one, other, late })
        { Assert.True((bool)Read(folder)["isArchived"]!); Assert.Equal(2000, (int)Read(folder)["lastFocusedAt"]!); }
        Assert.Equal(0, Graft.MirrorKnownPairs());
    }

    [Fact(DisplayName = "different replies wait for both profiles to close, then remain as two titled chats")]
    public void DivergedConversationsArePreserved()
    {
        var (one, other) = Pair();
        Edit(one, r => { r["lastActivityAt"] = 3000; r["lastAssistantUuid"] = "reply-own"; r["cliSessionId"] = "cli-own"; });
        Edit(other, r => { r["lastActivityAt"] = 2000; r["lastAssistantUuid"] = "reply-source"; r["cliSessionId"] = "cli-source"; });
        ClaudeProcesses.Enumerate = () => [(10, "\"C:\\AnthropicClaude\\app-1\\claude.exe\" --user-data-dir=\"" + GraftPaths.Profile("Claude-Borrower") + "\"")];
        Assert.Equal(0, Graft.MirrorChatFolders(one, other));
        Assert.Single(Chats(one)); Assert.Single(Chats(other));
        Assert.Equal("cli-own", (string?)Read(one)["cliSessionId"]);
        Assert.Equal("cli-source", (string?)Read(other)["cliSessionId"]);
        ClaudeProcesses.Enumerate = () => [];
        Graft.MirrorChatFolders(one, other);
        var names = Chats(one); Assert.Equal(2, names.Count); Assert.Equal(names, Chats(other));
        var split = names.Single(n => n != Name);
        foreach (var folder in new[] { one, other })
        {
            Assert.Equal("cli-source", (string?)Read(folder)["cliSessionId"]);
            Assert.Equal("Plan the trip (Claude)", (string?)Read(folder)["title"]);
            Assert.Equal("cli-own", (string?)Read(folder, split)["cliSessionId"]);
            Assert.Equal(split[..^5], (string?)Read(folder, split)["sessionId"]);
            Assert.Equal("Plan the trip (Claude-Borrower)", (string?)Read(folder, split)["title"]);
            Assert.Equal("user", (string?)Read(folder, split)["titleSource"]);
        }
        Assert.Equal(0, Graft.MirrorChatFolders(one, other));
        File.Delete(Path.Combine(one, split));
        Graft.MirrorChatFolders(one, other);
        Assert.Single(Chats(other));
    }

    [Fact(DisplayName = "a scratch chat keeps a local sidebar origin while resuming the same working files")]
    public void ScratchAliasesStayLocalAndHandBack()
    {
        var source = Profile("Claude", "SRC", "SRCORG");
        var borrower = Profile("Claude-Work", "OWN", "OWNORG");
        var one = Org(borrower, "OWN", "OWNORG"); var other = Org(source, "SRC", "SRCORG");
        Write(one, "{}", "local_mine.json");
        var suffix = Path.Combine("SRC", "SRCORG", "scratch-2026-10-03-abcdef");
        var cwd = Path.Combine(source, "scratch-workspaces", suffix);
        Directory.CreateDirectory(cwd); File.WriteAllText(Path.Combine(cwd, "work.txt"), "working files");
        var record = JsonNode.Parse(Shared)!.AsObject(); record["cwd"] = cwd; record["originCwd"] = cwd;
        Write(other, record.ToJsonString());
        Graft.GraftInto(source, borrower);
        var alias = Path.Combine(borrower, "scratch-workspaces", suffix);
        Assert.Equal(alias, (string?)Read(one)["originCwd"]);
        Assert.Equal(cwd, (string?)Read(one)["cwd"]);
        Assert.True(Junction.IsLink(alias));
        Assert.Equal("working files", File.ReadAllText(Path.Combine(alias, "work.txt")));
        Assert.Equal(0, Graft.MirrorChatFolders(one, other));
        Edit(one, r => { r["title"] = "Renamed"; r["lastFocusedAt"] = 2000; });
        Edit(other, r => r["isArchived"] = true);
        Graft.MirrorChatFolders(one, other);
        Assert.Equal(cwd, (string?)Read(other)["originCwd"]);
        Assert.Equal(alias, (string?)Read(one)["originCwd"]);
        Assert.True((bool)Read(one)["isArchived"]!);
        Assert.Equal("Renamed", (string?)Read(other)["title"]);
        Assert.Equal(0, Graft.MirrorChatFolders(one, other));
        Graft.Ungraft(borrower);
        Assert.Equal(new[] { "local_mine.json" }, Chats(one));
        Assert.True(File.Exists(Path.Combine(other, Name)));
        Assert.Equal("working files", File.ReadAllText(Path.Combine(cwd, "work.txt")));
    }

    [Theory(DisplayName = "a mismatched scratch origin or a remote session remains unchanged")]
    [InlineData("mismatch")]
    [InlineData("wslConfig")]
    [InlineData("sshConfig")]
    public void DoesNotRewriteUnrelatedOrigins(string reason)
    {
        var (one, other) = Pair();
        var cwd = Path.Combine(GraftPaths.DefaultProfile, "scratch-workspaces", "ACCOUNT", "ORG", "scratch-2026-10-03-abcdef");
        Directory.CreateDirectory(cwd);
        var record = Read(other); record["cwd"] = cwd;
        record["originCwd"] = reason == "mismatch" ? cwd.Replace("abcdef", "fedcba") : cwd;
        if (reason != "mismatch") record[reason] = new JsonObject();
        var text = record.ToJsonString(); Write(other, text);
        Graft.MirrorChatFolders(one, other);
        Assert.Equal(text, File.ReadAllText(Path.Combine(one, Name)));
    }

    [Fact(DisplayName = "changing a lender's active organization hands the old history back before pairing again")]
    public void MovingSourceDoesNotMixOrganizations()
    {
        var source = Profile("Claude", "SRC", "FIRST");
        var borrower = Profile("Claude-Work", "OWN", "ORG");
        var old = Org(source, "SRC", "FIRST"); var one = Org(borrower, "OWN", "ORG");
        Write(old, "{}", "local_first.json"); Write(one, "{}", "local_mine.json");
        Graft.GraftInto(source, borrower);
        Write(one, "{}", "local_during.json");
        var next = Org(source, "SRC", "SECOND"); Directory.CreateDirectory(next);
        Write(next, "{}", "local_second.json");
        Directory.SetLastWriteTimeUtc(next, DateTime.UtcNow.AddMinutes(5));
        Graft.GraftInto(source, borrower);
        Assert.Equal(new[] { "local_mine.json", "local_second.json" }, Chats(one));
        Assert.Equal(new[] { "local_mine.json", "local_second.json" }, Chats(next));
        Assert.Equal(new[] { "local_during.json", "local_first.json", "local_mine.json" }, Chats(old));
        Graft.Ungraft(borrower);
        Assert.Equal(new[] { "local_mine.json" }, Chats(one));
    }
}
