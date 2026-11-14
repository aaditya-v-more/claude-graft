using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

public static class LocalCodeSessions
{
    private sealed class CacheEntry
    {
        [JsonPropertyName("modified")] public double Modified { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("facts")] public SessionFacts? Facts { get; set; }
        [JsonPropertyName("reading")] public int? Reading { get; set; }
    }

    public static LocalCodeSession[] Read()
    {
        Dictionary<string, CacheEntry> cached;
        try { cached = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllBytes(Path.Combine(GraftPaths.OwnData, "transcript-cache.json"))) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { cached = []; }
        var carriedOn = Graft.SessionStoreContents().Superseded;
        var sessions = new List<LocalCodeSession>();
        foreach (var directory in Directories(GraftPaths.ClaudeProjects))
        foreach (var path in Files(directory))
        {
            try
            {
                var info = new FileInfo(path);
                var id = Path.GetFileNameWithoutExtension(path);
                var modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0;
                // A search never populates the sweep's cache; the sweep owns its
                // lifetime and may be writing a newer entry at the same time.
                var facts = cached.TryGetValue(path, out var entry) && entry.Modified == modified && entry.Size == info.Length
                    && entry.Reading == Graft.TranscriptReadingVersion ? entry.Facts : TranscriptParser.Read(path, id).Facts;
                if (facts is null) continue;
                sessions.Add(new(facts.BridgeIds.ToArray(), facts.OwnerAccount, facts.OwnerOrganization, facts.Title, facts.Cwd, path,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)facts.LastActivityAt), Graft.SessionFolderIsHere(facts.Cwd), carriedOn.Contains(id)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { }
        }
        return sessions.ToArray();
    }

    private static string[] Directories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
    private static string[] Files(string root)
    {
        try { return Directory.GetFiles(root, "*.jsonl"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
