using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>
/// Claude keeps the order of the sidebar's project headings per account, so two
/// accounts sharing a history drift apart as soon as one is reordered. The newer
/// order wins and is copied to the others. A saved environment (<c>preset::&lt;id&gt;</c>)
/// is named differently in each account, so it is matched by name and target, then
/// by folder, and left out where the other account has no equivalent.
/// </summary>
public static partial class SidebarSync
{
    public const string ProjectOrderPrefix = "LSS-persisted.code-projects-order.";
    public const string PresetsPrefix = "persisted.ccd-session-presets.";

    public sealed record ProjectInput(string Path, string? Order, string? Presets);

    /// Test seam for the helper, as <see cref="StorageOverride"/> is for the pin pass.
    public static Func<string, JsonArray, Dictionary<string, Dictionary<string, string>>>? ProjectStorageOverride { get; set; }

    public static List<string> TranslateProjectOrder(IReadOnlyList<string> order, IReadOnlyList<JsonObject> source, IReadOnlyList<JsonObject> destination)
    {
        static string Canonical(JsonNode? value) => value is JsonObject or JsonArray ? Graft.Canonical(value) : "";
        static string? Text(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        var byNameAndTarget = new Dictionary<string, string>();
        var byFolder = new Dictionary<string, string>();
        var destinationIds = new HashSet<string>();
        foreach (var preset in destination)
        {
            if (Text(preset["id"]) is not string id) continue;
            destinationIds.Add(id);
            var target = preset["target"] as JsonObject;
            byNameAndTarget.TryAdd((Text(preset["name"]) ?? "") + "\0" + Canonical(target), id);
            if (Text(target?["cwd"]) is string folder) byFolder.TryAdd(folder, id);
        }
        var sourceById = new Dictionary<string, JsonObject>();
        foreach (var preset in source)
            if (Text(preset["id"]) is string id) sourceById[id] = preset;
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var key in order)
        {
            string? mapped = key;
            if (key.StartsWith("preset::", StringComparison.Ordinal))
            {
                var id = key["preset::".Length..];
                mapped = null;
                if (sourceById.TryGetValue(id, out var preset))
                {
                    var target = preset["target"] as JsonObject;
                    var match = byNameAndTarget.TryGetValue((Text(preset["name"]) ?? "") + "\0" + Canonical(target), out var m) ? m
                        : Text(target?["cwd"]) is string folder && byFolder.TryGetValue(folder, out var f) ? f : null;
                    mapped = match is null ? null : "preset::" + match;
                }
                else if (destinationIds.Contains(id)) mapped = key;
            }
            if (mapped is not null && seen.Add(mapped)) result.Add(mapped);
        }
        return result;
    }

    /// <summary>
    /// Which profiles get which stored value. Only a strictly newer order is copied,
    /// and the copy keeps its time, so the next pass finds nothing to do and two
    /// orders saved at the same moment never take turns overwriting.
    /// </summary>
    public static Dictionary<string, string> PlanProjectOrders(IReadOnlyList<ProjectInput> inputs)
    {
        static JsonObject? Parse(string? text)
        {
            if (text is null) return null;
            try { return JsonNode.Parse(text) as JsonObject; } catch { return null; }
        }
        static List<string>? Strings(JsonNode? node) =>
            node is JsonArray a && a.All(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                ? a.Select(n => n!.GetValue<string>()).ToList() : null;
        var parsed = inputs.Select(input =>
        {
            var order = Parse(input.Order);
            var presets = (Parse(input.Presets)?["state"] as JsonObject)?["presets"] as JsonArray;
            var time = order?["timestamp"] is JsonValue t && t.GetValueKind() == JsonValueKind.Number && t.TryGetValue<double>(out var d) ? d : 0;
            return (input.Path, Order: Strings(order?["value"]), Time: time,
                Presets: presets?.OfType<JsonObject>().ToList() ?? new List<JsonObject>());
        }).ToList();
        var plan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var withOrder = parsed.Where(p => p.Order is not null).ToList();
        if (withOrder.Count == 0) return plan;
        var newest = withOrder.MaxBy(p => p.Time);
        // A time JSON cannot carry back out is a store nobody should copy from.
        if (!double.IsFinite(newest.Time)) return plan;
        foreach (var item in parsed)
        {
            if (string.Equals(item.Path, newest.Path, StringComparison.OrdinalIgnoreCase)) continue;
            if (item.Order is not null && item.Time >= newest.Time) continue;
            var wanted = TranslateProjectOrder(newest.Order!, newest.Presets, item.Presets);
            // What only this account lists keeps its place rather than being dropped by a copy.
            var carried = wanted.ToHashSet();
            wanted.AddRange((item.Order ?? new List<string>()).Where(k => !carried.Contains(k)));
            if (item.Order is not null && item.Order.SequenceEqual(wanted) && item.Time == newest.Time) continue;
            JsonNode time = newest.Time == Math.Floor(newest.Time) && Math.Abs(newest.Time) < 9e15
                ? JsonValue.Create((long)newest.Time) : JsonValue.Create(newest.Time);
            var value = new JsonObject
            {
                ["tabId"] = "graft", ["timestamp"] = time,
                ["value"] = new JsonArray(wanted.Select(k => (JsonNode?)k).ToArray()),
            };
            plan[item.Path] = Graft.Canonical(value);
        }
        return plan;
    }

    /// Every profile reachable from this one through a mirror pair.
    private static List<string> LinkedProfiles(string profile, List<Pair> pairs)
    {
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Fs.Resolve(profile) };
        for (var i = 0; i < pairs.Count; i++)
            foreach (var pair in pairs)
                if (connected.Contains(pair.Borrower) || connected.Contains(pair.Source))
                {
                    connected.Add(pair.Borrower);
                    connected.Add(pair.Source);
                }
        if (!pairs.Any(p => connected.Contains(p.Borrower) && connected.Contains(p.Source))) return new();
        return connected.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every group of profiles sharing a history, once each, after a quit: a quit
    /// says nothing about which group it belonged to. Claude's own profile can be
    /// opened from the Start menu without waiting for the launch lock, and one that
    /// starts while the helper holds its Local Storage cannot open its own, so it
    /// is given only the preferences file, which is replaced whole.
    /// </summary>
    public static void SynchronizeEveryProjectOrder()
    {
        List<Pair> pairs;
        try { pairs = Candidates(); }
        catch (UnreadableStateException) { return; }
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            if (done.Contains(pair.Borrower)) continue;
            done.UnionWith(LinkedProfiles(pair.Borrower, pairs));
            SynchronizeProjectOrder(pair.Borrower, p => Fs.SamePath(p, GraftPaths.DefaultProfile));
        }
    }

    /// <paramref name="spared"/> names the profiles that could open while this
    /// runs, whose Local Storage is left alone.
    public static void SynchronizeProjectOrder(string profileToOpen, Func<string, bool>? spared = null)
    {
        if (_storageBlocked) return;
        spared ??= _ => false;
        List<string> profiles;
        try { profiles = LinkedProfiles(profileToOpen, Candidates()); }
        catch (UnreadableStateException) { return; }
        if (profiles.Count < 2) return;
        bool AnyRunning()
        {
            var running = Graft.RunningClaudes();
            return profiles.Any(p => running.Any(r => Fs.SamePath(r, p)));
        }
        if (AnyRunning())
        {
            Diagnostics.Note("projects.sync", new Dictionary<string, object?> { ["status"] = "waiting" });
            return;
        }
        try
        {
            var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in profiles)
                accounts[item] = Graft.Account(item) ?? throw new Failure("account-not-ready");
            var read = ProjectStorage("projects-read", new JsonArray(profiles.Select(p => (JsonNode?)new JsonObject
            {
                ["path"] = p,
                ["keys"] = new JsonArray(ProjectOrderPrefix + accounts[p], PresetsPrefix + accounts[p]),
            }).ToArray()));
            // What Claude shows comes from the preferences file; the Local Storage
            // entry carries the time. Where they disagree, the preferences win.
            var plan = PlanProjectOrders(profiles.Select(p =>
            {
                var values = read.TryGetValue(p, out var v) ? v : new();
                var account = accounts[p];
                return new ProjectInput(p,
                    EffectiveProjectOrder(values.GetValueOrDefault(ProjectOrderPrefix + account), DesktopProjectOrder(p, account)),
                    values.GetValueOrDefault(PresetsPrefix + account));
            }).ToList());
            if (plan.Count == 0)
            {
                Diagnostics.Note("projects.sync", new Dictionary<string, object?> { ["status"] = "unchanged" });
                return;
            }
            if (AnyRunning()) throw new Failure("profile-running");
            var orders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in profiles.Where(plan.ContainsKey))
                orders[item] = (JsonNode.Parse(plan[item])?["value"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList()
                    ?? throw new Failure("verification-failed");
            // A spared profile already showing the order has nothing it may be given.
            var targets = profiles.Where(p => orders.ContainsKey(p)
                && !(spared(p) && DesktopProjectOrder(p, accounts[p]) is { } shown && shown.SequenceEqual(orders[p]))).ToList();
            if (targets.Count == 0)
            {
                Diagnostics.Note("projects.sync", new Dictionary<string, object?> { ["status"] = "unchanged" });
                return;
            }
            // The preferences file is written before the timed copy, so a pass stopped
            // between them leaves the order new under an old time, which no pass
            // carries anywhere, rather than an old order under a new time.
            var written = ProjectStorage("projects-write", new JsonArray(targets.Select(p =>
            {
                var key = ProjectOrderPrefix + accounts[p];
                return (JsonNode?)new JsonObject
                {
                    ["path"] = p,
                    ["keys"] = new JsonArray(key),
                    ["values"] = spared(p) ? new JsonObject() : new JsonObject { [key] = plan[p] },
                    ["prefs"] = new JsonObject
                    {
                        ["key"] = "code-projects-order." + accounts[p],
                        ["order"] = new JsonArray(orders[p].Select(k => (JsonNode?)k).ToArray()),
                    },
                };
            }).ToArray()));
            foreach (var item in targets)
            {
                var stored = written.TryGetValue(item, out var w) ? w.GetValueOrDefault(ProjectOrderPrefix + accounts[item]) : null;
                if ((!spared(item) && stored != plan[item])
                    || DesktopProjectOrder(item, accounts[item]) is not { } shown || !shown.SequenceEqual(orders[item]))
                    throw new Failure("verification-failed");
            }
            Diagnostics.Note("projects.sync", new Dictionary<string, object?>
                { ["status"] = "synced", ["profiles"] = string.Join(", ", targets.Select(Path.GetFileName)) });
        }
        catch (Exception e)
        {
            Diagnostics.Note("projects.sync-skipped", new Dictionary<string, object?> { ["reason"] = e.Message });
        }
    }

    /// The order Claude reads at startup, from claude_desktop_config.json.
    public static List<string>? DesktopProjectOrder(string profile, string account)
    {
        var file = Path.Combine(profile, "claude_desktop_config.json");
        try
        {
            if (Fs.IsReparsePoint(file)) return null;
            var root = JsonNode.Parse(File.ReadAllBytes(file)) as JsonObject;
            return root?["preferences"]?["epitaxyPrefs"]?["code-projects-order." + account] is JsonArray list
                && list.All(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                ? list.Select(n => n!.GetValue<string>()).ToList() : null;
        }
        catch { return null; }
    }

    /// The stored entry with its list replaced by the one Claude displays.
    public static string? EffectiveProjectOrder(string? stored, List<string>? desktop)
    {
        if (desktop is null) return stored;
        JsonObject value;
        try { value = (stored is null ? null : JsonNode.Parse(stored) as JsonObject) ?? new JsonObject { ["tabId"] = "graft", ["timestamp"] = 0 }; }
        catch { value = new JsonObject { ["tabId"] = "graft", ["timestamp"] = 0 }; }
        value["value"] = new JsonArray(desktop.Select(k => (JsonNode?)k).ToArray());
        return Graft.Canonical(value);
    }

    private static Dictionary<string, Dictionary<string, string>> ProjectStorage(string action, JsonArray profiles) =>
        ProjectStorageOverride is { } over ? over(action, profiles) : SidebarStorage.Projects(action, profiles);
}
