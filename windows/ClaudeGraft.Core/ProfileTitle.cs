namespace ClaudeGraft.Core;

/// <summary>
/// What a shortcut's Claude window is called: Claude names every window the
/// same, which is what made two of them alike on the taskbar and in Alt+Tab.
/// </summary>
public static class ProfileTitle
{
    public const string Separator = " — ";

    public static string Compose(string own, string profileName)
    {
        var trimmed = own.Trim();
        // A title already carrying the name, from this Graft or one that ran before, is
        // not given it again: appended each time, it grew past what the window reads
        // back, never matched what was set, and was corrected forever.
        var suffix = Separator + profileName;
        while (trimmed.EndsWith(suffix, StringComparison.Ordinal)) trimmed = trimmed[..^suffix.Length].TrimEnd();
        // One grown long enough to be cut short no longer ends in the name; it is let go.
        if (trimmed == profileName || trimmed.Length > 200) trimmed = "";
        return trimmed.Length == 0 || trimmed.Equals("Claude", StringComparison.OrdinalIgnoreCase)
            ? profileName
            : trimmed + Separator + profileName;
    }
}
