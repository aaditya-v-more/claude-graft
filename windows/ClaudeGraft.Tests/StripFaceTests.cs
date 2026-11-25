using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class StripFaceTests
{
    [Theory(DisplayName = "usage is measured at two of its widest digits, and a third only when the figure has one")]
    [InlineData("5%", "88%")]
    [InlineData("45%", "88%")]
    [InlineData("100%", "888%")]
    [InlineData("W 7%", "W 88%")]
    [InlineData("W 100%", "W 888%")]
    public void UsageReservesTwoDigits(string usage, string widest) =>
        Assert.Equal(widest, StripFace.WidestUsage(usage));

    [Theory(DisplayName = "a usage figure that went down is told apart from one that did not")]
    [InlineData("45%", "3%", true)]
    [InlineData("W 90%", "W 12%", true)]
    [InlineData("3%", "45%", false)]
    [InlineData("45%", "45%", false)]
    [InlineData("45%", "n/a", false)]
    [InlineData("n/a", "45%", false)]
    [InlineData(null, "5%", false)]
    public void UsageFellComparesFirstNumbers(string? before, string? now, bool fell) =>
        Assert.Equal(fell, StripFace.UsageFell(before, now));

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
