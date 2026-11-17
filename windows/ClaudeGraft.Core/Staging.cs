using System.Collections.Concurrent;

namespace ClaudeGraft.Core;

/// A profile's look, as the debug window stages it. Each null leaves the real one showing.
public sealed record AccountStage
{
    public bool? SignedIn { get; init; }
    public bool Running { get; init; }
    public UsageEntry? Usage { get; init; }
    public bool Fetching { get; init; }
    public string? SidebarStatus { get; init; }
    public Graft.ChatsElsewhere? ChatsElsewhere { get; init; }
    public SharingStage? Sharing { get; init; }
}

/// A card's shared-usage line and the accounts listed under it.
public sealed record SharingStage(string Label, string? Note, bool Attention, IReadOnlyList<LenderStage> Lenders);

public sealed record LenderStage(string Name, string State, bool Attention, string Figures);

/// <summary>
/// What the debug window has staged, held in memory only so a quit or Reset
/// puts everything back. A stage only ever makes Graft show more or do less:
/// a Claude can be staged open but never closed, since closed is what lets a
/// delete or a sidebar sync go ahead.
/// </summary>
public static class Staging
{
    public static SetupState? Setup { get; set; }

    /// The shortcut list reads as unreadable, which refuses every save to it.
    public static bool ListUnreadable { get; set; }
    public static ClaudeLinkHandler? LinkHandler { get; set; }

    /// Thrown in place of taking the state lock, so every pass refuses as it would for real.
    public static Func<IOException>? StateFailure { get; set; }

    public static readonly ConcurrentDictionary<string, AccountStage> Accounts = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? Changed;

    public static bool Any => Setup is not null || ListUnreadable || LinkHandler is not null || StateFailure is not null || !Accounts.IsEmpty;

    public static AccountStage? For(string profile) => Accounts.TryGetValue(Path.GetFullPath(profile), out var stage) ? stage : null;

    public static void Stage(string profile, AccountStage? stage)
    {
        if (stage is null) Accounts.TryRemove(Path.GetFullPath(profile), out _);
        else Accounts[Path.GetFullPath(profile)] = stage;
    }

    public static void Announce() => Changed?.Invoke();

    public static void Clear()
    {
        Setup = null;
        ListUnreadable = false;
        LinkHandler = null;
        StateFailure = null;
        Accounts.Clear();
        Announce();
    }
}
