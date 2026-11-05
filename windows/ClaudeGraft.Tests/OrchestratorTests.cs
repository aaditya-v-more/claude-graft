using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// The record sweep end to end, against a throwaway profiles root and projects
/// directory — the C# echo of the Swift suite's "Session records" section. The
/// static overrides and caches make these share process state, so they live in
/// one class (xUnit runs a class's methods in sequence) and each resets first.
/// </summary>
[Collection("GlobalState")]
public sealed class OrchestratorTests : IDisposable
{
    private readonly TempDir _t = new();
    private const string Account = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string Org = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    public OrchestratorTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        GraftPaths.ClaudeProjectsOverride = _t.Dir("projects");
        Graft.ResetCachesForTests();
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        GraftPaths.ClaudeProjectsOverride = null;
        _t.Dispose();
    }

    // Nothing is running in a test, so the quiet window is skipped and a settled
    // transcript files at once.
    private static readonly Func<string, bool> NothingRunning = _ => false;

    private string MakeProfile(string name, string account = Account, string org = Org)
    {
        var profile = Path.Combine(GraftPaths.ProfilesRoot, name);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "config.json"),
            $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
        foreach (var store in GraftPaths.ChatStoreNames)
            Directory.CreateDirectory(Path.Combine(profile, store, account, org));
        return profile;
    }

    private void MakeTranscript(string session, string account = Account, string org = Org,
        string? title = null, bool spoke = true, string last = "2026-08-29T17:00:10.000Z",
        string? said = null, string[]? extra = null)
    {
        var dir = Path.Combine(GraftPaths.ClaudeProjects, "proj");
        Directory.CreateDirectory(dir);
        var lines = new List<string>
        {
            $"{{\"type\":\"bridge-session\",\"sessionId\":\"{session}\",\"bridgeSessionId\":\"cse_01\"," +
            $"\"ownerAccountUuid\":\"{account}\",\"ownerOrganizationUuid\":\"{org}\"}}",
        };
        if (spoke)
        {
            lines.Add(JsonSerializer.Serialize(new { promptId = "p1", cwd = "/w", permissionMode = "auto",
                gitBranch = "main", type = "user", message = new { role = "user", content = said ?? "hello from " + session },
                timestamp = "2026-08-29T17:00:00.000Z" }));
            lines.Add("{\"cwd\":\"/w\",\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-5\"}," +
                      $"\"timestamp\":\"{last}\",\"effort\":\"max\"}}");
        }
        else
        {
            lines.Add($"{{\"cwd\":\"/w\",\"type\":\"file-history-summary\",\"timestamp\":\"{last}\"}}");
        }
        if (title is not null)
            lines.Add($"{{\"type\":\"custom-title\",\"customTitle\":\"{title}\"}}");
        if (extra is not null) lines.AddRange(extra);
        File.WriteAllText(Path.Combine(dir, session + ".jsonl"), string.Join("\n", lines) + "\n");
    }

    private static string RecordPath(string profile, string session, string account = Account, string org = Org) =>
        Path.Combine(profile, "claude-code-sessions", account, org, $"local_{session}.json");

    private static string FollowUp(string words) => JsonSerializer.Serialize(new { type = "user", promptId = "p2",
        cwd = "/w", message = new { role = "user", content = words }, timestamp = "2026-08-29T18:00:00.000Z" });

    [Fact]
    public void FilesRealBranchesAndUniqueWorkButDoesNotFileCopiesOrRemoveAnArchivedCopy()
    {
        const string opening = "Two accounts, one history?";
        MakeTranscript("longer", said: opening, extra: [FollowUp("Then test it")]);
        MakeTranscript("retried", said: opening, last: "2026-08-29T19:00:00.000Z");
        MakeTranscript("archived", said: opening);
        MakeTranscript("branch", said: opening, extra: [FollowUp("Give me commands to delete them")]);
        MakeTranscript("draft", said: opening, extra: [
            JsonSerializer.Serialize(new { type = "assistant", uuid = "a-draft",
                message = new { content = new[] { new { type = "text", text = new string('d', 650) } } } })]);
        MakeTranscript("edit", said: opening, extra: [
            """{"type":"assistant","uuid":"a-edit","message":{"content":[{"type":"tool_use","name":"Edit"}]}}"""]);
        MakeTranscript("hi", said: "hi");
        MakeTranscript("other-hi", said: "hi", extra: [FollowUp("Now sync my profiles please")]);

        // A previous release cached the same files before recording prompt fingerprints.
        Assert.Empty(Graft.FileMissingSessionRecords([], NothingRunning));
        var cacheFile = Path.Combine(GraftPaths.OwnData, "transcript-cache.json");
        var cache = JsonNode.Parse(File.ReadAllText(cacheFile))!.AsObject();
        foreach (var entry in cache) entry.Value!.AsObject().Remove("trace");
        File.WriteAllText(cacheFile, cache.ToJsonString());
        Graft.ResetCachesForTests();

        var profile = MakeProfile("Claude");
        var archived = RecordPath(profile, "archived");
        const string archivedBytes = """{"cliSessionId":"archived","isArchived":true}""";
        File.WriteAllText(archived, archivedBytes);
        var filed = Graft.FileMissingSessionRecords([profile], NothingRunning).Select(f => f.CliSessionId).ToHashSet();
        Assert.True(filed.SetEquals(["longer", "branch", "draft", "edit", "hi", "other-hi"]));
        Assert.False(File.Exists(RecordPath(profile, "retried")));
        Assert.Equal(archivedBytes, File.ReadAllText(archived));
        Assert.DoesNotContain(opening, File.ReadAllText(cacheFile));
        Graft.ResetCachesForTests();
        Assert.Empty(Graft.FileMissingSessionRecords([profile], NothingRunning));
    }

    [Fact]
    public void RemembersExplicitForkWhenBothRecordsDisappearAndRebuildsBoth()
    {
        const string opening = "Plan the move to the new computer";
        MakeTranscript("original", said: opening);
        MakeTranscript("fork", said: opening, extra: [FollowUp("And the routines on it?")]);
        var first = MakeProfile("Claude-Old");
        File.WriteAllText(RecordPath(first, "desktop-original"), """{"cliSessionId":"original"}""");
        File.WriteAllText(RecordPath(first, "desktop-fork"),
            """{"cliSessionId":"fork","forkedFromSessionId":"local_desktop-original"}""");

        var stateFile = Path.Combine(GraftPaths.OwnData, "session-records.json");
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(stateFile, """{"withdrawn":["previously-deleted"]}""");
        Assert.Empty(Graft.FileMissingSessionRecords([first], NothingRunning));
        var state = SessionRecordState.Load(stateFile);
        Assert.Contains("previously-deleted", state.Withdrawn);
        Assert.Equal("original", state.Forks["fork"]);

        Directory.Move(first, Path.Combine(_t.Path, "removed-profile"));
        var next = MakeProfile("Claude-New");
        Graft.ResetCachesForTests();
        var refiled = Graft.FileMissingSessionRecords([next], NothingRunning);
        Assert.True(refiled.Select(f => f.CliSessionId).ToHashSet().SetEquals(["original", "fork"]));
        Assert.True(File.Exists(RecordPath(next, "original")));
        Assert.True(File.Exists(RecordPath(next, "fork")));
    }

    [Fact(DisplayName = "a transcript cannot create a recovered record outside its profile")]
    public void MalformedOrganizationIsSkipped()
    {
        var profile = MakeProfile("Claude");
        MakeTranscript("sess1", org: "../../../../outside");
        Assert.Empty(Graft.FileMissingSessionRecords([profile], NothingRunning));
        Assert.False(Directory.Exists(Path.Combine(_t.Path, "outside")));
    }

    [Fact(DisplayName = "a transcript with no record is filed into the profile holding its account")]
    public void FilesMissing()
    {
        var profile = MakeProfile("Claude");
        MakeTranscript("sess1", title: "Recovered");

        var filed = Graft.FileMissingSessionRecords(new[] { profile }, NothingRunning);

        Assert.Single(filed);
        Assert.Equal("sess1", filed[0].CliSessionId);
        Assert.True(File.Exists(RecordPath(profile, "sess1")));
        Assert.Contains("\"title\":\"Recovered\"", File.ReadAllText(RecordPath(profile, "sess1")));
        // The recovered record carries the model and effort the transcript held,
        // not the defaults — the bug the parser test also guards, seen end to end.
        Assert.Contains("\"model\":\"claude-opus-5\"", File.ReadAllText(RecordPath(profile, "sess1")));
    }

    [Fact(DisplayName = "an open-and-close with nothing said is never filed")]
    public void SkipsEmpty()
    {
        var profile = MakeProfile("Claude");
        MakeTranscript("empty", spoke: false);
        Assert.Empty(Graft.FileMissingSessionRecords(new[] { profile }, NothingRunning));
        Assert.False(File.Exists(RecordPath(profile, "empty")));
    }

    [Fact(DisplayName = "a session a deletion marker names is withdrawn, not filed")]
    public void WithdrawnByMarker()
    {
        var profile = MakeProfile("Claude");
        MakeTranscript("gone", title: "Deleted");
        // The marker Claude leaves is named for the session.
        File.WriteAllText(Path.Combine(profile, "claude-code-sessions", Account, Org, "deleted_gone"), "0");

        Assert.Empty(Graft.FileMissingSessionRecords(new[] { profile }, NothingRunning));
        Assert.False(File.Exists(RecordPath(profile, "gone")));
    }

    [Fact(DisplayName = "a record already on disk is not filed a second time")]
    public void NoDoubleFile()
    {
        var profile = MakeProfile("Claude");
        MakeTranscript("has", title: "Already");
        File.WriteAllText(RecordPath(profile, "has"), "{\"cliSessionId\":\"has\"}");

        Assert.Empty(Graft.FileMissingSessionRecords(new[] { profile }, NothingRunning));
        // The pre-existing record is untouched, not overwritten.
        Assert.Equal("{\"cliSessionId\":\"has\"}", File.ReadAllText(RecordPath(profile, "has")));
    }

    [Fact(DisplayName = "a warm transcript is held back while the owner's Claude is running")]
    public void HeldWhileRunning()
    {
        var profile = MakeProfile("Claude");
        // last activity now, so it falls inside the quiet window.
        MakeTranscript("warm", last: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

        var filed = Graft.FileMissingSessionRecords(new[] { profile }, isRunning: _ => true);
        Assert.Empty(filed);
        Assert.False(File.Exists(RecordPath(profile, "warm")));
    }

    [Fact(DisplayName = "a sweep does not rebuild an organization folder inside a store this app emptied")]
    public void RefusesStashedStore()
    {
        var profile = MakeProfile("Claude");
        MakeTranscript("borrowed", title: "Borrowed");

        // Exactly what a same-account first pass leaves: the store moved aside,
        // an empty one built where it was.
        var store = Path.Combine(profile, "claude-code-sessions");
        Directory.Move(store, Path.Combine(profile, ".claude-code-sessions.graft-own"));
        Directory.CreateDirectory(store);

        Graft.FileMissingSessionRecords(new[] { profile }, NothingRunning);

        Assert.False(Directory.Exists(Path.Combine(store, Account, Org)));
        // and the real history stays where the graft put it, in the stash.
        Assert.True(Directory.Exists(
            Path.Combine(profile, ".claude-code-sessions.graft-own", Account, Org)));
    }
}
