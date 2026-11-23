namespace ClaudeGraft.Core;

/// <summary>The flyout's line about sessions the last pass filed records for.</summary>
public static class RecoveredNotice
{
    public static string? Text(IReadOnlyList<string> titles) => titles.Count switch
    {
        0 => null,
        1 => L10n.Format("Recovered “{0}”, which had closed without a record", titles[0]),
        _ => L10n.Plural(titles.Count, "Recovered {0} session that closed without a record", "Recovered {0} sessions that closed without records"),
    };
}
