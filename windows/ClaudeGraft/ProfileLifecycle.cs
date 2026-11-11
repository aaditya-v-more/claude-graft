using ClaudeGraft.Core;

namespace ClaudeGraft;

internal sealed class ProfileLifecycle : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim sweep = new(1, 1);
    public void Start() { _ = WatchAsync(); _ = SweepAsync(); }

    public async Task SweepAsync()
    {
        if (stop.IsCancellationRequested || !await sweep.WaitAsync(0)) return;
        try
        {
            await Task.Run(() =>
            {
                using var locked = StateLock.Acquire();
                var store = new ShortcutStore();
                if (store.LoadError is not null) return;
                var processes = ClaudeProcesses.Enumerate();
                var profiles = store.Shortcuts.Select(s => s.ProfileDir).Append(GraftPaths.DefaultProfile);
                Graft.MirrorKnownPairs();
                Graft.FileMissingSessionRecords(profiles, p => ClaudeProcesses.IsRunning(p, processes));
                SidebarSync.SynchronizeEveryProjectOrder();
            }, stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Diagnostics.Note("profiles.settle.failed", new Dictionary<string, object?> { ["error"] = e.GetType().Name }); }
        finally { sweep.Release(); }
    }

    private async Task WatchAsync()
    {
        HashSet<int>? previous = null;
        DateTimeOffset? settleAt = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var current = (await Task.Run(ClaudeProcesses.Enumerate, stop.Token))
                    .Where(p => ClaudeProcesses.IsClaudeDesktop(p.command) && !ClaudeProcesses.IsHelper(p.command))
                    .Select(p => p.pid).ToHashSet();
                if (previous is not null && previous.Except(current).Any()) settleAt = DateTimeOffset.UtcNow.AddSeconds(2);
                previous = current;
                if (settleAt is not null && DateTimeOffset.UtcNow >= settleAt)
                { settleAt = null; await SweepAsync(); }
            }
            catch (IOException) { previous = null; settleAt = null; }
            catch (OperationCanceledException) { return; }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => stop.Cancel();
}
