using System.Text;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class SharedChatsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static UsageSharing Sharing(Dictionary<string, string>? assignments = null) => new()
    {
        Lenders = new() { ["two"] = ["main"] },
        Assignments = (assignments ?? new()).ToDictionary(a => a.Key, a => new UsageSharing.Assignment(a.Value, Now)),
    };

    private static PoolSession S(string id, string from, string? servedBy, int minutesAgo, params PoolPass[] passed) =>
        new() { Id = id, From = from, ServedBy = servedBy, LastAt = Now.AddMinutes(-minutesAgo), PassedOver = passed };

    private static RelayStatus Status(params PoolSession[] sessions) => new() { Sessions = sessions };

    [Fact(DisplayName = "only conversations from a Claude that draws on another account are listed, the one on screen first")]
    public void Lists()
    {
        var chats = SharedChats.Build(Status(S("a", "two", "two", 1), S("b", "two", "main", 5), S("c", "main", "main", 0)),
            Sharing(), _ => null, "two", "b");
        Assert.Equal(["b", "a"], chats.Select(c => c.Id));
        Assert.Equal(["two", "main"], chats[0].Choices);
    }

    [Fact(DisplayName = "an archived conversation leaves the list, and titles come from Claude's own records")]
    public void Records()
    {
        var chats = SharedChats.Build(Status(S("a", "two", "two", 1), S("b", "two", "two", 2)), Sharing(),
            id => id == "a" ? new SharedChats.Record("Fix it", false) : new SharedChats.Record("Old", true), "two", null);
        Assert.Equal("Fix it", Assert.Single(chats).Title);
    }

    [Fact(DisplayName = "a conversation moved by hand names where it was put, until another answered for want of room")]
    public void Answering()
    {
        var moved = SharedChats.Build(Status(S("a", "two", null, 1)), Sharing(new() { ["a"] = "main" }), _ => null, "two", null)[0];
        Assert.Equal("main", SharedChats.AnsweringKey(moved));
        // Main is known to be full, so it is never asked and named in no list.
        var full = SharedChats.Build(Status(S("a", "two", "two", -1)), Sharing(new() { ["a"] = "main" }), _ => null, "two", null)[0];
        Assert.True(full.PassedOver);
        Assert.Equal("two", SharedChats.AnsweringKey(full));
        var before = SharedChats.Build(Status(S("a", "two", "two", 1)), Sharing(new() { ["a"] = "main" }), _ => null, "two", null)[0];
        Assert.False(before.PassedOver);
    }

    [Fact(DisplayName = "choosing Automatic again forgets the choice, and choices a day old are forgotten")]
    public void Assigning()
    {
        var sharing = SharedChats.Assign(Sharing(), "a", "main", Now);
        Assert.Equal("main", sharing.Assignments["a"].To);
        Assert.False(SharedChats.Assign(sharing, "a", null, Now).Assignments.ContainsKey("a"));
        var later = SharedChats.Assign(sharing, "b", "two", Now.AddDays(2));
        Assert.False(later.Assignments.ContainsKey("a"));
    }

    [Fact(DisplayName = "a Claude is never its own lender, and an empty list leaves no entry behind")]
    public void Lenders()
    {
        var s = SharedChats.SetLender(new UsageSharing(), "two", "two", true);
        Assert.Empty(s.Lenders);
        s = SharedChats.SetLender(s, "two", "main", true);
        Assert.Equal(["main"], s.Lenders["two"]);
        Assert.Empty(SharedChats.SetLender(s, "two", "main", false).Lenders);
    }

    [Fact(DisplayName = "a card names what it draws on, and says first whatever is wrong")]
    public void Wording()
    {
        Assert.Equal("Shared usage: off", SharingText.Label([], k => k));
        Assert.Equal("Draws on Claude", SharingText.Label(["main"], _ => "Claude"));
        Assert.Equal(("The relay could not be started.", true), SharingText.Note(["main"], new ClaudeCodeEnvironment.State.Waiting(), "The relay could not be started."));
        Assert.Equal(("Starts the next time this Claude opens.", false), SharingText.Note(["main"], new ClaudeCodeEnvironment.State.Waiting(), null));
        Assert.Equal(("Not shared: this Claude already sets HTTPS_PROXY", true), SharingText.Note(["main"], new ClaudeCodeEnvironment.State.Conflict("HTTPS_PROXY", false), null));
        Assert.Null(SharingText.Note([], new ClaudeCodeEnvironment.State.On(), null));
    }

    [Fact(DisplayName = "a lender says when it is full, when its login lapsed, and when it is the same account")]
    public void Conditions()
    {
        var result = new SharedUsage.Result(
            new RelayStatus { Members = [new PoolMemberStatus { Id = "main", Limits = new PoolLimits { Week = 1, WeekReset = Now.AddDays(1), Observed = Now } }] },
            new Dictionary<string, ClaudeCodeEnvironment.State>(),
            new Dictionary<string, SharedUsage.Lending> { ["main"] = SharedUsage.Lending.Ready, ["x"] = SharedUsage.Lending.Expired }, null);
        Assert.Equal("full", SharingText.Condition("main", result, false, Now));
        Assert.StartsWith("login expired", SharingText.Condition("x", result, false, Now));
        Assert.StartsWith("same account", SharingText.Condition("main", result, true, Now));
    }

    [Fact(DisplayName = "the tray says when another account answers the conversation on screen")]
    public void Tooltip() =>
        Assert.Equal("Claude 2\nFix it · answered by Claude", FrontLabel.Tooltip("Claude 2", null, "Fix it", "Claude"));

    [Fact(DisplayName = "a record's Claude Code session is read off it, which is how the relay knows it")]
    public void CliSession() =>
        Assert.Equal("cli-1", ChatFocus.Parse(Encoding.UTF8.GetBytes("""{"sessionId":"local_1","cliSessionId":"cli-1"}"""), DateTime.UtcNow)!.Cli);
}
