using System.Text;
using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class PoolPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1), Day = TimeSpan.FromDays(1);
    private const string P = "anthropic-ratelimit-unified-";
    private static string Stamp(TimeSpan offset) => (Now + offset).ToUnixTimeSeconds().ToString();
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static PoolLimits L(double five = 0.1, TimeSpan? fiveReset = null, double week = 0.1, TimeSpan? weekReset = null, TimeSpan? refused = null) =>
        new() { FiveHour = five, FiveHourReset = Now + fiveReset, Week = week, WeekReset = Now + weekReset, RefusedUntil = Now + refused, Observed = Now };

    private static List<string> Order(IReadOnlyList<PoolPolicy.Account> accounts, params string[] prefer) =>
        PoolPolicy.Order("own", accounts, prefer, Now);

    private static readonly PoolPolicy.Account Soon = new("soon", L(weekReset: Day), true);
    private static readonly PoolPolicy.Account Later = new("later", L(weekReset: 5 * Day), true);
    private static readonly PoolPolicy.Account Own = new("own", L(weekReset: 3 * Day), true);
    private static readonly PoolPolicy.Account Spent = new("soon", L(five: 0.99, fiveReset: 2 * Hour, weekReset: Day), true);

    [Fact(DisplayName = "an answer's headers give the account's two windows as fractions, and when each resets")]
    public void ReadsLimits()
    {
        var read = PoolLimits.Parse([(P + "5h-utilization", "0.42"), (P + "5h-reset", Stamp(2 * Hour)),
            (P + "7d-utilization", "0.9"), (P + "7d-reset", Stamp(3 * Day)), (P + "status", "allowed")], Now)!;
        Assert.Equal((0.42, 0.9), (read.FiveHour, read.Week));
        Assert.Equal((Now + 2 * Hour, Now + 3 * Day), (read.FiveHourReset!.Value, read.WeekReset!.Value));
        Assert.Null(read.RefusedUntil);
    }

    [Fact(DisplayName = "a refusal that names no reset closes the account for a minute")]
    public void RejectedForAMinute() =>
        Assert.Equal(Now.AddSeconds(60), PoolLimits.Parse([(P + "status", "rejected")], Now)!.RefusedUntil);

    [Fact(DisplayName = "an answer that never reached the model teaches nothing, and names match whatever their case")]
    public void NothingOrAnyCase()
    {
        Assert.Null(PoolLimits.Parse([("content-type", "application/json")], Now));
        Assert.Equal(0.5, PoolLimits.Parse([("Anthropic-RateLimit-Unified-5h-Utilization", "0.5")], Now)!.FiveHour);
    }

    [Fact(DisplayName = "the usage endpoint's percentages become the same fractions")]
    public void Percentages()
    {
        var p = PoolLimits.FromPercent(34, null, 71, null, Now);
        Assert.Equal(0.34, p.FiveHour, 9);
        Assert.Equal(0.71, p.Week, 9);
    }

    [Fact(DisplayName = "the account whose week resets soonest is asked first, its own included")]
    public void SoonestFirst() => Assert.Equal(["soon", "own", "later"], Order([Later, Own, Soon]));

    [Fact(DisplayName = "an account with its five hours spent is passed over until they reset, then is back in front")]
    public void FiveHoursSpent()
    {
        Assert.Equal(["own", "later"], Order([Later, Own, Spent]));
        Assert.Equal("soon", Order([Later, Own, new("soon", L(five: 0.99, fiveReset: -Hour, weekReset: Day), true)])[0]);
    }

    [Fact(DisplayName = "a spent week, or a refusal until a time named, is passed over too")]
    public void WeekOrRefused()
    {
        Assert.DoesNotContain("soon", Order([Own, new("soon", L(week: 1, weekReset: Day), true)]));
        Assert.DoesNotContain("soon", Order([Own, new("soon", L(weekReset: Day, refused: TimeSpan.FromMinutes(30)), true)]));
    }

    [Fact(DisplayName = "an account nobody has heard from goes after every one with a known reset")]
    public void UnheardLast() => Assert.Equal(["later", "unheard", "own"], Order([new("unheard", null, true), Later]));

    [Fact(DisplayName = "with nothing known about anyone, a request stays with the account that sent it")]
    public void TieStaysHome() => Assert.Equal(["own", "other"], Order([new("other", null, true), new("own", null, true)]));

    [Fact(DisplayName = "an account whose login cannot be used lends nothing")]
    public void Unusable() => Assert.Equal(["own"], Order([Own, new("soon", L(weekReset: Day), false)]));

    [Fact(DisplayName = "a sender with no room of its own is still the last resort, so the service decides")]
    public void SenderLast() => Assert.Equal(["later", "own"], Order([new("own", L(five: 1, fiveReset: Hour, weekReset: 3 * Day), true), Later]));

    [Fact(DisplayName = "a conversation stays where its cache is, unless that account has no room, and a hand move outranks it")]
    public void Preferences()
    {
        Assert.Equal("later", Order([Later, Own, Soon], "later")[0]);
        Assert.Equal("own", Order([Own, Spent, Later], "soon")[0]);
        Assert.Equal(["own", "later", "soon"], Order([Later, Own, Soon], "own", "later"));
    }

    [Fact(DisplayName = "an hour of cache asked for holds a conversation an hour, otherwise five minutes")]
    public void CacheLifetime()
    {
        Assert.Equal(PoolPolicy.LongCache, PoolPolicy.CacheLifetime(B("""{"cache_control":{"type":"ephemeral","ttl":"1h"}}""")));
        Assert.Equal(PoolPolicy.ShortCache, PoolPolicy.CacheLifetime(B("""{"cache_control":{"type":"ephemeral"}}""")));
    }

    [Fact(DisplayName = "a weekly reset that has passed is followed by one a week later")]
    public void NextWeek() => Assert.Equal(Now + 6 * Day, PoolPolicy.NextWeekReset(Now - Day, Now));

    [Fact(DisplayName = "a refusal for lack of room moves on, for as long as the service asked or until the reset it named")]
    public void Exhausted()
    {
        Assert.Equal(new PoolPolicy.Outcome.Exhausted(Now.AddSeconds(30)), PoolPolicy.OutcomeOf(429, [("retry-after", "30")], true, Now));
        Assert.Equal(new PoolPolicy.Outcome.Exhausted(Now + Hour),
            PoolPolicy.OutcomeOf(429, [(P + "status", "rejected"), (P + "reset", Stamp(Hour))], false, Now));
    }

    [Fact(DisplayName = "a refused borrowed login is not lent again, and a sender's own refusal reaches it untouched")]
    public void Logins()
    {
        Assert.IsType<PoolPolicy.Outcome.LoginRefused>(PoolPolicy.OutcomeOf(401, [], true, Now));
        Assert.IsType<PoolPolicy.Outcome.Deliver>(PoolPolicy.OutcomeOf(401, [], false, Now));
    }

    [Theory(DisplayName = "what another account cannot serve goes home; on its own account it is delivered")]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    public void RefusedForAccount(int status)
    {
        Assert.IsType<PoolPolicy.Outcome.RefusedForAccount>(PoolPolicy.OutcomeOf(status, [], true, Now));
        Assert.IsType<PoolPolicy.Outcome.Deliver>(PoolPolicy.OutcomeOf(status, [], false, Now));
    }

    [Theory(DisplayName = "an answer, or a service down for everyone, is passed straight back")]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(529)]
    public void Delivered(int status) => Assert.IsType<PoolPolicy.Outcome.Deliver>(PoolPolicy.OutcomeOf(status, [], true, Now));

    [Fact(DisplayName = "only a POST to /v1/messages is the request worth moving")]
    public void Inference()
    {
        Assert.True(PoolRequest.IsInference("POST", "/v1/messages?beta=true"));
        Assert.False(PoolRequest.IsInference("POST", "/v1/messages/count_tokens?beta=true"));
        Assert.False(PoolRequest.IsInference("GET", "/v1/messages"));
        Assert.False(PoolRequest.IsInference("POST", "/api/oauth/profile"));
    }

    private static readonly byte[] Upload = B("""{"model":"claude-x","messages":[{"content":[{"type":"document","source":{"type":"file","file_id":"file_1"}}]}]}""");
    private static readonly byte[] Quoted = B("""{"messages":[{"content":"what is \"file_id\": for"}]}""");
    private static readonly string Reasoned = """{"model":"claude-sonnet-5-5","messages":[{"role":"assistant","content":[{"type":"thinking","thinking":"…","signature":"x"}]}]}""";

    [Fact(DisplayName = "a request naming an uploaded file stays home; the key quoted in a prompt pins nothing; a compressed body is not moved")]
    public void Pinned()
    {
        Assert.True(PoolRequest.MustStayWithSender(Upload, null));
        Assert.False(PoolRequest.MustStayWithSender(Quoted, null));
        Assert.True(PoolRequest.MustStayWithSender(Quoted, "gzip"));
        Assert.False(PoolRequest.MustStayWithSender(Quoted, "identity"));
    }

    [Fact(DisplayName = "reasoning carried back holds a conversation to its account, unless the model is known to travel")]
    public void Reasoning()
    {
        Assert.True(PoolRequest.CarriesBoundReasoning(B(Reasoned)));
        Assert.True(PoolRequest.CarriesBoundReasoning(B("""{"model":"claude-haiku-5-5","messages":[{"content":[{"type":"redacted_thinking","data":"x"}]}]}""")));
        Assert.False(PoolRequest.CarriesBoundReasoning(B("""{"model":"claude-sonnet-5-5","thinking":{"type":"adaptive"},"messages":[{"role":"user","content":"hi"}]}""")));
        Assert.False(PoolRequest.CarriesBoundReasoning(B("""{"model":"claude-sonnet-5-5","messages":[{"content":"what does \"type\":\"thinking\" mean"}]}""")));
        Assert.False(PoolRequest.CarriesBoundReasoning(B(Reasoned.Replace("claude-sonnet-5-5", "claude-opus-5-5[1m]"))));
        Assert.True(PoolRequest.CarriesBoundReasoning(B(Reasoned.Replace("claude-sonnet-5-5", "claude-next-1"))));
    }

    [Fact(DisplayName = "the model and bearer are read off a request, the bearer whatever the field's case")]
    public void ModelAndBearer()
    {
        Assert.Equal("claude-x", PoolRequest.Model(Upload));
        Assert.Equal("abc", PoolRequest.Bearer([("authorization", "Bearer abc ")]));
        Assert.Null(PoolRequest.Bearer([("Authorization", "Basic abc")]));
    }

    [Fact(DisplayName = "a turn typed on a thread is a person typing; a tool's answer, an aside or a lone message is not")]
    public void Turns()
    {
        var typed = """{"model":"m","messages":[{"role":"user","content":[{"type":"text","text":"Is 391 prime?"}]}],"thread":{"type":"create"}}""";
        var lone = """{"model":"m","messages":[{"role":"user","content":"Name this conversation: Is 391 prime?"}]}""";
        var toolAnswer = """{"model":"m","thread":{"id":"t"},"messages":[{"role":"user","content":"Is 391 prime?"},{"role":"assistant","content":[{"type":"tool_use","id":"u"}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"u","content":"17 x 23"}]}]}""";
        var nextTurn = """{"model":"m","thread":{"id":"t"},"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"u","content":"17 x 23"}]},{"role":"assistant","content":"No: 17 x 23."},{"role":"user","content":"And 397?"}]}""";
        var aside = """{"model":"m","messages":[{"role":"user","content":"Is 391 prime?"},{"role":"assistant","content":"No."},{"role":"user","content":"Name this conversation."}]}""";
        Assert.True(PoolRequest.StartsTurn(B(typed)) && PoolRequest.StartsTurn(B(nextTurn)));
        Assert.False(PoolRequest.StartsTurn(B(toolAnswer)));
        Assert.False(PoolRequest.StartsTurn(B(aside)));
        Assert.False(PoolRequest.StartsTurn(B(lone)));
    }

    [Fact(DisplayName = "an account given under two names counts once, under the first that can lend")]
    public void Candidates()
    {
        PoolMember M(string id, string? account, string? token) => new() { Id = id, Account = account, Token = token };
        var sender = new PoolMember { Id = "A", Account = "a", BorrowsFrom = ["B", "E", "S"] };
        var members = new[] { sender, M("B", "b", null), M("E", "b", "t"), M("S", "a", "t2") };
        Assert.Equal(["A", "E"], PoolState.Candidates(sender, members).Select(m => m.Id));
    }

    [Fact(DisplayName = "a pool configuration crosses the pipe and back unchanged")]
    public void RoundTrip()
    {
        var request = new RelayRequest
        {
            Configure = new PoolConfiguration
            {
                Enabled = true,
                Members = [new PoolMember { Id = "main", Name = "Claude", Identify = [PoolRequest.Digest("token")], Token = "token", Limits = PoolLimits.FromPercent(34, null, 71, null, Now) }],
                Assignments = new Dictionary<string, string> { ["s1"] = "main" },
            },
        };
        var back = JsonSerializer.Deserialize<RelayRequest>(JsonSerializer.Serialize(request, RelayWire.Json), RelayWire.Json)!;
        Assert.Equal(request.Configure.Members[0].Token, back.Configure!.Members[0].Token);
        Assert.Equal(request.Configure.Members[0].Limits, back.Configure.Members[0].Limits);
        Assert.Equal("main", back.Configure.Assignments!["s1"]);
    }
}

public class PoolHttpTests
{
    private const string Sent = "POST /v1/messages?beta=true HTTP/1.1\r\nHost: api.anthropic.com\r\nContent-Length: 12\r\nConnection: keep-alive, x-hop\r\nX-Hop: 1\r\nAuthorization: Bearer mine\r\nX-Claude-Code-Session-Id: s1\r\n\r\n";
    private static HttpHead H(string s) => HttpHead.Parse(Encoding.ASCII.GetBytes(s))!;

    [Fact(DisplayName = "a request's length is read from its head; two lengths that disagree are refused")]
    public void Lengths()
    {
        Assert.Equal(new HttpBody.Length(12), H(Sent).RequestBody);
        Assert.Null(H("POST / HTTP/1.1\r\nContent-Length: 3\r\nContent-Length: 4\r\n\r\n").RequestBody);
        Assert.IsType<HttpBody.Chunked>(H("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n").RequestBody);
    }

    [Fact(DisplayName = "a request for a socket is known only when the connection names the upgrade")]
    public void Upgrade()
    {
        Assert.True(H("GET /ws HTTP/1.1\r\nConnection: keep-alive, Upgrade\r\nUpgrade: websocket\r\n\r\n").AsksToUpgrade);
        Assert.False(H("GET /ws HTTP/1.1\r\nUpgrade: websocket\r\n\r\n").AsksToUpgrade);
    }

    [Fact(DisplayName = "a stream with no length runs until close; HEAD and 204 have no body whatever they say")]
    public void AnswerBodies()
    {
        var stream = H("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n\r\n");
        Assert.IsType<HttpBody.UntilClose>(stream.ResponseBody("POST"));
        Assert.IsType<HttpBody.None>(stream.ResponseBody("HEAD"));
        Assert.IsType<HttpBody.None>(H("HTTP/1.1 204 No Content\r\nContent-Length: 10\r\n\r\n").ResponseBody("POST"));
    }

    [Fact(DisplayName = "a lent request carries the lender's token, every end-to-end field, none of the hop's, and the body whole")]
    public void Rebuilt()
    {
        var lent = Encoding.Latin1.GetString(HttpMessage.Request(H(Sent), "hello"u8.ToArray(), "Bearer theirs"));
        Assert.Contains("Authorization: Bearer theirs\r\n", lent);
        Assert.DoesNotContain("mine", lent);
        Assert.Contains("X-Claude-Code-Session-Id: s1\r\n", lent);
        Assert.DoesNotContain("x-hop", lent.ToLowerInvariant());
        Assert.DoesNotContain("connection:", lent.ToLowerInvariant());
        Assert.Contains("Content-Length: 5\r\n", lent);
        Assert.EndsWith("\r\n\r\nhello", lent);
        var kept = Encoding.Latin1.GetString(HttpMessage.Request(H(Sent), "hello"u8.ToArray(), null));
        Assert.Contains("Authorization: Bearer mine\r\n", kept);
    }

    [Fact(DisplayName = "a chunked body is read whole however its bytes arrive, and nothing after its end is taken")]
    public void Chunked()
    {
        var wire = "5\r\nhello\r\n6;x=1\r\n world\r\n0\r\nTrailer: y\r\n\r\nNEXT"u8.ToArray();
        var framing = new ChunkedFraming();
        var payload = new List<byte>();
        var used = 0;
        foreach (var b in wire)
        {
            if (framing.IsDone) break;
            used += framing.Consume(new[] { b }, payload);
        }
        Assert.True(framing.IsDone);
        Assert.Equal("hello world", Encoding.ASCII.GetString(payload.ToArray()));
        Assert.Equal(wire.Length - 4, used);
        Assert.Throws<FormatException>(() => new ChunkedFraming().Consume("zz\r\n"u8, null));
    }
}
