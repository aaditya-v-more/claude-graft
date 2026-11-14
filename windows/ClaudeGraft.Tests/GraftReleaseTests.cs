using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeGraft.Platform;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Xunit;

namespace ClaudeGraft.Tests;

public class GraftReleaseTests
{
    [Fact]
    public void AcceptsOnlyCompleteStableReleaseForThisArchitecture()
    {
        object Release(string tag, string arch = "x64", bool draft = false, bool prerelease = false, bool signed = true)
        {
            var name = $"ClaudeGraft-{tag[1..]}-windows-{arch}-setup.exe";
            var assets = new List<object> { new { name, size = 123 }, new { name = name + ".sha256" } };
            if (signed) assets.Add(new { name = name + ".sha256.sig" });
            return new { tag_name = tag, draft, prerelease, assets };
        }
        var releases = JsonSerializer.SerializeToElement(new[]
        {
            Release("v3.2.0", prerelease: true), Release("v3.0.0", draft: true),
            Release("v2.8.0", "arm64"), Release("v2.7.0", signed: false),
            Release("v2.6.0"), Release("v2.5.0")
        });
        Assert.Equal(new Version(2, 6, 0), GraftRelease.Select(releases, new(2, 5, 9, 0), "x64")!.Version);
        Assert.Null(GraftRelease.Select(releases, new(2, 6, 0, 0), "x64"));
    }

    [Fact]
    public void SignatureBindsTheExactDigestFilenameVersionAndArchitecture()
    {
        const string name = "ClaudeGraft-2.3.4-windows-x64-setup.exe";
        var digest = SHA256.HashData("installer"u8);
        var manifest = Encoding.ASCII.GetBytes($"{Convert.ToHexString(digest)}  {name}\r\n");
        var secret = RandomNumberGenerator.GetBytes(Ed25519.SecretKeySize);
        var publicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(secret, 0, publicKey, 0);
        var sig = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(secret, 0, manifest, 0, manifest.Length, sig, 0);
        var signature = Convert.ToBase64String(sig);
        Assert.Equal(digest, GraftRelease.VerifyManifest(name, manifest, signature, publicKey));
        Assert.Throws<CryptographicException>(() => GraftRelease.VerifyManifest(name.Replace("x64", "arm64"), manifest, signature, publicKey));
        Assert.Throws<CryptographicException>(() => GraftRelease.VerifyManifest(name.Replace("2.3.4", "2.4.0"), manifest, signature, publicKey));
        Assert.Throws<CryptographicException>(() => GraftRelease.VerifyManifest(name, manifest, signature, GraftRelease.ReadReleaseKey()));
        manifest[0] ^= 1;
        Assert.Throws<CryptographicException>(() => GraftRelease.VerifyManifest(name, manifest, signature, publicKey));
    }

    [Fact]
    public async Task RejectsInstallerBytesThatDifferFromTheSignedChecksum()
    {
        byte[] payload = "published installer"u8.ToArray();
        var expectedHash = SHA256.HashData(payload);
        using var original = new MemoryStream(payload);
        await GraftRelease.VerifyInstallerAsync(original, expectedHash, CancellationToken.None);
        payload[^1] ^= 1;
        using var changed = new MemoryStream(payload);
        await Assert.ThrowsAsync<CryptographicException>(() =>
            GraftRelease.VerifyInstallerAsync(changed, expectedHash, CancellationToken.None));
    }
}
