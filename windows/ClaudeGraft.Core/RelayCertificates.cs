using System.Formats.Asn1;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClaudeGraft.Core;

/// <summary>
/// The authority Claude Code is told to trust through NODE_EXTRA_CA_CERTS, and
/// the one certificate it signs. A critical name constraint lets it vouch for
/// api.anthropic.com alone, and its key is never stored, so nothing else can
/// ever be signed by it. Made only by the relay holding the mutex, as it starts.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RelayCertificates
{
    /// A session started before a renewal trusts only the old authority, so
    /// renewal comes rarely: two months before an 800-day leaf runs out.
    public static readonly TimeSpan Renewal = TimeSpan.FromDays(60);
    public static readonly TimeSpan LeafLife = TimeSpan.FromDays(800);

    /// The identity to serve, made or renewed first if it has to be.
    public static X509Certificate2 Identity(string keyName = UsagePool.KeyName, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var leaf = ReadLeaf();
        var key = OpenKey(keyName);
        if (leaf is null || key is null || leaf.NotAfter.ToUniversalTime() - at.UtcDateTime < Renewal
            || !SameKey(leaf, key))
        {
            leaf?.Dispose();
            key?.Dispose();
            (leaf, key) = Make(keyName, at);
        }
        using var ecdsa = new ECDsaCng(key);
        var identity = leaf.CopyWithPrivateKey(ecdsa);
        leaf.Dispose();
        key.Dispose();
        return identity;
    }

    /// Whether a certificate is one this authority signed, which is how a
    /// test asks that the pair on disk belong together.
    public static bool SignedByAuthority(X509Certificate2 leaf)
    {
        using var ca = X509CertificateLoader.LoadCertificate(PemBody(File.ReadAllText(UsagePool.CertificateFile)));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    public static void DeleteKey(string keyName = UsagePool.KeyName)
    {
        using var key = OpenKey(keyName);
        key?.Delete();
    }

    private static (X509Certificate2 Leaf, CngKey Key) Make(string keyName, DateTimeOffset now)
    {
        System.IO.Directory.CreateDirectory(UsagePool.Directory);
        X509Certificate2 ca;
        using (var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var request = new CertificateRequest("CN=Claude Graft relay for " + UsagePool.Host, caKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509Extension("2.5.29.30", PermitOnly(UsagePool.Host), critical: true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            ca = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(3650));
        }

        var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
        {
            KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None,
        });
        X509Certificate2 leaf;
        using (var leafKey = new ECDsaCng(key))
        {
            var request = new CertificateRequest("CN=" + UsagePool.Host, leafKey, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(UsagePool.Host);
            request.CertificateExtensions.Add(san.Build(false));
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7f;
            leaf = request.Create(ca, now.AddMinutes(-5), now.Add(LeafLife), serial);
        }

        // The authority last: until it lands a reader sees the old pair or
        // none, never a new authority beside an old certificate.
        Replace(UsagePool.LeafFile, leaf.ExportCertificatePem() + "\n");
        Replace(UsagePool.CertificateFile, ca.ExportCertificatePem() + "\n");
        ca.Dispose();
        Diagnostics.Note("relay.certificate-made", new Dictionary<string, object?> { ["until"] = leaf.NotAfter.ToString("u") });
        return (leaf, key);
    }

    private static X509Certificate2? ReadLeaf()
    {
        try
        {
            if (!File.Exists(UsagePool.LeafFile) || !File.Exists(UsagePool.CertificateFile)) return null;
            return X509CertificateLoader.LoadCertificate(PemBody(File.ReadAllText(UsagePool.LeafFile)));
        }
        catch (Exception e) when (e is IOException or CryptographicException or FormatException) { return null; }
    }

    private static CngKey? OpenKey(string keyName)
    {
        try
        {
            return CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)
                ? CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)
                : null;
        }
        catch (CryptographicException) { return null; }
    }

    private static bool SameKey(X509Certificate2 leaf, CngKey key)
    {
        using var mine = new ECDsaCng(key);
        using var theirs = leaf.GetECDsaPublicKey();
        return theirs is not null
            && mine.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(theirs.ExportSubjectPublicKeyInfo());
    }

    private static byte[] PermitOnly(string dns)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
        using (w.PushSequence())
            w.WriteCharacterString(UniversalTagNumber.IA5String, dns, new Asn1Tag(TagClass.ContextSpecific, 2));
        return w.Encode();
    }

    private static byte[] PemBody(string pem) =>
        PemEncoding.TryFind(pem, out var fields) ? Convert.FromBase64String(pem[fields.Base64Data]) : throw new FormatException();

    private static void Replace(string path, string text)
    {
        var temp = path + ".new";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
