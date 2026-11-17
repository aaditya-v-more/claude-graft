using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// Which Claudes draw on which accounts. Every Claude has its own list, kept by
/// its key: "main" for Claude's own profile, a shortcut's id for the rest.
/// </summary>
public sealed record UsageSharing
{
    public Dictionary<string, List<string>> Lenders { get; init; } = new();
    /// Conversations moved to an account by hand, by session.
    public Dictionary<string, Assignment> Assignments { get; init; } = new();

    public sealed record Assignment(string To, DateTimeOffset At);

    public const string MainKey = "main";
    public static string File => Path.Combine(GraftPaths.OwnData, "usage-sharing.json");

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// An unreadable file is no sharing rather than a guess at it.
    public static UsageSharing Load()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<UsageSharing>(System.IO.File.ReadAllText(File), Json) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(GraftPaths.OwnData);
        var temp = File + ".new";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        System.IO.File.Move(temp, File, overwrite: true);
    }
}

/// <summary>
/// Keeps the relay running while any Claude draws on another account, tells it
/// who is who, and puts the two lines into each borrowing Claude's environment.
/// Every pass reads each login afresh, since Claude renews them, and hands the
/// relay everything again, so a relay restarted or replaced is back in step
/// within one pass.
/// </summary>
public sealed class SharedUsage
{
    public enum Lending { Ready, SignedOut, Expired, Unreadable }

    public sealed record Result(RelayStatus? Status, IReadOnlyDictionary<string, ClaudeCodeEnvironment.State> Environment,
        IReadOnlyDictionary<string, Lending> Lending, string? Problem);

    /// The scope Claude hands Claude Code carries, which makes it the token to
    /// lend: the service has already accepted it for exactly this.
    public const string LendingScope = "user:sessions:claude_code";

    public static string RelaySource => Path.Combine(AppContext.BaseDirectory, "relay", RelayKeeper.ExeName);

    private readonly Dictionary<string, (Login Login, DateTimeOffset At)> _lastLogins = new();
    private DateTimeOffset _lastSpawn = DateTimeOffset.MinValue;
    private readonly object _gate = new();

    public sealed record Login(Lending State, IReadOnlyList<string> Identify, string? Token);

    /// A login that will not read is a config.json caught as Claude renames a
    /// new one over it; the last one read stands in, for ten minutes at most.
    public static Login Steady(Login login, (Login Login, DateTimeOffset At)? kept, DateTimeOffset now) =>
        login.State == Lending.Unreadable && kept is { } k && now - k.At < TimeSpan.FromMinutes(10) ? k.Login : login;

    public static Login LoginOf(string profile)
    {
        try
        {
            if (ClaudeCredentials.Tokens(profile) is not { } all) return new(Lending.SignedOut, [], null);
            var current = all.Where(t => t.IsCurrent).ToList();
            var lent = current.Where(t => t.Scopes.Contains(LendingScope)).MaxBy(t => t.Expires)
                ?? current.Where(t => t.Covers([ClaudeCredentials.InferenceScope])).MaxBy(t => t.Expires);
            return new(lent is null ? Lending.Expired : Lending.Ready, all.Select(t => PoolRequest.Digest(t.Value)).ToList(), lent?.Value);
        }
        catch (ClaudeCredentials.CredentialException e) when (e.Reason == ClaudeCredentials.Failure.NotSignedIn)
        {
            return new(Lending.SignedOut, [], null);
        }
        catch (Exception) { return new(Lending.Unreadable, [], null); }
    }

    /// Blocking: reads logins and files and talks to the relay.
    public Result Pass(IReadOnlyList<(string Key, string Name, string Profile)> claudes, Func<string, PoolLimits?> limitsFor,
        Func<string, bool> running, string? relaySource = null)
    {
        lock (_gate) return PassLocked(claudes, limitsFor, running, relaySource ?? RelaySource);
    }

    private Result PassLocked(IReadOnlyList<(string Key, string Name, string Profile)> claudes, Func<string, PoolLimits?> limitsFor,
        Func<string, bool> running, string relaySource)
    {
        var sharing = UsageSharing.Load();
        var byKey = claudes.ToDictionary(c => c.Key, c => c);
        var accounts = PoolAccounts.Load().ToDictionary(a => a.Id);
        var involved = new List<string>();
        foreach (var (claude, list) in sharing.Lenders.Where(l => l.Value.Count > 0))
            foreach (var key in list.Prepend(claude))
                if (!involved.Contains(key) && (byKey.ContainsKey(key) || accounts.ContainsKey(key))) involved.Add(key);

        var now = DateTimeOffset.UtcNow;
        var lending = new Dictionary<string, Lending>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = new List<PoolMember>();
        foreach (var key in involved)
        {
            if (accounts.TryGetValue(key, out var account))
            {
                var token = PoolAccounts.Unseal(account.Sealed);
                var current = token is not null && account.Expires > now.AddSeconds(120);
                lending[key] = token is null ? Lending.Unreadable : current ? Lending.Ready : Lending.Expired;
                members.Add(new PoolMember
                {
                    Id = key, Name = account.Name, Account = account.AccountUuid,
                    Identify = token is null ? [] : [PoolRequest.Digest(token)], Token = current ? token : null,
                });
                continue;
            }
            var claude = byKey[key];
            // Two shortcuts can stand for one profile; lending it twice would
            // count one account's room twice.
            if (!seen.Add(PoolEnvironment.Key(claude.Profile))) continue;
            var login = LoginOf(claude.Profile);
            if (login.State != Lending.Unreadable) _lastLogins[key] = (login, now);
            login = Steady(login, _lastLogins.TryGetValue(key, out var kept) ? kept : null, now);
            lending[key] = login.State;
            members.Add(new PoolMember
            {
                Id = key, Name = claude.Name, Account = Graft.Account(claude.Profile),
                Identify = login.Identify, Token = login.Token, Limits = limitsFor(claude.Profile),
                BorrowsFrom = sharing.Lenders.GetValueOrDefault(key),
            });
        }
        var configuration = new PoolConfiguration
        {
            Enabled = true, Members = members,
            Assignments = sharing.Assignments.ToDictionary(a => a.Key, a => a.Value.To),
        };
        var journal = PoolEnvironment.ReadJournal();
        var wanted = members.Count > 0 || journal.Profiles.Count > 0;
        var (status, problem) = Deliver(configuration, wanted, relaySource);

        var environment = new Dictionary<string, ClaudeCodeEnvironment.State>();
        if (wanted)
        {
            var wants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in claudes)
            {
                var borrows = sharing.Lenders.GetValueOrDefault(c.Key) is { Count: > 0 };
                if (borrows || journal.Profiles.ContainsKey(PoolEnvironment.Key(c.Profile))) wants[c.Profile] = borrows;
            }
            var states = ClaudeCodeEnvironment.Settle(wants, ClaudeCredentials.SafeStorageKey, relaySource, status is not null, running);
            foreach (var c in claudes)
                if (states.TryGetValue(PoolEnvironment.Key(c.Profile), out var state)) environment[c.Key] = state;
            // A relay that was not needed until the lines went in.
            if (status is null && states.Values.Any(s => s.HoldsLines)) (status, problem) = Deliver(configuration, true, relaySource);
        }
        return new Result(status, environment, lending, problem);
    }

    /// Nobody to serve and no Claude pointed at it: a relay still running steps
    /// aside. Otherwise this build's relay is made to answer and told everything.
    private (RelayStatus?, string?) Deliver(PoolConfiguration configuration, bool wanted, string relaySource)
    {
        var command = new RelayRequest { Configure = configuration };
        if (!wanted)
        {
            if (RelayKeeper.Send(new RelayRequest(), timeoutMs: 1500) is not null) RelayKeeper.Send(new RelayRequest { Quit = true });
            return (null, null);
        }
        if (!File.Exists(relaySource)) return (null, "This copy of Claude Graft has no relay in it.");
        var running = RelayKeeper.Send(command, timeoutMs: 2000);
        if (running is not null && running.Binary == RelayKeeper.Digest(relaySource)) return (running, null);
        // A relay that will not stay up is not started again every pass.
        if (DateTimeOffset.UtcNow - _lastSpawn < TimeSpan.FromSeconds(20)) return (running, running is null ? "The relay is not running. Diagnostics has the reason." : null);
        _lastSpawn = DateTimeOffset.UtcNow;
        if (RelayKeeper.EnsureRunning(relaySource) == RelayKeeper.Step.Failed) return (null, "The relay could not be started.");
        // The first start makes the certificate, which takes a moment.
        for (var i = 0; i < 60; i++)
        {
            Thread.Sleep(250);
            if (RelayKeeper.Send(command, timeoutMs: 1000) is { } status) return (status, null);
        }
        return (null, "The relay is not running. Diagnostics has the reason.");
    }
}
