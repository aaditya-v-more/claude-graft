using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class MirrorDecisionTests
{
    // The whole truth table, one row per branch — the Swift note is explicit
    // that getting one backwards deletes a new chat or resurrects a deleted one.
    [Theory]
    // one == other: nothing to do
    [InlineData("a", "a", null, MirrorAction.Nothing)]
    [InlineData(null, null, "a", MirrorAction.Nothing)]
    // one present, other absent
    [InlineData("a", null, null, MirrorAction.CopyToOther)]   // new on one
    [InlineData("a", null, "a", MirrorAction.RemoveFromOne)]  // deleted from other
    [InlineData("a", null, "b", MirrorAction.Conflict)]       // one changed, other deleted
    // other present, one absent (mirror image)
    [InlineData(null, "a", null, MirrorAction.CopyToOne)]
    [InlineData(null, "a", "a", MirrorAction.RemoveFromOther)]
    [InlineData(null, "a", "b", MirrorAction.Conflict)]
    // both present and different
    [InlineData("a", "b", "a", MirrorAction.CopyToOne)]       // other is the change
    [InlineData("a", "b", "b", MirrorAction.CopyToOther)]     // one is the change
    [InlineData("a", "b", "c", MirrorAction.Conflict)]        // both changed
    [InlineData("a", "b", null, MirrorAction.Conflict)]       // both new, no baseline
    public void Decides(string? one, string? other, string? baseline, MirrorAction expected) =>
        Assert.Equal(expected, Graft.MirrorDecision(one, other, baseline));

    [Fact(DisplayName = "only records and deletion markers are a mirror pass's concern")]
    public void MirroredNames()
    {
        Assert.True(Graft.IsMirrored("local_abc.json"));
        Assert.True(Graft.IsMirrored("deleted_abc"));
        Assert.False(Graft.IsMirrored("config.json"));
        Assert.False(Graft.IsMirrored("some-other-file"));
    }
}

[Collection("GlobalState")]
public sealed class MirrorFoldersTests : IDisposable
{
    private readonly TempDir _t = new();

    public MirrorFoldersTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");   // where the state file lives
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    private (string one, string other) Pair()
    {
        return (_t.Dir("one"), _t.Dir("two"));
    }

    private static void Rec(string folder, string name, string body) =>
        File.WriteAllText(Path.Combine(folder, "local_" + name + ".json"), body);

    private static bool Has(string folder, string name) =>
        File.Exists(Path.Combine(folder, "local_" + name + ".json"));

    [Fact(DisplayName = "a record on one side is copied to the other on the first pass")]
    public void CopiesNew()
    {
        var (one, other) = Pair();
        Rec(one, "a", "{\"lastActivityAt\":1}");
        Assert.Equal(1, Graft.MirrorChatFolders(one, other));
        Assert.True(Has(other, "a"));
    }

    [Fact(DisplayName = "a record deleted beside one that survives is carried across as a deletion")]
    public void CarriesDeletion()
    {
        // Two records, so deleting one leaves the side still holding the other —
        // which is a chat cleared by hand, not a folder emptied wholesale.
        var (one, other) = Pair();
        Rec(one, "a", "{\"lastActivityAt\":1}");
        Rec(one, "b", "{\"lastActivityAt\":2}");
        Graft.MirrorChatFolders(one, other);      // pass 1: both hold both, baseline earned
        Assert.True(Has(other, "a") && Has(other, "b"));

        File.Delete(Path.Combine(one, "local_a.json"));   // a deleted from one, b remains
        Graft.MirrorChatFolders(one, other);      // pass 2: a gone from the other, b kept
        Assert.False(Has(other, "a"));
        Assert.True(Has(other, "b"));
    }

    [Fact(DisplayName = "a side emptied wholesale refills from the other rather than emptying it")]
    public void WholesaleEmptyRefills()
    {
        var (one, other) = Pair();
        Rec(one, "a", "{\"lastActivityAt\":1}");
        Rec(one, "b", "{\"lastActivityAt\":2}");
        Graft.MirrorChatFolders(one, other);      // baseline now describes a and b
        Assert.True(Has(other, "a") && Has(other, "b"));

        // one is emptied all at once — a sign-in moved it, a graft stashed it.
        File.Delete(Path.Combine(one, "local_a.json"));
        File.Delete(Path.Combine(one, "local_b.json"));

        Graft.MirrorChatFolders(one, other);
        // The other side keeps its history, and the emptied side is refilled.
        Assert.True(Has(other, "a") && Has(other, "b"));
        Assert.True(Has(one, "a") && Has(one, "b"));
    }

    [Fact(DisplayName = "when both sides changed a record, the one that still exists wins")]
    public void ConflictKeepsSurvivor()
    {
        var (one, other) = Pair();
        Rec(one, "a", "{\"lastActivityAt\":1}");
        Graft.MirrorChatFolders(one, other);      // baseline earned for a

        // both rewrite a differently; one later, so its bytes win
        Rec(one, "a", "{\"lastActivityAt\":100}");
        Rec(other, "a", "{\"lastActivityAt\":50}");
        Graft.MirrorChatFolders(one, other);
        Assert.Equal("{\"lastActivityAt\":100}", File.ReadAllText(Path.Combine(other, "local_a.json")));
    }

    [Fact(DisplayName = "a pass over a folder whose parent is gone does nothing")]
    public void MissingParentIsNoOp()
    {
        var one = _t.Dir("present");
        Rec(one, "a", "{}");
        var other = Path.Combine(_t.Path, "no-such-parent", "org");   // parent absent
        Assert.Equal(0, Graft.MirrorChatFolders(one, other));
        Assert.False(Directory.Exists(other));
    }
}
