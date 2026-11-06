using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class FolderNameTests
{
    [Fact(DisplayName = "an ordinary folder name is fine")]
    public void Ordinary() => Assert.Null(Graft.ValidateFolder("Claude-Work"));

    [Fact(DisplayName = "an empty one is not, nor is whitespace")]
    public void Empty()
    {
        Assert.NotNull(Graft.ValidateFolder(""));
        Assert.NotNull(Graft.ValidateFolder("   "));
    }

    [Fact(DisplayName = "a name that climbs out, or carries a separator, is refused")]
    public void Escapes()
    {
        Assert.NotNull(Graft.ValidateFolder("../Claude"));
        Assert.NotNull(Graft.ValidateFolder("a/b"));
        Assert.NotNull(Graft.ValidateFolder("a\\b"));   // the Windows separator too
        Assert.NotNull(Graft.ValidateFolder("C:"));
    }

    [Fact(DisplayName = "a hidden name is refused")]
    public void Hidden() => Assert.NotNull(Graft.ValidateFolder(".hidden"));

    [Fact(DisplayName = "Claude's own folder and Graft's own are both reserved")]
    public void Reserved()
    {
        Assert.NotNull(Graft.ValidateFolder("Claude"));
        Assert.NotNull(Graft.ValidateFolder("ClaudeGraft"));
    }
}

public class ConfigIdentityTests
{
    [Fact(DisplayName = "a config that will not parse is unreadable, not empty")]
    public void MidWrite()
    {
        using var t = new TempDir();
        var profile = t.Dir("Claude-Work");
        // What a read landing part way through Claude's rename sees.
        File.WriteAllText(Path.Combine(profile, "config.json"),
            "{\"lastKnownAccountUuid\":\"BBBB\",\"oauth:tokenCache\":\"the-login\"");

        Assert.Null(Graft.ReadableConfigJson(profile));
        // account falls back through the empty config, so it cannot invent one
        // for a file it could not read.
        Assert.Null(Graft.Account(profile));
    }

    [Fact(DisplayName = "a profile with no config yet reads as an empty one, not an unreadable one")]
    public void NeverSignedIn()
    {
        using var t = new TempDir();
        var profile = t.Dir("Claude-Fresh");
        Assert.NotNull(Graft.ReadableConfigJson(profile));   // readable...
        Assert.Empty(Graft.ReadableConfigJson(profile)!);    // ...and empty
    }

    [Fact(DisplayName = "a well-formed config gives up the account it is signed into")]
    public void SignedIn()
    {
        using var t = new TempDir();
        var profile = t.Dir("Claude-Signed");
        File.WriteAllText(Path.Combine(profile, "config.json"),
            "{\"lastKnownAccountUuid\":\"AAAA\",\"oauth:tokenCache\":\"tok\"}");
        Assert.Equal("AAAA", Graft.Account(profile));
    }
}

public class StashedAwayTests
{
    [Fact(DisplayName = "a folder with nothing put away above it is not one this app stashed")]
    public void Nothing()
    {
        using var t = new TempDir();
        var org = t.Dir("Claude-Whole-Store", "claude-code-sessions", "WWWW", "ORG-W");
        Assert.False(Graft.IsStashedAway(org));
    }

    [Fact(DisplayName = "a sibling beside the organization folder is the cross-account shape")]
    public void Sibling()
    {
        using var t = new TempDir();
        var account = t.Dir("Claude-X", "claude-code-sessions", "WWWW");
        var org = Path.Combine(account, "ORG-W");
        Directory.CreateDirectory(org);
        Directory.CreateDirectory(Path.Combine(account, ".ORG-W.graft-own"));
        Assert.True(Graft.IsStashedAway(org));
    }

    [Fact(DisplayName = "a store put away whole, two levels up, is the same-account shape")]
    public void WholeStore()
    {
        using var t = new TempDir();
        var profile = t.Dir("Claude-Whole");
        var store = Path.Combine(profile, "claude-code-sessions");
        var org = Path.Combine(store, "WWWW", "ORG-W");
        Directory.CreateDirectory(org);

        // Exactly what a same-account first pass does: the store moved aside,
        // an empty one built where it was.
        Directory.Move(store, Path.Combine(profile, ".claude-code-sessions.graft-own"));
        Directory.CreateDirectory(org);

        Assert.True(Graft.IsStashedAway(org));
    }
}

public class CounterpartDirectoryTests
{
    [Fact(DisplayName = "an exact name is returned as itself")]
    public void Exact()
    {
        using var t = new TempDir();
        var parent = t.Dir("acct");
        Directory.CreateDirectory(Path.Combine(parent, "00000000-0000-0000-0000-000000000000"));
        Assert.Equal("00000000-0000-0000-0000-000000000000",
            Graft.CounterpartDirectory(parent, "00000000-0000-0000-0000-000000000000"));
    }

    [Fact(DisplayName = "a store spelling the org short is matched by its prefix")]
    public void ShortenedPrefix()
    {
        using var t = new TempDir();
        var parent = t.Dir("acct");
        Directory.CreateDirectory(Path.Combine(parent, "ed417e0f"));   // local-mode short name
        Assert.Equal("ed417e0f",
            Graft.CounterpartDirectory(parent, "ed417e0f-5edd-4a1b-9c2d-000000000000"));
    }

    [Fact(DisplayName = "two directories that both fit, with no exact match, are refused rather than guessed between")]
    public void Ambiguous()
    {
        using var t = new TempDir();
        var parent = t.Dir("acct");
        // Neither is the name asked for, but both are prefixes of it and both
        // are long enough, so there is no telling which store the chats are in.
        Directory.CreateDirectory(Path.Combine(parent, "ed417e0f"));
        Directory.CreateDirectory(Path.Combine(parent, "ed417e0f-5edd-4a1b"));
        Assert.Null(Graft.CounterpartDirectory(parent, "ed417e0f-5edd-4a1b-9c2d-000000000000"));
    }

    [Fact(DisplayName = "a prefix shorter than eight characters is not enough to match on")]
    public void TooShort()
    {
        using var t = new TempDir();
        var parent = t.Dir("acct");
        Directory.CreateDirectory(Path.Combine(parent, "ed41"));
        Assert.Null(Graft.CounterpartDirectory(parent, "ed417e0f-5edd-4a1b-9c2d-000000000000"));
    }
}

public class RelinkTests
{
    [Fact(DisplayName = "relink points a name at a target and reads through to it")]
    public void PointsThrough()
    {
        using var t = new TempDir();
        var source = t.Dir("src", "orgA");
        File.WriteAllText(Path.Combine(source, "rec1.json"), "{\"chat\":\"source\"}");
        var link = Path.Combine(t.Dir("dst"), "orgA");

        Assert.True(Graft.Relink(source, link));
        Assert.True(Junction.IsLink(link));
        Assert.Equal("{\"chat\":\"source\"}", File.ReadAllText(Path.Combine(link, "rec1.json")));
    }

    [Fact(DisplayName = "a real folder already at the name is stashed, never destroyed")]
    public void StashesExisting()
    {
        using var t = new TempDir();
        var source = t.Dir("src", "orgA");
        File.WriteAllText(Path.Combine(source, "borrowed.json"), "{}");

        var dst = t.Dir("dst");
        var link = Path.Combine(dst, "orgA");
        Directory.CreateDirectory(link);
        File.WriteAllText(Path.Combine(link, "own.json"), "mine");   // the profile's own chat

        Assert.True(Graft.Relink(source, link));
        // The link now shows the source's chats...
        Assert.True(Junction.IsLink(link));
        Assert.True(File.Exists(Path.Combine(link, "borrowed.json")));
        // ...and the profile's own were moved aside, not lost.
        var stash = Path.Combine(dst, ".orgA.graft-own");
        Assert.True(Directory.Exists(stash));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(stash, "own.json")));
    }

    [Fact(DisplayName = "relink refuses to link a name to itself")]
    public void RefusesSelf()
    {
        using var t = new TempDir();
        var profile = t.Dir("Claude-Selfie");
        Assert.False(Graft.Relink(profile, profile));
        Assert.True(Directory.Exists(profile));   // the folder survives that
    }

    [Fact(DisplayName = "relinking to a target already linked is a no-op that keeps the link")]
    public void Idempotent()
    {
        using var t = new TempDir();
        var source = t.Dir("src", "orgA");
        var link = Path.Combine(t.Dir("dst"), "orgA");
        Assert.True(Graft.Relink(source, link));
        Assert.True(Graft.Relink(source, link));   // second time changes nothing
        Assert.True(Junction.IsLink(link));
    }
}
