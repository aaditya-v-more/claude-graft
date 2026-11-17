using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// <summary>
/// What a sweep remembers between passes: where every record it has seen was
/// sitting, so one gone from a store this pass did read is known to have been
/// deleted by hand rather than merely out of sight; every session withdrawn
/// that way, so none is ever brought back; and the facts behind every record it
/// filed itself, so a record still holding those exact bytes is known to be one
/// of ours to keep current.
/// </summary>
public sealed class SessionRecordState
{
    [JsonPropertyName("records")]
    public Dictionary<string, string> Records { get; set; } = new();

    [JsonPropertyName("withdrawn")]
    public List<string> Withdrawn { get; set; } = new();

    [JsonPropertyName("authored")]
    public Dictionary<string, SessionFacts> Authored { get; set; } = new();

    /// Records missing on the last pass and not yet missing on a second.
    [JsonPropertyName("vanished")]
    public List<string> Vanished { get; set; } = new();

    /// Every fork seen while both its records stood, against the session it came
    /// from. Remembered because the pass that needs it is one where neither
    /// record stands any more.
    [JsonPropertyName("forks")]
    public Dictionary<string, string> Forks { get; set; } = new();

    // Every key is optional on the way in — System.Text.Json fills a missing
    // member with the initializer above rather than failing — so a state file
    // from a version before one of these existed loads with the rest, claiming
    // nothing it does not say. This is the JsonSerializer default, which is why
    // no custom reader is needed the way the Swift decoder needs one.

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static SessionRecordState Load(string path)
    {
        var state = StateFile.Read<SessionRecordState>(path, Options);
        // An explicit null parses, and would forget every withdrawal just as surely.
        if (state.Records is null || state.Withdrawn is null || state.Authored is null || state.Vanished is null)
            throw new UnreadableStateException(path, null);
        state.Forks ??= new();
        return state;
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = JsonSerializer.SerializeToUtf8Bytes(this, Options);
            AtomicWrite.Bytes(path, data);
        }
        catch { }
    }
}

/// <summary>
/// Write-to-a-temp-then-rename, the way Claude and the Mac build both write
/// their JSON, so a reader never sees a file half-written.
/// </summary>
public static class AtomicWrite
{
    public static void Bytes(string path, byte[] data)
    {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tmp, data);
        // File.Move with overwrite is the atomic replace on the same volume.
        File.Move(tmp, path, overwrite: true);
    }
}
