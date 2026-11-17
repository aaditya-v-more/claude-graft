using System.Text.Json;

namespace ClaudeGraft.Core;

public static partial class Graft
{
    /// <summary>
    /// Chat records for this profile's account that another profile holds and this
    /// one has not got. A store is keyed <c>&lt;account&gt;/&lt;org&gt;</c> and an
    /// instance reads only the account it holds, so switching accounts inside one
    /// Claude leaves the first account's history in that profile — and the shortcut
    /// for the second account opens to an empty sidebar with its chats next door.
    /// </summary>
    public sealed record ChatsElsewhere
    {
        public sealed record Chat(string Title, DateTime LastActive);

        public required string Profile { get; init; }
        /// Spelled the way the profile asking about it spells the account.
        public required string Account { get; init; }
        /// Only ever what this profile is missing: copying takes nothing from the
        /// source, so counting everything there would leave the offer standing for
        /// ever with a number that never matches what arrived.
        public required int Count { get; init; }
        /// This profile has chats of its own too — a merge, not a recovery.
        public required bool Merging { get; init; }
        public required IReadOnlyList<Chat> Chats { get; init; }
    }

    /// Whichever profile holds the most chats this one has not got, or null when
    /// there is nothing to offer.
    public static ChatsElsewhere? FindChatsElsewhere(string profile, IEnumerable<string> others)
    {
        // Unreadable is not signed out. A config caught mid-rename reads as "no
        // account", and every fallback below that point would be an offer to
        // copy somebody else's history into this profile.
        var config = ReadableConfigJson(profile);
        var account = config?["lastKnownAccountUuid"]?.GetValue<string>();
        if (account is null) return null;

        // A grafted profile's own folder has been linked or moved aside, so what
        // it is holding is not its own and what it appears to be missing is
        // already coming in from its source.
        foreach (var store in GraftPaths.ChatStores(profile))
            if (Junction.IsLink(store) || IsStashedAway(store)) return null;

        var here = RecordFiles(profile, account).Select(r => r.key).ToHashSet();

        ChatsElsewhere? best = null;
        foreach (var other in others)
        {
            if (Fs.SamePath(other, profile)) continue;
            var missing = RecordFiles(other, account).Where(r => !here.Contains(r.key)).ToList();
            if (missing.Count == 0 || missing.Count <= (best?.Count ?? 0)) continue;
            best = new ChatsElsewhere
            {
                Profile = other,
                Account = account,
                Count = missing.Count,
                Merging = here.Count > 0,
                Chats = NewestChats(missing.Select(r => r.url).ToList()),
            };
        }
        return best;
    }

    /// Every chat record one profile holds under one account. Keyed by store and
    /// file name, since the two stores hold different conversations.
    private static List<(string key, string url)> RecordFiles(string profile, string account)
    {
        var found = new List<(string key, string url)>();
        foreach (var store in GraftPaths.ChatStoreNames)
        {
            var dir = Path.Combine(profile, store);
            var accountDir = CounterpartDirectory(dir, account);
            if (accountDir is null) continue;
            var under = Path.Combine(dir, accountDir);
            foreach (var org in SafeEntries(under))
            {
                if (org.StartsWith('.') || org.EndsWith(StashSuffix)) continue;
                var orgDir = Path.Combine(under, org);
                // <org>.profile-origin.json is a plain file sitting beside the
                // organisation folders, and reading it as one of them is a
                // mistake this app has made before.
                if (!Fs.IsDirectory(orgDir)) continue;
                foreach (var name in SafeEntries(orgDir))
                    if (name.StartsWith("local_") && name.EndsWith(".json"))
                        found.Add((store + "/" + name, Path.Combine(orgDir, name)));
            }
        }
        return found;
    }

    /// The newest few, newest first. Shortlisted by file timestamp before any is
    /// opened — this runs on a window refresh, so only what will be shown is read
    /// — then ordered by the activity moment recorded inside.
    private static List<ChatsElsewhere.Chat> NewestChats(IReadOnlyList<string> files, int limit = 5)
    {
        return files
            .Select(url => (url, when: Fs.Modified(url)))
            .OrderByDescending(e => e.when)
            .Take(limit * 4)
            .Select(e =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(e.url));
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("title", out var t)
                        || t.ValueKind != JsonValueKind.String
                        || string.IsNullOrEmpty(t.GetString()))
                        return null;
                    // Milliseconds, the way a record stamps everything else. The
                    // file's own timestamp stands in for one written without it.
                    var active = root.TryGetProperty("lastActivityAt", out var a)
                                 && a.ValueKind == JsonValueKind.Number
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)a.GetDouble()).UtcDateTime
                        : e.when;
                    return new ChatsElsewhere.Chat(t.GetString()!, active);
                }
                catch { return null; }
            })
            .Where(c => c is not null)
            .Select(c => c!)
            .OrderByDescending(c => c.LastActive)
            .Take(limit)
            .ToList();
    }

    /// What one attempt to bring chats across did.
    public sealed record Adoption
    {
        /// Chats copied. Deletion markers travel with them but are not counted:
        /// the number has to match what the offer promised.
        public int Copied { get; init; }
        /// Profiles with a Claude open when this was asked. Nothing is copied
        /// while any is up, so a non-empty list means nothing happened.
        public IReadOnlyList<string> Running { get; init; } = Array.Empty<string>();
    }

    /// Replaced in tests: nothing in a temporary directory can be made to run,
    /// and asking for real would answer differently depending on whether somebody
    /// had Claude open while the suite ran.
    public static Func<List<string>>? RunningClaudesOverride { get; set; }

    /// Every profile on the machine with a Claude on it, Claude's own included.
    public static List<string> RunningClaudes()
    {
        if (RunningClaudesOverride is not null) return RunningClaudesOverride();
        var processes = ClaudeProcesses.Enumerate();
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var profile in new[] { GraftPaths.DefaultProfile }.Concat(SessionStoreProfiles()))
            if (seen.Add(Fs.Resolve(profile)) && ClaudeProcesses.IsRunning(profile, processes))
                result.Add(profile);
        return result;
    }

    /// Copy another profile's records for one account into this profile, taking
    /// nothing away.
    ///
    /// Copied, never moved: nothing here removes a record from a profile it does
    /// not own, so a wrong guess costs some JSON, not a history. Additive on this
    /// side too — a name already here is left alone, so a second run copies
    /// nothing and a chat archived or renamed since keeps its shape, which is what
    /// makes it safe to offer to a profile that already has a history of its own.
    public static Adoption AdoptChats(string source, string profile, string account) =>
        StateLock.Run(() => AdoptChatsUnlocked(source, profile, account));

    private static Adoption AdoptChatsUnlocked(string source, string profile, string account)
    {
        if (Fs.SamePath(source, profile)) return new Adoption();

        // Every Claude on the machine, not just the two either side: an instance
        // builds its sidebar at launch and rewrites records as it runs, so a copy
        // landing under a running one is invisible at best and overwritten at
        // worst — and any of them may share these folders through a graft.
        var running = RunningClaudes();
        if (running.Count > 0)
        {
            Diagnostics.Note("adopt.blocked", new Dictionary<string, object?>
            {
                ["into"] = Path.GetFileName(profile),
                ["running"] = running.Select(Path.GetFileName).ToList(),
                ["because"] = "records landing under a running Claude are ones it may write over",
            });
            return new Adoption { Copied = 0, Running = running };
        }

        var copied = 0;
        var markers = 0;

        foreach (var store in GraftPaths.ChatStoreNames)
        {
            var src = Path.Combine(source, store);
            var dst = Path.Combine(profile, store);
            var sourceAccountDir = CounterpartDirectory(src, account);
            if (sourceAccountDir is null) continue;
            var from = Path.Combine(src, sourceAccountDir);
            var into = Path.Combine(dst, CounterpartDirectory(dst, account) ?? account);

            // A folder that resolves outside the profile is one a graft linked or
            // stashed away; filling it would file these records into the store
            // next door. Per store, since a graft can take one and leave the other.
            if (IsStashedAway(into) || !Junction.ResolvesInside(into, profile))
            {
                Diagnostics.Note("adopt.skipped", new Dictionary<string, object?>
                {
                    ["profile"] = profile, ["store"] = store,
                    ["because"] = "this profile's folder for the account is not its own",
                });
                continue;
            }

            foreach (var org in SafeEntries(from))
            {
                if (org.StartsWith('.') || org.EndsWith(StashSuffix)) continue;
                var orgFrom = Path.Combine(from, org);
                if (!Fs.IsDirectory(orgFrom)) continue;
                var orgInto = Path.Combine(into, CounterpartDirectory(into, org) ?? org);
                Directory.CreateDirectory(orgInto);
                foreach (var name in SafeEntries(orgFrom).Where(IsMirrored))
                {
                    var to = Path.Combine(orgInto, name);
                    if (Fs.Exists(to)) continue;
                    try { File.Copy(Path.Combine(orgFrom, name), to); }
                    catch { continue; }
                    if (name.StartsWith("local_")) copied++;
                    else markers++;
                }
            }
        }

        Diagnostics.Note("adopt", new Dictionary<string, object?>
        {
            ["from"] = Path.GetFileName(source), ["into"] = Path.GetFileName(profile),
            ["account"] = account, ["copied"] = copied, ["markers"] = markers,
        });
        return new Adoption { Copied = copied };
    }
}
