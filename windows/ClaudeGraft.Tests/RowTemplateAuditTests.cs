using System.Runtime.CompilerServices;
using Xunit;

namespace ClaudeGraft.Tests;

public class RowTemplateAuditTests
{
    // A reused row's x:Bind sets IsChecked before Tag, so a Checked or Unchecked
    // handler in the template writes the new row's value into the old row's setting.
    [Fact(DisplayName = "keep-warm saves only on a person's toggle, never on a binding's change")]
    public void KeepWarmSavesOnClickOnly()
    {
        var xaml = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "MainPage.xaml"));
        Assert.DoesNotMatch(@"\s(Un)?Checked=""", xaml);
        Assert.Contains("Toggled=\"KeepWarm_Toggled\"", xaml);
        var code = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "MainPage.xaml.cs"));
        Assert.Contains("box.FocusState == FocusState.Unfocused) return;", code);
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
