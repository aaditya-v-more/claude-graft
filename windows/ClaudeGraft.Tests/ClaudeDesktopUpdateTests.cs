using System.IO.Compression;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public sealed class ClaudeDesktopUpdateTests
{
    private const string Download = "https://downloads.claude.ai/releases/win32/x64/4.20.0/Claude-release.msix";
    private static string Feed(string url) => System.Text.Json.JsonSerializer.Serialize(new { version = "4.20.0", url });

    [Theory]
    [InlineData(true, null, false, ClaudePolicyState.Managed)]
    [InlineData(true, 1, true, ClaudePolicyState.Managed)]
    [InlineData(false, 1, false, ClaudePolicyState.Managed)]
    [InlineData(false, 0, true, ClaudePolicyState.Managed)]
    [InlineData(false, "1", true, ClaudePolicyState.Managed)]
    [InlineData(false, 1, true, ClaudePolicyState.Manual)]
    [InlineData(false, null, false, ClaudePolicyState.Automatic)]
    public void ExistingPolicyIsOnlyReversibleWhenGraftOwnsTheExactValue(bool managed, object? value, bool owns, ClaudePolicyState expected) =>
        Assert.Equal(expected, ClaudeUpdatePolicy.Classify(managed, value, owns));

    [Fact]
    public void AReusedPidAndANewProfileRequireFreshConsent()
    {
        var original = new ClaudeUpdateProcess(21, 100, "Work");
        ClaudeUpdateProcess.RequireConsent([original], []);
        ClaudeUpdateProcess.RequireConsent([original], [original]);
        Assert.Throws<IOException>(() => ClaudeUpdateProcess.RequireConsent([original], [original with { Started = 200 }]));
        Assert.Throws<IOException>(() => ClaudeUpdateProcess.RequireConsent([original], [original with { Pid = 22 }]));
    }

    [Fact]
    public void WindowsRejectsAnUnsignedDownloadBeforeAnyProcessIsAskedToClose()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "Claude.msix");
        File.WriteAllText(path, "This file is not signed.");
        Assert.Throws<IOException>(() =>
        {
            if (OperatingSystem.IsWindows()) WindowsPackageTrust.Verify(path);
        });
    }

    [Theory]
    [InlineData("https://downloads.claude.ai.example.org/releases/win32/x64/4.20.0/Claude.msix")]
    [InlineData("https://downloads.claude.ai/releases/win32/arm64/4.20.0/Claude.msix")]
    [InlineData("https://downloads.claude.ai/releases/win32/x64/4.21.0/Claude.msix")]
    [InlineData("file:///tmp/Claude.msix")]
    public void TheUpdateFeedOnlyAcceptsTheOfferedVersionAndArchitectureFromClaude(string url) =>
        Assert.Throws<IOException>(() => ClaudeDesktopRelease.Parse(Feed(url), "x64"));

    [Theory]
    [InlineData("Claude", "Publisher", "x64", "4.20.0.0", true, true)]
    [InlineData("Other", "Publisher", "x64", "4.20.0.0", true, false)]
    [InlineData("Claude", "Other", "x64", "4.20.0.0", true, false)]
    [InlineData("Claude", "Publisher", "arm64", "4.20.0.0", true, false)]
    [InlineData("Claude", "Publisher", "x64", "4.19.0.0", true, false)]
    [InlineData("Claude", "Publisher", "x64", "4.20.0.0", false, false)]
    public void PackagePreflightChecksTheExactInstalledIdentityBeforeAskingWindowsToVerifyItsSignature(
        string name, string publisher, string arch, string version, bool signature, bool matches)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (signature) zip.CreateEntry("AppxSignature.p7x");
            using var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open());
            writer.Write($"""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="{name}" Publisher="{publisher}" ProcessorArchitecture="{arch}" Version="{version}"/></Package>""");
        }
        bytes.Position = 0;
        var offer = ClaudeDesktopRelease.Parse(Feed(Download), "x64");
        if (matches) offer.CheckPackage(bytes, "Publisher", "x64");
        else Assert.Throws<IOException>(() => offer.CheckPackage(bytes, "Publisher", "x64"));
    }
}
