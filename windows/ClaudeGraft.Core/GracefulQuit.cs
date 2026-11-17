using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGraft.Core;

/// <summary>
/// Asks Claudes to quit the way an installer does, through Windows' Restart
/// Manager: each is told the session is ending for it, which Claude answers by
/// quitting properly. Never forced: one that refuses is reported, not killed.
/// </summary>
public static class GracefulQuit
{
    /// A Claude to close, pinned by its start time so a pid reused by another
    /// process since is never the one asked.
    public readonly record struct Target(int Pid, DateTime Started);

    public static Target? Pin(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return new Target(pid, p.StartTime); }
        catch { return null; }
    }

    public static bool StillRunning(Target target)
    {
        try { using var p = Process.GetProcessById(target.Pid); return p.StartTime == target.Started && !p.HasExited; }
        catch { return false; }
    }

    /// Asks every target to quit and waits for them to go. Returns the ones
    /// still running afterwards: refused, or slower than the wait.
    public static IReadOnlyList<Target> Quit(IReadOnlyList<Target> targets, TimeSpan wait)
    {
        var live = targets.Where(StillRunning).ToList();
        if (live.Count == 0) return live;
        var key = new StringBuilder(64);
        if (RmStartSession(out var session, 0, key) != 0) return live;
        try
        {
            var processes = live.Select(t => new RmUniqueProcess
            {
                ProcessId = t.Pid,
                StartTime = ToFileTime(t.Started),
            }).ToArray();
            if (RmRegisterResources(session, 0, null, (uint)processes.Length, processes, 0, null) != 0) return live;
            // No force flag: an app that will not close is left running.
            var result = RmShutdown(session, 0, IntPtr.Zero);
            Diagnostics.Note("claude.quit", new Dictionary<string, object?> { ["count"] = live.Count, ["result"] = result });
        }
        finally { RmEndSession(session); }

        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline && live.Any(StillRunning)) Thread.Sleep(500);
        return live.Where(StillRunning).ToList();
    }

    private static System.Runtime.InteropServices.ComTypes.FILETIME ToFileTime(DateTime local)
    {
        var ticks = local.ToFileTime();
        return new() { dwLowDateTime = (int)(ticks & 0xFFFFFFFF), dwHighDateTime = (int)(ticks >> 32) };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[]? names, uint processes,
        RmUniqueProcess[] list, uint services, string[]? serviceNames);
    [DllImport("rstrtmgr.dll")] private static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
}
