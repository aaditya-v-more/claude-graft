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
        if (fetching) return new Line(L10n.Text("Refreshing usage…"), false);
        if (entry is null) return null;

        var from = entry.UpdatedAt is { } at ? L10n.Format("Showing figures from {0}", Ago(at, now)) : null;
        var wait = entry.Refusal?.RetryAt is { } retry ? Graft.Countdown(retry, now) : null;

        if (entry.Refusal is { } refusal)
        {
            var why = refusal.Kind switch
            {
                RefusalKind.RateLimited => L10n.Text("Anthropic is limiting usage checks right now."),
                RefusalKind.LoginRefused => L10n.Text("This profile's login was refused. Open it once so Claude can renew it."),
                RefusalKind.NoLogin => L10n.Text("This profile's login could not be read, so usage cannot be checked."),
                RefusalKind.Unreachable => L10n.Text("Could not reach Anthropic to check usage."),
                _ => refusal.Status is int status
                    ? L10n.Format("Usage could not be refreshed (Anthropic answered {0}).", status)
                    : L10n.Text("Usage could not be refreshed."),
            };
            return new Line(from is null ? why
                : wait is null ? L10n.Format("{0} {1}.", why, from)
                : L10n.Format("{0} {1}, next try in {2}.", why, from, wait), true);
        }

        if (entry.UpdatedAt is not { } updated) return null;
        return entry.IsLive
            ? new Line(L10n.Format("Updated {0} from Anthropic", Ago(updated, now)), false)
            : new Line(L10n.Format("Updated {0} from this profile's own record", Ago(updated, now)), false);
    }

    public static string Ago(DateTimeOffset then, DateTimeOffset now) =>
        Graft.Countdown(now, then) is { } span && now - then >= TimeSpan.FromMinutes(1)
            ? L10n.Format("{0} ago", span)
            : L10n.Text("just now");

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
        if (busy) return new Button(L10n.Text("Refreshing…"), false);
        if (heldUntil is { } until && until > now)
        {
            var left = until - now;
            var wait = left < TimeSpan.FromMinutes(1)
                ? L10n.Format("{0}s", (int)Math.Ceiling(left.TotalSeconds))
                : Graft.Countdown(until, now) ?? L10n.Format("{0}m", 1);
            return new Button(L10n.Format("Available in {0}", wait), false);
        }
        return new Button(L10n.Text("Refresh Usage"), true);
    }
}
