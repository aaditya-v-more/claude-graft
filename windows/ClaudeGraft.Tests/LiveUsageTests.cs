using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// Proves the disk-usage parser reads this machine's real main profile. Skips
/// off Windows or when the history file is absent; never surfaces figures beyond
/// asserting they parsed into range.
/// </summary>
public class LiveUsageTests
{
    [Fact(DisplayName = "the real main profile's plan-usage history parses into range")]
    public void RealHistoryParses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (!System.IO.File.Exists(System.IO.Path.Combine(profile, "plan-usage-history.json"))) return;

        var usage = Graft.UsageOf(profile);
        Assert.NotNull(usage);
        if (usage!.FiveHour is int fiveHour) Assert.InRange(fiveHour, 0, 100);
        Assert.InRange(usage.Week, 0, 100);
    }
}
