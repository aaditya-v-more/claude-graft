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

    [Fact(DisplayName = "a name whose folder a renamed Claude kept, or a profile still on disk holds, is not offered to a new one")]
    public void NamesPassOverFoldersInUse()
    {
        var store = new ShortcutStore();
        var renamed = Shortcut.New("Claude 2");
        store.Add(renamed);
        renamed.Name = "Work";
        store.Update(renamed);
        Assert.Equal("Claude 3", store.UniqueName());

        Directory.CreateDirectory(GraftPaths.Profile("Claude-3"));
        Assert.Equal("Claude 4", store.UniqueName());

        store.Add(Shortcut.New("claude 4", folder: "Elsewhere"));
        Assert.Equal("Claude 5", store.UniqueName());
    }

    [Fact(DisplayName = "the folder a name suggests steps past one another Claude has or one on disk, but keeps a Claude's own")]
    public void SuggestedFolders()
    {
        var store = new ShortcutStore();
        var holder = Shortcut.New("Holder", folder: "Claude-Profile");
        store.Add(holder);
        var draft = Guid.NewGuid();
        Assert.Equal("Claude-Work", store.SuggestedFolder("Work", draft));
        Assert.Equal("Claude-Profile-2", store.SuggestedFolder("", draft));
        Assert.Equal("Claude-profile-2", store.SuggestedFolder("profile", draft));
        Directory.CreateDirectory(GraftPaths.Profile("Claude-Profile-2"));
        Assert.Equal("Claude-Profile-3", store.SuggestedFolder("", draft));
        Assert.Equal("Claude-Profile", store.SuggestedFolder("", holder.Id));
    }

    [Fact(DisplayName = "a new Claude is refused a folder or a name another Claude has, while one already sharing can still be saved")]
    public void Conflicts()
    {
        var store = new ShortcutStore();
        var holder = Shortcut.New("Claude 2");
        store.Add(holder);

        var onItsFolder = Shortcut.New("Fresh", folder: "claude-2");
        Assert.Contains("“Claude 2” already uses the folder", store.Conflict(onItsFolder, null));
        Assert.Contains("already called “Claude 2”", store.Conflict(Shortcut.New("claude 2 ", folder: "Claude-Other"), null));
        Assert.Null(store.Conflict(Shortcut.New("Fresh"), null));

        var mover = Shortcut.New("Mover");
        store.Add(mover);
        var moved = mover.Copy();
        moved.Folder = "Claude-2";
        Assert.NotNull(store.Conflict(moved, mover));

        // An older version could leave two on one folder; the one there stays editable.
        var stale = Shortcut.New("Stale", folder: "Claude-2");
        store.Add(stale);
        var renamed = stale.Copy();
        renamed.Name = "Stale renamed";
        Assert.Null(store.Conflict(renamed, stale));
    }

    [Fact(DisplayName = "removing an open Claude together with its chats and login is refused before anything goes, while removing it alone is not")]
    public void OpenClaudeKeepsItsShortcut()
    {
        var store = new ShortcutStore();
        var open = Shortcut.New("Open One");
        store.Add(open);
        Assert.Contains("still open, so nothing was removed", store.DeletionRefusal(open, deletingProfile: true, isOpen: true));
        Assert.Null(store.DeletionRefusal(open, deletingProfile: false, isOpen: true));
        Assert.Null(store.DeletionRefusal(open, deletingProfile: true, isOpen: false));
        store.Add(Shortcut.New("Twin", folder: "claude-open-one"));
        Assert.Null(store.DeletionRefusal(open, deletingProfile: true, isOpen: true));

        var page = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!, "..", "ClaudeGraft", "MainPage.xaml.cs"));
        Assert.Contains("IsEnabled = !open", page);
        Assert.Contains("App.Store.DeletionRefusal(shortcut, deletingData,", page);
    }

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string here = "") => here;
}
