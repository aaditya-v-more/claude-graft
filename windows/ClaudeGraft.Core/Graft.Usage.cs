using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// How much of a plan's two windows has been spent, as Claude records it in the
/// profile. Both are percentages already used; the resets are worked out from
/// the history, since Claude records the figure but not when a window closes.
/// </summary>
public sealed record Usage
{
    /// Null means this plan has no five-hour limit; zero is a limited idle plan.
    public required int? FiveHour { get; init; }
    public required int Week { get; init; }
    public string? Organization { get; init; }
    public required DateTimeOffset Sampled { get; init; }
    public DateTimeOffset? FiveHourReset { get; init; }
    public DateTimeOffset? WeekReset { get; init; }
    public int? Fable { get; init; }
    public DateTimeOffset? FableReset { get; init; }

    /// Claude only writes this while it runs, so an old sample says nothing
    /// useful about a five-hour window that has since rolled over.
    public bool IsStale => DateTimeOffset.UtcNow - Sampled > TimeSpan.FromHours(5);
}

public static partial class Graft
{
    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeekWindow = TimeSpan.FromDays(7);
    private sealed record UsageSample(DateTimeOffset Time, int? FiveHour, int Week, string? Organization);

    /// The most recent sample a profile recorded in plan-usage-history.json.
    /// Null when Claude has never run on it, or has not reported usage yet.
    public static Usage? UsageOf(string profile)
    {
        var path = Path.Combine(profile, "plan-usage-history.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("samples", out var raw) || raw.ValueKind != JsonValueKind.Array)
                return null;

            var samples = new List<UsageSample>();
            foreach (var s in raw.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object
                    || !s.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                if (!s.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                if (!u.TryGetProperty("sd", out var sd) || sd.ValueKind != JsonValueKind.Number
                    || !sd.TryGetInt32(out var week)) continue;
                int? fiveHour = u.TryGetProperty("fh", out var fh) && fh.ValueKind == JsonValueKind.Number
                    && fh.TryGetInt32(out var figure) ? figure : null;
                var org = s.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
                samples.Add(new UsageSample(DateTimeOffset.FromUnixTimeMilliseconds((long)t.GetDouble()),
                                            fiveHour, week, org));
            }
            if (samples.Count == 0) return null;
            var newest = samples[^1];

            if (HasNoFiveHourLimit(samples))
                return new Usage
                {
                    FiveHour = null,
                    Week = newest.Week,
                    Organization = newest.Organization,
                    Sampled = newest.Time,
                    WeekReset = WeekResetOf(samples),
                };

            // In a limited plan, a missing figure is a gap. Preserve the last
            // complete sample, including its date and week, rather than inventing
            // a zero or making the older reading appear freshly sampled.
            var complete = samples.Where(s => s.FiveHour is not null).ToList();
            var latest = complete.LastOrDefault(s => s.Organization == newest.Organization);
            if (latest is null) return null;

            return new Usage
            {
                FiveHour = latest.FiveHour,
                Week = latest.Week,
                Organization = latest.Organization,
                Sampled = latest.Time,
                FiveHourReset = FiveHourResetOf(complete),
                WeekReset = WeekResetOf(complete),
            };
        }
        catch { return null; }
    }

    // Match the Mac's conservative history classifier: consecutive weekly-only
    // samples, with no five-hour figure for that same organization in the last
    // five hours. An account's first ever sample can itself establish no limit.
    private static bool HasNoFiveHourLimit(List<UsageSample> samples)
    {
        var newest = samples[^1];
        if (newest.FiveHour is not null) return false;
        var own = samples.Where(s => s.Organization == newest.Organization).ToList();
        if (own.Count > 1 && own[^2].FiveHour is not null) return false;
        return !own.Any(s => s.FiveHour is not null && newest.Time - s.Time < FiveHourWindow);
    }

    /// The window opened at the first sample after the figure was last zero, and
    /// closes five hours later. Nothing to report when none is open.
    private static DateTimeOffset? FiveHourResetOf(List<UsageSample> s)
    {
        if (s.Count == 0 || s[^1].FiveHour is not > 0) return null;
        for (var i = s.Count - 1; i > 0; i--)
            if (s[i - 1].FiveHour == 0 && s[i].FiveHour > 0)
            {
                var reset = s[i].Time + FiveHourWindow;
                return reset > DateTimeOffset.UtcNow ? reset : null;
            }
        return null;
    }

    /// Weekly resets are a cycle, so the last one seen is rolled forward across
    /// stretches where Claude was not running to record it.
    private static DateTimeOffset? WeekResetOf(List<UsageSample> s)
    {
        DateTimeOffset? lastReset = null;
        for (var i = 1; i < s.Count; i++)
            if (s[i].Week < s[i - 1].Week - 2) lastReset = s[i].Time;
        if (lastReset is not DateTimeOffset seen) return null;
        var reset = seen + WeekWindow;
        var now = DateTimeOffset.UtcNow;
        var rolls = 0;
        while (reset <= now && rolls < 520) { reset += WeekWindow; rolls++; }
        return reset;
    }

    /// "2d 3h 40m", "3h 40m", "12m" — days only when there is at least one.
    public static string? Countdown(DateTimeOffset to, DateTimeOffset? fromOpt = null)
    {
        var now = fromOpt ?? DateTimeOffset.UtcNow;
        var remaining = (int)(to - now).TotalSeconds;
        if (remaining <= 0) return null;
        var days = remaining / 86_400;
        var hours = remaining % 86_400 / 3_600;
        var minutes = remaining % 3_600 / 60;
        if (days > 0) return $"{days}d {hours}h {minutes}m";
        if (hours > 0) return $"{hours}h {minutes}m";
        return $"{Math.Max(minutes, 1)}m";
    }
}
