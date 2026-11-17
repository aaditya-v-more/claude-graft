using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// Graft's side of the relay: asking it, and making sure the one answering is
/// this build's. The relay outlives Graft, so it is started, never owned.
/// </summary>
public static class RelayKeeper
{
    public const string ExeName = "GraftRelay.exe";

    /// Blocking, so never on the UI thread. A relay that does not answer inside
    /// a few seconds is treated as absent rather than waited on.
    public static RelayStatus? Send(RelayRequest command, string? pipe = null, int timeoutMs = 4000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe ?? UsagePool.PipeName, PipeDirection.InOut,
                PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var line = JsonSerializer.Serialize(command, RelayWire.Json) + "\n";
            client.Write(Encoding.UTF8.GetBytes(line));
            client.Flush();
            using var reader = new StreamReader(client, Encoding.UTF8);
            var read = reader.ReadLineAsync();
            if (!read.Wait(timeoutMs) || read.Result is not string reply) return null;
            return JsonSerializer.Deserialize<RelayStatus>(reply, RelayWire.Json);
        }
        catch (Exception e) when (e is IOException or TimeoutException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static string Digest(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException) { return ""; }
    }

    /// The copy a version runs from, beside its source's digest so two builds
    /// stamped with one version (every development build) never share a folder.
    public static string CopyFor(string source)
    {
        var digest = Digest(source);
        return Path.Combine(UsagePool.RelayHome, digest.Length >= 16 ? digest[..16] : "unknown", ExeName);
    }

    public enum Step { Running, Replaced, Started, Failed }

    /// Makes sure this build's relay answers: a relay already running from the
    /// same binary is left alone, any other is asked to step aside first.
    public static Step EnsureRunning(string source)
    {
        if (!File.Exists(source)) return Step.Failed;
        var copy = CopyFor(source);
        var digest = Digest(source);
        var status = Send(new RelayRequest());
        if (status is not null && status.Binary == digest) return Step.Running;

        var replaced = false;
        if (status is not null)
        {
            replaced = Send(new RelayRequest { Quit = true }) is not null;
            Diagnostics.Note("relay.replacing", new Dictionary<string, object?> { ["was"] = status.Binary[..Math.Min(12, status.Binary.Length)] });
        }
        try
        {
            if (!File.Exists(copy) || Digest(copy) != digest)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                var temp = copy + ".new";
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, copy, overwrite: true);
            }
            Process.Start(new ProcessStartInfo(copy, "--serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(copy)!,
            })?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Diagnostics.Note("relay.start-failed", new Dictionary<string, object?> { ["error"] = e.Message });
            return Step.Failed;
        }
        Prune(Path.GetDirectoryName(copy)!);
        return replaced ? Step.Replaced : Step.Started;
    }

    /// Older copies go once nothing runs them; one still in use refuses the
    /// delete, and is tried again next time.
    public static void Prune(string keep)
    {
        if (!Directory.Exists(UsagePool.RelayHome)) return;
        foreach (var dir in Directory.EnumerateDirectories(UsagePool.RelayHome))
        {
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
