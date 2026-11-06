using System.Text;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class TranscriptTests
{
    // A transcript is one JSON object per line. These build the few line shapes
    // the parser cares about, by hand, so a test reads as the thing it asserts.
    private static byte[] Lines(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");

    private static string Bridge(string bridgeId, string account, string org, string stamp) =>
        $"{{\"type\":\"bridge-session\",\"bridgeSessionId\":\"{bridgeId}\"," +
        $"\"ownerAccountUuid\":\"{account}\",\"ownerOrganizationUuid\":\"{org}\",\"timestamp\":\"{stamp}\"}}";

    // A plain user line. Marker lines — bridge, title — carry no stamp into the
    // record, so a transcript needs at least one line like this to have a
    // created and last-activity time at all.
    private static string User(string stamp) =>
        $"{{\"type\":\"user\",\"timestamp\":\"{stamp}\"}}";

    // An assistant line, written the way Claude writes it: a nested message
    // carrying its own type first, and the line's own type last.
    private static string Assistant(string model, string effort, string stamp) =>
        $"{{\"parentUuid\":\"x\",\"message\":{{\"role\":\"assistant\",\"type\":\"message\"," +
        $"\"model\":\"{model}\",\"effort\":\"{effort}\"}},\"timestamp\":\"{stamp}\",\"type\":\"assistant\"}}";

    [Fact(DisplayName = "a transcript with no bridge line is not a desktop session")]
    public void NoBridge()
    {
        var bytes = Lines("{\"type\":\"user\",\"timestamp\":\"2026-08-29T17:00:00.000Z\"}");
        Assert.Null(TranscriptParser.SessionFacts(bytes, "sid"));
    }

    [Fact(DisplayName = "the owner account and organization come off the bridge line")]
    public void OwnerFromBridge()
    {
        var bytes = Lines(
            Bridge("cse_abc", "ACCT", "ORG", "2026-08-29T17:00:00.000Z"),
            User("2026-08-29T17:00:01.000Z"));
        var facts = TranscriptParser.SessionFacts(bytes, "sid");
        Assert.NotNull(facts);
        Assert.Equal("ACCT", facts!.OwnerAccount);
        Assert.Equal("ORG", facts.OwnerOrganization);
        Assert.Equal(new[] { "cse_abc" }, facts.BridgeIds);
    }

    [Fact(DisplayName = "the model and effort are read from the line's own type, not the nested message's")]
    public void ModelFromLineType()
    {
        // The bug this guards: taking the first `type` on the line reads the
        // nested message's, leaving every field on an assistant line unread and
        // the session back at the defaults.
        var bytes = Lines(
            Bridge("cse_abc", "ACCT", "ORG", "2026-08-29T17:00:00.000Z"),
            Assistant("claude-opus-4-8", "high", "2026-08-29T17:05:00.000Z"));
        var facts = TranscriptParser.SessionFacts(bytes, "sid")!;
        Assert.Equal("claude-opus-4-8", facts.Model);
        Assert.Equal("high", facts.Effort);
    }

    [Fact(DisplayName = "a session with no assistant answer keeps the default model and effort")]
    public void DefaultsWithoutAnswer()
    {
        var bytes = Lines(
            Bridge("cse_abc", "ACCT", "ORG", "2026-08-29T17:00:00.000Z"),
            User("2026-08-29T17:00:01.000Z"));
        var facts = TranscriptParser.SessionFacts(bytes, "sid")!;
        Assert.Equal("claude-sonnet-5", facts.Model);
        Assert.Equal("medium", facts.Effort);
    }

    [Fact(DisplayName = "the newest custom title wins, and an unnamed session gets a placeholder")]
    public void TitleLastWins()
    {
        var named = Lines(
            Bridge("cse_abc", "ACCT", "ORG", "2026-08-29T17:00:00.000Z"),
            User("2026-08-29T17:00:01.000Z"),
            "{\"type\":\"custom-title\",\"customTitle\":\"First\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"Second\"}");
        Assert.Equal("Second", TranscriptParser.SessionFacts(named, "sid")!.Title);

        var unnamed = Lines(
            Bridge("cse_abc", "ACCT", "ORG", "2026-08-29T17:00:00.000Z"),
            User("2026-08-29T17:00:01.000Z"));
        Assert.Equal("New session", TranscriptParser.SessionFacts(unnamed, "sid")!.Title);
    }

    [Fact(DisplayName = "first and last timestamps become the created and last-activity millis")]
    public void Timestamps()
    {
        // The bridge stamp does not count; the first stamp is the user line's.
        var bytes = Lines(
            Bridge("cse_abc", "ACCT", "ORG", "2026-08-29T16:59:59.000Z"),
            User("2026-08-29T17:00:00.000Z"),
            Assistant("m", "medium", "2026-08-29T17:00:01.000Z"));
        var facts = TranscriptParser.SessionFacts(bytes, "sid")!;
        Assert.Equal(1788022800000d, facts.CreatedAt);      // 2026-08-29T17:00:00Z
        Assert.Equal(1788022801000d, facts.LastActivityAt); // one second later
    }

    [Fact(DisplayName = "a quote pasted into a value does not turn the rest of the line into keys")]
    public void PastedQuote()
    {
        // The title carries an escaped quote and a colon; a scan that lost the
        // string boundary would read "gotcha" as another field.
        var bytes = Lines(
            Bridge("cse_abc", "ACCT", "ORG", "2026-08-29T17:00:00.000Z"),
            User("2026-08-29T17:00:01.000Z"),
            "{\"type\":\"custom-title\",\"customTitle\":\"a \\\"quote\\\": gotcha\"}");
        Assert.Equal("a \"quote\": gotcha", TranscriptParser.SessionFacts(bytes, "sid")!.Title);
    }

    [Fact(DisplayName = "distinct prompt ids are counted, repeats are not")]
    public void PromptCount()
    {
        var bytes = Lines(
            Bridge("cse_abc", "ACCT", "ORG", "2026-08-29T17:00:00.000Z"),
            "{\"type\":\"user\",\"promptId\":\"p1\",\"timestamp\":\"2026-08-29T17:00:01.000Z\"}",
            "{\"type\":\"user\",\"promptId\":\"p1\",\"timestamp\":\"2026-08-29T17:00:02.000Z\"}",
            "{\"type\":\"user\",\"promptId\":\"p2\",\"timestamp\":\"2026-08-29T17:00:03.000Z\"}");
        Assert.Equal(2, TranscriptParser.SessionFacts(bytes, "sid")!.Prompts);
    }
}

public class SessionRecordTests
{
    private static SessionFacts Facts(IReadOnlyList<string> bridges, IReadOnlyList<string>? branches = null) => new()
    {
        CliSessionId = "sid",
        BridgeIds = bridges,
        OwnerAccount = "ACCT",
        OwnerOrganization = "ORG",
        Title = "T",
        Cwd = "/w",
        CreatedAt = 1,
        LastActivityAt = 2,
        Model = "m",
        Effort = "medium",
        PermissionMode = "auto",
        Prompts = 3,
        Branches = branches ?? Array.Empty<string>(),
    };

    [Fact(DisplayName = "the record is named for its session and starts unarchived")]
    public void NameAndArchive()
    {
        var r = SessionRecord.For(Facts(new[] { "cse_abc" }));
        Assert.Equal("local_sid", (string?)r["sessionId"]);
        Assert.Equal("sid", (string?)r["cliSessionId"]);
        Assert.False((bool)r["isArchived"]!);
    }

    [Fact(DisplayName = "a cse_ bridge id is rewritten to the session_ the record uses")]
    public void BridgeRename()
    {
        var r = SessionRecord.For(Facts(new[] { "cse_abc", "already_session" }));
        var ids = r["bridgeSessionIds"]!.AsArray().Select(n => (string)n!).ToArray();
        Assert.Equal(new[] { "session_abc", "already_session" }, ids);
    }

    [Fact(DisplayName = "branches are written down only when there are some")]
    public void Branches()
    {
        Assert.Null(SessionRecord.For(Facts(new[] { "cse_abc" }))["writtenBranches"]);
        var withBranch = SessionRecord.For(Facts(new[] { "cse_abc" }, new[] { "main" }));
        Assert.Equal(new[] { "main" },
            withBranch["writtenBranches"]!.AsArray().Select(n => (string)n!).ToArray());
    }
}
