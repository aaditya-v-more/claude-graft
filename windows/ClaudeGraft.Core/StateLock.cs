using System.Security.Cryptography;
using System.Text;

namespace ClaudeGraft.Core;

public sealed class StateLock : IDisposable
{
    private readonly Mutex _mutex;
    private StateLock()
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(GraftPaths.OwnData).ToUpperInvariant())));
        _mutex = new Mutex(false, @"Local\ClaudeGraft-" + key);
        try
        {
            if (!_mutex.WaitOne(TimeSpan.FromSeconds(30)))
                throw new IOException("Another Graft operation is still running. Try again shortly.");
        }
        catch (AbandonedMutexException) { }
        catch { _mutex.Dispose(); throw; }
    }

    public static StateLock Acquire() => new();
    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
