using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// <summary>
/// One account as the relay is told about it: a Claude, or an account signed
/// in through Graft. Tokens travel only over the relay's pipe and live only in
/// its memory, so a relay that restarts passes everything through until told.
/// </summary>
public sealed record PoolMember
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    /// Two members on one account are one allowance.
    public string? Account { get; init; }
    /// Digests of every current token the profile holds, which is how a
    /// request is traced to its Claude.
    public IReadOnlyList<string> Identify { get; init; } = [];
    /// The token lent to others; null leaves a Claude able to borrow but not lend.
    public string? Token { get; init; }
    public PoolLimits? Limits { get; init; }
    /// The members whose accounts may answer this one's Claude Code.
    public IReadOnlyList<string>? BorrowsFrom { get; init; }

    public string AccountKey => Account ?? Id;
}

public sealed record PoolConfiguration
{
    public bool Enabled { get; init; }
    public IReadOnlyList<PoolMember> Members { get; init; } = [];
    /// Conversations somebody moved by hand, by session, to the member that
    /// should answer them.
    public IReadOnlyDictionary<string, string>? Assignments { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PoolPassReason>))]
public enum PoolPassReason { Full, Login, Refused, Unreachable }

public sealed record PoolPass(string Id, PoolPassReason Reason);

public sealed record PoolRouting(DateTimeOffset At, string From, string ServedBy, int Status,
    IReadOnlyList<PoolPass> PassedOver, string? Model, string? Session);

/// A conversation the relay has carried, by Claude Code's session id.
public sealed record PoolSession
{
    public required string Id { get; init; }
    public required string From { get; init; }
    public string? ServedBy { get; init; }
    public DateTimeOffset LastAt { get; init; }
    public int Requests { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? CachedUntil { get; init; }
    public IReadOnlyList<PoolPass> PassedOver { get; init; } = [];
    /// Null for one already under way when the relay first saw it.
    public DateTimeOffset? StartedAt { get; init; }
    /// The last turn a person typed, as against a tool's answer carried on.
    public DateTimeOffset? PromptedAt { get; init; }
}

public sealed record PoolMemberStatus
{
    public required string Id { get; init; }
    public PoolLimits? Limits { get; init; }
    public int Served { get; init; }
    public DateTimeOffset? LastServed { get; init; }
    /// The last request carrying this member's own token: the only sign its
    /// Claude is actually connected.
    public DateTimeOffset? LastSeen { get; init; }
    public bool TokenRefused { get; init; }
}

/// <summary>
/// What an account has left as the service last said, as fractions the way
/// the response headers carry them.
/// </summary>
public sealed record PoolLimits
{
    public double FiveHour { get; init; }
    public DateTimeOffset? FiveHourReset { get; init; }
    public double Week { get; init; }
    public DateTimeOffset? WeekReset { get; init; }
    /// Set when the service said no outright, until the time it named.
    public DateTimeOffset? RefusedUntil { get; init; }
    public DateTimeOffset Observed { get; init; }

    private const string Prefix = "anthropic-ratelimit-unified-";

    /// Every answer from /v1/messages carries the account's unified limits;
    /// null when one carried none, which is any that never reached the model.
    public static PoolLimits? Parse(IReadOnlyList<(string Name, string Value)> headers, DateTimeOffset now)
    {
        string? Value(string name) => headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        double? Number(string name) => double.TryParse(Value(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        DateTimeOffset? When(string name) => Number(name) is double s ? DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000)) : null;

        var five = Number(Prefix + "5h-utilization");
        var week = Number(Prefix + "7d-utilization");
        var status = Value(Prefix + "status");
        if (five is null && week is null && status is null) return null;
        return new PoolLimits
        {
            FiveHour = five ?? 0,
            FiveHourReset = When(Prefix + "5h-reset"),
            Week = week ?? 0,
            WeekReset = When(Prefix + "7d-reset"),
            RefusedUntil = status == "rejected" ? When(Prefix + "reset") ?? now.AddSeconds(60) : null,
            Observed = now,
        };
    }

    /// A reading from the usage endpoint, in percentages. An account with no
    /// five-hour window can never be full on one.
    public static PoolLimits FromPercent(int? fiveHour, DateTimeOffset? fiveHourReset, int week, DateTimeOffset? weekReset, DateTimeOffset observed) =>
        new() { FiveHour = (fiveHour ?? 0) / 100.0, FiveHourReset = fiveHourReset, Week = week / 100.0, WeekReset = weekReset, Observed = observed };
}

/// <summary>
/// Which account answers a request, and in what order to fall back. The one
/// whose week resets soonest goes first, since what it has left expires first.
/// </summary>
public static class PoolPolicy
{
    /// Short of a hundred: a request is checked against the limit as it
    /// starts, so the last one in at 99% goes through and the next is refused.
    public const double FiveHourCeiling = 0.98;
    public const double WeekCeiling = 0.99;

    /// A prompt cache lives on the account that wrote it, five minutes from
    /// last use unless the request asked for an hour.
    public static readonly TimeSpan ShortCache = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongCache = TimeSpan.FromHours(1);

    public static TimeSpan CacheLifetime(ReadOnlySpan<byte> body) =>
        body.IndexOf("\"ttl\":\"1h\""u8) >= 0 ? LongCache : ShortCache;

    public sealed record Account(string Id, PoolLimits? Limits, bool Usable);

    /// A reset already past starts the week again on the same weekday.
    public static DateTimeOffset? NextWeekReset(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is not DateTimeOffset r) return null;
        while (r <= now) r = r.AddDays(7);
        return r;
    }

    public static bool HasRoom(PoolLimits? limits, DateTimeOffset now)
    {
        if (limits is null) return true;
        if (limits.RefusedUntil > now) return false;
        if (limits.FiveHour >= FiveHourCeiling && (limits.FiveHourReset ?? DateTimeOffset.MaxValue) > now) return false;
        if (limits.Week >= WeekCeiling && (limits.WeekReset ?? DateTimeOffset.MaxValue) > now) return false;
        return true;
    }

    /// Best first. The sender is in wherever it ranked, or last when it has no
    /// room either. An account not yet heard from goes after every account with
    /// a known reset. prefer goes in front for as long as each still has room.
    public static List<string> Order(string own, IReadOnlyList<Account> accounts, IReadOnlyList<string>? prefer, DateTimeOffset now)
    {
        var open = accounts.Where(a => a.Usable && HasRoom(a.Limits, now)).ToList();
        open.Sort((a, b) =>
        {
            var ra = NextWeekReset(a.Limits?.WeekReset, now);
            var rb = NextWeekReset(b.Limits?.WeekReset, now);
            if (ra is not null && rb is not null && ra != rb) return ra.Value.CompareTo(rb.Value);
            if (ra is not null && rb is null) return -1;
            if (ra is null && rb is not null) return 1;
            // Staying on the sender's own account costs nothing, so it wins a tie.
            if ((a.Id == own) != (b.Id == own)) return a.Id == own ? -1 : 1;
            return string.CompareOrdinal(a.Id, b.Id);
        });
        var ids = open.Select(a => a.Id).ToList();
        foreach (var id in (prefer ?? []).Reverse())
        {
            var index = ids.IndexOf(id);
            if (index < 0) continue;
            ids.RemoveAt(index);
            ids.Insert(0, id);
        }
        if (!ids.Contains(own)) ids.Add(own);
        return ids;
    }

    public abstract record Outcome
    {
        public sealed record Deliver : Outcome;
        /// No room; try the next account.
        public sealed record Exhausted(DateTimeOffset Until) : Outcome;
        /// A borrowed token was refused; not offered again until it changes.
        public sealed record LoginRefused : Outcome;
        /// The borrowed account cannot serve this request: a model or feature
        /// its plan lacks. It goes back to the sender.
        public sealed record RefusedForAccount : Outcome;
    }

    /// Decided from the status line and headers alone, before a byte of the
    /// answer reaches Claude Code.
    public static Outcome OutcomeOf(int status, IReadOnlyList<(string Name, string Value)> headers, bool borrowed, DateTimeOffset now)
    {
        switch (status)
        {
            case 429:
                var limits = PoolLimits.Parse(headers, now);
                var retry = headers.FirstOrDefault(h => h.Name.Equals("retry-after", StringComparison.OrdinalIgnoreCase)).Value;
                var seconds = double.TryParse(retry, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 60;
                return new Outcome.Exhausted(limits?.RefusedUntil ?? now.AddSeconds(seconds));
            case 401:
                return borrowed ? new Outcome.LoginRefused() : new Outcome.Deliver();
            case 400 or 403 or 404:
                return borrowed ? new Outcome.RefusedForAccount() : new Outcome.Deliver();
            default:
                return new Outcome.Deliver();
        }
    }
}

/// <summary>What the relay reads off a request to decide where it may go.</summary>
public static class PoolRequest
{
    /// The one request worth moving; count_tokens and everything under /api/
    /// stay with the account that sent them.
    public static bool IsInference(string method, string target) =>
        method == "POST" && target.Split('?', 2)[0] == "/v1/messages";

    /// A file, container or server-side MCP connection lives in the
    /// organization that made it. Matched with the start of the value that
    /// makes it a reference: a tool schema can carry the bare key.
    public static readonly string[] AccountBoundMarks =
    [
        "\"file_id\":\"",
        "\"container\":\"", "\"container\":{\"id\":\"",
        "\"mcp_servers\":[{",
    ];

    public static bool MustStayWithSender(byte[] body, string? contentEncoding)
    {
        if (!string.IsNullOrEmpty(contentEncoding) && !contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) return true;
        return AccountBoundMarks.Any(m => Has(body, m));
    }

    /// For some models the service honours earlier reasoning only from the
    /// account that wrote it, and drops it silently anywhere else.
    public static readonly string[] ReasoningKeys = ["\"type\":\"thinking\"", "\"type\":\"redacted_thinking\""];

    /// Measured surviving a move between organizations; any other is bound.
    public static readonly string[] ReasoningTravels = ["claude-opus-5-5"];

    public static bool CarriesBoundReasoning(byte[] body)
    {
        if (!ReasoningKeys.Any(k => Has(body, k))) return false;
        return Model(body) is not string model || !ReasoningTravels.Any(model.StartsWith);
    }

    /// A thread is stored with the account that answered it; any other answers
    /// thread_not_found. JSON.stringify writes type first.
    public const string ThreadContinuation = "\"thread\":{\"type\":\"continue\"";
    public const string ThreadKey = "\"thread\":{";
    public static readonly byte[] ThreadMissing = "thread_not_found"u8.ToArray();

    public static bool ContinuesThread(byte[] body) => Has(body, ThreadContinuation);
    public static bool CarriesThread(byte[] body) => Has(body, ThreadKey);

    /// A first message carries no answer yet.
    public static bool StartsConversation(byte[] body) => !Has(body, "\"role\":\"assistant\"");

    /// A turn a person typed: on the thread, its last user message holding no
    /// tool's answer.
    public static bool StartsTurn(byte[] body)
    {
        if (!CarriesThread(body)) return false;
        var last = body.AsSpan().LastIndexOf("\"role\":\"user\""u8);
        return last >= 0 && body.AsSpan(last).IndexOf("\"type\":\"tool_result\""u8) < 0;
    }

    /// The first "model": is the request's own; a wrong guess mislabels one
    /// line on screen and decides nothing.
    public static string? Model(byte[] body)
    {
        var key = "\"model\":\""u8;
        var at = body.AsSpan().IndexOf(key);
        if (at < 0) return null;
        var start = at + key.Length;
        var window = body.AsSpan(start, Math.Min(120, body.Length - start));
        var end = window.IndexOf((byte)'"');
        return end < 0 ? null : Encoding.UTF8.GetString(window[..end]);
    }

    public static string? Bearer(IReadOnlyList<(string Name, string Value)> headers)
    {
        var value = headers.FirstOrDefault(h => h.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase)).Value;
        if (value is null || value.Length <= 7 || !value[..7].Equals("bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        return value[7..].Trim();
    }

    /// Tokens are known by digest, so the relay can say whose a request is
    /// without holding tokens it was not handed to lend.
    public static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool Has(byte[] body, string text) => body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
}
