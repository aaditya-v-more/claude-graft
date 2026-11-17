using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class UpdaterTests
{
    [Fact(DisplayName = "an update installs itself unless it is held")]
    public void InstallsUnlessHeld()
    {
        Assert.True(Updater.InstallsOnItsOwn(held: false, windowOpen: false));
        Assert.False(Updater.InstallsOnItsOwn(held: true, windowOpen: false));
    }

    [Fact(DisplayName = "an update waits while a Graft window is open, since installing closes it")]
    public void WaitsForWindow() => Assert.False(Updater.InstallsOnItsOwn(held: false, windowOpen: true));

    [Fact(DisplayName = "a settings file from before the hold existed installs on its own")]
    public void OlderSettingsDoNotHold() =>
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<GraftSettings>("""{"theme":"System"}""",
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!.HoldUpdates);

    [Fact(DisplayName = "the installed app never lives in the folder that holds the relay and the sign-ins")]
    public void PackIdIsNotTheDataFolder() =>
        Assert.False(string.Equals(Updater.PackId, "ClaudeGraft", StringComparison.OrdinalIgnoreCase));

    [Fact(DisplayName = "a file inside the old install is found at the same place in the new one")]
    public void MovesAFileInside() =>
        Assert.Equal(@"C:\new\current\relay\GraftRelay.exe",
            Updater.Moved(@"c:\Users\x\AppData\Local\Programs\ClaudeGraft\relay\GraftRelay.exe",
                @"C:\Users\x\AppData\Local\Programs\ClaudeGraft\", @"C:\new\current"));

    [Fact(DisplayName = "a folder that only starts with the old install's name is not inside it")]
    public void LeavesASiblingAlone() =>
        Assert.Null(Updater.Moved(@"C:\Programs\ClaudeGraft2\ClaudeGraft.exe", @"C:\Programs\ClaudeGraft", @"C:\new"));

    [Fact(DisplayName = "a link with no target is not moved")]
    public void LeavesAnEmptyTargetAlone() =>
        Assert.Null(Updater.Moved("", @"C:\Programs\ClaudeGraft", @"C:\new"));

    [Fact(DisplayName = "a development build never installs an update on its own")]
    public void DevelopmentWaitsForAPress() =>
        Assert.False(Updater.InstallsOnItsOwn(held: false, windowOpen: false, development: true));

    [Theory(DisplayName = "only a release numbered above the running build is offered")]
    [InlineData("2.0.1", "2.0.0", true)]
    [InlineData("2.1.0", "2.0.9", true)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("1.9.9", "2.0.0", false)]
    [InlineData("2.0.0", "2.0.0.0", false)]
    public void OffersOnlyNewer(string found, string running, bool offered) =>
        Assert.Equal(offered, Updater.Offers(Version.Parse(found), Version.Parse(running)));
}
