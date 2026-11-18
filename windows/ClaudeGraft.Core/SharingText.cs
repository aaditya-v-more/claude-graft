namespace ClaudeGraft.Core;

/// <summary>
/// The words a profile's card uses for its shared usage: what it draws on,
/// how each account it draws on stands, and the one note that most needs saying.
/// </summary>
public static class SharingText
{
    public static string Explanation => L10n.Text(
        "The Code tab in this Claude can be answered by the other Claudes chosen here. Each request goes to whichever has room and the soonest weekly reset, and a conversation stays on one account while its cache is still warm there. Chats, artifacts, connectors and Claude in Chrome stay with this Claude's own account, and the Chat tab is never shared.\n\nA request answered by another account carries the conversation to that account. Anthropic's terms for Claude plans may not allow one person to combine subscriptions this way, so read them before turning this on.");

    public static string Label(IReadOnlyList<string> lenders, Func<string, string> nameOf) =>
        lenders.Count == 0 ? L10n.Text("Shared usage: off") : L10n.Format("Draws on {0}", string.Join(", ", lenders.Select(nameOf)));

    /// How one lender stands, for its line in the menu.
    public static string Condition(string lender, SharedUsage.Result? result, bool sameAccount, DateTimeOffset now) =>
        Standing(lender, result, sameAccount, now).Text;

    /// Whether that standing is one a person has to do something about.
    public static bool Troubled(string lender, SharedUsage.Result? result, bool sameAccount, DateTimeOffset now) =>
        Standing(lender, result, sameAccount, now).Trouble;

    private static (string Text, bool Trouble) Standing(string lender, SharedUsage.Result? result, bool sameAccount, DateTimeOffset now)
    {
        if (sameAccount) return (L10n.Text("same account as this Claude, adds nothing"), true);
        var reported = result?.Status?.Members.FirstOrDefault(m => m.Id == lender);
        if (reported?.TokenRefused == true) return (L10n.Text("login refused · open that Claude to renew it"), true);
        switch (result?.Lending.GetValueOrDefault(lender))
        {
            case null when result is null || !result.Lending.ContainsKey(lender): return ("", false);
            case SharedUsage.Lending.SignedOut: return (L10n.Text("not signed in"), true);
            case SharedUsage.Lending.Expired: return (L10n.Text("login expired · open that Claude to renew it"), true);
            case SharedUsage.Lending.Unreadable: return (L10n.Text("login unreadable"), true);
        }
        if (reported?.Limits is { } limits && !PoolPolicy.HasRoom(limits, now))
            return ((limits.RefusedUntil ?? limits.FiveHourReset) is DateTimeOffset until
                ? L10n.Format("full until {0:t}", until.ToLocalTime())
                : L10n.Text("full"), false);
        return (reported is { Served: > 0 } ? L10n.Format("ready · answered {0}", reported.Served) : L10n.Text("ready"), false);
    }

    /// The most pressing thing to say under the card, if anything: something
    /// wrong first, then what this Claude is waiting on.
    public static (string Text, bool Attention)? Note(IReadOnlyList<string> lenders, ClaudeCodeEnvironment.State? environment, string? problem)
    {
        if (lenders.Count > 0 && problem is not null) return (problem, true);
        return environment switch
        {
            ClaudeCodeEnvironment.State.Conflict c => (L10n.Format("Not shared: this Claude already sets {0}", c.Name), true),
            ClaudeCodeEnvironment.State.Unreadable when lenders.Count > 0 => (L10n.Text("Not shared: this Claude's Code environment is unreadable"), true),
            ClaudeCodeEnvironment.State.Waiting => (L10n.Text("Starts the next time this Claude opens."), false),
            ClaudeCodeEnvironment.State.Leaving => (L10n.Text("Stops when this Claude next closes."), false),
            _ => null,
        };
    }
}
