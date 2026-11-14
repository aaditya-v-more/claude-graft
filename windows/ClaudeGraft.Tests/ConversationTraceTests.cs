using System.Text;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class ConversationTraceTests
{
    [Fact]
    public void ReadsHumanPromptsAndWorkWithoutCachingTheirContents()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            {"type":"user","promptId":"p1","message":{"role":"user","content":"  Review  the PR\n"}}
            {"type":"user","promptId":"p2","message":{"content":[{"tool_use_id":"t1","type":"tool_result","content":"42 files"}]}}
            {"type":"user","promptId":"p3","message":{"content":"command output"},"isMeta":true}
            {"type":"user","promptId":"p4","message":{"content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}
            {"type":"user","promptId":"p5","message":{"content":[{"type":"image","source":{"type":"base64"}},{"type":"text","text":"What is this?"}]}}
            {"type":"user","message":{"content":"internal line without a prompt ID"}}
            {"message":{"content":[{"type":"text","text":"Hello"},{"type":"text","text":"world"}]},"uuid":"line-1","type":"assistant"}
            {"message":{"content":[{"type":"tool_use","name":"Edit","input":{"uuid":"nested"}}]},"uuid":"line-2","type":"assistant"}
            {"message":{"content":[{"type":"text","text":"overloaded"}]},"uuid":"line-3","type":"assistant","isApiErrorMessage":true}
            """);
        var trace = TranscriptParser.Read(bytes, "chat").Trace;
        Assert.Equal(new[] { Hash("Review the PR"), Hash("What is this?") }, trace.Prompts);
        Assert.Equal("Review the PR".Length, trace.OpeningSize);
        Assert.Equal(2, trace.Work.Count);
        Assert.Equal(10, trace.Work[Hash("line-1")]);
        Assert.Equal(ConversationTrace.EditWeight, trace.Work[Hash("line-2")]);
        Assert.NotEqual(Hash("""path\\nchild"""), Hash("path child"));
    }

    private static string Hash(string words) =>
        ConversationTrace.Fingerprint(Encoding.UTF8.GetBytes(words))!.Value.Hash;

    private static (SessionFacts, ConversationTrace) Session(string id, double last, string[] prompts,
        Dictionary<string, long>? work = null, int openingSize = 40, string cwd = "C:\\work",
        string owner = "account", string org = "org") => (new SessionFacts
    {
        CliSessionId = id, OwnerAccount = owner, OwnerOrganization = org, Cwd = cwd, LastActivityAt = last,
        CreatedAt = 0, BridgeIds = ["cse_x"], Title = "Same title", Model = "m", Effort = "e",
        PermissionMode = "auto", Prompts = prompts.Length, Branches = [],
    }, new ConversationTrace { Prompts = [.. prompts], OpeningSize = openingSize, Work = work ?? [] });

    [Fact]
    public void OnlyHoldsBackRetriesThatHaveNoSubstantialWorkOfTheirOwn()
    {
        var copies = ConversationTrace.ResumedCopies([
            Session("kept", 5, ["a", "b", "c"], new() { ["h1"] = 4000, ["h2"] = 9000, ["k1"] = 6000 }),
            Session("retried", 9, ["a", "b"], new() { ["h1"] = 4000, ["h2"] = 9000, ["r1"] = 80 }),
            Session("drafted", 9, ["a", "b"], new() { ["h1"] = 4000, ["h2"] = 9000, ["d1"] = 9097 }),
            Session("implemented", 9, ["a"], new() { ["i1"] = ConversationTrace.EditWeight }),
            Session("branch", 3, ["a", "d"]), Session("different-opening", 1, ["b"]),
            Session("older", 1, ["x"]), Session("newer", 2, ["x"]),
            Session("different-folder", 1, ["a"], cwd: "C:\\other"),
            Session("different-account", 1, ["a"], owner: "other"), Session("different-org", 1, ["a"], org: "other"),
            Session("greeting", 1, ["hi"], openingSize: 2), Session("another-greeting", 2, ["hi", "sync"], openingSize: 2)
        ], new Dictionary<string, string>());
        Assert.True(copies.SetEquals(["retried", "older"]));
    }

    [Fact]
    public void SameTimestampBreaksTiesInTheSameDirectionAcrossPasses()
    {
        var a = Session("a", 1, ["same"]);
        var b = Session("b", 1, ["same"]);
        var noForks = new Dictionary<string, string>();
        Assert.Equal("a", Assert.Single(ConversationTrace.ResumedCopies([a, b], noForks)));
        Assert.Equal("a", Assert.Single(ConversationTrace.ResumedCopies([b, a], noForks)));
    }

    [Fact]
    public void ExplicitForkProtectsBothOriginalAndForkButNotTheirRetries()
    {
        var original = Session("original", 5, ["a", "b"], new() { ["h1"] = 4000, ["h2"] = 9000 });
        var fork = Session("fork", 9, ["a", "b", "f"], new() { ["h1"] = 4000, ["h2"] = 9000, ["f1"] = 3000 });
        var retry = Session("retried", 1, ["a"], new() { ["h1"] = 4000 });
        Assert.Contains("original", ConversationTrace.ResumedCopies([original, fork, retry], new Dictionary<string, string>()));
        var forks = new Dictionary<string, string> { ["fork"] = "original" };
        Assert.Equal("retried", Assert.Single(ConversationTrace.ResumedCopies([original, fork, retry], forks)));
        var freshFork = Session("fork", 9, ["a", "b"], new() { ["h1"] = 4000, ["h2"] = 9000 });
        Assert.Empty(ConversationTrace.ResumedCopies([original, freshFork], forks));
    }
}
