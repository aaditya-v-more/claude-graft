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
    public static int MirrorKnownPairs() =>
        StateLock.Run(() => MirrorKnownPairsUnlocked());

    private static int MirrorKnownPairsUnlocked()
    {
        var moved = 0;
        var keys = LoadMirrorState().Pairs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        // Pairs sharing a folder pass a change along one pair at a time, so a launcher
        // that stopped after one round opened its profile a change behind. A change
        // never needs more rounds than there are pairs, and one more proves it.
        for (var i = 0; i <= keys.Count; i++)
        {
            var round = 0;
            foreach (var key in keys)
                if (PairFolders(key) is (string one, string other))
                    round += MirrorChatFolders(one, other);
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
    public static int MirrorChatFolders(string one, string other) =>
        StateLock.Run(() => MirrorChatFoldersUnlocked(one, other));

    private static int MirrorChatFoldersUnlocked(string one, string other)
    {
        if (Fs.SamePath(one, other)) return 0;
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
        var recordsAsLoaded = LoadRecordBaselines(key);
        var records = new Dictionary<string, RecordBaseline>(recordsAsLoaded);
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
        // A removal that did not happen must not be written down as one: a name left
        // on one side with no baseline is a new chat, copied straight back.
        bool Drop(string folder, string name)
        {
            try { File.Delete(Path.Combine(folder, name)); return true; }
            catch { return false; }
        }

        // Both sides hold this copy now.
        void Settle(string name, (byte[] data, string digest) copy, RecordBaseline? known)
        {
            baseline[name] = copy.digest;
            if (known is not null && known.Digest == copy.digest && known.Fields is not null) { records[name] = known; return; }
            var record = RecordObject(MirrorData(copy.data));
            records[name] = new RecordBaseline(copy.digest, record is null ? null : FieldDigests(record), Number(record, "lastActivityAt"));
        }

        List<string>? running = null;
        // Both sides had a reply land since they last agreed: two Claudes carried the
        // chat on from one point, and no record can point at both transcripts, so it
        // becomes two. Waits while either Claude is open, which would write its own
        // half back over the split. False when the split is not this pass's to make.
        bool Split(string name, JsonObject borrowed, JsonObject source)
        {
            running ??= RunningClaudes();
            var profiles = new[] { ProfileOfChatFolder(one), ProfileOfChatFolder(other) };
            if (running.Any(open => profiles.Any(p => Fs.SamePath(p, open))))
            {
                Diagnostics.Note("mirror.diverged", new Dictionary<string, object?> { ["record"] = name, ["status"] = "waiting" });
                return true;
            }
            var newName = "local_" + Guid.NewGuid().ToString().ToLowerInvariant();
            var kept = (JsonObject)source.DeepClone();
            var carried = (JsonObject)borrowed.DeepClone();
            kept["title"] = SplitTitle(source["title"], ClaudeName(profiles[1]));
            carried["title"] = SplitTitle(borrowed["title"], ClaudeName(profiles[0]));
            // An automatic title gives way to the next one Claude thinks of, and the mark with it.
            kept["titleSource"] = "user";
            carried["titleSource"] = "user";
            carried["sessionId"] = newName;
            var keptData = EncodeRecord(kept);
            var carriedData = EncodeRecord(carried);
            // The new half goes in first and out again unless it lands on both sides,
            // so a failure leaves the chat as it was for the next pass.
            var file = newName + ".json";
            var placed = new[] { one, other }.Where(f => Put(carriedData, f, file)).ToList();
            if (placed.Count != 2)
            {
                foreach (var f in placed) Drop(f, file);
                return true;
            }
            moved += 2;
            Settle(file, (carriedData, Digest(MirrorData(carriedData))), null);
            var landed = true;
            foreach (var f in new[] { one, other })
                if (Put(keptData, f, name)) moved++; else landed = false;
            if (landed) Settle(name, (keptData, Digest(MirrorData(keptData))), null);
            Diagnostics.Note("mirror.diverged", new Dictionary<string, object?> { ["record"] = name, ["split"] = file });
            return true;
        }

        // Both sides moved since they last agreed: keep what each changed.
        void Merge(string name, (byte[] data, string digest) here, (byte[] data, string digest) there, RecordBaseline? known)
        {
            var oneRecord = RecordObject(MirrorData(here.data));
            var otherRecord = RecordObject(MirrorData(there.data));
            // Activity gone back past the baseline was written by a Claude holding an
            // older copy, so every field it differs in may be old memory: settle whole.
            bool WentBack(JsonObject? r) => known?.Activity is double then && Number(r, "lastActivityAt") is double now && now < then;
            bool Progressed(JsonObject r) =>
                known?.Activity is double then && Number(r, "lastActivityAt") is double now && now > then
                && r.TryGetPropertyValue("lastAssistantUuid", out var reply) && reply is not null
                && FieldDigest(reply) != (known.Fields is { } f && f.TryGetValue("lastAssistantUuid", out var was) ? was : null);
            if (IsRecordName(name) && oneRecord is not null && otherRecord is not null
                && Progressed(oneRecord) && Progressed(otherRecord)
                && FieldDigest(oneRecord["lastAssistantUuid"]) != FieldDigest(otherRecord["lastAssistantUuid"])
                && Split(name, oneRecord, otherRecord))
                return;

            (byte[] data, string digest) settled;
            if (oneRecord is not null && otherRecord is not null && known?.Fields is { } fields
                && !WentBack(oneRecord) && !WentBack(otherRecord))
            {
                var prefer = RecordMoment(Path.Combine(one, name)) >= RecordMoment(Path.Combine(other, name)) ? MirrorSide.One : MirrorSide.Other;
                var result = MergeRecordFields(oneRecord, otherRecord, fields, prefer);
                if (result.Contested.Count > 0)
                    Diagnostics.Note("mirror.contested", new Dictionary<string, object?> { ["record"] = name, ["fields"] = result.Contested });
                if (SameRecord(result.Record, oneRecord)) settled = here;
                else if (SameRecord(result.Record, otherRecord)) settled = there;
                else { var data = EncodeRecord(result.Record); settled = (data, Digest(MirrorData(data))); }
            }
            // A record that reads beats one that does not: Claude skips a torn file,
            // so carrying it over a whole one takes the chat out of both sidebars.
            else if (IsRecordName(name) && (oneRecord is null) != (otherRecord is null))
                settled = oneRecord is not null ? here : there;
            else
                settled = RecordMoment(Path.Combine(one, name)) >= RecordMoment(Path.Combine(other, name)) ? here : there;

            var allLanded = true;
            foreach (var (folder, current) in new[] { (one, here), (other, there) })
                if (current.digest != settled.digest)
                {
                    if (Put(settled.data, folder, name)) moved++; else allLanded = false;
                }
            // A side that refused the write still holds what it held; the next pass
            // reads both against the same baseline again.
            if (allLanded) Settle(name, settled, null);
        }

        var listedHere = new HashSet<string>(oneNames.Where(IsMirrored));
        var listedThere = new HashSet<string>(otherNames.Where(IsMirrored));
        var names = new HashSet<string>(listedHere);
        names.UnionWith(listedThere);
        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            var here = Read(one, name);
            var there = Read(other, name);
            // Listed but not readable — Claude holding it open, most often — is
            // not absent, and reading it as absent deletes the other side's copy.
            if ((here is null && listedHere.Contains(name)) || (there is null && listedThere.Contains(name)))
            {
                Diagnostics.Note("mirror.record-unread", new Dictionary<string, object?> { ["name"] = name });
                continue;
            }
            var known = records.TryGetValue(name, out var r) && baseline.TryGetValue(name, out var bd) && r.Digest == bd ? r : null;
            if (known is null) records.Remove(name);

            var action = MirrorDecision(here?.digest, there?.digest,
                baseline.TryGetValue(name, out var bl) ? bl : null);

            // Both sides moved. A record that still exists beats one that was
            // deleted, and two that both exist are merged field by field.
            if (action == MirrorAction.Conflict)
            {
                if (here is not null && there is not null) { Merge(name, here.Value, there.Value, known); continue; }
                action = here is null && there is not null ? MirrorAction.CopyToOne
                    : here is not null ? MirrorAction.CopyToOther : MirrorAction.Nothing;
            }

            switch (action)
            {
                case MirrorAction.Nothing:
                    if (here is { } same) Settle(name, same, known);
                    foreach (var (folder, record) in new[] { (one, here), (other, there) })
                    {
                        if (record is not { } held) continue;
                        var localized = LocalScratchOrigin(held.data, folder);
                        if (!localized.AsSpan().SequenceEqual(held.data)) Put(localized, folder, name);
                    }
                    break;
                case MirrorAction.CopyToOther:
                case MirrorAction.CopyToOne:
                    var toOther = action == MirrorAction.CopyToOther;
                    if ((toOther ? here : there) is not { } source) break;
                    var current = toOther ? there : here;
                    // A record nobody can read is not a change to carry; the readable
                    // copy goes back the other way instead.
                    if (IsRecordName(name) && current is { } readable
                        && RecordObject(source.data) is null && RecordObject(readable.data) is not null)
                    {
                        if (Put(readable.data, toOther ? one : other, name)) { Settle(name, readable, null); moved++; }
                        break;
                    }
                    if (!Put(source.data, toOther ? other : one, name)) break;
                    Settle(name, source, null);
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
        records = records.Where(kv => names.Contains(kv.Key) && baseline.TryGetValue(kv.Key, out var d) && d == kv.Value.Digest)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        SaveMirrorState(state);
        if (!SameBaselines(records, recordsAsLoaded)) SaveRecordBaselines(records, key);
        return moved;
    }
}
