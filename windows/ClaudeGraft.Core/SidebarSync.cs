using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public static class SidebarSync
{
    internal sealed record Snapshot(string[] Pins, string[] Order, string Sort, double OrderTime = 0,
        string? Scope = null, string? Fingerprint = null)
    {
        public Snapshot Restricted(HashSet<string> ids)
        {
            var pins = Pins.Where(ids.Contains).ToHashSet();
            return this with { Pins = pins.Order(StringComparer.Ordinal).ToArray(),
                Order = Order.Concat(Pins).Where(pins.Contains).Distinct().ToArray() };
        }

        public bool SameChoices(Snapshot other) =>
            Sort == other.Sort && Order.SequenceEqual(other.Order) && Pins.ToHashSet().SetEquals(other.Pins);

        public Snapshot Replacing(HashSet<string> shared, Snapshot next)
        {
            var order = new List<string>();
            using var incoming = next.Order.AsEnumerable().GetEnumerator();
            foreach (var id in Order)
            {
                if (!shared.Contains(id)) order.Add(id);
                else if (incoming.MoveNext()) order.Add(incoming.Current);
            }
            while (incoming.MoveNext()) order.Add(incoming.Current);
            return this with { Pins = Pins.Where(p => !shared.Contains(p)).Concat(next.Pins).Distinct().ToArray(),
                Order = order.Distinct().ToArray(), Sort = next.Sort, OrderTime = Math.Max(OrderTime, next.OrderTime) };
        }
    }

    private sealed record Store(string Profile, string Path, string Scope);
    private sealed record Pair(string Key, Store Left, Store Right, HashSet<string> Ids);
    private sealed record Agreement(int Version, Dictionary<string, Snapshot> Pairs);
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static string AgreementPath => Path.Combine(GraftPaths.OwnData, "sidebar-sync.json");

    internal static Snapshot Merge(Snapshot left, Snapshot right, Snapshot? baseline)
    {
        var pins = left.Pins.Union(right.Pins).Where(id => baseline is null ||
            !baseline.Pins.Contains(id) || (left.Pins.Contains(id) && right.Pins.Contains(id))).ToHashSet();
        Snapshot preferred;
        if (baseline is not null && left.Order.SequenceEqual(baseline.Order)) preferred = right;
        else if (baseline is not null && right.Order.SequenceEqual(baseline.Order)) preferred = left;
        else preferred = left.OrderTime > right.OrderTime ? left : right;
        var order = preferred.Order.Concat(right.Order).Concat(left.Order).Concat(pins.Order(StringComparer.Ordinal))
            .Where(pins.Contains).Distinct().ToArray();
        var sort = baseline is not null && right.Sort == baseline.Sort ? left.Sort : right.Sort;
        return new(pins.Order(StringComparer.Ordinal).ToArray(), order, sort, Math.Max(left.OrderTime, right.OrderTime));
    }

    // The launch lock covers every Graft launcher. A Claude opened separately
    // may still start in between samples, so the browser must own its normal
    // storage locks and the write pass must check the read fingerprints again.
    public static void BeforeOpening(string profile) => BeforeOpening(profile, SidebarBrowser.CompatibleRuntime);

    internal static void BeforeOpening(string profile, Func<string?> findRuntime)
    {
        try
        {
            using var stateLock = StateLock.Acquire();
            var keys = Graft.LoadMirrorState().Pairs.Keys.ToHashSet();
            var pairs = new List<Pair>();
            foreach (var key in keys.Order(StringComparer.Ordinal))
            {
                if (Graft.PairFolders(key) is not (string left, string right)) continue;
                var a = ActiveStore(left);
                var b = ActiveStore(right);
                if (a is null || b is null || Paths.Equals(a.Profile, b.Profile)) continue;
                var ids = SessionIds(left);
                ids.IntersectWith(SessionIds(right));
                if (ids.Count > 0) pairs.Add(new(key, a, b, ids));
            }
            var connected = new HashSet<string>(Paths) { profile };
            int previous;
            do
            {
                previous = connected.Count;
                foreach (var pair in pairs.Where(p => connected.Contains(p.Left.Profile) || connected.Contains(p.Right.Profile)))
                {
                    connected.Add(pair.Left.Profile);
                    connected.Add(pair.Right.Profile);
                }
            } while (connected.Count > previous);
            pairs.RemoveAll(p => !connected.Contains(p.Left.Profile));
            if (pairs.Count == 0) return;
            if (AnyRunning(connected)) { Status(connected, "waiting"); return; }
            var runtime = findRuntime();
            if (runtime is null) { Status(connected, "unsupported-runtime"); return; }
            Synchronize(runtime, pairs, connected.ToArray(), keys);
            Status(connected, "synced");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // An unfamiliar browser database must never strand the user at a
            // closed shortcut. Logs carry the type only, never browser data.
            Status([profile], "retry", e.GetType().Name);
        }
    }

    private static string StatusPath => Path.Combine(GraftPaths.OwnData, "sidebar-sync-status.json");
    private static Dictionary<string, string> ReadStatus() =>
        !Junction.IsLink(StatusPath) && File.Exists(StatusPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(StatusPath)) ?? new() : new();

    private static void Status(IEnumerable<string> profiles, string status, string? reason = null)
    {
        Diagnostics.Note("sidebar.sync", new Dictionary<string, object?> { ["status"] = status, ["reason"] = reason });
        try
        {
            if (Junction.IsLink(GraftPaths.OwnData) || Junction.IsLink(StatusPath)) return;
            var states = ReadStatus();
            foreach (var profile in profiles) states[profile] = status;
            AtomicWrite.Bytes(StatusPath, JsonSerializer.SerializeToUtf8Bytes(states));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public static string? StatusFor(string profile)
    {
        try
        {
            return ReadStatus().GetValueOrDefault(profile) switch
            {
                "synced" => "Pinned chats and Code sort order are synced.",
                "waiting" => "Close the linked Claude windows, then open a shortcut to sync pins and sort order.",
                "unsupported-runtime" => "Pins and sort order stay local on this Claude version.",
                "retry" => "Sidebar sync could not finish. Graft will retry on the next launch.",
                _ => null
            };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static void ForgetPairs(ISet<string> removed)
    {
        try
        {
            if (!File.Exists(AgreementPath) || Junction.IsLink(AgreementPath)) return;
            var state = JsonSerializer.Deserialize<Agreement>(File.ReadAllBytes(AgreementPath), SidebarBrowser.Json);
            if (state is not { Version: 1, Pairs: not null } || !state.Pairs.Keys.Intersect(removed).Any()) return;
            foreach (var key in removed) state.Pairs.Remove(key);
            AtomicWrite.Bytes(AgreementPath, JsonSerializer.SerializeToUtf8Bytes(state, SidebarBrowser.Json));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Diagnostics.Note("sidebar.cleanup", new Dictionary<string, object?> { ["reason"] = e.GetType().Name });
        }
    }

    private static Store? ActiveStore(string storage)
    {
        var org = new DirectoryInfo(storage);
        if (org.Parent?.Parent is not { } code || code.Name != "claude-code-sessions" ||
            code.Parent is not { } profile || !Paths.Equals(profile.Parent?.FullName, GraftPaths.ProfilesRoot)) return null;
        Graft.ValidateProfilePath(profile.FullName, allowDefault: true);
        if (!Fs.SamePath(Fs.Resolve(storage), Path.Combine(Fs.Resolve(profile.FullName), code.Name, org.Parent.Name, org.Name))
            || Junction.IsLink(Path.Combine(profile.FullName, "config.json")) || !org.Exists ||
            Graft.Account(profile.FullName) != org.Parent.Name) return null;
        return new(profile.FullName, storage, org.Parent.Name + "/" + org.Name);
    }

    private static HashSet<string> SessionIds(string folder) =>
        Directory.EnumerateFiles(folder, "local_*.json").Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(id => id.StartsWith("local_", StringComparison.Ordinal) && id.Length > 6 &&
                         id[6..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')).ToHashSet();

    private static bool AnyRunning(IEnumerable<string> profiles)
    {
        var processes = ClaudeProcesses.Enumerate();
        return profiles.Any(profile => ClaudeProcesses.IsRunning(profile, processes));
    }

    private static void Synchronize(string runtime, List<Pair> pairs, string[] connected, HashSet<string> liveKeys)
    {
        if (Junction.IsLink(AgreementPath)) throw new IOException("Linked sidebar agreement.");
        var state = File.Exists(AgreementPath)
            ? JsonSerializer.Deserialize<Agreement>(File.ReadAllBytes(AgreementPath), SidebarBrowser.Json)
            : new Agreement(1, new());
        if (state is not { Version: 1, Pairs: not null }) throw new IOException("Invalid sidebar agreement.");
        var original = SidebarBrowser.ReadOrWrite(runtime, connected);
        pairs.RemoveAll(p => original[p.Left.Profile].Scope != p.Left.Scope || original[p.Right.Profile].Scope != p.Right.Scope);
        if (pairs.Count == 0) throw new IOException("Sidebar account not ready.");
        var shared = new Dictionary<string, HashSet<string>>(Paths);
        foreach (var pair in pairs)
        {
            foreach (var store in new[] { pair.Left, pair.Right })
            {
                if (!shared.TryGetValue(store.Profile, out var ids)) shared[store.Profile] = ids = new();
                ids.UnionWith(pair.Ids);
            }
        }
        var wanted = new Dictionary<string, Snapshot>(original, Paths);
        for (var pass = 0; pass <= pairs.Count; pass++)
        {
            var changed = false;
            foreach (var pair in pairs)
            {
                var left = wanted[pair.Left.Profile].Restricted(pair.Ids);
                var right = wanted[pair.Right.Profile].Restricted(pair.Ids);
                var next = Merge(left, right, state.Pairs.GetValueOrDefault(pair.Key)?.Restricted(pair.Ids));
                changed |= !next.SameChoices(left) || !next.SameChoices(right);
                wanted[pair.Left.Profile] = wanted[pair.Left.Profile].Replacing(pair.Ids, next);
                wanted[pair.Right.Profile] = wanted[pair.Right.Profile].Replacing(pair.Ids, next);
                state.Pairs[pair.Key] = next;
            }
            if (!changed) break;
        }
        foreach (var pair in pairs)
            foreach (var store in new[] { pair.Left, pair.Right })
                foreach (var id in pair.Ids) ReadRecord(Path.Combine(store.Path, id + ".json"));
        if (AnyRunning(connected)) throw new IOException("A linked profile opened.");
        if (shared.Any(entry => !original[entry.Key].SameChoices(wanted[entry.Key])))
        {
            var changes = new Dictionary<string, object>(Paths);
            foreach (var (path, ids) in shared)
            {
                var next = wanted[path].Restricted(ids);
                changes[path] = new { expected = original[path].Fingerprint, shared = ids, next.Pins, next.Order, next.Sort };
            }
            var actual = SidebarBrowser.ReadOrWrite(runtime, shared.Keys.ToArray(), changes);
            foreach (var (path, ids) in shared)
                if (!wanted[path].Restricted(ids).SameChoices(actual[path].Restricted(ids)))
                    throw new IOException("Sidebar write verification failed.");
        }
        if (AnyRunning(connected)) throw new IOException("A linked profile opened.");
        foreach (var pair in pairs)
            foreach (var store in new[] { pair.Left, pair.Right })
            {
                var pinned = wanted[store.Profile].Pins.ToHashSet();
                foreach (var id in pair.Ids)
                {
                    var file = Path.Combine(store.Path, id + ".json");
                    var record = ReadRecord(file);
                    if ((bool?)record["isStarred"] == pinned.Contains(id)) continue;
                    record["isStarred"] = pinned.Contains(id);
                    AtomicWrite.Bytes(file, JsonSerializer.SerializeToUtf8Bytes(record));
                }
            }
        foreach (var key in state.Pairs.Keys.Except(liveKeys).ToArray()) state.Pairs.Remove(key);
        AtomicWrite.Bytes(AgreementPath, JsonSerializer.SerializeToUtf8Bytes(state, SidebarBrowser.Json));
    }

    private static JsonObject ReadRecord(string file)
    {
        if (Junction.IsLink(file) || JsonNode.Parse(File.ReadAllBytes(file)) is not JsonObject record)
            throw new IOException("Unreadable sidebar record.");
        return record;
    }
}
