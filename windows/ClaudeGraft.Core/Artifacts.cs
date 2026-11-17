using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// <summary>
/// An account Graft has been signed in to on claude.ai, for its artifacts. The
/// sign-in itself lives in the browser storage named by Id and never passes
/// through Graft; this is what the account said about itself, so the list can
/// name it before it is asked.
/// </summary>
public sealed record ArtifactAccount(Guid Id, string? Email, string? AccountUuid, string? Org);

public static class ArtifactAccounts
{
    /// A second sign-in to a listed account is made because the first stopped
    /// working, and a sign-in is never read out of one store into another; so
    /// the record moves to the new store, keeping its place, and the stores it
    /// was under come back as Retired to be removed.
    public static (List<ArtifactAccount> Accounts, List<Guid> Retired) SignedIn(
        IReadOnlyList<ArtifactAccount> accounts, Guid store, ArtifactAPI.Identity identity)
    {
        var record = new ArtifactAccount(store, identity.Email, identity.AccountUuid, identity.Org);
        var retired = accounts.Where(a => a.AccountUuid == identity.AccountUuid && a.Id != store).Select(a => a.Id).ToList();
        var replaced = retired.Append(store).ToHashSet();
        var list = accounts.Where(a => !replaced.Contains(a.Id)).ToList();
        var place = accounts.ToList().FindIndex(a => retired.Contains(a.Id));
        if (place < 0) place = accounts.ToList().FindIndex(a => a.Id == store);
        if (place >= 0) list.Insert(accounts.Take(place).Count(a => !replaced.Contains(a.Id)), record);
        else list.Add(record);
        return (list, retired);
    }
}

/// What an invited person may do, by the names claude.ai's invitations use.
public enum ArtifactRole { Reader, Commenter, Editor }

public static class ArtifactRoles
{
    public static string Wire(this ArtifactRole role) => role switch
    {
        ArtifactRole.Commenter => "commenter", ArtifactRole.Editor => "editor", _ => "reader",
    };

    public static ArtifactRole? Parse(string? wire) => wire switch
    {
        "reader" => ArtifactRole.Reader, "commenter" => ArtifactRole.Commenter, "editor" => ArtifactRole.Editor, _ => null,
    };

    public static string Label(this ArtifactRole role) => role switch
    {
        ArtifactRole.Commenter => "Can Comment", ArtifactRole.Editor => "Can Edit", _ => "Can View",
    };

    /// The most an artifact will take of what was asked, never more.
    public static ArtifactRole OfferedBy(this ArtifactRole role, ArtifactAPI.Permissions permissions) => role switch
    {
        ArtifactRole.Editor when permissions.Editors => ArtifactRole.Editor,
        ArtifactRole.Editor or ArtifactRole.Commenter => permissions.Commenters ? ArtifactRole.Commenter : ArtifactRole.Reader,
        _ => ArtifactRole.Reader,
    };
}

/// <summary>
/// People to share with in one go; claude.ai has no such thing for a personal
/// account, so the group is Graft's. An address missing from Roles is a viewer,
/// which is how groups saved before roles were shared, so an older Graft reads
/// a new file as viewers only: less than asked, never more.
/// </summary>
[JsonConverter(typeof(ArtifactGroupConverter))]
public sealed class ArtifactGroup : IEquatable<ArtifactGroup>
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Emails { get; set; } = [];
    public Dictionary<string, ArtifactRole> Roles { get; set; } = new();

    public ArtifactRole RoleOf(string email) => Roles.TryGetValue(email, out var r) ? r : ArtifactRole.Reader;

    public void SetRole(ArtifactRole role, string email)
    {
        if (role == ArtifactRole.Reader) Roles.Remove(email);
        else Roles[email] = role;
    }

    public void SetRoleForEveryone(ArtifactRole role) =>
        Roles = role == ArtifactRole.Reader ? new() : Emails.Distinct().ToDictionary(e => e, _ => role);

    /// The one role everyone has, or null for a group that mixes them.
    public ArtifactRole? CommonRole
    {
        get
        {
            if (Emails.Count == 0) return ArtifactRole.Reader;
            var all = Emails.Select(RoleOf).Distinct().ToList();
            return all.Count == 1 ? all[0] : null;
        }
    }

    public ArtifactGroup Copy() => new() { Id = Id, Name = Name, Emails = [.. Emails], Roles = new(Roles) };

    public bool Equals(ArtifactGroup? other) =>
        other is not null && Id == other.Id && Name == other.Name && Emails.SequenceEqual(other.Emails)
        && Emails.All(e => RoleOf(e) == other.RoleOf(e));

    public override bool Equals(object? obj) => Equals(obj as ArtifactGroup);
    public override int GetHashCode() => Id.GetHashCode();
}

internal sealed class ArtifactGroupConverter : JsonConverter<ArtifactGroup>
{
    public override ArtifactGroup Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("not a group");
        var group = new ArtifactGroup
        {
            Id = Guid.Parse((string?)node["id"] ?? throw new JsonException("no id")),
            Name = (string?)node["name"] ?? throw new JsonException("no name"),
            Emails = (node["emails"] as JsonArray ?? throw new JsonException("no emails")).Select(e => (string)e!).ToList(),
        };
        // A role from a newer Graft that this one does not know costs that
        // address its role, not the whole group.
        if (node["roles"] is JsonObject roles)
            foreach (var (email, value) in roles)
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && ArtifactRoles.Parse(s) is { } role)
                    group.Roles[email] = role;
        return group;
    }

    public override void Write(Utf8JsonWriter writer, ArtifactGroup value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("emails");
        foreach (var email in value.Emails) writer.WriteStringValue(email);
        writer.WriteEndArray();
        writer.WriteString("id", value.Id);
        writer.WriteString("name", value.Name);
        // Viewers are left out, so a group of viewers is written as before roles.
        var kept = value.Roles.Where(r => value.Emails.Contains(r.Key) && r.Value != ArtifactRole.Reader).ToList();
        if (kept.Count > 0)
        {
            writer.WriteStartObject("roles");
            foreach (var (email, role) in kept.OrderBy(r => r.Key, StringComparer.Ordinal)) writer.WriteString(email, role.Wire());
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}

public sealed record Artifact
{
    /// The artifact's UUID, which every request is keyed by; the page address
    /// uses a shorter id, which is why OpenUrl is kept as given.
    public required string Id { get; init; }
    public required Guid Account { get; init; }
    public required string Org { get; init; }
    public required string Title { get; init; }
    public string Kind { get; init; } = "page";
    public string? OpenUrl { get; init; }
    public string Audience { get; init; } = "owner";
    /// "mine", or "shared" for one someone else owns.
    public string Rel { get; init; } = "mine";
    public DateTimeOffset? SortTime { get; init; }
    public string? OwnerAccount { get; init; }
    public bool Renamable { get; init; }
    public bool Duplicable { get; init; }
    /// A copy claude.ai would make, but not today: it caps copies a day.
    public bool DuplicateAvailable { get; init; } = true;

    public bool IsOwn => Rel == "mine";
    public ArtifactAccess Access => ArtifactAccessOf.Audience(Audience);
}

/// Who can open an artifact, as the share panel on claude.ai says it.
public enum ArtifactAccess { OnlyYou, Invited, Organization, AnyoneWithLink }

public static class ArtifactAccessOf
{
    public static ArtifactAccess Audience(string audience) => audience switch
    {
        "public" or "link" => ArtifactAccess.AnyoneWithLink,
        "org" or "space" => ArtifactAccess.Organization,
        "users" => ArtifactAccess.Invited,
        _ => ArtifactAccess.OnlyYou,
    };

    public static string Label(this ArtifactAccess access) => access switch
    {
        ArtifactAccess.Invited => "Only people invited",
        ArtifactAccess.Organization => "Your organization",
        ArtifactAccess.AnyoneWithLink => "Anyone with the link",
        _ => "Only you",
    };

    /// Graft offers only invited and public; one shared with the organization
    /// checks an entry of its own, since read as invited, choosing the checked
    /// entry took the organization's access away without a word.
    public static ArtifactAccess MenuChoice(this ArtifactAccess access) =>
        access == ArtifactAccess.OnlyYou ? ArtifactAccess.Invited : access;

    public static bool ChangesToPublic(this ArtifactAccess access, bool isPublic) => access switch
    {
        ArtifactAccess.AnyoneWithLink => !isPublic,
        ArtifactAccess.Organization => true,
        _ => isPublic,
    };
}

/// <summary>
/// The requests claude.ai's own artifact pages make, read off its web client in
/// November 2026. None is documented, so each keeps exactly the page's shape.
/// </summary>
public static class ArtifactAPI
{
    public const string Origin = "https://claude.ai";
    public const string AccountPath = "/api/account";
    public const string SignInUrl = "https://claude.ai/login";

    /// Without X-Frame-CP the service answers 404 to a request otherwise
    /// identical to the page's, as though the artifact did not exist.
    public static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string> { ["X-Frame-CP"] = "go", ["X-Frame-Platform"] = "web" };

    public const int PageSize = 48;
    public const int MaximumPages = 5;
    public const int MaximumExportBytes = 200 * 1024 * 1024;

    private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ListPath(string rel, string org, string? after = null)
    {
        var query = new List<(string, string)> { ("rel", rel), ("empty", "exclude"), ("limit", PageSize.ToString()), ("org", org), ("sort", "last_viewed") };
        if (after is not null) query.Add(("page_token", after));
        return "/api/frame/artifacts?" + Encode(query);
    }

    public static string PermPath(string artifact, string org) => $"/api/frame/perm/{Component(artifact)}?" + Encode([("org", org)]);
    public static string InvitePath(string artifact, string org) => $"/api/frame/invite/{Component(artifact)}?" + Encode([("org", org)]);
    public static string RetitlePath(string artifact, string org) => $"/api/frame/retitle/{Component(artifact)}?" + Encode([("org", org)]);
    /// Posted with no body: the copy is of the version the owner last saved.
    public static string DuplicatePath(string artifact, string org) => $"/api/frame/duplicate/{Component(artifact)}?" + Encode([("org", org)]);
    /// DELETE removes it; GET is what its viewer asks first, for the version and file token.
    public static string ArtifactPath(string artifact, string org) => $"/api/frame/{Component(artifact)}?" + Encode([("org", org)]);

    public static string InviteBody(string email, ArtifactRole role = ArtifactRole.Reader) =>
        JsonSerializer.Serialize(new { email, role = role.Wire() }, Wire);

    public static string RetitleBody(string title) => JsonSerializer.Serialize(new { title }, Wire);

    /// Going back to private keeps whoever was invited, as the share panel does.
    public static string ReadModeBody(bool isPublic, IReadOnlyList<string> invited) =>
        isPublic ? JsonSerializer.Serialize(new { read = new { mode = "public" } }, Wire)
        : invited.Count == 0 ? JsonSerializer.Serialize(new { read = new { mode = "owner" } }, Wire)
        : JsonSerializer.Serialize(new { read = new { mode = "users", users = invited } }, Wire);

    /// A 403 is signed out only from the account page, which any signed-in
    /// account may read; elsewhere it is as likely a refusal of that one thing.
    public static bool MeansSignedOut(int status, string path) => status == 401 || (status == 403 && IsAccountPath(path));

    public static bool NeedsAccountCheck(int status, string path) => status == 403 && !IsAccountPath(path);

    private static bool IsAccountPath(string path) => path == AccountPath || path.StartsWith(AccountPath + "?", StringComparison.Ordinal);

    public sealed record Identity(string AccountUuid, string Email, string Org);

    /// Artifacts live under the organization that can chat; an account in a
    /// team as well belongs to both, and the first listed is not reliably either.
    public static Identity? IdentityFrom(string json)
    {
        if (Parse(json) is not JsonObject o || Str(o, "uuid") is not { } uuid || Str(o, "email_address") is not { } email
            || o["memberships"] is not JsonArray memberships) return null;
        var organizations = memberships.Select(m => (m as JsonObject)?["organization"] as JsonObject).OfType<JsonObject>().ToList();
        var chat = organizations.FirstOrDefault(org => org["capabilities"] is JsonArray caps && caps.Any(c => c is JsonValue v && v.TryGetValue<string>(out var s) && s == "chat"))
            ?? organizations.FirstOrDefault();
        return chat is not null && Str(chat, "uuid") is { } orgId ? new Identity(uuid, email, orgId) : null;
    }

    /// Null for anything that is not a list, such as a challenge page, which
    /// must not read as an empty one.
    public static (List<Artifact> Artifacts, string? Next)? Page(string json, Guid account, string org)
    {
        if (Parse(json) is not JsonObject o || o["artifacts"] is not JsonArray rows) return null;
        var artifacts = new List<Artifact>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Str(row, "id") is not { } id) continue;
            artifacts.Add(new Artifact
            {
                Id = id, Account = account, Org = org,
                Title = Str(row, "title") is { Length: > 0 } t ? t : "Untitled",
                Kind = Str(row, "kind") ?? "page",
                OpenUrl = Str(row, "open_url"),
                Audience = Str(row, "audience") ?? "owner",
                Rel = Str(row, "rel") ?? "mine",
                SortTime = Date(Str(row, "sort_time")) ?? Date(Str(row, "updated_at")),
                OwnerAccount = Str(row, "owner_account"),
                Renamable = Bool(row, "renamable") ?? false,
                Duplicable = Bool(row, "duplicable") ?? false,
                DuplicateAvailable = Bool(row, "duplicate_available") ?? true,
            });
        }
        return (artifacts, Str(o, "next_page_token") is { Length: > 0 } next ? next : null);
    }

    public sealed record Permissions
    {
        public string? Version { get; init; }
        public IReadOnlyList<string> Invited { get; init; } = [];
        public bool Commenters { get; init; }
        public bool Editors { get; init; }
        /// read.mode as claude.ai has it now, which a list read earlier may not.
        public string? Mode { get; init; }
    }

    public static Permissions? PermissionsFrom(string json)
    {
        if (Parse(json) is not JsonObject o) return null;
        var read = o["read"] as JsonObject;
        return new Permissions
        {
            Version = Str(o, "version") ?? (o["version"] is JsonValue v && v.TryGetValue<long>(out var n) ? n.ToString() : null),
            Invited = (read?["users"] as JsonArray)?.Select(u => u is JsonValue uv && uv.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() ?? [],
            Commenters = Bool(o, "commentersEnabled") ?? false,
            // Older answers name editing writersEnabled; a missing flag offers nothing.
            Editors = Bool(o, "editorsEnabled") ?? Bool(o, "writersEnabled") ?? false,
            Mode = read is null ? null : Str(read, "mode"),
        };
    }

    public static string? Reason(string json)
    {
        if (Parse(json) is not JsonObject o) return null;
        return Str(o, "reason") ?? (o["error"] is JsonObject e ? Str(e, "reason") : null);
    }

    /// What reading an artifact's files takes: their version and the viewer's token.
    public sealed record Source(string Version, string Token, string? Title, IReadOnlyList<string> Files);

    public static Source? SourceFrom(string json)
    {
        if (Parse(json) is not JsonObject o || Str(o, "assetToken") is not { Length: > 0 } token) return null;
        var version = Str(o, "ver") ?? (o["ver"] is JsonValue v && v.TryGetValue<long>(out var n) ? n.ToString() : null);
        if (string.IsNullOrEmpty(version)) return null;
        var files = new List<string> { "index.html" };
        foreach (var f in (o["files"] as JsonArray ?? []).OfType<JsonObject>())
            if (Str(f, "path") is { } path && IsSafeFilePath(path) && !files.Contains(path)) files.Add(path);
        return new Source(version, token, Str(o, "title"), files);
    }

    public static bool IsSafeFilePath(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':')
        && path.Split('/').All(p => p.Length > 0 && p != "." && p != "..");

    /// Where an artifact's files are served, as its viewer reads them. The token
    /// goes in the address because that is the only way the file host takes it.
    public static string? FileUrl(string artifact, Source source, string path)
    {
        var host = new string(artifact.ToLowerInvariant().Where(c => Uri.IsHexDigit(c) || c == '-').ToArray());
        if (host.Length == 0 || !IsSafeFilePath(path)) return null;
        var encoded = string.Join("/", path.Split('/').Select(Component));
        return $"https://{host}.frame.claudeusercontent.com/_f/{Component(source.Version)}/_src/{encoded}?" + Encode([("__frame_t", source.Token)]);
    }

    /// One artifact two accounts can see is listed once, under the owner, newest first.
    public static List<Artifact> Merge(IEnumerable<IEnumerable<Artifact>> lists)
    {
        var seen = new Dictionary<string, int>();
        var merged = new List<Artifact>();
        foreach (var artifact in lists.SelectMany(l => l))
        {
            if (seen.TryGetValue(artifact.Id, out var index))
            {
                if (artifact.IsOwn && !merged[index].IsOwn) merged[index] = artifact;
                continue;
            }
            seen[artifact.Id] = merged.Count;
            merged.Add(artifact);
        }
        return merged.OrderByDescending(a => a.SortTime ?? DateTimeOffset.MinValue).ToList();
    }

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); } catch (JsonException) { return null; }
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string Component(string text) => Uri.EscapeDataString(text);

    private static string Encode(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join("&", pairs.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
}

/// What Graft keeps about artifacts, beside the rest of its own state.
public static class ArtifactFiles
{
    public static string Accounts => Path.Combine(GraftPaths.OwnData, "artifact-accounts.json");
    public static string Groups => Path.Combine(GraftPaths.OwnData, "artifact-groups.json");

    public enum State { Missing, Unreadable, Read }

    /// A missing file may be written over; one that will not decode may not,
    /// since saving over it throws away every account or group in it.
    public sealed record Reading<T>(State State, T? Value);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Reading<T> Read<T>(string path)
    {
        if (!File.Exists(path)) return new(State.Missing, default);
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) is { } v ? new(State.Read, v) : new(State.Unreadable, default); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or FormatException) { return new(State.Unreadable, default); }
    }

    /// One entry at a time, so an entry that will not decode costs that entry
    /// and not the list; Lost says how many, which is when the file is set aside.
    public static Reading<(List<T> Values, int Lost)> ReadEach<T>(string path)
    {
        var raw = Read<JsonArray>(path);
        if (raw.State != State.Read) return new(raw.State, default);
        var values = new List<T>();
        var lost = 0;
        foreach (var entry in raw.Value!)
        {
            try
            {
                if (entry is not null && entry.Deserialize<T>(Json) is { } v) values.Add(v);
                else lost++;
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { lost++; }
        }
        return new(State.Read, (values, lost));
    }

    /// Moves an unreadable file aside, beside where it was, to be read by hand.
    /// Null when it could not be moved, and then nothing may be saved over it.
    public static string? SetAside(string path, DateTimeOffset? now = null)
    {
        var name = $"{Path.GetFileNameWithoutExtension(path)}.unreadable-{(now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()}{Path.GetExtension(path)}";
        var target = Path.Combine(Path.GetDirectoryName(path)!, name);
        try { File.Move(path, target); return target; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool Save<T>(T value, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

public static class GroupAddresses
{
    /// Addresses as people paste them: one per line, or out of a mail header
    /// with commas, semicolons and names in angle brackets. Each once, in order.
    public static List<string> Parse(string text)
    {
        var seen = new HashSet<string>();
        var addresses = new List<string>();
        foreach (var piece in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = piece.Trim('<', '>', '"', '\'', '(', ')').ToLowerInvariant();
            if (IsAddress(trimmed) && seen.Add(trimmed)) addresses.Add(trimmed);
        }
        return addresses;
    }

    public static bool IsAddress(string text)
    {
        var parts = text.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0) return false;
        var domain = parts[1];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }
}

/// The forms claude.ai's export menu offers that need nothing but this machine.
/// Google Docs and Notion are left out: each is a connection, not a file.
public enum ArtifactExportFormat { Markdown, Pdf, Word, Html }

public static class ArtifactExport
{
    public static string Label(this ArtifactExportFormat f) => f switch
    {
        ArtifactExportFormat.Markdown => "Markdown", ArtifactExportFormat.Pdf => "PDF",
        ArtifactExportFormat.Word => "Word Document", _ => "Page Source",
    };

    /// A page of several files goes out zipped, since its page alone would open with pieces missing.
    public static string Extension(this ArtifactExportFormat f, bool singleFile) => f switch
    {
        ArtifactExportFormat.Markdown => "md", ArtifactExportFormat.Pdf => "pdf", ArtifactExportFormat.Word => "docx",
        _ => singleFile ? "html" : "zip",
    };

    /// The title as a file name, with what Windows refuses in one made a dash.
    public static string FileName(string title, ArtifactExportFormat format, bool singleFile)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(title.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().Trim('.').Trim();
        if (name.Length > 120) name = name[..120].Trim();
        if (name.Length == 0) name = "Artifact";
        return name + "." + format.Extension(singleFile);
    }
}
