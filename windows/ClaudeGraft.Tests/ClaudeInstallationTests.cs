using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ClaudeInstallationTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly Func<ClaudeInstallation?> _locate = ClaudeInstallation.Locate;

    public void Dispose()
    {
        ClaudeInstallation.Locate = _locate;
        ClaudeInstallation.ForgetForTests();
        _temp.Dispose();
    }

    [Theory(DisplayName = "a package that moved its data out keeps its main profile in Claude-Data under its own name")]
    [InlineData("Claude_pzs8sxrjxfjjc")]
    [InlineData("AnthropicPBC.Claude_fnn82j28hfe8t")]
    public void MovedOut(string family)
    {
        var roaming = _temp.Dir("Roaming");
        var local = _temp.Dir("Local");
        // What the move leaves behind in the package: a marker and nothing else.
        File.WriteAllText(Path.Combine(_temp.Dir("Local", "Packages", family, "LocalCache", "Roaming", "Claude"),
            ".user-data-left." + family.ToLowerInvariant()), "");
        var moved = _temp.Dir("Local", "Claude-Data", family.ToLowerInvariant());
        Assert.Equal(moved, ClaudeInstallation.PackageMainProfile(family, roaming, local));
    }

    [Fact(DisplayName = "a package whose profile is still in its redirected AppData is read from there")]
    public void StillRedirected()
    {
        var family = PackagedClaude.FamilyNames[0];
        var local = _temp.Dir("Local");
        var redirected = _temp.Dir("Local", "Packages", family, "LocalCache", "Roaming", "Claude");
        File.WriteAllText(Path.Combine(redirected, "Local State"), "{}");
        Assert.Equal(redirected, ClaudeInstallation.PackageMainProfile(family, _temp.Dir("Roaming"), local));
    }

    [Fact(DisplayName = "a package that never ran will keep its profile in Claude-Data, and Graft does not make the folder")]
    public void PackageNeverRan()
    {
        var family = PackagedClaude.FamilyNames[1];
        var local = _temp.Dir("Local");
        var main = ClaudeInstallation.PackageMainProfile(family, _temp.Dir("Roaming"), local);
        Assert.Equal(Path.Combine(local, "Claude-Data", family.ToLowerInvariant()), main);
        Assert.False(Directory.Exists(main));
    }

    [Fact(DisplayName = "a package whose profile sits in AppData and nowhere of its own is read from AppData")]
    public void PackageNotRedirected()
    {
        var roaming = _temp.Dir("Roaming");
        var main = _temp.Dir("Roaming", "Claude");
        Assert.Equal(main, ClaudeInstallation.PackageMainProfile(PackagedClaude.FamilyNames[0], roaming, _temp.Dir("Local")));
    }

    [Fact(DisplayName = "a profile cannot be named after a main profile that moved out")]
    public void MovedFolderIsReserved()
    {
        var main = _temp.Dir("Claude-Data", "anthropicpbc.claude_fnn82j28hfe8t");
        ClaudeInstallation.Locate = () => new ClaudeInstallation(@"C:\claude.exe", main, null);
        ClaudeInstallation.ForgetForTests();
        Assert.NotNull(Graft.ValidateFolder("AnthropicPBC.Claude_fnn82j28hfe8t"));
        Assert.Null(Graft.ValidateFolder("Claude-2"));
    }

    [Fact(DisplayName = "profiles are found where the installed Claude keeps them")]
    public void ProfilesFollowTheInstall()
    {
        var main = _temp.Dir("Claude-Data", "anthropicpbc.claude_fnn82j28hfe8t");
        ClaudeInstallation.Locate = () => new ClaudeInstallation(@"C:\claude.exe", main, null);
        ClaudeInstallation.ForgetForTests();
        Assert.Equal(main, GraftPaths.DefaultProfile);
        Assert.Equal(Path.GetDirectoryName(main), GraftPaths.ProfilesRoot);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(main)!, "Claude-2"), GraftPaths.Profile("Claude-2"));
    }

    [Fact(DisplayName = "Graft's own data stays in AppData whichever Claude is installed")]
    public void OwnDataStays()
    {
        ClaudeInstallation.Locate = () => new ClaudeInstallation(@"C:\claude.exe", _temp.Dir("Claude-Data", "anthropicpbc.claude_fnn82j28hfe8t"), null);
        ClaudeInstallation.ForgetForTests();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeGraft"),
            GraftPaths.OwnData);
    }
}
