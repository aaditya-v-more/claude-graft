using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// Where a shortcut's Claude Code chats come from.
[JsonConverter(typeof(JsonStringEnumConverter<SourceKind>))]
public enum SourceKind { Own, Main, Shortcut }

public sealed class ShortcutSource
{
    [JsonPropertyName("kind")] public SourceKind Kind { get; set; } = SourceKind.Main;
    /// Set only when Kind is Shortcut.
    [JsonPropertyName("id")] public Guid? ShortcutId { get; set; }

    public static ShortcutSource Own => new() { Kind = SourceKind.Own };
    public static ShortcutSource Main => new() { Kind = SourceKind.Main };
    public static ShortcutSource Of(Guid id) => new() { Kind = SourceKind.Shortcut, ShortcutId = id };

    public bool Equals(ShortcutSource? other) =>
        other is not null && Kind == other.Kind && ShortcutId == other.ShortcutId;
}

/// <summary>
/// One extra Claude Desktop shortcut: a name, a profile folder of its own, and
/// where its Claude Code chats come from.
/// </summary>
public sealed class Shortcut
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("source")] public ShortcutSource Source { get; set; } = ShortcutSource.Main;
    /// Absent from an older list reads as Claude's own, so no saved shortcut
    /// changes colour; only a new one is handed a colour.
    [JsonPropertyName("iconPreset")] public string IconPreset { get; set; } = Core.IconPreset.Original;
    /// Name the bundle was last installed under, so a rename can clean up.
    [JsonPropertyName("installedName")] public string? InstalledName { get; set; }

    /// Whether the app opens this account's five-hour window on its own when it
    /// finds it closed — the one thing it does off nobody's press. Off unless the
    /// owner turned it on, and absent from an older file reads as off.
    [JsonPropertyName("keepWarm")] public bool KeepWarm { get; set; }

    /// The account whose elsewhere-chats this shortcut has been told to stop
    /// asking about on Open. Keyed by account, since signing into a different one
    /// is a new question; absent from an older file reads as never asked. It
    /// silences the Open prompt, not the offer still shown in the window.
    [JsonPropertyName("stopAskingChatsFor")] public string? StopAskingChatsFor { get; set; }

    [JsonIgnore] public string ProfileDir => GraftPaths.Profile(Folder);

    /// Everything, so an edit that is cancelled or saved loses nothing it did
    /// not show.
    public Shortcut Copy() => (Shortcut)MemberwiseClone();

    public static Shortcut New(string name, string? folder = null, ShortcutSource? source = null) => new()
    {
        Name = name,
        Folder = folder ?? FolderName(name),
        Source = source ?? ShortcutSource.Main,
    };

    /// "Work Account" -> "Claude-Work-Account", "Claude 2" -> "Claude-2".
    public static string FolderName(string name)
    {
        var words = name.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(w => new string(w.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        if (words.Count > 0 && words[0].Equals("claude", StringComparison.OrdinalIgnoreCase))
            words.RemoveAt(0);
        var cleaned = string.Join("-", words);
        return "Claude-" + (cleaned.Length == 0 ? "Profile" : cleaned);
    }
}
