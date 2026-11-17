using System.Runtime.CompilerServices;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// The Windows echo of the Mac suite's rule that <c>startSession</c> has exactly
/// one hand-audited set of callers and never sits on a timer or lifecycle line.
/// Opening a five-hour window without a press is the one thing this app does off
/// nobody's ask, so a new caller must be a deliberate, reviewed change — not a
/// line that quietly slipped into a view's <c>Tick</c> and started spending a
/// plan while nobody watched.
///
/// The only automated caller is <see cref="ClaudeGraft.Core.AutoStarter"/>, driven
/// by the app's own background timer through <c>SweepAsync</c> — never straight
/// from the timer to <c>StartAsync</c>. The other two are the manager's and the
/// flyout's Start buttons.
/// </summary>
public class StartCallerAuditTests
{
    private const string Invocation = "SessionStarter.StartAsync";

    private static readonly string[] SanctionedCallers =
    {
        "AutoStarter.cs",     // the one automated caller, itself gated and cooled down
        "MainPage.xaml.cs",   // the manager's Start button
        "FlyoutView.xaml.cs", // the tray flyout's Start button
    };

    // Wiring StartAsync onto any of these would open a window off a refresh or a
    // reappearing window, which is the whole thing the button is meant to be.
    private static readonly string[] LifecycleTokens =
    {
        "Tick", "Loaded", "OnAppear", "onAppear", "onReceive", "DispatcherTimer", "Timer(",
    };

    [Fact(DisplayName = "StartAsync is called from exactly the three hand-audited places and no others")]
    public void OnlySanctionedCallers()
    {
        var callers = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains(Invocation))
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(SanctionedCallers.OrderBy(n => n), callers);
    }

    [Fact(DisplayName = "no source line wires a session start onto a timer or a view's lifecycle")]
    public void NeverOnATimerOrLifecycleLine()
    {
        foreach (var file in SourceFiles())
            foreach (var line in File.ReadLines(file))
                if (line.Contains(Invocation))
                    Assert.DoesNotContain(LifecycleTokens, token => line.Contains(token));
    }

    /// Every hand-written C# file in the app and the core — the two projects that
    /// could reach StartAsync — with the build's own generated output left out.
    private static IEnumerable<string> SourceFiles()
    {
        var windows = WindowsRoot();
        return new[] { "ClaudeGraft", "ClaudeGraft.Core" }
            .Select(project => Path.Combine(windows, project))
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    /// The windows/ directory, from this test file's own path: its parent is the
    /// test project, whose parent is the tree the three projects sit in.
    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Directory.GetParent(here)!.Parent!.FullName;
}
