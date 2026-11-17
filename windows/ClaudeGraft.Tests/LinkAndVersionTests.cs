using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// The two regressions a real install surfaced: Claude's version folders sorted
/// as strings (so a stale build launched and the update prompt never cleared),
/// and a file shared by junction becoming a broken reparse point that Claude
/// could not open.
/// </summary>
public sealed class VersionSelectionTests
{
    [Fact(DisplayName = "the newest Claude is chosen by parsed version, not folder-name string order")]
    public void PicksHighestVersionNotStringOrder()
    {
        var root = @"C:\x\AnthropicClaude";
        var dirs = new[]
        {
            root + @"\app-2.2553.1",
            root + @"\app-2.2553.13",
            root + @"\app-2.2553.0",
            root + @"\app-2.110.1",
            root + @"\Squirrel-scratch",
        };
        // Ordinal string order would pick app-2.2553.1 (the "\" after "1" outranks
        // "3"); the version parse picks 2.2553.13.
        Assert.Equal(root + @"\app-2.2553.13", Launcher.NewestApp(dirs));
    }

    [Fact(DisplayName = "no parseable app-version folder yields nothing rather than a guess")]
    public void NothingWhenNoVersionParses()
    {
        Assert.Null(Launcher.NewestApp(new[] { @"C:\x\AnthropicClaude\app-nightly", @"C:\x\AnthropicClaude\Update.exe" }));
    }
}

[Collection("GlobalState")]
public sealed class LinkMigrationTests : IDisposable
{
    private readonly TempDir _t = new();

    public LinkMigrationTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        Graft.ResetCachesForTests();
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    /// A broken directory junction where a file belongs, plus the profile's own
    /// copy stashed beside it — the shape an older build left behind.
    private static string BrokenJunctionWithStash(string dir, string name, string ownContent)
    {
        var link = Path.Combine(dir, name);
        var throwaway = Path.Combine(dir, ".jtarget");
        Directory.CreateDirectory(throwaway);
        Junction.Create(link, throwaway);
        Directory.Delete(throwaway);   // now the junction dangles, like a file target
        File.WriteAllText(Graft.StashPath(link), ownContent);
        return link;
    }

    [Fact(DisplayName = "restoring a broken shared-file junction brings back the profile's own copy")]
    public void RestoresOwnFileFromStash()
    {
        var profile = _t.Dir("root", "Claude-Work");
        var link = BrokenJunctionWithStash(profile, "claude_desktop_config.json", "{\"mcpServers\":{\"own\":{}}}");
        Assert.True(Fs.IsReparsePoint(link));

        Graft.RestoreOwnFile(link);

        Assert.False(Fs.IsReparsePoint(link));
        Assert.Equal("{\"mcpServers\":{\"own\":{}}}", File.ReadAllText(link));
        Assert.False(Fs.Exists(Graft.StashPath(link)));
    }

    [Fact(DisplayName = "relinking never junctions a file target, and undoes a broken junction there")]
    public void RelinkRefusesFileTargetsAndMigrates()
    {
        var source = _t.Dir("root", "Claude");
        var profile = _t.Dir("root", "Claude-Work");
        File.WriteAllText(Path.Combine(source, "window-state.json"), "{\"source\":true}");
        var link = BrokenJunctionWithStash(profile, "window-state.json", "{\"own\":true}");

        // The source's file must never be junctioned in; the profile keeps its own.
        Assert.False(Graft.Relink(Path.Combine(source, "window-state.json"), link));
        Assert.False(Fs.IsReparsePoint(link));
        Assert.Equal("{\"own\":true}", File.ReadAllText(link));

        // A directory target still links normally.
        var sharedDir = Path.Combine(source, "Claude Extensions");
        Directory.CreateDirectory(sharedDir);
        var dirLink = Path.Combine(profile, "Claude Extensions");
        Assert.True(Graft.Relink(sharedDir, dirLink));
        Assert.True(Junction.IsLink(dirLink));
    }

    [Fact(DisplayName = "grafting restores a broken settings junction and merges the source's servers in")]
    public void GraftIntoHealsSettingsJunction()
    {
        var source = _t.Dir("root", "Claude");
        var profile = _t.Dir("root", "Claude-Work");
        File.WriteAllText(Path.Combine(source, "config.json"), "{\"lastKnownAccountUuid\":\"AAAA\"}");
        File.WriteAllText(Path.Combine(profile, "config.json"), "{\"lastKnownAccountUuid\":\"BBBB\"}");
        File.WriteAllText(Path.Combine(source, "claude_desktop_config.json"),
            "{\"mcpServers\":{\"shared\":{\"command\":\"s\"}}}");
        var link = BrokenJunctionWithStash(profile, "claude_desktop_config.json",
            "{\"mcpServers\":{\"own\":{\"command\":\"o\"}},\"preferences\":{\"keep\":true}}");

        Graft.GraftInto(source, profile);

        Assert.False(Fs.IsReparsePoint(link));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(link));
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("own", out _));      // the profile's own kept
        Assert.True(servers.TryGetProperty("shared", out _));   // the source's merged in
        Assert.True(doc.RootElement.GetProperty("preferences").GetProperty("keep").GetBoolean());
    }
}
