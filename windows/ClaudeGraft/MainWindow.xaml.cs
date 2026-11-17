using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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
        ToolTipService.SetToolTip(SourceLink, Links.Source);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Square over the rail's highlights, which sit 4 in from the edge; WinUI puts it at 2.
        // WinUI rules off the footer only while the items above it overflow; the footer draws
        // its own, always, and WinUI's would sit doubled on top of it.
        Sections.Loaded += (_, _) =>
        {
            if (Named<FrameworkElement>(Sections, "VisualItemsSeparator", _ => true) is { } rule) rule.Opacity = 0;
        };
        AppTitleBar.Loaded += (_, _) =>
        {
            if (Named<Button>(AppTitleBar, "PART_PaneToggleButton", _ => true) is { } toggle)
            {
                toggle.Margin = new Thickness(4, 0, 0, 0);
                // The rail's highlight in size; it once took its height from the heading beside it.
                toggle.Width = 40;
                toggle.Height = 36;
                toggle.VerticalAlignment = VerticalAlignment.Center;
            }
        };
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
        // Tall enough for two cards without scrolling, but never past a small or
        // heavily scaled screen, and centred so the extra height is not pushed off it.
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1100 * scale), work.Width);
        var height = Math.Min((int)(670 * scale), (int)(work.Height * 0.9));
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        // No smaller than the size every page is laid out to fit; narrower, the heading and
        // the page's rows folded into a column of single letters.
        if (AppWindow.Presenter is OverlappedPresenter frame)
        {
            frame.PreferredMinimumWidth = Math.Min((int)(820 * scale), work.Width);
            frame.PreferredMinimumHeight = Math.Min((int)(560 * scale), work.Height);
        }

        // WinUI empties the window before Windows animates it out, so the close
        // animation plays over the real content first and the window goes after.
        AppWindow.Closing += async (sender, args) =>
        {
            if (_closing) return;
            args.Cancel = true;
            _closing = true;
            sender.Hide();
            await Task.Delay(300);
            Close();
        };
        RootFrame.Navigate(typeof(MainPage));
        if (RootFrame.Content is MainPage page) page.ClaudesChanged += RebuildClaudes;
        RebuildClaudes();
    }

    /// The chosen Claude's name and state, or the section's name, as the Mac's
    /// window title and subtitle.
    private void ShowHeading()
    {
        var tag = (Sections.SelectedItem as NavigationViewItem)?.Tag as string;
        (string Title, string Subtitle) heading = tag switch
        {
            "artifacts" => ("Artifacts", "Every account"),
            "projects" => ("Projects", "Every account"),
            "search" => ("Search Chats", "Every account"),
            "settings" => ("Settings", "Claude Graft"),
            _ => RootFrame.Content is MainPage page ? page.Heading : ("Claude", ""),
        };
        HeadingTitle.Text = heading.Title;
        HeadingSubtitle.Text = heading.Subtitle;
        Title = heading.Title == "Claude Graft" ? heading.Title : $"{heading.Title} · Claude Graft";
    }

    // Folded to WinUI's icon rail.
    private const double PaneWidth = 240;
    private const double RailWidth = 48;
    private bool _paneMoving, _paneOpen = true;
    private FrameworkElement? _paneItems;
    private double _headerHeight;

    // The sidebar's width is walked frame by frame, both ways, so the page and the title
    // reflow with it; WinUI's own show slid the pane alone and dropped it on hide.
    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        if (_paneMoving) return;
        _paneMoving = true;
        var opening = !_paneOpen;
        if (Named<FrameworkElement>(Sections, "PaneContentGrid", _ => true) is { } items)
        {
            items.HorizontalAlignment = HorizontalAlignment.Left;
            _paneItems = items;
        }
        var started = DateTime.UtcNow;
        void Step(object? _, object __)
        {
            var t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / 220);
            var eased = 1 - Math.Pow(1 - t, 3);
            SetPane(RailWidth + (PaneWidth - RailWidth) * (opening ? eased : 1 - eased));
            if (t < 1) return;
            CompositionTarget.Rendering -= Step;
            _paneOpen = opening;
            _paneMoving = false;
        }
        CompositionTarget.Rendering += Step;
    }

    private void SetPane(double width)
    {
        Sections.OpenPaneLength = width;
        Heading.Margin = new Thickness(width + 22, 0, 140, 0);
        // Headings and the footer would show cut off beside the icons, so they fade with the width.
        var shown = Math.Clamp((width - RailWidth) / (PaneWidth - RailWidth), 0, 1);
        ClaudesHeader.Opacity = ShortcutsHeader.Opacity = PaneFooterPanel.Opacity = SettingsRule.Opacity = shown;
        PaneFooterPanel.IsHitTestVisible = shown > 0.99;
        Theming.SelectionFill(shown);
        // The items narrow with the pane, so a highlight stops short of the page as WinUI's does;
        // their headings and second lines fold away so the icons close up toward the top.
        if (_paneItems is not null) _paneItems.Width = width;
        if (_headerHeight == 0) _headerHeight = ClaudesHeader.ActualHeight;
        ClaudesHeader.MaxHeight = ShortcutsHeader.MaxHeight = _headerHeight * shown;        foreach (var item in Sections.MenuItems.Concat(Sections.FooterMenuItems).OfType<NavigationViewItem>())
        {
            // WinUI's own fold drops the item's right-hand margin so the icon fills the highlight;
            // this fold never changes its state, so the margin goes with the width instead.
            if (Named<Grid>(item, "ContentGrid", _ => true) is { } inner) inner.Margin = new Thickness(0, 0, 14 * shown, 0);
            // A Claude row's words sit in a strip beside its Open button, so the strip fades
            // too, and goes only at the rail, where its words would wrap the row tall.
            var words = item.Content as StackPanel;
            if (item.Content is Grid strip)
            {
                strip.Opacity = shown;
                strip.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
                words = strip.Children.OfType<StackPanel>().FirstOrDefault();
            }
            if (words is { Children.Count: > 1 })
            {
                words.Margin = new Thickness(0, 6 * shown, 0, 6 * shown);
                foreach (var line in words.Children.OfType<TextBlock>())
                {
                    line.TextWrapping = TextWrapping.NoWrap;
                    line.TextTrimming = TextTrimming.CharacterEllipsis;
                }
                var second = (FrameworkElement)words.Children[1];
                second.MaxHeight = 20 * shown;
                second.Opacity = shown;
            }
        }
        ShapePageEdge();
    }

    private Grid? _contentGrid;

    // WinUI resets the frame's corner itself as the pane changes, after this has run,
    // so the frame is held to its shape whenever its corner changes.
    private void ShapePageEdge()
    {
        // The page's frame, not a sidebar item's part of the same name: the one holding the page.
        if (_contentGrid is null && Named<Grid>(Sections, "ContentGrid", g => g.ActualHeight > 200) is { } grid)
        {
            _contentGrid = grid;
            grid.RegisterPropertyChangedCallback(Grid.CornerRadiusProperty, (_, _) => ShapePageEdge());
        }
        if (_contentGrid is null) return;
        var corner = new CornerRadius(8, 0, 0, 0);
        if (_contentGrid.CornerRadius != corner) _contentGrid.CornerRadius = corner;
        var edge = new Thickness(1, 1, 0, 0);
        if (_contentGrid.BorderThickness != edge) _contentGrid.BorderThickness = edge;
    }

    private static T? Named<T>(DependencyObject root, string name, Func<T, bool> which) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && found.Name == name && which(found)) return found;
            if (Named(child, name, which) is { } deeper) return deeper;
        }
        return null;
    }

    private bool _rebuilding;
    private string _builtSignature = "";

    /// One entry per Claude, from the page's own rows, so the sidebar and the page
    /// can never disagree about which Claudes there are.
    private void RebuildClaudes()
    {
        if (RootFrame.Content is not MainPage page || _rebuilding) return;
        NewClaudeItem.Visibility = page.SetupReady ? Visibility.Visible : Visibility.Collapsed;
        // A draft is no Claude in the list yet, so nothing there is chosen.
        if (page.Drafting && Sections.SelectedItem is not null)
        {
            _rebuilding = true;
            Sections.SelectedItem = null;
            _rebuilding = false;
        }
        else if (!page.Drafting && Sections.SelectedItem is null) SelectFocused(page);
        // Entries made afresh have no indicator to slide from, so a choice that
        // changed no Claude leaves them where they are.
        var signature = string.Join("\n", page.Rows.Select(r => $"{r.Key}|{r.Name}|{r.SourceLabel}|{r.Shortcut?.Source.Kind}|{r.Shortcut?.InstalledName}"));
        if (signature == _builtSignature) return;
        _builtSignature = signature;
        _rebuilding = true;
        try
        {
            var items = Sections.MenuItems;
            foreach (var old in items.OfType<NavigationViewItem>().Where(i => ((string?)i.Tag)?.StartsWith("claude:") == true).ToList())
                items.Remove(old);
            var showingClaude = Sections.SelectedItem is not NavigationViewItem { Tag: string t } || t.StartsWith("claude:");
            // Claude heads the first section, as on the Mac; the shortcuts have their own.
            var mainAt = items.IndexOf(ClaudesHeader) + 1;
            var shortcutAt = items.IndexOf(ShortcutsHeader) + 1;
            foreach (var row in page.Rows.ToList())
            {
                var line = row.Shortcut is null ? "Installed normally"
                    : row.Shortcut.InstalledName is null ? "Not created yet" : row.SourceLabel;
                var words = new StackPanel { Spacing = 1, Margin = new Thickness(0, 6, 0, 6) };
                words.Children.Add(new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                var caption = new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
                };
                caption.SetBinding(TextBlock.TextProperty, new Binding { Source = row, Path = new PropertyPath(nameof(ShortcutRow.SidebarCaption)) });
                words.Children.Add(caption);
                var glyph = row.Shortcut is null ? "\uE80F" : row.Shortcut.Source.Kind == SourceKind.Own ? "\uEA3A" : "\uE71B";
                var openRow = row;
                var open = new Button
                {
                    Content = new FontIcon { Glyph = "", FontSize = 14 },
                    Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(4, 0, -10, 0),
                    Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                open.Content = WithOpeningRing((FontIcon)open.Content, row);
                open.SetBinding(Control.IsEnabledProperty, new Binding { Source = row, Path = new PropertyPath(nameof(ShortcutRow.CanPressOpen)) });
                ToolTipService.SetToolTip(open, $"Open {row.Name}");
                AutomationProperties.SetName(open, Spoken.Act("Open", row.Name));
                AutomationProperties.SetAutomationId(open, Spoken.Id("sidebar", row.Key, "open"));
                open.SetBinding(UIElement.VisibilityProperty, new Binding { Source = row, Path = new PropertyPath(nameof(ShortcutRow.QuickOpenVisibility)) });
                // A Button takes its own press, so opening a Claude leaves the page shown where it was.
                open.Click += async (_, _) => { if (RootFrame.Content is MainPage current) await current.OpenAsync(openRow); };
                var strip = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                strip.Children.Add(words);
                Grid.SetColumn(open, 1);
                strip.Children.Add(open);
                var item = new NavigationViewItem
                {
                    Content = strip, Tag = "claude:" + row.Key, Icon = new FontIcon { Glyph = glyph },
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                if (!_paneOpen) strip.Visibility = Visibility.Collapsed;
                AutomationProperties.SetName(item, $"{row.Name}, {line}");
                AutomationProperties.SetAutomationId(item, "sidebar." + row.Key);
                if (row.Shortcut is null)
                {
                    items.Insert(mainAt, item);
                    shortcutAt++;
                }
                else items.Insert(shortcutAt++, item);
                if (showingClaude && row.Key == page.Focused) Sections.SelectedItem = item;
            }
        }        finally { _rebuilding = false; }
    }

    /// The glyph gives way to a ring while the row opens; the button keeps its size either way.
    private static Grid WithOpeningRing(FontIcon glyph, ShortcutRow row)
    {
        var ring = new ProgressRing
        {
            Width = 16, Height = 16, IsActive = true,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAccessibilityView(ring, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        ring.SetBinding(UIElement.VisibilityProperty, new Binding { Source = row, Path = new PropertyPath(nameof(ShortcutRow.OpeningVisibility)) });
        glyph.SetBinding(UIElement.VisibilityProperty, new Binding { Source = row, Path = new PropertyPath(nameof(ShortcutRow.NotOpeningVisibility)) });
        var both = new Grid();
        both.Children.Add(glyph);
        both.Children.Add(ring);
        return both;
    }

    private void SelectFocused(MainPage page)
    {
        var tag = "claude:" + page.Focused;
        if (Sections.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag) is { } item)
        {
            _rebuilding = true;
            Sections.SelectedItem = item;
            _rebuilding = false;
        }
    }

    private async void Sections_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var tag = (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        if (RootFrame.Content is not MainPage page) return;
        if (tag == "new")
        {
            ShowClaudes();
            await page.AddAsync();
        }
    }

    private async void ClaudeRestart_Requested()
    {
        if (RootFrame.Content is MainPage page) await page.ConfirmRestartClaudesAsync();
    }

    // A stretched row is not clipped to the compact rail, so its words would spill over the page.
    private void Sections_SelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Tag as string;
        var claudes = tag is null || tag.StartsWith("claude:");
        DispatcherQueue.TryEnqueue(ShowHeading);
        if (claudes && !_rebuilding && tag is not null && RootFrame.Content is MainPage page) page.Focus(tag["claude:".Length..]);
        // Hidden, not collapsed: brought back from collapsed, the Claudes' page was laid out
        // afresh in the frames the sidebar's indicator was sliding through, and it stuttered.
        RootFrame.Opacity = claudes ? 1 : 0;
        RootFrame.IsHitTestVisible = RootFrame.IsEnabled = claudes;
        AutomationProperties.SetAccessibilityView(RootFrame, claudes ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        SectionFrame.Visibility = claudes ? Visibility.Collapsed : Visibility.Visible;
        // Navigated every time, to pages the Frame keeps, so each visit plays the opening
        // transition and a page comes back as it was left; loading again refreshes it.
        var section = tag switch { "artifacts" => typeof(ArtifactsPage), "projects" => typeof(ProjectsPage), "search" => typeof(SearchPage), "settings" => typeof(SettingsPage), _ => null };
        if (section is not null)
        {
            SectionFrame.Navigate(section, null, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
            SectionFrame.BackStack.Clear();
        }
        else if (claudes && !_rebuilding && _leftClaudes && RootFrame.Content is MainPage back)
        {
            back.Replay();
            // A blank page stands in the hidden frame, so returning to the section it held is a real
            // navigation: opening the page already shown plays no transition.
            SectionFrame.Navigate(typeof(ClaudeHostPage), null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
            SectionFrame.BackStack.Clear();
        }
        _leftClaudes = !claudes;
    }

    private bool _leftClaudes;

    // Graft is one author's app carried to Windows by another; a tip can go to either.
    private void Support_Click(object sender, RoutedEventArgs e) => ShowSupport();

    public async void ShowSupport()
    {
        Show();
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Support Claude Graft",
            CloseButtonText = "Close",
            CloseButtonStyle = (Style)Application.Current.Resources["DialogNeutralButtonStyle"],
        };
        var list = new StackPanel { Spacing = 8, Width = 340 };
        list.Children.Add(new TextBlock
        {
            Text = "Graft is free. A tip on Ko-fi keeps it worked on.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        list.Children.Add(Tip("The original author", "Made Claude Graft for the Mac", Links.SupportAuthor, "support.author", dialog));
        list.Children.Add(Tip("The Windows maintainer", "Brought it to Windows", Links.SupportWindows, "support.windows", dialog));
        dialog.Content = list;
        Dialogs.Prepare(dialog, Content.XamlRoot);
        await dialog.ShowAsync();
    }

    private static Button Tip(string who, string what, string url, string id, ContentDialog dialog)
    {
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = who });
        text.Children.Add(new TextBlock { Text = what, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var open = new FontIcon { Glyph = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Themed.Brush("TextFillColorSecondaryBrush") };
        Grid.SetColumn(open, 2);
        row.Children.Add(open);
        var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 10, 14, 10) };
        AutomationProperties.SetName(button, $"Support {who.ToLowerInvariant()} on Ko-fi");
        AutomationProperties.SetAutomationId(button, id);
        ToolTipService.SetToolTip(button, url);
        button.Click += (_, _) => { OpenLink(url); dialog.Hide(); };
        return button;
    }
    private void Source_Click(object sender, RoutedEventArgs e) => OpenLink(Links.Source);

    private static void OpenLink(string url) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));

    /// A Claude's page again, for the tray's entries that act on that page; the
    /// one already chosen stays chosen.
    private void ShowClaudes()
    {
        if (Sections.SelectedItem is NavigationViewItem { Tag: string tag } && tag.StartsWith("claude:")) return;
        var focused = RootFrame.Content is MainPage page ? "claude:" + page.Focused : "claude:main";
        Sections.SelectedItem = Sections.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == focused)
            ?? Sections.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => ((string?)i.Tag)?.StartsWith("claude:") == true);
    }

    private bool _closing, _shown;
    public bool IsClosing => _closing;

    public void Show()
    {
        // A new window is shown only once its page has been built, or it opens on an empty frame.
        if (!_shown)
        {
            _shown = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Activate);
        }
        else Activate();
        // The page loads its usage once, when it is first built; every reopen
        // after that has to ask for a fresh read itself, or a first read that
        // came up empty would never be retried. Harmless on the first show, when
        // the list is still filling and there is nothing yet to refresh.
        if (RootFrame.Content is MainPage page) page.OnShown();
    }

    public void ConfirmRestartClaudes()
    {
        Show();
        ShowClaudes();
        if (RootFrame.Content is MainPage page) _ = page.ConfirmRestartClaudesAsync();
    }

    /// The window, open on the Settings page, for the tray's Settings entry.
    public void ShowSettings()
    {
        Show();
        Sections.SelectedItem = SettingsItem;
    }

    /// Dresses the window from the current settings: theme on the content root,
    /// the chosen backdrop, and the title bar to match. Backdrop None leaves the
    /// window with no material, so the root paints an opaque themed surface —
    /// otherwise the window would be see-through onto the desktop.
    private BackdropMaterial? _appliedBackdrop;

    private void ApplyAppearance()
    {
        var settings = App.Settings;
        Theming.Apply(settings);
        Theming.SelectionFill(_paneOpen ? 1 : 0);
        Theming.Refresh(ThemeRoot, Appearance.ToElementTheme(settings.Theme));

        // Only when the material actually changes: assigning SystemBackdrop
        // re-composites the window, which reads as a flash if done for a Done that
        // left the backdrop where it was.
        if (_appliedBackdrop != settings.Backdrop)
        {
            var backdrop = Appearance.ToBackdrop(settings.Backdrop);
            SystemBackdrop = backdrop;
            _appliedBackdrop = settings.Backdrop;
        }

        if (AppWindow is not null && AppWindowTitleBar.IsCustomizationSupported())
            AppWindow.TitleBar.PreferredTheme = settings.Theme switch
            {
                AppTheme.Dark => TitleBarTheme.Dark,
                AppTheme.Light => TitleBarTheme.Light,
                // Not Application.RequestedTheme, which is fixed at startup and
                // left the bar on the old theme after Windows switched.
                _ => TitleBarTheme.UseDefaultAppMode,
            };
    }
}
