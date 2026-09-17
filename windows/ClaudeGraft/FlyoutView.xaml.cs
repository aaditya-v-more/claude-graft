using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// The tray flyout's content: each profile as a compact card with its usage,
/// and the actions that used to live in the right-click menu. It raises the
/// window-level ones — showing the manager, quitting — rather than reaching for
/// the app itself, and dismisses the flyout once a card's Open is pressed.
public sealed partial class FlyoutView : UserControl
{
    public ObservableCollection<ShortcutRow> Rows { get; } = new();

    /// Raised for the actions the host window owns; the flyout only knows it
    /// wants them, not how the app carries them out.
    public event Action? OpenManagerRequested;
    public event Action? QuitRequested;
    public event Action? OpenSettingsRequested;
    public event Action? DismissRequested;

    /// The content changed size — a row added, an account's bars arriving — so
    /// the host window should measure again and refit. The view is stretched to
    /// the window, so its own SizeChanged cannot see the overflow; this says so.
    public event Action? LayoutChanged;

    public FlyoutView() => InitializeComponent();

    /// Paints the flyout's own surface opaque, for the Solid backdrop where there
    /// is no material behind it, or leaves it transparent so a material shows
    /// through. The window's mica is what the transparent case reveals.
    public void SetOpaqueSurface(bool opaque) => RootBorder.Opacity = opaque ? 1 : 0.97;

    /// Rebuilds the list and refreshes usage. Called each time the flyout opens,
    /// so the figures are current the way pressing the Mac menu bar item makes
    /// them — interactive, since a person is looking.
    public void Reload()
    {
        Rows.Clear();
        var rows = ProfileRows.Build();
        foreach (var row in rows)
        {
            Rows.Add(row);
            _ = LoadUsage(row);
        }
        LayoutChanged?.Invoke();
        _ = MarkRunning(rows);
    }

    /// The dots come in after the window is up, not before it: reading which
    /// Claude is running is a WMI query, slow enough that doing it inline made
    /// the flyout crawl open. It runs off the UI thread and lights the dots when
    /// it lands — a beat late is not something the eye catches on a status dot.
    private async Task MarkRunning(IReadOnlyList<ShortcutRow> rows)
    {
        try
        {
        var processes = await Task.Run(ClaudeProcesses.Enumerate);
        foreach (var row in rows)
            if (Rows.Contains(row)) row.SetRunning(ClaudeProcesses.IsRunning(row.ProfileDir, processes));
        }
        catch (Exception e) { foreach (var row in rows) row.Problem = e.Message; }
    }

    private async Task LoadUsage(ShortcutRow row)
    {
        var entry = await ProfileRows.ReadUsageSafe(row.ProfileDir, interactive: true);
        // Back on the UI thread after the await; the row may have been cleared
        // by a reload since.
        if (entry is not null && Rows.Contains(row))
        {
            row.SetUsage(entry);
            LayoutChanged?.Invoke();   // the bars just appeared; the flyout is taller now
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row })
        {
            var config = row.Shortcut is Shortcut s
                ? App.Store.ConfigFor(s)
                : new GraftConfig { ProfileDir = row.ProfileDir, SourceDir = null };
            Task.Run(() => ClaudeGraft.Platform.DesktopInteraction.Open(config));
            DismissRequested?.Invoke();
        }
    }

    /// Opens a five-hour window on one account by sending it a single short
    /// message, the way the Mac dropdown's per-row Start Session does. Only a
    /// press reaches here; the button gives way to a spinner while it runs and a
    /// second press is refused, so a window that is already opening is not asked
    /// for twice.
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutRow row } || row.Starting) return;
        row.Problem = null;
        row.Starting = true;
        var profile = row.ProfileDir;

        var problem = await Task.Run(() => SessionStarter.StartAsync(profile));

        // The window this just opened is exactly what the stored reading predates,
        // so it is dropped rather than waited out, then the row's usage re-read.
        UsageMonitor.Invalidate(profile);
        row.Starting = false;
        row.Problem = problem;
        if (Rows.Contains(row)) await LoadUsage(row);
        LayoutChanged?.Invoke();
    }

    /// Re-reads every account's usage from the endpoint, skipping the cache and
    /// the backoff the way the Mac's Refresh Usage does — a figure someone is
    /// looking at and pressed for.
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "Refreshing…";
        try
        {
            var rows = Rows.ToList();
            await Task.WhenAll(rows.Select(LoadUsage));
            await MarkRunning(rows);
        }
        finally
        {
            RefreshButton.Content = "Refresh Usage";
            RefreshButton.IsEnabled = true;
        }
    }

    private void Manager_Click(object sender, RoutedEventArgs e)
    {
        OpenManagerRequested?.Invoke();
        DismissRequested?.Invoke();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e) { OpenSettingsRequested?.Invoke(); DismissRequested?.Invoke(); }
    private void Support_Click(object sender, RoutedEventArgs e) { Links.Open(Links.Support); DismissRequested?.Invoke(); }
}
