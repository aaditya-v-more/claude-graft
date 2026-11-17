namespace ClaudeGraft.Core;

/// <summary>
/// The conversations the flyout lists, each with the account answering it and
/// the accounts it could be moved to. Only a Claude that draws on another
/// account has any, and the one on screen in the Claude in front comes first.
/// </summary>
public static class SharedChats
{
    public sealed record Chat
    {
        /// Claude Code's session id, which the relay keys conversations by.
        public required string Id { get; init; }
        public string? Title { get; init; }
        public required string Claude { get; init; }
        public string? ServedBy { get; init; }
        public string? Assigned { get; init; }
        /// The account chosen had no room, so another answered in its place.
        public bool PassedOver { get; init; }
        public DateTimeOffset LastAt { get; init; }
        /// The sender first, then what it may draw on.
        public IReadOnlyList<string> Choices { get; init; } = [];
    }

    /// What one record says about a conversation, found by its Claude Code session.
    public sealed record Record(string? Title, bool Archived);

    public static List<Chat> Build(RelayStatus? status, UsageSharing sharing, Func<string, Record?> record,
        string? frontClaude, string? onScreen)
    {
        if (status is null) return [];
        var chats = new List<Chat>();
        foreach (var session in status.Sessions)
        {
            if (sharing.Lenders.GetValueOrDefault(session.From) is not { Count: > 0 } lenders) continue;
            var found = record(session.Id);
            // Put away, it leaves the list as it leaves the sidebar.
            if (found?.Archived == true) continue;
            var assignment = sharing.Assignments.GetValueOrDefault(session.Id);
            var assigned = assignment?.To;
            chats.Add(new Chat
            {
                Id = session.Id,
                Title = found?.Title,
                Claude = session.From,
                ServedBy = session.ServedBy,
                Assigned = assigned,
                // An account already known to be full is never asked, so it is
                // in no list of those passed over; answered by another since it
                // was chosen says the same.
                PassedOver = assignment is not null && session.ServedBy is not null && session.ServedBy != assigned
                    && session.LastAt > assignment.At,
                LastAt = session.LastAt,
                Choices = lenders.Prepend(session.From).Distinct().ToList(),
            });
        }
        return chats
            .OrderByDescending(c => c.Claude == frontClaude && c.Id == onScreen)
            .ThenByDescending(c => c.Claude == frontClaude)
            .ThenByDescending(c => c.LastAt)
            .ToList();
    }

    /// Who answers it now: the account it was moved to, until one answers in
    /// its place for want of room, and otherwise the one that answered last.
    public static string? AnsweringKey(Chat chat) => chat.PassedOver ? chat.ServedBy : chat.Assigned ?? chat.ServedBy;

    /// Moving a conversation by hand; null leaves it to the ranking again.
    public static UsageSharing Assign(UsageSharing sharing, string session, string? to, DateTimeOffset now)
    {
        var assignments = new Dictionary<string, UsageSharing.Assignment>(sharing.Assignments);
        if (to is null) assignments.Remove(session);
        else assignments[session] = new UsageSharing.Assignment(to, now);
        // A choice outranks the ranking until a day of quiet, then is forgotten.
        foreach (var (id, a) in assignments.ToList())
            if (now - a.At > TimeSpan.FromDays(1) && id != session) assignments.Remove(id);
        return sharing with { Assignments = assignments };
    }

    public static UsageSharing SetLender(UsageSharing sharing, string claude, string lender, bool on)
    {
        var lenders = sharing.Lenders.ToDictionary(l => l.Key, l => l.Value.ToList());
        var list = lenders.TryGetValue(claude, out var l) ? l : lenders[claude] = new();
        list.Remove(lender);
        if (on && lender != claude) list.Add(lender);
        if (list.Count == 0) lenders.Remove(claude);
        return sharing with { Lenders = lenders };
    }
}
