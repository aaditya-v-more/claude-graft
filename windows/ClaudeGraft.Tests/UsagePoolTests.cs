using System.Text;
using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class UsagePoolTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static PoolLimits Limits(double five = 0, double week = 0, double resetHours = 24) => new(five, Now.AddHours(1), week, Now.AddHours(resetHours), null, Now);
    private static PoolMember Member(string id, string? account = null, string? token = null, double resetHours = 24, params string[] lenders) =>
        new(id, id, account, [UsagePool.Digest(id + "-sender")], token, Limits(resetHours: resetHours), lenders);

    [Theory]
    [InlineData(.979, .989, true)] [InlineData(.98, .1, false)] [InlineData(.1, .99, false)]
    public void Ceilings(double five, double week, bool expected) => Assert.Equal(expected, PoolPolicy.HasRoom(Limits(five, week), Now));
    [Fact] public void ElapsedWindowsRegainRoomAndRankingRollsTheWeek()
    {
        var old = new PoolLimits(1, Now.AddMinutes(-1), 1, Now.AddDays(-1), Now.AddSeconds(-1), Now.AddDays(-8));
        Assert.True(PoolPolicy.HasRoom(old, Now)); Assert.Equal(Now.AddDays(6), PoolPolicy.NextWeekReset(old.WeekReset, Now));
        Assert.Equal(new[] { "early", "own", "unknown" }, PoolPolicy.Order("own",
            [new("unknown", null, true), new("own", Limits(resetHours: 2), true), new("early", Limits(resetHours: 1), true)], null, Now));
    }
    [Fact] public void SenderWinsTiesPreferencesNeedRoomAndSenderAlwaysRemains()
    {
        Assert.Equal(new[] { "own", "other" }, PoolPolicy.Order("own", [new("other", null, true), new("own", null, true)], null, Now));
        Assert.Equal(new[] { "other", "own" }, PoolPolicy.Order("own", [new("other", Limits(), true), new("full", Limits(1), true)], ["full"], Now));
    }
    [Theory]
    [InlineData(401, true, PoolPolicy.Result.LoginRefused)] [InlineData(401, false, PoolPolicy.Result.Deliver)]
    [InlineData(400, true, PoolPolicy.Result.RefusedForAccount)] [InlineData(403, true, PoolPolicy.Result.RefusedForAccount)]
    [InlineData(404, true, PoolPolicy.Result.RefusedForAccount)] [InlineData(500, true, PoolPolicy.Result.Deliver)]
    public void RefusalsAreScoped(int status, bool borrowed, PoolPolicy.Result expected) => Assert.Equal(expected, PoolPolicy.Evaluate(status, [], borrowed, Now).Result);
    [Fact] public void RetryAfterAndServiceResetAreHonored()
    {
        Assert.Equal(Now.AddSeconds(123), PoolPolicy.Evaluate(429, [new("Retry-After", "123")], false, Now).Until);
        Assert.Equal(Now.AddMinutes(4), PoolPolicy.Evaluate(429, [new("anthropic-ratelimit-unified-status", "rejected"), new("anthropic-ratelimit-unified-reset", Now.AddMinutes(4).ToUnixTimeSeconds().ToString())], true, Now).Until);
    }
    [Theory]
    [InlineData("POST", "/v1/messages?beta=true", true)] [InlineData("GET", "/v1/messages", false)]
    [InlineData("POST", "/v1/messages/count_tokens", false)] [InlineData("POST", "/api/artifacts", false)]
    public void OnlyInferenceMoves(string method, string target, bool expected) => Assert.Equal(expected, PoolRequest.IsInference(method, target));
    [Theory]
    [InlineData("{\"file_id\":\"file_1\"}", true)] [InlineData("{\"container\":{\"id\":\"a\"}}", true)]
    [InlineData("{\"container\":{\"type\":\"object\"}}", false)] [InlineData("{\"mcp_servers\":[{}]}", true)]
    [InlineData("{\"text\":\"say file_id and container\"}", false)]
    public void OnlyActualAccountReferencesPin(string body, bool pinned) => Assert.Equal(pinned, PoolRequest.Inspect(Encoding.UTF8.GetBytes(body), null).Pinned);
    [Fact] public void ReasoningAndThreadsRetainTheirAccount()
    {
        var reasoning = Encoding.UTF8.GetBytes("{\"model\":\"claude-sonnet\",\"messages\":[{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\"}]}]}");
        Assert.True(PoolRequest.Inspect(reasoning, null).Bound);
        Assert.False(PoolRequest.Inspect(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(reasoning).Replace("claude-sonnet", "claude-opus-5-5")), null).Bound);
        var shape = PoolRequest.Inspect("{\"thread\":{\"type\":\"continue\"},\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"cache_control\":{\"ttl\":\"1h\"}}"u8.ToArray(), null);
        Assert.True(shape.Bound); Assert.True(shape.Continuing); Assert.True(shape.Prompted); Assert.True(shape.Starts); Assert.Equal(PoolPolicy.LongCache, shape.Cache);
        Assert.True(PoolRequest.Inspect(reasoning, "gzip").Pinned);
    }
    [Fact] public void PlanDeduplicatesAccountAndReturnsSenderAfterItsEarlierAttempt()
    {
        var state = new PoolState();
        state.Configure(new(true, [Member("own", "self", "own-token", 1, "duplicate", "closed", "saved"), Member("duplicate", "self", "duplicate-token"), Member("closed", "extra", null, 2), Member("saved", "extra", "saved-token", 2)]), Now);
        var plan = state.Plan("own", null, false, false, Now);
        Assert.Equal(new[] { "own", "saved", "own" }, plan.Select(a => a.Member)); Assert.Null(plan[0].Token); Assert.Equal("saved-token", plan[1].Token); Assert.Null(plan[^1].Token);
    }
    [Fact] public void RefusedTokenStaysOutUntilTokenActuallyChanges()
    {
        var state = new PoolState();
        PoolConfiguration Config(string token) => new(true, [Member("own", token: "own-token", resetHours: 10, lenders: ["lent"]), Member("lent", token: token, resetHours: 1)]);
        state.Configure(Config("old"), Now); state.Apply(new(PoolPolicy.Result.LoginRefused), "lent", Now); state.Configure(Config("old"), Now.AddMinutes(1));
        Assert.Single(state.Plan("own", null, false, false, Now));
        state.Configure(Config("new"), Now.AddMinutes(2)); Assert.Equal("lent", state.Plan("own", null, false, false, Now)[0].Member);
    }
    [Fact] public void BoundConversationOutlivesCacheButExplicitChoiceWinsAndAuxiliaryDoesNotMoveIt()
    {
        var state = new PoolState(); var config = new PoolConfiguration(true, [Member("own", token: "own", resetHours: 1, lenders: ["lent"]), Member("lent", token: "lent", resetHours: 2)]);
        state.Configure(config, Now);
        var threaded = new PoolRequest.Shape(false, true, false, true, true, true, "model", PoolPolicy.ShortCache);
        state.Served("lent", "own", "session", 200, [], threaded, Now);
        state.Served("own", "own", "session", 200, [], threaded with { Threaded = false }, Now.AddSeconds(1));
        Assert.Equal("lent", state.Plan("own", "session", false, true, Now.AddHours(1))[0].Member);
        Assert.Equal("own", state.Plan("own", "session", false, false, Now.AddMinutes(30))[0].Member);
        state.Configure(config with { Assignments = new() { ["session"] = "own" } }, Now);
        Assert.Equal("own", state.Plan("own", "session", false, true, Now)[0].Member);
        var status = state.Status("hash", 1, Now, Now);
        Assert.Equal("lent", Assert.Single(status.Sessions).ServedBy);
    }
    [Fact] public void StatusNeverContainsLentSecrets()
    {
        var state = new PoolState(); state.Configure(new(true, [Member("own", token: "never-output-secret")]), Now);
        Assert.DoesNotContain("never-output-secret", JsonSerializer.Serialize(state.Status("binary", 1, Now, Now), UsagePool.Json));
        Assert.Equal("own", state.Member("own-sender", Now));
    }
    [Theory]
    [InlineData("older", false, false, PoolDelivery.Step.Keep)]
    [InlineData("older", false, true, PoolDelivery.Step.Replace)]
    [InlineData("bundled", false, false, PoolDelivery.Step.Keep)]
    [InlineData(null, true, false, PoolDelivery.Step.Keep)]
    [InlineData(null, true, true, PoolDelivery.Step.Replace)]
    [InlineData(null, false, false, PoolDelivery.Step.Start)]
    public void AReplacementNeedsCompleteCredentialsButAnAbsentRelayStillStarts(string? binary, bool unreadable, bool complete, PoolDelivery.Step expected)
    {
        var status = binary is null ? null : new PoolStatus(binary, 1, Now, true, true, [], [], [], 0);
        Assert.Equal(expected, PoolDelivery.Decide(status, unreadable, "bundled", complete));
    }
    [Fact] public void IncompleteManagerRefreshPreservesExistingLendingWhileExplicitDisableStillWorks()
    {
        var state = new PoolState(); state.Configure(new(true, [Member("own", token: "own", lenders: ["lent"]), Member("lent", token: "secret", resetHours: 1)]), Now);
        var incomplete = new PoolConfiguration(true, [Member("own"), Member("lent")]);
        var command = PoolDelivery.Command(incomplete, false);
        Assert.Null(command.Configure);
        if (command.Configure is { } config) state.Configure(config, Now);
        Assert.Equal("secret", state.Plan("own", null, false, false, Now)[0].Token);
        var stopped = PoolDelivery.Command(new(false, []), false);
        Assert.NotNull(stopped.Configure); state.Configure(stopped.Configure!, Now);
        Assert.Null(Assert.Single(state.Plan("own", null, false, false, Now)).Token);
    }
}
