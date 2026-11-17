using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeGraft.Core;

/// <summary>
/// A directory junction is the Windows stand-in for the symlink the mac build
/// grafts with. Measured on this machine against every property the graft
/// depends on: created without administrator rights, a read through it sees the
/// target, a write through it lands in the target, and dropping it leaves the
/// target untouched — the reversibility the whole app rests on.
///
/// The one rule the rest of the app is built on is <see cref="ResolvesInside"/>:
/// a real folder is one a profile will write a record into, a link is not.
/// mac asks whether a path is a symlink; here the equivalent is a reparse
/// point whose target climbs out of the profile.
/// </summary>
public static class Junction
{
    /// Create <paramref name="linkPath"/> as a junction onto <paramref name="target"/>.
    /// Both the mac symlink and this take the link ahead of the target, and the
    /// graft is only ever made with the profile's own folder as the link.
    public static void Create(string linkPath, string target)
    {
        // .NET only makes symbolic links, which need administrator rights or
        // Developer Mode; a junction needs neither. Written as a reparse point
        // directly, since cmd /c mklink expands %VAR% even inside quotes.
        target = Path.GetFullPath(target);
        if (target.StartsWith(@"\\"))
            throw new IOException($"could not create junction {linkPath}: a junction needs a local target");
        if (Fs.Exists(linkPath))
            throw new IOException($"could not create junction {linkPath}: something is already there");
        Directory.CreateDirectory(linkPath);
        try
        {
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            var display = Encoding.Unicode.GetBytes(target);
            // REPARSE_DATA_BUFFER for a mount point: an 8-byte header, four
            // offset/length words, then both names NUL-terminated.
            var data = new byte[16 + substitute.Length + 2 + display.Length + 2];
            BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
            BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(data, 12);
            BitConverter.GetBytes((ushort)display.Length).CopyTo(data, 14);
            substitute.CopyTo(data, 16);
            display.CopyTo(data, 18 + substitute.Length);
            // GENERIC_WRITE, OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT.
            using var handle = CreateFile(linkPath, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException($"could not create junction {linkPath} -> {target}",
                    new Win32Exception(Marshal.GetLastWin32Error()));
        }
        catch { Directory.Delete(linkPath); throw; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input,
        int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    /// A junction, or any reparse point, carries this attribute. The mac build's
    /// isSymlink check answers here.
    public static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// Where a junction points, or null for a path that is not one. .NET reads
    /// the reparse target directly; the value is what tells a grafted folder
    /// from one the profile owns.
    public static string? Target(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Attributes.HasFlag(FileAttributes.ReparsePoint)
            ? info.LinkTarget
            : null;
    }

    /// The rule the session sweep exists to work around: a profile will not
    /// write a record into a folder that resolves outside itself. A real
    /// directory the profile owns resolves inside <paramref name="profile"/>;
    /// a graft junction resolves out to the source. Asked of the folder that a
    /// record would land in, never of the account it is stamped with.
    public static bool ResolvesInside(string path, string profile)
    {
        // Every link on the way is followed, not just the last: a real folder
        // reached through a linked store is still the source's folder.
        string full, root;
        try { full = WindowsPaths.Resolve(path); root = WindowsPaths.Resolve(profile); }
        catch (IOException) { return false; }
        root = Path.TrimEndingDirectorySeparator(root);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// Drop the link without following it into the target. Directory.Delete on a
    /// junction removes the link alone — measured leaving every file in the
    /// target where it was, which is what makes going back a handover rather
    /// than a delete.
    public static void Remove(string linkPath)
    {
        if (IsLink(linkPath))
            Directory.Delete(linkPath);
    }
}
