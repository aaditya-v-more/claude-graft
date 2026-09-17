using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGraft.Core;

public sealed record ClaudeInstallation(string Executable, string ProfilesRoot)
{
    public const string PackageFamily = "Claude_pzs8sxrjxfjjc";
    public static Func<ClaudeInstallation?> Discover { get; set; } = Find;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count,
        IntPtr names, ref uint length, IntPtr buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string name, ref uint length, StringBuilder? path);

    public static ClaudeInstallation? Find()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var name in PackageNames().OrderByDescending(PackageVersion))
        {
            uint length = 0;
            if (GetPackagePathByFullName(name, ref length, null) != 122) continue;
            var path = new StringBuilder((int)length);
            if (GetPackagePathByFullName(name, ref length, path) != 0) continue;
            var exe = Path.Combine(path.ToString(), "app", "Claude.exe");
            if (File.Exists(exe)) return new(exe, Path.Combine(local, "Packages", PackageFamily, "LocalCache", "Roaming"));
        }
        var root = Path.Combine(local, "AnthropicClaude");
        if (!Directory.Exists(root)) return null;
        var executable = NewestSquirrelExecutable(Directory.EnumerateDirectories(root, "app-*")
            .Select(d => Path.Combine(d, "claude.exe")).Where(File.Exists));
        return executable is null ? null : new(executable,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    public static string? NewestSquirrelExecutable(IEnumerable<string> candidates) => candidates
        .OrderByDescending(p => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(p))?[4..], out var v) ? v : new Version())
        .FirstOrDefault();

    private static Version PackageVersion(string name) =>
        Version.TryParse(name.Split('_').ElementAtOrDefault(1), out var version) ? version : new Version();

    private static List<string> PackageNames()
    {
        uint count = 0, length = 0;
        if (GetPackagesByPackageFamily(PackageFamily, ref count, IntPtr.Zero, ref length, IntPtr.Zero) != 122)
            return new();
        var names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var buffer = Marshal.AllocHGlobal(checked((int)length * 2));
        try
        {
            if (GetPackagesByPackageFamily(PackageFamily, ref count, names, ref length, buffer) != 0) return new();
            return Enumerable.Range(0, (int)count).Select(i => Marshal.PtrToStringUni(
                Marshal.ReadIntPtr(names, i * IntPtr.Size))!).ToList();
        }
        finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(buffer); }
    }
}
