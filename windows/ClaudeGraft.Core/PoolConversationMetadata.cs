using System.Text.Json;

namespace ClaudeGraft.Core;

public static class PoolConversationMetadata
{
    public sealed record Reading(string? Title, bool Hidden, DateTimeOffset? FocusedAt);
    private static readonly Dictionary<string, string> known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object gate = new();
    public static Reading Read(string profile, string session)
    {
        lock (gate)
        {
            var key = Path.GetFullPath(profile) + "|" + session;
            var account = Graft.Account(profile);
            if (account is null or "." or ".." || account.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return new(null, false, null);
            var root = Path.Combine(profile, "claude-code-sessions", account);
            try
            {
                var directories = Directory.GetDirectories(root).Where(p => !Path.GetFileName(p).StartsWith('.') && !p.EndsWith(Graft.StashSuffix)).ToArray();
                foreach (var directory in directories)
                foreach (var record in Directory.EnumerateFiles(directory, "local_*.json"))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(record)); var data = doc.RootElement;
                    if (!data.TryGetProperty("cliSessionId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != session) continue;
                    known[key] = record;
                    var hidden = data.TryGetProperty("isArchived", out var archived) && archived.ValueKind == JsonValueKind.True
                        || File.Exists(Path.Combine(directory, "deleted_" + Path.GetFileNameWithoutExtension(record)));
                    var title = data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    DateTimeOffset? focused = data.TryGetProperty("lastFocusedAt", out var f) && f.TryGetDouble(out var stamp) && double.IsFinite(stamp)
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)stamp) : null;
                    return new(title, hidden, focused);
                }
                return new(null, known.TryGetValue(key, out var prior) && directories.Contains(Path.GetDirectoryName(prior), StringComparer.OrdinalIgnoreCase), null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { return new(null, false, null); }
        }
    }
}
