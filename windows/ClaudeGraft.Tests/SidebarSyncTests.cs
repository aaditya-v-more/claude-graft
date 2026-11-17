using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// The pin/order merge, which is pure and needs no Chromium. The C# echo of the
/// Swift suite's "Pinned chats and sidebar order" merge checks.
/// </summary>
public sealed class SidebarMergeTests
{
    private static SidebarSnapshot Snap(string[] pins, string[] order, string sort = "recency", double time = 0) =>
        new() { Pins = pins, Order = order, Sort = sort, OrderTime = time };

    [Fact(DisplayName = "the merge carries pins, unpins, reorders and sort choices with a baseline")]
    public void MergeSemantics()
    {
        var old = Snap(new[] { "local_a", "local_b" }, new[] { "local_a", "local_b" });
        var unpinned = Snap(new[] { "local_b" }, new[] { "local_b" });
        var reordered = Snap(new[] { "local_a", "local_b" }, new[] { "local_b", "local_a" }, "alpha", 20);
        var none = Snap(Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal(new[] { "local_a", "local_b" }, SidebarSnapshot.Merge(unpinned, old, null).Pins);
        Assert.Equal(new[] { "local_b" }, SidebarSnapshot.Merge(unpinned, old, old).Pins);
        Assert.Equal(new[] { "local_b" }, SidebarSnapshot.Merge(old, unpinned, old).Pins);
        Assert.Empty(SidebarSnapshot.Merge(none, old, old).Pins);
        Assert.Equal(new[] { "local_b", "local_a" }, SidebarSnapshot.Merge(old, reordered, old).Order);
        Assert.Equal(new[] { "local_b", "local_a" }, SidebarSnapshot.Merge(reordered, old, old).Order);
        Assert.Equal("alpha", SidebarSnapshot.Merge(reordered, old, old).Sort);

        var other = Snap(new[] { "local_a", "local_c" }, new[] { "local_c", "local_a" }, "created", 30);
        var both = SidebarSnapshot.Merge(reordered, other, old);
        Assert.Equal(new[] { "local_a", "local_c" }, both.Pins);
        Assert.Equal(new[] { "local_c", "local_a" }, both.Order);
        Assert.Equal("created", both.Sort);
        Assert.True(SidebarSnapshot.Merge(both, both, both).SameChoices(both));

        Assert.Equal(new[] { "local_b" }, old.Restricted(new HashSet<string> { "local_b" }).Order);
        Assert.Equal(new[] { "remote", "local_b", "project", "local_a" },
            SidebarSnapshot.ReplaceShared(new[] { "remote", "local_a", "project", "local_b" },
                new HashSet<string> { "local_a", "local_b" }, new[] { "local_b", "local_a" }));
    }
}

/// <summary>
/// The orchestration around the storage engine, driven through a stub in place of
/// Chromium — the launch guard, scope check, baseline advance, and record flags.
/// The C# echo of the Swift suite's SidebarSync integration checks.
/// </summary>
[Collection("GlobalState")]
public sealed class SidebarSyncOrchestrationTests : IDisposable
{
    private readonly TempDir _t = new();

    public SidebarSyncOrchestrationTests()
    {
        GraftPaths.ProfilesRootOverride = _t.Dir("root");
        Graft.ResetCachesForTests();
        Graft.RunningClaudesOverride = () => new List<string>();
    }

    public void Dispose()
    {
        SidebarSync.StorageOverride = null;
        Graft.RunningClaudesOverride = null;
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    private static SidebarSnapshot With(SidebarSnapshot s, string[] pins, string[] order) =>
        new() { Pins = pins, Order = order, Sort = s.Sort, Scope = s.Scope, OrderTime = s.OrderTime, Fingerprint = s.Fingerprint };

    [Fact(DisplayName = "opening a closed shared profile reconciles both sidebars, and only then")]
    public void SynchronizesClosedProfiles()
    {
        var root = GraftPaths.ProfilesRoot;
        var one = Path.Combine(root, "One");
        var two = Path.Combine(root, "Two");
        var a = Path.Combine(one, "claude-code-sessions", "account-one", "org-one");
        var b = Path.Combine(two, "claude-code-sessions", "account-two", "org-two");
        foreach (var (profile, store, account) in new[] { (one, a, "account-one"), (two, b, "account-two") })
        {
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(profile, "config.json"), $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
            foreach (var id in new[] { "local_a", "local_b" })
                File.WriteAllText(Path.Combine(store, id + ".json"),
                    new JsonObject { ["sessionId"] = id, ["title"] = "Keep this title", ["isArchived"] = false }.ToJsonString());
        }
        var oneKey = Fs.Resolve(one);
        var twoKey = Fs.Resolve(two);
        Graft.SaveMirrorState(new MirrorState { Pairs = { [Graft.PairKey(a, b)] = new() } });

        var none = new SidebarSnapshot { Scope = "account-one/org-one", Fingerprint = "fp-one" };
        var old = new SidebarSnapshot
        {
            Pins = new[] { "local_a", "local_b" }, Order = new[] { "local_a", "local_b" },
            Sort = "recency", Scope = "account-two/org-two", Fingerprint = "fp-two",
        };
        var disk = new Dictionary<string, SidebarSnapshot> { [oneKey] = none, [twoKey] = old };

        int reads = 0, writes = 0;
        var refuse = false;
        var writtenFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SidebarSync.StorageOverride = (profiles, changes, _) =>
        {
            if (changes is not null)
            {
                writes++;
                foreach (var p in profiles) writtenFingerprints[p] = changes[p].Fingerprint;
                if (refuse) throw new SidebarSync.Failure("test-write-failure");
                foreach (var p in profiles) disk[p] = changes[p];
            }
            else reads++;
            return disk;
        };

        // An open linked profile prevents even reading the sidebar database.
        Graft.RunningClaudesOverride = () => new List<string> { two };
        SidebarSync.Synchronize(one);
        Assert.True(reads == 0 && writes == 0);

        // Closed: both sidebars are brought into line.
        Graft.RunningClaudesOverride = () => new List<string>();
        SidebarSync.Synchronize(one);
        Assert.True(writes == 1 && disk[oneKey].SameChoices(old));

        // Each write carried that profile's own read fingerprint, not a blank —
        // a blank "expected" makes the real storage engine reject it as stale.
        Assert.Equal("fp-one", writtenFingerprints[oneKey]);
        Assert.Equal("fp-two", writtenFingerprints[twoKey]);

        // The pin flag lands on the record without disturbing its title or archive.
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine(a, "local_a.json")))!;
        Assert.True(record["isStarred"]!.GetValue<bool>());
        Assert.Equal("Keep this title", record["title"]!.GetValue<string>());
        Assert.False(record["isArchived"]!.GetValue<bool>());

        // An unchanged sidebar does not rewrite either database.
        SidebarSync.Synchronize(two);
        Assert.Equal(1, writes);

        // A failed storage write cannot advance the baseline.
        disk[oneKey] = With(disk[oneKey], Array.Empty<string>(), Array.Empty<string>());
        refuse = true;
        var syncFile = Path.Combine(GraftPaths.OwnData, "sidebar-sync.json");
        var baseline = File.ReadAllBytes(syncFile);
        SidebarSync.Synchronize(two);
        Assert.Equal(baseline, File.ReadAllBytes(syncFile));

        // The next opening retries the unpin, and it reaches the source.
        refuse = false;
        SidebarSync.Synchronize(two);
        Assert.Empty(disk[twoKey].Pins);

        // Switching accounts cannot apply the previous account's pair.
        disk[oneKey] = new SidebarSnapshot { Scope = "different-account/other-org" };
        var before = writes;
        SidebarSync.Synchronize(one);
        Assert.Equal(before, writes);

        // Returning to independent histories forgets the sidebar agreement.
        Graft.SaveMirrorState(new MirrorState());
        var dropped = JsonNode.Parse(File.ReadAllBytes(syncFile))!;
        Assert.Empty(dropped["pairs"]!.AsObject());

        // And independent profiles never open the storage helper again.
        var beforeReads = reads;
        SidebarSync.Synchronize(one);
        Assert.Equal(beforeReads, reads);

        // A present-but-unreadable baseline aborts before any storage access,
        // rather than merging as if fresh and restoring an intentional unpin.
        Graft.SaveMirrorState(new MirrorState { Pairs = { [Graft.PairKey(a, b)] = new() } });
        File.WriteAllText(syncFile, "{ corrupt baseline");
        int r0 = reads, w0 = writes;
        SidebarSync.Synchronize(one);
        Assert.True(reads == r0 && writes == w0);
    }

    [Fact(DisplayName = "a baseline written by an older build (PascalCase fields) is honored, not rejected")]
    public void MigratesOldBaselineCasing()
    {
        var root = GraftPaths.ProfilesRoot;
        var one = Path.Combine(root, "One");
        var two = Path.Combine(root, "Two");
        var a = Path.Combine(one, "claude-code-sessions", "acc-one", "org-one");
        var b = Path.Combine(two, "claude-code-sessions", "acc-two", "org-two");
        foreach (var (profile, store, account) in new[] { (one, a, "acc-one"), (two, b, "acc-two") })
        {
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(profile, "config.json"), $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
            File.WriteAllText(Path.Combine(store, "local_a.json"),
                new JsonObject { ["sessionId"] = "local_a", ["isArchived"] = false }.ToJsonString());
        }
        var oneKey = Fs.Resolve(one);
        var twoKey = Fs.Resolve(two);
        var key = Graft.PairKey(a, b);
        Graft.SaveMirrorState(new MirrorState { Pairs = { [key] = new() } });

        // The baseline as an older build wrote it: PascalCase field names, recording
        // local_a pinned on both sides.
        var baseline = new JsonObject
        {
            ["Version"] = 1,
            ["Pairs"] = new JsonObject
            {
                [key] = new JsonObject
                {
                    ["Pins"] = new JsonArray { "local_a" },
                    ["Order"] = new JsonArray { "local_a" },
                    ["Sort"] = "recency",
                    ["OrderTime"] = 0,
                    ["Scope"] = null,
                    ["Fingerprint"] = "",
                },
            },
        };
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(Path.Combine(GraftPaths.OwnData, "sidebar-sync.json"), baseline.ToJsonString());

        // One side has since unpinned local_a. Honoring the baseline makes this an
        // unpin that propagates; rejecting it would union the pin back.
        var disk = new Dictionary<string, SidebarSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [oneKey] = new() { Scope = "acc-one/org-one", Fingerprint = "f1" },
            [twoKey] = new() { Pins = new[] { "local_a" }, Order = new[] { "local_a" }, Sort = "recency", Scope = "acc-two/org-two", Fingerprint = "f2" },
        };
        SidebarSync.StorageOverride = (profiles, changes, _) =>
        {
            if (changes is not null) foreach (var p in profiles) disk[p] = changes[p];
            return disk;
        };
        SidebarSync.Synchronize(two);
        Assert.Empty(disk[twoKey].Pins);
    }

    [Fact(DisplayName = "a baseline with duplicate case-variant keys is rejected, not silently loaded ambiguously")]
    public void RejectsAmbiguousDuplicateCaseKeys()
    {
        var root = GraftPaths.ProfilesRoot;
        var one = Path.Combine(root, "One");
        var two = Path.Combine(root, "Two");
        var a = Path.Combine(one, "claude-code-sessions", "acc-one", "org-one");
        var b = Path.Combine(two, "claude-code-sessions", "acc-two", "org-two");
        foreach (var (profile, store, account) in new[] { (one, a, "acc-one"), (two, b, "acc-two") })
        {
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(profile, "config.json"), $"{{\"lastKnownAccountUuid\":\"{account}\"}}");
            File.WriteAllText(Path.Combine(store, "local_a.json"),
                new JsonObject { ["sessionId"] = "local_a", ["isArchived"] = false }.ToJsonString());
        }
        var twoKey = Fs.Resolve(two);
        var key = Graft.PairKey(a, b);
        Graft.SaveMirrorState(new MirrorState { Pairs = { [key] = new() } });

        // "Pairs" records local_a pinned; a duplicate "pairs" is empty. A
        // case-insensitive load could validate one and deserialize the other.
        var snapshot = new JsonObject
        {
            ["Pins"] = new JsonArray { "local_a" }, ["Order"] = new JsonArray { "local_a" },
            ["Sort"] = "recency", ["OrderTime"] = 0, ["Scope"] = null, ["Fingerprint"] = "",
        };
        var baseline = new JsonObject
        {
            ["Version"] = 1,
            ["Pairs"] = new JsonObject { [key] = snapshot },
            ["pairs"] = new JsonObject(),
        };
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(Path.Combine(GraftPaths.OwnData, "sidebar-sync.json"), baseline.ToJsonString());

        var disk = new Dictionary<string, SidebarSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [Fs.Resolve(one)] = new() { Scope = "acc-one/org-one", Fingerprint = "f1" },
            [twoKey] = new() { Pins = new[] { "local_a" }, Order = new[] { "local_a" }, Sort = "recency", Scope = "acc-two/org-two", Fingerprint = "f2" },
        };
        var writes = 0;
        SidebarSync.StorageOverride = (profiles, changes, _) =>
        {
            if (changes is not null) { writes++; foreach (var p in profiles) disk[p] = changes[p]; }
            return disk;
        };
        SidebarSync.Synchronize(two);
        // The ambiguous baseline aborts the pass rather than loading as empty and
        // restoring the unpin, so nothing is written and the pin stands.
        Assert.Equal(0, writes);
        Assert.Equal(new[] { "local_a" }, disk[twoKey].Pins);
    }
}
