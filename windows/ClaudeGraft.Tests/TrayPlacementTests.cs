using System.Drawing;
using Xunit;

namespace ClaudeGraft.Tests;

public class TrayPlacementTests
{
    private static readonly Rectangle Screen = new(0, 0, 1000, 800);
    private static readonly Size Popup = new(320, 250);

    [Theory]
    [InlineData(0, 0, 1000, 760, 668, 498)]
    [InlineData(0, 40, 1000, 760, 668, 52)]
    [InlineData(40, 0, 960, 800, 52, 538)]
    [InlineData(0, 0, 960, 800, 628, 538)]
    public void Pinned_icons_keep_the_corner_on_each_taskbar_edge(int x, int y, int w, int h,
        int expectedX, int expectedY)
    {
        var at = TrayPlacement.Place(Screen, new Rectangle(x, y, w, h), Popup, 12, null, out _);
        Assert.Equal(new Point(expectedX, expectedY), at);
    }

    [Theory]
    [InlineData(0, 0, 1000, 760, 770, 620, 668, 358)]  // above a bottom taskbar
    [InlineData(0, 40, 1000, 760, 770, 50, 668, 182)]   // below a top taskbar
    [InlineData(40, 0, 960, 800, 50, 620, 262, 538)]    // right of a left taskbar
    [InlineData(0, 0, 960, 800, 750, 620, 418, 538)]    // left of a right taskbar
    public void Hidden_icons_open_the_menu_next_to_the_panel(int x, int y, int w, int h,
        int panelX, int panelY, int expectedX, int expectedY)
    {
        var work = new Rectangle(x, y, w, h);
        var panel = new Rectangle(panelX, panelY, 200, 120);
        var at = TrayPlacement.Place(Screen, work, Popup, 12, panel, out _);
        var menu = new Rectangle(at, Popup);
        Assert.Equal(new Point(expectedX, expectedY), at);
        Assert.True(work.Contains(menu));
        Assert.False(panel.IntersectsWith(menu));
    }

    [Fact]
    public void A_taller_menu_moves_to_the_side_on_a_scaled_secondary_monitor()
    {
        var screen = new Rectangle(-1920, -300, 1920, 1080);
        var work = new Rectangle(-1920, -300, 1920, 1008);
        var panel = new Rectangle(-550, 430, 390, 260);
        const int margin = 18; // 150% scale
        var shortSize = new Size(480, 360);
        var tallSize = new Size(480, 900);
        var before = TrayPlacement.Place(screen, work, shortSize, margin, panel, out _);
        var after = TrayPlacement.Place(screen, work, tallSize, margin, panel, out _);
        Assert.Equal(panel.Top - margin, before.Y + shortSize.Height);
        Assert.Equal(panel.Left - margin, after.X + tallSize.Width);
        Assert.True(work.Contains(new Rectangle(after, tallSize)));
    }

    [Fact]
    public void A_tight_fit_gives_up_outer_margin_before_covering_the_overflow()
    {
        var work = new Rectangle(0, 0, 300, 200);
        var panel = new Rectangle(0, 120, 300, 80);
        var size = new Size(200, 120);
        var at = TrayPlacement.Place(work, work, size, 12, panel, out _);
        Assert.Equal(0, at.Y);
        Assert.False(panel.IntersectsWith(new Rectangle(at, size)));
    }

    [Fact]
    public void When_all_sides_overlap_the_popup_stays_onscreen_with_the_least_overlap()
    {
        var work = new Rectangle(-500, 20, 300, 200);
        var panel = new Rectangle(-450, 100, 200, 120);
        var size = new Size(250, 150);
        var at = TrayPlacement.Place(work, work, size, 12, panel, out _);
        var menu = new Rectangle(at, size);
        Assert.True(work.Contains(menu));
        Assert.Equal(20, at.Y);
        Assert.InRange(Rectangle.Intersect(menu, panel).Height, 1, 70);
    }
}
