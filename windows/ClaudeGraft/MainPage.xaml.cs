using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.System.Power;

namespace ClaudeGraft;

public sealed partial class MainPage : Page
{
    public ObservableCollection<ShortcutRow> Rows { get; } = new();
    /// The one row the sidebar has chosen. Every row stays in Rows, so usage,
    /// running state and sharing keep moving for the Claudes not on screen.
    public ObservableCollection<ShortcutRow> Shown { get; } = new();
    private string _focus = "main";

    /// Raised whenever the Claudes or the chosen one change, for the sidebar.
    public event Action? ClaudesChanged;
    /// The title bar copies Heading, so it hears each time the chosen Claude's state line moves.
    public event Action? HeadingChanged;
    /// The title and state line, which the window shows in its title bar.
    public (string Title, string Subtitle) Heading => (ClaudeTitle.Text, ClaudeSubtitle.Text);
    /// The Claude on screen, which is the main one when the chosen one is gone.
    public string Focused => Shown.FirstOrDefault()?.Key ?? _focus;
    public bool SetupReady => _setup == SetupState.Ready;

    private bool _slidePending;

    // The page content moves into a fresh page the Frame navigates to, which plays its own transition.
    // It leaves the old page a tick before: moved straight across, it kept its place on screen and the
    // new page's entrance had nothing new to animate. Quick switches share one move, since content
    // still held by a page cannot be handed to another.
    private void SlideIn()
    {
        Release();
        if (_slidePending) return;
        _slidePending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _slidePending = false;
            Release();
            bool navigated;
            try { navigated = ClaudeFrame.Navigate(typeof(ClaudeHostPage), PageContent, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo()); }
            catch (Exception e)
            {
                navigated = false;
                Diagnostics.Note("claude-switch.navigate-failed", new Dictionary<string, object?> { ["type"] = e.GetType().Name, ["error"] = e.Message });
            }
            ClaudeFrame.BackStack.Clear();
            if (!navigated)
            {
                Release();
                ClaudeFrame.Content = PageContent;
            }
        });
    }

    private void Release()
    {
        if (ClaudeFrame.Content is Page held && held.Content == PageContent) held.Content = null;
        else if (ReferenceEquals(ClaudeFrame.Content, PageContent)) ClaudeFrame.Content = null;
    }

    /// Coming back from another section opens the page as that section's pages open.
    public void Replay() => SlideIn();

    /// Choosing a Claude puts away a draft not yet created, as leaving it does on the Mac.
    public void Focus(string key)
    {
        _focus = key;
        Drafting = false;
        SyncShown();
    }

    private string? _shownKey;

    private void SyncShown()
    {
        var row = Drafting ? null : Rows.FirstOrDefault(r => r.Key == _focus) ?? Rows.FirstOrDefault();
        var key = Drafting ? "draft" : row?.Key;
        if (_shownKey is not null && key != _shownKey) SlideIn();
        _shownKey = key;
        if (Shown.Count != (row is null ? 0 : 1) || (row is not null && Shown[0] != row))
        {
            Shown.Clear();
            if (row is not null) Shown.Add(row);
        }
        if (Drafting)
        {
            ClaudeTitle.Text = L10n.Text("New Claude");
            ClaudeSubtitle.Text = L10n.Text("Not created yet") + " · " + L10n.Text("Not running");
        }
        else
        {
            ClaudeTitle.Text = row?.Name ?? "Claude";
            ClaudeSubtitle.Text = row?.StateLine ?? "";
            // Loaded again only for another Claude, so a reload keeps a half-made edit.
            var shortcut = row?.Shortcut;
            if (shortcut?.Folder != _editing)
            {
                _editing = shortcut?.Folder;
                if (shortcut is not null) Editor.Edit(App.Store.Shortcuts.FirstOrDefault(s => s.Id == shortcut.Id) ?? shortcut);
            }
        }
        Editor.Visibility = Drafting || row?.Shortcut is not null ? Visibility.Visible : Visibility.Collapsed;
        MainIdentity.Visibility = !Drafting && row is { Shortcut: null } ? Visibility.Visible : Visibility.Collapsed;
        MainFolder.Text = Path.GetFileName(GraftPaths.DefaultProfile);
        UpdateBottomBar();
        EmptyState.Visibility = !Drafting && row?.Shortcut is null && App.Store.Shortcuts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClaudesChanged?.Invoke();
    }
    private readonly DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    // The window is built once and only hidden on close, so Loaded fires a single
    // time and its usage read is the only one there would ever be. A first read
    // that comes up empty — the app auto-started at login before the network was
    // up — would then stay empty for the life of the process. This poll is what
    // heals that, and keeps the figures current the way the Mac's own 30s timer
    // does; the polling budget makes a tick that already has a fresh reading free.
    private readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    // Moves what changes with time alone — the Refresh button's wait, the
    // countdowns, a window closing at its reset — without a read.
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _redrawAfterWake = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _clockTicks;

    public MainPage()
    {
        InitializeComponent();
        Rows.CollectionChanged += (_, _) => SyncShown();
        Editor.Saved += Editor_Saved;
        Editor.Discarded += Editor_Discarded;
        Editor.DeleteRequested += Editor_DeleteRequested;
        Editor.StateChanged += UpdateBottomBar;
        Loaded += (_, _) =>
        {
            App.SharingChanged += ShowSharing;
            Staging.Changed += Restaged;
            Reload();
            _processTimer.Start();
            _usageTimer.Start();
            _clock.Start();
        };
        Unloaded += (_, _) =>
        {
            App.SharingChanged -= ShowSharing;
            Staging.Changed -= Restaged;
            _processTimer.Stop();
            _usageTimer.Stop();
            _clock.Stop();
        };
        _clock.Tick += (_, _) =>
        {
            if (++_clockTicks % 5 == 0)
                foreach (var row in Rows) row.Tick();
            UpdateRefreshButton();
        };
        _processTimer.Tick += async (_, _) =>
        {
            // Picks up an install or sign-in finished while the prompt was up.
            if (Onboarding.Check(App.Store) != _setup) { Reload(); return; }
            foreach (var row in Rows.ToList())
            {
                var was = row.SignedIn;
                row.SetSignedIn(Onboarding.IsSignedIn(row.ProfileDir));
                if (!was && row.SignedIn) _ = LoadUsage(row, interactive: true);
                // The choice is made in Windows, so the link goes once it is made there, whatever was pressed.
                if (!row.SignedIn && row.Shortcut is not null) row.RecheckLinkHandler();
            }
            await MarkRunning(Rows.ToList());
        };
        _usageTimer.Tick += (_, _) => RefreshUsageQuietly();
        Setup.RecheckRequested += () => Reload();
        // Sleep never raised SurfaceContentsLost here; the display coming back on is
        // the wake that reaches a window left open, and the second pass is for a GPU
        // still settling when the first one lands.
        PowerManager.DisplayStatusChanged += (_, _) =>
        {
            if (PowerManager.DisplayStatus != DisplayStatus.On) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                RedrawChecks();
                _redrawAfterWake.Start();
            });
        };
        _redrawAfterWake.Tick += (_, _) => { _redrawAfterWake.Stop(); RedrawChecks(); };
        UsageMonitor.FetchingChanged += (profile, fetching) => DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var row in Rows.Where(r => Fs.SamePath(r.ProfileDir, profile)))
                row.SetFetching(fetching);
            UpdateRefreshButton();
        });
    }

    private SetupState _setup = SetupState.Ready;

    /// Until there is a signed-in Claude there is nothing a card could show, so
    /// the list and the actions that act on it step aside for the prompt.
    private bool ShowSetup(SetupState state)
    {
        _setup = state;
        var ready = state == SetupState.Ready;
        Setup.Show(state);
        ProfileList.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        BottomBar.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        ClaudesChanged?.Invoke();
        return ready;
    }

    /// A background usage read for every current row, off any button and without
    /// rebuilding the list — the timer's tick and the window reappearing both run
    /// through here. It leaves Refresh Usage alone: that button reflects a read
    /// the person asked for, not one a timer took.
    private void RefreshUsageQuietly(bool interactive = false)
    {
        if (_setup != SetupState.Ready) return;
        foreach (var row in Rows.ToList())
            _ = LoadUsage(row, interactive);
    }

    /// The window only hides on close and shows again on the next open, so its
    /// one Loaded is long past by then; this is called each time it reappears so
    /// a figure that failed to load, or has gone stale since, is fetched afresh.
    public void OnShown()
    {
        if (Onboarding.Check(App.Store) != _setup) { Reload(); return; }
        RedrawChecks();
        RefreshUsageQuietly(interactive: true);
        _ = MarkRunning(Rows.ToList());
        _ = MarkChatsElsewhere(Rows.ToList());
    }

    private int _reloadGeneration;

    private async void Reload(bool interactive = false)
    {
        // A later reload — finishing an edit while the first load is still in
        // flight — must own the button, or the earlier one's finally re-enables
        // it while the newer readings are still coming.
        var generation = ++_reloadGeneration;
        App.Store.Load();
        if (!ShowSetup(Onboarding.Check(App.Store))) { Rows.Clear(); return; }
        var built = ProfileRows.Build();
        var keep = ProfileRows.KeepShown(Rows, built);
        var rows = keep ? Rows.ToList() : built;
        if (!keep) Rows.Clear();

        // LoadUsage owns its own failures, so the WhenAll only completes, never throws.
        UpdateRefreshButton();
        var loads = new List<Task>();
        foreach (var row in rows)
        {
            if (!keep) Rows.Add(row);
            else row.SetSignedIn(Onboarding.IsSignedIn(row.ProfileDir));
            loads.Add(LoadUsage(row, interactive));   // fills the bars in when the answer arrives
        }
        _ = MarkRunning(rows);
        _ = MarkChatsElsewhere(rows);
        ShowSharing();
        // A kept list raises no change of its own, and a rename still needs the sidebar redrawn.
        SyncShown();
        try { await Task.WhenAll(loads); }
        finally { UpdateRefreshButton(); }
    }

    /// The refresh buttons sit in each page's usage heading now; what they said
    /// while held is the bottom bar's to show, through the chosen row.
    private void UpdateRefreshButton() => UpdateBottomBar();

    private ShortcutRow? _barRow;

    /// The chosen Claude's buttons, which live outside its card so they stay put
    /// as the page scrolls.
    private void UpdateBottomBar()
    {
        var row = Shown.FirstOrDefault();
        if (!ReferenceEquals(row, _barRow))
        {
            if (_barRow is not null) _barRow.PropertyChanged -= BarRowChanged;
            _barRow = row;
            if (row is not null) row.PropertyChanged += BarRowChanged;
        }
        if (!Drafting && row is not null && ClaudeSubtitle.Text != row.StateLine)
        {
            ClaudeSubtitle.Text = row.StateLine;
            HeadingChanged?.Invoke();
        }
        var editing = Drafting || row?.Shortcut is not null;
        DeleteButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Content = Drafting ? L10n.Text("Discard") : L10n.Text("Delete Shortcut…");
        AutomationProperties.SetName(DeleteButton, Drafting ? L10n.Text("Discard New Shortcut") : L10n.Text("Delete Shortcut"));
        DeleteButton.IsEnabled = !Editor.Busy;

        StartButton.Visibility = StartInfoButton.Visibility = row is { SignedIn: true } && !Drafting ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Tag = OpenButton.Tag = row;
        StartButton.IsEnabled = row is { StartEnabled: true };
        StartText.Opacity = row is { Starting: true } ? 0 : 1;
        StartRing.IsActive = row is { Starting: true };
        StartRing.Visibility = row is { Starting: true } ? Visibility.Visible : Visibility.Collapsed;
        if (row is not null) ToolTipService.SetToolTip(StartButton, row.StartTooltip);

        OpenButton.Visibility = Drafting || row is null ? Visibility.Collapsed : Visibility.Visible;
        OpenText.Text = row?.OpenLabel ?? L10n.Text("Open");
        OpenRing.IsActive = row is { Opening: true };
        OpenRing.Visibility = row is { Opening: true } ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(OpenButton, row is null ? L10n.Text("Open") : Spoken.Act(row.OpenLabel, row.Name));
        OpenButton.IsEnabled = row is not { Opening: true } && (!Editor.Busy && row?.Shortcut?.InstalledName is not null || row?.Shortcut is null);

        SaveButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        var title = Editor.IsNew ? L10n.Text("Create Shortcut") : L10n.Text("Update Shortcut");
        SaveText.Text = title;
        AutomationProperties.SetName(SaveButton, title);
        SaveButton.IsEnabled = Editor.IsDirty && !Editor.Busy;
        SaveText.Opacity = Editor.Busy ? 0 : 1;
        SaveRing.IsActive = Editor.Busy;
        SaveRing.Visibility = Editor.Busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BarRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateBottomBar();

    private void Save_Click(object sender, RoutedEventArgs e) => Editor.Save();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Drafting) Editor.Revert();
        else Editor.RequestDelete();
    }

    private void ShowMainFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(GraftPaths.DefaultProfile))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{GraftPaths.DefaultProfile}\"") { UseShellExecute = false })?.Dispose();
    }
    /// Lights each row's dot for the profile a Claude is holding, the way the
    /// flyout does. Reading what is running is a WMI query, slow enough that doing
    /// it inline would stall the list drawing, so it runs off the UI thread and
    /// sets the dots when it lands — a beat late is nothing the eye catches.
    private async Task MarkRunning(IReadOnlyList<ShortcutRow> rows)
    {
        IReadOnlyList<(int pid, string command)> processes;
        try { processes = await Task.Run(ClaudeProcesses.Enumerate); }
        catch (IOException) { return; }
        foreach (var row in rows)
            if (Rows.Contains(row)) row.SetRunning(Staging.For(row.ProfileDir)?.Running == true || ClaudeProcesses.IsRunning(row.ProfileDir, processes));
    }

    private async Task LoadUsage(ShortcutRow row, bool interactive)
    {
        // Wrapped because this runs in a fire-and-forget task: an unhandled
        // throw here has nowhere to surface and vanishes, which is exactly how
        // the main account's missing usage hid a null-dereference. A per-row
        // failure is written down rather than swallowed, and the other rows go
        // on loading.
        try
        {
            // row.ProfileDir, not row.Shortcut.ProfileDir — the main row has no
            // shortcut behind it.
            var entry = await UsageMonitor.ReadAsync(row.ProfileDir, interactive);
            // Back on the UI thread after the await; the row may still be shown.
            if (Rows.Contains(row)) row.SetUsage(entry);
        }
        catch (Exception e)
        {
            // A read that threw leaves the row without a figure; un-gate Start
            // Session so it is not held on a reading that will never land.
            if (Rows.Contains(row)) row.MarkUsageUnavailable();
            Diagnostics.Note("usage.rowFailed", new Dictionary<string, object?>
            {
                ["profile"] = row.ProfileDir,
                ["error"] = e.GetType().Name + ": " + e.Message,
            });
        }
    }

    private void Restaged()
    {
        Reload();
        ShowSharing();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload(interactive: true);

    private void PrepareDialog(ContentDialog dialog) => Dialogs.Prepare(dialog, XamlRoot);

    /// Opens the settings dialog and, once Done is pressed, writes the auto-start
    /// shortcut and hands the rest to the app to save and apply everywhere.
    // MARK: - Shared usage

    /// Every Claude Graft knows, by sharing key, with its name.
    private static List<(string Key, string Name)> SharingClaudes() =>
        App.Store.Shortcuts.ToList().Select(s => (s.Id.ToString(), s.Name)).Prepend((UsageSharing.MainKey, "Claude")).ToList();

    private static string NameOf(string key) => App.SharingName(key);

    /// Each card's line, from the settings and what the last pass found.
    private void ShowSharing()
    {
        var sharing = UsageSharing.Load();
        var accounts = PoolAccounts.Load();
        try
        {
            if (PoolAccounts.Colour(accounts, App.Store.Shortcuts.Select(s => s.IconPreset).ToList(), a => ClaudeColour(a.AccountUuid)))
                PoolAccounts.Save(accounts);
        }
        catch (IOException) { }
        var result = App.SharingResult;
        foreach (var row in Rows.ToList())
        {
            var lenders = sharing.Lenders.GetValueOrDefault(row.SharingKey) ?? [];
            row.SetSharing(SharingText.Label(lenders, NameOf),
                SharingText.Note(lenders, result?.Environment.GetValueOrDefault(row.SharingKey), result?.Problem));
            var own = Graft.Account(row.ProfileDir);
            row.SetLenders(lenders.Select((key, index) =>
            {
                var profile = ProfileOf(key);
                var same = own is not null && (profile is not null ? Graft.Account(profile) == own
                    : PoolAccounts.Load().FirstOrDefault(a => a.Id == key)?.AccountUuid == own);
                var state = SharingText.Condition(key, result, same, DateTimeOffset.UtcNow);
                return new LenderLine
                {
                    Claude = row, Key = key, Name = NameOf(key), First = index == 0,
                    State = state.Length == 0 ? L10n.Text("Checking its login…") : char.ToUpper(state[0]) + state[1..],
                    Attention = SharingText.Troubled(key, result, same, DateTimeOffset.UtcNow),
                    Icon = PresetIcon(PresetOf(key, accounts)),
                    Figures = profile is not null && UsageMonitor.Peek(profile)?.Usage is { } usage
                        ? usage.FiveHour is int five ? L10n.Format("5h {0}% · Week {1}%", five, usage.Week) : L10n.Format("Week {0}%", usage.Week)
                        : "",
                };
            }).ToList());
        }
    }

    // A Claude wears its own colour, Claude's own being the original; an account wears the
    // colour of a Claude signed in to it, or the one it was given, and graphite before that.
    internal static string PresetOf(string key, IReadOnlyList<PoolAccount> accounts)
    {
        if (key == UsageSharing.MainKey) return IconPreset.Original;
        if (App.Store.Shortcuts.FirstOrDefault(s => s.Id.ToString() == key) is { } shortcut) return shortcut.IconPreset;
        var account = accounts.FirstOrDefault(a => a.Id == key);
        return ClaudeColour(account?.AccountUuid) ?? account?.IconPreset ?? "graphite";
    }

    private static string? ClaudeColour(string? accountUuid)
    {
        if (accountUuid is null) return null;
        if (Graft.Account(GraftPaths.DefaultProfile) == accountUuid) return IconPreset.Original;
        return App.Store.Shortcuts.FirstOrDefault(s => Graft.Account(s.ProfileDir) == accountUuid)?.IconPreset;
    }

    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.ImageSource?> PresetIcons = [];

    private static Microsoft.UI.Xaml.Media.ImageSource? PresetIcon(string preset) =>
        PresetIcons.TryGetValue(preset, out var known) ? known
            : PresetIcons[preset] = ShortcutIcons.Preview(preset) is { } file
                ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file)) : null;

    private static string? ProfileOf(string key) => key == UsageSharing.MainKey ? GraftPaths.DefaultProfile
        : App.Store.Shortcuts.FirstOrDefault(s => s.Id.ToString() == key)?.ProfileDir;

    private void RemoveLender_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LenderLine line } || line.Staged) return;
        try
        {
            var next = SharedChats.SetLender(UsageSharing.Load(), line.Claude.SharingKey, line.Key, false);
            next.Save();
            PoolAccounts.ForgetUnused(next);
        }
        catch (IOException ex) { _ = Warn(L10n.Text("Shared usage was left as it was"), ex.Message); return; }
        ShowSharing();
        App.Instance.SyncSharingSoon();
    }

    /// Built as it opens, so it lists the Claudes and how each stands now.
    private void SharingMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Target is not FrameworkElement { Tag: ShortcutRow row }) return;
        menu.Items.Clear();
        var sharing = UsageSharing.Load();
        var lenders = sharing.Lenders.GetValueOrDefault(row.SharingKey) ?? [];
        var own = Graft.Account(row.ProfileDir);
        foreach (var (key, name) in SharingClaudes().Where(c => c.Key != row.SharingKey))
        {
            var profile = key == UsageSharing.MainKey ? GraftPaths.DefaultProfile
                : App.Store.Shortcuts.FirstOrDefault(s => s.Id.ToString() == key)?.ProfileDir;
            var same = own is not null && profile is not null && Graft.Account(profile) == own;
            var condition = lenders.Contains(key) || same
                ? SharingText.Condition(key, App.SharingResult, same, DateTimeOffset.UtcNow) : "";
            var item = new ToggleMenuFlyoutItem
            {
                Text = condition.Length == 0 ? name : $"{name} · {condition}",
                IsChecked = lenders.Contains(key),
                Tag = (row, key),
            };
            AutomationProperties.SetAutomationId(item, Spoken.Id("manager", row.Key, "sharing.lender"));
            AutomationProperties.SetName(item, L10n.Format("Draw on {0}", name));
            item.Click += SharingLender_Click;
            menu.Items.Add(item);
        }
        foreach (var account in PoolAccounts.Load())
        {
            var same = own is not null && account.AccountUuid == own;
            var on = lenders.Contains(account.Id);
            var condition = on || same ? SharingText.Condition(account.Id, App.SharingResult, same, DateTimeOffset.UtcNow) : "";
            var item = new ToggleMenuFlyoutItem
            {
                Text = condition.Length == 0 ? account.Name : $"{account.Name} · {condition}",
                IsChecked = on,
                Tag = (row, account.Id),
            };
            AutomationProperties.SetAutomationId(item, Spoken.Id("manager", row.Key, "sharing.account"));
            AutomationProperties.SetName(item, L10n.Format("Draw on {0}", account.Name));
            item.Click += SharingLender_Click;
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var signIn = new MenuFlyoutItem { Text = L10n.Text("Sign in to another account…") };
        AutomationProperties.SetAutomationId(signIn, Spoken.Id("manager", row.Key, "sharing.signIn"));
        signIn.Click += async (_, _) => await SignInToLend(row);
        menu.Items.Add(signIn);
        var about = new MenuFlyoutItem { Text = L10n.Text("About shared usage…") };
        AutomationProperties.SetAutomationId(about, Spoken.Id("manager", row.Key, "sharing.about"));
        about.Click += async (_, _) => await ExplainSharing(agree: false);
        menu.Items.Add(about);
    }

    private async void SharingLender_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: (ShortcutRow row, string lender) } item) return;
        var on = item.IsChecked;
        if (on && !App.Settings.SharingExplained)
        {
            if (!await ExplainSharing(agree: true)) return;
            App.Settings.SharingExplained = true;
            App.Settings.Save();
        }
        try
        {
            var next = SharedChats.SetLender(UsageSharing.Load(), row.SharingKey, lender, on);
            next.Save();
            PoolAccounts.ForgetUnused(next);
        }
        catch (IOException ex) { await Warn(L10n.Text("Shared usage was left as it was"), ex.Message); return; }
        ShowSharing();
        App.Instance.SyncSharingSoon();
    }

    /// An account with no Claude here, signed in through the browser so this
    /// Claude can draw on it. Explained first, as adding a Claude is.
    private async Task SignInToLend(ShortcutRow row)
    {
        if (!App.Settings.SharingExplained)
        {
            if (!await ExplainSharing(agree: true)) return;
            App.Settings.SharingExplained = true;
            App.Settings.Save();
        }
        using var cancel = new CancellationTokenSource();
        var waiting = new ContentDialog
        {
            Title = L10n.Text("Sign in to another account"),
            Content = new TextBlock
            {
                Text = L10n.Text("Finish signing in in your browser. Choose the account this Claude should draw on; it cannot be the one this Claude already uses."),
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = L10n.Text("Cancel"),
        };
        PrepareDialog(waiting);
        waiting.Closed += (_, _) => cancel.Cancel();
        var own = Graft.Account(row.ProfileDir);
        var signIn = AccountSignIn.Run(own, url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(), cancel.Token);
        var shown = waiting.ShowAsync().AsTask();
        PoolAccount? account = null;
        string? failure = null;
        try { account = await signIn; }
        catch (OperationCanceledException) { }
        catch (AccountSignIn.Failure f) { failure = f.Message; }
        // Only one dialog may be open at a time, so the warning waits for it to go.
        waiting.Hide();
        await shown;
        if (failure is not null) await Warn(L10n.Text("The account was not added"), failure);
        if (account is null) return;
        try
        {
            var stored = PoolAccounts.Add(account);
            SharedChats.SetLender(UsageSharing.Load(), row.SharingKey, stored.Id, true).Save();
        }
        catch (IOException ex) { await Warn(L10n.Text("The account was not added"), ex.Message); return; }
        ShowSharing();
        App.Instance.SyncSharingSoon();
    }

    /// Said once, before the first account is added, since what it does to a
    /// conversation and what the plans' terms say are for the person to weigh.
    private async Task<bool> ExplainSharing(bool agree)
    {
        var dialog = new ContentDialog
        {
            Title = L10n.Text("Shared usage"),
            Content = new TextBlock { Text = SharingText.Explanation, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = agree ? L10n.Text("Turn On") : "",
            CloseButtonText = agree ? L10n.Text("Cancel") : L10n.Text("OK"),
            DefaultButton = agree ? ContentDialogButton.Close : ContentDialogButton.None,
        };
        PrepareDialog(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void ClaudeRestart_Requested() => await ConfirmRestartClaudesAsync();

    /// Only the Claudes open when the warning was shown are covered by the
    /// person's answer; one opened while it was up is left alone.
    public async Task ConfirmRestartClaudesAsync()
    {
        var open = await Task.Run(ClaudeUpdateCenter.OpenClaudes);
        if (open.Count == 0) { await ClaudeUpdateCenter.CheckAsync(); return; }
        var names = string.Join(", ", open.Select(o => o.Name).Distinct());
        var dialog = new ContentDialog
        {
            Title = L10n.Text("Restart Claude to finish updating?"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = L10n.Format("These Claudes will close and open again on Claude Desktop {0}: {1}.\n\nAnything running in them stops — a Claude Code session, a task in progress, an open terminal. Finish or save that work first. A Claude that will not close is left open, never forced.",
                    ClaudeUpdateCenter.Installed, names),
            },
            PrimaryButtonText = L10n.Text("Restart"),
            CloseButtonText = L10n.Text("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        PrepareDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (await ClaudeUpdateCenter.RestartAsync(open) is string problem)
            await Warn(L10n.Text("Some Claudes stayed open"), problem);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutRow row } || row.Starting) return;
        row.Problem = null;
        row.Starting = true;

        var problem = await Task.Run(() => SessionStarter.StartAsync(row.ProfileDir));

        UsageMonitor.Invalidate(row.ProfileDir);
        row.Starting = false;
        row.Problem = problem;
        if (Rows.Contains(row)) await LoadUsage(row, interactive: true);
    }

    /// Persists the keep-warm choice where that profile carries it — on the
    /// shortcut for a grafted one, in the app's settings for the main Claude. The
    /// background sweep reads both the next time it runs, so nothing else has to be
    /// nudged; a start is at most a few minutes away, not this instant.
    ///
    /// Never saved while the switch is being refilled: a reused row's binding sets
    /// IsOn before Tag, so that flip belongs to the next Claude, not the one in Tag.
    /// Focus outlives a click, so a focus check let it through.
    private void KeepWarm_Toggled(object sender, RoutedEventArgs e)
    {
        // A binding setting the switch to what is already saved also raises
        // Toggled, and must not write it again.
        // The word beside it follows the switch itself, which runs ahead of the saved row.
        if (sender is ToggleSwitch { Parent: FrameworkElement { Parent: StackPanel { Children: [TextBlock words, ..] } } } moved)
            words.Text = SettingsCard.OnOff(moved.IsOn);
        if (sender is not ToggleSwitch { Tag: ShortcutRow row } box || box.IsOn == row.KeepWarm) return;
        if (_refilling.Contains(box)) return;
        var on = box.IsOn;
        if (row.Shortcut is Shortcut shortcut)
        {
            if (App.Store.LoadError is not null) { box.IsOn = !on; _ = WarnListUnreadable(); return; }
            shortcut.KeepWarm = on;
            App.Store.Update(shortcut);
        }
        else
        {
            App.Settings.KeepMainWarm = on;
            App.Settings.Save();
        }
    }

    private readonly HashSet<ToggleSwitch> _refilling = new();

    // The bindings follow a new row in this same pass, so the mark lifts once the queue has run past it.
    private void KeepWarm_Rebinding(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (sender is not ToggleSwitch box || !_refilling.Add(box)) return;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _refilling.Remove(box)))
            _refilling.Remove(box);
    }

    // A tick's drawing follows its icon's progress through an expression that sleep
    // can drop, leaving it on frame zero — the indeterminate dash. Setting the source
    // again rebuilds that link; the progress itself was never lost.
    private void RedrawChecks()
    {
        foreach (var glyph in Descendants<AnimatedIcon>(ProfileList).Where(g => g.Name == "CheckGlyph"))
        {
            var source = glyph.Source;
            glyph.Source = null;
            glyph.Source = source;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// Shortcuts asked about their elsewhere-chats already, for this run only. The
    /// dialog's checkbox is the permanent silence; this just stops a second Open
    /// press re-asking.
    private readonly HashSet<Guid> _askedAboutChats = new();

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row }) await OpenAsync(row);
    }

    /// The one way into a profile from this window, the sidebar's button included.
    public async Task OpenAsync(ShortcutRow row)
    {
        if (row.Opening) return;
        row.RecheckLinkHandler();

        var neighbours = App.Store.ChatStoreNeighbours(row.Shortcut);
        var sharers = await Task.Run(() => ChatConflict.OpenSharers(row.ProfileDir, neighbours));
        if (sharers.Count > 0 && !await AskOpenAnyway(sharers)) return;

        // Before opening, never after: Claude builds its sidebar at launch, so the
        // copy has to happen first to land in the window about to open.
        if (row.Shortcut is { } s && row.Elsewhere is { } found
            && s.StopAskingChatsFor != found.Account && !_askedAboutChats.Contains(s.Id))
        {
            if (!await AskAboutChats(row, s, found)) return;
        }
        await ProfileRows.OpenAsync(row);
    }

    private async void DefaultApps_Click(object sender, RoutedEventArgs e)
    {
        // Windows' own picker, Set default and all, comes up for a link being opened
        // while a handler is new to it; Settings is the way through where it cannot be.
        try { LinkRegistration.MarkUnseen(Microsoft.Win32.Registry.CurrentUser); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        // A picker the person cancels also answers false, so only a throw sends them to Settings.
        var shown = true;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(LinkRegistration.Probe)); }
        catch (Exception) { shown = false; }
        if (!shown)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(LinkRegistration.ApplicationName)) { UseShellExecute = true }); }
            catch { }
        }
        if (sender is FrameworkElement { Tag: ShortcutRow row }) row.RecheckLinkHandler();
    }


    /// The offer at the door. Four choices folded into a dialog's three buttons
    /// and a checkbox: copy or merge them across, open without them, or cancel,
    /// with "don't ask again" alongside — the persistent silence the Mac gives a
    /// button of its own. Returns whether to go on and open the window.
    private async Task<bool> AskAboutChats(ShortcutRow row, Shortcut s, Graft.ChatsElsewhere found)
    {
        var dontAsk = new CheckBox { Content = L10n.Text("Don't ask again for this account") };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = L10n.Format("{0}\n\nClaude builds its sidebar as it starts, so bringing them over now is what puts them in the window about to open. Every Claude has to be closed for that.",
                row.ElsewhereNote),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(dontAsk);

        var dialog = new ContentDialog
        {
            Title = found.Merging
                ? L10n.Text("Merge this account's other chats in first?")
                : L10n.Text("Bring this account's chats across first?"),
            Content = body,
            PrimaryButtonText = row.AdoptLabel,
            SecondaryButtonText = L10n.Text("Open Without Them"),
            CloseButtonText = L10n.Text("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        PrepareDialog(dialog);
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.None) return false;   // Cancel: leave it, do not open

        if (dontAsk.IsChecked == true && App.Store.LoadError is null && Staging.For(s.ProfileDir)?.ChatsElsewhere is null)
        {
            s.StopAskingChatsFor = found.Account;
            App.Store.Update(s);
        }

        if (result == ContentDialogResult.Secondary)   // Open Without Them
        {
            if (dontAsk.IsChecked != true) _askedAboutChats.Add(s.Id);
            return true;
        }

        // Copy / Merge Them Here. Open only once something arrived and nothing is
        // running: a copy refused for an open Claude leaves the "quit X" note that
        // opening a window would bury, and stays askable next time.
        var adoption = await Adopt(row, found);
        return adoption.Running.Count == 0 && adoption.Copied > 0;
    }

    private async void Adopt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row } && row.Elsewhere is { } found)
            await Adopt(row, found);
    }

    /// Off the UI thread, because it copies files and asks what is running. The
    /// result note stays on the row afterward, since Claude may not be open to
    /// show the answer for itself.
    private async Task<Graft.Adoption> Adopt(ShortcutRow row, Graft.ChatsElsewhere found)
    {
        var profile = row.ProfileDir;
        if (Staging.For(profile)?.ChatsElsewhere is not null)
        {
            row.CopiedNote = L10n.Text("Nothing copied — this offer was staged from the debug window");
            return new Graft.Adoption();
        }
        Graft.Adoption result;
        try { result = await Task.Run(() => Graft.AdoptChats(found.Profile, profile, found.Account)); }
        catch (StateBusyException e)
        {
            if (Rows.Contains(row)) row.CopiedNote = L10n.Format("Nothing copied — {0}", e.Message);
            return new Graft.Adoption();
        }
        if (!Rows.Contains(row)) return result;

        row.CopiedNote = result.Running.Count > 0
            ? L10n.Format("Nothing copied — quit {0} first", NamesOf(result.Running))
            : result.Copied == 0
                ? L10n.Text("Nothing was copied")
                : L10n.Plural(result.Copied, "{0} chat copied from {1}", "{0} chats copied from {1}", App.Store.NameOfProfile(found.Profile));

        // Once they are here the offer stops being made, and after a partial copy
        // the count drops to whatever is still missing.
        await MarkChatsElsewhere(new[] { row });
        return result;
    }

    /// A list of profiles as a sentence: "Claude", "Claude and Claude-Work",
    /// "Claude, Claude-Work and Claude-Play".
    private static string NamesOf(IReadOnlyList<string> profiles) => L10n.List(profiles.Select(App.Store.NameOfProfile).ToList());

    /// Fills each eligible row's "chats found elsewhere" offer, off the UI thread
    /// because it walks every profile's stores. Only a shortcut keeping its own
    /// chats is asked: the main row holds no shortcut, and a grafted one is having
    /// its sidebar filled from a source already.
    private async Task MarkChatsElsewhere(IReadOnlyList<ShortcutRow> rows)
    {
        var offers = await Task.Run(() =>
        {
            var profiles = Graft.SessionStoreProfiles();
            var map = new Dictionary<ShortcutRow, (Graft.ChatsElsewhere? offer, string? status)>();
            foreach (var row in rows)
                map[row] = (
                    row.Shortcut is { Source.Kind: SourceKind.Own } s
                        ? Graft.FindChatsElsewhere(s.ProfileDir, profiles)
                        : null,
                    SidebarSync.Status(row.ProfileDir));
            return map;
        });
        foreach (var (row, value) in offers)
            if (Rows.Contains(row))
            {
                row.SetChatsElsewhere(value.offer);
                row.SetSidebarStatus(value.status);
            }
    }

    /// From the sidebar's New Claude: a draft on the page, which nothing is
    /// written for until Create.
    public async Task AddAsync()
    {
        if (App.Store.LoadError is not null) { await WarnListUnreadable(); return; }
        Drafting = true;
        _editing = null;
        Editor.Edit(null);
        SyncShown();
    }

    /// A new Claude being filled in, shown in place of any card.
    public bool Drafting { get; private set; }
    /// Which Claude the editor holds, so a reload does not throw away a half-made edit.
    private string? _editing;

    private void Editor_Discarded()
    {
        Drafting = false;
        SyncShown();
    }

    private async void Editor_DeleteRequested(Shortcut shortcut) => await ConfirmDelete(shortcut);

    private async void Editor_Saved(Shortcut original, Shortcut edited, bool isNew)
    {
        if (App.Store.LoadError is not null) { await WarnListUnreadable(); return; }
        Editor.Busy = true;
        try
        {
            if (await ApplyEdit(isNew ? null : original, edited, isNew)) { Drafting = false; _editing = null; }
        }
        finally { Editor.Busy = false; }
        Reload();
    }

    /// Writes a saved edit through: moves the folder, saves the list, applies the
    /// chats and makes the shortcut. False when it stopped before saving.
    private async Task<bool> ApplyEdit(Shortcut? existing, Shortcut shortcut, bool isNew)
    {
        {
            var previousName = existing?.Name;
            var previousFolder = existing?.Folder;

            // A changed folder moves the profile's chats and login to the new
            // name rather than abandoning them at the old one. Its Claude must be
            // closed first — moving the files it has open could lose a chat — and
            // the move itself refuses to write over a folder already in use.
            if (existing is not null && previousFolder is not null
                && !Fs.SamePath(GraftPaths.Profile(previousFolder), GraftPaths.Profile(shortcut.Folder)))
            {
                bool open;
                try { open = ClaudeProcesses.IsRunning(GraftPaths.Profile(previousFolder)); }
                catch (IOException) { open = true; }
                if (open)
                {
                    await Warn(L10n.Text("Close Claude first"),
                        L10n.Format("“{0}” is open. Quit its Claude window before changing the folder — moving a profile's files while Claude is using them could lose chats.", existing.Name));
                    return false;
                }
                Graft.ProfileMove moved;
                try { moved = await Task.Run(() => Graft.MoveProfileFolder(previousFolder, shortcut.Folder)); }
                catch (StateBusyException e)
                {
                    await Warn(L10n.Text("The profile was not moved"), e.Message);
                    return false;
                }
                if (moved == Graft.ProfileMove.TargetExists)
                {
                    await Warn(L10n.Text("That folder is already in use"),
                        L10n.Format("A folder named “{0}” already exists. Pick a name that is not in use, so nothing there is overwritten.", shortcut.Folder));
                    return false;
                }
                if (moved == Graft.ProfileMove.Failed)
                {
                    await Warn(L10n.Text("Could not move the profile"),
                        L10n.Text("Its files could not be moved, so nothing was changed. Make sure its Claude is closed and try again."));
                    return false;
                }
            }

            if (isNew) App.Store.Add(shortcut);
            else App.Store.Update(shortcut);
            _focus = shortcut.Folder;

            var config = App.Store.ConfigFor(shortcut);
            try { await Task.Run(() => Graft.Apply(config)); }
            catch (StateBusyException)
            {
                await Warn(L10n.Text("Chats were not set up yet"),
                    L10n.Text("Graft was still finishing another change, so nothing was moved. It is set up the next time this profile opens."));
            }
            catch (UnreadableStateException ex)
            {
                await Warn(L10n.Text("Chats were not set up yet"),
                    L10n.Format("{0} could not be read, so nothing was moved. It is set up the next time this profile opens with that file readable.", Path.GetFileName(ex.Path)));
            }

            try
            {
                Installer.Install(shortcut);
                // A rename leaves the old-named .lnk behind; clear it.
                if (previousName is not null && !string.Equals(previousName, shortcut.Name, StringComparison.OrdinalIgnoreCase))
                    Installer.RemoveLink(previousName);
                shortcut.InstalledName = shortcut.Name;
                App.Store.Update(shortcut);
                WindowIdentity.IconsChanged();
                WindowIdentity.Refresh();
                App.ShortcutsChanged();
            }
            catch (Installer.InstallException ex)
            {
                await Warn(L10n.Text("Could not create the shortcut"), ex.Message);
            }
            return true;
        }
    }

    private async Task ConfirmDelete(Shortcut shortcut)
    {
        // Asked before the question, so it never offers what would leave the
        // shortcut gone and its folder stranded behind a running Claude.
        var open = await Task.Run(() => ClaudeProcesses.IsRunning(shortcut.ProfileDir));
        var deleteData = new CheckBox { Content = L10n.Text("Also delete its chats and login (cannot be undone)"), IsEnabled = !open };
        AutomationProperties.SetAutomationId(deleteData, "delete.alsoData");
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = L10n.Format("“{0}” will be removed from the list.", shortcut.Name),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(deleteData);
        if (open)
            body.Children.Add(new TextBlock
            {
                Text = L10n.Format("“{0}” is open, so its chats and login cannot be deleted now. Quit it first to delete them too.", shortcut.Name),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            });

        var confirm = new ContentDialog
        {
            Title = L10n.Text("Remove profile?"),
            Content = body,
            PrimaryButtonText = L10n.Text("Remove"),
            CloseButtonText = L10n.Text("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        PrepareDialog(confirm);

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        if (App.Store.LoadError is not null) { await WarnListUnreadable(); return; }

        string? problem;
        // Deleting touches a folder Claude may still be letting go of; whatever
        // goes wrong there is a warning to show, never a reason to crash.
        var deletingData = deleteData.IsChecked == true;
        // Asked again: the Claude may have opened while the question was up.
        if (App.Store.DeletionRefusal(shortcut, deletingData,
                deletingData && await Task.Run(() => ClaudeProcesses.IsRunning(shortcut.ProfileDir))) is string refusal)
        {
            await Warn(L10n.Text("Nothing was removed"), refusal);
            return;
        }
        try { problem = App.Store.Delete(shortcut.Id, deletingProfile: deletingData); }
        catch (StateBusyException e)
        {
            await Warn(L10n.Text("Nothing was removed"), e.Message);
            return;
        }
        catch (Exception e)
        {
            Diagnostics.Note("profile.removeFailed", new Dictionary<string, object?>
            {
                ["profile"] = shortcut.Folder, ["error"] = e.GetType().Name + ": " + e.Message,
            });
            problem = L10n.Format("Something went wrong removing it: {0}", e.Message);
        }
        Installer.Uninstall(shortcut);
        Reload();

        if (problem is not null) await Warn(L10n.Text("The profile folder was kept"), problem);
    }

    private Task WarnListUnreadable() => Warn(L10n.Text("The profile list could not be read"),
        L10n.Text("shortcuts.json in %APPDATA%\\ClaudeGraft would not read, so nothing is saved over it until it does. Your profiles and their chats are untouched."));

    /// Whether to open a Claude while others on the same chats are open.
    private async Task<bool> AskOpenAnyway(IReadOnlyList<string> sharers)
    {
        var dialog = new ContentDialog
        {
            Title = ChatConflict.Title,
            Content = new TextBlock { Text = ChatConflict.Message(sharers), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = L10n.Text("Open Anyway"),
            CloseButtonText = L10n.Text("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        PrepareDialog(dialog);
        AutomationProperties.SetAutomationId(dialog, "chatConflict");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task Warn(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = L10n.Text("OK"),
        };
        PrepareDialog(dialog);
        await dialog.ShowAsync();
    }
}
