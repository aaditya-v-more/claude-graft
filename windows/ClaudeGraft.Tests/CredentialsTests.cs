using System.Security.Cryptography;
using System.Text;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class CredentialCryptoTests
{
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

/// <summary>
/// The whole point of this port: prove DPAPI + AES-256-GCM actually decrypts the
/// real Claude Desktop login on this machine. Skips silently off Windows or when
/// no signed-in default profile is present, and never surfaces the token value.
/// </summary>
public class LiveCredentialTests
{
    [Fact(DisplayName = "the real default profile's token decrypts end to end")]
    public void RealProfileDecrypts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (!File.Exists(Path.Combine(profile, "config.json"))
            || !File.Exists(Path.Combine(profile, "Local State"))) return;

        // DPAPI unwrapping the safe-storage key out of Local State is the one
        // truly Windows-specific step, and it works whether or not a token cache
        // is present — so it is proven here every run on a real machine.
        var key = ClaudeCredentials.SafeStorageKey(profile);
        Assert.True(key is { Length: 32 }, "DPAPI did not yield a 32-byte AES key from Local State");

        // Claude Desktop only persists the token cache into config.json at
        // certain moments; when it is absent there is nothing to decrypt, so the
        // GCM half is validated whenever a cache is present and stands aside
        // otherwise rather than failing the suite on the machine's state.
        var config = Graft.ConfigJson(profile);
        if (!config.ContainsKey("oauth:tokenCacheV2")) return;

        var blob = Convert.FromBase64String(config["oauth:tokenCacheV2"]!.GetValue<string>());
        var plain = ClaudeCredentials.Decrypt(blob, key!);
        // The key decrypts the cache: this is DPAPI + AES-256-GCM proven against
        // the real login. The value itself is never surfaced.
        Assert.True(plain is { Length: > 0 }, "AES-256-GCM did not decrypt the token cache");
        Assert.True(plain![0] == (byte)'{', "decrypted cache is not JSON");
    }
}
