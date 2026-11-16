namespace ClaudeGraft.Core;

/// Whether an open of one profile is under way. A second press while one runs is
/// refused rather than queued: each Open would take the launch lock in turn and
/// then find the first one's Claude running, which is a wait for nothing.
public sealed class OpenInFlight
{
    private int _active;

    public bool Active => Volatile.Read(ref _active) == 1;

    /// Raised on the caller's context, before the work starts and once it is over.
    public event Action? Changed;

    /// False when one is already running, in which case <paramref name="work"/> never starts.
    /// A throw from it clears the state first and then reaches the caller.
    public async Task<bool> RunAsync(Func<Task> work)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return false;
        Changed?.Invoke();
        try { await work(); }
        finally
        {
            Volatile.Write(ref _active, 0);
            Changed?.Invoke();
        }
        return true;
    }
}
