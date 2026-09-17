using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace ClaudeGraft;

public sealed partial class MainWindow : Window
{
    private readonly WindowChrome _chrome;
    private bool _sidebarVisible = true;

    public MainWindow()
    {
        InitializeComponent();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(false, false);
        _chrome = new WindowChrome(this, () => _sidebarVisible);
        ApplyAppearance();
        App.SettingsChanged += ApplyAppearance;
        Closed += (_, _) => { App.SettingsChanged -= ApplyAppearance; _chrome.Dispose(); };

        var scale = _chrome.Scale;
        AppWindow.Resize(new SizeInt32((int)(820 * scale), (int)(560 * scale)));
        AppWindow.Closing += (sender, args) => { args.Cancel = true; sender.Hide(); };
        RootFrame.Navigate(typeof(MainPage));

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
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();
    private void Minimize_Click(object sender, RoutedEventArgs e) { if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize(); }
    private void Zoom_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter p) return;
        if (p.State == OverlappedPresenterState.Maximized) p.Restore(); else p.Maximize();
    }
    private void NewShortcut_Click(object sender, RoutedEventArgs e) { if (RootFrame.Content is MainPage p) p.AddShortcut(); }
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is not MainPage page) return;
        _sidebarVisible = page.ToggleSidebar();
        TitleSidebarColumn.Width = new GridLength(_sidebarVisible ? 220 : 154);
        NewShortcutToolbarButton.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
    }
    private BackdropMaterial? _material;
    private void ApplyAppearance()
    {
        WindowBorder.RequestedTheme = RootGrid.RequestedTheme = Appearance.ToElementTheme(App.Settings.Theme);
        if (_material != App.Settings.Backdrop)
        {
            SystemBackdrop = Appearance.ToBackdrop(App.Settings.Backdrop);
            _material = App.Settings.Backdrop;
        }
    }
}
