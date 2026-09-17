using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// How much of a plan's two windows has been spent, as Claude records it in the
/// profile. Both are percentages already used; the resets are worked out from
/// the history, since Claude records the figure but not when a window closes.
/// </summary>
public sealed record Usage
{
    public required int FiveHour { get; init; }
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

            var samples = new List<(DateTimeOffset time, int fiveHour, int week, string? org)>();
            foreach (var s in raw.EnumerateArray())
            {
                if (!s.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                if (!s.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                if (!u.TryGetProperty("fh", out var fh) || fh.ValueKind != JsonValueKind.Number) continue;
                if (!u.TryGetProperty("sd", out var sd) || sd.ValueKind != JsonValueKind.Number) continue;
                var org = s.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
                samples.Add((DateTimeOffset.FromUnixTimeMilliseconds((long)t.GetDouble()),
                             fh.GetInt32(), sd.GetInt32(), org));
            }
            if (samples.Count == 0) return null;
            var latest = samples[^1];

            return new Usage
            {
                FiveHour = latest.fiveHour,
                Week = latest.week,
                Organization = latest.org,
                Sampled = latest.time,
                FiveHourReset = FiveHourResetOf(samples),
                WeekReset = WeekResetOf(samples),
            };
        }
        catch { return null; }
    }

    /// The window opened at the first sample after the figure was last zero, and
    /// closes five hours later. Nothing to report when none is open.
    private static DateTimeOffset? FiveHourResetOf(List<(DateTimeOffset time, int fiveHour, int week, string? org)> s)
    {
        if (s.Count == 0 || s[^1].fiveHour == 0) return null;
        for (var i = s.Count - 1; i > 0; i--)
            if (s[i - 1].fiveHour == 0 && s[i].fiveHour > 0)
            {
                var reset = s[i].time + FiveHourWindow;
                return reset > DateTimeOffset.UtcNow ? reset : null;
            }
        return null;
    }

    /// Weekly resets are a cycle, so the last one seen is rolled forward across
    /// stretches where Claude was not running to record it.
    private static DateTimeOffset? WeekResetOf(List<(DateTimeOffset time, int fiveHour, int week, string? org)> s)
    {
        DateTimeOffset? lastReset = null;
        for (var i = 1; i < s.Count; i++)
            if (s[i].week < s[i - 1].week - 2) lastReset = s[i].time;
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
