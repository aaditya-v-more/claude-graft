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
    [JsonPropertyName("iconPreset")] public string IconPreset { get; set; } = "Original";
    /// Name the bundle was last installed under, so a rename can clean up.
    [JsonPropertyName("installedName")] public string? InstalledName { get; set; }

    [JsonIgnore] public string ProfileDir => GraftPaths.Profile(Folder);

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
