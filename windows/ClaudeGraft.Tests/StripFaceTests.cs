using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class StripFaceTests
{
    [Theory(DisplayName = "usage is measured at its widest so a climbing figure does not move the strip")]
    [InlineData("5%", "100%")]
    [InlineData("45%", "100%")]
    [InlineData("W 7%", "W 100%")]
    public void UsageReservesThreeDigits(string usage, string widest) =>
        Assert.Equal(widest, StripFace.WidestUsage(usage));

    [Fact(DisplayName = "the pop starts at nothing, overshoots, and ends at full size")]
    public void PopOvershoots()
    {
        Assert.Equal(0, StripFace.Pop(0));
        Assert.Equal(1, StripFace.Pop(1));
        Assert.Equal(1.3, StripFace.Pop(0.6), 6);
        Assert.InRange(StripFace.Pop(0.8), 1.0, 1.3);
    }

    [Fact(DisplayName = "an ease-out moves fastest at the start and stays within its range")]
    public void OutCubicShape()
    {
        Assert.Equal(0, StripFace.OutCubic(-1));
        Assert.Equal(1, StripFace.OutCubic(2));
        Assert.True(StripFace.OutCubic(0.25) > 0.25);
    }
}
