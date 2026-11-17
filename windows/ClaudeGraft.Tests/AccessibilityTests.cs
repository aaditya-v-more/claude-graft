using System.Xml.Linq;
using Xunit;

namespace ClaudeGraft.Tests;

public class AccessibilityTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Controls =
        { "Button", "ToggleSwitch", "CheckBox", "ComboBox", "TextBox", "ProgressBar", "GridView" };

    private static string AppFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ClaudeGraft", "MainPage.xaml");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
        }
        throw new DirectoryNotFoundException("the app's XAML was not found above the test output");
    }

    private static IEnumerable<(string File, XElement Control)> AllControls() =>
        Directory.EnumerateFiles(AppFolder(), "*.xaml")
            .SelectMany(file => XDocument.Load(file).Descendants()
                .Where(e => e.Name.Namespace == Ui && Controls.Contains(e.Name.LocalName))
                .Select(e => (Path.GetFileName(file), e)));

    private static string? Attribute(XElement e, string name) => e.Attribute(name)?.Value;

    [Fact(DisplayName = "every control can be found by automation the same way in every language")]
    public void EveryControlIdentified()
    {
        var missing = AllControls()
            .Where(c => Attribute(c.Control, "AutomationProperties.AutomationId") is null)
            .Select(c => $"{c.File}: {c.Control.Name.LocalName} {Attribute(c.Control, "Content") ?? ""}")
            .ToList();
        Assert.Empty(missing);
    }

    [Fact(DisplayName = "a button without plain text for content says what it does")]
    public void PictureButtonsNamed()
    {
        var unnamed = AllControls()
            .Where(c => c.Control.Name.LocalName == "Button"
                // WinUI names a button from plain text content alone, never from
                // a TextBlock nested beside an icon.
                && Attribute(c.Control, "Content") is null
                && Attribute(c.Control, "AutomationProperties.Name") is null
                // Their words, and names, are set in code as their state changes.
                && c.Control.Attribute(Xaml + "Name")?.Value is not ("AvailableButton" or "ActionButton"))
            .Select(c => $"{c.File}: {Attribute(c.Control, "AutomationProperties.AutomationId")}")
            .ToList();
        Assert.Empty(unnamed);
    }

    [Fact(DisplayName = "a usage bar carries its own words, so it is read as one element")]
    public void BarsSpeak()
    {
        var silent = AllControls()
            .Where(c => c.Control.Name.LocalName == "ProgressBar" && Attribute(c.Control, "AutomationProperties.Name") is null)
            .Select(c => c.File)
            .ToList();
        Assert.Empty(silent);
    }
}
