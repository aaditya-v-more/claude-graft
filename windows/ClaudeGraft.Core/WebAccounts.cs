using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

public sealed record WebAccount
{
    public required Guid Id { get; init; }
    public required string AccountUuid { get; init; }
    public required string Email { get; init; }
    public required string OrganizationId { get; init; }
    public string OrganizationName { get; init; } = "";
    public string DisplayName => string.IsNullOrWhiteSpace(Email) ? AccountUuid : Email;
}

public static class WebAccountPolicy
{
    public static WebAccount? Identity(Guid store, JsonNode? json)
    {
        if (json is not JsonObject account || Text(account["uuid"]) is not { Length: > 0 } uuid
            || Text(account["email_address"]) is not { Length: > 0 } email
            || account["memberships"] is not JsonArray memberships) return null;
        var organizations = memberships.OfType<JsonObject>().Select(m => m["organization"])
            .OfType<JsonObject>().ToArray();
        var organization = organizations.FirstOrDefault(o => o["capabilities"] is JsonArray caps
            && caps.Any(c => Text(c) == "chat")) ?? organizations.FirstOrDefault();
        if (Text(organization?["uuid"]) is not { Length: > 0 } org) return null;
        return new WebAccount { Id = store, AccountUuid = uuid, Email = email, OrganizationId = org,
            OrganizationName = Text(organization?["name"]) ?? "" };
    }

    public static (IReadOnlyList<WebAccount> Accounts, Guid[] Retired) SignedIn(
        IReadOnlyList<WebAccount> accounts, WebAccount account)
    {
        var retired = accounts.Where(a => a.AccountUuid == account.AccountUuid && a.Id != account.Id)
            .Select(a => a.Id).ToArray();
        var replaced = retired.Append(account.Id).ToHashSet();
        var place = accounts.Select((a, i) => (a, i)).FirstOrDefault(p => retired.Contains(p.a.Id));
        var oldIndex = place.a is not null ? place.i : accounts.ToList().FindIndex(a => a.Id == account.Id);
        var result = accounts.Where(a => !replaced.Contains(a.Id)).ToList();
        result.Insert(oldIndex < 0 ? result.Count : accounts.Take(oldIndex).Count(a => !replaced.Contains(a.Id)), account);
        return (result, retired);
    }

    public static bool IsClaudeOrigin(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsApiPath(string path) => path.StartsWith('/') && !path.StartsWith("//")
        && !path.Contains('\\') && !path.Any(char.IsControl)
        && Uri.TryCreate(new Uri("https://claude.ai"), path, out var uri) && IsClaudeOrigin(uri.AbsoluteUri)
        && (uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)
            || uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal));

    public static bool MeansSignedOut(int status, string path) => status == 401
        || status == 403 && path.Split('?')[0] == "/api/account";
    public static bool NeedsAccountCheck(int status, string path) => status == 403 && !MeansSignedOut(status, path);

    public static Dictionary<string, string> Headers(string path, bool hasBody,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        if (!IsApiPath(path)) throw new ArgumentException("Only claude.ai API paths are accepted.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
        if (path.StartsWith("/api/frame/", StringComparison.Ordinal))
        { headers["X-Frame-CP"] = "go"; headers["X-Frame-Platform"] = "web"; }
        if (hasBody) headers["Content-Type"] = "application/json";
        foreach (var pair in extra ?? new Dictionary<string, string>())
        {
            if (pair.Key.Equals("If-Match", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("x-organization-uuid", StringComparison.OrdinalIgnoreCase)) headers[pair.Key] = pair.Value;
            else throw new ArgumentException("That request header is not supported.");
        }
        return headers;
    }

    public static bool IsArtifactFile(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo) && uri.Host.EndsWith(".frame.claudeusercontent.com", StringComparison.OrdinalIgnoreCase)
        && Guid.TryParse(uri.Host[..^".frame.claudeusercontent.com".Length], out _)
        && uri.AbsolutePath.StartsWith("/_f/", StringComparison.Ordinal);

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public sealed class WebAccountStore
{
    private readonly string _file;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public bool ReadExisting { get; private set; }
    public bool CanWrite { get; private set; } = true;
    public string? Notice { get; private set; }
    public WebAccountStore(string? file = null) => _file = file ?? Path.Combine(GraftPaths.OwnData, "artifact-accounts.json");

    public IReadOnlyList<WebAccount> Read()
    {
        try
        {
            var accounts = JsonSerializer.Deserialize<List<WebAccount>>(File.ReadAllBytes(_file), Options)
                ?? throw new JsonException();
            if (accounts.Any(a => a is null || a.Id == Guid.Empty || string.IsNullOrWhiteSpace(a.AccountUuid)
                || string.IsNullOrWhiteSpace(a.OrganizationId) || string.IsNullOrWhiteSpace(a.Email))
                || accounts.Select(a => a.Id).Distinct().Count() != accounts.Count
                || accounts.Select(a => a.AccountUuid).Distinct().Count() != accounts.Count) throw new JsonException();
            ReadExisting = true;
            return accounts;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch
        {
            ReadExisting = false;
            try
            {
                File.Move(_file, _file + ".unreadable-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N"));
                Notice = "The unreadable account list was preserved beside the original. Sign in to add accounts again.";
            }
            catch { CanWrite = false; Notice = "The account list cannot be read or preserved. It has been left untouched."; }
            return [];
        }
    }

    public void Save(IEnumerable<WebAccount> accounts)
    {
        if (!CanWrite) throw new IOException(Notice);
        using var stateLock = StateLock.Acquire();
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        AtomicWrite.Bytes(_file, JsonSerializer.SerializeToUtf8Bytes(accounts, Options));
    }
}
