using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.System;

namespace ClaudeGraft;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);

    public MainWindow()
    {
        InitializeComponent();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        // Keep the system caption and frame: Windows owns its buttons, Snap,
        // resizing, system menu and keyboard commands.
        ExtendsContentIntoTitleBar = false;
        ApplyAppearance();
        App.SettingsChanged += ApplyAppearance;
        Closed += (_, _) => App.SettingsChanged -= ApplyAppearance;

        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(480 * scale);
            presenter.PreferredMinimumHeight = (int)(360 * scale);
        }
        AppWindow.ResizeClient(new SizeInt32((int)(820 * scale), (int)(560 * scale)));
        RootFrame.Navigate(typeof(MainPage));
        RootGrid.SizeChanged += (_, _) => UpdateNavigation();

        var add = new KeyboardAccelerator { Key = VirtualKey.N, Modifiers = VirtualKeyModifiers.Control };
        add.Invoked += (_, e) => { NewShortcut_Click(this, null!); e.Handled = true; };
        RootGrid.KeyboardAccelerators.Add(add);
        var settings = new KeyboardAccelerator { Key = (VirtualKey)188, Modifiers = VirtualKeyModifiers.Control };
        settings.Invoked += (_, e) => { ShowSettings(); e.Handled = true; };
        RootGrid.KeyboardAccelerators.Add(settings);
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    public void Show() { AppWindow.Show(); Activate(); }
    public void ShowSettings()
    {
        Show();
        if (RootFrame.Content is MainPage page) _ = page.OpenSettingsAsync();
    }
    private void NewShortcut_Click(object sender, RoutedEventArgs e) { if (RootFrame.Content is MainPage p) p.AddShortcut(); }
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is not MainPage page) return;
        page.ToggleSidebar();
        UpdateNavigation();
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void UpdateNavigation()
    {
        if (RootFrame.Content is not MainPage page) return;
        page.AdaptNavigation(RootGrid.ActualWidth);
        ToolbarSidebarColumn.Width = new GridLength(page.HasInlineSidebar ? 220 : 94);
    }
    private BackdropMaterial? _material;
    private void ApplyAppearance()
    {
        RootGrid.RequestedTheme = Appearance.ToElementTheme(App.Settings.Theme);
        AppWindow.TitleBar.PreferredTheme = Appearance.ToTitleBarTheme(App.Settings.Theme);
        if (_material != App.Settings.Backdrop)
        {
            SystemBackdrop = Appearance.ToBackdrop(App.Settings.Backdrop);
            _material = App.Settings.Backdrop;
        }
    }
}
