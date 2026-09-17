using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeGraft.Core;

internal static class WindowsPaths
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    internal static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        using var handle = CreateFile(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not (2 or 3)) throw new IOException("Cannot resolve the filesystem path.", new Win32Exception(error));
            var parent = Path.GetDirectoryName(full);
            if (parent is null) return full;
            return Path.Combine(Resolve(parent), Path.GetFileName(full));
        }
        var result = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
        if (count == 0 || count >= result.Capacity)
            throw new IOException("Cannot resolve the filesystem path.", new Win32Exception(Marshal.GetLastWin32Error()));
        var value = result.ToString();
        return value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + value[8..]
            : value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }
}
