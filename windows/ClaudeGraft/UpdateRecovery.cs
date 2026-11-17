using System.Diagnostics;
using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// Puts a shortcut's Claude back after Claude restarts itself to update.
/// Squirrel relaunches with no arguments, so the shortcut's Claude closes and
/// the main one opens in its place. An ordinary quit is never undone: only a
/// Claude that Squirrel's log names, after it started, counts as an update.
/// </summary>
public static class UpdateRecovery
{
    private static readonly HashSet<(int, DateTime)> Watched = new();

    /// The main Claude Squirrel opened in a shortcut's place, offered for closing.
    public static event Action<GracefulQuit.Target, string>? StrayOpened;

    public static void Start() => WindowIdentity.ShortcutFound += Watch;

    private static void Watch(int pid, DateTime started, string folder)
    {
        lock (Watched) if (!Watched.Add((pid, started))) return;
        try
        {
            var process = Process.GetProcessById(pid);
            if (process.StartTime != started) { process.Dispose(); return; }
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                lock (Watched) Watched.Remove((pid, started));
                process.Dispose();
                _ = Task.Run(() => Exited(pid, started, folder, DateTime.Now));
            };
        }
        catch (Exception) { lock (Watched) Watched.Remove((pid, started)); }
    }

    private static async Task Exited(int pid, DateTime started, string folder, DateTime exitedAt)
    {
        // Squirrel writes its line as it starts waiting on this process, so by
        // the time it has gone the line is there or is moments away.
        var updating = false;
        for (var i = 0; i < 10 && !updating; i++)
        {
            updating = ClaudeDesktopUpdates.QuitToUpdate(ReadLog(), pid, started);
            if (!updating) await Task.Delay(1000);
        }
        if (!updating) return;

        var shortcut = App.Store.Shortcuts.ToList().FirstOrDefault(s => s.Folder == folder);
        Diagnostics.Note("update.recover", new Dictionary<string, object?> { ["folder"] = folder, ["known"] = shortcut is not null });
        if (shortcut is null) return;

        // Let the relaunch land first: opening the shortcut while Squirrel is
        // still swapping versions would race it.
        GracefulQuit.Target? stray = null;
        for (var i = 0; i < 30 && stray is null; i++)
        {
            await Task.Delay(2000);
            stray = NewMainClaude(exitedAt);
        }
        try { Launcher.Open(App.Store.ConfigFor(shortcut)); }
        catch (Exception e) { Diagnostics.Note("update.recover-failed", new Dictionary<string, object?> { ["error"] = e.Message }); }

        // A main Claude that was already open took the relaunch as a second
        // launch and kept the window it had; only one started by the relaunch
        // is the stray, and it is only offered for closing.
        await Task.Delay(5000);
        if (NewMainClaude(exitedAt) is { } opened && GracefulQuit.StillRunning(opened))
            StrayOpened?.Invoke(opened, shortcut.Name);
    }

    private static GracefulQuit.Target? NewMainClaude(DateTime since)
    {
        try
        {
            foreach (var (pid, command) in ClaudeProcesses.Enumerate())
                if (ClaudeProcesses.IsDefaultInstance(command) && GracefulQuit.Pin(pid) is { } t && t.Started >= since.AddSeconds(-2))
                    return t;
        }
        catch (IOException) { }
        return null;
    }

    /// Closes the stray main Claude once a person agreed, after checking it is
    /// still the same process and still the main Claude.
    public static void CloseStray(GracefulQuit.Target target)
    {
        try
        {
            var command = ClaudeProcesses.Enumerate().FirstOrDefault(p => p.pid == target.Pid).command;
            if (command is null || !ClaudeProcesses.IsDefaultInstance(command) || !GracefulQuit.StillRunning(target)) return;
            GracefulQuit.Quit(new[] { target }, TimeSpan.FromSeconds(30));
        }
        catch (IOException) { }
    }

    private static string ReadLog()
    {
        try
        {
            using var stream = new FileStream(ClaudeDesktopUpdates.ProcessStartLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch { return ""; }
    }
}
