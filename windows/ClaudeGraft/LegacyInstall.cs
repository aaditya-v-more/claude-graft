using System.Diagnostics;
using ClaudeGraft.Core;
using Microsoft.Win32;

namespace ClaudeGraft;

/// <summary>
/// Carries a machine off the folder 1.x installed into, once a copy Velopack
/// installed is running. The old uninstaller is never run: it force-quits every
/// ClaudeGraft.exe, this one included, and takes the sharing lines out of every
/// Claude. Links into the old folder are pointed at the new one, and a junction
/// left where it stood keeps anything this pass could not find working.
/// </summary>
internal static class LegacyInstall
{
    private static string? Folder()
    {
        foreach (var name in Updater.LegacyUninstallKeys)
        {
            using var key = Registry.CurrentUser.OpenSubKey(name);
            if (key?.GetValue("InstallLocation") is string dir && dir.Trim().Length > 0) return dir.Trim();
        }
        return null;
    }

    /// Whether a 1.x install is still here as a real folder, not yet moved off.
    public static bool Present => Folder() is { } dir && Directory.Exists(dir) && !Junction.IsLink(dir);

    /// At every start of an installed copy; one that finds the folder held open tries again at the next.
    public static void MoveOff()
    {
        if (GraftUpdates.Root is null || Folder() is not { } legacy) return;
        var current = AppContext.BaseDirectory;
        if (Updater.Moved(current, legacy, legacy) is not null) return;

        StopCopiesIn(legacy);
        bool moved;
        try { moved = Installer.MoveOff(legacy, current); }
        catch (Exception e) { moved = false; Diagnostics.Note("legacy.move-failed", new Dictionary<string, object?> { ["folder"] = legacy, ["error"] = e.Message }); }
        if (!moved) return;

        foreach (var name in Updater.LegacyUninstallKeys)
            try { Registry.CurrentUser.DeleteSubKeyTree(name, throwOnMissingSubKey: false); } catch { }
        Diagnostics.Note("legacy.moved", new Dictionary<string, object?> { ["folder"] = legacy, ["to"] = current });
    }

    /// The 1.x app quits itself after starting the bridge installer; one started by hand may not have.
    private static void StopCopiesIn(string legacy)
    {
        foreach (var process in Process.GetProcessesByName("ClaudeGraft"))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    if (Updater.Moved(process.MainModule?.FileName ?? "", legacy, legacy) is null) continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch { }
            }
        }
    }

    /// Velopack removes its own folder and shortcuts; what Graft put elsewhere goes here.
    /// The launcher junction stays, dangling, so a reinstall brings the profile links back.
    public static void RemoveJunctions(string appRoot)
    {
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClaudeGraft");
        if (Junction.Target(programs) is { } target && Updater.Moved(target, appRoot, appRoot) is not null)
            try { Junction.Remove(programs); } catch { }
    }
}
