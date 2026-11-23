using System.Runtime.CompilerServices;
using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class FrontSwitchesTests
{
    [Fact(DisplayName = "a settings file from before the front switches shows the icon and not the name, as the Mac does by default")]
    public void Defaults()
    {
        var older = JsonSerializer.Deserialize<GraftSettings>("""{"taskbarStrip":true}""")!;
        Assert.True(older.FrontIcon);
        Assert.False(older.FrontName);
        var chosen = JsonSerializer.Deserialize<GraftSettings>(JsonSerializer.Serialize(new GraftSettings { FrontIcon = false, FrontName = true }))!;
        Assert.False(chosen.FrontIcon);
        Assert.True(chosen.FrontName);
    }

    [Fact(DisplayName = "each switch is saved from Settings, counts as a change, and decides what the strip draws")]
    public void Wired()
    {
        string Read(string file) => File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", file));
        var page = Read("SettingsPage.xaml.cs");
        Assert.Contains("FrontIcon = FrontIconSwitch.IsOn", page);
        Assert.Contains("FrontName = FrontNameSwitch.IsOn", page);
        var app = Read("App.xaml.cs");
        Assert.Contains("updated.FrontIcon == Settings.FrontIcon", app);
        Assert.Contains("updated.FrontName == Settings.FrontName", app);
        Assert.Contains("Settings.FrontIcon ? marks :", app);
        Assert.Contains("Settings.FrontName ?", app);
        Assert.Contains("a.Name == b.Name", Read("TaskbarStrip.cs"));
    }

    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}
