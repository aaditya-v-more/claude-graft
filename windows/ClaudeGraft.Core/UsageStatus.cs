namespace ClaudeGraft.Core;

/// <summary>
/// The line under a profile's usage bars: how old the figure is, and when a
/// refresh was refused, why and until when. A refused read keeps showing the
/// last good figure, so without this line a stuck number looks like a current one.
/// </summary>
public static class UsageStatus
{
    public sealed record Line(string Text, bool IsWarning);

    public static Line? Describe(UsageEntry? entry, bool fetching, DateTimeOffset now)
    {
        if (fetching) return new Line("Refreshing usage…", false);
        if (entry is null) return null;

        var from = entry.UpdatedAt is { } at ? $"Showing figures from {Ago(at, now)}" : null;
        var next = entry.Refusal?.RetryAt is { } retry && Graft.Countdown(retry, now) is { } wait
            ? $", next try in {wait}"
            : "";

        if (entry.Refusal is { } refusal)
        {
            var why = refusal.Kind switch
            {
                RefusalKind.RateLimited => "Anthropic is limiting usage checks right now.",
                RefusalKind.LoginRefused => "This profile's login was refused. Open it once so Claude can renew it.",
                RefusalKind.NoLogin => "This profile's login could not be read, so usage cannot be checked.",
                RefusalKind.Unreachable => "Could not reach Anthropic to check usage.",
                _ => refusal.Status is int status
                    ? $"Usage could not be refreshed (Anthropic answered {status})."
                    : "Usage could not be refreshed.",
            };
            return new Line(from is null ? why : $"{why} {from}{next}.", true);
        }

        if (entry.UpdatedAt is not { } updated) return null;
        return entry.IsLive
            ? new Line($"Updated {Ago(updated, now)} from Anthropic", false)
            : new Line($"Updated {Ago(updated, now)} from this profile's own record", false);
    }

    public static string Ago(DateTimeOffset then, DateTimeOffset now) =>
        Graft.Countdown(now, then) is { } span && now - then >= TimeSpan.FromMinutes(1)
            ? span + " ago"
            : "just now";

    /// A window whose reset time has passed has closed, and a closed window has
    /// spent nothing yet. The reset is known exactly, so this needs no request.
    public static Usage Rolled(Usage usage, DateTimeOffset now) => usage with
    {
        FiveHour = usage.FiveHourReset is { } h && h <= now && usage.FiveHour is not null ? 0 : usage.FiveHour,
        FiveHourReset = usage.FiveHourReset is { } h2 && h2 <= now ? null : usage.FiveHourReset,
        Week = usage.WeekReset is { } w && w <= now ? 0 : usage.Week,
        WeekReset = usage.WeekReset is { } w2 && w2 <= now ? null : usage.WeekReset,
        Fable = usage.FableReset is { } f && f <= now ? 0 : usage.Fable,
        FableReset = usage.FableReset is { } f2 && f2 <= now ? null : usage.FableReset,
    };

    /// Two missed polls is where a figure stops being one to read at face value.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public static bool IsStale(UsageEntry? entry, DateTimeOffset now) =>
        entry is not null && (entry.Refusal is not null || !entry.IsLive
            || entry.UpdatedAt is not { } at || now - at > StaleAfter);

    public sealed record Button(string Text, bool Enabled);

    /// The Refresh button, which should never be pressable while a press would do
    /// nothing: every account held off by a rate limit, or a refresh already out.
    public static Button RefreshButton(bool busy, DateTimeOffset? heldUntil, DateTimeOffset now)
    {
        if (busy) return new Button("Refreshing…", false);
        if (heldUntil is { } until && until > now)
        {
            var left = until - now;
            var wait = left < TimeSpan.FromMinutes(1)
                ? $"{(int)Math.Ceiling(left.TotalSeconds)}s"
                : Graft.Countdown(until, now) ?? "1m";
            return new Button($"Available in {wait}", false);
        }
        return new Button("Refresh Usage", true);
    }
}
