using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ApplyTests : IDisposable
{
    private readonly TempDir _t = new();

    public ApplyTests()
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
        File.WriteAllText(Path.Combine(profile, "config.json"), $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
        foreach (var store in GraftPaths.ChatStoreNames)
        {
            var dir = Path.Combine(profile, store, account, org);
            Directory.CreateDirectory(dir);
            foreach (var chat in chats)
                File.WriteAllText(Path.Combine(dir, $"local_{chat}.json"), "{}");
        }
        return profile;
    }

    private static List<string> Chats(string profile, string account, string org)
    {
        var dir = Path.Combine(profile, "claude-code-sessions", account, org);
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir).Select(Path.GetFileName)
            .Where(n => n!.StartsWith("local_")).Select(n => n!["local_".Length..^".json".Length])
            .OrderBy(n => n).ToList()!;
    }

    [Fact(DisplayName = "apply with a source grafts, and with none undoes the graft")]
    public void ApplyGraftsAndUngrafts()
    {
        var main = MakeProfile("Claude", "AAAA", "ORG-A", "shared");
        var work = MakeProfile("Claude-Work", "BBBB", "ORG-B", "mine");

        Graft.Apply(new GraftConfig { ProfileDir = work, SourceDir = main });
        Assert.Equal(new[] { "mine", "shared" }, Chats(work, "BBBB", "ORG-B"));

        Graft.Apply(new GraftConfig { ProfileDir = work, SourceDir = null });
        Assert.Equal(new[] { "mine" }, Chats(work, "BBBB", "ORG-B"));
    }

    [Fact(DisplayName = "apply run again on every launch does not stash the merge away")]
    public void ApplyIsIdempotent()
    {
        // The endless-loop guard: a second pass that read as a first one would
        // stash everything the first mirrored in and fetch it back for ever.
        var main = MakeProfile("Claude", "AAAA", "ORG-A", "shared");
        var work = MakeProfile("Claude-Work", "BBBB", "ORG-B", "mine");
        var cfg = new GraftConfig { ProfileDir = work, SourceDir = main };

        Graft.Apply(cfg);
        Graft.Apply(cfg);   // a second launch
        Graft.Apply(cfg);   // and a third

        Assert.Equal(new[] { "mine", "shared" }, Chats(work, "BBBB", "ORG-B"));
        // The stash still holds exactly what the profile brought — the one copy
        // of "mine" that makes the merge reversible — and has not grown to swallow
        // the borrowed "shared" (which would be a second pass reading as a first).
        var stash = Path.Combine(work, "claude-code-sessions", "BBBB", ".ORG-B.graft-own");
        Assert.True(Directory.Exists(stash));
        Assert.Equal(new[] { "local_mine.json" },
            Directory.EnumerateFiles(stash).Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }
}

[Collection("GlobalState")]
public sealed class DeleteProfileTests : IDisposable
{
    private readonly TempDir _t = new();
    private static readonly Func<string, bool> NothingRunning = _ => false;

    public DeleteProfileTests() => GraftPaths.ProfilesRootOverride = _t.Dir("root");

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    private string Make(string name)
    {
        var p = Path.Combine(GraftPaths.ProfilesRoot, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static ProfileError? Refusal(Action act)
    {
        try { act(); return null; }
        catch (ProfileException e) { return e.Reason; }
    }

    [Fact(DisplayName = "Claude's own profile is refused as the main profile")]
    public void RefusesMain()
    {
        var main = Make("Claude");
        Assert.Equal(ProfileError.MainProfile, Refusal(() => Graft.DeleteProfile(main, NothingRunning)));
        Assert.True(Directory.Exists(main));
    }

    [Fact(DisplayName = "a folder outside the profiles root is refused")]
    public void RefusesOutside()
    {
        var nested = Path.Combine(GraftPaths.ProfilesRoot, "a", "b");
        Directory.CreateDirectory(nested);
        Assert.Equal(ProfileError.OutsideProfilesRoot, Refusal(() => Graft.DeleteProfile(nested, NothingRunning)));
        // and the profiles root itself
        Assert.Equal(ProfileError.OutsideProfilesRoot,
            Refusal(() => Graft.DeleteProfile(GraftPaths.ProfilesRoot, NothingRunning)));
    }

    [Fact(DisplayName = "a profile with Claude running on it is refused")]
    public void RefusesRunning()
    {
        var work = Make("Claude-Work");
        Assert.Equal(ProfileError.Running, Refusal(() => Graft.DeleteProfile(work, _ => true)));
        Assert.True(Directory.Exists(work));
    }

    [Fact(DisplayName = "an ordinary idle profile is deleted")]
    public void Deletes()
    {
        var work = Make("Claude-Work");
        Graft.DeleteProfile(work, NothingRunning);
        Assert.False(Directory.Exists(work));
    }
}
