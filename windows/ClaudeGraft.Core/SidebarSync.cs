using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// <summary>
/// One profile's pin set, pin order and Code sort choice, restricted to the
/// sessions two profiles share so unrelated pins keep their place. The reconciled
/// half of the Mac build's SidebarSync; the storage read/write is SidebarStorage.
/// </summary>
public sealed class SidebarSnapshot
{
    [JsonPropertyName("pins")] public IReadOnlyList<string> Pins { get; init; } = Array.Empty<string>();
    [JsonPropertyName("order")] public IReadOnlyList<string> Order { get; init; } = Array.Empty<string>();
    [JsonPropertyName("sort")] public string Sort { get; init; } = "recency";
    [JsonPropertyName("orderTime")] public double OrderTime { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = "";

    private static List<string> Deduped(IEnumerable<string> items)
    {
        var seen = new HashSet<string>();
        return items.Where(seen.Add).ToList();
    }

    private static List<string> Sorted(IEnumerable<string> items) =>
        items.OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// Unrelated slots keep their position; only shared sessions are replaced or
    /// removed, in the wanted order.
    public static List<string> ReplaceShared(IReadOnlyList<string> existing, ISet<string> shared, IReadOnlyList<string> wanted)
    {
        var result = new List<string>();
        var next = 0;
        foreach (var item in existing)
        {
            if (!shared.Contains(item)) result.Add(item);
            else if (next < wanted.Count) result.Add(wanted[next++]);
        }
        for (; next < wanted.Count; next++) result.Add(wanted[next]);
        return Deduped(result);
    }

    public SidebarSnapshot Restricted(ISet<string> shared)
    {
        var members = new HashSet<string>(Pins);
        members.IntersectWith(shared);
        return new SidebarSnapshot
        {
            Pins = Sorted(members),
            Order = Deduped(Order.Concat(Pins).Where(members.Contains)),
            Sort = Sort,
            OrderTime = OrderTime,
        };
    }

    public SidebarSnapshot Replacing(ISet<string> shared, SidebarSnapshot other) => new()
    {
        Pins = Sorted(Pins.Where(p => !shared.Contains(p)).Concat(other.Pins).Distinct()),
        Order = ReplaceShared(Order, shared, other.Order),
        Sort = other.Sort,
        OrderTime = Math.Max(OrderTime, other.OrderTime),
        Scope = Scope,
        // Kept, or the write's expected fingerprint would be blank and the storage
        // engine would reject every change as stale.
        Fingerprint = Fingerprint,
    };

    public bool SameChoices(SidebarSnapshot other) =>
        new HashSet<string>(Pins).SetEquals(other.Pins)
        && Order.SequenceEqual(other.Order) && Sort == other.Sort;

    /// A baseline makes unpinning a change rather than an invitation to put the
    /// other side's pin back. Simultaneous reorders prefer the newer ordering; a
    /// tie, and conflicting sort choices, prefer the chat source.
    public static SidebarSnapshot Merge(SidebarSnapshot borrower, SidebarSnapshot source, SidebarSnapshot? baseline)
    {
        var left = new HashSet<string>(borrower.Pins);
        var right = new HashSet<string>(source.Pins);
        var old = new HashSet<string>(baseline?.Pins ?? Array.Empty<string>());

        var pins = new HashSet<string>();
        foreach (var id in left.Union(right).Union(old))
        {
            bool a = left.Contains(id), b = right.Contains(id);
            bool pinned = baseline is null ? a || b : a == b ? a : a == old.Contains(id) ? b : a;
            if (pinned) pins.Add(id);
        }

        SidebarSnapshot preferred;
        if (baseline is not null && borrower.Order.SequenceEqual(baseline.Order)) preferred = source;
        else if (baseline is not null && source.Order.SequenceEqual(baseline.Order)) preferred = borrower;
        else preferred = borrower.OrderTime > source.OrderTime ? borrower : source;

        var order = Deduped(preferred.Order.Concat(source.Order).Concat(borrower.Order)
            .Concat(Sorted(pins)).Where(pins.Contains));

        var sort = baseline is not null && source.Sort == baseline.Sort ? borrower.Sort : source.Sort;

        return new SidebarSnapshot
        {
            Pins = Sorted(pins),
            Order = order,
            Sort = sort,
            OrderTime = Math.Max(borrower.OrderTime, source.OrderTime),
        };
    }
}

/// <summary>
/// Reconciles pinned chats and Code sort order across profiles that share a chat
/// history, before one is opened and only while every profile on that history is
/// closed. Chromium owns the storage locks; the read/write goes through
/// <see cref="SidebarStorage"/>, stubbed via <see cref="StorageOverride"/> in tests.
/// </summary>
public static partial class SidebarSync
{
    public sealed class Failure : Exception
    {
        public Failure(string reason) : base(reason) { }
    }

    private static string Root => GraftPaths.OwnData;
    private static string StateFile => Path.Combine(Root, "sidebar-sync.json");
    private static string StatusFile => Path.Combine(Root, "sidebar-sync-status.json");

    private static readonly object ProcessLock = new();
    private const string LaunchLockName = "ClaudeGraftSidebarLaunch";
    private static bool _storageBlocked;

    /// Test seam: return merged snapshots for a read (changes null), or apply a
    /// write, without a real Chromium.
    public static Func<IReadOnlyList<string>, Dictionary<string, SidebarSnapshot>?,
        Dictionary<string, HashSet<string>>, Dictionary<string, SidebarSnapshot>>? StorageOverride { get; set; }

    private sealed class SyncState
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("pairs")] public Dictionary<string, SidebarSnapshot> Pairs { get; set; } = new();
    }

    private sealed class Pair
    {
        public required string Key { get; init; }
        public required string Borrower { get; init; }
        public required string Source { get; init; }
        public required string BorrowerStore { get; init; }
        public required string SourceStore { get; init; }
        public required HashSet<string> Shared { get; init; }
    }

    /// Serializes launchers as well as the app: otherwise two shortcuts can each
    /// observe closed profiles and open the same databases at once. The callback
    /// is told whether the lock was actually acquired, so a caller that must not
    /// act without it — the launcher — can decline rather than run unserialized.
    private static T RunLocked<T>(Func<bool, T> work)
    {
        lock (ProcessLock)
        {
            using var mutex = new Mutex(false, LaunchLockName);
            var held = false;
            // Blocks rather than timing out, so contention never yields an
            // unserialized run. A dead holder's mutex is released by the OS and
            // surfaces as abandoned, i.e. now ours.
            try { held = mutex.WaitOne(); }
            catch (AbandonedMutexException) { held = true; }
            catch { held = false; }
            _storageBlocked = !held;
            try { return work(held); }
            finally
            {
                _storageBlocked = false;
                if (held) { try { mutex.ReleaseMutex(); } catch { } }
            }
        }
    }

    public static T WithLaunchLock<T>(Func<T> work) => RunLocked(_ => work());
    public static void WithLaunchLock(Action work) => RunLocked(_ => { work(); return 0; });
    public static void WithLaunchLock(Action<bool> work) => RunLocked(held => { work(held); return 0; });

    private static HashSet<string> SessionIds(string directory)
    {
        var profile = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(directory)!)!)!;
        // A linked store belongs to the source, not this profile.
        if (Junction.IsLink(directory)
            || !Junction.ResolvesInside(directory, Path.Combine(profile, "claude-code-sessions")))
            throw new Failure("linked-chat-store");
        return Directory.EnumerateFiles(directory).Select(Path.GetFileName)
            .Where(n => n!.StartsWith("local_") && n.EndsWith(".json"))
            .Select(n => n![..^".json".Length]).ToHashSet()!;
    }

    private static List<Pair> Candidates()
    {
        var pairs = new List<Pair>();
        foreach (var key in Graft.LoadMirrorState().Pairs.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (Graft.PairFolders(key) is not (string one, string other)) continue;

            static string? Profile(string store)
            {
                var code = Path.GetDirectoryName(Path.GetDirectoryName(store)!);
                if (code is null || Path.GetFileName(code) != "claude-code-sessions") return null;
                var profile = Path.GetDirectoryName(code)!;
                if (Graft.Account(profile) is not string account
                    || Path.GetFileName(Path.GetDirectoryName(store)!) != account
                    || !Fs.SamePath(Path.GetDirectoryName(profile)!, GraftPaths.ProfilesRoot))
                    return null;
                return profile;
            }

            if (Profile(one) is not string borrower || Profile(other) is not string source
                || Fs.SamePath(borrower, source)) continue;
            HashSet<string> a, b;
            try { a = SessionIds(one); b = SessionIds(other); }
            catch { continue; }
            a.IntersectWith(b);
            if (a.Count == 0) continue;
            // Resolved, so every profile-keyed map agrees with what Storage returns.
            pairs.Add(new Pair
            {
                Key = key, Borrower = Fs.Resolve(borrower), Source = Fs.Resolve(source),
                BorrowerStore = one, SourceStore = other, Shared = a,
            });
        }
        return pairs;
    }

    public static void Synchronize(string profileToOpen)
    {
        if (_storageBlocked) return;
        List<Pair> pairs;
        try { pairs = Candidates(); }
        catch (UnreadableStateException)
        {
            RecordStatus("mirror-state-unreadable", new List<string>());
            return;
        }

        // Everything reachable through a shared history, so opening one profile
        // squares up the whole connected set at once. Case-insensitive throughout,
        // since Windows paths are and Fs.Resolve keeps the spelling it was given.
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Fs.Resolve(profileToOpen) };
        for (var i = 0; i < pairs.Count; i++)
            foreach (var pair in pairs)
                if (connected.Contains(Fs.Resolve(pair.Borrower)) || connected.Contains(Fs.Resolve(pair.Source)))
                {
                    connected.Add(Fs.Resolve(pair.Borrower));
                    connected.Add(Fs.Resolve(pair.Source));
                }
        pairs = pairs.Where(p => connected.Contains(Fs.Resolve(p.Borrower))
                                 && connected.Contains(Fs.Resolve(p.Source))).ToList();
        if (pairs.Count == 0) return;

        var profiles = connected.OrderBy(p => p, StringComparer.Ordinal).ToList();
        var running = Graft.RunningClaudes();
        if (profiles.Any(p => running.Any(r => Fs.SamePath(r, p))))
        {
            RecordStatus("waiting", profiles);
            return;
        }

        try
        {
            var state = LoadState();
            if (state.Version != 1) throw new Failure("unsupported-baseline");

            var original = new Dictionary<string, SidebarSnapshot>(
                Storage(profiles, null, new()), StringComparer.OrdinalIgnoreCase);

            bool Matches(SidebarSnapshot? snap, string store) =>
                snap?.Scope == Path.GetFileName(Path.GetDirectoryName(store)!) + "/" + Path.GetFileName(store);
            pairs = pairs.Where(p => Matches(Snapshot(original, p.Borrower), p.BorrowerStore)
                                     && Matches(Snapshot(original, p.Source), p.SourceStore)).ToList();
            if (pairs.Count == 0) throw new Failure("account-not-ready");

            var shared = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                Union(shared, pair.Borrower, pair.Shared);
                Union(shared, pair.Source, pair.Shared);
            }

            var wanted = new Dictionary<string, SidebarSnapshot>(original, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i <= pairs.Count; i++)
            {
                var changedThisPass = false;
                foreach (var pair in pairs)
                {
                    if (!wanted.TryGetValue(pair.Borrower, out var one) || !wanted.TryGetValue(pair.Source, out var two))
                        throw new Failure("missing-profile");
                    var a = one.Restricted(pair.Shared);
                    var b = two.Restricted(pair.Shared);
                    state.Pairs.TryGetValue(pair.Key, out var baseline);
                    var merged = SidebarSnapshot.Merge(a, b, baseline?.Restricted(pair.Shared));
                    changedThisPass = changedThisPass || !a.SameChoices(merged) || !b.SameChoices(merged);
                    wanted[pair.Borrower] = one.Replacing(pair.Shared, merged);
                    wanted[pair.Source] = two.Replacing(pair.Shared, merged);
                    state.Pairs[pair.Key] = merged;
                }
                if (!changedThisPass) break;
            }

            var affected = profiles.Where(shared.ContainsKey).ToList();
            foreach (var pair in pairs)
                foreach (var folder in new[] { pair.BorrowerStore, pair.SourceStore })
                    foreach (var id in pair.Shared)
                    {
                        var file = Path.Combine(folder, id + ".json");
                        if (Fs.IsReparsePoint(file) || ReadRecord(file) is null) throw new Failure("unreadable-session");
                    }

            var changed = affected.Any(p => !original[p].SameChoices(wanted[p]));
            if (changed)
            {
                if (Graft.RunningClaudes().Any(r => profiles.Any(p => Fs.SamePath(p, r))))
                    throw new Failure("profile-running");
                var verified = Storage(affected, wanted, shared);
                foreach (var item in affected)
                    if (!verified.TryGetValue(item, out var result)
                        || !result.Restricted(shared[item]).SameChoices(wanted[item].Restricted(shared[item])))
                        throw new Failure("verification-failed");
            }

            foreach (var pair in pairs)
            {
                UpdateRecordFlags(pair.BorrowerStore, pair.Shared, new HashSet<string>(wanted[pair.Borrower].Pins));
                UpdateRecordFlags(pair.SourceStore, pair.Shared, new HashSet<string>(wanted[pair.Source].Pins));
            }

            var liveKeys = Graft.LoadMirrorState().Pairs.Keys.ToHashSet();
            state.Pairs = state.Pairs.Where(kv => liveKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            SaveState(state);
            RecordStatus("synced", affected);
        }
        catch (Exception e)
        {
            Diagnostics.Note("sidebar.sync-skipped", new Dictionary<string, object?> { ["reason"] = e.Message });
            RecordStatus("retry", profiles);
        }
    }

    private static SidebarSnapshot? Snapshot(Dictionary<string, SidebarSnapshot> map, string profile) =>
        map.TryGetValue(profile, out var s) ? s : null;

    private static void Union(Dictionary<string, HashSet<string>> map, string key, IEnumerable<string> values)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>();
        set.UnionWith(values);
    }

    private static void UpdateRecordFlags(string folder, HashSet<string> shared, HashSet<string> pins)
    {
        foreach (var id in shared.OrderBy(x => x, StringComparer.Ordinal))
        {
            var file = Path.Combine(folder, id + ".json");
            if (Fs.IsReparsePoint(file) || ReadRecord(file) is not JsonObject record) throw new Failure("unreadable-session");
            var pinned = pins.Contains(id);
            if (record["isStarred"] is JsonValue v && v.TryGetValue(out bool current) && current == pinned) continue;
            record["isStarred"] = pinned;
            try
            {
                AtomicWrite.Bytes(file, JsonSerializer.SerializeToUtf8Bytes(record));
            }
            catch { throw new Failure("unreadable-session"); }
        }
    }

    private static JsonObject? ReadRecord(string file)
    {
        try { return JsonNode.Parse(File.ReadAllBytes(file)) as JsonObject; }
        catch { return null; }
    }

    private static Dictionary<string, SidebarSnapshot> Storage(IReadOnlyList<string> profiles,
        Dictionary<string, SidebarSnapshot>? changes, Dictionary<string, HashSet<string>> shared)
    {
        if (StorageOverride is not null) return StorageOverride(profiles, changes, shared);
        return SidebarStorage.Run(profiles, changes, shared);
    }

    /// Absent is a fresh start; present-but-unreadable is not — reading it as an
    /// empty baseline would make the merge take the union and restore a chat the
    /// user meant to unpin, so it fails the pass instead. The shape is validated
    /// rather than deserialized with defaults, or a truncated `{}` (which the
    /// class initializers would fill to version 1, no pairs) would read as fresh.
    private static SyncState LoadState()
    {
        if (!Fs.Exists(StateFile)) return new SyncState();
        try
        {
            // Case-insensitive throughout, so a baseline written by an earlier
            // build under PascalCase field names migrates instead of being
            // rejected for good (which would wedge sync in permanent retry).
            static JsonNode? Field(JsonObject obj, string name) =>
                obj.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            // No two keys that differ only in case: the case-insensitive
            // deserialize below would take a later duplicate, which could pass
            // validation on one value and load another (an empty pairs set,
            // silently dropping agreements). Ambiguous is unreadable.
            static bool Unambiguous(JsonObject obj) =>
                obj.Select(kv => kv.Key.ToLowerInvariant()).Distinct().Count() == obj.Count;

            if (JsonNode.Parse(File.ReadAllBytes(StateFile)) is not JsonObject root
                || !Unambiguous(root)
                || Field(root, "version")?.GetValueKind() != JsonValueKind.Number
                || Field(root, "version")!.GetValue<int>() != 1
                || Field(root, "pairs") is not JsonObject pairs
                || !Unambiguous(pairs))
                throw new Failure("unreadable-baseline");
            foreach (var kv in pairs)
                if (kv.Value is not JsonObject snap
                    || !Unambiguous(snap)
                    || Field(snap, "pins")?.GetValueKind() != JsonValueKind.Array
                    || Field(snap, "order")?.GetValueKind() != JsonValueKind.Array
                    || Field(snap, "sort")?.GetValueKind() != JsonValueKind.String)
                    throw new Failure("unreadable-baseline");
            return root.Deserialize<SyncState>(CaseInsensitive) ?? throw new Failure("unreadable-baseline");
        }
        catch (Exception e) when (e is not Failure) { throw new Failure("unreadable-baseline"); }
    }

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    /// Throws on failure, so a pass that could not persist its baseline reports a
    /// retry rather than "synced" over a baseline that never landed.
    private static void SaveState(SyncState state)
    {
        Directory.CreateDirectory(Root);
        AtomicWrite.Bytes(StateFile, JsonSerializer.SerializeToUtf8Bytes(state));
    }

    /// Drop baselines for pairs no longer mirrored, so a re-graft starts fresh
    /// rather than reconciling against a stale agreement. Under the launch lock,
    /// since a concurrent synchronization writes the same file, and only when the
    /// lock was actually held.
    public static void ForgetPairs() => WithLaunchLock(held => { if (held) ForgetPairsLocked(); });

    /// The prune itself, for a caller that already holds the launch lock — so a
    /// preceding mirror-state save and this prune are one critical section. Reads
    /// the live pairs from the mirror state on disk rather than a set captured
    /// before the lock, so it can never drop a baseline for a pair another writer
    /// has (re)recorded there.
    internal static void ForgetPairsLocked()
    {
        HashSet<string> live;
        try { live = Graft.LoadMirrorState().Pairs.Keys.ToHashSet(); }
        catch (UnreadableStateException) { return; }
        SyncState state;
        try { state = LoadState(); }
        catch { return; }   // an unreadable baseline is left for the sync pass to refuse
        if (state.Version != 1) return;
        var remaining = state.Pairs.Where(kv => live.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (remaining.Count == state.Pairs.Count) return;
        state.Pairs = remaining;
        try { SaveState(state); } catch { }
    }

    public static string? Status(string profile)
    {
        try
        {
            var states = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(StatusFile));
            if (states is null || !states.TryGetValue(Fs.Resolve(profile), out var state)) return null;
            return state switch
            {
                "synced" => "Pinned chats and sort order are synced.",
                "waiting" => "Quit the linked Claude apps, then open a shortcut to sync the sidebar.",
                _ => "Sidebar sync could not finish. It will retry when you next open a shortcut.",
            };
        }
        catch { return null; }
    }

    private static void RecordStatus(string status, IReadOnlyList<string> profiles)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var states = Fs.Exists(StatusFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(StatusFile)) ?? new()
                : new Dictionary<string, string>();
            foreach (var p in profiles) states[Fs.Resolve(p)] = status;
            AtomicWrite.Bytes(StatusFile, JsonSerializer.SerializeToUtf8Bytes(states));
        }
        catch { }
        Diagnostics.Note("sidebar.sync", new Dictionary<string, object?>
        {
            ["status"] = status,
            ["profiles"] = profiles.Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))).ToList(),
        });
    }
}
