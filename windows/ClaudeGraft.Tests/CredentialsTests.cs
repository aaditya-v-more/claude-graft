using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class CredentialCryptoTests
{
    [Fact(DisplayName = "a settings file caught mid-write is retried as unreadable instead of signed out")]
    public void UnreadableConfigIsNotAMissingLogin()
    {
        using var temp = new TempDir();
        Assert.Null(ClaudeCredentials.GetToken(temp.Path));
        var file = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(file, "{");
        var error = Assert.Throws<ClaudeCredentials.CredentialException>(() => ClaudeCredentials.GetToken(temp.Path));
        Assert.Equal(ClaudeCredentials.Failure.ConfigUnreadable, error.Reason);
        Assert.Equal("{", File.ReadAllText(file));
        File.WriteAllText(file, "{}");
        Assert.Null(ClaudeCredentials.GetToken(temp.Path));
    }
    // Build a blob the way Chromium on Windows does: v10, a 12-byte nonce, the
    // ciphertext, and the 16-byte GCM tag.
    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, cipher, tag);
        var blob = new byte[3 + nonce.Length + cipher.Length + tag.Length];
        Encoding.ASCII.GetBytes("v10").CopyTo(blob, 0);
        nonce.CopyTo(blob, 3);
        cipher.CopyTo(blob, 3 + nonce.Length);
        tag.CopyTo(blob, 3 + nonce.Length + cipher.Length);
        return blob;
    }

    [Fact(DisplayName = "a v10 blob round-trips through the same key it was sealed with")]
    public void RoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);   // AES-256
        var secret = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        var blob = Encrypt(secret, key);
        Assert.Equal(secret, ClaudeCredentials.Decrypt(blob, key));
    }

    [Fact(DisplayName = "the Windows user can open a disposable profile's DPAPI-protected token cache")]
    public void ReadsProtectedProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var profile = new TempDir();
        var key = RandomNumberGenerator.GetBytes(32);
        var encryptedKey = Encoding.ASCII.GetBytes("DPAPI")
            .Concat(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)).ToArray();
        profile.Write("Local State", JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String(encryptedKey) } }));
        var cache = new Dictionary<string, object>
        {
            ["client https://api.anthropic.com:user:profile"] = new { token = "fixture-token",
                expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }
        };
        var blob = Encrypt(JsonSerializer.SerializeToUtf8Bytes(cache), key);
        profile.Write("config.json", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["oauth:tokenCacheV2"] = Convert.ToBase64String(blob)
        }));
        Assert.Equal("fixture-token", ClaudeCredentials.GetToken(profile.Path)?.Value);
    }

    [Fact(DisplayName = "the wrong key, a tampered blob, or a missing prefix all decline")]
    public void RejectsBadInput()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var blob = Encrypt(Encoding.UTF8.GetBytes("secret"), key);

        Assert.Null(ClaudeCredentials.Decrypt(blob, RandomNumberGenerator.GetBytes(32))); // wrong key
        var tampered = (byte[])blob.Clone(); tampered[^1] ^= 0xFF;
        Assert.Null(ClaudeCredentials.Decrypt(tampered, key));                            // bad tag
        var noPrefix = Encoding.ASCII.GetBytes("v20").Concat(blob.Skip(3)).ToArray();
        Assert.Null(ClaudeCredentials.Decrypt(noPrefix, key));                            // not v10
        Assert.Null(ClaudeCredentials.Decrypt(new byte[4], key));                         // too short
    }
}

public class TokenSelectionTests
{
    private static long Ms(TimeSpan fromNow) => DateTimeOffset.UtcNow.Add(fromNow).ToUnixTimeMilliseconds();

    // One cache entry: a composite key, a token value, and an expiry in millis.
    private static string Entry(string key, string token, long expiresMs) =>
        "\"" + key + "\": { \"token\": \"" + token + "\", \"expiresAt\": " + expiresMs + " }";

    private static byte[] Cache(params string[] entries) =>
        Encoding.UTF8.GetBytes("{" + string.Join(",", entries) + "}");

    private const string ProfileKey = "client https://api.anthropic.com:user:profile";
    private const string InferenceKey = "client https://api.anthropic.com:user:inference";

    [Fact(DisplayName = "scopes are read off the cache key after the api host")]
    public void ParsesScopes()
    {
        var scopes = ClaudeCredentials.ScopesIn("client https://api.anthropic.com:user:profile user:inference");
        Assert.Equal(new[] { "user:profile", "user:inference" }, scopes);
        Assert.Empty(ClaudeCredentials.ScopesIn("no host here"));
    }

    [Fact(DisplayName = "the current token with the furthest expiry is chosen")]
    public void PicksFurthestCurrent()
    {
        var cache = Cache(
            Entry("a " + ProfileKey, "soon", Ms(TimeSpan.FromHours(1))),
            Entry("b " + ProfileKey, "later", Ms(TimeSpan.FromHours(5))));
        var token = ClaudeCredentials.BestToken(cache, new[] { ClaudeCredentials.UsageScope });
        Assert.NotNull(token);
        Assert.Equal("later", token!.Value);
    }

    [Fact(DisplayName = "an expired token is passed over")]
    public void SkipsExpired()
    {
        var cache = Cache(Entry(ProfileKey, "old", Ms(TimeSpan.FromHours(-1))));
        Assert.Null(ClaudeCredentials.BestToken(cache, new[] { ClaudeCredentials.UsageScope }));
    }

    [Fact(DisplayName = "a token missing a required scope is passed over, but one with no scopes is allowed")]
    public void RespectsScopes()
    {
        var narrow = Cache(Entry(InferenceKey, "t", Ms(TimeSpan.FromHours(1))));
        Assert.Null(ClaudeCredentials.BestToken(narrow, new[] { ClaudeCredentials.UsageScope }));

        // A key with no readable scopes is not ruled out — the service is the
        // real arbiter.
        var scopeless = Cache(Entry("plain-key", "t", Ms(TimeSpan.FromHours(1))));
        Assert.NotNull(ClaudeCredentials.BestToken(scopeless, new[] { ClaudeCredentials.UsageScope }));
    }
}
