using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ClaudeGraft;

/// <summary>
/// The profile manager. Closing it hides it back to the tray rather than
/// quitting — the app keeps running in the notification area, as the Mac build
/// keeps running in the menu bar. Quit is a deliberate act from the tray menu.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // The icon ships packed as an app resource, not a loose file, so this
        // path is absent in an unpackaged install — and SetIcon throwing on it
        // used to abort the rest of the constructor, navigation included, which
        // is a window that opens with no content behind the title bar. A window
        // titled by its resource icon is fine without this; a missing decoration
        // must not cost the profile list.
        var iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets/AppIcon.ico");
        if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        ApplyAppearance();
        App.SettingsChanged += ApplyAppearance;
        // The window only hides, so this outlives every close, but drop the
        // handler if it is ever truly closed rather than leak onto a dead window.
        Closed += (_, _) => App.SettingsChanged -= ApplyAppearance;

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(960 * scale), (int)(800 * scale)));

        // Closing hides to the tray instead of quitting; Window.Closed is too
        // late to cancel, so this is on AppWindow.Closing.
        AppWindow.Closing += (sender, args) => { args.Cancel = true; sender.Hide(); };

        RootFrame.Navigate(typeof(MainPage));
    }

    public void Show()
    {
        AppWindow.Show();
        Activate();
    }

    /// Brings the window up and opens the settings dialog on it — the tray's
    /// Settings entry has no window of its own to show one in.
    public void ShowSettings()
    {
        Show();
        if (RootFrame.Content is MainPage page) _ = page.OpenSettingsAsync();
    }

    /// Dresses the window from the current settings: theme on the content root,
    /// the chosen backdrop, and the title bar to match. Backdrop None leaves the
    /// window with no material, so the root paints an opaque themed surface —
    /// otherwise the window would be see-through onto the desktop.
    private BackdropMaterial? _appliedBackdrop;

    private void ApplyAppearance()
    {
        var settings = App.Settings;
        RootGrid.RequestedTheme = Appearance.ToElementTheme(settings.Theme);

        // Only when the material actually changes: assigning SystemBackdrop
        // re-composites the window, which reads as a flash if done for a Done that
        // left the backdrop where it was.
        if (_appliedBackdrop != settings.Backdrop)
        {
            var backdrop = Appearance.ToBackdrop(settings.Backdrop);
            SystemBackdrop = backdrop;
            RootGrid.Background = backdrop is null
                ? (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"]
                : null;
            _appliedBackdrop = settings.Backdrop;
        }

        if (AppWindow is not null && AppWindowTitleBar.IsCustomizationSupported())
            AppWindow.TitleBar.PreferredTheme = IsDark(settings.Theme)
                ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    /// Whether the resolved theme is dark — the explicit choice, or what Windows
    /// is set to when the choice is System.
    private static bool IsDark(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };
}
