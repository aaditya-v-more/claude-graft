using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>
/// Claude Code mods kept in Graft's own folder, and which Claude loads which.
/// A mod is handed to a Claude by folder through CLAUDE_CODE_PLUGIN_DIRS in its
/// Code environment, where it replaces the list in ~/.claude/settings.json
/// rather than joining it, so the list Graft writes carries that one too.
/// </summary>
public static class ModLibrary
{
    public const string Variable = "CLAUDE_CODE_PLUGIN_DIRS";

    public static string Directory => Path.Combine(GraftPaths.OwnData, "mods");
    public static string ChoicesFile => Path.Combine(GraftPaths.OwnData, "mods.json");

    /// Redirected by the test suite so the person's own Claude Code folder is never read.
    public static string? ClaudeHomeOverride { get; set; }
    public static string ClaudeHome => ClaudeHomeOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    public static string UserSettings => Path.Combine(ClaudeHome, "settings.json");

    public enum Origin { Library, Catalogue, Mods, InProgress }

    public sealed record Mod(string Name, string Folder, string Description, string? Version, Origin Origin = Origin.Library);

    /// Graft's own copies first, then what Claude Code keeps: finished mods
    /// under mods/, and those still being written under dev-mods/<session>/.
    /// A name met twice is the first one met; among mods in progress, the one
    /// changed last.
    public static List<Mod> List()
    {
        var mods = new List<Mod>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Take(IEnumerable<string> folders, Origin origin)
        {
            foreach (var folder in folders)
                if (Read(folder, origin) is { } mod && seen.Add(mod.Name)) mods.Add(mod);
        }
        Take(Children(Directory).Order(StringComparer.OrdinalIgnoreCase), Origin.Library);
        Take(Children(Path.Combine(ClaudeHome, "mods")).Order(StringComparer.OrdinalIgnoreCase), Origin.Mods);
        Take(Children(Path.Combine(ClaudeHome, "dev-mods")).SelectMany(Children)
            .OrderByDescending(f => Changed(f)), Origin.InProgress);
        return mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Children(string folder)
    {
        try
        {
            return System.IO.Directory.Exists(folder)
                ? System.IO.Directory.GetDirectories(folder).Where(f => !Path.GetFileName(f).StartsWith('.')).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static DateTime Changed(string folder)
    {
        try { return File.GetLastWriteTimeUtc(Path.Combine(folder, "hooks", "register.tsx")) is var t && t.Year > 1601 ? t : System.IO.Directory.GetLastWriteTimeUtc(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// A folder is a mod only when its manifest reads and names it.
    private static Mod? Read(string folder, Origin origin)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(folder, ".claude-plugin", "plugin.json"))) is not JsonObject manifest) return null;
            if (manifest["name"]?.GetValue<string>() is not { Length: > 0 } name) return null;
            if (origin == Origin.Library && File.Exists(Path.Combine(folder, ModCatalog.SourceFile))) origin = Origin.Catalogue;
            return new Mod(name, folder, manifest["description"]?.GetValue<string>() ?? "", manifest["version"]?.GetValue<string>(), origin);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    /// Copies a mod's folder into the library and answers its name, or why it
    /// was not added. A copy, so a mod still being written elsewhere changes in
    /// a Claude only when it is added again.
    public static (string? Name, string? Problem) Add(string source)
    {
        var manifest = Path.Combine(source, ".claude-plugin", "plugin.json");
        string? name;
        try { name = (JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject)?["name"]?.GetValue<string>(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { name = null; }
        if (string.IsNullOrWhiteSpace(name)) return (null, "That folder has no .claude-plugin\\plugin.json naming a mod.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith('.')) return (null, $"\"{name}\" cannot be a folder name.");
        var target = Path.Combine(Directory, name);
        if (Path.GetFullPath(source).TrimEnd('\\').Equals(Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return (name, null);
        var temp = Path.Combine(Directory, $".adding-{Guid.NewGuid():N}");
        try
        {
            CopyTree(source, temp);
            if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, recursive: true);
            System.IO.Directory.Move(temp, target);
            return (name, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { System.IO.Directory.Delete(temp, recursive: true); } catch { }
            return (null, "The mod could not be copied: " + e.Message);
        }
    }

    /// A mod made in a session lives in a folder swept with that session's data,
    /// so a Claude is given Graft's copy of it, never the folder itself.
    public static string? Keep(Mod mod) => mod.Origin == Origin.InProgress ? Add(mod.Folder).Problem : null;

    /// Sends a mod's folder to the Recycle Bin and forgets every choice made for
    /// it. One every Claude loads from settings is refused: settings would go on
    /// naming a folder that is not there, and Graft does not edit settings.
    /// Redirected by the test suite, which has no business filling the Recycle Bin.
    public static Action<string>? RemoveFolderOverride { get; set; }

    public static string? Delete(Mod mod)
    {
        if (SharedFolders().Contains(mod.Folder, StringComparer.OrdinalIgnoreCase))
            return $"Claude Code's settings turn {mod.Name} on for every Claude. Take it out of CLAUDE_CODE_PLUGIN_DIRS in "
                + $"{UserSettings} first.";
        if (ModChoices.Load() is not { } choices) return "The list of chosen mods could not be read, so nothing was deleted.";
        try
        {
            if (RemoveFolderOverride is { } remove) remove(mod.Folder);
            else if (OperatingSystem.IsWindows())
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(mod.Folder,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else System.IO.Directory.Delete(mod.Folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return "The mod could not be deleted: " + e.Message;
        }
        // Another copy by the same name may still be listed, and a choice
        // carried over to it would load something nobody ticked.
        foreach (var lists in new[] { choices.Profiles, choices.Off })
            foreach (var key in lists.Keys.ToList())
            {
                lists[key].RemoveAll(m => m.Equals(mod.Name, StringComparison.OrdinalIgnoreCase));
                if (lists[key].Count == 0) lists.Remove(key);
            }
        choices.Save();
        return null;
    }

    // The engine writes its own types into a loaded mod's folder; they are
    // rewritten wherever it loads next, so they are left behind.
    private static void CopyTree(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);
        foreach (var file in System.IO.Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in System.IO.Directory.GetDirectories(from))
        {
            var leaf = Path.GetFileName(dir);
            if (leaf is "node_modules" or ".git" || (leaf == "types" && Path.GetFileName(from) == ".claude-plugin")) continue;
            if (new DirectoryInfo(dir).LinkTarget is not null) continue;
            CopyTree(dir, Path.Combine(to, leaf));
        }
    }

    /// The folders every Claude loads from settings, which a list of Graft's
    /// would otherwise switch off for the Claude it is given to.
    public static List<string> SharedFolders()
    {
        try
        {
            if (!File.Exists(UserSettings)) return new();
            var value = (JsonNode.Parse(File.ReadAllText(UserSettings)) as JsonObject)?["env"]?[Variable]?.GetValue<string>();
            return Split(value);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return new(); }
    }

    public static List<string> Split(string? value) =>
        (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// Null when the Claude should be given no list of Graft's at all. Empty is
    /// a list too: it loads nothing, the settings' folders included.
    public static string? ValueFor(IEnumerable<string> chosen, IEnumerable<string> off, IReadOnlyList<Mod> library, IReadOnlyList<string> shared)
    {
        var folders = library.Where(m => chosen.Contains(m.Name, StringComparer.OrdinalIgnoreCase)).Select(m => m.Folder).ToList();
        var dropped = library.Where(m => off.Contains(m.Name, StringComparer.OrdinalIgnoreCase)).Select(m => m.Folder)
            .Where(f => shared.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        if (folders.Count == 0 && dropped.Count == 0) return null;
        var all = shared.Except(dropped, StringComparer.OrdinalIgnoreCase).Concat(folders).Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(Path.PathSeparator, all);
    }
}

/// <summary>
/// What each Claude was asked to load, and the exact value Graft last wrote
/// into it: a name holding that value is Graft's, anything else is the person's.
/// </summary>
public sealed record ModChoices
{
    /// Mod names by profile key.
    public Dictionary<string, List<string>> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// Mods from settings that a Claude is not to load, by profile key.
    public Dictionary<string, List<string>> Off { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// The value Graft wrote, by profile key.
    public Dictionary<string, string> Written { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// Null when the file is there and will not read: saving over it would
    /// forget which values are Graft's.
    public static ModChoices? Load()
    {
        try
        {
            if (!File.Exists(ModLibrary.ChoicesFile)) return new ModChoices();
            var read = JsonSerializer.Deserialize<ModChoices>(File.ReadAllText(ModLibrary.ChoicesFile), Json);
            return read is null ? null : new ModChoices
            {
                Profiles = new(read.Profiles, StringComparer.OrdinalIgnoreCase),
                Off = new(read.Off ?? new(), StringComparer.OrdinalIgnoreCase),
                Written = new(read.Written, StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public bool Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(GraftPaths.OwnData);
            var temp = Path.Combine(GraftPaths.OwnData, $".mods-{Guid.NewGuid():N}");
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, ModLibrary.ChoicesFile, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

public static class ModEnvironment
{
    public enum State { Off, On, Waiting, Conflict, Unreadable }

    /// One pass over every Claude with a choice or a value of Graft's left in
    /// it. A Claude is written only while closed and under the launch lock,
    /// since an open one saves its whole environment back over any write. Run
    /// before the shared-usage pass, which then reads what this one left.
    public static Dictionary<string, State> Settle(IReadOnlyList<string> profiles, Func<string, byte[]?> keyFor, Func<string, bool> running)
    {
        var states = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        if (ModChoices.Load() is not { } choices) return states;
        var library = ModLibrary.List();
        var shared = ModLibrary.SharedFolders();
        var paths = profiles.ToDictionary(PoolEnvironment.Key, p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var key in choices.Profiles.Keys.Concat(choices.Off.Keys).Concat(choices.Written.Keys)) paths.TryAdd(key, key);

        foreach (var (key, path) in paths)
        {
            var wanted = ModLibrary.ValueFor(choices.Profiles.GetValueOrDefault(key) ?? new(), choices.Off.GetValueOrDefault(key) ?? new(), library, shared);
            var ours = choices.Written.GetValueOrDefault(key);
            if (wanted is null && ours is null) continue;
            if (!System.IO.Directory.Exists(path))
            {
                choices.Written.Remove(key);
                continue;
            }
            states[key] = SettleOne(path, key, wanted, ours, keyFor, running, choices);
        }
        choices.Save();
        return states;
    }

    private static State SettleOne(string path, string key, string? wanted, string? ours,
        Func<string, byte[]?> keyFor, Func<string, bool> running, ModChoices choices)
    {
        if (keyFor(path) is not { } secret || Variables(path, secret) is not { } variables) return State.Unreadable;
        var name = variables.Keys.FirstOrDefault(n => n.Equals(ModLibrary.Variable, StringComparison.OrdinalIgnoreCase));
        var current = name is null ? null : variables[name];
        if (current == wanted)
        {
            if (wanted is null) choices.Written.Remove(key);
            else if (current == ours) return State.On;
            else return State.Conflict;
            return State.Off;
        }
        // A list of the person's own is never replaced, and once it is there
        // Graft has nothing of its own left in that Claude to account for.
        if (current is not null && current != ours)
        {
            choices.Written.Remove(key);
            return wanted is null ? State.Off : State.Conflict;
        }
        if (running(path)) return State.Waiting;

        var state = State.Waiting;
        SidebarSync.WithLaunchLock(() =>
        {
            if (running(path) || Variables(path, secret) is not { } again
                || !SameValue(again, current)) return;
            if (name is not null) again.Remove(name);
            if (wanted is not null) again[ModLibrary.Variable] = wanted;
            var sealedValue = again.Count == 0 ? null : ClaudeCodeEnvironment.Seal(again, secret);
            var written = PoolEnvironment.Write(sealedValue, path);
            Diagnostics.Note("mods.environment-written", new Dictionary<string, object?> { ["profile"] = Path.GetFileName(path), ["mods"] = wanted is not null, ["ok"] = written });
            if (!written) return;
            if (wanted is null) choices.Written.Remove(key);
            else choices.Written[key] = wanted;
            state = wanted is null ? State.Off : State.On;
        });
        return state;
    }

    private static bool SameValue(Dictionary<string, string> variables, string? expected) =>
        variables.FirstOrDefault(v => v.Key.Equals(ModLibrary.Variable, StringComparison.OrdinalIgnoreCase)).Value == expected;

    /// Null when the field cannot be read through; empty when there is none.
    private static Dictionary<string, string>? Variables(string profile, byte[] key)
    {
        var field = PoolEnvironment.Read(profile);
        if (field is PoolEnvironment.FieldValue.Unreadable) return null;
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (field.Text is not string text) return variables;
        if (ClaudeCodeEnvironment.Unseal(text, key) is not JsonObject obj) return null;
        foreach (var (name, value) in obj)
        {
            if (value is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
            variables[name] = s;
        }
        return variables;
    }

    /// For Graft going: every closed Claude still holding exactly Graft's value
    /// has it taken out. Answers the Claudes left holding it.
    public static List<string> TakeOut(Func<string, byte[]?> keyFor, Func<string, bool> running)
    {
        var left = new List<string>();
        if (ModChoices.Load() is not { } choices) return left;
        choices.Profiles.Clear();
        choices.Off.Clear();
        foreach (var (key, ours) in choices.Written.ToList())
            if (System.IO.Directory.Exists(key) && SettleOne(key, key, null, ours, keyFor, running, choices) != State.Off) left.Add(key);
        choices.Save();
        return left;
    }
}
