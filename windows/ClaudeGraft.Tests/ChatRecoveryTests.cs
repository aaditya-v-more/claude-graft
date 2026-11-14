using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ChatRecoveryTests : IDisposable
{
    private const string Account = "12345678-abcd-4a00-9000-aaaaaaaaaaaa";
    private const string Org = "87654321-abcd-4a00-9000-bbbbbbbbbbbb";
    private readonly TempDir temp = new();
    private readonly Func<IReadOnlyList<(int pid, string command)>> previousProcesses = ClaudeProcesses.Enumerate;
    private readonly string profile;
    private readonly string main;

    public ChatRecoveryTests()
    {
        GraftPaths.ProfilesRootOverride = temp.Path;
        ClaudeProcesses.Enumerate = () => [];
        main = temp.Dir("Claude");
        profile = temp.Dir("Work");
        SignIn(profile, Account);
        SignIn(main, "other-account");
        Write(main, "claude-code-sessions", Account, Org, "local_mine.json", """{"title":"Newer","lastActivityAt":1750000000000}""");
        Write(main, "claude-code-sessions", Account, Org, "deleted_previous", "1700000000000");
        Write(main, "local-agent-mode-sessions", Account[..8], Org[..8], "local_agent.json", """{"title":"Older","lastActivityAt":1740000000000}""");
        Write(main, "claude-code-sessions", "other-account", Org, "local_private.json", """{"title":"Another account"}""");
    }

    private static void SignIn(string directory, string account) => File.WriteAllText(Path.Combine(directory, "config.json"),
        JsonSerializer.Serialize(new { lastKnownAccountUuid = account }));

    private static string Write(string profile, string store, string account, string org, string name, string content)
    {
        var directory = Directory.CreateDirectory(Path.Combine(profile, store, account, org)).FullName;
        var file = Path.Combine(directory, name);
        File.WriteAllText(file, content);
        return file;
    }

    [Fact(DisplayName = "a shortcut recovers only the signed-in account's missing records and deletion markers without linking histories")]
    public void IndependentRecovery()
    {
        var offer = Assert.IsType<ChatRecovery.Offer>(ChatRecovery.Find(profile));
        Assert.Equal(main, offer.Source);
        Assert.Equal(2, offer.Count);
        Assert.False(offer.HasOwnChats);
        Assert.Equal(new[] { "Newer", "Older" }, offer.Recent.Select(p => p.Title));
        Directory.CreateDirectory(Path.Combine(profile, "local-agent-mode-sessions", Account[..8], Org[..8]));
        Assert.Equal(2, ChatRecovery.Copy(profile, offer));
        Assert.True(File.Exists(Path.Combine(profile, "local-agent-mode-sessions", Account[..8], Org[..8], "local_agent.json")));
        var copied = Path.Combine(profile, "claude-code-sessions", Account, Org);
        Assert.Equal("1700000000000", File.ReadAllText(Path.Combine(copied, "deleted_previous")));
        Assert.False(File.Exists(Path.Combine(copied, "local_private.json")));
        Assert.False(Directory.Exists(Path.Combine(profile, "claude-code-sessions", "other-account")));
        Assert.True(File.Exists(Path.Combine(main, "claude-code-sessions", Account, Org, "local_mine.json")));
        File.WriteAllText(Path.Combine(copied, "local_mine.json"), """{"isArchived":true}""");
        Assert.Equal(0, ChatRecovery.Copy(profile, offer));
        Assert.Equal("""{"isArchived":true}""", File.ReadAllText(Path.Combine(copied, "local_mine.json")));
        Assert.Null(ChatRecovery.Find(profile));
        Assert.Empty(Graft.MirrorPairsBorrowedBy(profile));
    }

    [Fact(DisplayName = "an existing chat is preserved and only the missing conversation is offered")]
    public void ExistingHistory()
    {
        var prior = Write(profile, "claude-code-sessions", Account, Org, "local_mine.json", """{"title":"My edits"}""");
        var offer = Assert.IsType<ChatRecovery.Offer>(ChatRecovery.Find(profile));
        Assert.True(offer.HasOwnChats);
        Assert.Equal(1, offer.Count);
        Assert.Equal(1, ChatRecovery.Copy(profile, offer));
        Assert.Equal("""{"title":"My edits"}""", File.ReadAllText(prior));
    }

    [Fact(DisplayName = "a stale account, a running Claude, or an unavailable process list prevents the copy")]
    public void StaleOrBusy()
    {
        var offer = Assert.IsType<ChatRecovery.Offer>(ChatRecovery.Find(profile));
        SignIn(profile, "someone-else");
        Assert.Throws<IOException>(() => ChatRecovery.Copy(profile, offer));
        SignIn(profile, Account);
        ClaudeProcesses.Enumerate = () => [(42, @"""C:\Program Files\WindowsApps\Claude_test\app\claude.exe""")];
        Assert.Throws<IOException>(() => ChatRecovery.Copy(profile, offer));
        ClaudeProcesses.Enumerate = () => throw new IOException("Fixture process query refused.");
        Assert.Throws<IOException>(() => ChatRecovery.Copy(profile, offer));
        Assert.False(Directory.Exists(Path.Combine(profile, "claude-code-sessions")));
    }

    [Fact(DisplayName = "neither linked nor stashed account storage is a recovery destination")]
    public void OwnFoldersOnly()
    {
        var outside = temp.Dir("unrelated");
        var store = temp.Dir("Work", "claude-code-sessions");
        Junction.Create(Path.Combine(store, Account), outside);
        Directory.CreateDirectory(Graft.StashPath(Path.Combine(profile, "local-agent-mode-sessions")));
        Assert.Null(ChatRecovery.Find(profile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact(DisplayName = "invalid or unreadable account data and active sharing suppress recovery offers")]
    public void InvalidIdentity()
    {
        var settings = Path.Combine(profile, "config.json");
        File.WriteAllText(settings, "{");
        Assert.Null(ChatRecovery.Find(profile));
        SignIn(profile, "..");
        Assert.Null(ChatRecovery.Find(profile));
        SignIn(profile, Account);
        var shortcuts = new ShortcutStore();
        shortcuts.Add(Shortcut.New("Work", "Work", ShortcutSource.Main));
        Assert.Null(ChatRecovery.Find(profile));
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        ClaudeProcesses.Enumerate = previousProcesses;
        temp.Dispose();
    }
}
