namespace ClaudeGraft.Core;

/// <summary>
/// The filesystem questions the graft asks over and over. Kept apart from
/// <see cref="Junction"/>, which is only about links, because these hold for
/// any path.
/// </summary>
public static class Fs
{
    /// A path that is there at all, a dangling junction included. The Mac
    /// build's exists is fileExists || isSymlink for exactly this reason: a
    /// link whose target has gone is still a thing sitting at that name that a
    /// stash or a relink has to reckon with.
    public static bool Exists(string path) =>
        File.Exists(path) || Directory.Exists(path) || Junction.IsLink(path);

    public static bool IsDirectory(string path) => Directory.Exists(path);

    /// A reparse point — junction or symlink — whether it stands where a file or a
    /// directory would. <see cref="Junction.IsLink"/> only recognises directory
    /// links, so a symlinked <c>local_*.json</c> record needs this before it is
    /// read or written over.
    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    /// When the item was last written, or the distant past for one that is not
    /// there — which is what lets newestChild sort a missing entry last rather
    /// than throw.
    public static DateTime Modified(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
        }
        catch { return DateTime.MinValue; }
    }

    /// Two paths naming the same place. Windows filesystems are case-insensitive,
    /// so this is, and both sides are made absolute first so that a relative and
    /// an absolute spelling of one folder are not read as two.
    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
                      Path.GetFullPath(b).TrimEnd('\\'),
                      StringComparison.OrdinalIgnoreCase);

    /// Where a path really sits once every link on the way to it is followed.
    /// The equivalent of resolvingSymlinksInPath: a store two profiles share
    /// resolves to one place, so it is read once and a record filed through a
    /// link counts the same as one filed beside it.
    public static string Resolve(string path)
    {
        try
        {
            var info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : (FileSystemInfo)new FileInfo(path);
            var final = info.ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath(final?.FullName ?? path);
        }
        catch { return Path.GetFullPath(path); }
    }

    /// How much is in a folder, for the diagnostics log alone. A count is what
    /// makes a stash line worth reading a week later.
    public static int Entries(string path)
    {
        try { return Directory.EnumerateFileSystemEntries(path).Count(); }
        catch { return 0; }
    }
}
