using ClaudeGraft.Core;

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

    /// A shortcut from 2.1.1 or the installer of then carries no flag, so its
    /// sign-in launch would open the window; rewritten at every start, it gains one.
    public static void Refresh()
    {
        if (IsEnabled()) Enable();
    }

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
            // Start hidden applies to this launch only, never to one a person starts.
            link.Arguments = AutomaticLaunch.Flag;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = L10n.Text("Run extra Claude Desktop profiles");
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
