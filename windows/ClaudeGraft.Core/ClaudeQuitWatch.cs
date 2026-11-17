using System.Management;

namespace ClaudeGraft.Core;

/// <summary>
/// Runs a pass a few seconds after any Claude quits. A Claude reads its records
/// once, as it starts, and one opened straight from the Start menu asks Graft
/// nothing on the way in, so what another profile changed has to land before then.
/// </summary>
public sealed class ClaudeQuitWatch : IDisposable
{
    private readonly Func<IReadOnlyList<string>> _filing;
    private readonly ManagementEventWatcher? _watcher;
    private readonly Timer _settle;
    private int _inFlight, _askedAgain;

    public ClaudeQuitWatch(Func<IReadOnlyList<string>> filing)
    {
        _filing = filing;
        // A moment's grace for the last of its writes, and for its helpers to follow
        // it out: a profile with any of them left still reads as running.
        _settle = new Timer(_ => Sweep(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery(
                "SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process' AND TargetInstance.Name = 'claude.exe'"));
            _watcher.EventArrived += (_, _) => _settle.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            _watcher.Start();
        }
        catch (Exception e)
        {
            Diagnostics.Note("quit-watch.unavailable", new Dictionary<string, object?> { ["error"] = e.Message });
        }
    }

    // A pass still running when another is asked for finishes, and one more follows:
    // it may have read a Claude as running that has since quit.
    private void Sweep()
    {
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) { Volatile.Write(ref _askedAgain, 1); return; }
        try
        {
            do
            {
                Volatile.Write(ref _askedAgain, 0);
                Launcher.Sweep(_filing());
                // Under the launch lock, so a shortcut opened meanwhile waits and opens on
                // the order this leaves.
                SidebarSync.WithLaunchLock(() =>
                {
                    if (Graft.RunningClaudes().Count == 0) SidebarSync.SynchronizeEveryProjectOrder();
                });
            } while (Volatile.Read(ref _askedAgain) == 1);
        }
        catch (Exception e)
        {
            Diagnostics.Note("quit-watch.sweep-failed", new Dictionary<string, object?> { ["error"] = e.Message });
        }
        finally { Volatile.Write(ref _inFlight, 0); }
        if (Volatile.Read(ref _askedAgain) == 1) Sweep();
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch { }
        _watcher?.Dispose();
        _settle.Dispose();
    }
}
