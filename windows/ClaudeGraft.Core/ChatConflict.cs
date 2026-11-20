namespace ClaudeGraft.Core;

/// <summary>
/// Two Claudes on one chat store. Both write the same files, so the same
/// conversation open in two of them can drop messages, and every route that
/// opens a profile asks first, as on the Mac.
/// </summary>
public static class ChatConflict
{
    public const string Title = "Another Claude is open on these chats";

    public static string Message(IReadOnlyList<string> sharers)
    {
        var list = sharers.Count switch
        {
            0 => "",
            1 => sharers[0],
            2 => sharers[0] + " and " + sharers[1],
            _ => string.Join(", ", sharers.Take(sharers.Count - 1)) + " and " + sharers[^1],
        };
        var verb = sharers.Count == 1 ? "is" : "are";
        return $"{list} {verb} already open on the same Claude Code chats.\n\n"
             + "Both write to the same chat files. Opening the same conversation in two of them at once can lose messages.";
    }

    /// Who among the neighbours is running. One process query each, so never on the UI thread.
    public static IReadOnlyList<string> OpenSharers(string profile, IReadOnlyList<(string Name, string Profile)> neighbours,
                                                    Func<string, bool>? isRunning = null)
    {
        if (neighbours.Count == 0) return [];
        if (isRunning is null)
        {
            var processes = ClaudeProcesses.Enumerate();
            isRunning = p => ClaudeProcesses.IsRunning(p, processes);
        }
        return SharersToAskAbout(isRunning(profile), neighbours.Where(n => isRunning(n.Profile)).Select(n => n.Name).ToList());
    }

    /// A profile already open is only being brought forward, and it has been
    /// reading those chats all along, so there is nothing to ask.
    public static IReadOnlyList<string> SharersToAskAbout(bool profileIsOpen, IReadOnlyList<string> openNeighbours) =>
        profileIsOpen ? [] : openNeighbours;
}
