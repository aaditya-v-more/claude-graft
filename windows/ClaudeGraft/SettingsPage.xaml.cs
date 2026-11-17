using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClaudeGraft;

/// <summary>
/// The app's preferences as a page of their own, each applied the moment it changes,
/// as Windows 11's are. Auto-start is read from and written to the Startup folder
/// rather than the settings file, so the switch shows the real state.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private const string AboutLine = "Keeps reporting usage in the notification area after its window is closed; quit it from there";
    private bool _loading;
    private Updater.Release? _update;

    public SettingsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        AboutCard.Header = $"Claude Graft {Updater.Shown}";
        AboutCard.GlyphTapped += CountTowardDebug;
        Loaded += (_, _) => App.UpdateProgressChanged += ReflectDownload;
        Unloaded += (_, _) => App.UpdateProgressChanged -= ReflectDownload;
    }

    // Read again on every visit: the tray, the main card and Windows itself change some of these.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        var current = App.Settings;
        // The combo order matches the enum order, so the choice round-trips through the index.
        ThemeBox.SelectedIndex = (int)current.Theme;
        BackdropBox.SelectedIndex = (int)current.Backdrop;
        DrawPalettes();
        PaletteGrid.SelectedIndex = (int)current.Palette;
        AutoStartSwitch.IsOn = AutoStart.IsEnabled();
        StartHiddenSwitch.IsOn = current.StartHidden;
        HoldUpdatesSwitch.IsOn = current.HoldUpdates;
        TaskbarStripSwitch.IsOn = current.TaskbarStrip;
        ChimeSwitch.IsOn = current.Chime;
        // Only an install Graft can update by hand can safely stop updating itself.
        ManualClaudeCard.Visibility = ClaudeDesktopUpdates.IsSquirrel ? Visibility.Visible : Visibility.Collapsed;
        try { ManualClaudeSwitch.IsOn = ManualUpdates.Enabled; ManualClaudeSwitch.IsEnabled = true; }
        catch (IOException) { ManualClaudeSwitch.IsEnabled = false; }
        _loading = false;
        ReflectUpdate();
        ReflectDownload();
    }

    private void Changed(object sender, object e)
    {
        if (_loading) return;
        var current = App.Settings;
        App.ApplySettings(new GraftSettings
        {
            Theme = (AppTheme)Math.Max(0, ThemeBox.SelectedIndex),
            Backdrop = (BackdropMaterial)Math.Max(0, BackdropBox.SelectedIndex),
            Palette = (AppPalette)Math.Max(0, PaletteGrid.SelectedIndex),
            StartHidden = StartHiddenSwitch.IsOn,
            HoldUpdates = HoldUpdatesSwitch.IsOn,
            TaskbarStrip = TaskbarStripSwitch.IsOn,
            Chime = ChimeSwitch.IsOn,
            // Set elsewhere, but a save must not drop them.
            KeepMainWarm = current.KeepMainWarm,
            SharingExplained = current.SharingExplained,
        });
    }

    // The tiles show the mode that will be in effect, so changing it repaints them.
    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaletteGrid.Items.Count > 0)
        {
            var chosen = PaletteGrid.SelectedIndex;
            var was = _loading;
            _loading = true;
            DrawPalettes();
            PaletteGrid.SelectedIndex = chosen;
            _loading = was;
        }
        Changed(sender, e);
    }

    private void AutoStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) AutoStart.Set(AutoStartSwitch.IsOn);
    }

    private int _debugTaps;
    private DateTime _lastDebugTap;

    /// Seven taps on About's icon, each within two seconds of the last.
    private void CountTowardDebug()
    {
        var now = DateTime.UtcNow;
        _debugTaps = now - _lastDebugTap < TimeSpan.FromSeconds(2) ? _debugTaps + 1 : 1;
        _lastDebugTap = now;
        if (_debugTaps < 7) return;
        _debugTaps = 0;
        DebugWindow.Show();
    }

    private async void ManualClaude_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var wanted = ManualClaudeSwitch.IsOn;
        try { await Task.Run(() => ManualUpdates.SetEnabled(wanted)); }
        catch (IOException ex)
        {
            _loading = true;
            ManualClaudeSwitch.IsOn = !wanted;
            _loading = false;
            ManualClaudeCard.Description = ex is StateBusyException
                ? "Claude's updates were left as they were: " + ex.Message
                : "Claude's updates were left as they were: its settings could not be written.";
        }
        ClaudeUpdateCenter.Redraw();
    }

    private void ReflectUpdate()
    {
        _update = App.AvailableUpdate;
        CheckButton.IsEnabled = true;
        CheckButton.Content = _update is null ? "Check for Updates" : $"Install Update {_update.Version}";
        ShowStatus(_update is null ? null : $"Version {_update.Version} is available.");
    }

    private void ReflectDownload()
    {
        if (App.UpdateDownload is not { } fraction) return;
        CheckButton.IsEnabled = false;
        CheckButton.Content = $"Downloading… {(int)Math.Round(fraction * 100)}%";
    }

    // Unlike the flyout, which stays quiet like the Mac dropdown, this page reports the
    // outcome in words: a person who pressed is owed an answer.
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not null)
        {
            CheckButton.IsEnabled = false;
            CheckButton.Content = "Downloading…";
            if (!await App.Instance.InstallUpdateAsync(_update))
            {
                ReflectUpdate();
                ShowStatus("The update could not be downloaded.");
            }
            return;
        }

        CheckButton.IsEnabled = false;
        CheckButton.Content = "Checking for Updates…";
        ShowStatus(null);
        try
        {
            var found = await App.Instance.RunUpdateCheckAsync();
            ReflectUpdate();
            if (found is null) ShowStatus("You’re up to date.");
        }
        catch (GraftUpdates.NotInstalledException)
        {
            ReflectUpdate();
            ShowStatus("This copy wasn’t installed by Claude Graft’s setup, so it can’t update itself.");
        }
        catch
        {
            ReflectUpdate();
            ShowStatus("Couldn’t reach the update server.");
        }
    }

    private void ShowStatus(string? text) => AboutCard.Description = text ?? AboutLine;

    /// Each scheme as Windows shows a theme: a small window in its own colours, its
    /// sidebar, a card on its content and a bar of its accent.
    private void DrawPalettes()
    {
        var dark = (AppTheme)Math.Max(0, ThemeBox.SelectedIndex) switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R < 128,
        };
        static Microsoft.UI.Xaml.Media.SolidColorBrush Paint(Rgb c) => new(Windows.UI.Color.FromArgb(0xFF, c.R, c.G, c.B));

        PaletteGrid.Items.Clear();
        foreach (var palette in Enum.GetValues<AppPalette>())
        {
            var c = Palettes.For(palette, dark, palette == AppPalette.Accent ? SystemAccent() : null);
            var window = new Grid { Width = 98, Height = 62, Background = Paint(c.Content) };
            window.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            window.ColumnDefinitions.Add(new ColumnDefinition());
            window.Children.Add(new Border { Background = Paint(c.Sidebar) });
            var card = new Border
            {
                Background = Paint(c.Card), BorderBrush = Paint(c.Divider), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Margin = new Thickness(26, 10, 8, 10),
                Child = new Border
                {
                    Background = Paint(c.Accent), CornerRadius = new CornerRadius(2), Width = 18, Height = 5,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 5, 5),
                },
            };
            Grid.SetColumnSpan(card, 2);
            window.Children.Add(card);
            var tile = new Border { CornerRadius = new CornerRadius(4), Child = window };
            ToolTipService.SetToolTip(tile, Palettes.Title(palette));
            var item = new GridViewItem { Content = tile, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, Palettes.Title(palette));
            PaletteGrid.Items.Add(item);
        }
    }

    private static Rgb SystemAccent()
    {
        var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        return new Rgb(c.R, c.G, c.B);
    }
}
