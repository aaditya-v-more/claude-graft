using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>
/// The graft itself: what a profile owns, how it is moved aside so a link can
/// stand in its place, and how it is put back. This half of the Mac build's
/// <c>enum Graft</c> — identity and the stash reversibility machinery — carries
/// over to Windows with the symlink swapped for a junction and nothing else of
/// substance changed. The session-record sweep is the other half and is ported
/// separately.
/// </summary>
public static partial class Graft
{
    /// A profile folder name has to be one plain component sitting directly in
    /// the profiles root. Anything else could send a graft, or a delete, at
    /// somebody else's data.
    public static string? ValidateFolder(string folder)
    {
        if (folder is null) return "The profile folder needs a name.";
        var trimmed = folder.Trim();
        if (trimmed.Length == 0) return "The profile folder needs a name.";
        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains(':'))
            return "The profile folder must be a single folder name, not a path.";
        if (trimmed is "." or ".." || trimmed.StartsWith('.'))
            return $"“{trimmed}” is not a usable folder name.";
        if (trimmed.Equals("Claude", StringComparison.OrdinalIgnoreCase))
            return "That is Claude's own profile folder. Pick another name.";
        if (trimmed.Equals("ClaudeGraft", StringComparison.OrdinalIgnoreCase))
            return "That folder belongs to Claude Graft itself. Pick another name.";
        return ValidateWindowsName(folder);
    }

    public static string? ValidateWindowsName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.')
            || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || name.Length > 120)
            return "Use a plain Windows name without trailing dots, spaces or reserved characters.";
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$"))
            return "That name is reserved by Windows.";
        return null;
    }

    public static void ValidateProfilePath(string profile, bool allowDefault = false)
    {
        var full = Path.GetFullPath(profile);
        if (!Path.IsPathFullyQualified(profile)
            || !Fs.SamePath(Path.GetDirectoryName(full) ?? full, GraftPaths.ProfilesRoot)
            || Junction.IsLink(full))
            throw new IOException("The profile must be a real folder directly inside Claude's profile directory.");
        if (allowDefault && Fs.SamePath(full, GraftPaths.DefaultProfile)) return;
        if (ValidateFolder(Path.GetFileName(full)) is string problem) throw new IOException(problem);
    }

    /// Files a second profile can share wholesale. Everything absent from this
    /// list is either credential material, per-organization cache, or a store
    /// two running instances cannot both hold open.
    public static readonly string[] SharedItems =
    {
        "claude_desktop_config.json",
        "Claude Extensions",
        "Claude Extensions Settings",
        "extensions-installations.json",
        "window-state.json",
        "git-worktrees.json",
        "claude-ssh-remote",
        "ssh_configs.json",
    };

    /// Keys copied out of config.json. The rest of that file is this profile's
    /// own credentials, so it is never linked.
    public static readonly string[] AppearanceKeys = { "userThemeMode", "locale" };

    // MARK: - Stash

    /// Suffix for anything a profile owned before it was grafted.
    public const string StashSuffix = ".graft-own";

    /// Hidden sibling, so the app never mistakes a stashed organization folder
    /// for a real one when it scans the store.
    public static string StashPath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        return Path.Combine(dir, "." + name + StashSuffix);
    }

    /// <store>/<account>/<org> is three components, which is how far above an
    /// organization folder this app can have put something away.
    public const int ChatStoreDepth = 3;

    /// Whether this app has stashed a folder, or any folder it sits inside.
    ///
    /// Two shapes, both known since merging was written: a cross-account graft
    /// stashes the organization folder itself, and two profiles on one account
    /// stash the whole store above it, leaving the same folder one level further
    /// down. Everything that asked only about the sibling saw a store put away
    /// whole as a profile that had never held anything — and filed a whole
    /// history into the empty folder standing in its place, unarchived, with the
    /// real one orphaned beside it.
    public static bool IsStashedAway(string folder)
    {
        var here = Path.GetFullPath(folder);
        for (var i = 0; i < ChatStoreDepth; i++)
        {
            if (Fs.Exists(StashPath(here))) return true;
            var parent = Path.GetDirectoryName(here);
            if (parent is null) break;
            here = parent;
        }
        return false;
    }

    /// Move a profile's own file or folder aside rather than destroying it.
    ///
    /// A stash already sitting beside the link does not mean the item is a
    /// redundant copy of the shared one. Claude writes config.json by renaming a
    /// temporary over it, and a rename replaces a link with a regular file, so
    /// the profile quietly goes back to writing its own copy while the stash
    /// still holds the pre-graft state. A chat directory Claude recreates when
    /// it cannot follow the link does the same. Deleting in that case threw away
    /// every chat written since the graft.
    private static void Stash(string path)
    {
        if (!Fs.Exists(path) || Junction.IsLink(path)) return;
        var stashed = StashPath(path);
        if (Fs.Exists(stashed)) Absorb(stashed, path);
        // Anything Absorb could not move is still in there and is still this
        // profile's; leave both alone rather than write over one of them.
        if (Fs.Exists(stashed))
        {
            Diagnostics.Note("stash.blocked", new Dictionary<string, object?>
            {
                ["folder"] = path,
                ["because"] = "a stash is already there and could not be folded back in",
            });
            return;
        }
        Diagnostics.Note("stash", new Dictionary<string, object?>
        {
            ["folder"] = path,
            ["held"] = Fs.Entries(path),
        });
        Move(path, stashed);
    }

    /// Fold a stash back into the copy the profile is using now, so that one
    /// item holds everything it owns. A name that appears in both is the same
    /// chat, or the same file written twice, and the live copy is the version
    /// the profile went on using.
    private static void Absorb(string stashed, string live)
    {
        var bothDirs = Fs.IsDirectory(stashed) && Fs.IsDirectory(live)
                       && !Junction.IsLink(stashed) && !Junction.IsLink(live);
        if (!bothDirs)
        {
            // Two files: the profile overwrote the stashed one itself, so the
            // live copy supersedes it. Anything else is a directory against a
            // file, which is not a pair this can reason about either way round,
            // and is left alone rather than guessed at.
            if (!Fs.IsDirectory(stashed) && !Fs.IsDirectory(live)) TryDelete(stashed);
            return;
        }

        foreach (var name in SafeEntries(stashed))
        {
            var from = Path.Combine(stashed, name);
            var to = Path.Combine(live, name);
            if (Fs.Exists(to)) Absorb(from, to);
            else Move(from, to);
        }
        // An empty shell left behind would read as a stash again on the next
        // launch. Anything still in there failed to move and stays.
        if (SafeEntries(stashed).Count == 0) TryDelete(stashed);
    }

    /// Put back what this profile owns: the state it had before the graft, and
    /// anything it has written since the link stopped being followed. Bailing
    /// out because something already sits at the link is what left stashes
    /// orphaned, and an orphaned stash is what armed the next graft to delete.
    private static void Unstash(string link)
    {
        if (Junction.IsLink(link)) Junction.Remove(link);
        var stashed = StashPath(link);
        if (!Fs.Exists(stashed)) return;
        var held = Fs.Entries(stashed);
        if (Fs.Exists(link))
        {
            Diagnostics.Note("unstash.absorb", new Dictionary<string, object?>
            {
                ["folder"] = link,
                ["returning"] = held,
                ["onto"] = Fs.Entries(link),
            });
            Absorb(stashed, link);
            return;
        }
        Diagnostics.Note("unstash", new Dictionary<string, object?>
        {
            ["folder"] = link,
            ["returning"] = held,
        });
        Move(stashed, link);
    }

    /// Point <paramref name="link"/> at <paramref name="target"/>, preserving
    /// anything already there.
    public static bool Relink(string target, string link)
    {
        if (!Fs.Exists(target)) return false;
        // Linking something to itself would stash the real thing away and leave
        // a link pointing at its own empty name.
        if (Fs.SamePath(target, link)) return false;
        if (Fs.SamePath(Fs.Resolve(target), Fs.Resolve(link))) return Junction.IsLink(link);
        if (File.Exists(target)) return SharedSettings.Share(target, link);
        if (Junction.IsLink(link))
        {
            if (Fs.SamePath(Junction.Target(link) ?? "", target)) return true;
            Junction.Remove(link);
        }
        else if (Fs.Exists(link))
        {
            Stash(link);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            Junction.Create(link, target);
            return true;
        }
        catch { return false; }
    }

    // MARK: - Profile identity

    /// The account this profile is currently signed into.
    public static string? Account(string profile) =>
        ConfigJson(profile)?["lastKnownAccountUuid"]?.GetValue<string>();

    /// config.json as a parsed object, or an empty one where the Mac build would
    /// have returned an empty dictionary.
    public static JsonObject ConfigJson(string profile) =>
        ReadableConfigJson(profile) ?? new JsonObject();

    /// The same, except that a file which is there and will not parse comes back
    /// as null rather than as an empty config. Claude writes this file by
    /// renaming a temporary over it, so a read landing mid-rename sees a
    /// truncated one; a profile that has simply never been signed in has no file
    /// at all. Anything that would write the config back, or decide where a
    /// profile's chats go, has to tell those two apart.
    public static JsonObject? ReadableConfigJson(string profile)
    {
        var path = Path.Combine(profile, "config.json");
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch
        {
            // No file — never signed in, safe to treat as an empty readable
            // config. A file that is there but could not be read is unreadable,
            // which is a different answer.
            return Fs.Exists(path) ? null : new JsonObject();
        }
        try { return JsonNode.Parse(data) as JsonObject; }
        catch { return null; }
    }

    // MARK: - Moving a profile to a new folder

    public enum ProfileMove { Moved, NothingToMove, TargetExists, Failed }

    /// Move a profile's data from one folder to another when its folder is
    /// changed in the window, so the chats and login follow the rename rather
    /// than being abandoned at the old name with an empty folder standing in.
    ///
    /// Three guards hold it in. It refuses when something already sits at the new
    /// name — merging two profiles' data is not what a rename means, and writing
    /// over it is worse — so the caller sends the person back to pick an unused
    /// name. It does nothing when there is no old folder to move, since the graft
    /// that follows will make the new one. And the path-keyed state moves with the
    /// folder: the mirror baselines and the sweep's record of where each session
    /// sat both name the old path, and a mirror whose baseline has gone reads as a
    /// first pass and stashes the live history away — the "moved, not cleared"
    /// mistake, reached here by a rename rather than a sign-in. The caller is
    /// responsible for the other half of the safety: a profile whose Claude is
    /// running must not have its files moved out from under it.
    public static ProfileMove MoveProfileFolder(string oldFolder, string newFolder)
    {
        using var stateLock = StateLock.Acquire();
        if (ValidateFolder(oldFolder) is not null || ValidateFolder(newFolder) is not null) return ProfileMove.Failed;
        var from = Path.GetFullPath(GraftPaths.Profile(oldFolder));
        var to = Path.GetFullPath(GraftPaths.Profile(newFolder));
        if (Junction.IsLink(from) || Junction.IsLink(to) || ClaudeProcesses.IsRunning(from)) return ProfileMove.Failed;
        if (Fs.SamePath(from, to)) return ProfileMove.NothingToMove;
        if (!Directory.Exists(from)) return ProfileMove.NothingToMove;
        if (Fs.Exists(to)) return ProfileMove.TargetExists;

        try
        {
            // Same volume — both are children of the profiles root — so this is a
            // rename, and a rename carries the junctions and stashes inside whole,
            // targets untouched, rather than walking into them.
            Directory.Move(from, to);
        }
        catch
        {
            Diagnostics.Note("profile.move.failed", new Dictionary<string, object?>
            {
                ["from"] = from, ["to"] = to,
            });
            return ProfileMove.Failed;
        }

        RenameProfileInState(from, to);
        Diagnostics.Note("profile.move", new Dictionary<string, object?>
        {
            ["from"] = from, ["to"] = to,
        });
        return ProfileMove.Moved;
    }

    /// Rewrites every path this app has written down that sat at or inside the old
    /// profile folder to sit at the new one instead. String work on the stored
    /// keys, not a re-resolve, so it matches how they were written whether or not
    /// the folders still exist by their old names.
    private static void RenameProfileInState(string oldRoot, string newRoot)
    {
        var mirror = LoadMirrorState();
        var movedPairs = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (key, value) in mirror.Pairs)
        {
            if (PairHalves(key) is not (string borrower, string source))
            {
                movedPairs[key] = value;
                continue;
            }
            var one = RemapPath(borrower, oldRoot, newRoot) ?? borrower;
            var other = RemapPath(source, oldRoot, newRoot) ?? source;
            movedPairs[one + PairSeparator + other] = value;
        }
        mirror.Pairs = movedPairs;
        SaveMirrorState(mirror);

        var records = SessionRecordState.Load(SessionRecordStateFile);
        var moved = false;
        foreach (var session in records.Records.Keys.ToList())
            if (RemapPath(records.Records[session], oldRoot, newRoot) is string moved2)
            {
                records.Records[session] = moved2;
                moved = true;
            }
        if (moved) records.Save(SessionRecordStateFile);
    }

    /// A path rewritten from under one root to the other, or null when it sat
    /// under neither and is left as it was.
    private static string? RemapPath(string path, string oldRoot, string newRoot)
    {
        if (Fs.SamePath(path, oldRoot)) return newRoot;
        var prefix = oldRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? newRoot + Path.DirectorySeparatorChar + path[prefix.Length..]
            : null;
    }

    // MARK: - Filesystem moves

    /// Move a file or directory, whichever it is. Directory.Move and File.Move
    /// are separate calls on .NET; the graft only ever moves one at a time and
    /// never across volumes, so a plain move is enough.
    private static void Move(string from, string to)
    {
        try
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }
        catch { /* a move that fails leaves both sides where they were */ }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch { }
    }

    /// A folder's entries by name, empty for one that could not be read — the
    /// try? every walk in the Mac build takes.
    private static List<string> SafeEntries(string path)
    {
        try { return Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).ToList()!; }
        catch { return new List<string>(); }
    }
}
