using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class UsageStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static UsageEntry Live(TimeSpan age, UsageRefusal? refusal = null) => new()
    {
        Usage = new Usage { FiveHour = 8, Week = 33, Sampled = Now - age },
        IsLive = true,
        UpdatedAt = Now - age,
        Refusal = refusal,
    };

    [Fact(DisplayName = "a figure fetched moments ago says it was updated just now")]
    public void FreshFigure()
    {
        var line = UsageStatus.Describe(Live(TimeSpan.FromSeconds(20)), fetching: false, Now);
        Assert.Equal(new UsageStatus.Line("Updated just now from Anthropic", false), line);
    }

    [Fact(DisplayName = "an older figure says how old it is")]
    public void OlderFigure()
    {
        var line = UsageStatus.Describe(Live(TimeSpan.FromMinutes(3)), fetching: false, Now);
        Assert.Equal("Updated 3m ago from Anthropic", line!.Text);
    }

    [Fact(DisplayName = "a read going out says it is refreshing, whatever the last one found")]
    public void Refreshing()
    {
        var line = UsageStatus.Describe(Live(TimeSpan.FromMinutes(12),
            new UsageRefusal { Kind = RefusalKind.RateLimited }), fetching: true, Now);
        Assert.Equal(new UsageStatus.Line("Refreshing usage…", false), line);
    }

    [Fact(DisplayName = "a rate-limited refresh warns, names the figure's age and when it will try again")]
    public void RateLimited()
    {
        var line = UsageStatus.Describe(Live(TimeSpan.FromMinutes(12),
            new UsageRefusal { Kind = RefusalKind.RateLimited, Status = 429, RetryAt = Now.AddMinutes(4) }),
            fetching: false, Now);
        Assert.Equal(new UsageStatus.Line(
            "Anthropic is limiting usage checks right now. Showing figures from 12m ago, next try in 4m.", true), line);
    }

    [Fact(DisplayName = "a refusal with no figure behind it still says why")]
    public void RefusedWithNothingToShow()
    {
        var entry = new UsageEntry { Refusal = new UsageRefusal { Kind = RefusalKind.Failed, Status = 500 } };
        var line = UsageStatus.Describe(entry, fetching: false, Now);
        Assert.Equal(new UsageStatus.Line("Usage could not be refreshed (Anthropic answered 500).", true), line);
    }

    [Fact(DisplayName = "a figure off disk says it is Claude's own record")]
    public void DiskFigure()
    {
        var entry = new UsageEntry { IsLive = false, UpdatedAt = Now.AddHours(-2) };
        Assert.Equal("Updated 2h 0m ago from this profile's own record", UsageStatus.Describe(entry, false, Now)!.Text);
    }

    [Theory(DisplayName = "each way a read can fail is told apart")]
    [InlineData(429, RefusalKind.RateLimited)]
    [InlineData(401, RefusalKind.LoginRefused)]
    [InlineData(403, RefusalKind.LoginRefused)]
    [InlineData(500, RefusalKind.Failed)]
    public void KindOfStatus(int status, RefusalKind kind) =>
        Assert.Equal(kind, UsageRefusal.KindOf(new UsageApi.Failure(status, null)));

    [Fact(DisplayName = "a read that never reached Anthropic is unreachable, not refused")]
    public void Unreachable() =>
        Assert.Equal(RefusalKind.Unreachable, UsageRefusal.KindOf(new HttpRequestException("offline")));

    [Fact(DisplayName = "a five-hour window past its reset reads as closed and empty, the week untouched")]
    public void FiveHourRollsOver()
    {
        var usage = new Usage
        {
            FiveHour = 20, Week = 34, Sampled = Now.AddHours(-6),
            FiveHourReset = Now.AddMinutes(-1), WeekReset = Now.AddDays(3),
        };
        var rolled = UsageStatus.Rolled(usage, Now);
        Assert.Equal(0, rolled.FiveHour);
        Assert.Null(rolled.FiveHourReset);
        Assert.Equal(34, rolled.Week);
        Assert.Equal(Now.AddDays(3), rolled.WeekReset);
    }

    [Fact(DisplayName = "a window still inside its reset is left exactly as read")]
    public void OpenWindowStands()
    {
        var usage = new Usage { FiveHour = 20, Week = 34, Sampled = Now, FiveHourReset = Now.AddHours(2) };
        Assert.Equal(usage, UsageStatus.Rolled(usage, Now));
    }

    [Fact(DisplayName = "a recent live figure is drawn at full strength")]
    public void RecentIsNotStale() =>
        Assert.False(UsageStatus.IsStale(Live(TimeSpan.FromMinutes(4)), Now));

    [Theory(DisplayName = "an old figure, a disk figure, or one behind a refusal is drawn faint")]
    [InlineData("old")]
    [InlineData("disk")]
    [InlineData("refused")]
    public void StaleIsFaint(string why)
    {
        var entry = why switch
        {
            "old" => Live(TimeSpan.FromMinutes(11)),
            "disk" => new UsageEntry { IsLive = false, UpdatedAt = Now },
            _ => Live(TimeSpan.FromMinutes(1), new UsageRefusal { Kind = RefusalKind.RateLimited }),
        };
        Assert.True(UsageStatus.IsStale(entry, Now));
    }

    [Fact(DisplayName = "Refresh reads as busy and cannot be pressed while a read is out")]
    public void RefreshBusy() =>
        Assert.Equal(new UsageStatus.Button("Refreshing…", false), UsageStatus.RefreshButton(true, null, Now));

    [Fact(DisplayName = "Refresh counts down in seconds while every account is held off")]
    public void RefreshHeldSeconds() =>
        Assert.Equal(new UsageStatus.Button("Available in 40s", false),
            UsageStatus.RefreshButton(false, Now.AddSeconds(39.2), Now));

    [Fact(DisplayName = "a longer hold counts down in minutes")]
    public void RefreshHeldMinutes() =>
        Assert.Equal("Available in 4m", UsageStatus.RefreshButton(false, Now.AddMinutes(4.5), Now).Text);

    [Fact(DisplayName = "with nothing out and nothing held, Refresh can be pressed")]
    public void RefreshReady()
    {
        Assert.Equal(new UsageStatus.Button("Refresh Usage", true), UsageStatus.RefreshButton(false, null, Now));
        Assert.True(UsageStatus.RefreshButton(false, Now.AddSeconds(-1), Now).Enabled);
    }
}
