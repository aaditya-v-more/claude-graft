using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// A graft from end to end: two profiles merged into one history and then taken
/// apart again, each getting back exactly what it owned. The C# echo of the
/// Swift suite's "Grafting", "orphaned stash" and "going back" sections.
/// </summary>
[Collection("GlobalState")]
public sealed class GraftRoundTripTests : IDisposable
{
    private readonly TempDir _t = new();

    public GraftRoundTripTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        Graft.ResetCachesForTests();
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    private string MakeProfile(string name, string account, string org, params string[] chats)
    {
        var profile = Path.Combine(GraftPaths.ProfilesRoot, name);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "config.json"),
            $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
        foreach (var store in GraftPaths.ChatStoreNames)
        {
            var dir = Path.Combine(profile, store, account, org);
            Directory.CreateDirectory(dir);
            foreach (var chat in chats)
                File.WriteAllText(Path.Combine(dir, $"local_{chat}.json"), "{}");
        }
        return profile;
    }

    private static string OrgDir(string profile, string account, string org) =>
        Path.Combine(profile, "claude-code-sessions", account, org);

    private static List<string> ChatsVisible(string profile, string account, string org)
    {
        var dir = OrgDir(profile, account, org);
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir)
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith("local_") && n.EndsWith(".json"))
            .Select(n => n!["local_".Length..^".json".Length])
            .OrderBy(n => n).ToList()!;
    }

    private static bool HasChat(string profile, string account, string org, string chat) =>
        File.Exists(Path.Combine(OrgDir(profile, account, org), $"local_{chat}.json"));

    [Fact(DisplayName = "a cross-account graft merges both histories into each sidebar")]
    public void MergesHistories()
    {
        var main = MakeProfile("Claude", "AAAA", "ORG-A", "shared");
        var work = MakeProfile("Claude-Work", "BBBB", "ORG-B", "mine");

        Graft.GraftInto(main, work);

        // Both sidebars now hold the union of the two.
        Assert.Equal(new[] { "mine", "shared" }, ChatsVisible(work, "BBBB", "ORG-B"));
        Assert.Equal(new[] { "mine", "shared" }, ChatsVisible(main, "AAAA", "ORG-A"));
    }

    [Fact(DisplayName = "grafting a profile from itself changes nothing")]
    public void SelfGraftIsNoOp()
    {
        var profile = MakeProfile("Claude-Selfie", "AAAA", "ORG1", "one");
        File.WriteAllText(Path.Combine(profile, "window-state.json"), "kept");

        Graft.GraftInto(profile, profile);

        Assert.False(Junction.IsLink(Path.Combine(profile, "window-state.json")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(profile, "window-state.json")));
        Assert.True(HasChat(profile, "AAAA", "ORG1", "one"));
    }

    [Fact(DisplayName = "going back hands the profile its own chats and leaves what it merged in the source")]
    public void UngraftHandsBackOwn()
    {
        var main = MakeProfile("Claude", "AAAA", "ORG-A", "shared");
        var work = MakeProfile("Claude-Work", "BBBB", "ORG-B", "mine");
        Graft.GraftInto(main, work);

        // The profile writes a new chat while it is borrowing.
        File.WriteAllText(Path.Combine(OrgDir(work, "BBBB", "ORG-B"), "local_written_since.json"), "{}");

        Graft.Ungraft(work);

        // Its own history comes back, and the borrowed copies are gone.
        Assert.Equal(new[] { "mine" }, ChatsVisible(work, "BBBB", "ORG-B"));
        // The stash was folded back in, not abandoned beside the copies.
        Assert.False(Directory.Exists(
            Path.Combine(work, "claude-code-sessions", "BBBB", ".ORG-B.graft-own")));
        // What it wrote while borrowing went to the profile it borrowed from...
        Assert.True(HasChat(main, "AAAA", "ORG-A", "written_since"));
        // ...and the merge is one-way: what came into the source stays there.
        Assert.True(HasChat(main, "AAAA", "ORG-A", "mine"));
    }
}
