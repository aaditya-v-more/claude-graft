using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeGraft.Core;

/// <summary>Which half of a pair, in the order its key was written: the borrower is One.</summary>
public enum MirrorSide { One, Other }

/// <summary>
/// What a pass knows about one record beyond its whole-file digest: each top-level
/// field as it stood at the baseline, so two sides that both moved can each keep
/// what they changed.
/// </summary>
public sealed record RecordBaseline(string Digest, Dictionary<string, string>? Fields, double? Activity);

/// <summary>One record out of two, and the fields both sides changed to different values.</summary>
public sealed record FieldMerge(JsonObject Record, List<string> Contested);

public static partial class Graft
{
    private static readonly JsonSerializerOptions RecordJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// A chat's record, as opposed to a deletion marker, which has no inside.
    public static bool IsRecordName(string name) => name.StartsWith("local_") && name.EndsWith(".json");

    /// Compact JSON with every object's keys sorted, so two equal records always
    /// encode, and digest, alike.
    public static string Canonical(JsonNode? node)
    {
        var text = new StringBuilder();
        void Write(JsonNode? n)
        {
            switch (n)
            {
                case null: text.Append("null"); break;
                case JsonObject o:
                    text.Append('{');
                    var first = true;
                    foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (!first) text.Append(',');
                        first = false;
                        text.Append(JsonSerializer.Serialize(k, RecordJson)).Append(':');
                        Write(v);
                    }
                    text.Append('}');
                    break;
                case JsonArray a:
                    text.Append('[');
                    for (var i = 0; i < a.Count; i++) { if (i > 0) text.Append(','); Write(a[i]); }
                    text.Append(']');
                    break;
                default: text.Append(n.ToJsonString(RecordJson)); break;
            }
        }
        Write(node);
        return text.ToString();
    }

    public static byte[] EncodeRecord(JsonObject record) => Encoding.UTF8.GetBytes(Canonical(record));

    public static JsonObject? RecordObject(byte[] data)
    {
        try { return JsonNode.Parse(data) as JsonObject; }
        catch { return null; }
    }

    public static string FieldDigest(JsonNode? value) => Digest(Encoding.UTF8.GetBytes(Canonical(value)));

    public static Dictionary<string, string> FieldDigests(JsonObject record) =>
        record.ToDictionary(p => p.Key, p => FieldDigest(p.Value));

    private static string? FieldDigestOf(JsonObject record, string key) =>
        record.TryGetPropertyValue(key, out var v) ? FieldDigest(v) : null;

    // Read through the JSON text: a value built in code as an integer will not hand itself over as a double.
    private static double? Number(JsonObject? record, string key) =>
        record is not null && record.TryGetPropertyValue(key, out var v) && v is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    private static bool SameBaselines(Dictionary<string, RecordBaseline> a, Dictionary<string, RecordBaseline> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && o.Digest == kv.Value.Digest && o.Activity == kv.Value.Activity
            && (o.Fields is null ? kv.Value.Fields is null
                : kv.Value.Fields is not null && o.Fields.Count == kv.Value.Fields.Count && !o.Fields.Except(kv.Value.Fields).Any()));

    public static bool SameRecord(JsonObject a, JsonObject b) => Canonical(a) == Canonical(b);

    /// <summary>
    /// One record out of two that both moved since they last agreed, field by field.
    /// A field one side changed takes the change, so an archive survives a glance on
    /// the other side and so does an unarchive. Only a field both changed to different
    /// values is contested: a time takes the later, an archive state the unarchived one,
    /// anything else the side <paramref name="prefer"/> names. A side whose
    /// <c>resumedAt</c> moved has decided the archive state even when the flag came
    /// back to where it started, since Claude sets it whenever it unarchives.
    /// </summary>
    public static FieldMerge MergeRecordFields(JsonObject one, JsonObject other,
        IReadOnlyDictionary<string, string> @base, MirrorSide prefer)
    {
        var merged = new JsonObject();
        var contested = new List<string>();
        string? Base(string key) => @base.TryGetValue(key, out var d) ? d : null;
        bool Moved(JsonObject r, string key) => FieldDigestOf(r, key) != Base(key);
        bool DecidedArchive(JsonObject r) => Moved(r, "isArchived") || Moved(r, "resumedAt");

        var keys = new SortedSet<string>(one.Select(p => p.Key), StringComparer.Ordinal);
        keys.UnionWith(other.Select(p => p.Key));
        keys.UnionWith(@base.Keys);
        foreach (var key in keys)
        {
            var hasA = one.TryGetPropertyValue(key, out var a);
            var hasB = other.TryGetPropertyValue(key, out var b);
            var da = hasA ? FieldDigest(a) : null;
            var db = hasB ? FieldDigest(b) : null;
            var movedOne = key == "isArchived" ? DecidedArchive(one) : da != Base(key);
            var movedOther = key == "isArchived" ? DecidedArchive(other) : db != Base(key);
            bool pickA;
            if (da == db) pickA = true;
            else if (!movedOne) pickA = false;
            else if (!movedOther) pickA = true;
            else
            {
                contested.Add(key);
                if (key == "isArchived")
                    // Archived on one side, brought back on the other: an archive lost
                    // costs a click, a chat still in use gone from view costs the chat.
                    pickA = !(a is JsonValue av && av.GetValueKind() == JsonValueKind.True);
                else if (key.EndsWith("At") && Number(one, key) is double x && Number(other, key) is double y)
                    pickA = x >= y;
                else pickA = prefer == MirrorSide.One;
            }
            if (pickA ? hasA : hasB) merged[key] = (pickA ? a : b)?.DeepClone();
        }
        return new FieldMerge(merged, contested);
    }

    // MARK: Field baselines, kept beside the state file: a couple of megabytes a
    // pair for a long history, read only by that pair's pass.

    public static string MirrorFieldsFolder => Path.Combine(GraftPaths.OwnData, "mirror-fields");

    public static string MirrorFieldsFile(string key) =>
        Path.Combine(MirrorFieldsFolder, Digest(Encoding.UTF8.GetBytes(key)) + ".json");

    public static Dictionary<string, RecordBaseline> LoadRecordBaselines(string key)
    {
        var result = new Dictionary<string, RecordBaseline>();
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(MirrorFieldsFile(key))) is not JsonObject top
                || Number(top, "version") != 1 || (string?)top["pair"] != key
                || top["records"] is not JsonObject entries) return result;
            foreach (var (name, node) in entries)
            {
                if (node is not JsonObject entry || entry["d"]?.GetValue<string>() is not string digest) continue;
                var fields = entry["f"] is JsonObject f ? f.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()) : null;
                result[name] = new RecordBaseline(digest, fields, Number(entry, "a"));
            }
        }
        catch { }
        return result;
    }

    public static void SaveRecordBaselines(Dictionary<string, RecordBaseline> records, string key)
    {
        var file = MirrorFieldsFile(key);
        try
        {
            if (records.Count == 0) { File.Delete(file); return; }
            var entries = new JsonObject();
            foreach (var (name, r) in records)
            {
                var entry = new JsonObject { ["d"] = r.Digest };
                if (r.Fields is not null) entry["f"] = new JsonObject(r.Fields.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
                if (r.Activity is double a) entry["a"] = a;
                entries[name] = entry;
            }
            Directory.CreateDirectory(MirrorFieldsFolder);
            AtomicWrite.Bytes(file, Encoding.UTF8.GetBytes(Canonical(new JsonObject { ["version"] = 1, ["pair"] = key, ["records"] = entries })));
        }
        catch { }
    }

    /// A pair no longer mirrored has no use for its fields, and one mirrored again
    /// must start from what its two sides hold then.
    public static void ForgetRecordBaselines(IEnumerable<string> keys)
    {
        var wanted = keys.Select(k => Path.GetFileName(MirrorFieldsFile(k))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(MirrorFieldsFolder, "*.json"))
                if (!wanted.Contains(Path.GetFileName(file))) File.Delete(file);
        }
        catch { }
    }

    // MARK: Splits

    /// What a Claude is called where people see it: Claude's own profile "Claude",
    /// a shortcut the name it was given.
    public static string ClaudeName(string profile)
    {
        if (Fs.SamePath(profile, GraftPaths.DefaultProfile)) return "Claude";
        var folder = Path.GetFileName(profile.TrimEnd('\\'));
        var named = new ShortcutStore().Shortcuts.FirstOrDefault(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))?.Name;
        return string.IsNullOrEmpty(named) ? folder : named;
    }

    /// A chat store folder is &lt;profile&gt;\&lt;store&gt;\&lt;account&gt;\&lt;organization&gt;.
    public static string ProfileOfChatFolder(string folder) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(folder.TrimEnd('\\'))!)!)!;

    /// A split chat's title: what it was called, then the Claude that carried this
    /// half on. A half split again keeps the one mark.
    public static string SplitTitle(JsonNode? title, string claude)
    {
        var mark = $" ({claude})";
        var text = title is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        var @base = string.IsNullOrWhiteSpace(text) ? "Chat" : text;
        return @base.EndsWith(mark, StringComparison.Ordinal) ? @base : @base + mark;
    }

    // MARK: Unfiled chats

    // Claude files a chat under "This computer" only when its originCwd lies in the
    // open profile's own scratch-workspaces, so each side gets a local alias for it.
    private static (JsonObject Record, string Cwd, string Suffix)? ScratchRecord(byte[] data)
    {
        if (RecordObject(data) is not JsonObject record
            || record["sessionId"] is not JsonValue
            || record.ContainsKey("sshConfig") || record.ContainsKey("wslConfig")
            || (string?)(record["cwd"] as JsonValue) is not string cwd
            || (string?)(record["originCwd"] as JsonValue) is not string origin) return null;
        string? Suffix(string path)
        {
            var prefix = Path.GetFullPath(GraftPaths.ProfilesRoot).TrimEnd('\\') + "\\";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var parts = path[prefix.Length..].Split('\\', '/');
            if (parts.Length != 5 || !parts[1].Equals("scratch-workspaces", StringComparison.OrdinalIgnoreCase)
                || parts.Any(p => p is "" or "." or "..")
                || !ScratchFolder().IsMatch(parts[4])) return null;
            return string.Join('\\', parts[2..]);
        }
        try
        {
            if (Suffix(cwd) is not string relative || !string.Equals(Suffix(origin), relative, StringComparison.OrdinalIgnoreCase)) return null;
            return (record, cwd, relative);
        }
        catch { return null; }
    }

    [GeneratedRegex(@"^scratch-\d{4}-\d{2}-\d{2}-[0-9a-f]{6}$")]
    private static partial Regex ScratchFolder();

    /// A local origin alias is not an edit to a shared conversation.
    private static byte[] MirrorData(byte[] data)
    {
        if (ScratchRecord(data) is not var (record, cwd, _)) return data;
        record["originCwd"] = cwd;
        return EncodeRecord(record);
    }

    private static byte[] LocalScratchOrigin(byte[] data, string folder)
    {
        if (ScratchRecord(data) is not var (record, cwd, suffix)) return data;
        var store = Path.GetDirectoryName(Path.GetDirectoryName(folder.TrimEnd('\\'))!)!;
        if (!Path.GetFileName(store).Equals("claude-code-sessions", StringComparison.OrdinalIgnoreCase)) return data;
        var profile = Path.GetDirectoryName(store)!;
        if (!Fs.SamePath(Path.GetDirectoryName(profile)!, GraftPaths.ProfilesRoot)) return data;
        var local = Path.Combine(profile, "scratch-workspaces", suffix);
        if (string.Equals((string?)(record["originCwd"] as JsonValue), local, StringComparison.OrdinalIgnoreCase)) return data;
        try
        {
            if (!Fs.SamePath(local, cwd))
            {
                if (!Fs.IsDirectory(cwd)) return data;
                if (Fs.Exists(local))
                {
                    if (!Fs.SamePath(Fs.Resolve(local), Fs.Resolve(cwd))) return data;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    Junction.Create(local, cwd);
                }
            }
            record["originCwd"] = local;
            return EncodeRecord(record);
        }
        catch (Exception e)
        {
            Diagnostics.Note("scratch.origin-failed", new Dictionary<string, object?>
                { ["profile"] = Path.GetFileName(profile), ["reason"] = e.Message });
            return data;
        }
    }
}
