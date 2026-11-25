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

    [Fact(DisplayName = "a slide travels half the strip's height")]
    public void TravelIsHalfTheStrip() => Assert.Equal(20, StripFace.Travel(40));

    [Fact(DisplayName = "the outgoing part is gone by sixty percent of the slide and stays gone")]
    public void OutgoingFadesEarly()
    {
        Assert.Equal(1, StripFace.OutgoingOpacity(0));
        Assert.Equal(0.5, StripFace.OutgoingOpacity(0.3), 6);
        Assert.Equal(0, StripFace.OutgoingOpacity(0.6), 6);
        Assert.Equal(0, StripFace.OutgoingOpacity(1));
    }

    [Fact(DisplayName = "the incoming part rises from nothing to full and never exceeds the range")]
    public void IncomingFadesIn()
    {
        Assert.Equal(0, StripFace.IncomingOpacity(-1));
        Assert.Equal(0.4, StripFace.IncomingOpacity(0.4), 6);
        Assert.Equal(1, StripFace.IncomingOpacity(2));
    }

    [Fact(DisplayName = "the old and new parts are never both above half strength")]
    public void NeverBothStrong()
    {
        for (var t = 0.0; t <= 1; t += 0.01)
            Assert.True(StripFace.OutgoingOpacity(t) <= 0.5 || StripFace.IncomingOpacity(t) <= 0.5);
    }

    [Fact(DisplayName = "a feather of no strength keeps every pixel and a full one keeps the ramp")]
    public void FeatherStrength()
    {
        Assert.Equal(1, StripFace.FeatherKeep(0, 10, 0));
        Assert.Equal(0.05, StripFace.FeatherKeep(0, 10, 1), 6);
        Assert.Equal(0.525, StripFace.FeatherKeep(0, 10, 0.5), 6);
        Assert.Equal(0.95, StripFace.FeatherKeep(9, 10, 1), 6);
    }

    [Fact(DisplayName = "an interpolation clamps to its two ends")]
    public void LerpClamps()
    {
        Assert.Equal(0, StripFace.Lerp(0, 1, -1));
        Assert.Equal(1, StripFace.Lerp(0, 1, 5));
        Assert.Equal(0.25, StripFace.Lerp(0, 1, 0.25));
    }
}
