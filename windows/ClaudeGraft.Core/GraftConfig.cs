using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// <summary>
/// Describes one grafted Claude Desktop profile: where its data lives, and
/// which other profile — if any — it borrows its Claude Code chats from.
/// </summary>
public sealed class GraftConfig
{
    /// Absolute path of the --user-data-dir this shortcut launches with.
    [JsonPropertyName("profileDir")]
    public string ProfileDir { get; set; } = "";

    /// Absolute path of the profile to inherit from. null keeps chats separate.
    ///
    /// A profile borrowing another's chats always gets its own copy of them.
    /// Bundles written by earlier versions carry a mirrorChats key saying
    /// whether they wanted one; decoding ignores what it does not know, so an
    /// old bundle migrates the next time its launcher runs and no bundle has to
    /// be rewritten for it to happen.
    [JsonPropertyName("sourceDir")]
    public string? SourceDir { get; set; }
}
