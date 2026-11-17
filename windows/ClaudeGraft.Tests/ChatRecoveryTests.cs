using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// The offer to bring across chats an account switch left behind: a single
/// profile holding two accounts' histories, and a second profile signed into the
/// account whose chats are sitting next door. The C# echo of the Swift suite's
/// "Chats left in another profile" section.
/// </summary>
[Collection("GlobalState")]
public sealed class ChatRecoveryTests : IDisposable
{
    private readonly TempDir _t = new();

    public ChatRecoveryTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        Graft.ResetCachesForTests();
        // Nothing in a temporary directory can be made to run, and asking for
        // real would answer differently depending on whether somebody had Claude
        // open while the suite ran.
        Graft.RunningClaudesOverride = () => new List<string>();
    }

    public void Dispose()
    {
        Graft.RunningClaudesOverride = null;
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    private static string Profile(string name, string? account)
    {
        var dir = Path.Combine(GraftPaths.ProfilesRoot, name);
        Directory.CreateDirectory(dir);
        if (account is not null)
            File.WriteAllText(Path.Combine(dir, "config.json"),
                $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
        return dir;
    }

    private static string WorkFolder(string profile, string store = "claude-code-sessions") =>
        Path.Combine(profile, store, "work", "ORG-W");

    private static void WriteChat(string dir, string id, string title,
                                  TimeSpan age = default, DateTime? lastActive = null)
    {
        Directory.CreateDirectory(dir);
        var record = new JsonObject { ["cliSessionId"] = id, ["title"] = title };
        if (lastActive is DateTime la)
            record["lastActivityAt"] = new DateTimeOffset(la, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var file = Path.Combine(dir, $"local_{id}.json");
        File.WriteAllText(file, record.ToJsonString());
        if (age != default) File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
    }

    private static List<string> ChatsVisible(string profile, string store = "claude-code-sessions")
    {
        var dir = WorkFolder(profile, store);
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir).Select(Path.GetFileName)
            .Where(n => n!.StartsWith("local_") && n.EndsWith(".json"))
            .OrderBy(n => n).ToList()!;
    }

    /// The profile a switch left both histories in: signed into "personal", still
    /// holding a "work" history in the folder next door.
    private static string MainWithBothHistories()
    {
        var main = Profile("Claude", "personal");
        WriteChat(Path.Combine(main, "claude-code-sessions", "personal", "ORG-P"), "p1", "Own chat");
        WriteChat(WorkFolder(main), "w1", "First look at the codebase", age: TimeSpan.FromSeconds(300));
        WriteChat(WorkFolder(main), "w2", "Invoice parser", age: TimeSpan.FromSeconds(120));
        WriteChat(WorkFolder(main), "w3", "Rewriting the deploy script");
        WriteChat(WorkFolder(main, "local-agent-mode-sessions"), "w4", "A local agent chat",
                  age: TimeSpan.FromSeconds(60));
        File.WriteAllText(Path.Combine(WorkFolder(main), "deleted_gone"), "1700000000");
        return main;
    }

    [Fact(DisplayName = "a profile finds the chats another holds for the account it is signed into")]
    public void FindsChatsHeldElsewhere()
    {
        MainWithBothHistories();
        var second = Profile("Claude-Work", "work");

        var found = Graft.FindChatsElsewhere(second, Graft.SessionStoreProfiles());

        Assert.NotNull(found);
        Assert.Equal("Claude", Path.GetFileName(found!.Profile));
        Assert.Equal("work", found.Account);
        Assert.Equal(4, found.Count);
        Assert.False(found.Merging);
        Assert.Equal("Rewriting the deploy script", found.Chats[0].Title);
        Assert.Contains(found.Chats, c => c.Title == "A local agent chat");
    }

    [Fact(DisplayName = "a profile is offered nothing when it holds every chat, is signed out, or is grafted")]
    public void OffersNothingWhenThereIsNothingToOffer()
    {
        var main = MainWithBothHistories();

        Assert.Null(Graft.FindChatsElsewhere(main, Graft.SessionStoreProfiles()));

        var signedOut = Profile("Claude-New", null);
        Assert.Null(Graft.FindChatsElsewhere(signedOut, Graft.SessionStoreProfiles()));

        // A config caught mid-rename is unreadable, not signed out — treating it
        // as "no account" would offer to copy somebody else's history in.
        File.WriteAllText(Path.Combine(signedOut, "config.json"), "{ truncated");
        Assert.Null(Graft.FindChatsElsewhere(signedOut, Graft.SessionStoreProfiles()));

        // A grafted profile's emptiness is this app's own doing, so it is offered
        // nothing: the store next door is coming in from its source already.
        var grafted = Profile("Claude-Grafted", "work");
        Directory.CreateDirectory(Path.Combine(grafted, "claude-code-sessions"));
        Directory.Delete(Path.Combine(grafted, "claude-code-sessions"));
        Junction.Create(Path.Combine(grafted, "claude-code-sessions"),
                        Path.Combine(main, "claude-code-sessions"));
        Assert.Null(Graft.FindChatsElsewhere(grafted, Graft.SessionStoreProfiles()));
    }

    [Fact(DisplayName = "a profile with its own history is told the two sets are merged, and counted only what it lacks")]
    public void MergingCountsOnlyWhatIsMissing()
    {
        var main = MainWithBothHistories();
        var busy = Profile("Claude-Busy", "work");
        WriteChat(WorkFolder(busy), "own", "My own work chat");

        var found = Graft.FindChatsElsewhere(busy, Graft.SessionStoreProfiles());
        Assert.Equal(4, found!.Count);
        Assert.True(found.Merging);

        // Copy one of the four across by hand; the count drops to what is still
        // missing, so it is the number that will actually arrive.
        WriteChat(WorkFolder(busy), "w1", "First look at the codebase");
        Assert.Equal(3, Graft.FindChatsElsewhere(busy, Graft.SessionStoreProfiles())!.Count);
    }

    [Fact(DisplayName = "the newest chat is ordered by when it was last active, not when its file was written")]
    public void OrdersByRecordedActivity()
    {
        var main = MainWithBothHistories();
        var stampedSource = Path.Combine(main, "claude-code-sessions", "stamps", "ORG-S");
        WriteChat(stampedSource, "late", "Touched last, active first",
                  age: TimeSpan.FromSeconds(9000), lastActive: DateTime.UtcNow);
        WriteChat(stampedSource, "early", "Touched first, active last",
                  lastActive: DateTime.UtcNow.AddSeconds(-9000));
        var stamped = Profile("Claude-Stamped", "stamps");

        var found = Graft.FindChatsElsewhere(stamped, Graft.SessionStoreProfiles());
        Assert.Equal("Touched last, active first", found!.Chats[0].Title);
    }

    [Fact(DisplayName = "adopting copies both stores in, leaves the source untouched, and stops offering")]
    public void AdoptCopiesAcrossAndLeavesSource()
    {
        var main = MainWithBothHistories();
        var second = Profile("Claude-Work", "work");

        var result = Graft.AdoptChats(main, second, "work");
        Assert.Equal(4, result.Copied);
        Assert.Equal(new[] { "local_w1.json", "local_w2.json", "local_w3.json" }, ChatsVisible(second));
        Assert.True(File.Exists(Path.Combine(WorkFolder(second, "local-agent-mode-sessions"), "local_w4.json")));
        // A chat deleted over there stays deleted here rather than coming back new.
        Assert.True(File.Exists(Path.Combine(WorkFolder(second), "deleted_gone")));
        // The originals stay put: a profile this app did not make loses nothing.
        Assert.Contains("local_w3.json", ChatsVisible(main));

        // Once they are here the offer stops being made.
        Assert.Null(Graft.FindChatsElsewhere(second, Graft.SessionStoreProfiles()));
    }

    [Fact(DisplayName = "a second run copies nothing and never writes over a chat archived since")]
    public void AdoptIsAdditiveAndPreservesLocalEdits()
    {
        var main = MainWithBothHistories();
        var second = Profile("Claude-Work", "work");
        Graft.AdoptChats(main, second, "work");

        // Archive one of the copies on this side, then run again.
        File.WriteAllText(Path.Combine(WorkFolder(second), "local_w3.json"),
            new JsonObject { ["cliSessionId"] = "w3", ["isArchived"] = true }.ToJsonString());
        Assert.Equal(0, Graft.AdoptChats(main, second, "work").Copied);

        var kept = JsonNode.Parse(File.ReadAllText(Path.Combine(WorkFolder(second), "local_w3.json")))!;
        Assert.True(kept["isArchived"]!.GetValue<bool>());

        // A profile is never asked to copy its own chats onto themselves.
        Assert.Equal(0, Graft.AdoptChats(main, main, "work").Copied);
    }

    [Fact(DisplayName = "nothing is copied while any Claude is open, and the refusal says which one")]
    public void RefusesWhileClaudeRunning()
    {
        var main = MainWithBothHistories();
        var waiting = Profile("Claude-Waiting", "work");

        Graft.RunningClaudesOverride = () => new List<string> { GraftPaths.DefaultProfile };
        var refused = Graft.AdoptChats(main, waiting, "work");
        Assert.Equal(new[] { "Claude" }, refused.Running.Select(Path.GetFileName).ToArray());
        Assert.Equal(0, refused.Copied);
        Assert.Empty(ChatsVisible(waiting));

        Graft.RunningClaudesOverride = () => new List<string>();
        Assert.Equal(4, Graft.AdoptChats(main, waiting, "work").Copied);
    }

    [Fact(DisplayName = "a folder this app stashed away is left alone, and the refusal is per store")]
    public void RefusalIsPerStore()
    {
        var main = MainWithBothHistories();
        var stashed = Profile("Claude-Stashed", "work");
        // A stash sitting where the sessions-store account folder would be.
        Directory.CreateDirectory(Graft.StashPath(Path.Combine(stashed, "claude-code-sessions", "work")));

        var result = Graft.AdoptChats(main, stashed, "work");
        Assert.Empty(ChatsVisible(stashed));
        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(Path.Combine(WorkFolder(stashed, "local-agent-mode-sessions"), "local_w4.json")));
    }

    [Fact(DisplayName = "a store spelling the account short is filled where it really keeps it")]
    public void FillsShortenedSpelling()
    {
        var main = Profile("Claude", "personal");
        WriteChat(Path.Combine(main, "claude-code-sessions", "workaccount-1234", "ORG-W"),
                  "s1", "Long-named account");

        var shortProfile = Profile("Claude-Short", "workaccount-1234");
        var shortWork = Path.Combine(shortProfile, "claude-code-sessions", "workacco");
        Directory.CreateDirectory(Path.Combine(shortWork, "ORG-W"));

        Assert.Equal(1, Graft.AdoptChats(main, shortProfile, "workaccount-1234").Copied);
        Assert.True(File.Exists(Path.Combine(shortWork, "ORG-W", "local_s1.json")));
    }
}
