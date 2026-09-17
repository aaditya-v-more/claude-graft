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
}

public static class UsageMonitor
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, ProfileState> States = new();

    private static readonly TimeSpan LiveTtl = TimeSpan.FromMinutes(5);
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
    }

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
        var fiveHourElapsed = age > TimeSpan.FromHours(5);
        return disk with
        {
            FiveHour = fiveHourElapsed ? 0 : disk.FiveHour,
            FiveHourReset = fiveHourElapsed ? null : disk.FiveHourReset,
            Week = age > TimeSpan.FromDays(7) ? 0 : disk.Week,
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
    public static async Task<UsageEntry> ReadAsync(string profile, bool interactive = false)
    {
        var reading = await LiveAsync(profile, interactive).ConfigureAwait(false);
        if (reading is null)
        {
            // The endpoint has never answered for this profile, so there is no
            // live figure to stand on; the on-disk history is all there is.
            var disk = AsCurrentFigure(Graft.UsageOf(profile), DateTimeOffset.UtcNow);
            return new UsageEntry { Usage = disk, IsLive = false };
        }

        var org = Graft.UsageOf(profile)?.Organization;
        return new UsageEntry
        {
            Usage = new Usage
            {
                FiveHour = reading.FiveHour,
                Week = reading.Week,
                Organization = org,
                Sampled = DateTimeOffset.UtcNow,
                FiveHourReset = reading.FiveHourReset,
                WeekReset = reading.WeekReset,
                Fable = reading.Fable,
                FableReset = reading.FableReset,
            },
            IsLive = true,
            Plan = reading.Plan,
        };
    }

    /// Marks a profile's figure stale — after starting a session, say, which
    /// opens a window the cached reading predates. The next read must refetch,
    /// but the old reading is kept as the fallback so a refetch that the endpoint
    /// briefly refuses shows the last live figure rather than a stale disk one.
    public static void Invalidate(string profile)
    {
        lock (Lock) State(profile).Invalidated = true;
    }

    private static ProfileState State(string profile)
    {
        if (!States.TryGetValue(profile, out var state))
            States[profile] = state = new ProfileState();
        return state;
    }

    private static async Task<UsageApi.Reading?> LiveAsync(string profile, bool interactive)
    {
        var now = DateTimeOffset.UtcNow;
        lock (Lock)
        {
            var state = State(profile);
            var fetch = ShouldFetch(
                interactive,
                state.Invalidated,
                justFetched: state.LastLive is not null && now - state.LiveAt < PressInterval,
                haveFresh: state.LastLive is not null && now - state.LiveAt < LiveTtl,
                inBackoff: now < state.BackoffUntil,
                inRetry: now < state.RetryUntil);
            if (!fetch) return state.LastLive;
        }

        string token;
        try
        {
            if (ClaudeCredentials.GetToken(profile) is not ClaudeCredentials.Token t) return Cached(profile);
            token = t.Value;
        }
        catch (ClaudeCredentials.CredentialException) { return Cached(profile); }

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
            }
            return reading;
        }
        catch (Exception e)
        {
            var retryAfter = (e as UsageApi.Failure)?.RetryAfter;
            lock (Lock)
            {
                var state = State(profile);
                if (retryAfter is TimeSpan ra)
                    // The endpoint named its own wait; honour it exactly, for a
                    // press as much as for a background tick.
                    state.RetryUntil = DateTimeOffset.UtcNow + ra;
                else
                {
                    state.Failures++;
                    state.BackoffUntil = DateTimeOffset.UtcNow
                        + BackoffSteps[Math.Min(state.Failures - 1, BackoffSteps.Length - 1)];
                }
                // A refused refetch leaves the figure marked stale, so the next
                // read tries again rather than trusting a reading a press dropped.
                return state.LastLive;
            }
        }
    }

    private static UsageApi.Reading? Cached(string profile)
    {
        lock (Lock) return State(profile).LastLive;
    }
}
