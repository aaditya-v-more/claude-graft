using System.Runtime.CompilerServices;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class RecoveredNoticeTests
{
    [Fact(DisplayName = "the flyout names one recovered session, counts several, and says nothing when none was")]
    public void Text()
    {
        Assert.Null(RecoveredNotice.Text([]));
        Assert.Equal("Recovered “Fix the build”, which had closed without a record", RecoveredNotice.Text(["Fix the build"]));
        Assert.Equal("Recovered 3 sessions that closed without records", RecoveredNotice.Text(["a", "b", "c"]));
    }

    [Fact(DisplayName = "Graft runs a pass at its start and when the flyout opens, and the flyout shows what it filed")]
    public void Wired()
    {
        var app = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "App.xaml.cs"));
        Assert.Contains("_quitWatch.Swept +=", app);
        Assert.Contains("_quitWatch.Request();", app);
        Assert.Contains("_quitWatch?.Request();", app);
        Assert.Contains("RecoveredNotice.Text(App.Recovered)", File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "FlyoutView.xaml.cs")));
        Assert.Contains("AutomationProperties.AutomationId=\"flyout.recovered\"", File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "FlyoutView.xaml")));
    }

    [Fact(DisplayName = "a pass asked for by a look runs no project-order sync; only a quit owes one")]
    public void LookOwesNoOrderSync()
    {
        var watch = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft.Core", "ClaudeQuitWatch.cs"));
        Assert.Contains("if (Interlocked.Exchange(ref _quitOwed, 0) == 0) continue;", watch);
        Assert.Contains("_look = new Timer(_ => Sweep()", watch);
    }

    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}
