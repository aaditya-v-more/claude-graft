namespace ClaudeGraft.Core;

/// <summary>What a pass makes of one transcript.</summary>
public enum SessionFiling
{
    File,
    /// Claude wrote its own record, or an earlier pass wrote this one.
    AlreadyRecorded,
    /// Deleted in a sidebar once, and not to be brought back.
    Withdrawn,
    /// Written too recently to be sure Claude will not write the record itself —
    /// the owner's own window always does, within moments.
    TooRecent,
    /// No profile on this machine holds the owner's account, so there is nowhere
    /// the record would be read. Asked again next pass, because accounts move.
    NoOwnerProfile,
    /// Run from a terminal, or by a Claude without a bridge: never in a sidebar,
    /// so nothing is missing.
    NotADesktopSession,
    /// Worked in on another machine whose transcripts were carried here. Asked
    /// again next pass, since a drive not attached yet may be.
    FolderElsewhere,
}

/// <summary>What a pass does with a record that already exists on disk.</summary>
public enum SessionUpdate
{
    /// Nothing to do: the record is not one this app wrote, or it already says
    /// what the transcript says.
    Leave,
    /// The transcript has moved past the record, which still holds what this app
    /// last wrote — so the record is brought up to date.
    Refresh,
    /// Something else has rewritten the record — Claude's own hand, always
    /// richer than a parsed transcript — and this app never touches it again.
    TakenOver,
}

public static partial class Graft
{
    /// Whether a transcript still needs a record written for it.
    /// Whether the folder a session worked in could be on this machine at all.
    /// A record filed for a transcript carried over from elsewhere is a chat
    /// that cannot be opened. Asked of the drive and the home folder, never the
    /// folder itself: a worktree cleaned up after its chat closed is still this
    /// machine's history.
    public static bool SessionFolderIsHere(string cwd)
    {
        if (cwd.StartsWith('/')) return false;
        if (cwd.Length < 3 || !char.IsLetter(cwd[0]) || cwd[1] != ':' || cwd[2] is not ('\\' or '/')) return true;
        var parts = cwd.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var depth = parts.Length > 2 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
        var place = string.Join('\\', parts.Take(depth)) + (depth == 1 ? "\\" : "");
        return Directory.Exists(place);
    }

    public static SessionFiling DecideFiling(
        SessionFacts? facts,
        IReadOnlySet<string> recorded,
        IReadOnlySet<string> withdrawn,
        IReadOnlyCollection<double> deletions,
        DateTime lastWrite,
        string? ownerProfile,
        bool anyDesktopRunning,
        DateTime now,
        TimeSpan quietWindow,
        bool folderIsHere = true)
    {
        if (facts is null) return SessionFiling.NotADesktopSession;
        if (!folderIsHere) return SessionFiling.FolderElsewhere;
        if (recorded.Contains(facts.CliSessionId)) return SessionFiling.AlreadyRecorded;
        if (withdrawn.Contains(facts.CliSessionId)) return SessionFiling.Withdrawn;

        // A marker naming a record Claude wrote names it by an id no transcript
        // carries, so the only trace of which session went is timing: the one
        // that had just gone quiet is the one that was on screen when the delete
        // was pressed. A session still being written grows past the window and
        // files later, so this can only hold back one whose final line fell
        // inside it. Only markers this app cannot read by name reach here — the
        // caller keeps back any naming a session it has a transcript for.
        foreach (var d in deletions)
            if (facts.LastActivityAt > d - 60_000 && facts.LastActivityAt <= d)
                return SessionFiling.Withdrawn;

        // The wait is for one thing only: a Claude Desktop window writing the
        // record itself, which it does within a second of a session opening. The
        // window that will do so is the one the session was typed into, and its
        // account need not be the session's owner — the owner is stamped from the
        // command line login, the record is filed under the window's account. So
        // the question is whether any window at all is up, not whether one on the
        // owner's account is: asking only after the owner ran the sweep past a
        // window on another account and filed a duplicate into a second profile,
        // the stray chat that is the symptom here. No window up is nobody about to
        // write it, and waiting then is waiting for something that is not coming —
        // which is what made closing a chat and reaching straight for the other
        // profile the one move guaranteed to miss it.
        if (anyDesktopRunning && now - lastWrite < quietWindow) return SessionFiling.TooRecent;
        if (ownerProfile is null) return SessionFiling.NoOwnerProfile;
        return SessionFiling.File;
    }

    /// Whether a record this app filed gets rewritten as its transcript moves
    /// on. A record only stays this app's while it holds byte for byte what the
    /// last pass wrote there: Claude Desktop rewrites the records of the sessions
    /// it takes over — measured coming down over a filed record within seconds of
    /// the session being opened in the owner's window — and flattening one of
    /// those back into a parsed version would throw away everything a transcript
    /// cannot recover.
    public static SessionUpdate DecideUpdate(SessionFacts? authored, SessionFacts facts, bool diskMatchesAuthored)
    {
        if (authored is null || authored.Equals(facts)) return SessionUpdate.Leave;
        return diskMatchesAuthored ? SessionUpdate.Refresh : SessionUpdate.TakenOver;
    }

    /// Whether a record may be written into an organisation folder.
    ///
    /// A folder this pass could not read is not a folder with nothing in it. It
    /// is stashed behind a graft, or waiting on a permission, or gone for the
    /// moment while Claude renames something over it — and from outside, every
    /// session it holds looks like a session nobody ever filed. Writing those
    /// records again gives the profile a second copy of a history it already
    /// has, and a record written fresh says isArchived:false, so a chat put out
    /// of sight by hand comes back into the sidebar.
    ///
    /// A folder that is simply not there yet is a different thing and is filed
    /// into, since that is how a profile gets its first record — unless this app
    /// stashed it itself, which is the one absence it can recognise, and which
    /// <see cref="IsStashedAway"/> asks about all the way up so that both stash
    /// shapes are caught.
    public static bool MayFileRecords(string organisationDir, IReadOnlySet<string> storesRead)
    {
        if (!Fs.Exists(organisationDir)) return !IsStashedAway(organisationDir);
        return storesRead.Contains(Fs.Resolve(organisationDir));
    }
}
