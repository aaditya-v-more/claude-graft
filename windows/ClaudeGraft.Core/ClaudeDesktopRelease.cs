using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ClaudeGraft.Core;

public sealed record ClaudeDesktopRelease(Version Version, Uri Download)
{
    public static ClaudeDesktopRelease Parse(string json, string architecture)
    {
        using var doc = JsonDocument.Parse(json);
        var versionText = doc.RootElement.GetProperty("version").GetString();
        var url = doc.RootElement.GetProperty("url").GetString();
        if (architecture is not ("x64" or "arm64") || !Version.TryParse(versionText, out var version)
            || version.Build < 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "downloads.claude.ai" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0
            || !uri.AbsolutePath.StartsWith($"/releases/win32/{architecture}/{versionText}/", StringComparison.Ordinal)
            || !uri.AbsolutePath.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Claude's update service returned an unrecognized package.");
        return new(new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision)), uri);
    }

    public void CheckPackage(Stream file, string publisher, string architecture)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = zip.GetEntry("AppxManifest.xml");
        if (manifest is null || manifest.Length > 2_000_000 || zip.GetEntry("AppxSignature.p7x") is null)
            throw new IOException("The download is missing its signed MSIX manifest or signature.");
        using var stream = manifest.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 2_000_000 });
        XNamespace schema = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var root = XDocument.Load(reader).Root;
        var id = root?.Name == schema + "Package" ? root.Element(schema + "Identity") : null;
        if ((string?)id?.Attribute("Name") != "Claude" || (string?)id.Attribute("Publisher") != publisher
            || (string?)id.Attribute("ProcessorArchitecture") != architecture
            || !Version.TryParse((string?)id.Attribute("Version"), out var packaged) || packaged != Version)
            throw new IOException("This package does not match the installed Claude publisher, architecture and offered version.");
    }
}

public sealed record ClaudeUpdateProcess(int Pid, long Started, string Profile)
{
    public static void RequireConsent(IReadOnlyCollection<ClaudeUpdateProcess> approved, IEnumerable<ClaudeUpdateProcess> current)
    {
        if (current.Any(p => !approved.Contains(p)))
            throw new IOException("Another Claude opened after confirmation. Check for updates again to review the running profiles.");
    }
}
