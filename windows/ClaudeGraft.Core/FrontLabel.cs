namespace ClaudeGraft.Core;

/// <summary>
/// The tray's words for the Claude in front: its name, and the figures its
/// account is spending, short enough for a notification-area tooltip.
/// </summary>
public static class FrontLabel
{
    // Windows cuts a tray tooltip off past 127 characters.
    public const int MaxLength = 127;

    /// The conversation on screen goes on a line of its own, and is what gets
    /// cut when the whole will not fit.
    public static string Tooltip(string name, Usage? usage, string? chat = null, string? answeredBy = null)
    {
        // Answered by another account, the conversation says so, since the
        // figures above are this Claude's own and are not what it is spending.
        if (!string.IsNullOrWhiteSpace(chat) && answeredBy is not null) chat = $"{chat.Trim()} · answered by {answeredBy}";
        var text = usage is null
            ? name
            : name + " — " + (usage.FiveHour is int five ? $"5h {five}%" : "5h no limit") + $" · week {usage.Week}%";
        if (text.Length > MaxLength) return text[..(MaxLength - 1)] + "…";
        if (string.IsNullOrWhiteSpace(chat)) return text;
        var room = MaxLength - text.Length - 1;
        if (room < 4) return text;
        chat = chat.Trim().ReplaceLineEndings(" ");
        return text + "\n" + (chat.Length <= room ? chat : chat[..(room - 1)] + "…");
    }
}
