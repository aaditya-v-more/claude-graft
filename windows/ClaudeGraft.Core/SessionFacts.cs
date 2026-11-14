namespace ClaudeGraft.Core;

/// <summary>
/// What a transcript says about the session it holds. A record is what lists a
/// session; the transcript is the session. Everything a sidebar can show comes
/// out of the record, so every field of one has to be pulled out of the other.
///
/// A value type with structural equality, because the sweep writes down the
/// facts behind every record it files — which is how a later pass tells a
/// record still its own from one Claude has rewritten.
/// </summary>
public sealed record SessionFacts
{
    public required string CliSessionId { get; init; }
    public required IReadOnlyList<string> BridgeIds { get; init; }
    public required string OwnerAccount { get; init; }
    public required string OwnerOrganization { get; init; }
    public required string Title { get; init; }
    public required string Cwd { get; init; }
    /// Milliseconds since the epoch, which is what the records carry.
    public required double CreatedAt { get; init; }
    public required double LastActivityAt { get; init; }
    public required string Model { get; init; }
    public required string Effort { get; init; }
    public required string PermissionMode { get; init; }
    /// One per distinct prompt, which is as close to "turns" as a transcript
    /// gets. Claude's own records count only the turns that finished; the number
    /// is a badge, not a boundary.
    public required int Prompts { get; init; }
    public required IReadOnlyList<string> Branches { get; init; }

    public bool Equals(SessionFacts? other) =>
        other is not null
        && CliSessionId == other.CliSessionId
        && BridgeIds.SequenceEqual(other.BridgeIds)
        && OwnerAccount == other.OwnerAccount
        && OwnerOrganization == other.OwnerOrganization
        && Title == other.Title
        && Cwd == other.Cwd
        && CreatedAt == other.CreatedAt
        && LastActivityAt == other.LastActivityAt
        && Model == other.Model
        && Effort == other.Effort
        && PermissionMode == other.PermissionMode
        && Prompts == other.Prompts
        && Branches.SequenceEqual(other.Branches);

    public override int GetHashCode() =>
        HashCode.Combine(CliSessionId, OwnerAccount, OwnerOrganization, Title, CreatedAt, LastActivityAt, Prompts);
}
