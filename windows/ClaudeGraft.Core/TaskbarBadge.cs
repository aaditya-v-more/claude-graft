namespace ClaudeGraft.Core;

/// <summary>
/// What a profile's taskbar button and the strip's dot say about its conversation,
/// from where the turn stands and whether the person has watched it finish.
/// </summary>
public static class TaskbarBadge
{
    public enum Kind { None, NeedsPerson, Done }

    // A finished turn that was on screen with its Claude in front has nothing left to
    // tell; one waiting on the person is told until they answer it.
    public static Kind For(ChatFocus.Turn turn, bool watched) => turn switch
    {
        ChatFocus.Turn.NeedsPerson => Kind.NeedsPerson,
        ChatFocus.Turn.Done when !watched => Kind.Done,
        _ => Kind.None,
    };
}
