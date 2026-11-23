namespace ClaudeGraft.Core;

/// <summary>
/// Two Claudes on one chat store. Both write the same files, so the same
/// conversation open in two of them can drop messages, and every route that
/// opens a profile asks first, as on the Mac.
/// </summary>
public static class ChatConflict
{
    public static string Title => L10n.Text("Another Claude is open on these chats");

    public static string Message(IReadOnlyList<string> sharers) => sharers.Count == 1
        ? L10n.Format("{0} is already open on the same Claude Code chats.\n\nBoth write to the same chat files. Opening the same conversation in two of them at once can lose messages.", sharers[0])
        : L10n.Format("{0} are already open on the same Claude Code chats.\n\nBoth write to the same chat files. Opening the same conversation in two of them at once can lose messages.", L10n.List(sharers));

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
