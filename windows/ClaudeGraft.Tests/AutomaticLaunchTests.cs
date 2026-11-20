using System.Runtime.CompilerServices;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class AutomaticLaunchTests
{
    private const string Exe = @"C:\Users\x\AppData\Local\ClaudeGraftApp\current\ClaudeGraft.exe";

    [Fact(DisplayName = "a launch from Start or the desktop opens the window even with Start hidden on")]
    public void PersonSeesTheWindow()
    {
        Assert.True(AutomaticLaunch.ShowsManager(startHidden: true, [Exe]));
        Assert.True(AutomaticLaunch.AsksForWindow([Exe]));
    }

    [Fact(DisplayName = "the launch at sign-in stays in the notification area with Start hidden on")]
    public void SignInStaysHidden()
    {
        Assert.False(AutomaticLaunch.ShowsManager(startHidden: true, [Exe, "--autostart"]));
        Assert.False(AutomaticLaunch.ShowsManager(startHidden: true, [Exe, "--AutoStart"]));
    }

    [Fact(DisplayName = "with Start hidden off even the launch at sign-in opens the window")]
    public void OffShowsAtSignIn()
    {
        Assert.True(AutomaticLaunch.ShowsManager(startHidden: false, [Exe, "--autostart"]));
        Assert.True(AutomaticLaunch.ShowsManager(startHidden: false, [Exe]));
    }

    [Fact(DisplayName = "a sign-in launch that finds Graft already running does not bring up its window")]
    public void SecondSignInLaunchIsQuiet()
    {
        Assert.False(AutomaticLaunch.AsksForWindow([Exe, "--autostart"]));
    }

    [Fact(DisplayName = "the Startup shortcut carries the flag, and an older one is rewritten to carry it at every start")]
    public void StartupShortcutCarriesTheFlag()
    {
        var autoStart = Read("AutoStart.cs");
        Assert.Contains("link.Arguments = AutomaticLaunch.Flag;", autoStart);
        Assert.Contains("if (IsEnabled()) Enable();", autoStart);
        var app = Read("App.xaml.cs");
        Assert.Contains("Task.Run(AutoStart.Refresh)", app);
        Assert.Contains("AutomaticLaunch.ShowsManager(Settings.StartHidden, Environment.GetCommandLineArgs())", app);
        Assert.DoesNotContain("if (!Settings.StartHidden) ShowManager();", app);
        // The installer's own hook writes the same shortcut, flag included.
        Assert.Contains("AutoStart.Set(true)", Read("Program.cs"));
    }

    [Fact(DisplayName = "a second launch asks for the window only when a person started it")]
    public void SecondLaunchWiring()
    {
        Assert.Contains("AutomaticLaunch.AsksForWindow(Environment.GetCommandLineArgs())) ShowRequest.Set()", Read("Program.cs"));
    }

    [Fact(DisplayName = "an update Graft installed on its own restarts hidden, and one a person pressed restarts as a launch of theirs")]
    public void QuietUpdateRestartsHidden()
    {
        Assert.Contains("restartArgs: restartsUnseen ? new[] { AutomaticLaunch.Flag } : null", Read("GraftUpdates.cs"));
        Assert.Contains("GraftUpdates.ApplyOnExit(release, restartsUnseen: quiet)", Read("App.xaml.cs"));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", file));

    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}
