namespace ClaudeGraft.Core;

/// <summary>The flyout's line about sessions the last pass filed records for.</summary>
public static class RecoveredNotice
{
    public static string? Text(IReadOnlyList<string> titles) => titles.Count switch
    {
        0 => null,
        1 => $"Recovered “{titles[0]}”, which had closed without a record",
        _ => $"Recovered {titles.Count} sessions that closed without records",
    };
}
