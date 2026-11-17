namespace ClaudeGraft;

/// <summary>
/// Whether Claude Graft starts with Windows, held as a shortcut in the user's
/// Startup folder — the same one the installer drops there. The folder is the
/// single source of truth, so the toggle reads it directly rather than keeping a
/// copy that could disagree, and writes the same kind of shortcut the installer
/// does so the two are indistinguishable.
/// </summary>
internal static class AutoStart
{
    private const string LinkName = "Claude Graft.lnk";

    private static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName);

    /// The running executable — what a Startup shortcut has to point at.
    private static string Exe => Environment.ProcessPath ?? "";

    public static bool IsEnabled() => File.Exists(LinkPath);

    public static void Set(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

    private static void Enable()
    {
        var exe = Exe;
        if (exe.Length == 0) return;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.CreateShortcut(LinkPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Run extra Claude Desktop profiles";
            link.Save();
        }
        catch { }
    }

    private static void Disable()
    {
        try { if (File.Exists(LinkPath)) File.Delete(LinkPath); }
        catch { }
    }
}
