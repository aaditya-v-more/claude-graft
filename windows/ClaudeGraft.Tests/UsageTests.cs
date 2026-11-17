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

    [Fact(DisplayName = "an account with no five-hour limit is read as having none, not as zero")]
    public void ReadsWeekOnly()
    {
        foreach (var body in new[] { "{\"seven_day\":{\"utilization\":10}}",
                                     "{\"five_hour\":null,\"seven_day\":{\"utilization\":10}}" })
        {
            var reading = UsageApi.ReadingFrom(Body(body));
            Assert.Equal(10, reading!.Week);
            Assert.Null(reading.FiveHour);
            Assert.Null(reading.FiveHourReset);
        }
    }

    [Fact(DisplayName = "a five-hour window sent in a shape that will not read is refused rather than taken for no limit")]
    public void RefusesMalformedSession() =>
        Assert.Null(UsageApi.ReadingFrom(Body("{\"five_hour\":{\"used\":3},\"seven_day\":{\"utilization\":10}}")));

    [Fact(DisplayName = "a response with neither window is unreadable")]
    public void RejectsEmpty() => Assert.Null(UsageApi.ReadingFrom(Body("{}")));

    private static Usage? History(params (TimeSpan ago, int? fh, int sd)[] samples)
    {
        using var t = new TempDir();
        var profile = t.Dir("profile");
        var now = DateTimeOffset.UtcNow;
        var json = "{\"samples\":[" + string.Join(",", samples.Select(x =>
            "{\"t\":" + (now - x.ago).ToUnixTimeMilliseconds() + ",\"org\":\"o\",\"u\":{"
            + (x.fh is int f ? "\"fh\":" + f + "," : "") + "\"sd\":" + x.sd + "}}")) + "]}";
        File.WriteAllText(Path.Combine(profile, "plan-usage-history.json"), json);
        return Graft.UsageOf(profile);
    }

    [Fact(DisplayName = "one weekly-only sample on a limited account is a gap, not a lost limit")]
    public void StraySampleIsAGap()
    {
        var usage = History((TimeSpan.FromHours(1), 40, 10), (TimeSpan.FromMinutes(1), null, 12));
        Assert.Equal(40, usage!.FiveHour);
        Assert.Equal(10, usage.Week);
    }

    [Fact(DisplayName = "an account that has stopped writing the five-hour figure has no five-hour limit")]
    public void HistoryWithoutLimit()
    {
        var usage = History((TimeSpan.FromHours(9), 40, 10), (TimeSpan.FromHours(2), null, 50), (TimeSpan.FromMinutes(1), null, 52));
        Assert.Null(usage!.FiveHour);
        Assert.Equal(52, usage.Week);
    }

    private const string Session = "\"five_hour\":{\"utilization\":1}";

    [Fact(DisplayName = "a weekly Fable limit among the per-model windows is read with its reset")]
    public void ParsesFable()
    {
        var reading = UsageApi.ReadingFrom(Body("{" + Session + ",\"limits\":[" +
            "{\"kind\":\"weekly_scoped\",\"scope\":{\"model\":{\"display_name\":\"Claude Opus 5.5\"}},\"percent\":80}," +
            "{\"kind\":\"weekly_scoped\",\"scope\":{\"model\":{\"display_name\":\"Claude Fable 5.1\"}}," +
            "\"percent\":12,\"resets_at\":\"2026-09-05T00:00:00Z\"}]}"));
        Assert.Equal(12, reading!.Fable);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero), reading.FableReset);
    }

    [Fact(DisplayName = "a Fable limit giving only utilization falls back to it, rounded")]
    public void FableFallsBackToUtilization()
    {
        var reading = UsageApi.ReadingFrom(Body("{" + Session + ",\"limits\":[" +
            "{\"kind\":\"weekly_scoped\",\"scope\":{\"model\":{\"display_name\":\"claude fable\"}}," +
            "\"utilization\":33.6,\"resets_at\":1788566400}]}"));
        Assert.Equal(34, reading!.Fable);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788566400), reading.FableReset);
    }

    [Fact(DisplayName = "an account with no Fable limit reports no Fable figure")]
    public void NoFable()
    {
        Assert.Null(UsageApi.ReadingFrom(Body("{" + Session + "}"))!.Fable);
        Assert.Null(UsageApi.ReadingFrom(Body("{" + Session + ",\"limits\":[]}"))!.Fable);
        Assert.Null(UsageApi.ReadingFrom(Body("{" + Session + ",\"limits\":[" +
            "{\"kind\":\"five_hour_scoped\",\"scope\":{\"model\":{\"display_name\":\"Claude Fable 5.1\"}},\"percent\":5}]}"))!.Fable);
    }
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
