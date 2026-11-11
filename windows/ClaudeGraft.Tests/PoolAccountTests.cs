using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public class PoolAccountTests
{
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation); }
    [Fact] public void AuthorizationIsInferenceOnlyPkceWithEscapedRedirectAndState()
    {
        var values = Query(PoolAccountSignIn.AuthorizeUrl("http://localhost:1234/callback", "verifier", "state+with/slash"));
        Assert.Equal("S256", values["code_challenge_method"]); Assert.Equal("user:inference", values["scope"]);
        Assert.Equal("state+with/slash", values["state"]); Assert.Equal("http://localhost:1234/callback", values["redirect_uri"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData("verifier"u8.ToArray())).TrimEnd('=').Replace('+', '-').Replace('/', '_'), values["code_challenge"]);
    }
    [Fact] public async Task OAuthRejectsWrongStateThenSavesOnlyTheAccessTokenSealedForThisUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir(); var previous = GraftPaths.ProfilesRootOverride; GraftPaths.ProfilesRootOverride = temp.Path;
        try
        {
            var calls = 0;
            using var endpoint = new HttpClient(new Handler(async (request, ct) =>
            {
                Interlocked.Increment(ref calls); Assert.Equal(PoolAccountSignIn.TokenEndpoint, request.RequestUri!.AbsoluteUri);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));
                Assert.Equal("authorization_code", payload.RootElement.GetProperty("grant_type").GetString());
                Assert.Equal("fixture-code", payload.RootElement.GetProperty("code").GetString());
                Assert.True(payload.RootElement.GetProperty("code_verifier").GetString()!.Length >= 43);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"saved-access-fixture\",\"refresh_token\":\"never-save-fixture\",\"expires_in\":3600,\"scope\":\"user:inference\",\"account\":{\"uuid\":\"lent-account\",\"email_address\":\"fixture@example.invalid\"}}", Encoding.UTF8, "application/json") };
            }));
            Task? browser = null;
            var account = await PoolAccountSignIn.SignInAsync(uri =>
            {
                var query = Query(uri);
                browser = Task.Run(async () =>
                {
                    using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                    var redirect = query["redirect_uri"];
                    using var wrong = await http.GetAsync(redirect + "?code=wrong&state=wrong"); Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode); Assert.Equal(0, calls);
                    using var result = await http.GetAsync(redirect + "?code=fixture-code&state=" + Uri.EscapeDataString(query["state"])); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
                });
            }, "sender-account", endpoint, CancellationToken.None);
            await browser!; Assert.Equal(1, calls); Assert.Equal("saved-access-fixture", account.Token());
            PoolAccount.Save([account]); var file = File.ReadAllText(UsagePool.AccountsFile);
            Assert.DoesNotContain("saved-access-fixture", file); Assert.DoesNotContain("never-save-fixture", file); Assert.Equal("saved-access-fixture", Assert.Single(PoolAccount.Load()).Token());
        }
        finally { GraftPaths.ProfilesRootOverride = previous; }
    }
    [Fact] public async Task CancelledSignInClosesTheLoopbackListenerWithoutContactingTheService()
    {
        using var cancelled = new CancellationTokenSource(); var calls = 0; int port = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; throw new InvalidOperationException(); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PoolAccountSignIn.SignInAsync(uri =>
        { port = new Uri(Query(uri)["redirect_uri"]).Port; cancelled.Cancel(); }, null, http, cancelled.Token));
        Assert.Equal(0, calls);
        using var socket = new System.Net.Sockets.TcpClient();
        await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(async () => await socket.ConnectAsync(IPAddress.Loopback, port));
    }
    [Fact] public void BorrowersOwnAccountCannotBeSavedAsExtraAllowance()
    {
        using var json = JsonDocument.Parse("{\"access_token\":\"fixture\",\"account\":{\"uuid\":\"own\"}}");
        Assert.Throws<IOException>(() => PoolAccountSignIn.ParseAccount(json.RootElement, "own"));
    }
}
