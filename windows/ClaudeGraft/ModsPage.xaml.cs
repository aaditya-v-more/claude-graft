using System.Diagnostics;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// <summary>
/// The mods in Graft's folder, each with a box per Claude. A choice reaches a
/// Claude the next time Graft finds it closed.
/// </summary>
public sealed partial class ModsPage : Page
{
    public const string Note =
        "A mod changes how Claude Code looks or behaves. Mods in Claude Code's own folders are listed by themselves, "
        + "and Add Mod copies one in from anywhere else; the boxes choose which Claudes load each. A Claude that is open takes its mods when it next starts.\n\n"
        + "A mod runs code on this computer, so add only mods you trust. Mods you set for every Claude in Claude Code's "
        + "own settings keep loading beside these.";

    public ModsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        NoteText.Text = Note;
        Loaded += (_, _) =>
        {
            App.ModsChanged += Render;
            Render();
        };
        Unloaded += (_, _) => App.ModsChanged -= Render;
    }

    private static List<(string Name, string Profile)> Claudes()
    {
        var claudes = new List<(string, string)> { ("Claude", GraftPaths.DefaultProfile) };
        claudes.AddRange(App.Store.Shortcuts.ToList().Select(s => (s.Name, s.ProfileDir)));
        return claudes;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ModLibrary.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ModLibrary.Directory}\"") { UseShellExecute = false })?.Dispose();
    }

    private async void AddMod_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        var (_, problem) = await Task.Run(() => ModLibrary.Add(folder.Path));
        if (problem is not null)
            await new ContentDialog { XamlRoot = XamlRoot, Title = "Mod Not Added", Content = problem, CloseButtonText = "OK" }.ShowAsync();
        Render();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Listing) _ = LoadCatalogue();
        else Render();
    }

    private bool Browsing => Views.SelectedItem == BrowseItem;
    private bool Listing => Browsing || Views.SelectedItem == FeaturedItem;
    private ModCatalog.Catalogue? _catalogue;
    private string? _catalogueProblem;
    private bool _loading;
    private readonly HashSet<string> _installing = new();
    private const int PageSize = 40;
    private int _shown = PageSize;

    private void Views_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        if (Listing && _catalogue is null && !_loading) _ = LoadCatalogue();
        else Render();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _shown = PageSize;
        if (IsLoaded) Render();
    }

    private async Task LoadCatalogue()
    {
        _loading = true;
        Render();
        try
        {
            _catalogue = await Task.Run(() => ModCatalog.Load());
            _catalogueProblem = null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException)
        {
            _catalogueProblem = "The list could not be read: " + e.Message;
        }
        _loading = false;
        Render();
    }

    private static TextBlock Quiet(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GraftTextFillColorSecondaryBrush"],
    };

    private static string Short(string commit, int length) => commit[..Math.Min(length, commit.Length)];

    public const string OverlapText =
        "May clash with Graft: it handles accounts, sign-ins, sessions or where requests go, which Graft also manages.";

    private static InfoBar Overlap()
    {
        var bar = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false, Message = OverlapText };
        AutomationProperties.SetName(bar, OverlapText);
        return bar;
    }

    private static Border Card(UIElement content) => new()
    {
        Child = content, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
    };

    private ModCatalog.Filter CurrentFilter() => new(
        SearchBox.Text.Trim(),
        ReachBox.SelectedIndex switch { 1 => 0, 2 => 1, 3 => 2, _ => null },
        PassedBox.IsChecked == true,
        SortBox.SelectedIndex switch { 1 => ModCatalog.Sort.Updated, 2 => ModCatalog.Sort.Name, _ => ModCatalog.Sort.Stars },
        DesktopBox.IsChecked == true);

    private void RenderListing()
    {
        var credit = new HyperlinkButton { Content = "Mods from awesome-claude-code-mods by karanb192", Padding = new Thickness(0),
            NavigateUri = new Uri(ModCatalog.ListPage) };
        AutomationProperties.SetAutomationId(credit, "mods.list.credit");
        ToolTipService.SetToolTip(credit, ModCatalog.ListPage);
        Rows.Children.Add(credit);
        Rows.Children.Add(Quiet("A community list, scanned independently. What each mod reaches comes from that scan; read a mod's source before you install it."));
        if (_loading)
        {
            Rows.Children.Add(new ProgressRing { IsActive = true, HorizontalAlignment = HorizontalAlignment.Left });
            return;
        }
        if (_catalogueProblem is not null || _catalogue is null)
        {
            Rows.Children.Add(Quiet(_catalogueProblem ?? "The list has not been read yet."));
            return;
        }
        var installed = ModLibrary.List().Where(m => m.Origin == ModLibrary.Origin.Catalogue)
            .Select(m => ModCatalog.SourceOf(m.Folder)).OfType<ModCatalog.Source>().ToList();
        if (!Browsing)
        {
            foreach (var entry in _catalogue.Featured)
                Rows.Children.Add(ListedCard(entry.Featured.Name, entry.Featured.Url, entry.Featured.Description, entry.Scan, installed));
            return;
        }
        var found = ModCatalog.Browse(_catalogue.All, CurrentFilter());
        Rows.Children.Add(Quiet(found.Count == 0 ? "No mod matches." : $"{found.Count:N0} mods"));
        foreach (var mod in found.Take(_shown))
            Rows.Children.Add(ListedCard(mod.Name, mod.Url.Length > 0 ? mod.Url : $"https://github.com/{mod.Repo}", mod.Description, mod, installed));
        if (found.Count > _shown)
        {
            var more = new Button { Content = $"Show {Math.Min(PageSize, found.Count - _shown)} More", HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetAutomationId(more, "mods.browse.more");
            more.Click += (_, _) =>
            {
                _shown += PageSize;
                Render();
            };
            Rows.Children.Add(more);
        }
    }

    private Border ListedCard(string name, string url, string description, ModCatalog.Scanned? scan, List<ModCatalog.Source> installed)
    {
        var id = scan?.Id is { Length: > 0 } scanned ? scanned : name;
        var body = new StackPanel { Spacing = 4 };
        var title = new Grid { ColumnSpacing = 8 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        names.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (scan is not null)
            names.Children.Add(new TextBlock { Text = scan.Stars > 0 ? $"{scan.Repo} · ★ {scan.Stars:N0}" : scan.Repo, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        title.Children.Add(names);
        var source = new HyperlinkButton { Content = "Source", Padding = new Thickness(4, 0, 4, 0),
            NavigateUri = Uri.TryCreate(url, UriKind.Absolute, out var link) && link.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ? link : null };
        AutomationProperties.SetName(source, $"Source of {name}");
        AutomationProperties.SetAutomationId(source, $"mods.list.{id}.source");
        Grid.SetColumn(source, 1);
        title.Children.Add(source);
        var have = scan is not null && installed.Any(i => i.Repo.Equals(scan.Repo, StringComparison.OrdinalIgnoreCase) && i.Path == scan.Path);
        var busy = _installing.Contains(id);
        var install = new Button { Content = busy ? "Installing…" : have ? "Install Again…" : "Install…", IsEnabled = scan is not null && !busy };
        AutomationProperties.SetName(install, $"Install {name}");
        AutomationProperties.SetAutomationId(install, $"mods.list.{id}.install");
        if (scan is not null) install.Click += (_, _) => _ = AskInstall(name, id, scan);
        Grid.SetColumn(install, 2);
        title.Children.Add(install);
        body.Children.Add(title);
        body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        if (scan is not null && ModCatalog.ManagesAccountsOrSessions(scan)) body.Children.Add(Overlap());
        body.Children.Add(Quiet(scan is null
            ? "Not in the latest scan, so Graft cannot say what it reaches or which commit to install."
            : $"Reaches: {(scan.Reach.Count == 0 ? "nothing reported" : string.Join(", ", scan.Reach))} · scanned at {Short(scan.Commit, 7)}"
              + (scan.Updated is { } updated ? $" · updated {updated.LocalDateTime:d}" : "")));
        return Card(body);
    }

    private async Task AskInstall(string name, string id, ModCatalog.Scanned scan)
    {
        var text = $"Downloads {scan.Repo}{(scan.Path.Length > 0 ? "/" + scan.Path : "")} at commit {Short(scan.Commit, 12)} from GitHub "
            + "and checks it with Claude Code's validator.\n\n"
            + $"The list's scan reports it reaches: {(scan.Reach.Count == 0 ? "nothing" : string.Join(", ", scan.Reach))}. "
            + "A mod runs code inside your Claude sessions, and the scan does not prove it safe.\n\n"
            + "Once installed it loads in no Claude until you tick one.";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Install {name}?", Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Install", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _installing.Add(id);
        Render();
        var (_, problem) = await Task.Run(() => ModCatalog.Install(scan, warning => OnUi(() => Confirm($"Install {name} anyway?", warning
            + " Graft read this from the mod's code, which can be wrong; it installs either way if you go on.", "Install Anyway"))));
        _installing.Remove(id);
        if (problem is not null) await Tell("Mod Not Installed", problem);
        Render();
    }

    private void Render()
    {
        Rows.Children.Clear();
        FilterBar.Visibility = Browsing ? Visibility.Visible : Visibility.Collapsed;
        if (Listing)
        {
            RenderListing();
            return;
        }
        var mods = ModLibrary.List();
        var choices = ModChoices.Load();
        if (mods.Count == 0 || choices is null)
        {
            Rows.Children.Add(new TextBlock
            {
                Text = choices is null ? "The list of chosen mods could not be read, so nothing is changed."
                    : "No mods found. Mods Claude Code makes show here by themselves, and Add Mod copies one in from any folder.",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GraftTextFillColorSecondaryBrush"],
            });
            return;
        }
        var shared = ModLibrary.SharedFolders();
        foreach (var mod in mods)
        {
            var row = new StackPanel { Spacing = 4 };
            var title = new Grid { ColumnSpacing = 8 };
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.Children.Add(new TextBlock { Text = mod.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            var delete = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(6),
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
            ToolTipService.SetToolTip(delete, "Move this mod to the Recycle Bin");
            AutomationProperties.SetName(delete, $"Delete {mod.Name}");
            AutomationProperties.SetAutomationId(delete, $"mods.{mod.Name}.delete");
            delete.Click += (_, _) => _ = AskDelete(mod);
            Grid.SetColumn(delete, 1);
            title.Children.Add(delete);
            row.Children.Add(title);
            var everywhere = shared.Contains(mod.Folder, StringComparer.OrdinalIgnoreCase);
            var caption = string.Join(" · ", new[] { mod.Description, Origin(mod, everywhere) }.Where(t => t.Length > 0));
            row.Children.Add(new TextBlock { Text = caption, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
            var boxes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            foreach (var (name, profile) in Claudes())
            {
                var key = PoolEnvironment.Key(profile);
                var on = everywhere
                    ? !(choices.Off.GetValueOrDefault(key)?.Contains(mod.Name, StringComparer.OrdinalIgnoreCase) ?? false)
                    : choices.Profiles.GetValueOrDefault(key)?.Contains(mod.Name, StringComparer.OrdinalIgnoreCase) ?? false;
                var box = new CheckBox { Content = Label(name, key, on), IsChecked = on, MinWidth = 0 };
                AutomationProperties.SetName(box, $"Load {mod.Name} in {name}");
                AutomationProperties.SetAutomationId(box, $"mods.{mod.Name}.{Path.GetFileName(profile)}");
                box.Click += (_, _) => _ = Choose(key, mod, box.IsChecked == true, everywhere);
                boxes.Children.Add(box);
            }
            row.Children.Add(boxes);
            Rows.Children.Add(Card(row));
        }
    }

    private static string Origin(ModLibrary.Mod mod, bool everywhere) => everywhere
        ? "On for every Claude in Claude Code's settings; untick one to leave it out there"
        : mod.Origin switch
        {
            ModLibrary.Origin.Library => "Kept by Graft",
            ModLibrary.Origin.Catalogue => ModCatalog.SourceOf(mod.Folder) is { } from
                ? $"Installed from {from.Repo} at {Short(from.Commit, 7)}" : "Installed from the list",
            ModLibrary.Origin.InProgress => "Made in Claude Code, and on in the session that made it; ticking a Claude keeps a copy",
            _ => "Made in Claude Code",
        };

    private static string Label(string name, string key, bool on) => App.ModStates.GetValueOrDefault(key) switch
    {
        ModEnvironment.State.Waiting => $"{name} (at next start)",
        ModEnvironment.State.Conflict when on => $"{name} (has its own list)",
        ModEnvironment.State.Unreadable when on => $"{name} (unreadable)",
        _ => name,
    };

    private async Task Choose(string key, ModLibrary.Mod mod, bool on, bool everywhere)
    {
        if (on && !everywhere && await Task.Run(() => ModLibrary.Keep(mod)) is string problem)
        {
            await Tell("Mod Not Kept", problem);
            Render();
            return;
        }
        if (ModChoices.Load() is not { } choices) return;
        // A settings mod is on unless left out; any other is off unless chosen.
        var lists = everywhere ? choices.Off : choices.Profiles;
        var list = lists.GetValueOrDefault(key) ?? new();
        list.RemoveAll(m => m.Equals(mod.Name, StringComparison.OrdinalIgnoreCase));
        if (on != everywhere) list.Add(mod.Name);
        if (list.Count == 0) lists.Remove(key);
        else lists[key] = list;
        choices.Save();
        Render();
        App.Instance.SyncSharingSoon();
    }

    private async Task AskDelete(ModLibrary.Mod mod)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Delete {mod.Name}?",
            Content = $"{mod.Folder} goes to the Recycle Bin, and every Claude that loads it stops at its next start.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (await Task.Run(() => ModLibrary.Delete(mod)) is string problem) await Tell("Mod Not Deleted", problem);
        Render();
        App.Instance.SyncSharingSoon();
    }

    private async Task<bool> Confirm(string title, string text, string go)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = go, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // The install runs off the UI thread, and a dialog can only be shown on it.
    private Task<T> OnUi<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>();
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { done.SetResult(await work()); }
                catch (Exception e) { done.SetException(e); }
            }))
            done.SetCanceled();
        return done.Task;
    }

    private Task Tell(string title, string text) =>
        new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = text, CloseButtonText = "OK" }.ShowAsync().AsTask();
}
