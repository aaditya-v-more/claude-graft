namespace ClaudeGraft.Core;

public static partial class Graft
{
    /// Most recently touched child directory, used to resolve the active
    /// organization inside an account directory.
    ///
    /// A directory, and the test is not decoration. Claude writes
    /// <c>&lt;org&gt;.profile-origin.json</c> as a plain file beside the
    /// organization folders and writes it as the organization is created, so for
    /// a moment it is the newest thing there — and handing that name back as an
    /// organization stashed the profile's own copy of the file and built a
    /// directory where it had been.
    public static string? NewestChild(string dir)
    {
        List<string> names;
        try { names = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).ToList()!; }
        catch { return null; }

        return names
            .Where(n => !n.StartsWith('.') && !n.EndsWith(StashSuffix))
            .Where(n => Fs.IsDirectory(Path.Combine(dir, n)))
            .Select(n => (name: n, date: Fs.Modified(Path.Combine(dir, n))))
            .OrderByDescending(x => x.date)
            .Select(x => x.name)
            .FirstOrDefault();
    }

    /// The directory <paramref name="parent"/> keeps <paramref name="name"/>'s
    /// contents in.
    ///
    /// <c>&lt;accountUuid&gt;/&lt;orgUuid&gt;</c> is the shape, except where it
    /// is not. A profile in local mode was measured naming both halves by their
    /// first eight characters — <c>local-agent-mode-sessions/ed417e0f/00000000</c>
    /// against <c>claude-code-sessions/ed417e0f-5edd-…/00000000-0000-…</c>, the
    /// same account and organization in the same profile. Asking such a store
    /// for the full uuid finds nothing at all, and a graft that read that as an
    /// empty store put the borrowing profile's whole history away.
    ///
    /// A shortened name is taken only when one of the two is a prefix of the
    /// other, the shorter is at least eight characters, and it is the only
    /// directory there that fits. Nothing here may put one account's chats into
    /// another's folder, so ambiguity gives back null and the caller falls back
    /// to the name it was given.
    public static string? CounterpartDirectory(string parent, string name)
    {
        if (Fs.IsDirectory(Path.Combine(parent, name))) return name;

        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName).ToList()!; }
        catch { return null; }

        var candidates = entries
            .Where(n => !n.StartsWith('.') && !n.EndsWith(StashSuffix))
            .Where(n => Math.Min(n.Length, name.Length) >= 8
                        && (n.StartsWith(name, StringComparison.Ordinal)
                            || name.StartsWith(n, StringComparison.Ordinal)))
            .Where(n => Fs.IsDirectory(Path.Combine(parent, n)))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
