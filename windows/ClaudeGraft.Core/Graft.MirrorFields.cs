using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

public static partial class Graft
{
    internal sealed record RecordBaseline
    {
        [JsonPropertyName("d")] public required string Digest { get; init; }
        [JsonPropertyName("f")] public Dictionary<string, string>? Fields { get; init; }
        [JsonPropertyName("a")] public double? Activity { get; init; }
    }

    private sealed class RecordBaselines
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("pair")] public string Pair { get; set; } = "";
        [JsonPropertyName("records")] public Dictionary<string, RecordBaseline> Records { get; set; } = new();
    }

    private static string MirrorFieldsFolder => Path.Combine(GraftPaths.OwnData, "mirror-fields");
    internal static string MirrorFieldsFile(string key) =>
        Path.Combine(MirrorFieldsFolder, Digest(System.Text.Encoding.UTF8.GetBytes(key)) + ".json");

    private static Dictionary<string, RecordBaseline> LoadRecordBaselines(string key)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<RecordBaselines>(File.ReadAllBytes(MirrorFieldsFile(key)));
            if (saved is { Version: 1 } && saved.Pair == key && saved.Records is not null
                && saved.Records.Values.All(v => v is not null && v.Digest is not null)) return saved.Records;
        }
        catch { }
        // These are optional evidence, never the deletion baseline. An old or
        // unreadable set falls back to whole-record conflict handling.
        return new();
    }

    private static void SaveRecordBaselines(string key, Dictionary<string, RecordBaseline> records)
    {
        var file = MirrorFieldsFile(key);
        try
        {
            if (records.Count == 0) { File.Delete(file); return; }
            var data = JsonSerializer.SerializeToUtf8Bytes(new RecordBaselines { Pair = key, Records = records });
            if (File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(data)) return;
            Directory.CreateDirectory(MirrorFieldsFolder);
            AtomicWrite.Bytes(file, data);
        }
        catch { }
    }

    private static void ForgetRecordBaselines(IEnumerable<string> keys)
    {
        var wanted = keys.Select(k => Path.GetFileName(MirrorFieldsFile(k))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SafeEntries(MirrorFieldsFolder).Where(n => n.EndsWith(".json") && !wanted.Contains(n)))
            try { File.Delete(Path.Combine(MirrorFieldsFolder, name)); } catch { }
    }

    internal static JsonObject? RecordObject(byte[] data)
    {
        try { return JsonNode.Parse(data) as JsonObject; } catch { return null; }
    }

    // Object order is not a field change. Arrays retain order, and a missing
    // property remains distinct from one whose JSON value is explicitly null.
    private static JsonNode? SortedNode(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, SortedNode(kv.Value)))),
        JsonArray array => new JsonArray(array.Select(SortedNode).ToArray()),
        _ => node?.DeepClone(),
    };

    internal static byte[] RecordBytes(JsonNode? node) => JsonSerializer.SerializeToUtf8Bytes(SortedNode(node));
    private static string FieldDigest(JsonNode? node) => Digest(RecordBytes(node));
    private static string? FieldHash(JsonObject record, string key) =>
        record.TryGetPropertyValue(key, out var value) ? FieldDigest(value) : null;
    private static Dictionary<string, string> FieldDigests(JsonObject record) =>
        record.ToDictionary(kv => kv.Key, kv => FieldDigest(kv.Value));
    private static double? RecordNumber(JsonObject? record, string key) =>
        record?[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
    private static string? RecordString(JsonObject? record, string key) =>
        record?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static JsonObject MergeRecordFields(JsonObject one, JsonObject other,
        IReadOnlyDictionary<string, string> baseline, bool preferOne)
    {
        string? Before(string key) => baseline.TryGetValue(key, out var value) ? value : null;
        bool Moved(JsonObject record, string key) => FieldHash(record, key) != Before(key);
        bool ArchiveDecided(JsonObject record) => Moved(record, "isArchived") || Moved(record, "resumedAt");
        var merged = new JsonObject();
        foreach (var key in one.Select(kv => kv.Key).Union(other.Select(kv => kv.Key)).Union(baseline.Keys).Order())
        {
            var a = FieldHash(one, key);
            var b = FieldHash(other, key);
            var movedOne = key == "isArchived" ? ArchiveDecided(one) : a != Before(key);
            var movedOther = key == "isArchived" ? ArchiveDecided(other) : b != Before(key);
            bool pickOne;
            if (a == b) pickOne = true;
            else if (!movedOne) pickOne = false;
            else if (!movedOther) pickOne = true;
            else if (key == "isArchived")
                pickOne = !(one[key] is JsonValue flag && flag.TryGetValue<bool>(out var archived) && archived);
            else if (key.EndsWith("At", StringComparison.Ordinal)
                && RecordNumber(one, key) is double x && RecordNumber(other, key) is double y) pickOne = x >= y;
            else pickOne = preferOne;
            if ((pickOne ? one : other).TryGetPropertyValue(key, out var selected)) merged[key] = selected?.DeepClone();
        }
        return merged;
    }

    private static string ProfileOfChatFolder(string folder) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!)!;

    private static string ClaudeName(string profile)
    {
        if (Fs.SamePath(profile, GraftPaths.DefaultProfile)) return "Claude";
        return new ShortcutStore().Shortcuts.FirstOrDefault(s => Fs.SamePath(s.ProfileDir, profile))?.Name
            ?? Path.GetFileName(profile);
    }

    private static string SplitTitle(JsonObject record, string name)
    {
        var title = RecordString(record, "title");
        if (string.IsNullOrWhiteSpace(title)) title = "Chat";
        var mark = $" ({name})";
        return title.EndsWith(mark, StringComparison.Ordinal) ? title : title + mark;
    }
}
