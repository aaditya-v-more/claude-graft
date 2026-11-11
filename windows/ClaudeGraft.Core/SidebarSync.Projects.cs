using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public static partial class SidebarSync
{
    internal const string ProjectOrderPrefix = "LSS-persisted.code-projects-order.";
    internal const string PresetsPrefix = "persisted.ccd-session-presets.";
    internal sealed record ProjectInput(string Path, string? Order, string? Presets);
    private sealed record ParsedProjectOrder(string Path, string[]? Order, double Time, JsonObject[] Presets);

    internal static string[] TranslateProjectOrder(string[] order, JsonObject[] source, JsonObject[] destination)
    {
        var byIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
        var byFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var destinationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in destination)
        {
            if (Text(preset["id"]) is not string id) continue;
            destinationIds.Add(id);
            var target = preset["target"] as JsonObject;
            byIdentity.TryAdd((Text(preset["name"]) ?? "") + "\0" + Canonical(target), id);
            if (Text(target?["cwd"]) is string folder) byFolder.TryAdd(folder, id);
        }
        var sources = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var preset in source)
            if (Text(preset["id"]) is string id) sources[id] = preset;
        var result = new List<string>();
        foreach (var key in order)
        {
            if (!key.StartsWith("preset::", StringComparison.Ordinal)) { result.Add(key); continue; }
            var id = key[8..];
            if (sources.TryGetValue(id, out var preset))
            {
                var target = preset["target"] as JsonObject;
                var identity = (Text(preset["name"]) ?? "") + "\0" + Canonical(target);
                var matched = byIdentity.GetValueOrDefault(identity)
                    ?? (Text(target?["cwd"]) is string folder ? byFolder.GetValueOrDefault(folder) : null);
                if (matched is not null) result.Add("preset::" + matched);
            }
            else if (destinationIds.Contains(id)) result.Add(key);
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static Dictionary<string, string> PlanProjectOrders(IEnumerable<ProjectInput> inputs)
    {
        var parsed = inputs.Select(input =>
        {
            var stored = Object(input.Order);
            var presets = (Object(input.Presets)?["state"] as JsonObject)?["presets"] as JsonArray;
            return new ParsedProjectOrder(input.Path, Strings(stored?["value"]),
                stored?["timestamp"] is JsonValue time && time.TryGetValue<double>(out var value) ? value : 0,
                presets is not null && presets.All(p => p is JsonObject) ? presets.Cast<JsonObject>().ToArray() : []);
        }).ToArray();
        var newest = parsed.Where(p => p.Order is not null).MaxBy(p => p.Time);
        var plan = new Dictionary<string, string>(Paths);
        if (newest?.Order is null || !double.IsFinite(newest.Time)) return plan;
        foreach (var item in parsed)
        {
            if (Paths.Equals(item.Path, newest.Path) || (item.Order is not null && item.Time >= newest.Time)) continue;
            var translated = TranslateProjectOrder(newest.Order, newest.Presets, item.Presets);
            var wanted = translated.Concat((item.Order ?? []).Except(translated, StringComparer.Ordinal)).ToArray();
            plan[item.Path] = JsonSerializer.Serialize(new { value = wanted, tabId = "graft", timestamp = newest.Time });
        }
        return plan;
    }

    private static string? Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

    private static string[]? Strings(JsonNode? value) =>
        value is JsonArray array && array.All(item => Text(item) is not null) ? array.Select(item => Text(item)!).ToArray() : null;

    private static JsonObject? Object(string? value)
    {
        try { return value is null ? null : JsonNode.Parse(value) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string Canonical(JsonObject? target)
    {
        JsonNode? Sorted(JsonNode? node) => node switch
        {
            JsonObject value => new JsonObject(value.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Sorted(p.Value)))),
            JsonArray value => new JsonArray(value.Select(Sorted).ToArray()),
            _ => node?.DeepClone()
        };
        return target is null ? "" : Sorted(target)!.ToJsonString();
    }

    internal static string? EffectiveProjectOrder(string? stored, string[]? desktop)
    {
        if (desktop is null) return stored;
        var value = Object(stored) ?? new JsonObject { ["tabId"] = "graft", ["timestamp"] = 0 };
        value["value"] = JsonSerializer.SerializeToNode(desktop);
        return value.ToJsonString();
    }

    internal static string[]? DesktopProjectOrder(string profile, string account)
    {
        var file = Path.Combine(profile, "claude_desktop_config.json");
        if (Junction.IsLink(file)) throw new IOException("Linked desktop settings.");
        if (!File.Exists(file)) return null;
        var root = Object(File.ReadAllText(file));
        return Strings(((root?["preferences"] as JsonObject)?["epitaxyPrefs"] as JsonObject)?["code-projects-order." + account]);
    }

    public static void SynchronizeEveryProjectOrder() => SynchronizeEveryProjectOrder(SidebarBrowser.CompatibleRuntime);

    internal static void SynchronizeEveryProjectOrder(Func<string?> findRuntime)
    {
        try
        {
            using var stateLock = StateLock.Acquire();
            var pairs = Candidates(Graft.LoadMirrorState().Pairs.Keys);
            var done = new HashSet<string>(Paths);
            foreach (var pair in pairs)
            {
                if (done.Contains(pair.Left.Profile)) continue;
                done.UnionWith(ConnectedProfiles(pair.Left.Profile, pairs));
                SynchronizeProjectOrder(pair.Left.Profile, findRuntime, p => Fs.SamePath(p, GraftPaths.DefaultProfile));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Diagnostics.Note("projects.sync-skipped", new Dictionary<string, object?> { ["reason"] = e.GetType().Name });
        }
    }

    internal static void SynchronizeProjectOrder(string profile, Func<string?> findRuntime, Func<string, bool>? spare = null)
    {
        try
        {
            using var stateLock = StateLock.Acquire();
            var profiles = ConnectedProfiles(profile, Candidates(Graft.LoadMirrorState().Pairs.Keys)).Order(Paths).ToArray();
            if (profiles.Length < 2) return;
            if (AnyRunning(profiles)) { ProjectStatus("waiting"); return; }
            var runtime = findRuntime();
            if (runtime is null) { ProjectStatus("unsupported-runtime"); return; }
            var accounts = profiles.ToDictionary(p => p, p => Graft.Account(p) ?? throw new IOException("Account not ready."), Paths);
            var read = SidebarBrowser.Projects(runtime, "projects-read", profiles.Select(p =>
                new SidebarBrowser.ProjectRequest(p, [ProjectOrderPrefix + accounts[p], PresetsPrefix + accounts[p]])).ToArray());
            var plan = PlanProjectOrders(profiles.Select(p => new ProjectInput(p,
                EffectiveProjectOrder(read[p].GetValueOrDefault(ProjectOrderPrefix + accounts[p]), DesktopProjectOrder(p, accounts[p])),
                read[p].GetValueOrDefault(PresetsPrefix + accounts[p]))));
            if (plan.Count == 0) { ProjectStatus("unchanged"); return; }
            if (AnyRunning(profiles) || profiles.Any(p => Graft.Account(p) != accounts[p]))
                throw new IOException("A linked profile changed.");
            var targets = new List<SidebarBrowser.ProjectRequest>();
            foreach (var path in profiles.Where(plan.ContainsKey))
            {
                var order = Strings(Object(plan[path])?["value"]) ?? throw new IOException("Invalid project order.");
                var spared = spare?.Invoke(path) ?? false;
                if (spared && DesktopProjectOrder(path, accounts[path]) is { } current && current.SequenceEqual(order)) continue;
                var key = ProjectOrderPrefix + accounts[path];
                targets.Add(new(path, [key], spared ? new() : new() { [key] = plan[path] },
                    new("code-projects-order." + accounts[path], order)));
            }
            if (targets.Count == 0) { ProjectStatus("unchanged"); return; }
            var written = SidebarBrowser.Projects(runtime, "projects-write", targets.ToArray());
            foreach (var target in targets)
            {
                if (target.Values!.Any(pair => written[target.Path].GetValueOrDefault(pair.Key) != pair.Value)
                    || DesktopProjectOrder(target.Path, accounts[target.Path]) is not { } displayed
                    || !displayed.SequenceEqual(target.Prefs!.Order))
                    throw new IOException("Project order write verification failed.");
            }
            ProjectStatus("synced");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Diagnostics.Note("projects.sync-skipped", new Dictionary<string, object?> { ["reason"] = e.GetType().Name });
        }
    }

    private static void ProjectStatus(string status) =>
        Diagnostics.Note("projects.sync", new Dictionary<string, object?> { ["status"] = status });
}
