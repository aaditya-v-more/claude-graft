using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>What one pass makes of one name held by two mirrored folders.</summary>
public enum MirrorAction
{
    Nothing,
    /// One side holds the newer copy; the other gets it.
    CopyToOther,
    CopyToOne,
    /// It was there last time and is gone from the other side now — a deletion
    /// to carry across rather than a copy to undo.
    RemoveFromOne,
    RemoveFromOther,
    /// Both sides moved since the last pass. Nothing here can say which is
    /// wanted, so the caller settles it on what the records say about themselves.
    Conflict,
}

public static partial class Graft
{
    /// <summary>
    /// Which way one record moves.
    ///
    /// The baseline is what makes a missing file mean something. A name on one
    /// side and not the other is either a record just written or one just
    /// deleted, and the two are the same on disk; the difference is only whether
    /// the last pass saw it on both. Getting this backwards deletes a new chat or
    /// resurrects a deleted one, so it is a function with no filesystem in it and
    /// every branch is driven by a test.
    /// </summary>
    public static MirrorAction MirrorDecision(string? one, string? other, string? baseline)
    {
        if (one == other) return MirrorAction.Nothing;
        if (one is not null && other is null)
            return baseline is null ? MirrorAction.CopyToOther
                 : baseline == one ? MirrorAction.RemoveFromOne : MirrorAction.Conflict;
        if (other is not null && one is null)
            return baseline is null ? MirrorAction.CopyToOne
                 : baseline == other ? MirrorAction.RemoveFromOther : MirrorAction.Conflict;
        if (one == baseline) return MirrorAction.CopyToOne;
        if (other == baseline) return MirrorAction.CopyToOther;
        return MirrorAction.Conflict;
    }

    /// The names a mirror pass is responsible for — records and the markers that
    /// say which have been deleted. Everything else in an organization folder
    /// belongs to the profile that wrote it and is left where it is.
    public static bool IsMirrored(string name) =>
        (name.StartsWith("local_") && name.EndsWith(".json")) || name.StartsWith("deleted_");

    /// When a record says it last moved, for settling a name both sides have
    /// rewritten since the last pass. The file's own timestamp is the fallback,
    /// since a marker has no inside to read.
    private static double RecordMoment(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("lastActivityAt", out var v)
                && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
        }
        catch { }
        return new DateTimeOffset(Fs.Modified(path), TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    /// Bring every pair this app has ever mirrored back into line. A shortcut
    /// being opened squares up its own pair; the source is the case that misses,
    /// so a launcher that knows only its own profile still puts everything in
    /// step by reading the pairs out of the state file.
    public static int MirrorKnownPairs()
    {
        using var stateLock = StateLock.Acquire();
        var moved = 0;
        var keys = LoadMirrorState().Pairs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        // A late borrower's edit reaches earlier borrowers on the next round.
        // One extra round proves every connected pair has stopped moving.
        for (var pass = 0; pass <= keys.Count; pass++)
        {
            var round = 0;
            foreach (var key in keys)
                if (PairFolders(key) is (string one, string other)) round += MirrorChatFolders(one, other);
            moved += round;
            if (round == 0) break;
        }
        return moved;
    }

    /// <summary>
    /// Bring two organization folders into line, and remember what they agreed
    /// on so the next pass can read an absence.
    ///
    /// A folder that could not be read is not one with nothing in it — the same
    /// rule the session sweep learned — and here the cost of getting it wrong is
    /// worse than a duplicate: every record on the other side would look deleted.
    /// So a walk that fails on either side does nothing at all rather than half a
    /// sync.
    /// </summary>
    public static int MirrorChatFolders(string one, string other)
    {
        using var stateLock = StateLock.Acquire();
        if (Fs.SamePath(Fs.Resolve(one), Fs.Resolve(other))) return 0;
        foreach (var folder in new[] { one, other })
            if (!Fs.Exists(folder))
            {
                // Only the folder itself, never the path to it: a pass whose whole
                // job is keeping two sidebars in step must not build a deleted
                // profile back up around a folder it wanted to write into.
                if (!Fs.IsDirectory(Path.GetDirectoryName(folder)!)) return 0;
                try { Directory.CreateDirectory(folder); } catch { return 0; }
            }

        List<string> oneNames, otherNames;
        try
        {
            oneNames = Directory.EnumerateFileSystemEntries(one).Select(Path.GetFileName).ToList()!;
            otherNames = Directory.EnumerateFileSystemEntries(other).Select(Path.GetFileName).ToList()!;
        }
        catch
        {
            Diagnostics.Note("mirror.unread", new Dictionary<string, object?> { ["one"] = one, ["other"] = other });
            return 0;
        }

        var state = LoadMirrorState();
        var key = PairKey(one, other);
        var baseline = state.Pairs.TryGetValue(key, out var b) ? new Dictionary<string, string>(b) : new();
        var records = LoadRecordBaselines(key);
        var moved = 0;

        // A side holding none of the names the baseline describes, while the
        // other still holds them, has not had its sidebar cleared one chat at a
        // time. It has been stashed, replaced, or emptied wholesale, and carrying
        // that across as deletions takes the other profile's history with it.
        // Forgetting the baseline instead makes the survivors look new, so the
        // emptied side is filled again rather than the full one emptied.
        if (baseline.Count > 0)
        {
            var described = new HashSet<string>(baseline.Keys);
            var hereHasNone = !oneNames.Where(IsMirrored).Any(described.Contains);
            var thereHasNone = !otherNames.Where(IsMirrored).Any(described.Contains);
            if (hereHasNone != thereHasNone) { baseline = new(); records = new(); }
        }

        (byte[] data, string digest)? Read(string folder, string name)
        {
            try { var d = File.ReadAllBytes(Path.Combine(folder, name)); return (d, Digest(MirrorData(d))); }
            catch { return null; }
        }
        bool Put(byte[] data, string folder, string name)
        {
            try { AtomicWrite.Bytes(Path.Combine(folder, name), LocalScratchOrigin(data, folder)); return true; }
            catch { return false; }
        }
        // A removal that did not happen must not be written down as one: the
        // baseline is dropped whether or not the file went, so a removal the
        // filesystem refused would leave a name on one side, absent on the other,
        // with no baseline — a new chat, copied straight back to where it was
        // deleted from.
        bool Drop(string folder, string name)
        {
            try { File.Delete(Path.Combine(folder, name)); return true; }
            catch { return false; }
        }

        var names = new HashSet<string>(oneNames.Where(IsMirrored));
        names.UnionWith(otherNames.Where(IsMirrored));
        void Settle(string name, (byte[] data, string digest) copy, RecordBaseline? known = null)
        {
            baseline[name] = copy.digest;
            if (known?.Digest == copy.digest && known.Fields is not null) records[name] = known;
            else
            {
                var record = RecordObject(MirrorData(copy.data));
                records[name] = new RecordBaseline { Digest = copy.digest,
                    Fields = record is null ? null : FieldDigests(record), Activity = RecordNumber(record, "lastActivityAt") };
            }
        }

        IReadOnlyList<(int pid, string command)>? running = null;
        bool Split(string name, JsonObject borrowed, JsonObject source)
        {
            running ??= ClaudeProcesses.Enumerate();
            var profiles = new[] { ProfileOfChatFolder(one), ProfileOfChatFolder(other) };
            if (profiles.Any(p => ClaudeProcesses.IsRunning(p, running)))
            {
                Diagnostics.Note("mirror.diverged", new Dictionary<string, object?> { ["record"] = name, ["status"] = "waiting" });
                return true;
            }
            var newId = "local_" + Guid.NewGuid().ToString("D");
            var file = newId + ".json";
            var kept = (JsonObject)source.DeepClone();
            var carried = (JsonObject)borrowed.DeepClone();
            kept["title"] = SplitTitle(source, ClaudeName(profiles[1]));
            carried["title"] = SplitTitle(borrowed, ClaudeName(profiles[0]));
            kept["titleSource"] = "user";
            carried["titleSource"] = "user";
            carried["sessionId"] = newId;
            var keptData = RecordBytes(kept);
            var carriedData = RecordBytes(carried);
            var placed = new[] { one, other }.Where(folder => Put(carriedData, folder, file)).ToList();
            if (placed.Count != 2)
            {
                foreach (var folder in placed) Drop(folder, file);
                return true;
            }
            // The new record belongs in this pass's saved baseline too, so an
            // immediate deletion is not mistaken for a new chat next time.
            names.Add(file);
            moved += 2;
            Settle(file, (carriedData, Digest(MirrorData(carriedData))));
            var landed = true;
            foreach (var folder in new[] { one, other })
                if (Put(keptData, folder, name)) moved++; else landed = false;
            if (landed) Settle(name, (keptData, Digest(MirrorData(keptData))));
            Diagnostics.Note("mirror.diverged", new Dictionary<string, object?> { ["record"] = name, ["split"] = file });
            return true;
        }

        void Merge(string name, (byte[] data, string digest) here, (byte[] data, string digest) there, RecordBaseline? known)
        {
            var oneRecord = RecordObject(MirrorData(here.data));
            var otherRecord = RecordObject(MirrorData(there.data));
            bool WentBack(JsonObject record) => known?.Activity is double then
                && RecordNumber(record, "lastActivityAt") is double now && now < then;
            bool Progressed(JsonObject record) => known?.Activity is double then
                && RecordNumber(record, "lastActivityAt") is double now && now > then
                && record.ContainsKey("lastAssistantUuid")
                && FieldHash(record, "lastAssistantUuid") != known.Fields?.GetValueOrDefault("lastAssistantUuid");
            var isRecord = name.StartsWith("local_", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);
            if (isRecord && oneRecord is not null && otherRecord is not null && Progressed(oneRecord) && Progressed(otherRecord)
                && FieldHash(oneRecord, "lastAssistantUuid") != FieldHash(otherRecord, "lastAssistantUuid")
                && Split(name, oneRecord, otherRecord)) return;

            var preferOne = RecordMoment(Path.Combine(one, name)) >= RecordMoment(Path.Combine(other, name));
            (byte[] data, string digest) settled;
            if (oneRecord is not null && otherRecord is not null && known?.Fields is { } fields
                && !WentBack(oneRecord) && !WentBack(otherRecord))
            {
                var merged = MergeRecordFields(oneRecord, otherRecord, fields, preferOne);
                if (RecordBytes(merged).AsSpan().SequenceEqual(RecordBytes(oneRecord))) settled = here;
                else if (RecordBytes(merged).AsSpan().SequenceEqual(RecordBytes(otherRecord))) settled = there;
                else { var data = RecordBytes(merged); settled = (data, Digest(MirrorData(data))); }
            }
            else if (isRecord && (oneRecord is null) != (otherRecord is null)) settled = oneRecord is not null ? here : there;
            else settled = preferOne ? here : there;

            var landed = true;
            foreach (var (folder, current) in new[] { (one, here), (other, there) })
                if (current.digest != settled.digest)
                { if (Put(settled.data, folder, name)) moved++; else landed = false; }
            if (landed) Settle(name, settled);
        }

        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal).ToList())
        {
            var here = Read(one, name);
            var there = Read(other, name);
            // Listed but unreadable means a sharing violation or an I/O error,
            // never permission to remove the surviving copy on the other side.
            if ((here is null && oneNames.Contains(name)) || (there is null && otherNames.Contains(name))) continue;
            if (Junction.IsLink(Path.Combine(one, name)) || Junction.IsLink(Path.Combine(other, name))) continue;
            var known = records.GetValueOrDefault(name);
            if (known?.Digest != baseline.GetValueOrDefault(name)) { known = null; records.Remove(name); }
            var action = MirrorDecision(here?.digest, there?.digest,
                baseline.TryGetValue(name, out var bl) ? bl : null);

            // Both sides moved. What the records say about themselves is all
            // that is left, and a record that still exists beats one that was
            // deleted — a chat put back is a nuisance, a chat taken away is not.
            if (action == MirrorAction.Conflict)
            {
                if (here is not null && there is not null)
                {
                    Merge(name, here.Value, there.Value, known);
                    continue;
                }
                action = (here, there) switch
                {
                    (null, not null) => MirrorAction.CopyToOne,
                    (not null, null) => MirrorAction.CopyToOther,
                    _ => MirrorAction.Nothing,
                };
            }

            switch (action)
            {
                case MirrorAction.Nothing:
                    // Where a name held identically by both sides first earns a
                    // baseline; either side names it.
                    if (here is not null) Settle(name, here.Value, known);
                    foreach (var (folder, record) in new[] { (one, here), (other, there) })
                        if (record is { } copy && !LocalScratchOrigin(copy.data, folder).AsSpan().SequenceEqual(copy.data))
                            Put(copy.data, folder, name);
                    break;
                case MirrorAction.CopyToOther:
                case MirrorAction.CopyToOne:
                    var source = action == MirrorAction.CopyToOther ? here : there;
                    var destination = action == MirrorAction.CopyToOther ? other : one;
                    var current = action == MirrorAction.CopyToOther ? there : here;
                    if (source is null) break;
                    // A torn JSON file is not an edit to carry over the sole
                    // readable copy, even if only the torn side changed.
                    if (name.StartsWith("local_", StringComparison.Ordinal) && current is not null
                        && RecordObject(source.Value.data) is null && RecordObject(current.Value.data) is not null)
                    {
                        if (Put(current.Value.data, action == MirrorAction.CopyToOther ? one : other, name))
                        { Settle(name, current.Value); moved++; }
                        break;
                    }
                    if (!Put(source.Value.data, destination, name)) break;
                    Settle(name, source.Value);
                    moved++;
                    break;
                case MirrorAction.RemoveFromOne:
                case MirrorAction.RemoveFromOther:
                    if (!Drop(action == MirrorAction.RemoveFromOne ? one : other, name)) break;
                    baseline.Remove(name);
                    records.Remove(name);
                    moved++;
                    break;
                case MirrorAction.Conflict:
                    break;
            }
        }

        // A name neither side holds any more is never visited above, so its
        // entry would sit in the baseline for good — growing the file, and ready
        // to read the name coming back on one side alone as a deletion.
        state.Pairs[key] = baseline.Where(kv => names.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        SaveMirrorState(state);
        SaveRecordBaselines(key, records.Where(kv => names.Contains(kv.Key)
            && baseline.GetValueOrDefault(kv.Key) == kv.Value.Digest).ToDictionary());
        return moved;
    }
}
