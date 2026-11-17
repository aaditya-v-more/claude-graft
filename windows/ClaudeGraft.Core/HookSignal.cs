using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// <summary>
/// One event a Claude Code hook reported, as the launcher files it for Graft:
/// a turn that ended, or a permission a turn now waits on.
/// </summary>
public sealed record HookSignal(
    [property: JsonPropertyName("kind")] HookSignal.Kind What,
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("at")] long At,
    [property: JsonPropertyName("transcript")] string? Transcript = null)
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Kind { TurnEnded, AwaitingPermission }

    /// What a hook's input says, or null for an event Graft has no use for. An idle
    /// reminder follows a turn's end that was already reported, so it is not one.
    public static HookSignal? From(string input, long at)
    {
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            string? Field(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Field("session_id") is not { Length: > 0 } session) return null;
            return Field("hook_event_name") switch
            {
                "Stop" => new HookSignal(Kind.TurnEnded, session, at, Field("transcript_path")),
                "Notification" when Field("notification_type") == "permission_prompt" => new HookSignal(Kind.AwaitingPermission, session, at, Field("transcript_path")),
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }

    /// Leaves a hook's event in the signals folder, one file each, for Graft to take.
    public static void File(string input)
    {
        if (From(input, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) is not { } signal) return;
        Directory.CreateDirectory(ChimeHook.SignalsDirectory);
        var name = $"{signal.At}-{Guid.NewGuid():N}.json";
        AtomicWrite.Bytes(Path.Combine(ChimeHook.SignalsDirectory, name), JsonSerializer.SerializeToUtf8Bytes(signal));
    }

    public static HookSignal? Read(string file)
    {
        try { return JsonSerializer.Deserialize<HookSignal>(System.IO.File.ReadAllBytes(file)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
