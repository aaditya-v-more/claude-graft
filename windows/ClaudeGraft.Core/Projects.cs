using System.Globalization;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>A project on claude.ai, as its own Projects page lists it.</summary>
public sealed record ClaudeProject
{
    public required string Id { get; init; }
    /// The signed-in account that listed it.
    public required Guid Account { get; init; }
    public required string Name { get; init; }
    public string Summary { get; init; } = "";
    public bool IsPrivate { get; init; } = true;
    public bool IsStarred { get; init; }
    /// The "How to use Claude" project every account starts with.
    public bool IsExample { get; init; }
    public string? Creator { get; init; }
    public DateTimeOffset? Updated { get; init; }

    /// A Beta project, which claude.ai keeps as a channel of the Code service.
    public bool IsBeta { get; init; }

    /// A Beta project opens under Code; at the classic address it will not load.
    public string OpenUrl => $"{ArtifactAPI.Origin}/{(IsBeta ? "code/project" : "project")}/{Uri.EscapeDataString(Id)}";
}

/// <summary>
/// The requests claude.ai's Projects page makes, read off its web client in
/// November 2026. None is documented, so each is kept to exactly what that page
/// sends, and none takes the artifact service's headers.
/// </summary>
public static class ClaudeAPI
{
    /// Thirty is what the Projects page asks for; five pages stops a list that
    /// never ends from being followed.
    public const int ProjectPageSize = 30;
    public const int MaximumProjectPages = 5;

    /// The request the Projects page itself makes. `projects_v2` answered the
    /// same account with an empty list while this one held its project.
    public static string ProjectsPath(string org, int offset = 0) =>
        $"/api/organizations/{Uri.EscapeDataString(org)}/projects?include_harmony_projects=true"
        + $"&limit={ProjectPageSize}&offset={offset}&order_by=latest_chat";

    /// A bare array, as `/projects` answers; a full page may have more after it.
    /// The `{data, pagination}` shape `projects_v2` answers is read too.
    public static (List<ClaudeProject> Projects, bool HasMore)? Projects(string json, Guid account)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
        var rows = root as JsonArray ?? (root as JsonObject)?["data"] as JsonArray;
        if (rows is null) return null;
        var projects = new List<ClaudeProject>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Text(row, "uuid") is not { } id) continue;
            // An archived project is off the Projects page, so it is off here.
            if (row["archived_at"] is not null) continue;
            projects.Add(new ClaudeProject
            {
                Id = id,
                Account = account,
                Name = Text(row, "name") is { Length: > 0 } name ? name : "Untitled",
                Summary = Text(row, "description") ?? "",
                IsPrivate = Flag(row, "is_private") ?? true,
                IsStarred = Flag(row, "is_starred") ?? false,
                IsExample = Flag(row, "is_starter_project") ?? false,
                Creator = row["creator"] is JsonObject creator ? Text(creator, "full_name") : null,
                Updated = Date(Text(row, "updated_at")),
            });
        }
        var hasMore = root is JsonObject o
            ? o["pagination"] is JsonObject p && Flag(p, "has_more") == true
            : rows.Count >= ProjectPageSize;
        return (projects, hasMore);
    }

    /// Beta projects, as claude.ai's Projects page asks for them. A classic
    /// project moved into the Beta is archived on the old list with `moved_to`
    /// naming its channel, so the two lists never hold the same project.
    public const string ChannelsPath = "/v1/code/channels?scope=joined&include_members=true&include_archived=true&limit=200";

    /// Without the beta and the organization the Code service answers 400.
    public static Dictionary<string, string> CodeHeaders(string org) => new()
    {
        ["anthropic-beta"] = "ccr-byoc-2025-07-29", ["anthropic-version"] = "2023-06-01", ["x-organization-uuid"] = org,
    };

    public static List<ClaudeProject>? Channels(string json, Guid account)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
        if ((root as JsonObject)?["data"] is not JsonArray rows) return null;
        var projects = new List<ClaudeProject>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Text(row, "id") is not { } id || row["archived_at"] is not null || row["disabled_at"] is not null) continue;
            projects.Add(new ClaudeProject
            {
                Id = id,
                Account = account,
                Name = Text(row, "name") is { Length: > 0 } name ? name : "Untitled",
                Summary = Text(row, "topic") ?? "",
                IsPrivate = Text(row, "visibility") is not { } visibility || !visibility.Contains("org", StringComparison.OrdinalIgnoreCase),
                IsBeta = true,
                Updated = Date(Text(row, "last_activity_at")) ?? Date(Text(row, "updated_at")),
            });
        }
        return projects;
    }

    /// Every account's projects in one list: starred first, as on claude.ai,
    /// then the most recently changed.
    public static List<ClaudeProject> Merge(IEnumerable<IEnumerable<ClaudeProject>> lists) =>
        lists.SelectMany(l => l)
            .OrderByDescending(p => p.IsStarred)
            .ThenByDescending(p => p.Updated ?? DateTimeOffset.MinValue)
            .ToList();

    internal static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static bool? Flag(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    internal static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
