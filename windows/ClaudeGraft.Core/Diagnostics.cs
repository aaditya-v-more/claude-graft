using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// Every pass writes down what it saw, because every rule in this app turns on
/// telling two indistinguishable situations apart and the symptom of getting
/// one wrong arrives hours later naming none of it. One JSON line per event,
/// appended.
///
/// Opened for append per write rather than held, because the app and any number
/// of launchers write to it at once and every one of them is a short-lived
/// process that may be killed mid-pass. The launchers are the reason it exists
/// rather than a print: a launcher runs the sweep in front of a window opening,
/// with nobody watching a terminal, and has exited by the time anyone asks.
/// </summary>
public static class Diagnostics
{
    /// Set by the test suite for its whole run: most tests leave the profiles
    /// root alone, and every pass they drive would note itself into the real one.
    public static string? FolderOverride { get; set; }

    private static string Folder =>
        GraftPaths.ProfilesRootOverride is null && FolderOverride is { } folder ? folder : GraftPaths.OwnData;

    public static string LogFile => Path.Combine(Folder, "diagnostics.log");

    /// What is true now, beside the log of what happened, so a copy of one is a copy of the other.
    public static string ReportFile => Path.Combine(Folder, "state-report.txt");

    /// Which process wrote it: a launcher and the app run the same pass over the same stores.
    public static string Who => Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "unknown";

    public static void Note(string @event, IReadOnlyDictionary<string, object?>? fields = null)
    {
        try
        {
            var line = new Dictionary<string, object?>
            {
                ["at"] = DateTimeOffset.UtcNow.ToString("o"),
                ["event"] = @event,
            };
            if (fields is not null)
                foreach (var (k, v) in fields) line[k] = v;

            var json = JsonSerializer.Serialize(line);
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            // FileMode.Append opens O_APPEND; each write is its own open/close so
            // a process killed between passes never truncates another's line.
            using var stream = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }
        catch { /* diagnostics must never be the thing that fails a pass */ }
    }
}
