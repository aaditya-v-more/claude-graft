namespace ClaudeGraft.Core;

/// <summary>
/// Whether a flip of a card's keep-window-open switch is a choice to save. The
/// binding sets the switch to its Claude's saved value, so that flip agrees with
/// it and saves nothing; any flip that disagrees is a choice, whether it came by
/// click, key or Narrator. Focus is no guide: a new card's switch has none on
/// its first click.
/// </summary>
public static class KeepWarmSwitch
{
    /// The value to save for the Claude the switch is bound to, or null when
    /// the switch only agrees with what that Claude already has.
    public static bool? ToSave(bool isOn, bool saved) => isOn == saved ? null : isOn;
}
