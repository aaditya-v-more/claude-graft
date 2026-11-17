using System.Net.Sockets;
using System.Text;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class AccountSignInTests
{
    [Fact(DisplayName = "the browser is sent to sign in with a proof of this sign-in, asking only for model requests")]
    public void AuthorizeUrl()
    {
        if (!OperatingSystem.IsWindows()) return;
        var url = AccountSignIn.AuthorizeUrl("http://localhost:1234/callback", "verifier", "state-1");
        Assert.StartsWith(AccountSignIn.Authorize + "?", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A1234%2Fcallback", url);
        Assert.Contains("scope=user%3Ainference", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.DoesNotContain("verifier", url);
        Assert.Contains("state=state-1", url);
    }

    [Fact(DisplayName = "only GET /callback is read as the browser's answer; a favicon is not")]
    public void Callback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var q = AccountSignIn.ParseCallback("GET /callback?code=abc&state=s%2B1 HTTP/1.1\r\nHost: localhost\r\n\r\n")!;
        Assert.Equal(("abc", "s+1"), (q["code"], q["state"]));
        Assert.Null(AccountSignIn.ParseCallback("GET /favicon.ico HTTP/1.1\r\n\r\n"));
        Assert.Null(AccountSignIn.ParseCallback("POST /callback?code=abc HTTP/1.1\r\n\r\n"));
    }

    [Fact(DisplayName = "a page poking at the port without this sign-in's state is turned away, and a refusal in the browser ends it")]
    public async Task Refusal()
    {
        if (!OperatingSystem.IsWindows()) return;
        var task = AccountSignIn.Run(null, url => _ = Task.Run(async () =>
        {
            var redirect = Uri.UnescapeDataString(url.Split("redirect_uri=")[1].Split('&')[0]);
            var state = url.Split("state=")[1].Split('&')[0];
            var port = new Uri(redirect).Port;
            Assert.Contains("out of date", await Get(port, "/callback?code=x&state=wrong"));
            Assert.Contains("not completed", await Get(port, $"/callback?error=access_denied&state={state}"));
        }), CancellationToken.None);
        var failure = await Assert.ThrowsAsync<AccountSignIn.Failure>(() => task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("access_denied", failure.Message);
    }

    [Fact(DisplayName = "cancelling stops listening at once")]
    public async Task Cancel()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancel = new CancellationTokenSource();
        var task = AccountSignIn.Run(null, _ => cancel.Cancel(), cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<string> Get(int port, string path)
    {
        using var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        using var reader = new StreamReader(s);
        return await reader.ReadToEndAsync();
    }
}

[Collection("GlobalState")]
public sealed class PoolAccountsTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public PoolAccountsTests() => GraftPaths.ProfilesRootOverride = _tmp.Path;
    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _tmp.Dispose();
    }

    private static PoolAccount A(string id, string? uuid, string token) => new()
    {
        Id = id, AccountUuid = uuid, Email = $"{id}@example.com", Expires = DateTimeOffset.UtcNow.AddDays(365), Sealed = PoolAccounts.Seal(token),
    };

    [Fact(DisplayName = "a signed-in account's token is sealed for this Windows user and reads back")]
    public void Seals()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sealedToken = PoolAccounts.Seal("tok");
        Assert.DoesNotContain("tok", Encoding.UTF8.GetString(Convert.FromBase64String(sealedToken)));
        Assert.Equal("tok", PoolAccounts.Unseal(sealedToken));
        Assert.Null(PoolAccounts.Unseal("not sealed"));
    }

    [Fact(DisplayName = "signing in to an account already here replaces its login and keeps its name in every list")]
    public void Replaces()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = PoolAccounts.Add(A("account:1", "u1", "old"));
        var second = PoolAccounts.Add(A("account:other", "u1", "new"));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("new", PoolAccounts.Unseal(Assert.Single(PoolAccounts.Load()).Sealed));
    }

    [Fact(DisplayName = "each account is given a colour once, in the order added, never one a Claude already wears")]
    public void Colours()
    {
        var older = new PoolAccount { Id = "account:1", Sealed = "", Added = DateTimeOffset.UnixEpoch.AddDays(1) };
        var newer = new PoolAccount { Id = "account:2", Sealed = "", Added = DateTimeOffset.UnixEpoch.AddDays(2) };
        var mirrored = new PoolAccount { Id = "account:3", Sealed = "", Added = DateTimeOffset.UnixEpoch };
        var accounts = new List<PoolAccount> { newer, older, mirrored };
        Assert.True(PoolAccounts.Colour(accounts, ["blue"], a => a.Id == "account:3" ? "blue" : null));
        Assert.Equal("blue", accounts[2].IconPreset);
        Assert.Equal("violet", accounts[1].IconPreset);
        Assert.Equal("green", accounts[0].IconPreset);
        Assert.False(PoolAccounts.Colour(accounts, ["violet"], _ => null));
        Assert.Equal("violet", accounts[1].IconPreset);
    }

    [Fact(DisplayName = "signing in again keeps the colour an account was given")]
    public void KeepsColour()
    {
        if (!OperatingSystem.IsWindows()) return;
        PoolAccounts.Save([A("account:1", "u1", "old") with { IconPreset = "teal" }]);
        Assert.Equal("teal", PoolAccounts.Add(A("account:new", "u1", "new")).IconPreset);
    }

    [Fact(DisplayName = "an account nobody draws on any more is forgotten with its login")]
    public void Forgets()
    {
        if (!OperatingSystem.IsWindows()) return;
        PoolAccounts.Add(A("account:1", "u1", "a"));
        PoolAccounts.Add(A("account:2", "u2", "b"));
        PoolAccounts.ForgetUnused(new UsageSharing { Lenders = new() { ["main"] = ["account:2"] } });
        Assert.Equal("account:2", Assert.Single(PoolAccounts.Load()).Id);
    }
}
