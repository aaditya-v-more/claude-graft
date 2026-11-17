using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// What one profile's usage is, from whichever source could answer. The live
/// endpoint is preferred — current and with exact resets — and the on-disk
/// history is the fallback for a profile whose login could not be read or whose
/// endpoint has never once answered.
///
/// Far simpler than the Mac's monitor: DPAPI reads the key without a dialog, so
/// there is no prompting to schedule around. What it does carry over from the
/// Mac is the polling budget's two hard rules — a service Retry-After is the one
/// wait even a press cannot skip, and once the live endpoint has answered, the
/// last figure it gave stands in for a briefly-refusing one rather than a stale
/// sample off disk, so the two never disagree on screen.
/// </summary>
public sealed record UsageEntry
{
    public Usage? Usage { get; init; }
    public bool IsLive { get; init; }
    public string? Plan { get; init; }
    public bool HasUsage => Usage is not null;

    /// When the figure shown was fetched, or sampled off disk. Older than now
    /// whenever a refresh was refused and the last good figure stands in.
    public DateTimeOffset? UpdatedAt { get; init; }

    /// Why the last attempt to refresh failed, or null when it succeeded.
    public UsageRefusal? Refusal { get; init; }
}

public enum RefusalKind { RateLimited, LoginRefused, Failed, Unreachable, NoLogin }

public sealed record UsageRefusal
{
    public required RefusalKind Kind { get; init; }
    public int? Status { get; init; }
    /// When the next automatic attempt is allowed, if one is being held off.
    public DateTimeOffset? RetryAt { get; init; }

    public static RefusalKind KindOf(Exception e) => e switch
    {
        UsageApi.Failure { StatusCode: 429 } => RefusalKind.RateLimited,
        UsageApi.Failure { StatusCode: 401 or 403 } => RefusalKind.LoginRefused,
        UsageApi.Failure => RefusalKind.Failed,
        _ => RefusalKind.Unreachable,
    };
}

public static class UsageMonitor
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, ProfileState> States = new();

    private static readonly TimeSpan LiveTtl = TimeSpan.FromMinutes(5);
    /// How old a figure may be for a Claude in front, whose figure is the one
    /// being spent.
    public static readonly TimeSpan OpenProfileTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] BackoffSteps =
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
    };

    /// Everything a profile's live reads carry between calls. The last good
    /// reading is kept even as it ages, so a refusal shows it rather than the
    /// disk sample it would otherwise fall back to.
    private sealed class ProfileState
    {
        public UsageApi.Reading? LastLive;
        public DateTimeOffset LiveAt;
        public bool Invalidated;         // a press changed the figure; refetch before trusting the cache
        public DateTimeOffset BackoffUntil;
        public int Failures;
        public DateTimeOffset RetryUntil; // a service Retry-After — honoured even for a press
        public RefusalKind? LastRefusal;
        public int? LastStatus;
    }

    /// Raised with true as a live read goes out and false when it lands, from
    /// whichever thread made it, so a window can show a refresh it did not start.
    public static event Action<string, bool>? FetchingChanged;

    /// A disk sample read as a figure that is current now. Claude records the
    /// percentage while it runs but not when a window closes, so a sample that has
    /// outlived its five-hour window describes a window that has since rolled over
    /// — its percentage is spent, and reading it as the high-water mark it was is
    /// what left a long-closed profile showing 100% of a five hours that reset
    /// overnight. Past its window each figure reads as zero; the weekly one lasts
    /// seven days the same way.
    public static Usage? AsCurrentFigure(Usage? disk, DateTimeOffset now)
    {
        if (disk is null) return null;
        var age = now - disk.Sampled;
        // Past the week it has nothing left to say. Read as 0% it looked like a
        // fresh week, which is what a profile Claude stopped recording showed.
        if (age > TimeSpan.FromDays(7)) return null;
        var fiveHourElapsed = age > TimeSpan.FromHours(5);
        return disk with
        {
            FiveHour = fiveHourElapsed && disk.FiveHour is not null ? 0 : disk.FiveHour,
            FiveHourReset = fiveHourElapsed ? null : disk.FiveHourReset,
            Week = disk.Week,
        };
    }

    /// The pressed rung: a real call goes out no more than once every few seconds
    /// however hard the button is pressed. Wide enough to swallow a mashed refresh
    /// or the two reads a single open stacks — open the flyout and each row reads,
    /// then a press reads again — without asking the endpoint enough to earn its
    /// rate limit; a genuine change still gets through, since Invalidate overrides
    /// it. The Mac's pressed rung, at two seconds, is the same idea.
    public static readonly TimeSpan PressInterval = TimeSpan.FromSeconds(3);

    /// Whether a live call should go out, or the cached reading answers. Pure so
    /// the polling budget can be tested without a live endpoint.
    public static bool ShouldFetch(
        bool interactive, bool invalidated, bool justFetched, bool haveFresh, bool inBackoff, bool inRetry)
    {
        // The service asked us to wait; a person pressing refresh cannot skip it,
        // or a throttled endpoint is asked again on every press and stays throttled.
        if (inRetry) return false;
        // A figure a press has marked stale is fetched even so — a session was
        // started and the number really did change.
        if (invalidated) return true;
        // A call moments ago answers the next press from its result, so mashing
        // the button cannot walk the endpoint into a rate limit.
        if (justFetched) return false;
        if (interactive) return true;
        if (haveFresh) return false;
        if (inBackoff) return false;
        return true;
    }

    /// The reading for one profile: live if it can be had, otherwise the last
    /// live figure, otherwise disk. <paramref name="interactive"/> is a figure
    /// someone pressed for — it skips the freshness cache and the backoff, but
    /// never a service Retry-After.
    public static async Task<UsageEntry> ReadAsync(string profile, bool interactive = false, TimeSpan? freshFor = null)
    {
        if (Staging.For(profile)?.Usage is { } staged) return staged;
        var reading = await LiveAsync(profile, interactive, freshFor ?? LiveTtl).ConfigureAwait(false);
        var (refusal, liveAt) = Standing(profile);
        var local = Graft.UsageOf(profile);
        if (reading is null || PrefersLocal(liveAt, local?.Sampled))
        {
            var disk = AsCurrentFigure(local, DateTimeOffset.UtcNow);
            return new UsageEntry { Usage = disk, IsLive = false, UpdatedAt = disk?.Sampled, Refusal = refusal };
        }
        return LiveEntry(reading, liveAt, refusal, local?.Organization);
    }

    /// A running profile writes its own record; when that is newer than the last
    /// answer from the service it is the better figure, where a cached reading
    /// used to win regardless and left the bar behind what the profile had said.
    public static bool PrefersLocal(DateTimeOffset liveFetched, DateTimeOffset? localSampled) =>
        localSampled is { } sampled && sampled > liveFetched;

    /// The last live figure already in hand, with no read and no disk history, so
    /// a view can be built showing it rather than starting its bars from nothing.
    public static UsageEntry? Peek(string profile)
    {
        if (Staging.For(profile)?.Usage is { } staged) return staged;
        UsageApi.Reading? reading;
        lock (Lock) reading = State(profile).LastLive;
        if (reading is null) return null;
        var (refusal, liveAt) = Standing(profile);
        return LiveEntry(reading, liveAt, refusal, null);
    }

    private static (UsageRefusal? refusal, DateTimeOffset liveAt) Standing(string profile)
    {
        lock (Lock)
        {
            var state = State(profile);
            var retryAt = state.RetryUntil > state.BackoffUntil ? state.RetryUntil : state.BackoffUntil;
            var refusal = state.LastRefusal is RefusalKind kind
                ? new UsageRefusal
                {
                    Kind = kind, Status = state.LastStatus,
                    RetryAt = retryAt > DateTimeOffset.UtcNow ? retryAt : null,
                }
                : null;
            return (refusal, state.LiveAt);
        }
    }

    private static UsageEntry LiveEntry(UsageApi.Reading reading, DateTimeOffset liveAt, UsageRefusal? refusal, string? org) => new()
    {
        Usage = new Usage
        {
            FiveHour = reading.FiveHour,
            Week = reading.Week,
            Organization = org,
            Sampled = liveAt,
            FiveHourReset = reading.FiveHourReset,
            WeekReset = reading.WeekReset,
            Fable = reading.Fable,
            FableReset = reading.FableReset,
        },
        IsLive = true,
        Plan = reading.Plan,
        UpdatedAt = liveAt,
        Refusal = refusal,
    };

    /// Marks a profile's figure stale — after starting a session, say, which
    /// opens a window the cached reading predates. The next read must refetch,
    /// but the old reading is kept as the fallback so a refetch that the endpoint
    /// briefly refuses shows the last live figure rather than a stale disk one.
    public static void Invalidate(string profile)
    {
        lock (Lock) State(profile).Invalidated = true;
    }

    /// Until when a press for this profile would be held off, or null when one
    /// would go out.
    public static DateTimeOffset? HeldUntil(string profile)
    {
        lock (Lock)
        {
            var until = State(profile).RetryUntil;
            return until > DateTimeOffset.UtcNow ? until : null;
        }
    }

    /// A Retry-After the service means as a wait, or null to fall back on our own
    /// backoff. The endpoint answers 429 with Retry-After: 0, which honoured as
    /// written set no wait and no backoff, so every 30s tick asked again.
    public static TimeSpan? ServiceWait(TimeSpan? retryAfter) =>
        retryAfter is { } wait && wait > TimeSpan.Zero ? wait : null;

    private static ProfileState State(string profile)
    {
        if (!States.TryGetValue(profile, out var state))
        {
            States[profile] = state = new ProfileState();
            if (LoadSaved().TryGetValue(Path.GetFullPath(profile), out var saved))
            {
                state.LastLive = saved.Reading;
                state.LiveAt = saved.At;
            }
        }
        return state;
    }

    // MARK: - The last live figure, kept across restarts

    /// A restart otherwise begins with no live figure, and the first read after
    /// one is the likeliest to be refused, which fell back to a disk history
    /// Claude may not have written for weeks.
    public static string SavedPath => Path.Combine(GraftPaths.OwnData, "usage-cache.json");

    public sealed record Saved(UsageApi.Reading Reading, DateTimeOffset At);

    private static Dictionary<string, Saved> LoadSaved()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllBytes(SavedPath)) ?? new(); }
        catch { return new(); }
    }

    private static void Save(string profile, UsageApi.Reading reading, DateTimeOffset at)
    {
        try
        {
            var map = LoadSaved();
            map[Path.GetFullPath(profile)] = new Saved(reading, at);
            Directory.CreateDirectory(Path.GetDirectoryName(SavedPath)!);
            AtomicWrite.Bytes(SavedPath, JsonSerializer.SerializeToUtf8Bytes(map));
        }
        catch { }
    }

    private static async Task<UsageApi.Reading?> LiveAsync(string profile, bool interactive, TimeSpan freshFor)
    {
        var now = DateTimeOffset.UtcNow;
        lock (Lock)
        {
            var state = State(profile);
            var fetch = ShouldFetch(
                interactive,
                state.Invalidated,
                justFetched: state.LastLive is not null && now - state.LiveAt < PressInterval,
                haveFresh: state.LastLive is not null && now - state.LiveAt < freshFor,
                inBackoff: now < state.BackoffUntil,
                inRetry: now < state.RetryUntil);
            if (!fetch)
            {
                if (interactive && now < state.RetryUntil)
                    Diagnostics.Note("usage.pressHeld", new Dictionary<string, object?>
                    {
                        ["profile"] = profile,
                        ["retryUntil"] = state.RetryUntil,
                        ["showingFigureFrom"] = state.LastLive is null ? null : state.LiveAt,
                    });
                return state.LastLive;
            }
        }

        string token;
        try
        {
            if (ClaudeCredentials.GetToken(profile) is not ClaudeCredentials.Token t)
            {
                Diagnostics.Note("usage.noToken", new Dictionary<string, object?> { ["profile"] = profile });
                return Refused(profile, RefusalKind.NoLogin);
            }
            token = t.Value;
        }
        catch (ClaudeCredentials.CredentialException e)
        {
            Diagnostics.Note("usage.noToken", new Dictionary<string, object?>
            {
                ["profile"] = profile, ["reason"] = e.Reason.ToString(),
            });
            return Refused(profile, RefusalKind.NoLogin);
        }

        FetchingChanged?.Invoke(profile, true);
        try
        {
            var reading = await UsageApi.FetchAsync(token).ConfigureAwait(false);
            lock (Lock)
            {
                var state = State(profile);
                state.LastLive = reading;
                state.LiveAt = DateTimeOffset.UtcNow;
                state.Invalidated = false;
                state.BackoffUntil = default;
                state.RetryUntil = default;
                state.Failures = 0;
                state.LastRefusal = null;
                state.LastStatus = null;
                Save(profile, reading, state.LiveAt);
            }
            return reading;
        }
        catch (Exception e)
        {
            var retryAfter = ServiceWait((e as UsageApi.Failure)?.RetryAfter);
            lock (Lock)
            {
                var state = State(profile);
                state.LastRefusal = UsageRefusal.KindOf(e);
                state.LastStatus = (e as UsageApi.Failure)?.StatusCode;
                // A refused read shows the last figure as though it were current,
                // so this line is the only thing that says the bar has stopped moving.
                Diagnostics.Note("usage.fetchFailed", new Dictionary<string, object?>
                {
                    ["profile"] = profile,
                    ["interactive"] = interactive,
                    ["status"] = (e as UsageApi.Failure)?.StatusCode,
                    ["error"] = e is UsageApi.Failure ? null : e.GetType().Name + ": " + e.Message,
                    ["retryAfterSeconds"] = retryAfter?.TotalSeconds,
                    ["failures"] = state.Failures + (retryAfter is null ? 1 : 0),
                    ["showingFigureFrom"] = state.LastLive is null ? null : state.LiveAt,
                });
                if (retryAfter is TimeSpan ra)
                    // The endpoint named its own wait; honour it exactly, for a
                    // press as much as for a background tick.
                    state.RetryUntil = DateTimeOffset.UtcNow + ra;
                else
                {
                    state.Failures++;
                    state.BackoffUntil = DateTimeOffset.UtcNow
                        + BackoffSteps[Math.Min(state.Failures - 1, BackoffSteps.Length - 1)];
                    // A 429 is the service saying wait even when it names no wait,
                    // so a press is held too; six presses in a minute kept it refusing.
                    if (state.LastRefusal == RefusalKind.RateLimited) state.RetryUntil = state.BackoffUntil;
                }
                // A refused refetch leaves the figure marked stale, so the next
                // read tries again rather than trusting a reading a press dropped.
                return state.LastLive;
            }
        }
        finally { FetchingChanged?.Invoke(profile, false); }
    }

    private static UsageApi.Reading? Refused(string profile, RefusalKind kind)
    {
        lock (Lock)
        {
            var state = State(profile);
            state.LastRefusal = kind;
            state.LastStatus = null;
            return state.LastLive;
        }
    }
}
