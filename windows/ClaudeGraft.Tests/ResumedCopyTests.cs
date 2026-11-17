using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public sealed class ResumedCopyTests
{
    private const string Opening = "Please look into why the co-op lobby stalls on the second map";

    // A transcript as the command line writes it: the bridge line naming the
    // owner, then each prompt and the answer to it, answers keyed by line id.
    private static (SessionFacts facts, ConversationTrace trace) Session(
        string id, double lastActivity, params (string prompt, string answerId, string answer, bool edit)[] turns)
    {
        var lines = new List<string>
        {
            "{\"type\":\"bridge-session\",\"bridgeSessionId\":\"cse_" + id + "\",\"ownerAccountUuid\":\"acct\",\"ownerOrganizationUuid\":\"org\",\"timestamp\":\"2026-11-01T10:00:00.000Z\"}",
        };
        var n = 0;
        foreach (var (prompt, answerId, answer, edit) in turns)
        {
            n++;
            lines.Add("{\"type\":\"user\",\"promptId\":\"p" + n + id + "\",\"cwd\":\"C:\\work\",\"timestamp\":\"2026-11-01T10:0" + n + ":00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"" + prompt + "\"},\"uuid\":\"u" + n + id + "\"}");
            var tool = edit ? "{\"type\":\"tool_use\",\"name\":\"Edit\",\"id\":\"t" + n + "\"}," : "";
            lines.Add("{\"message\":{\"content\":[" + tool + "{\"type\":\"text\",\"text\":\"" + answer + "\"}],\"type\":\"message\"},\"timestamp\":\"2026-11-01T10:0" + n + ":30.000Z\",\"uuid\":\"" + answerId + "\",\"type\":\"assistant\"}");
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        var (facts, trace) = TranscriptParser.Read(bytes, id);
        return (facts! with { LastActivityAt = lastActivity }, trace);
    }

    [Fact(DisplayName = "a resumed copy that asked nothing new and did nothing of its own is held back")]
    public void ResumedCopyHeldBack()
    {
        var original = Session("a", 1, (Opening, "x1", "Looking at the lobby code now.", false));
        var carriedOn = Session("b", 2, (Opening, "x1", "Looking at the lobby code now.", false),
                                        ("And the third map?", "x2", "Same cause there.", false));
        Assert.Equal(new[] { "a" }, Graft.ResumedCopies(new[] { original, carriedOn }));
    }

    [Fact(DisplayName = "a retry whose answer differs only by a few words is still a copy")]
    public void ShortOwnAnswerIsACopy()
    {
        var retry = Session("a", 1, (Opening, "y1", "Let me look.", false));
        var kept = Session("b", 2, (Opening, "x1", "Found it: the handshake times out.", false));
        Assert.Equal(new[] { "a" }, Graft.ResumedCopies(new[] { retry, kept }));
    }

    [Fact(DisplayName = "a session that edited a file of its own is never a copy")]
    public void EditIsNeverACopy()
    {
        var edited = Session("a", 1, (Opening, "y1", "Fixed.", true));
        var other = Session("b", 2, (Opening, "x1", "Found it.", false));
        Assert.DoesNotContain("a", Graft.ResumedCopies(new[] { edited, other }));
    }

    [Fact(DisplayName = "chats that open with a greeting are not grouped by it")]
    public void GreetingsAreNotCopies()
    {
        var one = Session("a", 1, ("hi", "y1", "Hello!", false));
        var two = Session("b", 2, ("hi", "x1", "Hello!", false), ("Second question", "x2", "Answer.", false));
        Assert.Empty(Graft.ResumedCopies(new[] { one, two }));
    }

    [Fact(DisplayName = "neither half of a known fork is a copy of the other")]
    public void ForksAreKept()
    {
        var original = Session("a", 1, (Opening, "x1", "Looking at the lobby code now.", false));
        var fork = Session("b", 2, (Opening, "x1", "Looking at the lobby code now.", false),
                                   ("A side question", "x2", "Answer.", false));
        Assert.Empty(Graft.ResumedCopies(new[] { original, fork }, new Dictionary<string, string> { ["b"] = "a" }));
    }

    [Fact(DisplayName = "a turn stopped by the person is not a prompt anybody typed")]
    public void InterruptionIsNotAPrompt()
    {
        var stopped = Session("a", 1, (Opening, "x1", "Looking.", false),
                                      ("[Request interrupted by user]", "y2", "", false));
        Assert.Single(stopped.trace.Prompts);
    }
}
