using System.Security.Cryptography;
using System.Text;

namespace ClaudeGraft.Core;

/// <summary>
/// One pass at a time over mirrored-chats.json and session-records.json, across
/// the app and every launcher. Each reads a state file, works, and writes it
/// back; two at once and the second puts back a baseline from before the
/// first's copies, which the next pass reads as deletions.
///
/// Reentrant within a thread. Never taken inside the launch lock, which a pass
/// takes after this one in <see cref="Graft.SaveMirrorState"/>, so the two are
/// always held in one order.
/// </summary>
public static class StateLock
{
    /// A launcher stands in front of a window opening, so a pass that has hung
    /// is waited out this long and then skipped: a holder that is alive rather
    /// than hung would otherwise have its copies read back as deletions.
    public static TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(20);

    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static Mutex? _held;

    public static T Run<T>(Func<T> work)
    {
        Enter();
        try { return work(); }
        finally { Leave(); }
    }

    public static void Run(Action work) => Run(() => { work(); return 0; });

    private static void Enter()
    {
        if (_depth == 0 && Staging.StateFailure is { } staged) throw staged();
        if (_depth++ > 0) return;
        // Named for the data directory, so a test's temporary one never waits
        // on the installed app.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(GraftPaths.OwnData).ToUpperInvariant())))[..32];
        var mutex = new Mutex(false, @"Local\ClaudeGraft-state-" + key);
        bool owned;
        try { owned = mutex.WaitOne(Patience); }
        catch (AbandonedMutexException) { owned = true; }
        if (owned) { _held = mutex; return; }
        _depth--;
        Diagnostics.Note("state-lock.busy", null);
        mutex.Dispose();
        throw new StateBusyException();
    }

    private static void Leave()
    {
        if (--_depth > 0) return;
        if (_held is null) return;
        _held.ReleaseMutex();
        _held.Dispose();
        _held = null;
    }
}
