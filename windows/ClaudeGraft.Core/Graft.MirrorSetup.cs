using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public static partial class Graft
{
    /// Share the safe files, then map this profile's chat directory onto the
    /// source's. Chats are stored per account, so when the two profiles are on
    /// different accounts the link is made one level deeper.
    public static void GraftInto(string source, string profile)
    {
        using var stateLock = StateLock.Acquire();
        // A profile pointed at itself would stash every one of its own files
        // away and replace them with links to nothing.
        if (Fs.SamePath(Fs.Resolve(source), Fs.Resolve(profile))) return;
        Directory.CreateDirectory(profile);

        foreach (var item in SharedItems)
            Relink(Path.Combine(source, item), Path.Combine(profile, item));
        MirrorChatStores(source, profile);
        CopyAppearance(source, profile);
    }

    /// Make a folder ready to be filled from the source: real, so the profile
    /// can write into it at all, and emptied of what it held — the profile's own
    /// chats going to the same hidden sibling a linked graft would have moved
    /// them to, which <see cref="SeedOwnChats"/> then copies straight back in.
    /// The stash keeps the copy that makes the merge reversible; the folder gets
    /// the copy the person reads.
    ///
    /// firstPass is what stops the second launch stashing away everything the
    /// first one mirrored in, and what stops the seeding running twice.
    private static void OpenForMirror(string folder, bool firstPass)
    {
        if (Junction.IsLink(folder)) Junction.Remove(folder);
        else if (firstPass) Stash(folder);
        Directory.CreateDirectory(folder);
    }

    /// The stashed copy of an organization folder, whichever way it was put away
    /// — the sibling a cross-account graft leaves, or the same records one level
    /// down inside a whole store two same-account profiles stash. Null rather
    /// than a guess when neither is there.
    private static string? StashedCounterpart(string folder, string store)
    {
        var sibling = StashPath(folder);
        if (Fs.IsDirectory(sibling)) return sibling;
        var account = Path.GetFileName(Path.GetDirectoryName(folder)!);
        var inside = Path.Combine(StashPath(store), account, Path.GetFileName(folder));
        return Fs.IsDirectory(inside) ? inside : null;
    }

    /// Take the borrowed copies back out of a folder about to be stashed a
    /// second time. A first pass that finds a stash already in place is really a
    /// second one, and folding it back in would leave nothing saying which of the
    /// merged records were the profile's own. A record the source holds byte for
    /// byte that the stash does not name is a borrowed one; it comes back in on
    /// the pass that follows.
    ///
    /// Only ever inside the profile: a shortcut left on an older shape has a
    /// folder that IS the source, and every byte matches because of it, so this
    /// would delete the lender's whole history. The rule is whether the folder
    /// resolves inside the profile at all.
    private static int DropBorrowedCopies(string folder, string source, string store)
    {
        var profile = Fs.Resolve(Path.GetDirectoryName(store)!) + Path.DirectorySeparatorChar;
        if (!(Fs.Resolve(folder) + Path.DirectorySeparatorChar).StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            return 0;
        var own = StashedCounterpart(folder, store);
        if (own is null) return 0;

        var dropped = 0;
        foreach (var name in SafeEntries(folder).Where(IsMirrored))
        {
            if (Fs.Exists(Path.Combine(own, name))) continue;
            try
            {
                var mine = File.ReadAllBytes(Path.Combine(folder, name));
                var theirs = File.ReadAllBytes(Path.Combine(source, name));
                if (!mine.AsSpan().SequenceEqual(theirs)) continue;
                File.Delete(Path.Combine(folder, name));
                dropped++;
            }
            catch { }
        }
        if (dropped > 0)
            Diagnostics.Note("mirror.reborrowed", new Dictionary<string, object?>
            {
                ["folder"] = folder, ["dropped"] = dropped,
                ["because"] = "a stash already names what this profile brought, so these were borrowed",
            });
        return dropped;
    }

    /// Put the profile's own chats into the shared set, so a graft merges the
    /// two histories instead of replacing one. Copied, not moved: the stash goes
    /// on holding them, since with the copies gone from the shared set it is the
    /// only thing that still knows which merged records this profile brought.
    private static int SeedOwnChats(string folder, string store)
    {
        var own = StashedCounterpart(folder, store);
        if (own is null) return 0;
        var seeded = 0;
        foreach (var name in SafeEntries(own).Where(IsMirrored))
        {
            var to = Path.Combine(folder, name);
            if (Fs.Exists(to)) continue;
            try { File.Copy(Path.Combine(own, name), to); seeded++; }
            catch { }
        }
        Diagnostics.Note("mirror.seed", new Dictionary<string, object?> { ["folder"] = folder, ["own"] = seeded });
        return seeded;
    }

    /// Give a profile its own copy of the source's chats rather than a link, and
    /// keep the two in step — a link being the reason a grafted profile cannot
    /// archive, rename or delete anything. The pairing is this profile's
    /// <account>/<org> against the source's active one, where each side's Claude
    /// actually reads.
    private static void MirrorChatStores(string source, string profile)
    {
        var sourceAccount = Account(source);
        if (sourceAccount is null) return;
        // Unreadable is not the same as signed out: a config caught mid-rename
        // read as "no account, so the same as the source" would copy one
        // account's chats into another's folder.
        var ownConfig = ReadableConfigJson(profile);
        if (ownConfig is null) return;
        var ownAccount = ownConfig["lastKnownAccountUuid"]?.GetValue<string>();

        foreach (var store in GraftPaths.ChatStoreNames)
        {
            var src = Path.Combine(source, store);
            var dst = Path.Combine(profile, store);
            if (!Fs.IsDirectory(src)) continue;

            if (ownAccount is not null && ownAccount != sourceAccount)
                MirrorAcrossAccounts(src, sourceAccount, dst, ownAccount);
            else
                MirrorWithinOneAccount(src, dst, ownAccount ?? sourceAccount);

            // Keyed by organization, not a sidebar, so it stays a link — and made
            // after both branches, since a same-account graft stashes the whole
            // store and would take a link made first away with it.
            var skills = Path.Combine(src, "skills-plugin");
            if (Fs.IsDirectory(skills)) Relink(skills, Path.Combine(dst, "skills-plugin"));
        }
    }

    /// One account on both sides: every organization it has is shared. Read the
    /// source before anything of this profile's is moved — opening the
    /// destination first was a store emptied on the strength of a source that had
    /// nothing under this account.
    private static void MirrorWithinOneAccount(string src, string dst, string account)
    {
        var sourceAccountDir = CounterpartDirectory(src, account);
        if (sourceAccountDir is null)
        {
            Diagnostics.Note("mirror.nothingToShare", new Dictionary<string, object?>
            {
                ["source"] = src, ["account"] = account,
                ["because"] = "the source holds no folder for this account",
            });
            return;
        }
        var theirAccount = Path.Combine(src, sourceAccountDir);
        var orgs = SafeEntries(theirAccount)
            .Where(n => !n.StartsWith('.') && !n.EndsWith(StashSuffix))
            .Where(n => Fs.IsDirectory(Path.Combine(theirAccount, n)))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (orgs.Count == 0)
        {
            Diagnostics.Note("mirror.nothingToShare", new Dictionary<string, object?>
            {
                ["source"] = theirAccount, ["account"] = account,
                ["because"] = "the source holds no organization folder under this account",
            });
            return;
        }

        var ownAccountDir = CounterpartDirectory(dst, account) ?? account;
        var mine = orgs.Select(org =>
        {
            var ownAccountPath = Path.Combine(dst, ownAccountDir);
            return Path.Combine(ownAccountPath, CounterpartDirectory(ownAccountPath, org) ?? org);
        }).ToList();
        var theirs = orgs.Select(org => Path.Combine(theirAccount, org)).ToList();
        ForgetStalePairs(dst, mine.Zip(theirs).Select(p => (p.First, p.Second)).ToList());

        // Read before the store is opened, and held for every organization: the
        // first pass stashes the store as a whole, so asking again after the
        // first pairing would say no to seeding the rest.
        var firstPass = MirrorPairsBorrowedBy(dst).Count == 0;
        if (firstPass)
            for (var i = 0; i < mine.Count; i++) DropBorrowedCopies(mine[i], theirs[i], dst);
        OpenForMirror(dst, firstPass);
        for (var i = 0; i < mine.Count; i++)
        {
            OpenForMirror(mine[i], firstPass: false);
            if (firstPass) SeedOwnChats(mine[i], dst);
            MirrorChatFolders(mine[i], theirs[i]);
        }
    }

    /// Two accounts: this profile's own <account>/<org> is paired with the
    /// source's active one, where each side's Claude reads.
    private static void MirrorAcrossAccounts(string src, string sourceAccount, string dst, string ownAccount)
    {
        var sourceAccountDir = CounterpartDirectory(src, sourceAccount);
        var sourceOrg = sourceAccountDir is null ? null : NewestChild(Path.Combine(src, sourceAccountDir));
        if (sourceAccountDir is null || sourceOrg is null)
        {
            Diagnostics.Note("mirror.nothingToShare", new Dictionary<string, object?>
            {
                ["source"] = src, ["account"] = sourceAccount,
                ["because"] = "the source holds no organization folder under this account",
            });
            return;
        }
        var ownAccountDir = CounterpartDirectory(dst, ownAccount) ?? ownAccount;
        // The source is asked about its own spelling of this profile's account: a
        // profile with no organization folder of its own yet may still have one
        // sitting in the store it is borrowing from.
        var heldDir = CounterpartDirectory(src, ownAccount);
        var orgHeldBySource = heldDir is null ? null : NewestChild(Path.Combine(src, heldDir));
        var ownOrg = NewestChild(Path.Combine(dst, ownAccountDir)) ?? orgHeldBySource;
        if (ownOrg is null) return;

        // A store-wide link, from when these two were on one account.
        if (Junction.IsLink(dst)) OpenForMirror(dst, firstPass: false);
        var mine = Path.Combine(dst, ownAccountDir, ownOrg);
        var theirs = Path.Combine(src, sourceAccountDir, sourceOrg);
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        ForgetStalePairs(dst, new List<(string, string)> { (mine, theirs) });
        var firstPass = MirrorPairsBorrowedBy(mine).Count == 0;
        if (firstPass) DropBorrowedCopies(mine, theirs, dst);
        OpenForMirror(mine, firstPass);
        if (firstPass) SeedOwnChats(mine, dst);
        MirrorChatFolders(mine, theirs);
    }

    /// Theme and locale sit in config.json beside this profile's credentials, so
    /// those two keys are copied rather than the file being linked — and never
    /// over a config caught mid-rename, which took the login and the account its
    /// chats are filed under along with it.
    private static void CopyAppearance(string source, string profile)
    {
        var from = ConfigJson(source);
        if (from.Count == 0) return;
        var into = ReadableConfigJson(profile);
        if (into is null) return;

        var wanted = AppearanceKeys.Where(k => from.ContainsKey(k)).ToList();
        // Nothing to gain from rewriting a file that holds a login, every launch,
        // to put back the values already in it.
        if (!wanted.Any(k => !JsonNode.DeepEquals(into[k], from[k]))) return;
        foreach (var key in wanted) into[key] = from[key]?.DeepClone();

        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(into, new JsonSerializerOptions { WriteIndented = true });
            AtomicWrite.Bytes(Path.Combine(profile, "config.json"), data);
        }
        catch { }
    }
}
