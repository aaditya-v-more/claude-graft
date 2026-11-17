using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class PackagedClaudeTests
{
    private const string Root = @"C:\Program Files\WindowsApps\Claude_2.2553.13.0_x64__pzs8sxrjxfjjc";

    private const string Manifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Applications>
            <Application Id="Claude" Executable="app\claude.exe" EntryPoint="Windows.FullTrustApplication" />
          </Applications>
        </Package>
        """;

    private const string StoreRoot = @"C:\Program Files\WindowsApps\AnthropicPBC.Claude_5.1231.2.0_x64__fnn82j28hfe8t";

    [Theory(DisplayName = "the manifest's application gives the exe and the id activation wants, for either package")]
    [InlineData(Root, "Claude_pzs8sxrjxfjjc")]
    [InlineData(StoreRoot, "AnthropicPBC.Claude_fnn82j28hfe8t")]
    public void FromManifest(string root, string family)
    {
        var install = PackagedClaude.FromManifest(root, Manifest, family)!;
        Assert.Equal(root + @"\app\claude.exe", install.Exe);
        Assert.Equal(family + "!Claude", install.AppUserModelId);
        Assert.Equal(family, install.FamilyName);
    }

    [Fact(DisplayName = "a manifest that will not parse finds no install")]
    public void BrokenManifest() => Assert.Null(PackagedClaude.FromManifest(Root, "<Package", PackagedClaude.FamilyNames[0]));

    [Theory(DisplayName = "a Claude started from either package is recognised as the app")]
    [InlineData(Root)]
    [InlineData(StoreRoot)]
    public void EitherPackageIsTheApp(string root) =>
        Assert.True(ClaudeProcesses.IsClaudeDesktop(root + @"\app\claude.exe --user-data-dir=C:\x\Claude-2"));

    [Fact(DisplayName = "a folder named like a package but from another publisher is not the app")]
    public void OtherPublisherIsNotTheApp() =>
        Assert.False(ClaudeProcesses.IsClaudeDesktop(@"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__someoneelse\claude.exe"));

    [Fact(DisplayName = "the packaged app counts as Claude Desktop, on its own profile or the main one")]
    public void PackagedProcess()
    {
        var exe = Root + @"\app\claude.exe";
        Assert.True(ClaudeProcesses.IsDefaultInstance(exe));
        Assert.True(ClaudeProcesses.IsClaudeDesktop(exe + @" --user-data-dir=C:\Users\T\AppData\Roaming\Claude-2"));
    }

    [Fact(DisplayName = "a copy of Claude Code inside a profile is still not the desktop app")]
    public void BridgedCli() =>
        Assert.False(ClaudeProcesses.IsClaudeDesktop(@"C:\Users\T\AppData\Roaming\Claude\claude-code\2.1\claude.exe"));
}
