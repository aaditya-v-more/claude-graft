using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>Which command line sessions one record speaks for.</summary>
public sealed class RecordSessions
{
    /// The session it holds now.
    public string? CliSessionId { get; init; }
    /// The sessions it grew out of. A conversation carried on past a compaction,
    /// or resumed, gets a new command line session and the record keeps the old
    /// ids here.
    public IReadOnlyList<string> Prior { get; init; } = Array.Empty<string>();
}

/// <summary>What one walk of the chat stores found.</summary>
public sealed class StoreContents
{
    /// The session a record describes, against the organisation directory the
    /// record sits in. The directory rather than the file name, because knowing
    /// where a record was is what makes its absence later mean something.
    public Dictionary<string, string> Records { get; } = new();
    /// The moment of every deletion marker, against the name it carries.
    public Dictionary<string, double> Deletions { get; } = new();
    /// What every deletion marker is named after — a session id, which a record
    /// this app filed is named for, so a delete can be read rather than inferred.
    public HashSet<string> Deleted { get; } = new();
    /// Every organisation directory this walk actually managed to read. Missing
    /// from here means not looked in, which is a different thing from empty.
    public HashSet<string> Stores { get; } = new();
    /// Every session a record says it has carried on from. Its earlier
    /// transcript sits on disk with no record naming it, which is what a session
    /// that closed without a record looks like from here.
    public HashSet<string> Superseded { get; } = new();
}

public static partial class Graft
{
    private static readonly object RecordCacheLock = new();
    private static Dictionary<string, (DateTime stamp, long size, RecordSessions sessions)> _recordCache = new();

    /// Drop what a walk did not find. Claude re-files a session under a new
    /// record name as it goes, so a cache keyed by path grows by one entry every
    /// time it does — and the menu bar app runs for weeks and walks the stores
    /// on every dropdown.
    private static void ForgetRecordsOutsideOf(ISet<string> walked)
    {
        lock (RecordCacheLock)
            _recordCache = _recordCache.Where(kv => walked.Contains(kv.Key))
                                       .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public static RecordSessions SessionsOfRecord(string path)
    {
        DateTime stamp;
        long size;
        try
        {
            var info = new FileInfo(path);
            stamp = info.LastWriteTimeUtc;
            size = info.Length;
        }
        catch { stamp = DateTime.MinValue; size = 0; }

        lock (RecordCacheLock)
            if (_recordCache.TryGetValue(path, out var cached) && cached.stamp == stamp && cached.size == size)
                return cached.sessions;

        var sessions = ReadRecordSessions(path);
        lock (RecordCacheLock) _recordCache[path] = (stamp, size, sessions);
        return sessions;
    }

    private static RecordSessions ReadRecordSessions(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            string? cli = root.TryGetProperty("cliSessionId", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            var prior = new List<string>();
            if (root.TryGetProperty("priorCliSessionIds", out var p) && p.ValueKind == JsonValueKind.Array)
                foreach (var e in p.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) prior.Add(e.GetString()!);
            return new RecordSessions { CliSessionId = cli, Prior = prior };
        }
        catch { return new RecordSessions(); }
    }

    /// Every directory under the profiles root holding a chat store — profiles
    /// this app was never told about included, because the question a sweep asks
    /// is whether any Claude anywhere is listing the session, and a record in a
    /// profile outside every shortcut still means one is. Sorted, so which of
    /// two mirrored copies is written down as the place a record lives does not
    /// flip between passes.
    public static List<string> SessionStoreProfiles()
    {
        List<string> names;
        try { names = Directory.EnumerateDirectories(GraftPaths.ProfilesRoot).Select(Path.GetFileName).ToList()!; }
        catch { return new List<string>(); }
        return names
            .Where(n => !n.StartsWith('.'))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => Path.Combine(GraftPaths.ProfilesRoot, n))
            .Where(p => Fs.IsDirectory(Path.Combine(p, "claude-code-sessions")))
            .ToList();
    }

    /// The folder of every shortcut this app has made, read out of the list the
    /// window keeps — the only thing on the machine that says which profiles are
    /// this app's doing. Read rather than decoded into the app's Shortcut type,
    /// because a launcher needs this too and never sees that type.
    public static List<string> ShortcutProfiles()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(GraftPaths.OwnData, "shortcuts.json")));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<string>();
            var profiles = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String)
                {
                    var folder = f.GetString()!;
                    if (ValidateFolder(folder) is null) profiles.Add(GraftPaths.Profile(folder));
                }
            return profiles;
        }
        catch { return new List<string>(); }
    }

    /// The profiles a sweep may write a record into: whatever the caller named,
    /// Claude's own, and the ones this app made — deduped by where they resolve.
    /// The caller's own list comes first: with one account on two profiles, the
    /// one about to build a sidebar is the one that named itself.
    public static List<string> RecordFilingProfiles(IEnumerable<string> named)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var profile in named.Append(GraftPaths.DefaultProfile).Concat(ShortcutProfiles()))
            if (seen.Add(Fs.Resolve(profile))) result.Add(profile);
        return result;
    }

    /// The profile a session's owner account lives on now. Accounts move between
    /// profiles — a migration, a sign-in — so it is asked rather than assumed.
    public static string? OwnerProfile(SessionFacts facts, IEnumerable<string> profiles)
    {
        var seen = new HashSet<string>();
        foreach (var profile in profiles)
        {
            if (!seen.Add(Fs.Resolve(profile))) continue;
            if (Account(profile) == facts.OwnerAccount) return profile;
        }
        return null;
    }

    /// Everything the chat stores on the machine hold, in one walk. Organisation
    /// directories grafted by hand resolve through to where they really sit, so
    /// a store two profiles share is only read once and a record filed through a
    /// link counts the same as one filed beside it.
    public static StoreContents SessionStoreContents()
    {
        var contents = new StoreContents();
        var walked = new HashSet<string>();

        foreach (var profile in SessionStoreProfiles())
        {
            var store = Path.Combine(profile, "claude-code-sessions");
            foreach (var account in SafeEntries(store).OrderBy(n => n, StringComparer.Ordinal))
            {
                if (account.StartsWith('.') || account.EndsWith(StashSuffix)
                    || GraftPaths.NonAccountStoreItems.Contains(account)) continue;
                var accountDir = Path.Combine(store, account);
                foreach (var org in SafeEntries(accountDir).OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (org.StartsWith('.') || org.EndsWith(StashSuffix)) continue;
                    var orgDir = Path.Combine(accountDir, org);
                    var names = SafeEntries(orgDir);
                    if (!Fs.IsDirectory(orgDir)) continue;
                    var resolved = Fs.Resolve(orgDir);
                    contents.Stores.Add(resolved);
                    foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
                    {
                        var file = Fs.Resolve(Path.Combine(orgDir, name));
                        if (!walked.Add(file)) continue;
                        if (name.StartsWith("local_") && name.EndsWith(".json"))
                        {
                            var sessions = SessionsOfRecord(file);
                            // The first store to hold it keeps it; letting the
                            // last win left the place a session was filed
                            // changing from pass to pass in the one file written
                            // to be read afterwards.
                            if (sessions.CliSessionId is string sid && !contents.Records.ContainsKey(sid))
                                contents.Records[sid] = resolved;
                            foreach (var prior in sessions.Prior) contents.Superseded.Add(prior);
                        }
                        else if (name.StartsWith("deleted_"))
                        {
                            var marker = name["deleted_".Length..];
                            contents.Deleted.Add(marker);
                            try
                            {
                                var text = File.ReadAllText(file).Trim();
                                if (double.TryParse(text, out var when)) contents.Deletions[marker] = when;
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        ForgetRecordsOutsideOf(walked);
        return contents;
    }
}
