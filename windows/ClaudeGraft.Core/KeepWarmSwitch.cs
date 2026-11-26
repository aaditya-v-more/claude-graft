namespace ClaudeGraft.Core;

/// <summary>
/// Whether a flip of a card's keep-window-open switch is a person's choice to
/// save. The card's switch is reused from one Claude to the next, and its
/// binding sets it to each Claude's saved value as it goes; that flip always
/// matches the Claude it was bound from, so only a flip that differs from it is
/// a choice. Event order, focus and input kind do not enter into it.
/// </summary>
public static class KeepWarmSwitch
{
    /// The value to save for the Claude the switch is bound to, or null when
    /// the switch only agrees with what that Claude already has.
    public static bool? ToSave(bool isOn, bool saved) => isOn == saved ? null : isOn;
}
