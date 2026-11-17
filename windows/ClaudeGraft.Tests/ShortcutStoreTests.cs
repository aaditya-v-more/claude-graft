using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ShortcutStoreTests : IDisposable
{
    private readonly TempDir _t = new();

    public ShortcutStoreTests() => GraftPaths.ProfilesRootOverride = _t.Dir("root");

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    [Theory]
    [InlineData("Work Account", "Claude-Work-Account")]
    [InlineData("Claude 2", "Claude-2")]
    [InlineData("Claude", "Claude-Profile")]
    [InlineData("!!!", "Claude-Profile")]
    public void DerivesFolderFromName(string name, string expected) =>
        Assert.Equal(expected, Shortcut.FolderName(name));

    [Fact(DisplayName = "the store round-trips through its json file")]
    public void RoundTrips()
    {
        var store = new ShortcutStore();
        store.Add(Shortcut.New("Work", source: ShortcutSource.Own));

        var reloaded = new ShortcutStore();
        Assert.Single(reloaded.Shortcuts);
        Assert.Equal("Work", reloaded.Shortcuts[0].Name);
        Assert.Equal(SourceKind.Own, reloaded.Shortcuts[0].Source.Kind);
    }

    [Fact(DisplayName = "a source that would loop back is not offered")]
    public void NoSourceLoops()
    {
        var store = new ShortcutStore();
        var a = Shortcut.New("A", source: ShortcutSource.Main);
        var b = Shortcut.New("B", source: ShortcutSource.Of(a.Id));  // B reads from A
        store.Add(a);
        store.Add(b);

        // A may not read from B — that would be A -> B -> A.
        var sourcesForA = store.AvailableSources(a);
        Assert.DoesNotContain(sourcesForA, s => s.Kind == SourceKind.Shortcut && s.ShortcutId == b.Id);
        // own and main are always on offer.
        Assert.Contains(sourcesForA, s => s.Kind == SourceKind.Own);
        Assert.Contains(sourcesForA, s => s.Kind == SourceKind.Main);
    }

    [Fact(DisplayName = "deleting a source repoints the profiles that borrowed from it back to their own chats")]
    public void DeleteRepointsChildren()
    {
        var store = new ShortcutStore();
        var source = Shortcut.New("Source", source: ShortcutSource.Own);
        var borrower = Shortcut.New("Borrower", source: ShortcutSource.Of(source.Id));
        store.Add(source);
        store.Add(borrower);

        store.Delete(source.Id);

        Assert.Null(store.Get(source.Id));
        Assert.Equal(SourceKind.Own, store.Get(borrower.Id)!.Source.Kind);
    }

    [Fact(DisplayName = "the folder is kept when another shortcut still points at it")]
    public void KeepsSharedFolder()
    {
        var store = new ShortcutStore();
        var one = Shortcut.New("One", folder: "Claude-Shared", source: ShortcutSource.Own);
        var two = Shortcut.New("Two", folder: "Claude-Shared", source: ShortcutSource.Own);
        store.Add(one);
        store.Add(two);
        Directory.CreateDirectory(one.ProfileDir);

        var problem = store.Delete(one.Id, deletingProfile: true);
        Assert.NotNull(problem);   // refused: two still uses the folder
        Assert.True(Directory.Exists(one.ProfileDir));
    }

    [Fact(DisplayName = "unique names start numbering at two")]
    public void UniqueNames()
    {
        var store = new ShortcutStore();
        Assert.Equal("Claude 2", store.UniqueName());
        store.Add(Shortcut.New("Claude 2"));
        Assert.Equal("Claude 3", store.UniqueName());
    }
}
