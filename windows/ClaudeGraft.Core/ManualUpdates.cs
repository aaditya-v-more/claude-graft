using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// <summary>
/// Turns Claude's own automatic updates off, so an update is installed only
/// when somebody asks and Claude never quits mid-task to apply one. Done the
/// way Claude reads it: disableAutoUpdates in the configuration its library
/// applies. On Windows that library is one folder every Claude reads, so one
/// switch covers the main Claude and every shortcut. What is changed is
/// journalled first, and turning the mode off puts back exactly that.
/// </summary>
public static class ManualUpdates
{
    public sealed class SavedKey
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("previous")] public bool? Previous { get; set; }
    }

    public sealed class Change
    {
        [JsonPropertyName("configId")] public string ConfigId { get; set; } = "";
        [JsonPropertyName("keys")] public List<SavedKey> Keys { get; set; } = new();
        [JsonPropertyName("createdConfig")] public bool CreatedConfig { get; set; }
        [JsonPropertyName("createdMetadata")] public bool CreatedMetadata { get; set; }
        [JsonPropertyName("createdLibrary")] public bool CreatedLibrary { get; set; }
        [JsonPropertyName("createdFolder")] public bool CreatedFolder { get; set; }
    }

    public sealed class State
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("change")] public Change? Change { get; set; }
    }

    public sealed class Refusal(string message) : IOException(message);

    private static readonly string[] PolicyKeys = { "disableAutoUpdates", "autoUpdate.disabled" };

    /// Claude's configuration folder; replaced in tests.
    public static Func<string> Folder = () => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-3p");

    /// Whether an administrator's policy can override a local setting, in which
    /// case a switch here would be a promise Claude does not keep. Replaced in tests.
    public static Func<bool> Managed = () =>
        OperatingSystem.IsWindows()
        && (Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Policies\Claude") is not null
            || Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Claude") is not null);

    private static string Library => Path.Combine(Folder(), "configLibrary");
    private static string StateFile => Path.Combine(GraftPaths.OwnData, "manual-updates.json");

    /// An unreadable journal is never read as an empty one: that would forget
    /// what has to be put back.
    public static State Read()
    {
        if (!File.Exists(StateFile)) return new State();
        try { return JsonSerializer.Deserialize<State>(File.ReadAllBytes(StateFile)) ?? throw new JsonException(); }
        catch (JsonException) { throw new Refusal("Graft's saved update settings could not be read."); }
    }

    private static void Save(State state)
    {
        Directory.CreateDirectory(GraftPaths.OwnData);
        AtomicWrite.Bytes(StateFile, JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool Enabled => Read().Enabled;

    public static void SetEnabled(bool enabled) => StateLock.Run(() =>
    {
        if (enabled && Managed()) throw new Refusal(
            "Claude's updates are set by an administrator's policy, which overrides a local setting. Ask them to set disableAutoUpdates.");
        var state = Read();
        state.Enabled = enabled;
        Save(state);
        Synchronize(state);
        Diagnostics.Note("updates.manual-mode", new Dictionary<string, object?> { ["enabled"] = enabled });
    });

    /// Re-applies the saved choice, at start, in case Claude's own settings
    /// window rewrote the configuration since.
    public static void Synchronize()
    {
        if (!File.Exists(StateFile)) return;
        StateLock.Run(() => Synchronize(Read()));
    }

    private static void Synchronize(State state)
    {
        if (state.Enabled) { if (!Managed()) Protect(state); }
        else if (state.Change is { } change)
        {
            Restore(change);
            state.Change = null;
            Save(state);
        }
    }

    private static JsonObject ReadObject(string file, bool optional = false)
    {
        if (optional && !File.Exists(file)) return new JsonObject();
        try
        {
            if (new FileInfo(file).LinkTarget is not null) throw new Refusal($"{Path.GetFileName(file)} is a link.");
            return JsonNode.Parse(File.ReadAllBytes(file)) as JsonObject
                   ?? throw new Refusal($"{Path.GetFileName(file)} could not be read. Its configuration was kept.");
        }
        catch (JsonException) { throw new Refusal($"{Path.GetFileName(file)} could not be read. Its configuration was kept."); }
    }

    private static void WriteObject(JsonObject value, string file) =>
        AtomicWrite.Bytes(file, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));

    private static bool IsId(string? id) => id is not null && Guid.TryParse(id, out var g) && g.ToString() == id;

    private static bool? Bool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static void Protect(State state)
    {
        var folderExisted = Directory.Exists(Folder());
        var libraryExisted = Directory.Exists(Library);
        var metadataFile = Path.Combine(Library, "_meta.json");
        var hadMetadata = File.Exists(metadataFile);
        var metadata = ReadObject(metadataFile, optional: true);
        if (metadata["hybridPointer"] is not null)
            throw new Refusal("Claude's configuration comes from an organisation, which overrides a local setting.");
        if (metadata["entries"] is not null and not JsonArray)
            throw new Refusal("Claude's configuration list could not be read. It was kept.");
        var applied = metadata["appliedId"];
        string? appliedId = applied is JsonValue av && av.TryGetValue<string>(out var a) ? a : null;
        if (applied is not null && !IsId(appliedId))
            throw new Refusal("Claude's selected configuration could not be read. It was kept.");

        var id = appliedId ?? Guid.NewGuid().ToString();
        var file = Path.Combine(Library, id + ".json");
        var config = ReadObject(file, optional: appliedId is null);
        if (config["bootstrapUrl"] is not null || config["bootstrap.url"] is not null)
            throw new Refusal("Claude's configuration comes from an organisation, which overrides a local setting.");

        // A configuration chosen since the last write is a different one: what
        // was saved for the old one no longer describes this.
        if (state.Change is { } stale && stale.ConfigId != id)
        {
            Restore(stale);
            state.Change = null;
        }

        var keys = state.Change?.Keys ?? new List<SavedKey>();
        foreach (var name in PolicyKeys)
        {
            if (config[name] is not { } existing || keys.Any(k => k.Name == name)) continue;
            if (Bool(existing) is not bool previous) throw new Refusal($"{name} in Claude's configuration is not a yes or no.");
            keys.Add(new SavedKey { Name = name, Previous = previous });
        }
        if (keys.Count == 0) keys.Add(new SavedKey { Name = PolicyKeys[0], Previous = null });
        state.Change ??= new Change
        {
            ConfigId = id,
            CreatedConfig = appliedId is null,
            CreatedMetadata = !hadMetadata,
            CreatedLibrary = !libraryExisted,
            CreatedFolder = !folderExisted,
        };
        state.Change.Keys = keys;
        // The undo record lands before anything it undoes.
        Save(state);

        if (keys.Any(k => Bool(config[k.Name]) != true))
        {
            foreach (var k in keys) config[k.Name] = true;
            Directory.CreateDirectory(Library);
            WriteObject(config, file);
        }
        if (appliedId is null)
        {
            var entries = metadata["entries"] as JsonArray ?? new JsonArray();
            if (!entries.Any(e => e?["id"]?.GetValue<string>() == id))
                entries.Add(new JsonObject { ["id"] = id, ["name"] = "Manual updates" });
            metadata["entries"] = entries;
            metadata["appliedId"] = id;
            WriteObject(metadata, metadataFile);
        }
    }

    private static void Restore(Change change)
    {
        if (!IsId(change.ConfigId) || change.Keys.Any(k => !PolicyKeys.Contains(k.Name)))
            throw new Refusal("Graft's saved update settings name something it never changed.");
        var file = Path.Combine(Library, change.ConfigId + ".json");
        if (!File.Exists(file)) return;
        var config = ReadObject(file);
        foreach (var saved in change.Keys)
        {
            // A value changed after this app's write belongs to that change.
            if (Bool(config[saved.Name]) != true) continue;
            if (saved.Previous is bool previous) config[saved.Name] = previous;
            else config.Remove(saved.Name);
        }
        if (!change.CreatedConfig || config.Count > 0)
        {
            WriteObject(config, file);
            return;
        }

        var metadataFile = Path.Combine(Library, "_meta.json");
        var metadata = ReadObject(metadataFile, optional: true);
        if (metadata["appliedId"] is JsonValue v && v.TryGetValue<string>(out var a) && a == change.ConfigId)
            metadata.Remove("appliedId");
        if (metadata["entries"] is JsonArray entries)
        {
            foreach (var entry in entries.Where(e => e?["id"]?.GetValue<string>() == change.ConfigId).ToList())
                entries.Remove(entry);
        }
        else if (metadata["entries"] is not null) throw new Refusal("Claude's configuration list could not be read. It was kept.");

        var onlyEmptyEntries = metadata.All(p => p.Key == "entries") && (metadata["entries"] as JsonArray ?? new JsonArray()).Count == 0;
        if (change.CreatedMetadata && onlyEmptyEntries) File.Delete(metadataFile);
        else if (File.Exists(metadataFile)) WriteObject(metadata, metadataFile);
        File.Delete(file);
        // Only folders this mode created go, and only while still empty.
        if (change.CreatedLibrary) RemoveIfEmpty(Library);
        if (change.CreatedFolder) RemoveIfEmpty(Folder());
    }

    private static void RemoveIfEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (IOException) { }
    }
}
