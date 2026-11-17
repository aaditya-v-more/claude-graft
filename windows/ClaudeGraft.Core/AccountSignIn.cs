using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// Signs an account in through the browser so it can lend without a Claude of
/// its own: the sign-in Claude Code's long-lived login uses, a year-long token
/// for model requests and nothing else. The browser comes back to a listener on
/// loopback with a one-time code, exchanged at Anthropic's sign-in service; that
/// exchange sends the code, never a token, and the refresh token it returns is
/// dropped unread, since holding one is holding a login that renews itself.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AccountSignIn
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string Authorize = "https://claude.com/cai/oauth/authorize";
    public const string Exchange = "https://platform.claude.com/v1/oauth/token";
    public const string Scope = "user:inference";
    public const int Lifetime = 365 * 24 * 60 * 60;
    /// Long enough to sign in from scratch, two-step verification included.
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    public sealed class Failure(string message) : Exception(message);

    /// The account on success; throws Failure with a sentence for the person,
    /// or OperationCanceledException when they cancelled.
    public static async Task<PoolAccount> Run(string? ownAccount, Action<string> openBrowser, CancellationToken cancel)
    {
        // localhost is whichever of the two a browser tries first.
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        var port = ((IPEndPoint)v4.LocalEndpoint).Port;
        TcpListener? v6 = null;
        try { v6 = new TcpListener(IPAddress.IPv6Loopback, port); v6.Start(); }
        catch (SocketException) { v6 = null; }
        try
        {
            var verifier = RandomToken();
            var state = RandomToken();
            var redirect = $"http://localhost:{port}/callback";
            openBrowser(AuthorizeUrl(redirect, verifier, state));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(Patience);
            var accepts = new List<Task<TcpClient>> { v4.AcceptTcpClientAsync(deadline.Token).AsTask() };
            if (v6 is not null) accepts.Add(v6.AcceptTcpClientAsync(deadline.Token).AsTask());
            while (true)
            {
                Task<TcpClient> done;
                try
                {
                    done = await Task.WhenAny(accepts);
                    await done;
                }
                catch (OperationCanceledException)
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new Failure("Nothing came back from the browser, so the sign-in was abandoned.");
                }
                var index = accepts.IndexOf(done);
                accepts[index] = (index == 0 ? v4 : v6!).AcceptTcpClientAsync(deadline.Token).AsTask();
                using var connection = done.Result;
                var stream = connection.GetStream();
                var query = await CallbackQuery(stream);
                if (query is null)
                {
                    await Respond(stream, 404, null);
                    continue;
                }
                // Anything without this sign-in's state came from somewhere
                // else: a stale tab, or a page poking at the port.
                if (query.GetValueOrDefault("state") != state)
                {
                    await Respond(stream, 400, Page("This sign-in link is out of date. Start again from Claude Graft."));
                    continue;
                }
                if (query.GetValueOrDefault("code") is not string code)
                {
                    var reason = query.GetValueOrDefault("error_description") ?? query.GetValueOrDefault("error");
                    var message = reason is null ? "The sign-in was not completed." : $"The sign-in was not completed: {reason}";
                    await Respond(stream, 200, Page(message));
                    throw new Failure(message);
                }
                try
                {
                    var account = await Finish(code, redirect, verifier, state, ownAccount, cancel);
                    await Respond(stream, 200, Page($"{account.Name} is signed in. You can close this tab and go back to Claude Graft."));
                    return account;
                }
                catch (Failure f)
                {
                    await Respond(stream, 200, Page(f.Message));
                    throw;
                }
            }
        }
        finally
        {
            v4.Stop();
            v6?.Stop();
        }
    }

    private static async Task<PoolAccount> Finish(string code, string redirect, string verifier, string state, string? ownAccount, CancellationToken cancel)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
            ["client_id"] = ClientId, ["code_verifier"] = verifier, ["state"] = state, ["expires_in"] = Lifetime,
        });
        // No cookies, no cache, no proxy of the machine's: the code goes to the
        // sign-in service and nowhere else.
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, Exchange) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancel); }
        catch (HttpRequestException e) { throw new Failure($"Anthropic's sign-in service could not be reached: {e.Message}"); }
        using var _ = response;
        var text = await response.Content.ReadAsStringAsync(cancel);
        JsonElement? root = null;
        try { root = JsonDocument.Parse(text).RootElement; } catch (JsonException) { }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var reason = Str(root, "error_description") ?? (root?.TryGetProperty("error", out var e) == true ? Str(e, "message") : null);
            throw new Failure(reason is null ? $"Anthropic did not accept the sign-in ({(int)response.StatusCode})." : $"Anthropic did not accept the sign-in: {reason}");
        }
        if (Str(root, "access_token") is not { Length: > 0 } token) throw new Failure("Anthropic's answer to the sign-in could not be read.");
        JsonElement? account = root?.TryGetProperty("account", out var a) == true ? a : null;
        JsonElement? organization = root?.TryGetProperty("organization", out var o) == true ? o : null;
        var uuid = Str(account, "uuid");
        if (uuid is not null && uuid == ownAccount)
            throw new Failure("That is the account this Claude already uses, so it has nothing to add. Switch accounts in the browser, then try again.");
        var lifetime = root?.TryGetProperty("expires_in", out var x) == true && x.TryGetDouble(out var seconds) ? seconds : Lifetime;
        return new PoolAccount
        {
            Id = PoolAccount.Prefix + (uuid ?? Guid.NewGuid().ToString()),
            AccountUuid = uuid,
            Email = Str(account, "email_address"),
            OrganizationName = Str(organization, "name"),
            Scopes = Str(root, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [Scope],
            Expires = DateTimeOffset.UtcNow.AddSeconds(lifetime),
            Added = DateTimeOffset.UtcNow,
            Sealed = PoolAccounts.Seal(token),
        };
    }

    private static string? Str(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string AuthorizeUrl(string redirect, string verifier, string state)
    {
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new (string, string)[]
        {
            ("code", "true"), ("client_id", ClientId), ("response_type", "code"), ("redirect_uri", redirect),
            ("scope", Scope), ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", state),
        };
        return Authorize + "?" + string.Join("&", query.Select(q => q.Item1 + "=" + Uri.EscapeDataString(q.Item2)));
    }

    public static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// The query of a GET /callback, or null for anything else: a browser asks
    /// for a favicon on the same port.
    public static async Task<Dictionary<string, string>?> CallbackQuery(Stream stream)
    {
        var collected = new List<byte>();
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (collected.Count < 16384)
            {
                var n = await stream.ReadAsync(buffer, timeout.Token);
                if (n == 0) break;
                collected.AddRange(buffer.AsSpan(0, n).ToArray());
                if (Encoding.ASCII.GetString(collected.ToArray()).Contains("\r\n\r\n")) break;
            }
        }
        catch (OperationCanceledException) { return null; }
        return ParseCallback(Encoding.UTF8.GetString(collected.ToArray()));
    }

    public static Dictionary<string, string>? ParseCallback(string head)
    {
        var parts = head.Split("\r\n", 2)[0].Split(' ');
        if (parts.Length < 2 || parts[0] != "GET" || !Uri.TryCreate("http://localhost" + parts[1], UriKind.Absolute, out var uri)
            || uri.AbsolutePath != "/callback") return null;
        var query = new Dictionary<string, string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
            if (!query.ContainsKey(key)) query[key] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return query;
    }

    private static async Task Respond(Stream stream, int status, string? page)
    {
        var body = Encoding.UTF8.GetBytes(page ?? "");
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        try { await stream.WriteAsync((byte[])[.. Encoding.ASCII.GetBytes(head), .. body]); }
        catch (IOException) { }
    }

    public static string Page(string message)
    {
        var escaped = WebUtility.HtmlEncode(message);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>Claude Graft</title>" +
               "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><style>" +
               ":root{color-scheme:light dark}body{font:15px 'Segoe UI',system-ui,sans-serif;margin:0;min-height:100vh;" +
               "display:grid;place-items:center;background:Canvas;color:CanvasText}p{max-width:28em;padding:0 24px;line-height:1.45;text-align:center}" +
               $"</style></head><body><p>{escaped}</p></body></html>";
    }
}
