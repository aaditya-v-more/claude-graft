using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// Borrows the access token Claude Desktop already holds for a profile, so usage
/// can be read for that account from Anthropic's own API.
///
/// Read-only, and deliberately narrow: only the access token is decoded — never
/// the refresh token, since Anthropic rotates those and using one would sign
/// Claude Desktop out; nothing is written back to Claude's config; the token
/// goes to api.anthropic.com and nowhere else.
///
/// The Mac stores the safe-storage key behind a keychain item whose ACL names
/// each trusted build, so the whole app has a keychain-dialog dance around it.
/// Windows keeps the same key in the profile's Local State, DPAPI-wrapped to the
/// logged-in user — which unwraps silently, for this user, with no dialog. So
/// none of the prompting machinery has a counterpart here; it simply works or the
/// profile is not signed in.
/// </summary>
public static class ClaudeCredentials
{
    public const string UsageScope = "user:profile";
    public const string InferenceScope = "user:inference";

    private static readonly string[] CacheKeys = { "oauth:tokenCacheV2", "oauth:tokenCache" };

    public sealed record Token
    {
        public required string Value { get; init; }
        public required DateTimeOffset Expires { get; init; }
        public string? Plan { get; init; }
        public required IReadOnlyList<string> Scopes { get; init; }

        public bool IsCurrent => Expires > DateTimeOffset.UtcNow.AddSeconds(120);

        /// An entry whose scopes could not be read from its cache key is not
        /// ruled out: the key's shape is Claude's to change, and the service
        /// refuses the request anyway if the token really is insufficient.
        public bool Covers(IReadOnlyCollection<string> required) =>
            Scopes.Count == 0 || required.All(Scopes.Contains);
    }

    public enum Failure { NoKey, NotSignedIn, Expired }

    public sealed class CredentialException : Exception
    {
        public Failure Reason { get; }
        public CredentialException(Failure reason) : base(reason.ToString()) => Reason = reason;
    }

    // MARK: - The token

    /// Null when this profile has no login cached at all.
    public static Token? GetToken(string profile, IReadOnlyList<string>? requiring = null)
    {
        var required = requiring ?? new[] { UsageScope };
        var config = Graft.ConfigJson(profile);
        var encoded = CacheKeys
            .Select(k => config[k]?.GetValue<string>())
            .FirstOrDefault(v => v is not null);
        if (encoded is null) return null;

        byte[] blob;
        try { blob = Convert.FromBase64String(encoded); }
        catch { return null; }

        var key = SafeStorageKeyGuarded(profile) ?? throw new CredentialException(Failure.NoKey);
        var plain = Decrypt(blob, key) ?? throw new CredentialException(Failure.NotSignedIn);

        return BestToken(plain, required) ?? throw new CredentialException(Failure.Expired);
    }

    /// The best current token in a decrypted cache, or null if none qualifies —
    /// pulled out so it can be tested without any live crypto.
    public static Token? BestToken(byte[] plaintext, IReadOnlyList<string> required)
    {
        // The document owns the memory its elements point into, so it is held
        // alive for the whole walk — a RootElement outliving its JsonDocument
        // reads freed buffers and finds nothing, nondeterministically.
        using JsonDocument? doc = Parse(plaintext);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        Token? best = null;
        foreach (var pair in doc.RootElement.EnumerateObject())
        {
            var entry = pair.Value;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("token", out var t) || t.ValueKind != JsonValueKind.String) continue;
            if (!entry.TryGetProperty("expiresAt", out var e) || e.ValueKind != JsonValueKind.Number) continue;

            var candidate = new Token
            {
                Value = t.GetString()!,
                Expires = DateTimeOffset.FromUnixTimeMilliseconds((long)e.GetDouble()),
                Plan = entry.TryGetProperty("subscriptionType", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : null,
                Scopes = ScopesIn(pair.Name),
            };
            if (!candidate.IsCurrent || !candidate.Covers(required)) continue;
            if (best is null || candidate.Expires > best.Expires) best = candidate;
        }
        return best;
    }

    private static JsonDocument? Parse(byte[] data)
    {
        try { return JsonDocument.Parse(data); }
        catch { return null; }
    }

    /// Scopes are appended to the cache key after the api host.
    public static IReadOnlyList<string> ScopesIn(string composite)
    {
        var marker = composite.IndexOf("https://", StringComparison.Ordinal);
        if (marker < 0) return Array.Empty<string>();
        var tail = composite[(marker + "https://".Length)..];
        var separator = tail.IndexOf(':');
        if (separator < 0) return Array.Empty<string>();
        return tail[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // MARK: - The crypto

    /// Chromium's Windows safe-storage format: <c>v10</c>, a 12-byte nonce, the
    /// ciphertext, and a 16-byte GCM tag. AES-256-GCM with the key from Local
    /// State — where the Mac uses AES-128-CBC with a fixed IV and a PBKDF2 key.
    public static byte[]? Decrypt(byte[] blob, byte[] key)
    {
        const int nonceLen = 12, tagLen = 16, prefixLen = 3;
        if (blob.Length < prefixLen + nonceLen + tagLen) return null;
        if (blob[0] != (byte)'v' || blob[1] != (byte)'1' || blob[2] != (byte)'0') return null;

        var nonce = blob.AsSpan(prefixLen, nonceLen);
        var tag = blob.AsSpan(blob.Length - tagLen, tagLen);
        var cipher = blob.AsSpan(prefixLen + nonceLen, blob.Length - prefixLen - nonceLen - tagLen);
        var plain = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, tagLen);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    /// The AES key Chromium wraps the token with, read from the profile's own
    /// Local State and unwrapped through DPAPI. The stored value is base64 of
    /// <c>DPAPI</c> followed by the wrapped key; DPAPI unwraps it silently for the
    /// logged-in user, which is why Windows needs none of the Mac's dialog rules.
    [SupportedOSPlatform("windows")]
    public static byte[]? SafeStorageKey(string profile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(profile, "Local State")));
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)
                || !osCrypt.TryGetProperty("encrypted_key", out var ek)
                || ek.ValueKind != JsonValueKind.String)
                return null;
            var wrapped = Convert.FromBase64String(ek.GetString()!);
            var prefix = "DPAPI"u8;
            if (wrapped.Length <= prefix.Length || !wrapped.AsSpan(0, prefix.Length).SequenceEqual(prefix))
                return null;
            return ProtectedData.Unprotect(wrapped[prefix.Length..], null, DataProtectionScope.CurrentUser);
        }
        catch { return null; }
    }

    // A GetToken guarded so the cross-platform Core still compiles; the DPAPI key
    // read is the only Windows-only step and is reached only here.
    private static byte[]? SafeStorageKeyGuarded(string profile) =>
        OperatingSystem.IsWindows() ? SafeStorageKey(profile) : null;
}
