using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class UsageApiTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact(DisplayName = "a usage response yields both windows, resets, and the capitalised plan")]
    public void ParsesReading()
    {
        var reading = UsageApi.ReadingFrom(Body(
            "{\"five_hour\":{\"utilization\":42,\"resets_at\":\"2026-09-01T22:00:00Z\"}," +
            "\"seven_day\":{\"utilization\":30,\"resets_at\":\"2026-09-05T00:00:00Z\"}," +
            "\"subscription_type\":\"max\"}"));

        Assert.NotNull(reading);
        Assert.Equal(42, reading!.FiveHour);
        Assert.Equal(30, reading.Week);
        Assert.Equal("Max", reading.Plan);
        Assert.NotNull(reading.FiveHourReset);
    }

    [Fact(DisplayName = "a fractional utilization is rounded, and a missing seven-day window reads as zero")]
    public void RoundsAndDefaults()
    {
        var reading = UsageApi.ReadingFrom(Body("{\"five_hour\":{\"utilization\":66.7}}"));
        Assert.NotNull(reading);
        Assert.Equal(67, reading!.FiveHour);
        Assert.Equal(0, reading.Week);
    }

    [Fact(DisplayName = "a response with no five-hour window is unreadable")]
    public void RejectsMissingSession() =>
        Assert.Null(UsageApi.ReadingFrom(Body("{\"seven_day\":{\"utilization\":10}}")));
}

public class UsageDiskTests
{
    [Fact(DisplayName = "the latest sample's figures are the ones reported")]
    public void ReadsLatestSample()
    {
        using var t = new TempDir();
        var profile = t.Dir("profile");
        // fh goes 0 -> 20 -> 55; the reset window opens at the first non-zero.
        var now = DateTimeOffset.UtcNow;
        long Ms(TimeSpan ago) => (now - ago).ToUnixTimeMilliseconds();
        var json =
            "{\"samples\":[" +
            "{\"t\":" + Ms(TimeSpan.FromHours(2)) + ",\"u\":{\"fh\":0,\"sd\":10}}," +
            "{\"t\":" + Ms(TimeSpan.FromHours(1)) + ",\"u\":{\"fh\":20,\"sd\":12}}," +
            "{\"t\":" + Ms(TimeSpan.FromMinutes(5)) + ",\"u\":{\"fh\":55,\"sd\":15},\"org\":\"o1\"}" +
            "]}";
        File.WriteAllText(Path.Combine(profile, "plan-usage-history.json"), json);

        var usage = Graft.UsageOf(profile);
        Assert.NotNull(usage);
        Assert.Equal(55, usage!.FiveHour);
        Assert.Equal(15, usage.Week);
        Assert.Equal("o1", usage.Organization);
        // The window opened an hour ago, so a reset ~4 hours out is still future.
        Assert.NotNull(usage.FiveHourReset);
    }

    [Fact(DisplayName = "a profile that never recorded usage reports none")]
    public void NoFileNoUsage()
    {
        using var t = new TempDir();
        Assert.Null(Graft.UsageOf(t.Dir("empty")));
    }
}

public class CountdownTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2 * 86400 + 3 * 3600 + 40 * 60, "2d 3h 40m")]
    [InlineData(3 * 3600 + 40 * 60, "3h 40m")]
    [InlineData(12 * 60, "12m")]
    [InlineData(20, "1m")]      // under a minute still shows a minute
    public void Formats(int seconds, string expected) =>
        Assert.Equal(expected, Graft.Countdown(Now.AddSeconds(seconds), Now));

    [Fact(DisplayName = "a time already past has no countdown")]
    public void PastIsNull() => Assert.Null(Graft.Countdown(Now.AddMinutes(-1), Now));
}
