using System.Formats.Asn1;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace ClaudeGraft.Core;

public static class PoolStorage
{
    public static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) return;
        SetPrivateAccess(path);
    }

    [SupportedOSPlatform("windows")]
    private static void SetPrivateAccess(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("The Windows account could not be identified.");
        var security = new DirectorySecurity();
        // Setting an owner requires a privilege a normal per-user installation
        // need not hold. The private DACL is sufficient; preserve the existing owner.
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static byte[] Protect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Protect(data, "ClaudeGraft.UsagePool.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
    }
    public static byte[] Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Unprotect(data, "ClaudeGraft.UsagePool.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
    }

    public static void Write(string path, byte[] data)
    {
        var directory = Path.GetDirectoryName(path)!;
        EnsurePrivateDirectory(directory);
        AtomicWrite(path, data);
    }

    internal static void AtomicWrite(string path, byte[] data)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".graft-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(data); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>The authority signs exactly one host, then its private key is discarded. No certificate is installed in a trust store.</summary>
public static class PoolCertificates
{
    public static X509Certificate2 Ensure()
    {
        PoolStorage.EnsurePrivateDirectory(UsagePool.Directory);
        try
        {
            var existing = Load();
            if (existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(60) && File.Exists(UsagePool.CertificateFile)) return existing;
            existing.Dispose();
        }
        catch (Exception e) when (e is IOException or CryptographicException) { }
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Claude Graft relay for api.anthropic.com", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        var constraints = new AsnWriter(AsnEncodingRules.DER);
        constraints.PushSequence();
        var permitted = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        constraints.PushSequence(permitted); constraints.PushSequence();
        constraints.WriteCharacterString(UniversalTagNumber.IA5String, UsagePool.Host, new Asn1Tag(TagClass.ContextSpecific, 2));
        constraints.PopSequence(); constraints.PopSequence(permitted); constraints.PopSequence();
        caRequest.CertificateExtensions.Add(new X509Extension("2.5.29.30", constraints.Encode(), true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + UsagePool.Host, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(UsagePool.Host); request.CertificateExtensions.Add(san.Build());
        using var signed = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(800), RandomNumberGenerator.GetBytes(16));
        using var leaf = signed.CopyWithPrivateKey(leafKey);
        var privateBytes = leaf.Export(X509ContentType.Pkcs12);
        try { PoolStorage.Write(UsagePool.IdentityFile, PoolStorage.Protect(privateBytes)); }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
        PoolStorage.Write(UsagePool.CertificateFile, System.Text.Encoding.ASCII.GetBytes(ca.ExportCertificatePem()));
        return Load();
    }

    public static X509Certificate2 Load()
    {
        var bytes = PoolStorage.Unprotect(File.ReadAllBytes(UsagePool.IdentityFile));
        try
        {
            // Schannel cannot serve an ephemeral key. UserKeySet without PersistKeySet
            // creates a temporary user key which the certificate's disposal removes.
            return X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.UserKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
