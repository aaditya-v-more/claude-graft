namespace ClaudeGraft.Core;

public static partial class Graft
{
    /// Stop mirroring anything belonging to a profile, without moving a file.
    /// The pairs outlive the graft that made them — they are the keys of a state
    /// file every launcher reads — so a deleted or returned profile leaves a pair
    /// that would go on being squared up by whichever profile opened next.
    public static void ForgetMirrors(string profile) =>
        StateLock.Run(() => ForgetMirrorsUnlocked(profile));

    private static void ForgetMirrorsUnlocked(string profile)
    {
        var stale = MirrorPairsUnder(profile).ToHashSet();
        if (stale.Count == 0) return;
        Diagnostics.Note("mirror.forget", new Dictionary<string, object?>
        {
            ["profile"] = Path.GetFileName(profile), ["pairs"] = stale.Count,
        });
        DropPairs(stale);
    }

    private static void DropPairs(ISet<string> stale)
    {
        var state = LoadMirrorState();
        state.Pairs = state.Pairs.Where(kv => !stale.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        SaveMirrorState(state);
    }

    /// Drop the pairs this store borrows through that name a folder nobody writes
    /// to any more. Signing in again can move <account>/<org> on either side, and
    /// the pair an earlier pass wrote down then names a folder nobody reads —
    /// which a launcher would carry across as a wholesale deletion. The pairs this
    /// pass is actually mirroring are the ones kept, so it is given them whole
    /// rather than a folder to find a half in. Borrowed pairs alone: what this
    /// store lends is somebody else's mirror, kept current by their own launch.
    private static void ForgetStalePairs(string store, IReadOnlyList<(string mine, string theirs)> live)
    {
        var kept = live.Select(p => PairKey(p.mine, p.theirs)).ToHashSet();
        var stale = MirrorPairsBorrowedBy(store).Where(k => !kept.Contains(k)).ToHashSet();
        if (stale.Count == 0) return;
        Diagnostics.Note("mirror.stale", new Dictionary<string, object?>
        {
            ["store"] = store, ["dropped"] = stale.Count,
            ["because"] = "these name a folder one side has stopped writing to",
        });
        DropPairs(stale);
    }

    /// Take a mirrored profile back off the shared set, and lose nothing on the
    /// way. A mirrored folder is a real one full of copies, so unlike a link it
    /// cannot simply be dropped: one last pass carries everything the profile did
    /// while mirroring over to the profile it borrowed from, and only then are the
    /// copies taken out — a copy going only when the other side holds the same
    /// bytes this moment, so a deleted source or an unreadable folder leaves every
    /// copy where it is.
    public static int UnmirrorChatStores(string profile) =>
        StateLock.Run(() => UnmirrorChatStoresUnlocked(profile));

    /// One last pass, then out with every copy the other side holds byte for
    /// byte that the stash does not name; returns how many went.
    private static int HandBack(string mine, string theirs)
    {
        MirrorChatFolders(mine, theirs);

        // Which of the merged records this profile brought, asked of the stash
        // because it is the only thing that still knows. Since the merge these
        // have been on both sides, so the test below would otherwise take the
        // profile's own history out and leave it in the one it was only lent to.
        var store = Path.GetDirectoryName(Path.GetDirectoryName(mine)!)!;
        var own = StashedCounterpart(mine, store);
        var removed = 0;
        foreach (var name in SafeEntries(mine).Where(IsMirrored))
        {
            // Kept rather than removed and fetched back out of the stash, so a
            // chat of its own archived while grafted stays archived.
            if (own is not null && Fs.Exists(Path.Combine(own, name))) continue;
            try
            {
                var here = File.ReadAllBytes(Path.Combine(mine, name));
                var there = File.ReadAllBytes(Path.Combine(theirs, name));
                if (!here.AsSpan().SequenceEqual(there)) continue;
                File.Delete(Path.Combine(mine, name));
                removed++;
            }
            catch { }
        }
        return removed;
    }

    /// Take what a folder borrowed from one source back out before it borrows
    /// from another. Left in, the old source's chats were carried into the new
    /// one as this profile's own. A source that is gone has nothing to be handed
    /// back to, and its copies are all that is left of those chats, so they stay.
    private static void Repair(string mine, string old)
    {
        if (!Fs.IsDirectory(old))
        {
            Diagnostics.Note("mirror.repair.kept", new Dictionary<string, object?>
            {
                ["folder"] = mine, ["was"] = old,
                ["because"] = "the folder it borrowed from is gone, so its copies are kept",
            });
            return;
        }
        var returned = HandBack(mine, old);
        Diagnostics.Note("mirror.repair", new Dictionary<string, object?>
        {
            ["folder"] = mine, ["was"] = old, ["returned"] = returned,
        });
    }

    private static int UnmirrorChatStoresUnlocked(string profile)
    {
        // The pairs this profile borrows through, never the ones it lends: a
        // lender picking up a borrower's pair reads its own folder as the borrowed
        // one and hands its whole history over. Being nobody's borrower is the
        // ordinary case, and the ordinary answer is to do nothing at all.
        var keys = MirrorPairsBorrowedBy(profile);
        if (keys.Count == 0) return 0;
        var removed = 0;

        foreach (var key in keys)
        {
            if (PairFolders(key) is not (string mine, string theirs)) continue;
            removed += HandBack(mine, theirs);
        }
        // Only the pairs this pass settled: a borrower whose pair was dropped
        // reads its next launch as a first pass and stashes the merged folder.
        DropPairs(keys.ToHashSet());
        Diagnostics.Note("unmirror", new Dictionary<string, object?>
        {
            ["profile"] = Path.GetFileName(profile), ["pairs"] = keys.Count, ["copiesRemoved"] = removed,
        });
        return removed;
    }

    /// Undo a graft: drop every link this profile holds so it falls back to its
    /// own storage. Real files it wrote itself are left alone.
    public static void Ungraft(string profile) =>
        StateLock.Run(() => UngraftUnlocked(profile));

    private static void UngraftUnlocked(string profile)
    {
        Diagnostics.Note("ungraft.begin", new Dictionary<string, object?> { ["profile"] = Path.GetFileName(profile) });
        UnmirrorChatStores(profile);
        foreach (var item in SharedItems)
            Unstash(Path.Combine(profile, item));
        // No longer linked, but older grafts left a link and a backup behind, so
        // the local settings file and anything stashed under it still come back.
        Unstash(Path.Combine(profile, "claude_desktop_config.json"));
        foreach (var store in GraftPaths.ChatStoreNames)
        {
            var dst = Path.Combine(profile, store);
            if (Junction.IsLink(dst)) { Unstash(dst); continue; }
            foreach (var account in SafeEntries(dst).Where(a => !a.EndsWith(StashSuffix)))
            {
                var accountDir = Path.Combine(dst, account);
                if (Junction.IsLink(accountDir)) { Unstash(accountDir); continue; }
                foreach (var org in SafeEntries(accountDir).Where(o => !o.EndsWith(StashSuffix)))
                    Unstash(Path.Combine(accountDir, org));
            }
            // A whole store goes to the stash when both profiles are on one
            // account, and it has to come back whether a link or a real mirror
            // folder stands in its place. Only the link was ever looked for.
            Unstash(dst);
            Unstash(Path.Combine(dst, "skills-plugin"));
        }
        Diagnostics.Note("ungraft.end", new Dictionary<string, object?> { ["profile"] = Path.GetFileName(profile) });
    }
}
