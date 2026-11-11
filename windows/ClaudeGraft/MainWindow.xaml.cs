using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.System;

namespace ClaudeGraft;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    private readonly Dictionary<string, object> _pages = new();
    private bool _refreshing;
    private bool _settingsOpen;
    private string _currentPage = "profile:";
    public MainWindow()
    {
        InitializeComponent();
        if (Navigation.SettingsItem is DependencyObject settingsItem)
            AutomationProperties.SetAutomationId(settingsItem, "Navigation.settings");
        Navigation.Loaded += (_, _) =>
        {
            if (Navigation.SettingsItem is DependencyObject readySettings)
                AutomationProperties.SetAutomationId(readySettings, "Navigation.settings");
        };
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        ExtendsContentIntoTitleBar = false;
        ApplyAppearance();
        App.SettingsChanged += ApplyAppearance;
        App.ProfilesChanged += ProfilesChanged;
        Closed += (_, _) =>
        {
            App.SettingsChanged -= ApplyAppearance;
            App.ProfilesChanged -= ProfilesChanged;
            foreach (var page in _pages.Values.OfType<IDisposable>()) page.Dispose();
            ProjectCenter.Release(App.Accounts);
        };
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(820 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
        }
        AppWindow.ResizeClient(new SizeInt32((int)(1000 * scale), (int)(680 * scale)));
        RefreshNavigation();
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(i => (string?)i.Tag == "profile:");
        Navigate("profile:", "Claude");
        var add = new KeyboardAccelerator { Key = VirtualKey.N, Modifiers = VirtualKeyModifiers.Control };
        add.Invoked += (_, e) => { NewShortcut_Click(this, null!); e.Handled = true; };
        RootGrid.KeyboardAccelerators.Add(add);
        var settings = new KeyboardAccelerator { Key = (VirtualKey)188, Modifiers = VirtualKeyModifiers.Control };
        settings.Invoked += (_, e) => { ShowSettings(); e.Handled = true; };
        RootGrid.KeyboardAccelerators.Add(settings);
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }
    private void ProfilesChanged(object? sender, EventArgs e)
    {
        RefreshNavigation();
        if (RootFrame.Content is not MainPage page) return;
        var profile = page.SelectedProfileId is Guid id ? App.Store.Get(id) : null;
        _currentPage = "profile:" + profile?.Id.ToString();
        _refreshing = true;
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == _currentPage);
        _refreshing = false;
        PageTitle.Text = profile?.Name ?? "Claude";
    }
    private void RefreshNavigation()
    {
        var selected = (Navigation.SelectedItem as NavigationViewItem)?.Tag as string;
        _refreshing = true;
        Navigation.MenuItems.Clear();
        Add("Shared usage", "usage", Symbol.Switch);
        Add("Artifacts", "artifacts", Symbol.Document);
        Add("Projects", "projects", Symbol.Folder);
        Add("Search Chats", "search", Symbol.Find);
        Navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Claudes" });
        Add("Claude", "profile:", Symbol.Contact);
        foreach (var profile in App.Store.Shortcuts) Add(profile.Name, "profile:" + profile.Id, Symbol.Contact);
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == selected);
        _refreshing = false;
    }
    private void Add(string name, string tag, Symbol symbol)
    {
        var item = new NavigationViewItem { Content = name, Tag = tag, Icon = new SymbolIcon(symbol) };
        AutomationProperties.SetName(item, name);
        AutomationProperties.SetAutomationId(item, "Navigation." + tag);
        Navigation.MenuItems.Add(item);
    }
    private async void Navigation_Changed(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_refreshing) return;
        if (args.IsSettingsSelected)
        {
            await SettingsAsync();
            _refreshing = true;
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == _currentPage);
            _refreshing = false;
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } item) Navigate(tag, item.Content?.ToString() ?? "Claude Graft");
    }
    private void Navigate(string tag, string title)
    {
        _currentPage = tag;
        PageTitle.Text = title;
        if (tag.StartsWith("profile:"))
        {
            var page = ProfilesPage();
            page.UseShellNavigation(Guid.TryParse(tag[8..], out var id) ? id : null);
            RootFrame.Content = page;
            return;
        }
        if (!_pages.TryGetValue(tag, out var content))
        {
            content = tag switch
            {
                "artifacts" => new ArtifactsView(App.Accounts),
                "projects" => new ProjectsPage(),
                "search" => new SearchChatsPage(),
                "usage" => new SharedUsageView(App.UsagePool),
                _ => ProfilesPage()
            };
            _pages[tag] = content;
        }
        RootFrame.Content = content;
    }
    private MainPage ProfilesPage()
    {
        if (!_pages.TryGetValue("profiles", out var page)) _pages["profiles"] = page = new MainPage();
        return (MainPage)page;
    }
    public void Show() { AppWindow.Show(); Activate(); }
    public void ShowSettings() { Show(); _ = SettingsAsync(); }
    private async Task SettingsAsync()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            if (!RootGrid.IsLoaded)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler? loaded = null;
                loaded = (_, _) => { RootGrid.Loaded -= loaded; ready.TrySetResult(); };
                RootGrid.Loaded += loaded;
                await ready.Task;
            }
            var dialog = new SettingsDialog(App.Settings) { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
            ContentDialogResult action;
            do
            {
                dialog.RequestedClaudeUpdates = false;
                action = await dialog.ShowAsync();
                if (dialog.RequestedClaudeUpdates) await ClaudeUpdateDialog.ShowAsync(RootGrid.XamlRoot);
            } while (dialog.RequestedClaudeUpdates);
            if (action != ContentDialogResult.Primary) return;
            try { AutoStart.Set(dialog.AutoStartEnabled); App.ApplySettings(dialog.Result); }
            catch (Exception error)
            {
                await new ContentDialog { Title = "Settings could not be saved", Content = error.Message,
                    CloseButtonText = "Close", XamlRoot = RootGrid.XamlRoot }.ShowAsync();
            }
        }
        finally { _settingsOpen = false; }
    }
    private void NewShortcut_Click(object sender, RoutedEventArgs e)
    {
        var page = ProfilesPage();
        _currentPage = "";
        Navigation.SelectedItem = null;
        page.UseShellNavigation(null);
        RootFrame.Content = page;
        PageTitle.Text = "New Claude";
        if (page.IsLoaded) page.AddShortcut();
        else
        {
            RoutedEventHandler? ready = null;
            ready = (_, _) => { page.Loaded -= ready; page.AddShortcut(); };
            page.Loaded += ready;
        }
    }
    private void Website_Click(object sender, RoutedEventArgs e) => Links.Open(Links.Website);
    private void Source_Click(object sender, RoutedEventArgs e) => Links.Open(Links.Source);
    private void Support_Click(object sender, RoutedEventArgs e) => Links.Open(Links.Support);
    private BackdropMaterial? _material;
    private void ApplyAppearance()
    {
        RootGrid.RequestedTheme = Appearance.ToElementTheme(App.Settings.Theme);
        AppWindow.TitleBar.PreferredTheme = Appearance.ToTitleBarTheme(App.Settings.Theme);
        if (_material != App.Settings.Backdrop) { SystemBackdrop = Appearance.ToBackdrop(App.Settings.Backdrop); _material = App.Settings.Backdrop; }
    }
}
