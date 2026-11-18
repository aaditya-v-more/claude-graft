using System.Runtime.CompilerServices;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class LinksTests
{
    [Fact(DisplayName = "an email from Settings goes to the support address and says which Graft and which Windows it comes from")]
    public void Mail()
    {
        var mail = new Uri(Links.Mail("2.1.2", "Microsoft Windows NT 10.0.26100.0"));
        Assert.Equal("mailto", mail.Scheme);
        Assert.StartsWith("mailto:support@tinkerfold.com?", mail.OriginalString);
        Assert.Contains("subject=Claude%20Graft%202.1.2", mail.OriginalString);
        Assert.Contains(Uri.EscapeDataString("Claude Graft 2.1.2, Microsoft Windows NT 10.0.26100.0"), mail.OriginalString);
    }

    [Fact(DisplayName = "Settings offers help, what's new, a problem report and an email, at the addresses Links names")]
    public void SettingsLinks()
    {
        var xaml = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "SettingsPage.xaml"));
        foreach (var url in new[] { Links.Site, Links.Releases, Links.NewIssue })
            Assert.Contains($"NavigateUri=\"{url}\"", xaml);
        Assert.Contains("Click=\"Email_Click\"", xaml);
        Assert.Contains("Links.Mail()", File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "SettingsPage.xaml.cs")));
    }

    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}
