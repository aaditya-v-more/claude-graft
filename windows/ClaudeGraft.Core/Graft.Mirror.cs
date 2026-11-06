using System.Text.Json;

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
        foreach (var key in LoadMirrorState().Pairs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
            if (PairFolders(key) is (string one, string other))
                moved += MirrorChatFolders(one, other);
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
            if (hereHasNone != thereHasNone) baseline = new();
        }

        (byte[] data, string digest)? Read(string folder, string name)
        {
            try { var d = File.ReadAllBytes(Path.Combine(folder, name)); return (d, Digest(d)); }
            catch { return null; }
        }
        bool Put(byte[] data, string folder, string name)
        {
            try { AtomicWrite.Bytes(Path.Combine(folder, name), data); return true; }
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
        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            var here = Read(one, name);
            var there = Read(other, name);
            // Listed but unreadable means a sharing violation or an I/O error,
            // never permission to remove the surviving copy on the other side.
            if ((here is null && oneNames.Contains(name)) || (there is null && otherNames.Contains(name))) continue;
            if (Junction.IsLink(Path.Combine(one, name)) || Junction.IsLink(Path.Combine(other, name))) continue;
            var action = MirrorDecision(here?.digest, there?.digest,
                baseline.TryGetValue(name, out var bl) ? bl : null);

            // Both sides moved. What the records say about themselves is all
            // that is left, and a record that still exists beats one that was
            // deleted — a chat put back is a nuisance, a chat taken away is not.
            if (action == MirrorAction.Conflict)
            {
                action = (here, there) switch
                {
                    (null, not null) => MirrorAction.CopyToOne,
                    (not null, null) => MirrorAction.CopyToOther,
                    (not null, not null) => RecordMoment(Path.Combine(one, name)) >= RecordMoment(Path.Combine(other, name))
                        ? MirrorAction.CopyToOther : MirrorAction.CopyToOne,
                    _ => MirrorAction.Nothing,
                };
            }

            switch (action)
            {
                case MirrorAction.Nothing:
                    // Where a name held identically by both sides first earns a
                    // baseline; either side names it.
                    if (here?.digest is string d) baseline[name] = d;
                    break;
                case MirrorAction.CopyToOther:
                case MirrorAction.CopyToOne:
                    var source = action == MirrorAction.CopyToOther ? here : there;
                    var destination = action == MirrorAction.CopyToOther ? other : one;
                    if (source is null || !Put(source.Value.data, destination, name)) break;
                    baseline[name] = source.Value.digest;
                    moved++;
                    break;
                case MirrorAction.RemoveFromOne:
                case MirrorAction.RemoveFromOther:
                    if (!Drop(action == MirrorAction.RemoveFromOne ? one : other, name)) break;
                    baseline.Remove(name);
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
        return moved;
    }
}
