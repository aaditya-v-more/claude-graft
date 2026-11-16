using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// Profiles, mods and settings are all in a throwaway directory sealed with a
/// key made for the test.
[Collection("GlobalState")]
public sealed class ModsTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public ModsTests()
    {
        GraftPaths.ProfilesRootOverride = _tmp.Path;
        ModLibrary.ClaudeHomeOverride = _tmp.Dir("home");
        ModLibrary.RemoveFolderOverride = folder => Directory.Delete(folder, recursive: true);
    }

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        ModLibrary.ClaudeHomeOverride = null;
        ModLibrary.RemoveFolderOverride = null;
        _tmp.Dispose();
    }

    private string Mod(string name)
    {
        var dir = Path.Combine(ModLibrary.Directory, name, ".claude-plugin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), new JsonObject { ["name"] = name, ["description"] = "d" }.ToJsonString());
        return Path.GetDirectoryName(dir)!;
    }

    private string Profile(string name, Dictionary<string, string>? env = null)
    {
        var dir = _tmp.Dir(name);
        var outer = new JsonObject();
        if (env is not null) outer["envVars"] = ClaudeCodeEnvironment.Seal(env, _key);
        File.WriteAllText(Path.Combine(dir, PoolEnvironment.FileName), outer.ToJsonString());
        return dir;
    }

    private Dictionary<string, string> Env(string profile) =>
        PoolEnvironment.Read(profile).Text is not string text ? new()
        : ClaudeCodeEnvironment.Unseal(text, _key)!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());

    private void Choose(string profile, params string[] mods)
    {
        var choices = ModChoices.Load()!;
        choices.Profiles[PoolEnvironment.Key(profile)] = mods.ToList();
        choices.Save();
    }

    private Dictionary<string, ModEnvironment.State> Settle(Func<string, bool>? running = null, params string[] profiles) =>
        ModEnvironment.Settle(profiles, _ => _key, running ?? (_ => false));

    [Fact(DisplayName = "a closed Claude given a mod holds its folder beside what it already had")]
    public void ModGoesIn()
    {
        var mica = Mod("mica");
        var p = Profile("Claude-2", new() { ["FOO"] = "bar" });
        Choose(p, "mica");
        Assert.Equal(ModEnvironment.State.On, Settle(null, p)[PoolEnvironment.Key(p)]);
        Assert.Equal("bar", Env(p)["FOO"]);
        Assert.Equal(mica, Env(p)[ModLibrary.Variable]);
    }

    [Fact(DisplayName = "the mods every Claude loads from settings stay in a Claude given one more")]
    public void SharedFoldersCarry()
    {
        var mica = Mod("mica");
        File.WriteAllText(ModLibrary.UserSettings, new JsonObject { ["env"] = new JsonObject { [ModLibrary.Variable] = @"C:\mods\band" } }.ToJsonString());
        var p = Profile("Claude-2");
        Choose(p, "mica");
        Settle(null, p);
        Assert.Equal(new[] { @"C:\mods\band", mica }, ModLibrary.Split(Env(p)[ModLibrary.Variable]));
    }

    [Fact(DisplayName = "a mod from settings left out of one Claude is gone from its list, even when that leaves the list empty")]
    public void SettingsModLeftOut()
    {
        var band = Found("mods\\limits-band", "limits-band", DateTime.UtcNow);
        File.WriteAllText(ModLibrary.UserSettings, new JsonObject { ["env"] = new JsonObject { [ModLibrary.Variable] = band } }.ToJsonString());
        var p = Profile("Claude-2");
        var choices = ModChoices.Load()!;
        choices.Off[PoolEnvironment.Key(p)] = ["limits-band"];
        choices.Save();
        Assert.Equal(ModEnvironment.State.On, Settle(null, p)[PoolEnvironment.Key(p)]);
        Assert.Equal("", Env(p)[ModLibrary.Variable]);

        choices = ModChoices.Load()!;
        choices.Off.Clear();
        choices.Save();
        Settle(null, p);
        Assert.False(Env(p).ContainsKey(ModLibrary.Variable));
    }

    [Fact(DisplayName = "an open Claude is left alone and takes its mods once it has closed")]
    public void OpenClaudeWaits()
    {
        Mod("mica");
        var p = Profile("Claude-2");
        Choose(p, "mica");
        Assert.Equal(ModEnvironment.State.Waiting, Settle(_ => true, p)[PoolEnvironment.Key(p)]);
        Assert.False(Env(p).ContainsKey(ModLibrary.Variable));
        Assert.Equal(ModEnvironment.State.On, Settle(null, p)[PoolEnvironment.Key(p)]);
    }

    [Fact(DisplayName = "a list of the person's own is never replaced")]
    public void OwnListKept()
    {
        Mod("mica");
        var p = Profile("Claude-2", new() { [ModLibrary.Variable] = @"C:\mine" });
        Choose(p, "mica");
        Assert.Equal(ModEnvironment.State.Conflict, Settle(null, p)[PoolEnvironment.Key(p)]);
        Assert.Equal(@"C:\mine", Env(p)[ModLibrary.Variable]);
    }

    [Fact(DisplayName = "turning off a Claude's last mod takes out Graft's value and nothing else")]
    public void LastModComesOut()
    {
        Mod("mica");
        var p = Profile("Claude-2", new() { ["FOO"] = "bar" });
        Choose(p, "mica");
        Settle(null, p);
        Choose(p);
        Assert.Equal(ModEnvironment.State.Off, Settle(null, p)[PoolEnvironment.Key(p)]);
        Assert.Equal(new Dictionary<string, string> { ["FOO"] = "bar" }, Env(p));
    }

    [Fact(DisplayName = "a value the person changed after Graft wrote it is theirs and stays")]
    public void EditedValueStays()
    {
        Mod("mica");
        var p = Profile("Claude-2");
        Choose(p, "mica");
        Settle(null, p);
        PoolEnvironment.Write(ClaudeCodeEnvironment.Seal(new Dictionary<string, string> { [ModLibrary.Variable] = @"C:\edited" }, _key), p);
        Choose(p);
        Settle(null, p);
        Assert.Equal(@"C:\edited", Env(p)[ModLibrary.Variable]);
    }

    [Fact(DisplayName = "Graft going takes its value out of every closed Claude")]
    public void TakeOutOnLeaving()
    {
        Mod("mica");
        var closed = Profile("Claude-2");
        var open = Profile("Claude-3");
        Choose(closed, "mica");
        Choose(open, "mica");
        Settle(null, closed, open);
        var left = ModEnvironment.TakeOut(_ => _key, path => PoolEnvironment.Key(path) == PoolEnvironment.Key(open));
        Assert.False(Env(closed).ContainsKey(ModLibrary.Variable));
        Assert.Equal(new[] { PoolEnvironment.Key(open) }, left.Select(PoolEnvironment.Key));
    }

    [Fact(DisplayName = "an unreadable choices file is not saved over")]
    public void UnreadableChoicesKept()
    {
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(ModLibrary.ChoicesFile, "{ not json");
        Mod("mica");
        var p = Profile("Claude-2");
        Settle(null, p);
        Assert.Equal("{ not json", File.ReadAllText(ModLibrary.ChoicesFile));
    }

    [Fact(DisplayName = "adding a mod copies its folder in under its own name and leaves the engine's types behind")]
    public void AddCopiesMod()
    {
        var source = _tmp.Dir("work\\anything");
        Directory.CreateDirectory(Path.Combine(source, ".claude-plugin", "types"));
        Directory.CreateDirectory(Path.Combine(source, "hooks"));
        File.WriteAllText(Path.Combine(source, ".claude-plugin", "plugin.json"), "{\"name\":\"mica\"}");
        File.WriteAllText(Path.Combine(source, ".claude-plugin", "types", "index.d.ts"), "");
        File.WriteAllText(Path.Combine(source, "hooks", "register.tsx"), "v1");
        Assert.Equal(("mica", (string?)null), ModLibrary.Add(source));
        File.WriteAllText(Path.Combine(source, "hooks", "register.tsx"), "v2");
        ModLibrary.Add(source);
        var added = Path.Combine(ModLibrary.Directory, "mica");
        Assert.Equal("v2", File.ReadAllText(Path.Combine(added, "hooks", "register.tsx")));
        Assert.False(Directory.Exists(Path.Combine(added, ".claude-plugin", "types")));
        Assert.Equal(new[] { "mica" }, ModLibrary.List().Select(m => m.Name));
    }

    [Fact(DisplayName = "a folder with no manifest is refused and nothing is copied")]
    public void AddRefusesNonMod()
    {
        var (name, problem) = ModLibrary.Add(_tmp.Dir("loose"));
        Assert.Null(name);
        Assert.NotNull(problem);
        Assert.Empty(ModLibrary.List());
    }

    private string Found(string relative, string name, DateTime changed)
    {
        var folder = Path.Combine(ModLibrary.ClaudeHome, relative);
        Directory.CreateDirectory(Path.Combine(folder, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(folder, "hooks"));
        File.WriteAllText(Path.Combine(folder, ".claude-plugin", "plugin.json"), new JsonObject { ["name"] = name }.ToJsonString());
        var register = Path.Combine(folder, "hooks", "register.tsx");
        File.WriteAllText(register, "");
        File.SetLastWriteTimeUtc(register, changed);
        return folder;
    }

    [Fact(DisplayName = "mods in Claude Code's own folders are listed without being added")]
    public void FindsMods()
    {
        var band = Found("mods\\limits-band", "limits-band", DateTime.UtcNow);
        var older = Found("dev-mods\\session-a\\mica", "mica", DateTime.UtcNow.AddDays(-2));
        var newer = Found("dev-mods\\session-b\\mica", "mica", DateTime.UtcNow);
        var listed = ModLibrary.List();
        Assert.Equal(new[] { "limits-band", "mica" }, listed.Select(m => m.Name));
        Assert.Equal(band, listed[0].Folder);
        Assert.Equal(newer, listed[1].Folder);
        Assert.Equal(ModLibrary.Origin.InProgress, listed[1].Origin);
    }

    [Fact(DisplayName = "a mod Graft holds a copy of is listed once, as Graft's copy")]
    public void LibraryCopyWins()
    {
        Found("dev-mods\\session-a\\mica", "mica", DateTime.UtcNow);
        var copy = Mod("mica");
        Assert.Equal(new[] { copy }, ModLibrary.List().Select(m => m.Folder));
    }

    [Fact(DisplayName = "a found mod is loaded from where it is, not copied")]
    public void FoundModLoadsInPlace()
    {
        var mica = Found("dev-mods\\session-a\\mica", "mica", DateTime.UtcNow);
        var p = Profile("Claude-2");
        Choose(p, "mica");
        Settle(null, p);
        Assert.Equal(mica, Env(p)[ModLibrary.Variable]);
        Assert.False(Directory.Exists(ModLibrary.Directory));
    }

    [Fact(DisplayName = "keeping a mod made in a session gives Graft a copy that outlives the session's folder")]
    public void KeepCopiesSessionMod()
    {
        var made = Found(@"dev-mods\session-a\mica", "mica", DateTime.UtcNow);
        Assert.Null(ModLibrary.Keep(ModLibrary.List().Single()));
        Directory.Delete(made, recursive: true);
        var kept = ModLibrary.List().Single();
        Assert.Equal(ModLibrary.Origin.Library, kept.Origin);
        Assert.Equal(Path.Combine(ModLibrary.Directory, "mica"), kept.Folder);
    }

    [Fact(DisplayName = "keeping a mod that is not a session's copies nothing")]
    public void KeepLeavesOthers()
    {
        Found(@"mods\limits-band", "limits-band", DateTime.UtcNow);
        Assert.Null(ModLibrary.Keep(ModLibrary.List().Single()));
        Assert.False(Directory.Exists(ModLibrary.Directory));
    }

    [Fact(DisplayName = "deleting a mod removes its folder and every choice made for it")]
    public void DeleteForgetsChoices()
    {
        var mica = Mod("mica");
        var p = Profile("Claude-2");
        Choose(p, "mica");
        Assert.Null(ModLibrary.Delete(ModLibrary.List().Single()));
        Assert.False(Directory.Exists(mica));
        Assert.Empty(ModChoices.Load()!.Profiles);
    }

    [Fact(DisplayName = "a mod settings turns on for every Claude is not deleted")]
    public void DeleteRefusesSettingsMod()
    {
        var band = Found(@"mods\limits-band", "limits-band", DateTime.UtcNow);
        File.WriteAllText(ModLibrary.UserSettings, new JsonObject { ["env"] = new JsonObject { [ModLibrary.Variable] = band } }.ToJsonString());
        Assert.NotNull(ModLibrary.Delete(ModLibrary.List().Single()));
        Assert.True(Directory.Exists(band));
    }

    [Fact(DisplayName = "a folder without a manifest naming it is not a mod")]
    public void ManifestNamesMod()
    {
        Mod("mica");
        Directory.CreateDirectory(Path.Combine(ModLibrary.Directory, "loose"));
        Assert.Equal(new[] { "mica" }, ModLibrary.List().Select(m => m.Name));
    }
}
