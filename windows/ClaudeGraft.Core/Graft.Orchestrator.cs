using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

public static partial class Graft
{
    // MARK: - Transcript cache

    /// One transcript's parse, remembered against the file it came from.
    private sealed class CachedTranscript
    {
        [JsonPropertyName("modified")] public double Modified { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("facts")] public SessionFacts? Facts { get; set; }
    }

    private static readonly object TranscriptCacheLock = new();
    private static Dictionary<string, CachedTranscript> _transcriptCache = new();
    private static bool _transcriptCacheLoaded;
    private static bool _transcriptCacheDirty;

    private static string TranscriptCacheFile => Path.Combine(GraftPaths.OwnData, "transcript-cache.json");

    private static void LoadTranscriptCache()
    {
        if (_transcriptCacheLoaded) return;
        _transcriptCacheLoaded = true;
        try
        {
            var data = File.ReadAllBytes(TranscriptCacheFile);
            _transcriptCache = JsonSerializer.Deserialize<Dictionary<string, CachedTranscript>>(data) ?? new();
        }
        catch { }
    }

    /// Kept on disk rather than only in memory, because a launcher is a process
    /// that exits the moment it has handed over — so a memory-only cache would be
    /// cold every time, and cold is two hundred megabytes of transcript between a
    /// double click and a window. Keeps only what this pass looked at, folding in
    /// anything another process learned meanwhile; the worst a lost entry costs
    /// is one transcript parsed twice.
    private static void SaveTranscriptCache(ISet<string> visited)
    {
        lock (TranscriptCacheLock)
        {
            if (!_transcriptCacheDirty) return;
            _transcriptCacheDirty = false;
            _transcriptCache = _transcriptCache.Where(kv => visited.Contains(kv.Key))
                                               .ToDictionary(kv => kv.Key, kv => kv.Value);
            var merged = new Dictionary<string, CachedTranscript>(_transcriptCache);
            try
            {
                var onDisk = JsonSerializer.Deserialize<Dictionary<string, CachedTranscript>>(
                    File.ReadAllBytes(TranscriptCacheFile));
                if (onDisk is not null)
                    foreach (var (path, entry) in onDisk)
                        if (!merged.ContainsKey(path) && visited.Contains(path)) merged[path] = entry;
            }
            catch { }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TranscriptCacheFile)!);
                AtomicWrite.Bytes(TranscriptCacheFile, JsonSerializer.SerializeToUtf8Bytes(merged));
            }
            catch { }
        }
    }

    private static SessionFacts? TranscriptFacts(string path, string cliSessionId)
    {
        double stamp;
        long size;
        try
        {
            var info = new FileInfo(path);
            stamp = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0;
            size = info.Length;
        }
        catch { stamp = 0; size = 0; }

        lock (TranscriptCacheLock)
        {
            LoadTranscriptCache();
            if (_transcriptCache.TryGetValue(path, out var cached) && cached.Modified == stamp && cached.Size == size)
                return cached.Facts;
        }

        var facts = TranscriptParser.SessionFacts(path, cliSessionId);
        lock (TranscriptCacheLock)
        {
            _transcriptCache[path] = new CachedTranscript { Modified = stamp, Size = size, Facts = facts };
            _transcriptCacheDirty = true;
        }
        return facts;
    }

    // MARK: - Record files

    private static byte[]? SessionRecordData(SessionFacts facts) =>
        Encoding.UTF8.GetBytes(SessionRecord.Serialize(SessionRecord.For(facts)));

    /// Where a session's record lives, resolved through any graft link on the
    /// way, because where a link points is where both windows read.
    private static string RecordFile(SessionFacts facts, string profile)
    {
        var orgDir = Fs.Resolve(Path.Combine(profile, "claude-code-sessions", facts.OwnerAccount, facts.OwnerOrganization));
        return Path.Combine(orgDir, $"local_{facts.CliSessionId}.json");
    }

    private static bool RecordOnDiskMatches(SessionFacts facts, string file)
    {
        try
        {
            var onDisk = File.ReadAllBytes(file);
            var authored = SessionRecordData(facts);
            return authored is not null && onDisk.AsSpan().SequenceEqual(authored);
        }
        catch { return false; }
    }

    /// Puts one record where the session's owner will list it. Never over an
    /// existing file — a record already there is somebody's to keep, not this
    /// pass's to replace.
    public static bool WriteSessionRecord(SessionFacts facts, string profile)
    {
        var file = RecordFile(facts, profile);
        if (Fs.Exists(file)) return false;
        var data = SessionRecordData(facts);
        if (data is null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicWrite.Bytes(file, data);
            return true;
        }
        catch { return false; }
    }

    /// Brings a record this app owns up to date with its transcript. The caller
    /// has already established the record on disk is the one this app wrote;
    /// nothing else may be overwritten with a parsed version.
    public static bool RewriteSessionRecord(SessionFacts facts, string profile)
    {
        var data = SessionRecordData(facts);
        if (data is null) return false;
        try { AtomicWrite.Bytes(RecordFile(facts, profile), data); return true; }
        catch { return false; }
    }

    private static string SessionRecordStateFile => Path.Combine(GraftPaths.OwnData, "session-records.json");

    /// Clears the process-lifetime caches so a test starts from cold, the way a
    /// fresh launcher would. Not for production use — nothing there wants its
    /// caches thrown away mid-run.
    public static void ResetCachesForTests()
    {
        lock (TranscriptCacheLock)
        {
            _transcriptCache = new();
            _transcriptCacheLoaded = false;
            _transcriptCacheDirty = false;
        }
        lock (RecordCacheLock) _recordCache = new();
    }

    // MARK: - The sweep

    /// Writes a record for every session whose transcript survived without one,
    /// keeps the ones it wrote current while their transcripts move, and hands
    /// back what it filed so the caller can say so.
    ///
    /// <paramref name="isRunning"/> answers whether a Claude signed into a given
    /// profile is running — the one thing the quiet window waits on. It is
    /// injected rather than defaulted so a test can drive it and so the process
    /// detection can be ported and wired in on its own; the app must supply the
    /// real check, since defaulting it to "nothing is running" would file a
    /// record the owner's own window is about to write, giving two.
    public static IReadOnlyList<SessionFacts> FileMissingSessionRecords(
        IEnumerable<string> filingInto,
        Func<string, bool> isRunning,
        DateTime? nowOpt = null,
        TimeSpan? quietWindowOpt = null)
    {
        using var stateLock = StateLock.Acquire();
        var now = nowOpt ?? DateTime.UtcNow;
        var quietWindow = quietWindowOpt ?? TimeSpan.FromSeconds(60);

        var state = SessionRecordState.Load(SessionRecordStateFile);
        var contents = SessionStoreContents();
        var candidates = RecordFilingProfiles(filingInto);
        Diagnostics.Note("sweep.begin", new Dictionary<string, object?>
        {
            ["storesRead"] = contents.Stores.Count,
            ["recordsSeen"] = contents.Records.Count,
            ["deletionMarkers"] = contents.Deleted.Count,
            ["remembered"] = state.Records.Count,
            ["vanishedLastPass"] = state.Vanished.Count,
        });

        var recorded = new HashSet<string>(contents.Records.Keys);
        var withdrawn = new HashSet<string>(state.Withdrawn);
        var vanished = new HashSet<string>(state.Vanished);
        var missing = new HashSet<string>();
        var outOfSight = new HashSet<string>();
        var stashedStores = new HashSet<string>();
        var unreadStores = new HashSet<string>();
        var forgotten = new HashSet<string>();
        var goneStores = new HashSet<string>();

        var storeAnswers = new Dictionary<string, (bool stashed, bool present)>();
        (bool stashed, bool present) Answers(string store)
        {
            if (storeAnswers.TryGetValue(store, out var known)) return known;
            var answer = (stashed: IsStashedAway(store), present: Fs.Exists(store));
            storeAnswers[store] = answer;
            return answer;
        }

        foreach (var (session, store) in state.Records)
        {
            // An old state file named the record file rather than its directory;
            // those go on sight, since every record still on disk is learned
            // again below. Only a rooted path is a directory to reason about.
            if (!Path.IsPathRooted(store)) continue;
            if (Answers(store).stashed) { outOfSight.Add(session); stashedStores.Add(store); continue; }
            if (!Answers(store).present)
            {
                forgotten.Add(session); goneStores.Add(store);
                Diagnostics.Note("sweep.forget", new Dictionary<string, object?>
                {
                    ["session"] = session, ["store"] = store,
                    ["because"] = "the folder is gone and nothing here stashed it",
                });
                continue;
            }
            if (!contents.Stores.Contains(store)) { outOfSight.Add(session); unreadStores.Add(store); continue; }
            if (recorded.Contains(session)) continue;
            if (vanished.Contains(session))
            {
                withdrawn.Add(session);
                state.Authored.Remove(session);
                Diagnostics.Note("sweep.withdraw", new Dictionary<string, object?>
                {
                    ["session"] = session, ["store"] = store, ["because"] = "missing from a store read twice",
                });
            }
            else missing.Add(session);
        }

        var remembered = state.Records
            .Where(kv => Path.IsPathRooted(kv.Value) && !forgotten.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (session, store) in contents.Records) remembered[session] = store;
        foreach (var session in withdrawn) remembered.Remove(session);
        state.Records = remembered;
        state.Vanished = missing.OrderBy(s => s, StringComparer.Ordinal).ToList();

        var filed = new List<SessionFacts>();
        var visited = new HashSet<string>();
        var runningProfiles = new Dictionary<string, bool>();
        bool Running(string profile)
        {
            if (runningProfiles.TryGetValue(profile, out var known)) return known;
            var answer = isRunning(profile);
            runningProfiles[profile] = answer;
            return answer;
        }

        List<string> projects;
        try { projects = Directory.EnumerateDirectories(GraftPaths.ClaudeProjects).Select(Path.GetFileName).ToList()!; }
        catch
        {
            state.Withdrawn = withdrawn.OrderBy(s => s, StringComparer.Ordinal).ToList();
            state.Save(SessionRecordStateFile);
            Diagnostics.Note("sweep.end", new Dictionary<string, object?> { ["filed"] = 0, ["because"] = "no transcripts to read" });
            return filed;
        }

        var transcripts = projects.Select(project =>
        {
            var dir = Path.Combine(GraftPaths.ClaudeProjects, project);
            var names = SafeEntries(dir).Where(n => n.EndsWith(".jsonl")).ToList();
            return (dir, names);
        }).ToList();

        var named = new HashSet<string>(transcripts.SelectMany(t => t.names).Select(n => n[..^".jsonl".Length]));
        var guessableDeletions = contents.Deletions.Where(kv => !named.Contains(kv.Key)).Select(kv => kv.Value).ToList();

        var owners = new Dictionary<string, string?>();
        string? Owner(SessionFacts facts)
        {
            if (owners.TryGetValue(facts.OwnerAccount, out var known)) return known;
            var answer = OwnerProfile(facts, candidates);
            owners[facts.OwnerAccount] = answer;
            return answer;
        }

        foreach (var (dir, names) in transcripts)
        {
            foreach (var name in names)
            {
                var transcript = Path.Combine(dir, name);
                visited.Add(transcript);
                var lastWrite = Fs.Modified(transcript);

                var facts = TranscriptFacts(transcript, name[..^".jsonl".Length]);
                if (facts is null) continue;
                // An open-and-close with nothing said never had a conversation to lose.
                if (facts.Prompts == 0) continue;

                if (contents.Deleted.Contains(facts.CliSessionId))
                {
                    withdrawn.Add(facts.CliSessionId);
                    state.Authored.Remove(facts.CliSessionId);
                    Diagnostics.Note("sweep.withdraw", new Dictionary<string, object?>
                    {
                        ["session"] = facts.CliSessionId, ["because"] = "a deletion marker names it",
                    });
                }
                if (withdrawn.Contains(facts.CliSessionId)) continue;
                if (missing.Contains(facts.CliSessionId)) continue;
                if (outOfSight.Contains(facts.CliSessionId)) continue;
                if (contents.Superseded.Contains(facts.CliSessionId)) continue;

                var owner = Owner(facts);

                if (recorded.Contains(facts.CliSessionId))
                {
                    if (owner is not null && state.Authored.TryGetValue(facts.CliSessionId, out var authored))
                    {
                        switch (DecideUpdate(authored, facts, RecordOnDiskMatches(authored, RecordFile(facts, owner))))
                        {
                            case SessionUpdate.Leave: break;
                            case SessionUpdate.TakenOver: state.Authored.Remove(facts.CliSessionId); break;
                            case SessionUpdate.Refresh:
                                if (RewriteSessionRecord(facts, owner)) state.Authored[facts.CliSessionId] = facts;
                                break;
                        }
                    }
                    continue;
                }

                if (owner is null) continue;
                if (DecideFiling(facts, recorded, withdrawn, guessableDeletions, lastWrite,
                        owner, Running(owner), now, quietWindow) != SessionFiling.File) continue;

                var destination = Path.GetDirectoryName(RecordFile(facts, owner))!;
                if (!MayFileRecords(destination, contents.Stores))
                {
                    Diagnostics.Note("sweep.refused", new Dictionary<string, object?>
                    {
                        ["session"] = facts.CliSessionId,
                        ["destination"] = destination,
                        ["because"] = Fs.Exists(destination)
                            ? "the folder is there but this pass never read it"
                            : "this app stashed the folder itself",
                    });
                    continue;
                }

                if (WriteSessionRecord(facts, owner))
                {
                    Diagnostics.Note("sweep.file", new Dictionary<string, object?>
                    {
                        ["session"] = facts.CliSessionId,
                        ["owner"] = facts.OwnerAccount,
                        ["into"] = Path.GetFileName(owner),
                        ["title"] = facts.Title,
                        ["cwd"] = facts.Cwd,
                    });
                    filed.Add(facts);
                    state.Records[facts.CliSessionId] = destination;
                    state.Authored[facts.CliSessionId] = facts;
                }
            }
        }

        state.Withdrawn = withdrawn.OrderBy(s => s, StringComparer.Ordinal).ToList();
        // The loop above withdrew every session a marker names, after the record
        // memory was squared up — so without this those keep an entry naming a
        // folder their record has already gone from.
        foreach (var session in withdrawn) state.Records.Remove(session);
        state.Save(SessionRecordStateFile);
        Diagnostics.Note("sweep.end", new Dictionary<string, object?>
        {
            ["filed"] = filed.Count,
            ["withdrawn"] = withdrawn.Count,
            ["missingThisPass"] = missing.Count,
            ["outOfSight"] = outOfSight.Count,
            ["forgotten"] = forgotten.Count,
        });
        SaveTranscriptCache(visited);
        return filed;
    }
}
