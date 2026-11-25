using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class UsageLevelTests
{
    [Theory(DisplayName = "usage below 75 percent is normal")]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(74)]
    public void BelowWarning(int percent) => Assert.Equal(UsageLevel.Normal, UsageLevels.For(percent));

    [Theory(DisplayName = "usage from 75 percent up to 90 is a warning")]
    [InlineData(75)]
    [InlineData(89)]
    public void WarningBand(int percent) => Assert.Equal(UsageLevel.Warning, UsageLevels.For(percent));

    [Theory(DisplayName = "usage at 90 percent and above is critical")]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(130)]
    public void CriticalBand(int percent) => Assert.Equal(UsageLevel.Critical, UsageLevels.For(percent));
}
