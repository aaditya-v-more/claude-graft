using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>
/// One thing a search found: a chat claude.ai's search matched, or a Code
/// session whose title or folder did.
/// </summary>
public sealed record SearchHit
{
    public enum Kinds { Chat, Code }

    /// Where a Code session ran, which says whether it can be picked up again
    /// here. A chat is always on claude.ai and has none.
    public enum Places { ThisPC, OtherPC, Cloud, GoneFromClaude }

    public required string Id { get; init; }
    public required Guid Account { get; init; }
    public required Kinds Kind { get; init; }
    public required string Title { get; init; }
    public string Snippet { get; init; } = "";
    /// Where the query sits in Snippet and Title, in UTF-16 units, which is how
    /// both the page's script and .NET count them.
    public IReadOnlyList<(int Start, int End)> SnippetMatches { get; init; } = [];
    public IReadOnlyList<(int Start, int End)> TitleMatches { get; init; } = [];
    public string? Project { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public bool IsArchived { get; init; }
    public bool IsStarred { get; init; }
    public Places? Place { get; init; }
    public string? Folder { get; init; }
    public string? Transcript { get; init; }
    /// The order claude.ai ranked it in, within its own account.
    public int Rank { get; init; }
    /// Null for a session claude.ai no longer has, which is shown in Explorer.
    public string? OpenUrl { get; init; }
}

/// <summary>A Code session as claude.ai's own list has it.</summary>
public sealed record RemoteCodeSession(string Id, string Title, bool IsArchived, bool InCloud, DateTimeOffset? Updated, string? Repository);

/// <summary>What this PC's transcripts say about a Code session.</summary>
public sealed record LocalCodeSession
{
    public required IReadOnlyList<string> BridgeIds { get; init; }
    public required string Owner { get; init; }
    /// An account in two organizations has a session list in each, and only
    /// one of them is read.
    public required string Organization { get; init; }
    public required string Title { get; init; }
    public required string Folder { get; init; }
    public required string Transcript { get; init; }
    public required DateTimeOffset LastActive { get; init; }
    public required bool FolderIsHere { get; init; }
    /// A record names it among the sessions its conversation grew out of; the
    /// conversation goes on under a later session, so its not being listed
    /// says nothing about it.
    public bool CarriedOn { get; init; }
}

/// <summary>
/// The requests claude.ai's search box makes, read off its web client in
/// November 2026. None is documented, so each is kept to what that box sends.
/// </summary>
public static class SearchAPI
{
    public static string SearchPath(string org) => $"/api/organizations/{Uri.EscapeDataString(org)}/conversation/search/v2";

    /// Twenty-five results with a hundred characters around each match is what
    /// the box asks for. Archived chats come back with the rest, marked.
    public const int SearchLimit = 25;

    public static string SearchBody(string query) =>
        JsonSerializer.Serialize(new SortedDictionary<string, object> { ["n"] = SearchLimit, ["query"] = query, ["target_snippet_size"] = 100 });

    /// Two hundred a page is what the Code tab asks; ten pages is four times
    /// what a heavy account holds.
    public const int CodePageSize = 200;
    public const int MaximumCodePages = 10;

    public static string CodeSessionsPath(string? cursor = null) =>
        $"/v1/code/sessions?limit={CodePageSize}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));

    /// Listed as `cse_…` and opened as `session_…`, the same name after the prefix.
    public static string CodeSessionUrl(string id) =>
        $"{ArtifactAPI.Origin}/code/{Uri.EscapeDataString(id.StartsWith("cse_", StringComparison.Ordinal) ? "session_" + id[4..] : id)}";

    public static List<SearchHit>? Chats(string json, Guid account)
    {
        if (Parse(json) is not JsonObject o || o["data"] is not JsonArray rows) return null;
        var hits = new List<SearchHit>();
        var index = 0;
        foreach (var row in rows)
        {
            var position = index++;
            if (row is not JsonObject r || r["conversation"] is not JsonObject c || ClaudeAPI.Text(c, "uuid") is not { } id) continue;
            var snippet = r["matched_snippet"] as JsonObject;
            var title = ClaudeAPI.Text(c, "name") is { Length: > 0 } name ? name : null;
            hits.Add(new SearchHit
            {
                Id = id, Account = account, Kind = SearchHit.Kinds.Chat,
                Title = title ?? "Untitled",
                Snippet = snippet is null ? "" : ClaudeAPI.Text(snippet, "text") ?? "",
                SnippetMatches = Ranges(snippet?["matches"]),
                TitleMatches = title is null ? [] : Ranges(r["title_matches"]),
                Project = ClaudeAPI.Text(c, "project_uuid"),
                Updated = ClaudeAPI.Date(ClaudeAPI.Text(c, "updated_at")),
                IsArchived = ClaudeAPI.Flag(c, "is_archived") ?? false,
                IsStarred = ClaudeAPI.Flag(c, "is_starred") ?? false,
                Rank = r["semantic_rank"] is JsonValue v && v.TryGetValue<double>(out var rank) ? (int)rank : position,
                OpenUrl = $"{ArtifactAPI.Origin}/chat/{Uri.EscapeDataString(id)}",
            });
        }
        return hits;
    }

    public static (List<RemoteCodeSession> Sessions, string? Next)? CodeSessions(string json)
    {
        if (Parse(json) is not JsonObject o || o["data"] is not JsonArray rows) return null;
        var sessions = new List<RemoteCodeSession>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (ClaudeAPI.Text(row, "id") is not { } id) continue;
            var source = ((row["config"] as JsonObject)?["sources"] as JsonArray)?.FirstOrDefault() as JsonObject;
            sessions.Add(new RemoteCodeSession(id,
                ClaudeAPI.Text(row, "title") is { Length: > 0 } title ? title : "Untitled",
                ClaudeAPI.Text(row, "status") == "archived",
                ClaudeAPI.Text(row, "environment_kind") == "anthropic_cloud",
                ClaudeAPI.Date(ClaudeAPI.Text(row, "last_event_at")) ?? ClaudeAPI.Date(ClaudeAPI.Text(row, "updated_at")),
                source is null ? null : ClaudeAPI.Text(source, "url")));
        }
        var next = ClaudeAPI.Text(o, "next_cursor") is { Length: > 0 } cursor ? cursor : null;
        return (sessions, next);
    }

    /// Every word of the query, in any order, somewhere in what is searched, as
    /// the Code tab matches; the title's own matches are picked out.
    public static List<(int Start, int End)>? CodeMatch(string query, string title, params string?[] also)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var lowered = title.ToLowerInvariant();
        var haystack = string.Join("\n", also.OfType<string>().Select(s => s.ToLowerInvariant()).Prepend(lowered));
        if (!words.All(w => haystack.Contains(w, StringComparison.Ordinal))) return null;
        return words.Select(w => lowered.IndexOf(w, StringComparison.Ordinal))
            .Zip(words, (at, w) => (at, w)).Where(p => p.at >= 0).Select(p => (p.at, p.at + p.w.Length)).ToList();
    }

    /// One account's Code sessions that match, each placed by what this PC's
    /// transcripts say, and the transcripts claude.ai no longer has. Gone is
    /// claimed only against a whole list, for its own organization, of a
    /// transcript last touched before the list was read, and never for an
    /// earlier half of a conversation still going.
    public static List<SearchHit> CodeHits(string query, Guid account, string? accountUuid, string? organization,
        IReadOnlyList<RemoteCodeSession> remote, bool complete, DateTimeOffset? listedAt, IReadOnlyList<LocalCodeSession> local)
    {
        var byBridge = new Dictionary<string, LocalCodeSession>();
        foreach (var session in local)
            foreach (var id in session.BridgeIds) byBridge[id] = session;
        var hits = new List<SearchHit>();
        for (var index = 0; index < remote.Count; index++)
        {
            var session = remote[index];
            var here = byBridge.GetValueOrDefault(session.Id);
            if (CodeMatch(query, session.Title, session.Repository, here?.Folder) is not { } matches) continue;
            var place = session.InCloud ? SearchHit.Places.Cloud
                : here is { FolderIsHere: true } ? SearchHit.Places.ThisPC : SearchHit.Places.OtherPC;
            hits.Add(new SearchHit
            {
                Id = session.Id, Account = account, Kind = SearchHit.Kinds.Code, Title = session.Title,
                Snippet = here?.Folder ?? session.Repository ?? "", TitleMatches = matches,
                Updated = session.Updated, IsArchived = session.IsArchived, Place = place,
                Folder = here?.Folder, Transcript = here?.Transcript, Rank = index, OpenUrl = CodeSessionUrl(session.Id),
            });
        }
        if (!complete || accountUuid is null || organization is null || listedAt is not { } listed) return hits;
        var ids = remote.Select(r => r.Id).ToHashSet();
        foreach (var session in local)
        {
            if (session.Owner != accountUuid || session.Organization != organization || session.LastActive >= listed
                || session.CarriedOn || session.BridgeIds.Any(ids.Contains)) continue;
            if (CodeMatch(query, session.Title, session.Folder) is not { } matches) continue;
            hits.Add(new SearchHit
            {
                Id = "local:" + session.Transcript, Account = account, Kind = SearchHit.Kinds.Code, Title = session.Title,
                Snippet = session.Folder, TitleMatches = matches, Updated = session.LastActive,
                Place = SearchHit.Places.GoneFromClaude, Folder = session.Folder, Transcript = session.Transcript, Rank = hits.Count,
            });
        }
        return hits;
    }

    /// A read of this PC's transcripts can stand beside a session list only if
    /// it is fresh and no older than the list: one made earlier misses whatever
    /// began here in between, which the list holds and would place elsewhere.
    public static bool LocalReadServes(DateTimeOffset read, DateTimeOffset list, DateTimeOffset now, TimeSpan lifetime) =>
        read >= list && now - read < lifetime;

    /// Each account ranks its own chats, and two accounts' ranks mean nothing
    /// against each other, so the lists are dealt in turn. Code sessions have no
    /// rank but their age, and follow, newest first.
    public static List<SearchHit> Interleave(IEnumerable<IEnumerable<SearchHit>> lists)
    {
        var all = lists.Select(l => l.ToList()).ToList();
        var chats = all.Select(l => l.Where(h => h.Kind == SearchHit.Kinds.Chat).OrderBy(h => h.Rank).ToList()).ToList();
        var merged = new List<SearchHit>();
        for (var position = 0; position < chats.Select(c => c.Count).DefaultIfEmpty(0).Max(); position++)
            foreach (var list in chats.Where(c => position < c.Count)) merged.Add(list[position]);
        merged.AddRange(all.SelectMany(l => l).Where(h => h.Kind == SearchHit.Kinds.Code).OrderByDescending(h => h.Updated ?? DateTimeOffset.MinValue));
        return merged;
    }

    /// Ranges that do not fit the text are dropped rather than trusted.
    public static List<(int Start, int End)> Fitting(string text, IEnumerable<(int Start, int End)> ranges) =>
        ranges.Where(r => r.Start >= 0 && r.End <= text.Length && r.Start < r.End).OrderBy(r => r.Start).ToList();

    private static List<(int, int)> Ranges(JsonNode? value) =>
        (value as JsonArray ?? []).OfType<JsonObject>()
            .Select(m => (m["start"] is JsonValue s && s.TryGetValue<int>(out var a) ? a : -1, m["end"] is JsonValue e && e.TryGetValue<int>(out var b) ? b : -1))
            .Where(r => r.Item1 >= 0 && r.Item1 < r.Item2).ToList();

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// What a person narrows a search to. Every filter is applied to what came
/// back, so changing one asks claude.ai nothing.
/// </summary>
public sealed record SearchFilters
{
    public enum Kinds { All, Chats, Code }
    public enum Statuses { Active, Archived, Any }
    public enum Places { Anywhere, ThisPC, OtherPC, Cloud, GoneFromClaude }
    public enum Ages { Any, Day, Week, Month, Year }

    public Kinds Kind { get; init; } = Kinds.All;
    /// Active only, as claude.ai's own box starts.
    public Statuses Status { get; init; } = Statuses.Active;
    public Places Place { get; init; } = Places.Anywhere;
    public Ages Age { get; init; } = Ages.Any;
    public Guid? Account { get; init; }
    /// Null for any project, "" for chats in none, otherwise a project's id.
    public string? Project { get; init; }
    public bool StarredOnly { get; init; }

    public bool IsNarrowed => this != new SearchFilters();

    private static TimeSpan? Span(Ages age) => age switch
    {
        Ages.Day => TimeSpan.FromDays(1), Ages.Week => TimeSpan.FromDays(7),
        Ages.Month => TimeSpan.FromDays(31), Ages.Year => TimeSpan.FromDays(366), _ => null,
    };

    /// A place, a project or a star is something only one kind has, so asking
    /// for one leaves the other kind out rather than letting it through.
    public bool Admits(SearchHit hit, DateTimeOffset now)
    {
        if (Kind == Kinds.Chats && hit.Kind != SearchHit.Kinds.Chat) return false;
        if (Kind == Kinds.Code && hit.Kind != SearchHit.Kinds.Code) return false;
        if (Status == Statuses.Active && hit.IsArchived) return false;
        if (Status == Statuses.Archived && !hit.IsArchived) return false;
        if (Place != Places.Anywhere && hit.Place != (SearchHit.Places)(Place - 1)) return false;
        if (Span(Age) is { } span && (hit.Updated is not { } updated || now - updated > span)) return false;
        if (Account is { } account && hit.Account != account) return false;
        if (Project == "" && (hit.Kind != SearchHit.Kinds.Chat || hit.Project is not null)) return false;
        if (Project is { Length: > 0 } project && hit.Project != project) return false;
        if (StarredOnly && !hit.IsStarred) return false;
        return true;
    }
}

public static partial class Graft
{
    /// This PC's Code transcripts, by the ids claude.ai lists their sessions
    /// under. What the sweep parsed comes out of its cache; one it has not
    /// reached is parsed here and never added, so a search writes nothing the
    /// sweep relies on.
    public static List<LocalCodeSession> LocalCodeSessions()
    {
        Dictionary<string, CachedTranscript> cached;
        try { cached = JsonSerializer.Deserialize<Dictionary<string, CachedTranscript>>(File.ReadAllBytes(TranscriptCacheFile)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { cached = new(); }
        var carriedOn = SessionStoreContents().Superseded;
        var sessions = new List<LocalCodeSession>();
        if (!Directory.Exists(GraftPaths.ClaudeProjects)) return sessions;
        foreach (var folder in Directory.EnumerateDirectories(GraftPaths.ClaudeProjects))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.jsonl"))
            {
                var cli = Path.GetFileNameWithoutExtension(path);
                SessionFacts? facts;
                try
                {
                    var info = new FileInfo(path);
                    var stamp = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0;
                    facts = cached.TryGetValue(path, out var entry) && entry.Modified == stamp && entry.Size == info.Length
                        ? entry.Facts
                        : TranscriptParser.SessionFacts(path, cli);
                }
                catch (IOException) { continue; }
                if (facts is null) continue;
                sessions.Add(new LocalCodeSession
                {
                    BridgeIds = facts.BridgeIds, Owner = facts.OwnerAccount, Organization = facts.OwnerOrganization,
                    Title = facts.Title, Folder = facts.Cwd, Transcript = path,
                    LastActive = DateTimeOffset.FromUnixTimeMilliseconds((long)facts.LastActivityAt),
                    FolderIsHere = SessionFolderIsHere(facts.Cwd), CarriedOn = carriedOn.Contains(cli),
                });
            }
        }
        return sessions;
    }
}
