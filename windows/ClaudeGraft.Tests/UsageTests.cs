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

    [Theory(DisplayName = "a null or absent five-hour window preserves the weekly-only plan")]
    [InlineData("{\"five_hour\":null,\"seven_day\":{\"utilization\":94,\"resets_at\":\"2026-11-10T22:00:00Z\"}}")]
    [InlineData("{\"seven_day\":{\"utilization\":94,\"resets_at\":\"2026-11-10T22:00:00Z\"}}")]
    public void AcceptsNoFiveHourLimit(string json)
    {
        var reading = UsageApi.ReadingFrom(Body(json));
        Assert.NotNull(reading);
        Assert.Null(reading.FiveHour);
        Assert.Null(reading.FiveHourReset);
        Assert.Equal(94, reading.Week);
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T22:00:00Z"), reading.WeekReset);
    }

    [Fact(DisplayName = "zero usage is an idle limited plan, not an absent limit")]
    public void IdleLimitedPlanKeepsZero()
    {
        var reading = UsageApi.ReadingFrom(Body("{\"five_hour\":{\"utilization\":0,\"resets_at\":null},\"seven_day\":{\"utilization\":94}}"));
        Assert.NotNull(reading);
        Assert.Equal(0, reading.FiveHour);
        Assert.Null(reading.FiveHourReset);
    }

    [Theory(DisplayName = "an unreadable supplied five-hour window is rejected even with weekly usage")]
    [InlineData("{}")]
    [InlineData("{\"utilization\":null}")]
    [InlineData("{\"utilization\":\"0\"}")]
    [InlineData("\"unreadable\"")]
    [InlineData("false")]
    public void RejectsMalformedSession(string window) =>
        Assert.Null(UsageApi.ReadingFrom(Body("{\"five_hour\":" + window + ",\"seven_day\":{\"utilization\":94}}")));

    [Theory(DisplayName = "at least one readable limit must be supplied")]
    [InlineData("{}")]
    [InlineData("{\"five_hour\":null}")]
    [InlineData("{\"five_hour\":null,\"seven_day\":null}")]
    [InlineData("{\"seven_day\":{\"utilization\":\"94\"}}")]
    [InlineData("null")]
    public void RejectsMissingLimits(string json) => Assert.Null(UsageApi.ReadingFrom(Body(json)));
}

public class UsageDiskTests
{
    [Fact(DisplayName = "reopening the menu keeps the original fetch time and prefers a newer desktop sample")]
    public void CachedSamplesKeepTheirAge()
    {
        var now = DateTimeOffset.UtcNow;
        var reading = new UsageApi.Reading { FiveHour = 21, Week = 34, Sampled = now.AddMinutes(-10) };
        var disk = new Usage { FiveHour = 32, Week = 45, Sampled = now.AddMinutes(-5) };
        var cached = UsageMonitor.LatestEntry(reading, null, now);
        Assert.Equal(reading.Sampled, cached.Usage!.Sampled);
        var newer = UsageMonitor.LatestEntry(reading, disk, now);
        Assert.False(newer.IsLive);
        Assert.Equal(disk.FiveHour, newer.Usage!.FiveHour);
    }

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

    private static DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static Usage? Read(params (DateTimeOffset time, int? fiveHour, int week, string? org)[] samples)
    {
        using var t = new TempDir();
        var profile = t.Dir("profile");
        var rows = samples.Select(s =>
        {
            var values = new Dictionary<string, int> { ["sd"] = s.week };
            if (s.fiveHour is int figure) values["fh"] = figure;
            return new { t = s.time.ToUnixTimeMilliseconds(), org = s.org, u = values };
        });
        File.WriteAllText(Path.Combine(profile, "plan-usage-history.json"), JsonSerializer.Serialize(new { samples = rows }));
        return Graft.UsageOf(profile);
    }

    [Fact(DisplayName = "consecutive weekly-only history after an old limit reads the latest week")]
    public void ReadsWeeklyOnlyHistory()
    {
        var now = Now;
        var usage = Read((now.AddDays(-8), 50, 55, "org"), (now.AddHours(-2), null, 93, "org"), (now, null, 94, "org"));
        Assert.NotNull(usage);
        Assert.Null(usage.FiveHour);
        Assert.Equal(94, usage.Week);
        Assert.Equal(now, usage.Sampled);
        Assert.Null(usage.FiveHourReset);
    }

    [Fact(DisplayName = "an account's first weekly-only sample establishes no five-hour limit")]
    public void ReadsFirstWeeklyOnlySample()
    {
        var usage = Read((Now, null, 3, "org"));
        Assert.NotNull(usage);
        Assert.Null(usage.FiveHour);
        Assert.Equal(3, usage.Week);
    }

    [Fact(DisplayName = "zero in disk history remains an idle limited window")]
    public void ZeroRemainsLimited()
    {
        var now = Now;
        var usage = Read((now.AddHours(-1), 0, 93, "org"), (now, 0, 94, "org"));
        Assert.NotNull(usage);
        Assert.Equal(0, usage.FiveHour);
        Assert.Equal(94, usage.Week);
        Assert.Null(usage.FiveHourReset);
    }

    [Fact(DisplayName = "a stray weekly-only sample retains the last complete figures and their age")]
    public void IncompleteSampleDoesNotRefreshOlderFigures()
    {
        var now = Now;
        var usage = Read((now.AddHours(-2), 30, 40, "org"), (now.AddHours(-1), 35, 41, "org"), (now, null, 42, "org"));
        Assert.NotNull(usage);
        Assert.Equal(35, usage.FiveHour);
        Assert.Equal(41, usage.Week);
        Assert.Equal(now.AddHours(-1), usage.Sampled);
    }

    [Theory(DisplayName = "recent complete history or only one later gap keeps a limited account limited")]
    [InlineData(3, true)]
    [InlineData(30, false)]
    public void DoesNotInferNoLimitPrematurely(int hoursAgo, bool secondGap)
    {
        var now = Now;
        var samples = new List<(DateTimeOffset, int?, int, string?)> { (now.AddHours(-hoursAgo), 30, 40, "org") };
        if (secondGap) samples.Add((now.AddHours(-1), null, 41, "org"));
        samples.Add((now, null, 42, "org"));
        var usage = Read(samples.ToArray());
        Assert.NotNull(usage);
        Assert.Equal(30, usage.FiveHour);
        Assert.Equal(40, usage.Week);
        Assert.Equal(now.AddHours(-hoursAgo), usage.Sampled);
    }

    [Theory(DisplayName = "another organization's limit cannot classify the current account")]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassifiesOnlyOwnOrganization(bool interleaved)
    {
        var now = Now;
        var usage = interleaved
            ? Read((now.AddHours(-2), null, 90, "own"), (now.AddHours(-1), 40, 50, "other"), (now, null, 94, "own"))
            : Read((now.AddHours(-2), 40, 50, "other"), (now.AddHours(-1), null, 90, "own"), (now, null, 94, "own"));
        Assert.NotNull(usage);
        Assert.Null(usage.FiveHour);
        Assert.Equal(94, usage.Week);
        Assert.Equal("own", usage.Organization);
    }

    [Fact(DisplayName = "a history gap cannot restart a limited account's five-hour countdown")]
    public void GapDoesNotMoveReset()
    {
        var now = Now;
        var usage = Read((now.AddHours(-3), 0, 40, "org"), (now.AddHours(-2), 30, 41, "org"),
                         (now.AddHours(-1), null, 42, "org"), (now, 40, 43, "org"));
        Assert.NotNull(usage);
        Assert.Equal(now.AddHours(3), usage.FiveHourReset);
    }

    [Fact(DisplayName = "weekly-only history still infers and rolls the weekly reset")]
    public void WeeklyOnlyResetStillRolls()
    {
        var now = Now;
        var resetSample = now.AddDays(-19).AddHours(-21);
        var usage = Read((now.AddDays(-20), 20, 60, "org"), (resetSample, null, 2, "org"), (now, null, 30, "org"));
        Assert.NotNull(usage);
        Assert.Null(usage.FiveHour);
        Assert.Equal(resetSample.AddDays(21), usage.WeekReset);
    }

    [Theory(DisplayName = "cached live and disk weekly-only readings both hide Start Session")]
    [InlineData(false)]
    [InlineData(true)]
    public void NoLimitSurvivesMonitor(bool fromLive)
    {
        var now = Now;
        var sampled = now.AddHours(-6);
        var entry = fromLive
            ? UsageMonitor.LatestEntry(new UsageApi.Reading { FiveHour = null, Week = 94, Sampled = sampled }, null, now)
            : UsageMonitor.LatestEntry(null, new Usage { FiveHour = null, Week = 94, Sampled = sampled }, now);
        Assert.True(entry.HasUsage);
        Assert.Equal(fromLive, entry.IsLive);
        Assert.Null(entry.Usage!.FiveHour);
        Assert.Equal(94, entry.Usage.Week);
        Assert.Equal(sampled, entry.Usage.Sampled);
        Assert.False(entry.MayStartSession);
    }

    [Fact(DisplayName = "unknown usage and an idle limited plan retain the explicit session action")]
    public void UnknownAndLimitedMayStartSession()
    {
        Assert.True(new UsageEntry().MayStartSession);
        Assert.True(new UsageEntry { Usage = new Usage { FiveHour = 0, Week = 94, Sampled = Now } }.MayStartSession);
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
