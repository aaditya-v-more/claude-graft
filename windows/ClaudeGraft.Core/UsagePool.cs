using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

public static class UsagePool
{
    public const int Port = 47823;
    public const string Host = "api.anthropic.com";
    public const string LendingScope = "user:sessions:claude_code";
    public static string Directory => Path.Combine(GraftPaths.OwnData, "pool");
    public static string CertificateFile => Path.Combine(Directory, "relay-ca.pem");
    public static string IdentityFile => Path.Combine(Directory, "relay-identity.bin");
    public static string SettingsFile => Path.Combine(Directory, "sharing.json");
    public static string AccountsFile => Path.Combine(Directory, "accounts.json");
    public static string Digest(string text) => Digest(Encoding.UTF8.GetBytes(text));
    public static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record PoolLimits(double FiveHour, DateTimeOffset? FiveHourReset, double Week,
    DateTimeOffset? WeekReset, DateTimeOffset? RefusedUntil, DateTimeOffset Observed)
{
    public static PoolLimits? Parse(IEnumerable<KeyValuePair<string, string>> headers, DateTimeOffset now)
    {
        var values = headers.ToList();
        string? Value(string name) => values.FirstOrDefault(x => x.Key.Equals("anthropic-ratelimit-unified-" + name, StringComparison.OrdinalIgnoreCase)).Value;
        double? Number(string name) => double.TryParse(Value(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
        DateTimeOffset? Date(string name)
        {
            try { return Number(name) is double number ? DateTimeOffset.FromUnixTimeSeconds((long)number) : null; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        var five = Number("5h-utilization"); var week = Number("7d-utilization"); var status = Value("status");
        return five is null && week is null && status is null ? null
            : new(five ?? 0, Date("5h-reset"), week ?? 0, Date("7d-reset"),
                status == "rejected" ? Date("reset") ?? now.AddMinutes(1) : null, now);
    }
}

public static class PoolPolicy
{
    public static readonly TimeSpan ShortCache = TimeSpan.FromMinutes(5), LongCache = TimeSpan.FromHours(1);
    public sealed record Account(string Id, PoolLimits? Limits, bool Usable);
    public enum Result { Deliver, Exhausted, LoginRefused, RefusedForAccount }
    public sealed record Outcome(Result Result, DateTimeOffset? Until = null);

    public static bool HasRoom(PoolLimits? limits, DateTimeOffset now) => limits is null ||
        !(limits.RefusedUntil > now || limits.FiveHour >= .98 && (limits.FiveHourReset ?? DateTimeOffset.MaxValue) > now
          || limits.Week >= .99 && (limits.WeekReset ?? DateTimeOffset.MaxValue) > now);

    public static DateTimeOffset? NextWeekReset(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null || reset > now) return reset;
        return reset.Value.AddDays((Math.Floor((now - reset.Value).TotalDays / 7) + 1) * 7);
    }

    public static List<string> Order(string own, IEnumerable<Account> accounts, IEnumerable<string>? prefer, DateTimeOffset now)
    {
        var ids = accounts.Where(x => x.Usable && HasRoom(x.Limits, now))
            .OrderBy(x => NextWeekReset(x.Limits?.WeekReset, now) ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Id == own ? 0 : 1).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Id).ToList();
        foreach (var id in (prefer ?? []).Reverse()) if (ids.Remove(id)) ids.Insert(0, id);
        if (!ids.Contains(own)) ids.Add(own);
        return ids;
    }

    public static Outcome Evaluate(int status, IEnumerable<KeyValuePair<string, string>> headers, bool borrowed, DateTimeOffset now)
    {
        if (status == 429)
        {
            var pairs = headers.ToArray();
            var retry = pairs.FirstOrDefault(x => x.Key.Equals("retry-after", StringComparison.OrdinalIgnoreCase)).Value;
            var seconds = double.TryParse(retry, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? Math.Clamp(parsed, 0, 86400 * 7) : 60;
            return new(Result.Exhausted, PoolLimits.Parse(pairs, now)?.RefusedUntil ?? now.AddSeconds(seconds));
        }
        return new(borrowed ? status switch { 401 => Result.LoginRefused, 400 or 403 or 404 => Result.RefusedForAccount, _ => Result.Deliver } : Result.Deliver);
    }
}

public static class PoolRequest
{
    public static bool IsInference(string method, string target) => method == "POST" && target.Split('?', 2)[0] == "/v1/messages";
    public static string? Bearer(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var value = headers.FirstOrDefault(x => x.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)).Value;
        return value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? value[7..].Trim() : null;
    }

    public sealed record Shape(bool Pinned, bool Bound, bool Continuing, bool Threaded, bool Starts, bool Prompted, string? Model, TimeSpan Cache);
    public static Shape Inspect(byte[] body, string? encoding)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var threaded = root.TryGetProperty("thread", out var thread) && thread.ValueKind == JsonValueKind.Object;
            var continuing = threaded && thread.TryGetProperty("type", out var type) && type.GetString() == "continue";
            bool Walk(JsonElement value, Func<JsonProperty, bool> match) => value.ValueKind switch
            {
                JsonValueKind.Object => value.EnumerateObject().Any(p => match(p) || Walk(p.Value, match)),
                JsonValueKind.Array => value.EnumerateArray().Any(p => Walk(p, match)), _ => false,
            };
            var pinned = !string.IsNullOrEmpty(encoding) && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)
                || Walk(root, p => p.Name == "file_id" && p.Value.ValueKind == JsonValueKind.String
                    || p.Name == "container" && (p.Value.ValueKind == JsonValueKind.String
                        || p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    || p.Name == "mcp_servers" && p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object));
            var reasoning = Walk(root, p => p.Name == "type" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "thinking" or "redacted_thinking");
            var messages = root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array ? msgs.EnumerateArray().ToArray() : [];
            string? Role(JsonElement msg) => msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var user = messages.LastOrDefault(x => Role(x) == "user");
            var prompted = threaded && user.ValueKind != JsonValueKind.Undefined && !Walk(user, p => p.Name == "type" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "tool_result");
            return new(pinned, continuing || reasoning && !(model?.StartsWith("claude-opus-5-5", StringComparison.Ordinal) ?? false),
                continuing, threaded, !messages.Any(x => Role(x) == "assistant"), prompted, model,
                Walk(root, p => p.Name == "ttl" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "1h") ? PoolPolicy.LongCache : PoolPolicy.ShortCache);
        }
        catch (JsonException) { return new(true, false, false, false, false, false, null, PoolPolicy.ShortCache); }
        catch (InvalidOperationException) { return new(true, false, false, false, false, false, null, PoolPolicy.ShortCache); }
    }
}

public sealed record PoolMember(string Id, string Name, string? Account, string[] Identify, string? Token, PoolLimits? Limits, string[]? BorrowsFrom);
public sealed record PoolConfiguration(bool Enabled, PoolMember[] Members, Dictionary<string, string>? Assignments = null);
public sealed record PoolCommand(PoolConfiguration? Configure = null, bool Quit = false);
public sealed record PoolPass(string Id, string Reason);
public sealed record PoolRouting(DateTimeOffset At, string From, string ServedBy, int Status, PoolPass[] PassedOver, string? Model, string? Session);
public sealed record PoolSession(string Id, string From, string? ServedBy, DateTimeOffset LastAt, int Requests, string? Model,
    DateTimeOffset? CachedUntil, PoolPass[] PassedOver, DateTimeOffset? StartedAt, DateTimeOffset? PromptedAt);
public sealed record PoolMemberStatus(string Id, PoolLimits? Limits, int Served, DateTimeOffset? LastServed, DateTimeOffset? LastSeen, bool TokenRefused);
public sealed record PoolStatus(string Binary, int Pid, DateTimeOffset Started, bool Enabled, bool Configured,
    PoolMemberStatus[] Members, PoolRouting[] Recent, PoolSession[] Sessions, int OpenExchanges);

/// <summary>An update may replace a relay only when it can configure its successor.</summary>
public static class PoolDelivery
{
    public enum Step { Keep, Replace, Start }
    public static Step Decide(PoolStatus? status, bool unreadableReply, string bundled, bool canConfigure) =>
        status is null && !unreadableReply ? Step.Start
        : status?.Binary == bundled || !canConfigure ? Step.Keep : Step.Replace;
    public static PoolCommand Command(PoolConfiguration configuration, bool credentialsComplete, bool revocationPending = false) =>
        credentialsComplete || !configuration.Enabled ? new(configuration)
        : revocationPending ? new(new(false, [])) : new();
}

/// <summary>Only service observations and routing metadata live here; tokens never reach status or disk.</summary>
public sealed class PoolState
{
    public sealed record Attempt(string? Member, string? Token);
    private sealed class Runtime { public int Served; public DateTimeOffset? LastServed, LastSeen; public string? Refused; }
    private sealed class Conversation
    {
        public required string From; public string? ServedBy, Model; public DateTimeOffset LastAt;
        public int Requests; public TimeSpan Cache = PoolPolicy.ShortCache; public PoolPass[] Passed = [];
        public bool Threaded; public DateTimeOffset? Started, Prompted;
    }
    private readonly object gate = new();
    private PoolConfiguration? configuration;
    private readonly Dictionary<string, Runtime> runtime = [];
    private readonly Dictionary<string, string> byDigest = [], lending = [];
    private readonly Dictionary<string, PoolLimits> limits = [];
    private readonly Dictionary<string, Conversation> conversations = [];
    private readonly List<PoolRouting> recent = [];
    private int active;
    private string Account(string id) => configuration?.Members.FirstOrDefault(m => m.Id == id)?.Account ?? id;

    public void Configure(PoolConfiguration next, DateTimeOffset now)
    {
        lock (gate)
        {
            configuration = next; byDigest.Clear(); lending.Clear();
            foreach (var member in next.Members)
            {
                foreach (var digest in member.Identify) byDigest.TryAdd(digest, member.Id);
                if (!runtime.TryGetValue(member.Id, out var run)) runtime[member.Id] = run = new();
                if (member.Token is not null)
                {
                    var digest = UsagePool.Digest(member.Token); lending[member.Id] = digest; byDigest.TryAdd(digest, member.Id);
                    if (run.Refused != digest) run.Refused = null;
                }
                var key = Account(member.Id);
                if (member.Limits is { } pushed && (!limits.TryGetValue(key, out var old) || pushed.Observed > old.Observed))
                    limits[key] = pushed with { RefusedUntil = limits.GetValueOrDefault(key)?.RefusedUntil > now ? limits[key].RefusedUntil : pushed.RefusedUntil };
            }
            foreach (var id in runtime.Keys.Except(next.Members.Select(m => m.Id)).ToArray()) runtime.Remove(id);
            foreach (var id in limits.Keys.Except(next.Members.Select(m => m.Account ?? m.Id)).ToArray()) limits.Remove(id);
        }
    }

    public string? Member(string bearer, DateTimeOffset now)
    {
        lock (gate) { if (!byDigest.TryGetValue(UsagePool.Digest(bearer), out var id)) return null; runtime[id].LastSeen = now; return id; }
    }

    public static List<PoolMember> Candidates(PoolMember sender, IEnumerable<PoolMember> members, Func<PoolMember, bool>? canLend = null)
    {
        canLend ??= m => m.Token is not null;
        var all = members.ToArray(); var output = new List<PoolMember> { sender };
        foreach (var id in sender.BorrowsFrom ?? [])
        {
            var lender = all.FirstOrDefault(m => m.Id == id); if (lender is null || id == sender.Id) continue;
            var index = output.FindIndex(m => (m.Account ?? m.Id) == (lender.Account ?? lender.Id));
            if (index < 0) output.Add(lender);
            else if (index > 0 && !canLend(output[index]) && canLend(lender)) output[index] = lender;
        }
        return output;
    }

    public List<Attempt> Plan(string own, string? session, bool pinned, bool bound, DateTimeOffset now)
    {
        lock (gate)
        {
            var sender = configuration?.Members.FirstOrDefault(m => m.Id == own);
            if (configuration?.Enabled != true || pinned || sender is null) return [new(own, null)];
            bool CanLend(PoolMember m) => m.Token is not null && (runtime.GetValueOrDefault(m.Id)?.Refused is not string refused || refused != lending.GetValueOrDefault(m.Id));
            var candidates = Candidates(sender, configuration.Members, CanLend);
            string? Counted(string id) => candidates.FirstOrDefault(m => (m.Account ?? m.Id) == Account(id))?.Id;
            var prefer = new List<string>(); string? holder = null;
            if (session is not null)
            {
                if (configuration.Assignments?.GetValueOrDefault(session) is string choice && Counted(choice) is string chosen) prefer.Add(chosen);
                if (conversations.GetValueOrDefault(session) is { ServedBy: not null } held && (bound || now - held.LastAt < held.Cache))
                { holder = Counted(held.ServedBy); if (holder is not null) prefer.Add(holder); }
            }
            if (bound && holder is null) prefer.Add(own);
            var ids = PoolPolicy.Order(own, candidates.Select(m => new PoolPolicy.Account(m.Id, limits.GetValueOrDefault(Account(m.Id)), m.Id == own || CanLend(m))), prefer, now);
            if (ids[^1] != own) ids.Add(own);
            return ids.Select(id => new Attempt(id, id == own ? null : candidates.First(m => m.Id == id).Token)).ToList();
        }
    }

    public void Observe(string id, IEnumerable<KeyValuePair<string, string>> headers, DateTimeOffset now)
    { lock (gate) { if (PoolLimits.Parse(headers, now) is { } value) limits[Account(id)] = value; } }
    public void Apply(PoolPolicy.Outcome outcome, string id, DateTimeOffset now)
    {
        lock (gate)
        {
            if (outcome.Result == PoolPolicy.Result.LoginRefused && runtime.TryGetValue(id, out var run)) run.Refused = lending.GetValueOrDefault(id);
            if (outcome.Result == PoolPolicy.Result.Exhausted)
            { var key = Account(id); limits[key] = (limits.GetValueOrDefault(key) ?? new(0, null, 0, null, null, now)) with { RefusedUntil = outcome.Until }; }
        }
    }
    public void Served(string member, string own, string? session, int status, PoolPass[] passed, PoolRequest.Shape shape, DateTimeOffset now)
    {
        lock (gate)
        {
            if (runtime.TryGetValue(member, out var run)) { run.Served++; run.LastServed = now; }
            if (session is not null)
            {
                if (!conversations.TryGetValue(session, out var held)) conversations[session] = held = new() { From = own, LastAt = now, Started = shape.Starts ? now : null };
                held.From = own; held.Requests++; if (shape.Prompted) held.Prompted = now;
                if (shape.Threaded || !held.Threaded)
                {
                    held.LastAt = now; held.Model = shape.Model ?? held.Model; held.Passed = passed;
                    if (status is >= 200 and < 300) { held.ServedBy = member; held.Cache = shape.Cache; held.Threaded |= shape.Threaded; }
                }
                foreach (var id in conversations.Where(x => now - x.Value.LastAt >= TimeSpan.FromDays(1)).Select(x => x.Key).ToArray()) conversations.Remove(id);
            }
            recent.Add(new(now, own, member, status, passed, shape.Model, session)); if (recent.Count > 40) recent.RemoveAt(0);
        }
    }
    public void Began() { lock (gate) active++; }
    public void Ended() { lock (gate) active--; }
    public PoolStatus Status(string binary, int pid, DateTimeOffset started, DateTimeOffset now)
    {
        lock (gate)
        {
            return new(binary, pid, started, configuration?.Enabled ?? false, configuration is not null,
                (configuration?.Members ?? []).Select(m => { var r = runtime.GetValueOrDefault(m.Id); return new PoolMemberStatus(m.Id, limits.GetValueOrDefault(Account(m.Id)), r?.Served ?? 0, r?.LastServed, r?.LastSeen, r?.Refused is not null); }).ToArray(),
                recent.ToArray(), conversations.Where(x => now - x.Value.LastAt < TimeSpan.FromDays(1)).OrderByDescending(x => x.Value.LastAt).Take(64)
                    .Select(x => new PoolSession(x.Key, x.Value.From, x.Value.ServedBy, x.Value.LastAt, x.Value.Requests, x.Value.Model,
                        x.Value.ServedBy is null ? null : x.Value.LastAt + x.Value.Cache, x.Value.Passed, x.Value.Started, x.Value.Prompted)).ToArray(), active);
        }
    }
}
