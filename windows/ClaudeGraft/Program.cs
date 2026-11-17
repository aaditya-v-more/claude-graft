using ClaudeGraft.Core;
using Velopack;

namespace ClaudeGraft;

/// <summary>
/// Velopack's setup and Update.exe start this exe with arguments of their own and
/// wait for it to act and leave, so their hooks run before any of the app does.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build()
            // Graft applies an update when it decides to; on, any launch, --quit included, applied whatever package lay newest on disk.
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => Installed())
            .OnBeforeUninstallFastCallback(_ => Uninstalling())
            .Run();
        RegisterLinks();

        // Before the single-instance check: the running Graft may be hidden in the
        // tray, and the chooser has to come up either way. This process shows only it.
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length > 1 && arguments[1] == "--open-link")
        {
            Link = arguments.Length == 3 ? arguments[2] : "";
            XamlGeneratedProgram.XamlGeneratedMain();
            return;
        }

        // --quit asks a running copy to shut down as its own Quit does: killed, it
        // leaves its taskbar strip inside Explorer, which stalls until Windows notices.
        var quit = Environment.GetCommandLineArgs().Contains("--quit");
        // Two copies fight over the tray, the relay and every state file, so a second launch only asks the first to show itself.
        using var running = new Mutex(true, @"Local\ClaudeGraft.Running", out var first);
        if (!first || quit)
        {
            if (!first) (quit ? QuitRequest : ShowRequest).Set();
            return;
        }
        XamlGeneratedProgram.XamlGeneratedMain();
    }

    /// The claude:// link this process was started to route, or null for the app itself.
    public static string? Link { get; private set; }

    /// Set by a second launch; the running copy opens its window.
    public static readonly EventWaitHandle ShowRequest = new(false, EventResetMode.AutoReset, @"Local\ClaudeGraft.Show");

    /// Set by a launch with --quit; the running copy quits.
    public static readonly EventWaitHandle QuitRequest = new(false, EventResetMode.AutoReset, @"Local\ClaudeGraft.Quit");

    // A machine coming from 1.x keeps the choice it made there; the move carries its link across.
    private static void Installed()
    {
        if (!LegacyInstall.Present) AutoStart.Set(true);
        RegisterLinks();
    }

    private static void RegisterLinks()
    {
        try { LinkRegistration.Write(Microsoft.Win32.Registry.CurrentUser, Environment.ProcessPath!); }
        catch (Exception e) { Diagnostics.Note("links.register-failed", new Dictionary<string, object?> { ["error"] = e.GetType().Name }); }
    }

    // What the 1.x uninstaller ran GraftLaunch --stop-sharing for, done in place.
    private static void Uninstalling()
    {
        try { ChimeHook.Remove(ChimeHook.SettingsFile); } catch { }
        try { PoolEnvironment.TakeOutLines(null, ClaudeProcesses.IsRunning); } catch { }
        AutoStart.Set(false);
        try { LinkRegistration.Remove(Microsoft.Win32.Registry.CurrentUser); } catch { }
        if (GraftUpdates.Root is { } root) try { LegacyInstall.RemoveJunctions(root); } catch { }
    }
}
