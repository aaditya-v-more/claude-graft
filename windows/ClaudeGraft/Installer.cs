using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// Writes the desktop shortcut a profile is opened from. The Mac builds a small
/// .app bundle carrying a copy of the launcher; Windows writes a .lnk pointing
/// at a copy of the launcher stub kept in a stable per-user spot, so the shortcut
/// goes on working after the tray app updates to a new versioned install path.
///
/// Deliberately blind to anything this app did not create: a .lnk is only ever
/// removed or overwritten when it already points at our own launcher, so an
/// unrelated shortcut that happens to share a name is left alone — the Windows
/// echo of the graft.json check that guards every destructive step on the Mac.
/// </summary>
public static class Installer
{
    /// Names that belong to Claude itself and must never be written over.
    public static readonly string[] ReservedNames = { "Claude", "Claude Graft" };

    private static string LauncherDir => Path.Combine(GraftPaths.OwnData, "launcher",
        System.Reflection.Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("N"));
    private static string LauncherExe => Path.Combine(LauncherDir, "GraftLaunch.exe");

    private static string DesktopDir => GraftPaths.ProfilesRootOverride is null
        ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        : Path.Combine(GraftPaths.ProfilesRootOverride, "Desktop");
    private static string LinkPath(string name) => Path.Combine(DesktopDir, name + ".lnk");

    public enum InstallError { ReservedName, NameTaken, MissingLauncher, WriteFailed }

    public sealed class InstallException(InstallError reason, string? detail = null) : Exception(detail ?? reason.ToString())
    {
        public InstallError Reason { get; } = reason;
    }

    /// Creates or updates the desktop shortcut for a profile.
    public static void Install(Shortcut shortcut)
    {
        if (Graft.ValidateWindowsName(shortcut.Name) is string problem || Graft.ValidateFolder(shortcut.Folder) is not null)
            throw new InstallException(InstallError.WriteFailed, "Use valid Windows shortcut and profile names.");
        if (ReservedNames.Contains(shortcut.Name, StringComparer.OrdinalIgnoreCase))
            throw new InstallException(InstallError.ReservedName, $"“{shortcut.Name}” is the name of Claude itself. Pick another.");

        if (!EnsureLauncher())
            throw new InstallException(InstallError.MissingLauncher, "This copy of Claude Graft is missing its launcher.");

        var path = LinkPath(shortcut.Name);
        Directory.CreateDirectory(DesktopDir);
        // Something already there that is not ours must not be clobbered.
        if (File.Exists(path) && !IsOurs(path))
            throw new InstallException(InstallError.NameTaken, $"A shortcut named “{shortcut.Name}” already exists that Claude Graft did not create.");

        try
        {
            var store = new ShortcutStore();
            ShortcutManifest.Save(shortcut, store.ConfigFor(shortcut));
            WriteLink(path, LauncherExe, "--config \"" + ShortcutManifest.FileFor(shortcut.Id) + "\"", ShortcutIcons.IconFor(shortcut),
                      $"Open {shortcut.Name} — a Claude Desktop profile");
        }
        catch (Exception e)
        {
            throw new InstallException(InstallError.WriteFailed, e.Message);
        }
    }

    /// Removes a shortcut's .lnk, and a stale one left by a rename. Only ever a
    /// link that points at our launcher.
    public static void Uninstall(Shortcut shortcut, string? previousName = null)
    {
        foreach (var name in (previousName is null ? new[] { shortcut.Name } : new[] { previousName }).Where(n => !string.IsNullOrEmpty(n)))
        {
            var path = LinkPath(name!);
            if (File.Exists(path) && IsOurs(path)) try { File.Delete(path); } catch { }
        }
    }

    /// The .lnk this app installed for a shortcut, or null if there is none of
    /// ours at that name.
    public static string? InstalledLink(Shortcut shortcut)
    {
        var path = LinkPath(shortcut.Name);
        return File.Exists(path) && IsOurs(path) ? path : null;
    }

    // MARK: - The stable launcher copy

    /// Copies the launcher stub into a stable per-user folder, refreshing it when
    /// the bundled copy is newer — the Windows echo of refreshLaunchers, so a
    /// shortcut written by an older version picks up the current launcher. Returns
    /// false when no stub can be found to copy.
    public static bool EnsureLauncher()
    {
        var source = StubSource();
        if (source is null) return File.Exists(LauncherExe);   // already copied on an earlier run

        Directory.CreateDirectory(LauncherDir);
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var dest = Path.Combine(LauncherDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // Copy only what changed, so an open shortcut's launcher is not
                // needlessly replaced under it.
                if (!File.Exists(dest) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(dest))
                    File.Copy(file, dest, overwrite: true);
            }
        }
        catch { return false; }
        return File.Exists(LauncherExe);
    }

    /// Where the launcher stub's build output sits — bundled beside the app once
    /// packaged, or found in the repo's build output during development.
    private static string? StubSource()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "launcher", "GraftLaunch.exe");
        if (File.Exists(bundled)) return Path.GetDirectoryName(bundled);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var built = Path.Combine(dir.FullName, "GraftLaunch", "bin");
            if (!Directory.Exists(built)) continue;
            var exe = Directory.EnumerateFiles(built, "GraftLaunch.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (exe is not null) return Path.GetDirectoryName(exe);
        }
        return null;
    }

    // MARK: - .lnk and icon

    /// Claude's own executable, so the shortcut wears Claude's icon in the Dock's
    /// Windows equivalent — the taskbar and Start menu.
    private static string ClaudeIcon() => Launcher.ClaudeExe() ?? "";

    private static bool IsOurs(string linkPath)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.CreateShortcut(linkPath);
            string target = link.TargetPath;
            var root = Path.Combine(GraftPaths.OwnData, "launcher");
            return Fs.IsInside(target, root) && Path.GetFileName(target).Equals("GraftLaunch.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void WriteLink(string path, string target, string arguments, string icon, string description)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Arguments = arguments;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        if (icon.Length > 0) link.IconLocation = icon + ",0";
        link.Description = description;
        link.Save();
    }
}
