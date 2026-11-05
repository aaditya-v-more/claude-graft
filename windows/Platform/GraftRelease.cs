using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace ClaudeGraft.Platform;

/// GitHub hosts the artifacts. The release key embedded from the Mac app's Info.plist
/// authenticates the exact Windows installer filename and digest before it can run.
internal sealed record GraftRelease(Version Version, string Filename, long Length)
{
    private const string Repository = "aaditya-v-more/claude-graft";
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        MaxResponseContentBufferSize = 4 * 1024 * 1024,
        DefaultRequestHeaders = { UserAgent = { new("ClaudeGraft-Windows", "1.0") } }
    };

    public static async Task<GraftRelease?> FindAsync(Version current, string arch, CancellationToken token)
    {
        // A series of Mac-only tags must not hide the newest published Windows build.
        for (int page = 1; ; page++)
        {
            var data = await Client.GetByteArrayAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30&page={page}", token);
            using var releases = JsonDocument.Parse(data);
            var candidate = Select(releases.RootElement, current, arch);
            if (candidate is not null || releases.RootElement.GetArrayLength() < 30) return candidate;
        }
    }

    internal static GraftRelease? Select(JsonElement releases, Version current, string arch)
    {
        if (arch is not ("x64" or "arm64")) throw new PlatformNotSupportedException();
        GraftRelease? newest = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString();
            if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version)
                || version.Build < 0 || version.Revision >= 0 || tag != $"v{version}"
                || version <= current || (newest is not null && version <= newest.Version)) continue;
            var name = $"ClaudeGraft-{version}-windows-{arch}-setup.exe";
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var installer = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString() == name);
            if (installer.ValueKind == JsonValueKind.Undefined) continue; // A Mac-only release.
            var length = installer.GetProperty("size").GetInt64();
            if (length <= 0 || length > 768L * 1024 * 1024) throw new InvalidDataException("Invalid installer size.");
            if (!assets.Any(asset => asset.GetProperty("name").GetString() == name + ".sha256.sig") ||
                !assets.Any(asset => asset.GetProperty("name").GetString() == name + ".sha256")) continue;
            newest = new(version, name, length);
        }
        return newest;
    }

    private string Asset(string suffix) =>
        $"https://github.com/{Repository}/releases/download/v{Version}/{Filename}{suffix}";

    internal static byte[] VerifyManifest(string expectedName, byte[] manifest, string signature, byte[] key)
    {
        var sig = Convert.FromBase64String(signature);
        if (key.Length != Ed25519.PublicKeySize || sig.Length != Ed25519.SignatureSize || manifest.Length > 512 ||
            !Ed25519.Verify(sig, 0, key, 0, manifest, 0, manifest.Length))
            throw new CryptographicException("The Windows update signature is invalid.");
        var line = Encoding.ASCII.GetString(manifest).TrimEnd('\r', '\n');
        if (line.Length < 66 || line[64..] != "  " + expectedName)
            throw new CryptographicException("The signed installer name does not match this version and architecture.");
        return Convert.FromHexString(line[..64]);
    }

    internal static byte[] ReadReleaseKey()
    {
        using var stream = typeof(GraftRelease).Assembly.GetManifestResourceStream("Graft.ReleaseTrust")
            ?? throw new InvalidDataException("Missing release trust configuration.");
        var key = XDocument.Load(stream).Descendants("key").Single(item => item.Value == "SUPublicEDKey");
        return Convert.FromBase64String(key.ElementsAfterSelf("string").First().Value);
    }

    internal static async Task VerifyInstallerAsync(Stream installer, byte[] expectedHash, CancellationToken token)
    {
        var actualHash = await SHA256.HashDataAsync(installer, token);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new CryptographicException("The Windows installer checksum is invalid.");
    }

    public async Task<byte[]> DownloadAsync(string path, CancellationToken token)
    {
        var manifest = await Client.GetByteArrayAsync(Asset(".sha256"), token);
        var signature = await Client.GetStringAsync(Asset(".sha256.sig"), token);
        var digest = VerifyManifest(Filename, manifest, signature, ReadReleaseKey());
        using var response = await Client.GetAsync(Asset(""), HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long size && size != Length)
            throw new InvalidDataException("Unexpected Windows installer length.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[128 * 1024];
        long bytes = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            bytes += count;
            if (bytes > Length) throw new InvalidDataException("Unexpected Windows installer length.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (bytes != Length) throw new EndOfStreamException("Incomplete Windows installer.");
        return digest;
    }
}
