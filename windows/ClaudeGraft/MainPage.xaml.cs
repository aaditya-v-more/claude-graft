using System.Collections.ObjectModel;
using System.Diagnostics;
using ClaudeGraft.Core;
using ClaudeGraft.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

public sealed partial class MainPage : Page
{
    public ObservableCollection<ShortcutRow> Shortcuts { get; } = new();
    public ObservableCollection<IconChoice> IconPresets { get; } = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ShortcutRow _main = null!;
    private ShortcutRow? _selected;
    private Shortcut? _original;
    private Shortcut? _draft;
    private bool _loading;
    private bool _busy;
    private bool _loaded;
    private string _lastName = "";

    public MainPage()
    {
        InitializeComponent();
        Problem.Closed += (_, _) => Problem.Visibility = Visibility.Collapsed;
        Loaded += async (_, _) =>
        {
            _loaded = true;
            Reload(); _timer.Start(); _ = LoadIcons();
            if (GraftPaths.ProfilesRootOverride is not null && Environment.GetCommandLineArgs().Contains("--review-settings"))
                await OpenSettingsAsync();
        };
        Unloaded += (_, _) => { _loaded = false; _timer.Stop(); };
        _timer.Tick += async (_, _) => await MarkRunning();
    }

    private void Reload(Guid? select = null)
    {
        App.Store.Load();
        Shortcuts.Clear();
        foreach (var shortcut in App.Store.Shortcuts) Shortcuts.Add(ShortcutRow.ForShortcut(shortcut));
        _main = ShortcutRow.MainProfile();
        Select(select is Guid id ? Shortcuts.FirstOrDefault(s => s.Shortcut?.Id == id) ?? _main : _main);
        if (App.Store.LoadError is not null) ShowProblem(App.Store.LoadError);
        _ = MarkRunning();
    }

    private void Select(ShortcutRow row)
    {
        _loading = true;
        _selected = row;
        _original = row.Shortcut;
        _draft = row.Shortcut is null ? null : new Shortcut
        {
            Id = row.Shortcut.Id, Name = row.Name, Folder = row.Folder, Source = row.Shortcut.Source,
            InstalledName = row.Shortcut.InstalledName, IconPreset = row.Shortcut.IconPreset
        };
        DetailPanel.DataContext = row;
        ShortcutHeader.Text = _draft is null ? "Claude" : "Shortcut";
        _lastName = NameBox.Text = row.Name;
        FolderBox.Text = row.Folder;
        NameBox.IsReadOnly = FolderBox.IsReadOnly = _draft is null;
        var editable = _draft is null ? Visibility.Collapsed : Visibility.Visible;
        IconSection.Visibility = ChatsSection.Visibility = DeleteButton.Visibility =
            SaveButton.Visibility = ShortcutStatusRow.Visibility = StatusDivider.Visibility = editable;
        IconBox.SelectedItem = IconPresets.FirstOrDefault(i => i.Name == _draft?.IconPreset);
        IconLabel.Text = _draft?.IconPreset ?? "Original";
        if (_draft is not null)
        {
            SourceBox.ItemsSource = App.Store.AvailableSources(_draft)
                .Select(s => new SourceOption { Label = App.Store.Label(s), Source = s }).ToList();
            SourceBox.SelectedItem = ((List<SourceOption>)SourceBox.ItemsSource)
                .FirstOrDefault(s => s.Source.Equals(_draft.Source));
        }
        else SourceBox.ItemsSource = null;
        var created = _draft?.InstalledName is not null;
        SaveButton.Content = created ? "Update Shortcut" : "Create Shortcut";
        DeleteButton.Content = created ? "Delete Shortcut…" : "Discard";
        OpenButton.IsEnabled = _draft is null || created;
        SignInButton.IsEnabled = OpenButton.IsEnabled;
        ShortcutStatus.Text = created ? Installer.InstalledLink(_draft!) ?? "Shortcut needs updating" : "Not created yet";
        Problem.IsOpen = false;
        Problem.Visibility = Visibility.Collapsed;
        ShortcutList.SelectedItem = row == _main ? null : row;
        if (row == _main)
        {
            MainButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 101, 220));
            MainButton.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        }
        else
        {
            MainButton.ClearValue(BackgroundProperty);
            MainButton.ClearValue(ForegroundProperty);
        }
        _loading = false;
        Source_Changed(null!, null!);
        _ = LoadUsage(row, false);
    }

    private void Main_Click(object sender, RoutedEventArgs e) { ShortcutList.SelectedItem = null; Select(_main); }
    private void Shortcut_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ShortcutList.SelectedItem is ShortcutRow row) Select(row);
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var shortcut = Shortcut.New(App.Store.UniqueName());
        var row = ShortcutRow.ForShortcut(shortcut);
        Shortcuts.Add(row);
        ShortcutList.SelectedItem = row;
    }
    public void AddShortcut() => Add_Click(this, new RoutedEventArgs());
    public bool ToggleSidebar()
    {
        var show = SidebarBorder.Visibility != Visibility.Visible;
        SidebarBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(show ? 220 : 0);
        return show;
    }
    public void RefreshUsage() => Refresh_Click(this, new RoutedEventArgs());
    private async Task LoadIcons()
    {
        if (IconPresets.Count > 0) return;
        try
        {
            var paths = await Task.Run(ShortcutIcons.PreviewPaths);
            if (!_loaded) return;
            foreach (var icon in paths)
                IconPresets.Add(new IconChoice(icon.Name, new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(icon.Path))));
            IconBox.SelectedItem = IconPresets.FirstOrDefault(i => i.Name == (_draft?.IconPreset ?? "Original"));
        }
        catch (Exception e) { ShowProblem("Could not load shortcut icons: " + e.Message); }
    }
    private void Support_Click(object sender, RoutedEventArgs e) => Links.Open(Links.Support);
    private void Source_Click(object sender, RoutedEventArgs e) => Links.Open(Links.Source);
    private void Icon_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IconBox.SelectedItem is IconChoice icon) IconLabel.Text = icon.Name;
    }
    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || _draft is null) return;
        if (_draft.InstalledName is null && FolderBox.Text == Shortcut.FolderName(_lastName))
            FolderBox.Text = Shortcut.FolderName(NameBox.Text);
        _lastName = NameBox.Text;
    }
    private void Source_Changed(object sender, SelectionChangedEventArgs e) =>
        MergeNote.Visibility = SourceBox.SelectedItem is SourceOption { Source.Kind: not SourceKind.Own }
            ? Visibility.Visible : Visibility.Collapsed;

    private void ShowProblem(string message)
    {
        if (!_loaded) return;
        Problem.Message = message; Problem.Visibility = Visibility.Visible; Problem.IsOpen = true;
    }

    private async Task MarkRunning()
    {
        try
        {
            var processes = await Task.Run(ClaudeProcesses.Enumerate);
            if (!_loaded) return;
            foreach (var row in Shortcuts.Append(_main).Where(r => r is not null))
                row.SetRunning(ClaudeProcesses.IsRunning(row.ProfileDir, processes));
        }
        catch (Exception e) { ShowProblem(e.Message); }
    }
    private async Task LoadUsage(ShortcutRow row, bool interactive)
    {
        try
        {
            var reading = await UsageMonitor.ReadAsync(row.ProfileDir, interactive);
            if (_loaded) row.SetUsage(reading);
        }
        catch (Exception e) { row.Problem = e.Message; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) await LoadUsage(_selected, true);
        await MarkRunning();
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not ShortcutRow row || row.Starting) return;
        row.Starting = true;
        try
        {
            row.Problem = await Task.Run(() => SessionStarter.StartAsync(row.ProfileDir));
            UsageMonitor.Invalidate(row.ProfileDir);
            await LoadUsage(row, true);
        }
        catch (Exception ex) { row.Problem = ex.Message; }
        finally { row.Starting = false; }
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var config = _original is null ? new GraftConfig { ProfileDir = _main.ProfileDir }
            : App.Store.ConfigFor(_original);
        _ = Task.Run(() => DesktopInteraction.Open(config));
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _original?.ProfileDir ?? GraftPaths.DefaultProfile;
            if (!Directory.Exists(path)) throw new IOException("This profile folder has not been created yet.");
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "\"" + path + "\"" });
        }
        catch (Exception ex) { ShowProblem(ex.Message); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _busy) return;
        _busy = true;
        SaveButton.IsEnabled = false;
        try
        {
            var candidate = new Shortcut
            {
                Id = _draft.Id, Name = NameBox.Text.Trim(), Folder = FolderBox.Text.Trim(),
                Source = (SourceBox.SelectedItem as SourceOption)?.Source ?? ShortcutSource.Own,
                IconPreset = (IconBox.SelectedItem as IconChoice)?.Name ?? _draft.IconPreset,
                InstalledName = _draft.InstalledName
            };
            if (Graft.ValidateWindowsName(candidate.Name) is string nameProblem) throw new IOException(nameProblem);
            if (Installer.ReservedNames.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
                throw new IOException("That name belongs to Claude or Claude Graft. Pick another.");
            if (Graft.ValidateFolder(candidate.Folder) is string folderProblem) throw new IOException(folderProblem);
            App.Store.Load();
            if (App.Store.LoadError is not null) throw new IOException(App.Store.LoadError);
            if (App.Store.Shortcuts.Any(s => s.Id != candidate.Id
                && (s.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase) || Fs.SamePath(s.ProfileDir, candidate.ProfileDir))))
                throw new IOException("Another shortcut already uses that name or profile folder.");
            var previous = App.Store.Get(candidate.Id);
            var changesStorage = previous is null || !Fs.SamePath(previous.ProfileDir, candidate.ProfileDir)
                || !previous.Source.Equals(candidate.Source);
            if (changesStorage)
            {
                var running = await Task.Run(ClaudeProcesses.Enumerate);
                if (ClaudeProcesses.IsRunning(candidate.ProfileDir, running)
                    || (previous is not null && ClaudeProcesses.IsRunning(previous.ProfileDir, running))
                    || (candidate.Source.Kind != SourceKind.Own && running.Any(p => ClaudeProcesses.IsClaudeDesktop(p.command))))
                    throw new IOException("Close the affected Claude windows before changing their profile folders or chat sharing.");
                if (previous is not null && !Fs.SamePath(previous.ProfileDir, candidate.ProfileDir))
                {
                    if (App.Store.Shortcuts.Any(s => s.Source.Kind == SourceKind.Shortcut && s.Source.ShortcutId == previous.Id))
                        throw new IOException("Return the shortcuts borrowing from this profile to their own chats before moving its folder.");
                    var result = await Task.Run(() => Graft.MoveProfileFolder(previous.Folder, candidate.Folder));
                    if (result is Graft.ProfileMove.Failed or Graft.ProfileMove.TargetExists)
                        throw new IOException("The profile could not be moved. Keep the old folder or choose an unused name.");
                }
                await Task.Run(() => Graft.Apply(App.Store.ConfigFor(candidate)));
            }
            Installer.Install(candidate);
            candidate.InstalledName = candidate.Name;
            App.Store.Update(candidate);
            if (previous is not null && !previous.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase))
                Installer.Uninstall(candidate, previous.Name);
            if (_loaded) Reload(candidate.Id);
        }
        catch (Exception ex) { ShowProblem(ex.Message); }
        finally { _busy = false; if (_loaded) SaveButton.IsEnabled = true; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_original is not Shortcut shortcut || _busy) return;
        if (App.Store.Get(shortcut.Id) is null) { Shortcuts.Remove(_selected!); Select(_main); return; }
        var data = new CheckBox { Content = "Also delete its chats and login (cannot be undone)" };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "The desktop shortcut will be removed. Keeping its profile preserves the login and chat history.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(data);
        var dialog = new ContentDialog { Title = "Delete “" + shortcut.Name + "”?", Content = body,
            PrimaryButtonText = "Delete Shortcut", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        PrepareDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            Installer.Uninstall(shortcut);
            var deleteProfile = data.IsChecked == true;
            var problem = await Task.Run(() => App.Store.Delete(shortcut.Id, deleteProfile));
            Reload();
            if (problem is not null) ShowProblem(problem);
        }
        catch (Exception ex) { ShowProblem(ex.Message); }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var input = new TextBox { PlaceholderText = "claude://…", MaxLength = 16384, IsSpellCheckEnabled = false, AcceptsReturn = false };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "Start sign-in in this Claude profile. If the browser opens the main Claude instead, copy the link behind its Open Claude button and paste it here.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(input);
        var dialog = new ContentDialog { Title = "Complete browser sign-in", Content = body,
            PrimaryButtonText = "Send to this profile", CloseButtonText = "Cancel" };
        PrepareDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var callback = input.Text.Trim();
        input.Text = "";
        try
        {
            var config = _original is null ? new GraftConfig { ProfileDir = _main.ProfileDir } : App.Store.ConfigFor(_original);
            await Task.Run(() => Launcher.CompleteSignIn(config, callback));
        }
        catch (Exception ex) { ShowProblem(ex.Message); }
    }

    private void PrepareDialog(ContentDialog dialog)
    {
        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = Appearance.ToElementTheme(App.Settings.Theme);
        dialog.CornerRadius = new CornerRadius(12);
        dialog.Resources["OverlayCornerRadius"] = new CornerRadius(12);
        MacDialogs.Prepare(dialog);
    }
    private async void Settings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();
    public async Task OpenSettingsAsync()
    {
        var dialog = new SettingsDialog(App.Settings);
        PrepareDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { AutoStart.Set(dialog.AutoStartEnabled); App.ApplySettings(dialog.Result); }
        catch (Exception ex) { ShowProblem(ex.Message); }
    }
}
