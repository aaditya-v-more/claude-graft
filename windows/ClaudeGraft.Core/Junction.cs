using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeGraft.Core;

public static class Junction
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input,
        int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    // cmd.exe expands percent-delimited paths even inside quotes. The reparse
    // API accepts the exact Unicode paths and needs no elevation.
    public static void Create(string linkPath, string target)
    {
        target = Path.GetFullPath(target);
        if (!Directory.Exists(target) || target.StartsWith(@"\\"))
            throw new IOException("A junction needs an existing local directory.");
        if (Fs.Exists(linkPath)) throw new IOException("The link path already exists.");
        Directory.CreateDirectory(linkPath);
        try
        {
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            var display = Encoding.Unicode.GetBytes(target);
            var data = new byte[16 + substitute.Length + 2 + display.Length + 2];
            BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
            BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(data, 12);
            BitConverter.GetBytes((ushort)display.Length).CopyTo(data, 14);
            substitute.CopyTo(data, 16);
            display.CopyTo(data, 18 + substitute.Length);
            using var handle = CreateFile(linkPath, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException("Could not create the directory junction.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        catch { Directory.Delete(linkPath); throw; }
    }

    public static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static string? Target(string path) => IsLink(path)
        ? new DirectoryInfo(path).LinkTarget : null;

    public static bool ResolvesInside(string path, string profile) =>
        Fs.IsInside(Fs.Resolve(path), Fs.Resolve(profile));

    public static void Remove(string linkPath)
    {
        if (!IsLink(linkPath)) return;
        if (File.GetAttributes(linkPath).HasFlag(FileAttributes.Directory)) Directory.Delete(linkPath);
        else File.Delete(linkPath);
    }
}
