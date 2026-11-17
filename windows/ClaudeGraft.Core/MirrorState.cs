using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// <summary>
/// What both sides of a mirrored pair agreed on last time, so a later pass can
/// tell a record somebody edited from one somebody deleted. Without a baseline
/// the two look identical: a name on one side and not the other.
/// </summary>
public sealed class MirrorState
{
    /// Pair of folders, then record name, then the digest both held.
    [JsonPropertyName("pairs")]
    public Dictionary<string, Dictionary<string, string>> Pairs { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static MirrorState Load(string path)
    {
        var state = StateFile.Read<MirrorState>(path, Options);
        if (state.Pairs is null || state.Pairs.Values.Any(v => v is null))
            throw new UnreadableStateException(path, null);
        return state;
    }

    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicWrite.Bytes(path, JsonSerializer.SerializeToUtf8Bytes(this, Options));
            return true;
        }
        catch { return false; }
    }
}

public static partial class Graft
{
    private static string MirrorStateFile => Path.Combine(GraftPaths.OwnData, "mirrored-chats.json");

    public static MirrorState LoadMirrorState() => MirrorState.Load(MirrorStateFile);

    public static void SaveMirrorState(MirrorState state) => SidebarSync.WithLaunchLock(held =>
    {
        // The mirror-state save must happen regardless of the sidebar launch lock
        // — persisting a graft's pairs never depended on it, and skipping it would
        // lose graft state. Only the sidebar-baseline prune needs serialization,
        // so it runs only with the lock held and after a successful save; it reads
        // the live pairs back from disk, so it can never drop a baseline for a
        // pair still recorded there.
        if (!state.Save(MirrorStateFile)) return;
        ForgetRecordBaselines(state.Pairs.Keys);
        if (held) SidebarSync.ForgetPairsLocked();
    });

    /// A byte no path can contain, so the two halves of a key always come back
    /// apart the way they went together.
    public const string PairSeparator = "\0";

    public static string PairKey(string one, string other) =>
        Fs.Resolve(one) + PairSeparator + Fs.Resolve(other);

    /// <summary>
    /// The two halves of a key, in the roles they were written in — the
    /// borrowing folder first, because <see cref="MirrorChatFolders"/> is only
    /// ever called with the profile's own folder ahead of the one it is
    /// borrowing from. That order is the only record of which side is which:
    /// after a merge both folders hold the same bytes, and nothing on disk tells
    /// a lender from a borrower.
    /// </summary>
    public static (string borrower, string source)? PairHalves(string key)
    {
        var halves = key.Split(PairSeparator);
        if (halves.Length != 2 || !halves.All(Path.IsPathRooted)) return null;
        return (halves[0], halves[1]);
    }

    public static (string one, string other)? PairFolders(string key) => PairHalves(key);

    /// Every pair with a half at or inside this folder, whichever half it is.
    /// For the cases where both roles are equally finished: a profile deleted, a
    /// shortcut deleted. Undoing a graft is not one of them.
    public static List<string> MirrorPairsUnder(string folder) =>
        MirrorPairsMatching(folder, borrowingHalfOnly: false);

    /// Every pair this folder borrows through: the half its own Claude reads and
    /// writes, never the half it is lending to somebody else — which is also the
    /// question "has mirroring been set up here before", asked of the state file
    /// rather than the stash, because a profile that had nothing to put away
    /// leaves no stash and would otherwise look new for ever.
    public static List<string> MirrorPairsBorrowedBy(string folder) =>
        MirrorPairsMatching(folder, borrowingHalfOnly: true);

    private static List<string> MirrorPairsMatching(string folder, bool borrowingHalfOnly)
    {
        var path = Fs.Resolve(folder);
        var childPrefix = path + Path.DirectorySeparatorChar;
        return LoadMirrorState().Pairs.Keys.Where(key =>
        {
            if (PairHalves(key) is not (string borrower, string source)) return false;
            var sides = borrowingHalfOnly ? new[] { borrower } : new[] { borrower, source };
            return sides.Any(s => Fs.SamePath(s, path) || s.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase));
        }).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// A stable digest of a record's bytes — FNV-1a, so the launcher and the app
    /// agree across processes on whether a file has moved, which a runtime hash
    /// would not.
    public static string Digest(byte[] data)
    {
        ulong hash = 0xcbf2_9ce4_8422_2325;
        unchecked
        {
            foreach (var b in data)
            {
                hash ^= b;
                hash *= 0x100_0000_01b3;
            }
        }
        return hash.ToString("x");
    }
}
