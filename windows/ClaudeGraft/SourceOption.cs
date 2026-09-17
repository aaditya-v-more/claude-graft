using ClaudeGraft.Core;

namespace ClaudeGraft;

public sealed class SourceOption
{
    public required string Label { get; init; }
    public required ShortcutSource Source { get; init; }
}
