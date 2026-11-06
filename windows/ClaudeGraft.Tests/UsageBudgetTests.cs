using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class UsageBudgetTests
{
    [Fact(DisplayName = "a service Retry-After is the one wait even a press cannot skip")]
    public void RetryAfterHoldsEvenAPress()
    {
        // interactive, invalidated, and stale all say fetch — the retry gate wins,
        // so a throttled endpoint is not asked again on every refresh.
        Assert.False(UsageMonitor.ShouldFetch(
            interactive: true, invalidated: true, justFetched: false,
            haveFresh: false, inBackoff: false, inRetry: true));
    }

    [Fact(DisplayName = "a press goes to the endpoint, skipping the freshness cache and the backoff")]
    public void APressFetches()
    {
        Assert.True(UsageMonitor.ShouldFetch(
            interactive: true, invalidated: false, justFetched: false,
            haveFresh: true, inBackoff: true, inRetry: false));
    }

    [Fact(DisplayName = "a mashed refresh answers from the call moments ago rather than a new one")]
    public void APressJustMadeIsNotRepeated()
    {
        Assert.False(UsageMonitor.ShouldFetch(
            interactive: true, invalidated: false, justFetched: true,
            haveFresh: true, inBackoff: false, inRetry: false));
    }

    [Fact(DisplayName = "a started session refetches at once, through the pressed rung")]
    public void InvalidatedBeatsThePressedRung()
    {
        Assert.True(UsageMonitor.ShouldFetch(
            interactive: false, invalidated: true, justFetched: true,
            haveFresh: true, inBackoff: false, inRetry: false));
    }

    [Fact(DisplayName = "a figure a press marked stale is refetched even on a background tick")]
    public void InvalidatedFetches()
    {
        Assert.True(UsageMonitor.ShouldFetch(
            interactive: false, invalidated: true, justFetched: false,
            haveFresh: true, inBackoff: false, inRetry: false));
    }

    [Fact(DisplayName = "a fresh reading answers a background tick without a call")]
    public void FreshAnswersQuietly()
    {
        Assert.False(UsageMonitor.ShouldFetch(
            interactive: false, invalidated: false, justFetched: false,
            haveFresh: true, inBackoff: false, inRetry: false));
    }

    [Fact(DisplayName = "an ordinary backoff holds a background tick but not a press")]
    public void BackoffHoldsOnlyTheQuietTick()
    {
        Assert.False(UsageMonitor.ShouldFetch(
            interactive: false, invalidated: false, justFetched: false,
            haveFresh: false, inBackoff: true, inRetry: false));
        Assert.True(UsageMonitor.ShouldFetch(
            interactive: true, invalidated: false, justFetched: false,
            haveFresh: false, inBackoff: true, inRetry: false));
    }

    private static Usage Sample(int fiveHour, int week, DateTimeOffset sampled) =>
        new() { FiveHour = fiveHour, Week = week, Sampled = sampled };

    [Fact(DisplayName = "a fresh disk sample is read as the figure it recorded")]
    public void FreshDiskSampleStands()
    {
        var now = DateTimeOffset.UtcNow;
        var current = UsageMonitor.AsCurrentFigure(Sample(80, 20, now - TimeSpan.FromMinutes(30)), now);
        Assert.Equal(80, current!.FiveHour);
        Assert.Equal(20, current.Week);
    }

    [Fact(DisplayName = "a disk sample past its five-hour window reads as zero, not the old high-water mark")]
    public void StaleFiveHourReadsZero()
    {
        var now = DateTimeOffset.UtcNow;
        // The overnight-100% report: a window that closed hours ago must not go on
        // reading full.
        var current = UsageMonitor.AsCurrentFigure(Sample(100, 20, now - TimeSpan.FromHours(14)), now);
        Assert.Equal(0, current!.FiveHour);
        Assert.Equal(20, current.Week);   // still inside the week
    }

    [Fact(DisplayName = "a sample older than a week has its weekly figure spent too")]
    public void StaleWeekReadsZero()
    {
        var now = DateTimeOffset.UtcNow;
        var current = UsageMonitor.AsCurrentFigure(Sample(100, 90, now - TimeSpan.FromDays(8)), now);
        Assert.Equal(0, current!.FiveHour);
        Assert.Equal(0, current.Week);
    }
}
