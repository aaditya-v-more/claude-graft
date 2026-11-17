using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>
/// Graft's entry in Claude Code's own settings, which has every Claude it runs
/// report a turn's end and a permission it waits on to <c>GraftLaunch --signal</c>.
/// The session records never say a permission is pending; only this does.
///
/// The file belongs to the person. Graft adds one entry per event and takes out
/// only its own, known by the launcher and flag it names, and never writes a
/// file it could not read.
/// </summary>
public static class ChimeHook
{
    public const string Flag = "--signal";
    private static readonly string[] Events = ["Notification", "Stop"];

    public static string SettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// Where the launcher leaves each event for Graft to pick up.
    public static string SignalsDirectory => Path.Combine(GraftPaths.OwnData, "signals");

    public static string Command(string launcher) => $"\"{launcher}\" {Flag}";

    public static bool IsOurs(string? command) =>
        command is not null
        && command.Contains("GraftLaunch", StringComparison.OrdinalIgnoreCase)
        && command.TrimEnd().EndsWith(Flag, StringComparison.Ordinal);

    /// Puts Graft's entry in, replacing one that names another launcher. False
    /// where the file cannot be read as an object, which is left as it is.
    public static bool Install(string file, string launcher) =>
        Rewrite(file, settings =>
        {
            var hooks = settings["hooks"] as JsonObject ?? new JsonObject();
            settings["hooks"] = hooks;
            TakeOut(hooks);
            foreach (var name in Events)
            {
                var groups = hooks[name] as JsonArray ?? new JsonArray();
                hooks[name] = groups;
                groups.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command(launcher) }),
                });
            }
        });

    /// Takes Graft's entry out and nothing else. A file with none is not touched.
    public static bool Remove(string file) =>
        !File.Exists(file) || Rewrite(file, settings =>
        {
            if (settings["hooks"] is not JsonObject hooks || !TakeOut(hooks)) return;
            if (hooks.Count == 0) settings.Remove("hooks");
        });

    public static bool IsInstalled(string file, string launcher)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject { } settings
                || settings["hooks"] is not JsonObject hooks) return false;
            return Events.All(name => (hooks[name] as JsonArray)?.OfType<JsonObject>()
                .SelectMany(g => (g["hooks"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Any(h => Text(h["command"]) == Command(launcher)) == true);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // Each group of ours is dropped whole, and an event left with none goes too.
    private static bool TakeOut(JsonObject hooks)
    {
        var removed = false;
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray groups) continue;
            var here = false;
            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray entries) continue;
                var ours = entries.OfType<JsonObject>().Where(h => IsOurs(Text(h["command"]))).ToList();
                if (ours.Count == 0) continue;
                foreach (var h in ours) entries.Remove(h);
                if (entries.Count == 0) groups.Remove(group);
                here = removed = true;
            }
            if (here && groups.Count == 0) hooks.Remove(name);
        }
        return removed;
    }

    private static bool Rewrite(string file, Action<JsonObject> change)
    {
        string? before = null;
        JsonObject settings;
        try
        {
            if (File.Exists(file))
            {
                before = File.ReadAllText(file);
                if (JsonNode.Parse(before) is not JsonObject read) return false;
                settings = read;
            }
            else settings = new JsonObject();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }

        change(settings);
        var after = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (before is not null && Normalised(before) == Normalised(after)) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicWrite.Bytes(file, new UTF8Encoding(false).GetBytes(after));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // A file Graft changed nothing in is left byte for byte, whatever its spacing.
    private static string Normalised(string json)
    {
        try { return JsonNode.Parse(json)!.ToJsonString(); }
        catch (JsonException) { return json; }
    }
}
