using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

public static class PoolControl
{
    public static string PipeName => "ClaudeGraft.Pool." + UsagePool.Digest(Path.GetFullPath(GraftPaths.OwnData).ToUpperInvariant() + "|" + UserId())[..32];
    private static string UserId() => OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Windows account unavailable.") : Environment.UserName;
    public static async Task<PoolStatus> SendAsync(PoolCommand command, CancellationToken cancellation = default, int timeoutMilliseconds = 4000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(timeoutMilliseconds);
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(timeout.Token);
        if (OperatingSystem.IsWindows() && !SameServerUser(pipe)) throw new UnauthorizedAccessException("The usage relay belongs to a different Windows account.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command, UsagePool.Json);
        await pipe.WriteAsync(bytes, timeout.Token); await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
        var response = await ReadLineAsync(pipe, timeout.Token);
        return JsonSerializer.Deserialize<PoolStatus>(response, UsagePool.Json) ?? throw new IOException("The usage relay did not return a status.");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint server);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [SupportedOSPlatform("windows")]
    private static bool SameServerUser(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return false;
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, 8, out var token)) return false;
            try { using var server = new WindowsIdentity(token); using var own = WindowsIdentity.GetCurrent(); return server.User is not null && server.User == own.User; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
    public static async Task<bool> AnswersAsync(int timeoutMilliseconds = 1200)
    {
        try { await SendAsync(new(), timeoutMilliseconds: timeoutMilliseconds); return true; }
        catch (JsonException) { return true; } // An older relay still owns the port; ask it to step aside before replacement.
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    public static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellation)
    {
        using var output = new MemoryStream(); var bytes = new byte[8192];
        while (output.Length <= 8 * 1024 * 1024)
        {
            var count = await stream.ReadAsync(bytes, cancellation); if (count == 0) throw new EndOfStreamException();
            var newline = Array.IndexOf(bytes, (byte)10, 0, count);
            output.Write(bytes, 0, newline < 0 ? count : newline);
            if (newline >= 0) return output.ToArray();
        }
        throw new IOException("Relay control message too large.");
    }
    public static async Task<bool> EnsureRelayAsync(string? executable)
    {
        if (await AnswersAsync()) return true;
        if (executable is null || !File.Exists(executable)) return false;
        // Only our private stable bundle may be started from a recovery journal.
        if (!Fs.IsInside(executable, Path.Combine(GraftPaths.OwnData, "relay")) || Path.GetFileName(executable) != "GraftRelay.exe") return false;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            if (GraftPaths.ProfilesRootOverride is not null) { start.ArgumentList.Add("--test-data-root"); start.ArgumentList.Add(GraftPaths.ProfilesRootOverride); }
            using var process = Process.Start(start);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
            while (DateTimeOffset.UtcNow < deadline) { if (await AnswersAsync(250)) return true; await Task.Delay(100); }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
        return false;
    }

    public static string BundleDigest(string directory)
    {
        var files = new[] { "GraftRelay.exe", "GraftRelay.dll", "ClaudeGraft.Core.dll" };
        return UsagePool.Digest(string.Join("\n", files.Select(file => file + ":" + UsagePool.Digest(File.ReadAllBytes(Path.Combine(directory, file))))));
    }
    public static string StageRelay(string sourceDirectory)
    {
        var digest = BundleDigest(sourceDirectory);
        var root = Path.Combine(GraftPaths.OwnData, "relay"); PoolStorage.EnsurePrivateDirectory(root);
        var target = Path.Combine(root, digest);
        if (!Directory.Exists(target))
        {
            var temporary = Path.Combine(root, ".new-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(temporary, Path.GetRelativePath(sourceDirectory, source));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
                }
                if (BundleDigest(temporary) != digest) throw new IOException("The usage relay changed while being prepared.");
                Directory.Move(temporary, target);
            }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }
        if (BundleDigest(target) != digest) throw new IOException("The saved usage relay does not match this installation.");
        return Path.Combine(target, "GraftRelay.exe");
    }

    public static Task<string[]> StopSharingAsync() => Task.Run(() =>
    {
        using var stateLock = StateLock.Acquire();
        // Persist the disable first so a still-open manager observes it on its next poll.
        Exception? recoveryError = null;
        string[] left;
        PoolSettings? settings = null;
        try
        {
            settings = PoolSettings.Load(); settings.DisableAndSave();
            left = PoolEnvironment.TakeOutLines();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { recoveryError = e; left = [PoolEnvironment.JournalFile]; }
        var acknowledged = false;
        try
        {
            var command = new PoolCommand(new(false, []), Quit: left.Length == 0);
            var status = SendAsync(command).GetAwaiter().GetResult(); acknowledged = !status.Enabled;
            if (settings?.Acknowledge(command, status) == true) settings.Save();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { }
        if (recoveryError is not null) throw new IOException(acknowledged
            ? "Lending has been stopped. A profile environment or its recovery journal could not be read, so the relay was kept available. Open Shared Usage in Graft to resolve it."
            : "Sharing is disabled in the saved settings, but the relay did not acknowledge it, so lending could not be confirmed stopped. Its recovery journal or a profile environment also needs attention. Open Shared Usage in Graft to resolve it.", recoveryError);
        return left;
    });
}
