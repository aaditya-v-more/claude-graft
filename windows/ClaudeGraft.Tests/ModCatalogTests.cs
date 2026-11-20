using System.Text;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ModCatalogTests
{
    private const string Readme = """
        # Awesome

        ## Featured

        Seven picks.

        - [cache-tax](https://github.com/karanb192/cache-tax) - Keeps the prompt cache warm. [Recording](https://x).
        - [mod-builder](https://github.com/karanb192/claude-code-mods/tree/main/plugins/mod-builder) - Builds mods.
        - not a link

        ## Use mods

        - [elsewhere](https://github.com/a/b) - Not featured.
        """;

    private static Stream Scan(string mods) => new MemoryStream(Encoding.UTF8.GetBytes($$"""{ "mods": [ {{mods}} ] }"""));

    [Fact(DisplayName = "only the bullets under the Featured heading are featured")]
    public void FeaturedSection()
    {
        var featured = ModCatalog.ParseFeatured(Readme.Replace("\r\n", "\n"));
        Assert.Equal(new[] { "cache-tax", "mod-builder" }, featured.Select(f => f.Name));
        Assert.StartsWith("Keeps the prompt cache warm.", featured[0].Description);
    }

    [Fact(DisplayName = "a scanned mod carries its commit, its folder and what it reaches")]
    public void ScanFields()
    {
        var scan = ModCatalog.ParseScan(Scan("""
            { "id": "a/b:x", "repo": "a/b", "path": "x", "sourceCommit": "abc123", "name": "x", "description": "d",
              "reach": { "labels": ["reads files", "draws"] }, "validate": { "status": "warnings" } }
            """));
        var only = Assert.Single(scan);
        Assert.Equal(("a/b", "x", "abc123", "warnings"), (only.Repo, only.Path, only.Commit, only.Validation));
        Assert.Equal(new[] { "reads files", "draws" }, only.Reach);
    }

    [Fact(DisplayName = "a mod the scan could not place has no level, and a filter on reach leaves it out")]
    public void UnplacedReach()
    {
        var scan = ModCatalog.ParseScan(Scan("""
            { "id": "u", "kind": "mod", "name": "u", "repo": "a/u", "sourceCommit": "1", "stars": null,
              "reach": { "level": null, "name": "unknown" }, "validate": null }
            """));
        Assert.Null(Assert.Single(scan).Level);
        Assert.Single(ModCatalog.Browse(scan, new()));
        Assert.Empty(ModCatalog.Browse(scan, new(MostReach: 3)));
    }

    [Fact(DisplayName = "a featured link into a folder matches that folder's scan, not the repository's first")]
    public void MatchByFolder()
    {
        var scan = ModCatalog.ParseScan(Scan("""
            { "repo": "karanb192/claude-code-mods", "path": "plugins/other", "name": "other", "sourceCommit": "1" },
            { "repo": "karanb192/claude-code-mods", "path": "plugins/mod-builder", "name": "mod-builder", "sourceCommit": "2" }
            """));
        var featured = ModCatalog.ParseFeatured(Readme.Replace("\r\n", "\n"));
        Assert.Equal("2", ModCatalog.Match(featured[1], scan)?.Commit);
    }

    [Fact(DisplayName = "a link to a repository holding several mods matches the one of the same name, or none")]
    public void MatchByName()
    {
        var featured = new ModCatalog.Featured("cache-tax", "https://github.com/karanb192/cache-tax", "");
        var named = ModCatalog.ParseScan(Scan("""
            { "repo": "karanb192/cache-tax", "path": "a", "name": "helper", "sourceCommit": "1" },
            { "repo": "karanb192/cache-tax", "path": "b", "name": "cache-tax", "sourceCommit": "2" }
            """));
        Assert.Equal("2", ModCatalog.Match(featured, named)?.Commit);
        var unnamed = ModCatalog.ParseScan(Scan("""
            { "repo": "karanb192/cache-tax", "path": "a", "name": "one", "sourceCommit": "1" },
            { "repo": "karanb192/cache-tax", "path": "b", "name": "two", "sourceCommit": "2" }
            """));
        Assert.Null(ModCatalog.Match(featured, unnamed));
    }

    private static List<ModCatalog.Scanned> Several() => ModCatalog.ParseScan(Scan("""
        { "id": "a", "kind": "mod", "name": "paper-theme", "description": "A warm look", "repo": "x/paper", "sourceCommit": "1",
          "stars": 5, "pushedAt": "2026-11-01T00:00:00Z", "reach": { "level": 0 }, "validate": { "status": "passed" } },
        { "id": "b", "kind": "mod", "name": "net-meter", "description": "Shows usage", "repo": "x/net", "sourceCommit": "2",
          "stars": 50, "pushedAt": "2026-10-01T00:00:00Z", "reach": { "level": 3 }, "validate": { "status": "warnings" } },
        { "id": "c", "kind": "mod", "name": "broken", "description": "A look that fails", "repo": "x/broken", "sourceCommit": "3",
          "stars": 9, "pushedAt": "2026-11-10T00:00:00Z", "reach": { "level": 1 }, "validate": { "status": "failed" } },
        { "id": "d", "kind": "builtin", "name": "diff", "repo": "anthropics/claude-code", "sourceCommit": "4", "stars": 99999 },
        { "id": "e", "kind": "duplicate", "name": "paper-copy", "repo": "y/paper", "sourceCommit": "5" }
        """));

    [Fact(DisplayName = "browsing lists the mods to install, most starred first, and leaves out built-ins, duplicates and the like")]
    public void BrowseEverything() =>
        Assert.Equal(new[] { "b", "c", "a" }, ModCatalog.Browse(Several(), new()).Select(m => m.Id));

    [Fact(DisplayName = "the reach filter keeps mods at or under the level chosen, and the validation filter drops failures")]
    public void BrowseFilters()
    {
        Assert.Equal(new[] { "c", "a" }, ModCatalog.Browse(Several(), new(MostReach: 1)).Select(m => m.Id));
        Assert.Equal(new[] { "b", "a" }, ModCatalog.Browse(Several(), new(PassedOnly: true)).Select(m => m.Id));
    }

    [Fact(DisplayName = "every word of a search must appear, and the list sorts by update or name when asked")]
    public void BrowseSearchAndSort()
    {
        Assert.Equal(new[] { "c", "a" }, ModCatalog.Browse(Several(), new(Query: "look")).Select(m => m.Id));
        Assert.Equal(new[] { "a" }, ModCatalog.Browse(Several(), new(Query: "warm look")).Select(m => m.Id));
        Assert.Equal(new[] { "c", "a", "b" }, ModCatalog.Browse(Several(), new(Order: ModCatalog.Sort.Updated)).Select(m => m.Id));
        Assert.Equal(new[] { "c", "b", "a" }, ModCatalog.Browse(Several(), new(Order: ModCatalog.Sort.Name)).Select(m => m.Id));
    }

    [Theory(DisplayName = "a description that names the desktop app counts, unless it says the mod does nothing there")]
    [InlineData("A band above the prompt in the terminal and the desktop app", true)]
    [InlineData("Status band in the Claude Desktop app's Code tab", true)]
    [InlineData("Claude desktop の Code タブに", true)]
    [InlineData("Thumbnails above the prompt in kitty and Ghostty", false)]
    [InlineData("Terminal-only; in Claude Desktop it does nothing", false)]
    [InlineData("只在终端里工作，Claude Desktop 里整个 mod 不做任何事", false)]
    public void DesktopMention(string description, bool mentions) =>
        Assert.Equal(mentions, ModCatalog.MentionsDesktop(description));

    [Fact(DisplayName = "the Desktop filter keeps only mods that mention it")]
    public void BrowseDesktopOnly()
    {
        var scan = ModCatalog.ParseScan(Scan("""
            { "id": "d", "kind": "mod", "name": "d", "description": "Works in the desktop app", "repo": "a/d", "sourceCommit": "1" },
            { "id": "t", "kind": "mod", "name": "t", "description": "Kitty thumbnails", "repo": "a/t", "sourceCommit": "2" }
            """));
        Assert.Equal(new[] { "d" }, ModCatalog.Browse(scan, new(DesktopOnly: true)).Select(m => m.Id));
    }

    [Theory(DisplayName = "a mod that switches accounts, moves sessions or reads the sign-in is flagged as overlapping Graft")]
    [InlineData("Switch Claude accounts from a sidebar", "", true)]
    [InlineData("Move Claude desktop Code sessions between sidebar workspaces", "", true)]
    [InlineData("Switches a running session between api.anthropic.com and a local gateway", "", true)]
    [InlineData("Keeps the prompt cache warm", "reads your sign-in", true)]
    [InlineData("A DM screen to manage your sessions like a D&D table", "", false)]
    [InlineData("A progress bar above the spinner", "", false)]
    public void Overlap(string description, string label, bool flagged)
    {
        var mod = new ModCatalog.Scanned("x", "a/x", "", "1", "x", description, label.Length > 0 ? [label] : [], "passed");
        Assert.Equal(flagged, ModCatalog.ManagesAccountsOrSessions(mod));
    }

    [Fact(DisplayName = "a mod whose drawing gives way outside the terminal, and never names the desktop, is taken as terminal-only")]
    public void TerminalOnlyCode()
    {
        using var tmp = new TempDir();
        var hooks = tmp.Dir("mod\\hooks");
        File.WriteAllText(Path.Combine(hooks, "register.tsx"),
            "on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {\n  if (e.surface !== 'terminal') return next(e)\n})");
        Assert.True(ModCatalog.TerminalOnly(Path.GetDirectoryName(hooks)!));

        File.AppendAllText(Path.Combine(hooks, "register.tsx"), "\nconst svg = e.surface === 'desktop'");
        Assert.False(ModCatalog.TerminalOnly(Path.GetDirectoryName(hooks)!));
    }

    [Fact(DisplayName = "a mod that draws nothing is not taken as terminal-only")]
    public void NoDrawingNoWarning()
    {
        using var tmp = new TempDir();
        var hooks = tmp.Dir("guard\\hooks");
        File.WriteAllText(Path.Combine(hooks, "register.ts"), "on('tool.call', { tool: 'Bash' }, async ($, e, next) => next(e))");
        Assert.False(ModCatalog.TerminalOnly(Path.GetDirectoryName(hooks)!));
    }

    [Fact(DisplayName = "the live list's featured mods are each found in its scan", Skip = "reads the network; run with GRAFT_LIVE=1")]
    public async Task LiveList()
    {
        using var tmp = new TempDir();
        GraftPaths.ProfilesRootOverride = tmp.Path;
        try
        {
            var entries = await ModCatalog.Load();
            Assert.NotEmpty(entries.Featured);
            Assert.All(entries.Featured, e => Assert.NotNull(e.Scan));
            Assert.True(ModCatalog.Browse(entries.All, new()).Count > 1000);
        }
        finally { GraftPaths.ProfilesRootOverride = null; }
    }
}
