using System.Runtime.CompilerServices;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ChatConflictTests : IDisposable
{
    private readonly TempDir _t = new();

    public ChatConflictTests() => GraftPaths.ProfilesRootOverride = _t.Dir("root");

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    [Fact(DisplayName = "the question reads as a sentence for one, two or more Claudes")]
    public void Message()
    {
        Assert.StartsWith("Test A is already open on the same Claude Code chats.", ChatConflict.Message(["Test A"]));
        Assert.StartsWith("Claude and Test A are already open", ChatConflict.Message(["Claude", "Test A"]));
        Assert.StartsWith("Claude, Test A and Test B are already open", ChatConflict.Message(["Claude", "Test A", "Test B"]));
    }

    [Fact(DisplayName = "a Claude reading the main chats has Claude and every other borrower as neighbours, and one with its own chats has only those borrowing from it")]
    public void Neighbours()
    {
        var store = new ShortcutStore();
        var a = Shortcut.New("Test A", source: ShortcutSource.Main);
        var b = Shortcut.New("Test B", source: ShortcutSource.Own);
        var c = Shortcut.New("Test C", source: ShortcutSource.Of(b.Id));
        store.Add(a);
        store.Add(b);
        store.Add(c);

        Assert.Equal(["Claude"], store.ChatStoreNeighbours(a).Select(n => n.Name));
        Assert.Equal(["Test A"], store.ChatStoreNeighbours(null).Select(n => n.Name));
        Assert.Equal(["Test C"], store.ChatStoreNeighbours(b).Select(n => n.Name));
        Assert.Equal(["Test B"], store.ChatStoreNeighbours(c).Select(n => n.Name));
    }

    [Fact(DisplayName = "only open neighbours are asked about, and nothing when the Claude being opened is already open")]
    public void Sharers()
    {
        var neighbours = new List<(string Name, string Profile)> { ("Claude", "main"), ("Test A", "a"), ("Test B", "b") };
        Assert.Equal(["Claude", "Test B"], ChatConflict.OpenSharers("c", neighbours, p => p is "main" or "b"));
        Assert.Empty(ChatConflict.OpenSharers("c", neighbours, p => p is "c" or "main"));
        Assert.Empty(ChatConflict.OpenSharers("c", [], _ => true));
    }

    [Fact(DisplayName = "every way into a Claude asks: the window, the flyout and the tray menu")]
    public void EveryRouteAsks()
    {
        string Read(string file) => File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", file));
        Assert.Contains("ChatConflict.OpenSharers(", Read("MainPage.xaml.cs"));
        Assert.Contains("ChatConflict.OpenSharers(", Read("App.xaml.cs"));
        var flyout = Read("FlyoutView.xaml.cs");
        Assert.Contains("App.OpenFromTray(row)", flyout);
        Assert.DoesNotContain("ProfileRows.Open(", flyout);
        Assert.DoesNotContain("Task.Run(() => Launcher.Open(", Read("App.xaml.cs"));
    }

    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}
