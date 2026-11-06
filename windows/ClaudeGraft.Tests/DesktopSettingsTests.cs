using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class DesktopSettingsTests : IDisposable
{
    private const string Name = "claude_desktop_config.json";
    private const string Borrowed = """{"preferences":{"bypassPermissionsOptInByAccount":{"A":true}},"mcpServers":{"shared":{"command":"other"},"mine":{"command":"wrong"}}}""";
    private const string Local = """{"preferences":{"bypassPermissionsOptInByAccount":{"B":false}},"globalShortcut":"disabled","mcpServers":{"mine":{"command":"local"}}}""";
    private readonly TempDir temp = new();
    private readonly string source;
    private readonly string profile;
    private string SourceFile => Path.Combine(source, Name);
    private string OwnFile => Path.Combine(profile, Name);
    private string Journal => Graft.StashPath(OwnFile) + ".share.json";

    public DesktopSettingsTests()
    {
        GraftPaths.ProfilesRootOverride = temp.Path;
        source = temp.Dir("Claude");
        profile = temp.Dir("Work");
        File.WriteAllText(SourceFile, Borrowed);
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        temp.Dispose();
    }

    private void Apply(bool share = true) =>
        Graft.Apply(new GraftConfig { ProfileDir = profile, SourceDir = share ? source : null });

    [Fact(DisplayName = "sharing chats imports missing MCP servers while both profiles keep their own permission choices")]
    public void ImportsOnlyMissingServers()
    {
        File.WriteAllText(OwnFile, Local);
        Apply();
        var own = JsonNode.Parse(File.ReadAllText(OwnFile))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Local)!["preferences"], own["preferences"]));
        Assert.Equal("disabled", (string?)own["globalShortcut"]);
        Assert.Equal("local", (string?)own["mcpServers"]!["mine"]!["command"]);
        Assert.Equal("other", (string?)own["mcpServers"]!["shared"]!["command"]);
        Assert.False(File.Exists(Journal));

        own["preferences"]!["bypassPermissionsOptInByAccount"]!["B"] = true;
        own["mcpServers"]!["shared"]!["command"] = "overridden";
        var edited = own.ToJsonString();
        File.WriteAllText(OwnFile, edited);
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(OwnFile, old);
        Apply();
        Apply(share: false);
        Assert.Equal(old, File.GetLastWriteTimeUtc(OwnFile));
        Assert.Equal(edited, File.ReadAllText(OwnFile));
        Assert.Equal(Borrowed, File.ReadAllText(SourceFile));
    }

    [Theory(DisplayName = "an invalid desktop config leaves each profile's file intact")]
    [InlineData("{", Local)]
    [InlineData(Borrowed, "{")]
    [InlineData("""{"mcpServers":null}""", Local)]
    [InlineData(Borrowed, """{"mcpServers":[]}""")]
    public void SkipsUnreadableSettings(string borrowed, string local)
    {
        File.WriteAllText(SourceFile, borrowed);
        File.WriteAllText(OwnFile, local);
        Apply();
        Assert.Equal(local, File.ReadAllText(OwnFile));
        Assert.Equal(borrowed, File.ReadAllText(SourceFile));
    }

    [Theory(DisplayName = "an older whole-file share restores local choices and keeps unassigned edits for recovery")]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void RetiresTheOldJournal(bool hadOwn, bool share)
    {
        if (hadOwn) File.WriteAllText(OwnFile, Local);
        SharedSettings.Share(SourceFile, OwnFile);
        const string unassignedEdit = """{"preferences":{"value":"changed while shared"}}""";
        File.WriteAllText(OwnFile, unassignedEdit);
        Apply(share);

        var own = JsonNode.Parse(File.ReadAllText(OwnFile))!;
        Assert.True(JsonNode.DeepEquals(hadOwn ? JsonNode.Parse(Local)!["preferences"] : null, own["preferences"]));
        Assert.Equal(share, own["mcpServers"]?["shared"] is not null);
        Assert.Equal(hadOwn ? "local" : share ? "wrong" : null, (string?)own["mcpServers"]?["mine"]?["command"]);
        if (hadOwn) Assert.Equal(Local, File.ReadAllText(Graft.StashPath(OwnFile)));
        Assert.False(File.Exists(Journal));
        var saved = Assert.Single(Directory.GetFiles(profile, Name + ".graft-before-local-*"));
        Assert.Equal(unassignedEdit, File.ReadAllText(saved));
        Apply(share);
        Assert.Equal(Borrowed, File.ReadAllText(SourceFile));
    }

    [Theory(DisplayName = "an unreadable journal or original stash cannot authorize replacing desktop settings")]
    [InlineData(true)]
    [InlineData(false)]
    public void RefusesIncompleteMigration(bool damageJournal)
    {
        File.WriteAllText(OwnFile, Local);
        SharedSettings.Share(SourceFile, OwnFile);
        File.WriteAllText(damageJournal ? Journal : Graft.StashPath(OwnFile), damageJournal ? "{}" : "{");
        Assert.Throws<IOException>(() => Apply());
        Assert.True(File.Exists(Journal));
        Assert.Equal(Borrowed, File.ReadAllText(OwnFile));
        Assert.Equal(Borrowed, File.ReadAllText(SourceFile));
    }
}
