using System.Runtime.CompilerServices;
using Xunit;

namespace ClaudeGraft.Tests;

public class RowTemplateAuditTests
{
    // A reused card's Tag can still name the Claude it showed before; its
    // DataContext is the one the switch was just bound from.
    [Fact(DisplayName = "keep-warm judges a flip against the Claude the switch is bound to, never its Tag")]
    public void KeepWarmReadsTheBoundClaude()
    {
        var xaml = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "MainPage.xaml"));
        Assert.DoesNotMatch(@"\s(Un)?Checked=""", xaml);
        Assert.Contains("Toggled=\"KeepWarm_Toggled\" />", xaml);
        var code = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "MainPage.xaml.cs"));
        Assert.Contains("sender is not ToggleSwitch { DataContext: ShortcutRow row } box", code);
        Assert.Contains("KeepWarmSwitch.ToSave(box.IsOn, row.KeepWarm)", code);
        Assert.DoesNotContain("ToggleSwitch { Tag: ShortcutRow row }", code);
    }

    [Fact(DisplayName = "the sidebar's open button goes through the page's open, never straight to the launcher")]
    public void SidebarOpenSharesThePagesOpen()
    {
        var window = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "MainWindow.xaml.cs"));
        Assert.Contains("current.OpenAsync(openRow)", window);
        Assert.DoesNotContain("Launcher.Open", window);
        Assert.DoesNotContain("ProfileRows.Open", window);
        Assert.Contains("Spoken.Id(\"sidebar\", row.Key, \"open\")", window);
        var page = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "MainPage.xaml.cs"));
        Assert.Contains("await OpenAsync(row)", page);
    }

    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Directory.GetParent(here)!.Parent!.FullName;
}
