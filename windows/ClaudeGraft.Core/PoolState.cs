namespace ClaudeGraft.Core;

/// <summary>
/// Everything the relay knows: what Graft last told it and what the service
/// has said since. Nothing is written to disk, tokens least of all.
/// </summary>
public sealed class PoolState
{
    /// Member null is a request from nobody in the pool; token null is the
    /// token the request arrived with.
    public sealed record Attempt(string? Member, string? Token);

    private sealed class Runtime
    {
        public int Served;
        public DateTimeOffset? LastServed;
        public DateTimeOffset? LastSeen;
        /// Digest of the lent token the service refused; lending stops until
        /// Graft hands over a different one, which is what a renewed login is.
        public string? RefusedToken;
    }

    private sealed class Conversation
    {
        public required string From;
        public string? ServedBy;
        public DateTimeOffset LastAt;
        public int Requests;
        public string? Model;
        public TimeSpan CacheLifetime = PoolPolicy.ShortCache;
        public IReadOnlyList<PoolPass> PassedOver = [];
        /// Once an answer to a request carrying a thread arrives, only such
        /// answers say where the conversation is; a title answered elsewhere
        /// holds none of it.
        public bool Threaded;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? PromptedAt;
    }

    /// Long enough to go on naming a conversation left for the afternoon,
    /// short enough that the list stays the day's.
    public static readonly TimeSpan ConversationMemory = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private PoolConfiguration? _configuration;
    private Dictionary<string, string> _memberByDigest = new();
    private Dictionary<string, string> _lendingDigest = new();
    private Dictionary<string, Runtime> _runtime = new();
    /// By account: two members on one account are one allowance.
    private Dictionary<string, PoolLimits> _limits = new();
    private Dictionary<string, Conversation> _conversations = new();
    private readonly List<PoolRouting> _recent = new();
    private int _active;

    private string AccountKey(string id) => _configuration?.Members.FirstOrDefault(m => m.Id == id)?.AccountKey ?? id;

    public void Configure(PoolConfiguration configuration, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _configuration = configuration;
            _memberByDigest = new();
            _lendingDigest = new();
            var kept = new Dictionary<string, Runtime>();
            foreach (var member in configuration.Members)
            {
                foreach (var digest in member.Identify) _memberByDigest.TryAdd(digest, member.Id);
                var state = _runtime.GetValueOrDefault(member.Id) ?? new Runtime();
                if (member.Token is string token)
                {
                    var digest = PoolRequest.Digest(token);
                    _lendingDigest[member.Id] = digest;
                    _memberByDigest.TryAdd(digest, member.Id);
                    if (state.RefusedToken != digest) state.RefusedToken = null;
                }
                // The service's headers are fresher than any poll while the
                // account is in use, and a poll is fresher once it goes quiet.
                var key = member.AccountKey;
                if (member.Limits is { } pushed && pushed.Observed > (_limits.GetValueOrDefault(key)?.Observed ?? DateTimeOffset.MinValue))
                {
                    var refused = _limits.GetValueOrDefault(key)?.RefusedUntil;
                    _limits[key] = refused > now ? pushed with { RefusedUntil = refused } : pushed;
                }
                kept[member.Id] = state;
            }
            _runtime = kept;
            var named = configuration.Members.Select(m => m.AccountKey).ToHashSet();
            _limits = _limits.Where(l => named.Contains(l.Key)).ToDictionary();
        }
    }

    /// Which member a token belongs to; any request carrying one counts as that
    /// Claude being connected.
    public string? MemberFor(string bearer, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            if (!_memberByDigest.TryGetValue(PoolRequest.Digest(bearer), out var id)) return null;
            (_runtime.TryGetValue(id, out var r) ? r : _runtime[id] = new Runtime()).LastSeen = at ?? DateTimeOffset.UtcNow;
            return id;
        }
    }

    /// The sender first, then the accounts it was given, each account once.
    /// A lender on the sender's own account lends nothing. An account given
    /// under two names counts under the first that can lend.
    public static List<PoolMember> Candidates(PoolMember sender, IReadOnlyList<PoolMember> members, Func<PoolMember, bool>? canLend = null)
    {
        canLend ??= m => m.Token is not null;
        var result = new List<PoolMember> { sender };
        var place = new Dictionary<string, int> { [sender.AccountKey] = 0 };
        foreach (var id in sender.BorrowsFrom ?? [])
        {
            if (id == sender.Id || members.FirstOrDefault(m => m.Id == id) is not { } lender) continue;
            if (place.TryGetValue(lender.AccountKey, out var index))
            {
                if (index > 0 && !canLend(result[index]) && canLend(lender)) result[index] = lender;
            }
            else
            {
                place[lender.AccountKey] = result.Count;
                result.Add(lender);
            }
        }
        return result;
    }

    /// bound is a request only the account that last answered can take as it
    /// stands: reasoning that account wrote, or a thread stored there.
    public List<Attempt> Plan(string own, string? session, bool pinned, bool bound = false, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var asSent = new List<Attempt> { new(own, null) };
        lock (_gate)
        {
            if (_configuration is not { Enabled: true } configuration || pinned
                || configuration.Members.FirstOrDefault(m => m.Id == own) is not { } sender) return asSent;
            bool CanLend(PoolMember m) =>
                m.Token is not null && _runtime.GetValueOrDefault(m.Id)?.RefusedToken != _lendingDigest.GetValueOrDefault(m.Id);
            var candidates = Candidates(sender, configuration.Members, CanLend);
            if (candidates.Count <= 1) return asSent;
            var accounts = candidates.Select(m => new PoolPolicy.Account(m.Id, _limits.GetValueOrDefault(m.AccountKey), m.Id == own || CanLend(m))).ToList();

            // One account is one candidate whichever name it came under, so a
            // name the relay did not count stands for the one it did.
            string? Counted(string id)
            {
                var account = configuration.Members.FirstOrDefault(m => m.Id == id)?.AccountKey ?? id;
                return candidates.FirstOrDefault(c => c.AccountKey == account)?.Id;
            }
            var prefer = new List<string>();
            string? holder = null;
            if (session is not null)
            {
                _conversations.TryGetValue(session, out var held);
                if (configuration.Assignments?.GetValueOrDefault(session) is string chosen && Counted(chosen) is string id)
                    prefer.Add(id);
                // Reasoning outlives the cache, so a bound conversation stays
                // until its account has no room, then stays wherever it went.
                if (held?.ServedBy is string last && (bound || now - held.LastAt < held.CacheLifetime))
                {
                    holder = Counted(last);
                    if (holder is not null) prefer.Add(holder);
                }
            }
            // Nothing says where an unseen conversation wrote its reasoning,
            // and the sender's own account is where it began.
            if (bound && holder is null) prefer.Add(own);
            var ids = PoolPolicy.Order(own, accounts, prefer, now);
            // The last word is always the sender's own: whatever the lenders
            // said is about a login Claude Code never sent.
            if (ids[^1] != own) ids.Add(own);
            return ids.Select(id => id == own ? new Attempt(own, null) : new Attempt(id, candidates.First(c => c.Id == id).Token)).ToList();
        }
    }

    public void Observe(string member, IReadOnlyList<(string Name, string Value)> headers, DateTimeOffset? at = null)
    {
        if (PoolLimits.Parse(headers, at ?? DateTimeOffset.UtcNow) is not { } parsed) return;
        lock (_gate) _limits[AccountKey(member)] = parsed;
    }

    public void Apply(PoolPolicy.Outcome outcome, string member, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            switch (outcome)
            {
                case PoolPolicy.Outcome.Exhausted e:
                    var key = AccountKey(member);
                    var known = _limits.GetValueOrDefault(key) ?? new PoolLimits { Observed = now };
                    _limits[key] = known with { RefusedUntil = e.Until };
                    break;
                case PoolPolicy.Outcome.LoginRefused:
                    (_runtime.TryGetValue(member, out var r) ? r : _runtime[member] = new Runtime()).RefusedToken = _lendingDigest.GetValueOrDefault(member);
                    break;
            }
        }
    }

    public void Served(string member, string own, string? session, int status, IReadOnlyList<PoolPass> passedOver,
        string? model, TimeSpan cacheLifetime, bool threaded = false, bool starts = false, bool prompted = false, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var runtime = _runtime.TryGetValue(member, out var r) ? r : _runtime[member] = new Runtime();
            runtime.Served++;
            runtime.LastServed = now;
            if (session is not null)
            {
                if (!_conversations.TryGetValue(session, out var held))
                    _conversations[session] = held = new Conversation { From = own, LastAt = now, StartedAt = starts ? now : null };
                held.From = own;
                held.Requests++;
                if (prompted) held.PromptedAt = now;
                if (threaded || !held.Threaded)
                {
                    held.LastAt = now;
                    held.Model = model ?? held.Model;
                    held.PassedOver = passedOver;
                    // A failed answer wrote no cache anywhere, so it moves nothing.
                    if (status is >= 200 and < 300)
                    {
                        held.ServedBy = member;
                        held.CacheLifetime = cacheLifetime;
                        held.Threaded |= threaded;
                    }
                }
                if (_conversations.Count > 256)
                    _conversations = _conversations.Where(c => now - c.Value.LastAt < ConversationMemory).ToDictionary();
            }
            _recent.Add(new PoolRouting(now, own, member, status, passedOver, model, session));
            if (_recent.Count > 40) _recent.RemoveRange(0, _recent.Count - 40);
        }
    }

    public void Began() => Interlocked.Increment(ref _active);
    public void Ended() => Interlocked.Decrement(ref _active);
    public int InFlight => Volatile.Read(ref _active);

    public RelayStatus Status(string binary, int pid, DateTimeOffset started, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var members = (_configuration?.Members ?? []).Select(m =>
            {
                var state = _runtime.GetValueOrDefault(m.Id);
                return new PoolMemberStatus
                {
                    Id = m.Id,
                    Limits = _limits.GetValueOrDefault(m.AccountKey),
                    Served = state?.Served ?? 0,
                    LastServed = state?.LastServed,
                    LastSeen = state?.LastSeen,
                    TokenRefused = state?.RefusedToken is not null,
                };
            }).ToList();
            var sessions = _conversations
                .Where(c => now - c.Value.LastAt < ConversationMemory)
                .OrderByDescending(c => c.Value.LastAt)
                .Take(64)
                .Select(c => new PoolSession
                {
                    Id = c.Key,
                    From = c.Value.From,
                    ServedBy = c.Value.ServedBy,
                    LastAt = c.Value.LastAt,
                    Requests = c.Value.Requests,
                    Model = c.Value.Model,
                    CachedUntil = c.Value.ServedBy is null ? null : c.Value.LastAt + c.Value.CacheLifetime,
                    PassedOver = c.Value.PassedOver,
                    StartedAt = c.Value.StartedAt,
                    PromptedAt = c.Value.PromptedAt,
                }).ToList();
            return new RelayStatus
            {
                Binary = binary,
                Pid = pid,
                Started = started,
                Enabled = _configuration?.Enabled ?? false,
                Configured = _configuration is not null,
                Members = members,
                Recent = _recent.ToList(),
                Sessions = sessions,
                OpenExchanges = InFlight,
            };
        }
    }
}
