using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class PalettesTests
{
    [Fact(DisplayName = "the Graft theme keeps the colours measured off the Mac build")]
    public void GraftIsMeasured()
    {
        var dark = Palettes.For(AppPalette.Graft, dark: true);
        Assert.Equal(Rgb.Hex("#2B2927"), dark.Content);
        Assert.Equal(Rgb.Hex("#D97757"), dark.Accent);
        Assert.Equal(Rgb.Hex("#F6F5F3"), Palettes.For(AppPalette.Graft, dark: false).Content);
    }

    [Fact(DisplayName = "a tinted theme turns each surface toward its hue and leaves it as light or dark as Graft's")]
    public void TintKeepsLightness()
    {
        var graft = Palettes.For(AppPalette.Graft, dark: true);
        var ocean = Palettes.For(AppPalette.Ocean, dark: true);
        Assert.InRange(ocean.Sidebar.Hsl().L, graft.Sidebar.Hsl().L - 0.01, graft.Sidebar.Hsl().L + 0.01);
        Assert.InRange(ocean.Sidebar.Hsl().H, 195, 215);
        Assert.True(ocean.Card.Hsl().L > ocean.Content.Hsl().L);
    }

    [Fact(DisplayName = "the system accent theme takes its hue and its accent from the colour Windows gives")]
    public void FollowsSystemAccent()
    {
        var green = Rgb.Hex("#3FA66A");
        var colors = Palettes.For(AppPalette.Accent, dark: true, green);
        Assert.Equal(green, colors.Accent);
        Assert.True(colors.AccentLight2.Hsl().L > green.Hsl().L && colors.AccentDark2.Hsl().L < green.Hsl().L);
        Assert.Equal(Palettes.For(AppPalette.Graft, dark: true), Palettes.For(AppPalette.Accent, dark: true));
    }

    [Fact(DisplayName = "a settings file from before themes reads as Graft")]
    public void OlderSettingsReadAsGraft() =>
        Assert.Equal(AppPalette.Graft, System.Text.Json.JsonSerializer.Deserialize<GraftSettings>("{\"theme\":0}")!.Palette);
}
