namespace ClaudeGraft.Core;

/// <summary>
/// The words a profile's card uses for its shared usage: what it draws on,
/// how each account it draws on stands, and the one note that most needs saying.
/// </summary>
public static class SharingText
{
    public const string Explanation =
        "The Code tab in this Claude can be answered by the other Claudes chosen here. Each request goes to " +
        "whichever has room and the soonest weekly reset, and a conversation stays on one account while its " +
        "cache is still warm there. Chats, artifacts, connectors and Claude in Chrome stay with this Claude's " +
        "own account, and the Chat tab is never shared.\n\n" +
        "A request answered by another account carries the conversation to that account. Anthropic's terms " +
        "for Claude plans may not allow one person to combine subscriptions this way, so read them before " +
        "turning this on.";

    public static string Label(IReadOnlyList<string> lenders, Func<string, string> nameOf) =>
        lenders.Count == 0 ? "Shared usage: off" : "Draws on " + string.Join(", ", lenders.Select(nameOf));

    /// How one lender stands, for its line in the menu.
    public static string Condition(string lender, SharedUsage.Result? result, bool sameAccount, DateTimeOffset now)
    {
        if (sameAccount) return "same account as this Claude, adds nothing";
        var reported = result?.Status?.Members.FirstOrDefault(m => m.Id == lender);
        if (reported?.TokenRefused == true) return "login refused · open that Claude to renew it";
        switch (result?.Lending.GetValueOrDefault(lender))
        {
            case null when result is null || !result.Lending.ContainsKey(lender): return "";
            case SharedUsage.Lending.SignedOut: return "not signed in";
            case SharedUsage.Lending.Expired: return "login expired · open that Claude to renew it";
            case SharedUsage.Lending.Unreadable: return "login unreadable";
        }
        if (reported?.Limits is { } limits && !PoolPolicy.HasRoom(limits, now))
            return (limits.RefusedUntil ?? limits.FiveHourReset) is DateTimeOffset until
                ? $"full until {until.ToLocalTime():t}"
                : "full";
        return reported is { Served: > 0 } ? $"ready · answered {reported.Served}" : "ready";
    }

    /// The most pressing thing to say under the card, if anything: something
    /// wrong first, then what this Claude is waiting on.
    public static (string Text, bool Attention)? Note(IReadOnlyList<string> lenders, ClaudeCodeEnvironment.State? environment, string? problem)
    {
        if (lenders.Count > 0 && problem is not null) return (problem, true);
        return environment switch
        {
            ClaudeCodeEnvironment.State.Conflict c => ($"Not shared: this Claude already sets {c.Name}", true),
            ClaudeCodeEnvironment.State.Unreadable when lenders.Count > 0 => ("Not shared: this Claude's Code environment is unreadable", true),
            ClaudeCodeEnvironment.State.Waiting => ("Starts the next time this Claude opens.", false),
            ClaudeCodeEnvironment.State.Leaving => ("Stops when this Claude next closes.", false),
            _ => null,
        };
    }
}
