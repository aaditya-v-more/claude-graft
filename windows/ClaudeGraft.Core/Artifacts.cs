using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public enum ArtifactRole { Reader, Commenter, Editor }
public enum ArtifactAccess { OnlyYou, Invited, Organization, AnyoneWithLink }
public enum ArtifactExportFormat { Markdown, Pdf, Word, Source }

public sealed record Artifact
{
    public required string Id { get; init; }
    public required Guid Account { get; init; }
    public required string Organization { get; init; }
    public required string Title { get; init; }
    public string Kind { get; init; } = "page";
    public Uri? OpenUrl { get; init; }
    public string Audience { get; init; } = "owner";
    public string Relation { get; init; } = "mine";
    public DateTimeOffset? SortTime { get; init; }
    public bool Renamable { get; init; }
    public bool Duplicable { get; init; }
    public bool DuplicateAvailable { get; init; } = true;
    public bool IsOwn => Relation == "mine";
    public ArtifactAccess Access => ArtifactApi.Access(Audience);
}

public sealed class ArtifactGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<string> Emails { get; set; } = new();
    public Dictionary<string, ArtifactRole> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ArtifactRole RoleOf(string email) => Roles.GetValueOrDefault(email, ArtifactRole.Reader);
    public ArtifactRole? CommonRole => Emails.Count == 0 ? ArtifactRole.Reader
        : Emails.Select(RoleOf).Distinct().Take(2).ToArray() is [var role] ? role : null;
    public void SetRoleForEveryone(ArtifactRole role) => Roles = role == ArtifactRole.Reader ? new()
        : Emails.ToDictionary(e => e, _ => role, StringComparer.OrdinalIgnoreCase);
}

public sealed record ArtifactPermissions(string? Version, string[] Invited, bool Commenters, bool Editors, string? Mode);
public sealed record ArtifactSource(string Version, string Token, string[] Files);

public static class ArtifactApi
{
    public const int MaximumPages = 5;
    public const int MaximumExportBytes = 200 * 1024 * 1024;
    private static string Escape(string text) => Uri.EscapeDataString(text);
    public static string ListPath(string relation, string org, string? after = null) =>
        $"/api/frame/artifacts?rel={Escape(relation)}&empty=exclude&limit=48&org={Escape(org)}&sort=last_viewed"
        + (after is null ? "" : "&page_token=" + Escape(after));
    public static string Path(string id, string org, string? action = null) =>
        "/api/frame/" + (action is null ? "" : action + "/") + Escape(id) + "?org=" + Escape(org);

    public static ArtifactAccess Access(string? audience) => audience switch
    { "public" or "link" => ArtifactAccess.AnyoneWithLink, "org" or "space" => ArtifactAccess.Organization,
        "users" => ArtifactAccess.Invited, _ => ArtifactAccess.OnlyYou };
    public static string AccessLabel(ArtifactAccess access) => access switch
    { ArtifactAccess.AnyoneWithLink => "Anyone with the link", ArtifactAccess.Organization => "Your organization",
        ArtifactAccess.Invited => "Only people invited", _ => "Only you" };
    public static bool ChangesAccess(ArtifactAccess current, bool makePublic) => current switch
    { ArtifactAccess.AnyoneWithLink => !makePublic, ArtifactAccess.Organization => true, _ => makePublic };
    public static ArtifactRole Offered(ArtifactRole wanted, ArtifactPermissions permissions) => wanted switch
    { ArtifactRole.Editor when permissions.Editors => ArtifactRole.Editor,
        ArtifactRole.Editor or ArtifactRole.Commenter when permissions.Commenters => ArtifactRole.Commenter, _ => ArtifactRole.Reader };
    public static string RoleLabel(ArtifactRole role) => role switch
    { ArtifactRole.Editor => "Can edit", ArtifactRole.Commenter => "Can comment", _ => "Can view" };
    public static JsonObject AccessBody(bool makePublic, IEnumerable<string> invited)
    {
        var users = invited.ToArray();
        var read = new JsonObject { ["mode"] = makePublic ? "public" : users.Length == 0 ? "owner" : "users" };
        if (!makePublic && users.Length != 0) read["users"] = new JsonArray(users.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return new JsonObject { ["read"] = read };
    }

    public static (List<Artifact> Items, string? Next)? Page(JsonNode? json, Guid account, string org)
    {
        if (json is not JsonObject root || root["artifacts"] is not JsonArray rows) return null;
        var items = new List<Artifact>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Text(row["id"]) is not { Length: > 0 } id) continue;
            Uri? url = Uri.TryCreate(Text(row["open_url"]), UriKind.Absolute, out var proposed)
                && WebAccountPolicy.IsClaudeOrigin(proposed.AbsoluteUri) ? proposed : null;
            var title = Text(row["title"]);
            items.Add(new Artifact { Id = id, Account = account, Organization = org, Title = string.IsNullOrEmpty(title) ? "Untitled" : title,
                Kind = Text(row["kind"]) ?? "page", OpenUrl = url, Audience = Text(row["audience"]) ?? "owner",
                Relation = Text(row["rel"]) ?? "mine", Renamable = Flag(row["renamable"]), Duplicable = Flag(row["duplicable"]),
                DuplicateAvailable = row["duplicate_available"] is null || Flag(row["duplicate_available"]),
                SortTime = DateTimeOffset.TryParse(Text(row["sort_time"]) ?? Text(row["updated_at"]), out var date) ? date : null });
        }
        var token = Text(root["next_page_token"]);
        return (items, string.IsNullOrEmpty(token) ? null : token);
    }

    public static ArtifactPermissions? Permissions(JsonNode? json)
    {
        if (json is not JsonObject root) return null;
        var read = root["read"] as JsonObject;
        var version = Text(root["version"]) ?? (root["version"] is JsonValue value && value.TryGetValue<long>(out var n) ? n.ToString() : null);
        return new ArtifactPermissions(version, (read?["users"] as JsonArray)?.Select(Text).OfType<string>().ToArray() ?? [],
            Flag(root["commentersEnabled"]), root.ContainsKey("editorsEnabled") ? Flag(root["editorsEnabled"]) : Flag(root["writersEnabled"]),
            Text(read?["mode"]));
    }

    public static List<Artifact> Merge(IEnumerable<IEnumerable<Artifact>> lists)
    {
        var found = new Dictionary<string, Artifact>();
        foreach (var artifact in lists.SelectMany(l => l))
            if (!found.TryGetValue(artifact.Id, out var earlier) || artifact.IsOwn && !earlier.IsOwn) found[artifact.Id] = artifact;
        return found.Values.OrderByDescending(a => a.SortTime ?? DateTimeOffset.MinValue).ToList();
    }

    public static string? Reason(JsonNode? json) => json is JsonObject obj
        ? Text(obj["reason"]) ?? (obj["error"] is JsonObject error ? Text(error["reason"]) : null) : null;

    public static ArtifactSource? Source(JsonNode? json)
    {
        if (json is not JsonObject root || Text(root["assetToken"]) is not { Length: > 0 } token) return null;
        var version = Text(root["ver"]) ?? (root["ver"] is JsonValue value && value.TryGetValue<long>(out var n) ? n.ToString() : null);
        if (string.IsNullOrEmpty(version)) return null;
        var files = new List<string> { "index.html" };
        foreach (var row in (root["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (Text(row["path"]) is string path && IsSafeFilePath(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
        return new ArtifactSource(version, token, files.ToArray());
    }

    public static bool IsSafeFilePath(string path) => !string.IsNullOrEmpty(path) && !path.Contains('\\')
        && path.Split('/').All(p => p is not ("" or "." or "..") && Graft.ValidateWindowsName(p) is null);
    public static Uri? FileUrl(string artifact, ArtifactSource source, string path) =>
        Guid.TryParseExact(artifact, "D", out var id) && IsSafeFilePath(path)
            ? new Uri($"https://{id:D}.frame.claudeusercontent.com/_f/{Escape(source.Version)}/_src/"
                + string.Join('/', path.Split('/').Select(Escape)) + "?__frame_t=" + Escape(source.Token)) : null;

    public static string[] Addresses(string text) => text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Trim('<', '>', '"', '\'', '(', ')').ToLowerInvariant()).Where(IsAddress).Distinct().ToArray();
    private static bool IsAddress(string text) => text.Split('@') is [var local, var domain] && local.Length > 0
        && domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
