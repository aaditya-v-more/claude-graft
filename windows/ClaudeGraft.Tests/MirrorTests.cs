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

    [Fact(DisplayName = "quiet passes write nothing: not a record, not the mirror's state")]
    public void QuietPassesWriteNothing()
    {
        var (one, other) = Pair();
        for (var i = 0; i < 20; i++) Rec(one, "q" + i, "{\"sessionId\":\"local_q" + i + "\",\"lastActivityAt\":1}");
        Graft.MirrorChatFolders(one, other);
        Graft.MirrorChatFolders(one, other);
        // Set well back, so a rewrite shows however soon it comes.
        var longAgo = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string[] Written() => [.. new[] { one, other, Graft.MirrorFieldsFolder }.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d)),
                               Path.Combine(GraftPaths.OwnData, "mirrored-chats.json")];
        foreach (var file in Written()) File.SetLastWriteTimeUtc(file, longAgo);
        for (var i = 0; i < 5; i++) Assert.Equal(0, Graft.MirrorChatFolders(one, other));
        Assert.All(Written(), file => Assert.Equal(longAgo, File.GetLastWriteTimeUtc(file)));
    }

    [Fact(DisplayName = "a quiet pass allocates about what reading its records does, never a parse of each")]
    public void QuietPassParsesNothing()
    {
        // Asking whether each record was a scratch chat once parsed every one on
        // every pass. Long lists of numbers cost far more to parse than to read.
        var (one, other) = Pair();
        var numbers = string.Join(",", Enumerable.Range(0, 30_000));
        for (var i = 0; i < 40; i++)
            Rec(other, "n" + i, "{\"sessionId\":\"local_n" + i + "\",\"cwd\":\"C:\\\\a\",\"originCwd\":\"C:\\\\a\",\"numbers\":[" + numbers + "]}");
        Graft.MirrorChatFolders(one, other);
        Graft.MirrorChatFolders(one, other);
        var files = new[] { one, other }.SelectMany(d => Directory.EnumerateFiles(d)).ToList();
        static long Allocated(Action work)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            work();
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        var reading = Allocated(() => { foreach (var file in files) Graft.Digest(File.ReadAllBytes(file)); });
        var moved = -1;
        var quiet = Allocated(() => moved = Graft.MirrorChatFolders(one, other));
        Assert.Equal(80, files.Count);
        Assert.Equal(0, moved);
        Assert.True(quiet < reading * 2, $"a quiet pass allocated {quiet} bytes, where reading its records takes {reading}");
    }
}
