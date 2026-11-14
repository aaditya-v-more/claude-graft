using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

public sealed class PoolSettings
{
    public Dictionary<string, string[]> Borrowers { get; set; } = [];
    public Dictionary<string, string> Assignments { get; set; } = [];
    public Dictionary<string, DateTimeOffset> AssignedAt { get; set; } = [];
    public bool RevocationPending { get; set; }
    public bool Enabled => Borrowers.Values.Any(x => x.Length > 0);
    public static PoolSettings Load()
    {
        if (!File.Exists(UsagePool.SettingsFile)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<PoolSettings>(File.ReadAllBytes(UsagePool.SettingsFile), UsagePool.Json);
            if (settings?.Borrowers is null || settings.Assignments is null || settings.AssignedAt is null
                || settings.Borrowers.Values.Any(v => v is null || v.Any(string.IsNullOrEmpty)) || settings.Assignments.Values.Any(string.IsNullOrEmpty)) throw new JsonException();
            return settings;
        }
        catch (JsonException) { throw new IOException("Shared Usage settings could not be read. The saved file has been preserved."); }
    }
    public void Save() => PoolStorage.Write(UsagePool.SettingsFile, JsonSerializer.SerializeToUtf8Bytes(this, UsagePool.Json));
    public void DisableAndSave() { Borrowers.Clear(); Assignments.Clear(); AssignedAt.Clear(); RevocationPending = true; Save(); }
    public void SetLender(string borrower, string lender, bool enabled)
    {
        if (borrower == lender) return;
        var current = (Borrowers.GetValueOrDefault(borrower) ?? []).ToList();
        if (enabled) { if (!current.Contains(lender)) current.Add(lender); }
        else if (current.Remove(lender)) RevocationPending = true;
        Borrowers[borrower] = current.ToArray();
    }
    public void ForgetLender(string lender)
    {
        foreach (var borrower in Borrowers.Keys.ToArray()) SetLender(borrower, lender, false);
        foreach (var session in Assignments.Where(p => p.Value == lender).Select(p => p.Key).ToArray())
        { Assignments.Remove(session); AssignedAt.Remove(session); }
    }
    public bool Acknowledge(PoolCommand command, PoolStatus status)
    {
        if (!RevocationPending || command.Configure is null || !status.Configured || status.Enabled != command.Configure.Enabled) return false;
        RevocationPending = false; return true;
    }
    public void Assign(string session, string? target)
    {
        if (target is null) { Assignments.Remove(session); AssignedAt.Remove(session); }
        else { Assignments[session] = target; AssignedAt[session] = DateTimeOffset.UtcNow; }
        Save();
    }
    public bool PruneAssignments(IEnumerable<PoolSession> sessions, DateTimeOffset now)
    {
        var active = sessions.Where(s => now - s.LastAt < TimeSpan.FromDays(1)).Select(s => s.Id).ToHashSet();
        var expired = AssignedAt.Where(p => now - p.Value > TimeSpan.FromDays(1) && !active.Contains(p.Key)).Select(p => p.Key).ToArray();
        foreach (var id in expired) { AssignedAt.Remove(id); Assignments.Remove(id); }
        return expired.Length > 0;
    }
    public bool PruneMembers(IEnumerable<string> profiles, IEnumerable<string> accounts)
    {
        var borrowers = profiles.ToHashSet(); var lenders = accounts.Concat(borrowers).ToHashSet(); var changed = false;
        foreach (var key in Borrowers.Keys.ToArray())
        {
            if (!borrowers.Contains(key)) { Borrowers.Remove(key); changed = true; continue; }
            var kept = Borrowers[key].Where(id => id != key && lenders.Contains(id)).Distinct().ToArray();
            if (!kept.SequenceEqual(Borrowers[key])) { Borrowers[key] = kept; changed = true; }
        }
        foreach (var id in Assignments.Where(p => !lenders.Contains(p.Value)).Select(p => p.Key).ToArray())
        { Assignments.Remove(id); AssignedAt.Remove(id); changed = true; }
        if (changed) RevocationPending = true;
        return changed;
    }
}

public sealed record PoolAccount(string Id, string? AccountUuid, string? Email, string? OrganizationName,
    string[] Scopes, DateTimeOffset Expires, DateTimeOffset Added, string Sealed)
{
    public bool IsCurrent => Expires > DateTimeOffset.UtcNow.AddMinutes(2);
    public string Name => Email ?? OrganizationName ?? "Saved account";
    public string Token()
    {
        var bytes = PoolStorage.Unprotect(Convert.FromBase64String(Sealed));
        try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static PoolAccount[] Load()
    {
        if (!File.Exists(UsagePool.AccountsFile)) return [];
        try
        {
            var accounts = JsonSerializer.Deserialize<PoolAccount[]>(File.ReadAllBytes(UsagePool.AccountsFile), UsagePool.Json);
            if (accounts is null || accounts.Any(a => a is null || string.IsNullOrEmpty(a.Id) || a.Scopes is null || string.IsNullOrEmpty(a.Sealed))) throw new JsonException();
            return accounts;
        }
        catch (JsonException) { throw new IOException("Saved lending accounts could not be read. Their file has been preserved."); }
    }
    public static void Save(IEnumerable<PoolAccount> accounts) => PoolStorage.Write(UsagePool.AccountsFile, JsonSerializer.SerializeToUtf8Bytes(accounts, UsagePool.Json));
}

/// <summary>An independent inference-only OAuth login. Nothing in a Claude profile is renewed or replaced.</summary>
public static class PoolAccountSignIn
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string AuthorizeEndpoint = "https://claude.com/cai/oauth/authorize";
    public const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    public const int Lifetime = 365 * 24 * 60 * 60;
    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Uri AuthorizeUrl(string redirect, string verifier, string state)
    {
        var values = new Dictionary<string, string>
        {
            ["code"] = "true", ["client_id"] = ClientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = ClaudeCredentials.InferenceScope, ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256", ["state"] = state,
        };
        return new(AuthorizeEndpoint + "?" + string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
    }
    public static async Task<PoolAccount> SignInAsync(Action<Uri> openBrowser, string? ownAccount = null, CancellationToken cancellation = default)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        return await SignInAsync(openBrowser, ownAccount, client, cancellation);
    }
    internal static async Task<PoolAccount> SignInAsync(Action<Uri> openBrowser, string? ownAccount, HttpClient http, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var v4 = new TcpListener(IPAddress.Loopback, 0); v4.Start(8);
        var port = ((IPEndPoint)v4.LocalEndpoint).Port;
        TcpListener? v6 = null;
        try { v6 = new(IPAddress.IPv6Loopback, port); v6.Server.DualMode = false; v6.Start(8); }
        catch (SocketException) { v6?.Stop(); v6 = null; }
        var redirect = $"http://localhost:{port}/callback"; var verifier = RandomToken(); var state = RandomToken();
        try
        {
            var pending = new List<(TcpListener Listener, Task<TcpClient> Accept)> { (v4, v4.AcceptTcpClientAsync(deadline.Token).AsTask()) };
            if (v6 is not null) pending.Add((v6, v6.AcceptTcpClientAsync(deadline.Token).AsTask()));
            openBrowser(AuthorizeUrl(redirect, verifier, state));
            while (true)
            {
                var accepted = await Task.WhenAny(pending.Select(p => p.Accept)); var index = pending.FindIndex(p => p.Accept == accepted);
                using var browser = await accepted;
                pending[index] = (pending[index].Listener, pending[index].Listener.AcceptTcpClientAsync(deadline.Token).AsTask());
                using var wire = new PoolWire(browser.GetStream());
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                PoolHead? head;
                try { head = await wire.HeadAsync(requestTimeout.Token); }
                catch (Exception e) when (e is IOException or OperationCanceledException) { continue; }
                if (head?.Method != "GET" || !Uri.TryCreate("http://localhost" + head.Target, UriKind.Absolute, out var callback) || callback.AbsolutePath != "/callback")
                { await Respond(wire, 404, "Return to Graft to sign in.", requestTimeout.Token); continue; }
                var query = new Dictionary<string, string>();
                foreach (var part in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    query.TryAdd(Uri.UnescapeDataString(pair[0]), pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "");
                }
                if (!query.TryGetValue("state", out var supplied) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(state)))
                { await Respond(wire, 400, "That sign-in does not belong to this Graft window. Return to the sign-in tab opened by Graft.", requestTimeout.Token); continue; }
                if (query.ContainsKey("error") || !query.TryGetValue("code", out var code) || code.Length == 0)
                { await Respond(wire, 400, "Sign-in was not completed. Return to Graft and try again.", requestTimeout.Token); throw new IOException("The account sign-in was cancelled or refused."); }
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        { grant_type = "authorization_code", code, redirect_uri = redirect, client_id = ClientId, code_verifier = verifier, state, expires_in = Lifetime }), Encoding.UTF8, "application/json"),
                    };
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (!response.IsSuccessStatusCode) throw new IOException("The account sign-in was refused. Return to Graft and try again.");
                    await response.Content.LoadIntoBufferAsync(1024 * 1024, deadline.Token);
                    using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
                    var account = ParseAccount(result.RootElement, ownAccount);
                    await Respond(wire, 200, "This account is ready to lend usage. Return to Graft; you can close this tab.", deadline.Token);
                    return account;
                }
                catch (Exception e) when (e is IOException or HttpRequestException or JsonException or CryptographicException)
                { await Respond(wire, 400, "The account could not be saved. Return to Graft and try again.", deadline.Token); throw new IOException("The account could not be signed in and saved. Please try again."); }
            }
        }
        finally { deadline.Cancel(); v4.Stop(); v6?.Stop(); }
    }
    internal static PoolAccount ParseAccount(JsonElement result, string? ownAccount)
    {
        if (!result.TryGetProperty("access_token", out var t) || t.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(t.GetString())) throw new JsonException();
        string? String(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var account = result.TryGetProperty("account", out var a) ? a : default;
        var organization = result.TryGetProperty("organization", out var o) ? o : default;
        var uuid = String(account, "uuid");
        if (uuid is not null && uuid == ownAccount) throw new IOException("This is the borrowing Claude's own account. Choose a different account to add more usage.");
        var expires = result.TryGetProperty("expires_in", out var expiry) && expiry.TryGetDouble(out var seconds) && double.IsFinite(seconds) ? Math.Clamp(seconds, 0, Lifetime * 2d) : Lifetime;
        var bytes = Encoding.UTF8.GetBytes(t.GetString()!);
        try
        {
            return new("account:" + (uuid ?? Guid.NewGuid().ToString("N")), uuid, String(account, "email_address"), String(organization, "name"),
                (String(result, "scope") ?? ClaudeCredentials.InferenceScope).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                DateTimeOffset.UtcNow.AddSeconds(expires), DateTimeOffset.UtcNow, Convert.ToBase64String(PoolStorage.Protect(bytes)));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task Respond(PoolWire wire, int status, string message, CancellationToken cancellation)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Claude Graft</title></head><body style=\"font:16px system-ui;padding:2rem;max-width:36rem;margin:auto\"><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>");
        await wire.SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"), cancellation);
        await wire.SendAsync(body, cancellation);
    }
}
