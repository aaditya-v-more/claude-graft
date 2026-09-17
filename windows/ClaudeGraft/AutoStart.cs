using ClaudeGraft.Core;

namespace ClaudeGraft;

internal static class AutoStart
{
    private static string LinkPath => Path.Combine(GraftPaths.ProfilesRootOverride is null
        ? Environment.GetFolderPath(Environment.SpecialFolder.Startup)
        : Path.Combine(GraftPaths.ProfilesRootOverride, "Startup"), "Claude Graft.lnk");
    private static string Exe => Environment.ProcessPath ?? throw new IOException("Cannot locate Claude Graft.");

    private static bool IsOurs()
    {
        if (!File.Exists(LinkPath)) return false;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(LinkPath);
        string target = link.TargetPath;
        string description = link.Description;
        return Path.GetFileName(target).Equals("ClaudeGraft.exe", StringComparison.OrdinalIgnoreCase)
            && description == "Run extra Claude Desktop profiles";
    }

    public static bool IsEnabled()
    {
        try { return IsOurs(); } catch { return false; }
    }
    public static void Set(bool enabled)
    {
        if (File.Exists(LinkPath) && !IsOurs())
        {
            if (enabled) throw new IOException("An unrelated startup shortcut already uses that name.");
            return;
        }
        if (!enabled) { if (File.Exists(LinkPath)) File.Delete(LinkPath); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(LinkPath)!);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(LinkPath);
        link.TargetPath = Exe;
        link.WorkingDirectory = Path.GetDirectoryName(Exe);
        link.IconLocation = Exe + ",0";
        link.Description = "Run extra Claude Desktop profiles";
        link.Save();
    }
}
