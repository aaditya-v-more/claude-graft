using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// Changing a profile's folder moves its contents to the new name rather than
/// leaving them behind, and carries the junctions, stashes and path-keyed state
/// along so a mirrored history is not misread as a first pass at its new home.
/// </summary>
[Collection("GlobalState")]
public sealed class ProfileMoveTests : IDisposable
{
    private readonly TempDir _t = new();

    public ProfileMoveTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        Graft.ResetCachesForTests();
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    [Fact(DisplayName = "the chats and login follow the folder to its new name")]
    public void ContentsFollow()
    {
        var from = GraftPaths.Profile("work");
        Directory.CreateDirectory(Path.Combine(from, "claude-code-sessions", "acct", "org"));
        File.WriteAllText(Path.Combine(from, "config.json"), "{\"lastKnownAccountUuid\":\"acct\"}");
        File.WriteAllText(Path.Combine(from, "claude-code-sessions", "acct", "org", "local_a.json"), "{}");

        Assert.Equal(Graft.ProfileMove.Moved, Graft.MoveProfileFolder("work", "research"));

        var to = GraftPaths.Profile("research");
        Assert.False(Directory.Exists(from), "the old folder is gone");
        Assert.True(File.Exists(Path.Combine(to, "config.json")), "the login moved");
        Assert.True(File.Exists(Path.Combine(to, "claude-code-sessions", "acct", "org", "local_a.json")),
            "the chats moved");
    }

    [Fact(DisplayName = "a graft junction inside the profile survives the move, still pointing at its source")]
    public void JunctionSurvives()
    {
        var source = GraftPaths.Profile("source");
        var target = Path.Combine(source, "claude-code-sessions", "acct", "org");
        Directory.CreateDirectory(target);

        var from = GraftPaths.Profile("work");
        var link = Path.Combine(from, "claude-code-sessions", "acct", "org");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Junction.Create(link, target);

        Assert.Equal(Graft.ProfileMove.Moved, Graft.MoveProfileFolder("work", "research"));

        var moved = Path.Combine(GraftPaths.Profile("research"), "claude-code-sessions", "acct", "org");
        Assert.True(Junction.IsLink(moved), "it is still a junction");
        Assert.True(Fs.SamePath(Junction.Target(moved)!, target), "and still points at the source");
    }

    [Fact(DisplayName = "a mirror baseline is rewritten so the new home is not read as a first pass")]
    public void MirrorBaselineFollows()
    {
        var from = GraftPaths.Profile("work");
        var source = GraftPaths.Profile("source");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(source);

        var state = Graft.LoadMirrorState();
        state.Pairs[Graft.PairKey(from, source)] = new() { ["local_a.json"] = "deadbeef" };
        Graft.SaveMirrorState(state);

        Assert.Equal(Graft.ProfileMove.Moved, Graft.MoveProfileFolder("work", "research"));

        var to = GraftPaths.Profile("research");
        // The pass that follows asks this: a profile it borrows through is not new.
        Assert.NotEmpty(Graft.MirrorPairsBorrowedBy(to));
        Assert.Empty(Graft.MirrorPairsBorrowedBy(from));
    }

    [Fact(DisplayName = "moving onto a folder already in use is refused, and changes nothing")]
    public void TargetInUseRefused()
    {
        var from = GraftPaths.Profile("work");
        Directory.CreateDirectory(from);
        File.WriteAllText(Path.Combine(from, "config.json"), "{}");
        var occupied = GraftPaths.Profile("research");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "someone-elses.json"), "{}");

        Assert.Equal(Graft.ProfileMove.TargetExists, Graft.MoveProfileFolder("work", "research"));
        Assert.True(File.Exists(Path.Combine(from, "config.json")), "the source is left where it was");
        Assert.True(File.Exists(Path.Combine(occupied, "someone-elses.json")), "the target is untouched");
    }

    [Fact(DisplayName = "a profile with nothing at the old name is left for the graft to create")]
    public void NothingToMove()
    {
        Assert.Equal(Graft.ProfileMove.NothingToMove, Graft.MoveProfileFolder("work", "research"));
        Assert.False(Directory.Exists(GraftPaths.Profile("research")));
    }
}
