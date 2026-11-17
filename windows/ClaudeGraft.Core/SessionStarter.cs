using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// Opens a five-hour window on one account by sending it a single short message.
/// Nothing appears on screen; the account's own borrowed token pays for it, the
/// request goes to Anthropic and nowhere else, and nothing is written back.
///
/// The Windows echo of the Mac's SessionStarter, without the keychain dance:
/// DPAPI reads the token silently, so there is no prompt to gate on. What
/// carries over is the rule that only a person starts a session — the button is
/// the one caller — and the per-account claim, so the two ways the same account
/// can be pressed while a start is still in flight do not send a second message
/// to open a window that is already opening.
/// </summary>
public static class SessionStarter
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string Model = "claude-haiku-4-5-20251001";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// Null means the window is open. Anything else is a line to show the person
    /// who pressed the button, in the words the Mac dropdown uses.
    public static async Task<string?> StartAsync(string profile, string prompt = "hi")
    {
        if (!Claim(profile)) return "A session is already being started for this account.";
        try
        {
            string token;
            try
            {
                if (ClaudeCredentials.GetToken(profile, new[] { ClaudeCredentials.InferenceScope })
                    is not ClaudeCredentials.Token t)
                    return "No login is stored for this profile yet. Open it once and sign in, "
                        + "then a session can be started from here.";
                token = t.Value;
            }
            catch (ClaudeCredentials.CredentialException)
            {
                return "That profile's login could not be read. Open it once so Claude can renew it.";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            // These tokens are minted for Claude Code, and the service expects to
            // see its system prompt on requests made with them.
            var body = new
            {
                model = Model,
                max_tokens = 4,
                system = new[]
                {
                    new { type = "text", text = "You are Claude Code, Anthropic's official CLI for Claude." },
                },
                messages = new[] { new { role = "user", content = prompt } },
            };
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try { response = await Http.SendAsync(request).ConfigureAwait(false); }
            catch (Exception e) { return "Could not reach Anthropic: " + e.Message; }

            using (response)
            {
                if (response.IsSuccessStatusCode) return null;
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Describe((int)response.StatusCode, payload);
            }
        }
        finally
        {
            Release(profile);
        }
    }

    /// The line for a refusal, told apart the way the Mac's Failure is: the two
    /// the person can act on named plainly, and anything else handed back with
    /// whatever Anthropic said.
    public static string Describe(int status, string? payload) => status switch
    {
        401 or 403 => "That profile's login was refused. Open it once so Claude can renew it.",
        429 => "That account is rate-limited right now.",
        _ => Message(payload) is { Length: > 0 } detail
            ? $"Anthropic answered {status}: {detail}"
            : $"Anthropic answered {status}.",
    };

    /// Anthropic returns <c>{ "error": { "message": … } }</c> on a refusal.
    public static string Message(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return "";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? "";
        }
        catch { }
        return "";
    }

    // MARK: - One at a time, per account

    private static readonly object ClaimLock = new();
    private static readonly HashSet<string> Claimed = new(StringComparer.OrdinalIgnoreCase);

    private static bool Claim(string profile)
    {
        lock (ClaimLock) return Claimed.Add(Path.GetFullPath(profile));
    }

    private static void Release(string profile)
    {
        lock (ClaimLock) Claimed.Remove(Path.GetFullPath(profile));
    }
}
