using ClaudeGraft.Core;

// What a generated desktop shortcut runs when clicked: open the profile named
// by its one argument — the shortcut's folder — bringing the storage in line
// and starting Claude on it, then exit. The heavy lifting is the shared core's;
// this is only the entry point a .lnk can carry, the way each Mac bundle carries
// a copy of the launcher.
//
// The record sweep the Mac runs here is not wired in yet; opening the profile
// is, which is the half a shortcut needs first.
// Run by the uninstaller while Graft is still there to run it from: nothing
// else puts back what a borrowing Claude's environment held before sharing.
// Before anything reaches the network or opens a Claude that would inherit them.
PoolEnvironment.ForgetInheritedLines();

// Run by Claude Code's hook at a turn's end and a permission it waits on. It must
// return at once, so it only leaves the event for Graft and never fails the hook.
if (args.Length == 1 && args[0] == ChimeHook.Flag)
{
    try { HookSignal.File(Console.In.ReadToEnd()); }
    catch (Exception e) { Diagnostics.Note("launcher.signal-failed", new Dictionary<string, object?> { ["error"] = e.Message }); }
    return 0;
}

if (args.Length == 1 && args[0] == "--stop-sharing")
{
    // Graft going takes its hook out of Claude Code's settings with it.
    ChimeHook.Remove(ChimeHook.SettingsFile);
    foreach (var path in PoolEnvironment.TakeOutLines(null, ClaudeProcesses.IsRunning))
        Console.WriteLine($"still open, or changed since Graft read it: {path}");
    return 0;
}

if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Diagnostics.Note("launcher.noArgs", null);
    return 1;
}

var folder = args[0].Trim();
Diagnostics.Note("launcher.run", new Dictionary<string, object?> { ["folder"] = folder });
try
{
    Launcher.OpenByFolder(folder);
    return 0;
}
catch (Exception e)
{
    Diagnostics.Note("launcher.failed", new Dictionary<string, object?>
    {
        ["folder"] = folder, ["error"] = e.GetType().Name + ": " + e.Message,
    });
    return 1;
}
