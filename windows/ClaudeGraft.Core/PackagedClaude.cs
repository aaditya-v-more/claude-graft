using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace ClaudeGraft.Core;

/// <summary>
/// Claude Desktop installed as an MSIX package, which is what the download now
/// gives a fresh machine. The binary sits under the protected WindowsApps folder
/// and registers no execution alias, so it is started through package
/// activation, which is also the only route that runs it with its identity.
/// Windows redirects its AppData into the package's own LocalCache, so its
/// profiles are not in %APPDATA% (<see cref="ClaudeInstallation"/>).
/// </summary>
public static class PackagedClaude
{
    /// The website download, then the Microsoft Store listing. They have not been
    /// seen installed side by side.
    public static readonly string[] FamilyNames = ["Claude_pzs8sxrjxfjjc", "AnthropicPBC.Claude_fnn82j28hfe8t"];

    public sealed record Install(string Root, string Exe, string AppUserModelId, string FamilyName);

    /// Replaced in tests; nothing off Windows has packages to find.
    public static Func<Install?> Locate = () => OperatingSystem.IsWindows() ? Find() : null;

    /// A package installs to WindowsApps\<name>_<version>_<arch>__<publisher id>.
    public static bool IsPackagePath(string command) => FamilyNames.Any(family =>
    {
        var split = family.LastIndexOf('_');
        return command.Contains(@"\WindowsApps\" + family[..split] + "_", StringComparison.OrdinalIgnoreCase)
            && command.Contains("__" + family[(split + 1)..], StringComparison.OrdinalIgnoreCase);
    });

    private static Install? Find()
    {
        foreach (var family in FamilyNames)
        foreach (var fullName in PackagesInFamily(family))
        {
            var root = PathOf(fullName);
            if (root is null) continue;
            string? manifest;
            try { manifest = File.ReadAllText(Path.Combine(root, "AppxManifest.xml")); }
            catch { continue; }
            var install = FromManifest(root, manifest, family);
            if (install is not null && File.Exists(install.Exe)) return install;
        }
        return null;
    }

    /// The first application the manifest declares, as an exe path and the id
    /// activation wants. Pure so the suite can hand it a manifest.
    public static Install? FromManifest(string root, string? manifest, string family)
    {
        if (manifest is null) return null;
        try
        {
            var app = XDocument.Parse(manifest).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Application"
                                     && e.Parent?.Name.LocalName == "Applications");
            var id = app?.Attribute("Id")?.Value;
            var exe = app?.Attribute("Executable")?.Value;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(exe)) return null;
            return new Install(root, Path.Combine(root, exe), family + "!" + id, family);
        }
        catch { return null; }
    }

    /// Starts the packaged Claude with the given arguments, or returns false so
    /// the caller can say nothing opened.
    public static bool Activate(Install install, string arguments)
    {
        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            return manager.ActivateApplication(install.AppUserModelId, arguments, AoNoErrorUi, out _) >= 0;
        }
        catch { return false; }
    }

    // MARK: - Interop

    private const int AoNoErrorUi = 0x2;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(
        string family, ref uint count, IntPtr[]? fullNames, ref uint bufferLength, char[]? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint length, char[]? path);

    private static List<string> PackagesInFamily(string family)
    {
        uint count = 0, length = 0;
        if (GetPackagesByPackageFamily(family, ref count, null, ref length, null) != ErrorInsufficientBuffer
            || count == 0)
            return new List<string>();
        var names = new IntPtr[count];
        var buffer = new char[length];
        if (GetPackagesByPackageFamily(family, ref count, names, ref length, buffer) != 0)
            return new List<string>();
        // The pointers land inside the buffer; the names are its NUL-separated runs.
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).Take((int)count).ToList();
    }

    private static string? PathOf(string fullName)
    {
        uint length = 0;
        if (GetPackagePathByFullName(fullName, ref length, null) != ErrorInsufficientBuffer) return null;
        var path = new char[length];
        return GetPackagePathByFullName(fullName, ref length, path) == 0
            ? new string(path, 0, (int)length - 1)
            : null;
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
    }
}
