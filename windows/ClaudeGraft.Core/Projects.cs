using System.Globalization;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public sealed record ClaudeProject(string Id, Guid Account, string Name, string Summary, bool IsPrivate,
    bool IsStarred, bool IsExample, string? Creator, DateTimeOffset? Updated)
{
    public Uri OpenUrl => new("https://claude.ai/project/" + Uri.EscapeDataString(Id));
}

public enum SearchKind { Chat, Code }
public enum SessionPlace { ThisComputer, OtherComputer, Cloud, GoneFromClaude }
public readonly record struct TextMatch(int Start, int End);
public sealed record SearchHit(string Id, Guid Account, SearchKind Kind, string Title, string Snippet,
    TextMatch[] SnippetMatches, TextMatch[] TitleMatches, string? Project, DateTimeOffset? Updated,
    bool IsArchived, bool IsStarred, SessionPlace? Place, string? Folder, string? Transcript, int Rank, Uri? OpenUrl);
public sealed record RemoteCodeSession(string Id, string Title, bool IsArchived, bool InCloud, DateTimeOffset? Updated, string? Repository);
public sealed record LocalCodeSession(string[] BridgeIds, string Owner, string Organization, string Title,
    string Folder, string Transcript, DateTimeOffset LastActive, bool FolderIsHere, bool CarriedOn = false);

public static class ProjectApi
{
    public const int ProjectPageSize = 30, MaximumProjectPages = 5, CodePageSize = 200, MaximumCodePages = 10;
    public static string ProjectsPath(string org, int offset = 0) => "/api/organizations/" + Uri.EscapeDataString(org)
        + "/projects_v2?include_harmony_projects=true&limit=30&offset=" + offset + "&order_by=latest_chat";
    public static string SearchPath(string org) => "/api/organizations/" + Uri.EscapeDataString(org) + "/conversation/search/v2";
    public static JsonObject SearchBody(string query) => new() { ["query"] = query, ["n"] = 25, ["target_snippet_size"] = 100 };
    public static string CodePath(string? cursor = null) => "/v1/code/sessions?limit=200" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
    public static IReadOnlyDictionary<string, string> CodeHeaders(string org) => new Dictionary<string, string> {
        ["anthropic-beta"] = "ccr-byoc-2025-07-29", ["anthropic-version"] = "2023-06-01", ["x-organization-uuid"] = org };
    public static Uri CodeUrl(string id) => new("https://claude.ai/code/" + Uri.EscapeDataString(id.StartsWith("cse_", StringComparison.Ordinal) ? "session_" + id[4..] : id));

    public static (ClaudeProject[] Projects, bool HasMore) ParseProjects(JsonNode? node, Guid account)
    {
        var rows = Rows(node);
        return (rows.Where(row => String(row["uuid"]) is not null && row["archived_at"] is null).Select(row =>
            new ClaudeProject(String(row["uuid"])!, account, Title(row["name"]), String(row["description"]) ?? "",
                Bool(row["is_private"], true), Bool(row["is_starred"]), Bool(row["is_starter_project"]),
                String((row["creator"] as JsonObject)?["full_name"]), Date(row["updated_at"]))).ToArray(),
            Bool((node?["pagination"] as JsonObject)?["has_more"]));
    }

    public static SearchHit[] ParseChats(JsonNode? node, Guid account) => Rows(node).Select((row, index) =>
    {
        if (row["conversation"] is not JsonObject chat || String(chat["uuid"]) is not string id) return null;
        var snippet = row["matched_snippet"] as JsonObject;
        return new SearchHit(id, account, SearchKind.Chat, Title(chat["name"]), String(snippet?["text"]) ?? "",
            Matches(snippet?["matches"]), String(chat["name"]) is { Length: > 0 } ? Matches(row["title_matches"]) : [],
            String(chat["project_uuid"]), Date(chat["updated_at"]), Bool(chat["is_archived"]), Bool(chat["is_starred"]),
            null, null, null, Int(row["semantic_rank"]) ?? index, new Uri("https://claude.ai/chat/" + Uri.EscapeDataString(id)));
    }).OfType<SearchHit>().ToArray();

    public static (RemoteCodeSession[] Sessions, string? Next) ParseCode(JsonNode? node) =>
        (Rows(node).Where(row => String(row["id"]) is not null).Select(row => new RemoteCodeSession(
            String(row["id"])!, Title(row["title"]), String(row["status"]) == "archived", String(row["environment_kind"]) == "anthropic_cloud",
            Date(row["last_event_at"]) ?? Date(row["updated_at"]),
            String((((row["config"] as JsonObject)?["sources"] as JsonArray)?.FirstOrDefault() as JsonObject)?["url"]))).ToArray(),
            String(node?["next_cursor"]) is { Length: > 0 } next ? next : null);

    public static TextMatch[]? CodeMatch(string query, string title, params string?[] other)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var haystack = string.Join('\n', new[] { title }.Concat(other));
        if (words.Length == 0 || !words.All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase))) return null;
        return words.Select(word => (Word: word, Index: title.IndexOf(word, StringComparison.OrdinalIgnoreCase)))
            .Where(match => match.Index >= 0).Select(match => new TextMatch(match.Index, match.Index + match.Word.Length)).ToArray();
    }

    public static SearchHit[] CodeHits(string query, Guid account, string? accountUuid, string? organization,
        IReadOnlyList<RemoteCodeSession> remote, bool complete, DateTimeOffset? listedAt, IReadOnlyList<LocalCodeSession> local)
    {
        var byBridge = new Dictionary<string, LocalCodeSession>(StringComparer.Ordinal);
        foreach (var session in local) foreach (var id in session.BridgeIds) byBridge[id] = session;
        var hits = new List<SearchHit>();
        for (var index = 0; index < remote.Count; index++)
        {
            var session = remote[index]; var here = byBridge.GetValueOrDefault(session.Id);
            if (CodeMatch(query, session.Title, session.Repository, here?.Folder) is not { } matches) continue;
            hits.Add(new(session.Id, account, SearchKind.Code, session.Title, here?.Folder ?? session.Repository ?? "", [], matches,
                null, session.Updated, session.IsArchived, false, session.InCloud ? SessionPlace.Cloud
                    : here?.FolderIsHere == true ? SessionPlace.ThisComputer : SessionPlace.OtherComputer,
                here?.Folder, here?.Transcript, index, CodeUrl(session.Id)));
        }
        if (!complete || accountUuid is null || organization is null || listedAt is null) return hits.ToArray();
        var listed = remote.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var session in local.Where(session => session.Owner == accountUuid && session.Organization == organization
            && session.LastActive < listedAt && !session.CarriedOn && !session.BridgeIds.Any(listed.Contains)))
        {
            if (CodeMatch(query, session.Title, session.Folder) is not { } matches) continue;
            hits.Add(new("local:" + session.Transcript, account, SearchKind.Code, session.Title, session.Folder, [], matches,
                null, session.LastActive, false, false, SessionPlace.GoneFromClaude, session.Folder, session.Transcript, hits.Count, null));
        }
        return hits.ToArray();
    }

    public static bool LocalReadServes(DateTimeOffset read, DateTimeOffset list, DateTimeOffset now, TimeSpan lifetime) => read >= list && now - read < lifetime;
    public static ClaudeProject[] Merge(IEnumerable<IEnumerable<ClaudeProject>> lists) => lists.SelectMany(list => list)
        .OrderByDescending(project => project.IsStarred).ThenByDescending(project => project.Updated).ToArray();
    public static SearchHit[] Interleave(IEnumerable<IEnumerable<SearchHit>> lists)
    {
        var arrays = lists.Select(list => list.ToArray()).ToArray();
        var chats = arrays.Select(list => list.Where(hit => hit.Kind == SearchKind.Chat).OrderBy(hit => hit.Rank).ToArray()).ToArray();
        var result = new List<SearchHit>();
        for (var i = 0; i < chats.Select(list => list.Length).DefaultIfEmpty().Max(); i++)
            foreach (var list in chats) if (i < list.Length) result.Add(list[i]);
        return result.Concat(arrays.SelectMany(list => list).Where(hit => hit.Kind == SearchKind.Code).OrderByDescending(hit => hit.Updated)).ToArray();
    }

    public static TextMatch[] ValidMatches(string text, IEnumerable<TextMatch> matches) => matches.Where(match => match.Start >= 0 && match.End > match.Start
        && match.End <= text.Length && (match.Start == 0 || !char.IsLowSurrogate(text[match.Start]))
        && (match.End == text.Length || !char.IsLowSurrogate(text[match.End]))).ToArray();
    private static IEnumerable<JsonObject> Rows(JsonNode? node) => node is JsonObject root && root["data"] is JsonArray data
        && data.All(row => row is JsonObject) ? data.Cast<JsonObject>() : throw new InvalidDataException("Unreadable claude.ai response.");
    private static string Title(JsonNode? node) => String(node) is { Length: > 0 } title ? title : "Untitled";
    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int? Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    private static bool Bool(JsonNode? node, bool fallback = false) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
    private static DateTimeOffset? Date(JsonNode? node) => DateTimeOffset.TryParse(String(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    private static TextMatch[] Matches(JsonNode? node) => (node as JsonArray)?.OfType<JsonObject>()
        .Select(match => (Start: Int(match["start"]), End: Int(match["end"]))).Where(match => match.Start is not null && match.End > match.Start)
        .Select(match => new TextMatch(match.Start!.Value, match.End!.Value)).ToArray() ?? [];
}

public enum SearchKindFilter { All, Chats, Code }
public enum SearchStatus { Active, Archived, Any }
public enum SearchAge { Any, Day, Week, Month, Year }
public sealed record SearchFilters
{
    public SearchKindFilter Kind { get; init; }
    public SearchStatus Status { get; init; }
    public SessionPlace? Place { get; init; }
    public SearchAge Age { get; init; }
    public Guid? Account { get; init; }
    public string? Project { get; init; }
    public bool NoProject { get; init; }
    public bool StarredOnly { get; init; }
    public bool IsNarrowed => this != new SearchFilters();
    public bool Admits(SearchHit hit, DateTimeOffset now)
    {
        if (Kind == SearchKindFilter.Chats && hit.Kind != SearchKind.Chat || Kind == SearchKindFilter.Code && hit.Kind != SearchKind.Code) return false;
        if (Status == SearchStatus.Active && hit.IsArchived || Status == SearchStatus.Archived && !hit.IsArchived) return false;
        if (Place is not null && hit.Place != Place || Account is not null && hit.Account != Account) return false;
        var days = Age switch { SearchAge.Day => 1, SearchAge.Week => 7, SearchAge.Month => 31, SearchAge.Year => 366, _ => 0 };
        if (days > 0 && (hit.Updated is null || now - hit.Updated > TimeSpan.FromDays(days))) return false;
        if (NoProject && (hit.Kind != SearchKind.Chat || hit.Project is not null) || Project is not null && hit.Project != Project) return false;
        return !StarredOnly || hit.IsStarred;
    }
}
