using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// Sharing MCP server definitions without touching a profile's permission
/// choices. The C# echo of the Swift suite's "Each profile keeps its permission
/// choices" section — minus the legacy-symlink-migration cases, which cannot
/// arise here: <c>claude_desktop_config.json</c> is a file, a junction cannot
/// point at a file, so this profile never had a link to migrate from.
/// </summary>
[Collection("GlobalState")]
public sealed class DesktopServersTests : IDisposable
{
    private readonly TempDir _t = new();

    public DesktopServersTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        Graft.ResetCachesForTests();
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    private const string Name = "claude_desktop_config.json";

    private static string Profile(string name, string account)
    {
        var dir = Path.Combine(GraftPaths.ProfilesRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"),
            $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
        return dir;
    }

    private static void WriteSettings(string dir, JsonObject value) =>
        File.WriteAllText(Path.Combine(dir, Name), value.ToJsonString());

    private static JsonObject Settings(string dir) =>
        (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, Name))) as JsonObject) ?? new JsonObject();

    private static JsonObject SourceSettings() => new()
    {
        ["mcpServers"] = new JsonObject { ["shared"] = new JsonObject { ["command"] = "source-server" } },
        ["preferences"] = new JsonObject
        {
            ["bypassPermissionsOptInByAccount"] = new JsonObject { ["AAAA"] = true },
        },
    };

    private static JsonObject OwnSettings() => new()
    {
        ["mcpServers"] = new JsonObject { ["own"] = new JsonObject { ["command"] = "own-server" } },
        ["preferences"] = new JsonObject
        {
            ["bypassPermissionsOptInByAccount"] = new JsonObject { ["BBBB"] = false },
        },
        ["globalShortcut"] = "disabled",
    };

    [Fact(DisplayName = "sharing copies missing MCP servers in and never touches the profile's permission choices")]
    public void MergesServersAndKeepsChoices()
    {
        var source = Profile("Claude-Settings-Source", "AAAA");
        var profile = Profile("Claude-Settings-Own", "BBBB");
        WriteSettings(source, SourceSettings());
        WriteSettings(profile, OwnSettings());
        var sourceBytes = File.ReadAllBytes(Path.Combine(source, Name));

        Graft.CopyDesktopServers(source, profile);

        var own = Settings(profile);
        Assert.False(Junction.IsLink(Path.Combine(profile, Name)));
        // The source's own permission opt-in never lands in the borrowing profile.
        Assert.False(((JsonObject)own["preferences"]!["bypassPermissionsOptInByAccount"]!).ContainsKey("AAAA"));
        Assert.False(own["preferences"]!["bypassPermissionsOptInByAccount"]!["BBBB"]!.GetValue<bool>());
        // Its own server stays; the source's missing one is added beside it.
        var servers = (JsonObject)own["mcpServers"]!;
        Assert.Equal(2, servers.Count);
        Assert.True(servers.ContainsKey("own"));
        Assert.True(servers.ContainsKey("shared"));
        // The source is never written to.
        Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(source, Name)));
    }

    [Fact(DisplayName = "a choice made after sharing survives the next launch, and an existing server keeps its config")]
    public void ChoicesAndOverridesSurviveRelaunch()
    {
        var source = Profile("Claude-Settings-Source", "AAAA");
        var profile = Profile("Claude-Settings-Own", "BBBB");
        WriteSettings(source, SourceSettings());

        // Claude saved its own settings by replacing the file: a permission opt-in
        // of its own, and a "shared" server configured this profile's way.
        var edited = new JsonObject
        {
            ["preferences"] = new JsonObject
            {
                ["bypassPermissionsOptInByAccount"] = new JsonObject { ["BBBB"] = true },
            },
            ["mcpServers"] = new JsonObject { ["shared"] = new JsonObject { ["command"] = "my-override" } },
        };
        WriteSettings(profile, edited);

        Graft.CopyDesktopServers(source, profile);

        var own = Settings(profile);
        Assert.True(own["preferences"]!["bypassPermissionsOptInByAccount"]!["BBBB"]!.GetValue<bool>());
        Assert.Equal("my-override", own["mcpServers"]!["shared"]!["command"]!.GetValue<string>());
    }

    [Fact(DisplayName = "a repeated launch leaves already-copied settings untouched")]
    public void RepeatedLaunchIsIdempotent()
    {
        var source = Profile("Claude-Settings-Source", "AAAA");
        var profile = Profile("Claude-Settings-Own", "BBBB");
        WriteSettings(source, SourceSettings());
        WriteSettings(profile, OwnSettings());

        Graft.CopyDesktopServers(source, profile);
        var settled = File.ReadAllBytes(Path.Combine(profile, Name));
        Graft.CopyDesktopServers(source, profile);
        Assert.Equal(settled, File.ReadAllBytes(Path.Combine(profile, Name)));
    }

    [Fact(DisplayName = "an unreadable settings file on either side is never treated as empty")]
    public void UnreadableSettingsAreLeftAlone()
    {
        var source = Profile("Claude-Settings-Source", "AAAA");
        var profile = Profile("Claude-Settings-Own", "BBBB");

        // A source caught mid-write cannot change local settings.
        WriteSettings(profile, OwnSettings());
        File.WriteAllText(Path.Combine(source, Name), "{ incomplete");
        var own = File.ReadAllBytes(Path.Combine(profile, Name));
        Graft.CopyDesktopServers(source, profile);
        Assert.Equal(own, File.ReadAllBytes(Path.Combine(profile, Name)));

        // An unreadable local file is not overwritten as though it were empty.
        WriteSettings(source, SourceSettings());
        File.WriteAllText(Path.Combine(profile, Name), "{ incomplete");
        Graft.CopyDesktopServers(source, profile);
        Assert.Equal("{ incomplete", File.ReadAllText(Path.Combine(profile, Name)));
    }

    [Fact(DisplayName = "grafting wires the settings copy in and leaves a writable local file")]
    public void GraftInvokesTheCopy()
    {
        var source = Profile("Claude-Settings-Source", "AAAA");
        var profile = Profile("Claude-Settings-Own", "BBBB");
        WriteSettings(source, SourceSettings());

        Graft.GraftInto(source, profile);

        Assert.False(Junction.IsLink(Path.Combine(profile, Name)));
        Assert.True(((JsonObject)Settings(profile)["mcpServers"]!).ContainsKey("shared"));
    }
}
