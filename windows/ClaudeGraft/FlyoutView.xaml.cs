using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    public event Action? SupportRequested;
    public event Action? DismissRequested;

    /// The content changed size — a row added, an account's bars arriving — so
    /// the host window should measure again and refit. The view is stretched to
    /// the window, so its own SizeChanged cannot see the overflow; this says so.
    public event Action? LayoutChanged;

    private Updater.Release? _update;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _clockTicks;
    private bool _refreshing;
    // Rows are rebuilt on every open and every open reads afresh, so a sweep on
    // each new reading swept every time. Only numbers that moved sweep now.
    private readonly Dictionary<string, (int, int, int?)> _shown = new(StringComparer.OrdinalIgnoreCase);

    private static (int, int, int?) Figures(UsageEntry entry) =>
        (entry.Usage?.FiveHour ?? -1, entry.Usage?.Week ?? -1, entry.Usage?.Fable);

    /// Whether this entry's numbers differ from the ones last on screen for the
    /// profile, remembering these as the ones on screen now.
    private bool Moved(string profile, UsageEntry entry)
    {
        var key = Path.GetFullPath(profile);
        var moved = !_shown.TryGetValue(key, out var last) || last != Figures(entry);
        _shown[key] = Figures(entry);
        return moved;
    }

    public FlyoutView()
    {
        InitializeComponent();
        App.UpdateProgressChanged += ReflectUpdate;
        Setup.RecheckRequested += Reload;
        Staging.Changed += Reload;
        Setup.Acted += () => DismissRequested?.Invoke();
        UsageMonitor.FetchingChanged += (profile, fetching) => DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var row in Rows.Where(r => Fs.SamePath(r.ProfileDir, profile)))
                row.SetFetching(fetching);
            UpdateRefreshButton();
            LayoutChanged?.Invoke();
        });
        _clock.Tick += (_, _) =>
        {
            if (++_clockTicks % 5 == 0)
                foreach (var row in Rows) row.Tick();
            UpdateRefreshButton();
        };
        _clock.Start();
        App.SharingChanged += () => { ShowChats(); LayoutChanged?.Invoke(); };
        ChatOnScreen.Changed += ShowChats;
    }

    /// Paints the flyout's own surface opaque, for the Solid backdrop where there
    /// is no material behind it, or leaves it transparent so a material shows
    /// through. The window's mica is what the transparent case reveals.
    public void SetOpaqueSurface(bool opaque) =>
        OpaqueSurface.Visibility = opaque ? Visibility.Visible : Visibility.Collapsed;

    /// Rebuilds the list and refreshes usage. Called each time the flyout opens,
    /// but not as a press: the endpoint rate-limits hard, and someone who opens
    /// the tray often was earning a refusal for every few opens. The five-minute
    /// cache answers an open; Refresh Usage is the way to ask.
    public void Reload()
    {
        App.Store.Load();
        var state = Onboarding.Check(App.Store);
        var ready = state == SetupState.Ready;
        Setup.Show(state);
        ProfileScroller.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        var built = ready ? ProfileRows.Build() : new List<ShortcutRow>();
        var keep = ProfileRows.KeepShown(Rows, built);
        if (!keep) Rows.Clear();
        var rows = keep ? Rows.ToList() : built;
        foreach (var row in rows)
        {
            if (keep) row.SetSignedIn(Onboarding.IsSignedIn(row.ProfileDir));
            // A new row is built already holding its last figure, so its bars open
            // where they stand instead of filling up from zero while the read is out.
            else if (UsageMonitor.Peek(row.ProfileDir) is { } known)
                row.SetUsage(known, Moved(row.ProfileDir, known));
            if (!keep) Rows.Add(row);
            _ = LoadUsage(row);
        }
        ReflectUpdate();
        ShowChats();
        LayoutChanged?.Invoke();
        _ = MarkRunning(rows);
    }

    // MARK: - Conversations

    private bool _othersShown;

    private static string KeyOf(string? profile)
    {
        if (profile is null) return "";
        if (Fs.SamePath(profile, GraftPaths.DefaultProfile)) return UsageSharing.MainKey;
        return App.Store.Shortcuts.FirstOrDefault(s => Fs.SamePath(s.ProfileDir, profile))?.Id.ToString() ?? "";
    }

    private static string NameOf(string key) => App.SharingName(key);

    /// The conversation on screen in the Claude in front comes first and alone;
    /// the rest wait behind a line saying how many, so they never push the
    /// figures off a short screen.
    private void ShowChats()
    {
        var front = FrontClaude.Profile;
        var frontKey = KeyOf(front);
        var chats = SharedChats.Build(App.SharingResult?.Status, UsageSharing.Load(), ChatOnScreen.RecordFor,
            frontKey, front is null ? null : ChatOnScreen.OnScreenSession(front));
        ChatList.Children.Clear();
        Conversations.Visibility = chats.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (chats.Count == 0) return;
        var inFront = chats[0].Claude == frontKey ? chats[0] : null;
        var others = chats.Where(c => c != inFront).Take(8).ToList();
        var showClaude = chats.Select(c => c.Claude).Distinct().Count() > 1;
        if (inFront is not null)
        {
            ChatList.Children.Add(ChatRow(inFront, showClaude: false, emphasised: true));
            if (others.Count > 0)
            {
                var toggle = new HyperlinkButton
                {
                    Content = $"{(_othersShown ? "Hide" : "Show")} other conversations · {others.Count}",
                    Padding = new Thickness(0),
                    FontSize = 12,
                };
                AutomationProperties.SetAutomationId(toggle, "flyout.conversations.others");
                toggle.Click += (_, _) =>
                {
                    _othersShown = !_othersShown;
                    ShowChats();
                    LayoutChanged?.Invoke();
                };
                ChatList.Children.Add(toggle);
            }
        }
        if (inFront is null || _othersShown)
            foreach (var chat in others) ChatList.Children.Add(ChatRow(chat, showClaude, emphasised: false));
    }

    private UIElement ChatRow(SharedChats.Chat chat, bool showClaude, bool emphasised)
    {
        var title = string.IsNullOrWhiteSpace(chat.Title) ? "Untitled conversation" : chat.Title!;
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = emphasised ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        });
        string? detail = chat.PassedOver && chat.Assigned is string a ? $"{NameOf(a)} had no room"
            : showClaude ? $"in {NameOf(chat.Claude)}" : null;
        if (detail is not null)
            text.Children.Add(new TextBlock
            {
                Text = detail, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Themed.Brush(chat.PassedOver ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"),
            });
        grid.Children.Add(text);

        var answering = SharedChats.AnsweringKey(chat);
        var label = answering is null ? "Automatic" : chat.Assigned is null ? $"Auto · {NameOf(answering)}" : NameOf(answering);
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        void Add(string text, string? to)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "account." + chat.Id, IsChecked = chat.Assigned == to };
            item.Click += (_, _) => Assign(chat.Id, to);
            menu.Items.Add(item);
        }
        Add(chat.ServedBy is string served && chat.Assigned is null ? $"Automatic · now {NameOf(served)}" : "Automatic", null);
        foreach (var choice in chat.Choices) Add(NameOf(choice), choice);
        var button = new DropDownButton { Content = label, Flyout = menu, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        // Several reading "Claude 2" would not say which conversation each moves.
        AutomationProperties.SetName(button, $"Account answering “{title}”");
        AutomationProperties.SetAutomationId(button, $"flyout.conversation.{chat.Id}.account");
        ToolTipService.SetToolTip(button, "Which account answers this conversation");
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    private void Assign(string session, string? to)
    {
        try { SharedChats.Assign(UsageSharing.Load(), session, to, DateTimeOffset.UtcNow).Save(); }
        catch (IOException) { return; }
        ShowChats();
        App.Instance.SyncSharingSoon();
    }

    private bool _checking;

    private void ReflectUpdate()
    {
        _update = App.AvailableUpdate;
        var downloading = App.UpdateDownload;
        GraftWords.Inlines.Clear();
        GraftWords.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Claude Graft", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        GraftWords.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = " " + (_update is null
                ? $"{Updater.Shown}" + (_checking ? " · checking" : App.UpdateChecked ? " · Up to date" : "")
                : downloading is { } f
                    ? $"{_update.Version} · downloading {(int)Math.Round(f * 100)}%"
                    : $"{_update.Version} is available · you have {Updater.Shown}"),
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
        });
        GraftGlyph.Glyph = _update is not null ? "" : _checking || !App.UpdateChecked ? "" : "";
        var busy = _checking || downloading is not null;
        GraftCheckBusy.IsActive = busy;
        GraftCheckBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.Visibility = busy || _update is not null ? Visibility.Collapsed : Visibility.Visible;
        AvailableButton.Visibility = _update is not null && downloading is null ? Visibility.Visible : Visibility.Collapsed;
        var showBar = downloading is not null && _update is not null;
        if (showBar) UpdateProgress.Value = downloading!.Value;
        if ((UpdateProgress.Visibility == Visibility.Visible) != showBar)
        {
            UpdateProgress.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
            LayoutChanged?.Invoke();
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        _checking = true;
        ReflectUpdate();
        // Long enough to be seen, since an unchanged answer otherwise looks like nothing happened.
        var shown = Task.Delay(800);
        // A failed check is left silent, the way the Mac's is; the reason is in diagnostics.
        try { await App.Instance.RunUpdateCheckAsync(); }
        catch { }
        await shown;
        _checking = false;
        ReflectUpdate();
    }

    /// The flyout stays up so the download can be watched; the progress itself
    /// arrives through ReflectUpdate, and a failure puts the offer back.
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        await App.Instance.InstallUpdateAsync(_update);
    }

    /// The dots come in after the window is up, not before it: reading which
    /// Claude is running is a WMI query, slow enough that doing it inline made
    /// the flyout crawl open. It runs off the UI thread and lights the dots when
    /// it lands — a beat late is not something the eye catches on a status dot.
    private async Task MarkRunning(IReadOnlyList<ShortcutRow> rows)
    {
        IReadOnlyList<(int pid, string command)> processes;
        try { processes = await Task.Run(ClaudeProcesses.Enumerate); }
        catch (IOException) { return; }
        foreach (var row in rows)
            if (Rows.Contains(row)) row.SetRunning(Staging.For(row.ProfileDir)?.Running == true || ClaudeProcesses.IsRunning(row.ProfileDir, processes));
    }

    private async Task LoadUsage(ShortcutRow row) => await LoadUsage(row, interactive: false);

    private async Task LoadUsage(ShortcutRow row, bool interactive)
    {
        var entry = await ProfileRows.ReadUsageSafe(row.ProfileDir, interactive);
        // Back on the UI thread after the await; the row may have been cleared
        // by a reload since.
        if (!Rows.Contains(row)) return;
        if (entry is not null)
        {
            row.SetUsage(entry, Moved(row.ProfileDir, entry));
            LayoutChanged?.Invoke();   // the bars just appeared; the flyout is taller now
        }
        else
        {
            // No reading at all: un-gate Start Session rather than leaving it
            // held on a usage figure that never comes.
            row.MarkUsageUnavailable();
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row })
        {
            row.RecheckLinkHandler();
            ProfileRows.Open(row);
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
        _refreshing = true;
        UpdateRefreshButton();
        try
        {
            var rows = Rows.ToList();
            await Task.WhenAll(rows.Select(row => LoadUsage(row, interactive: true)));
            await MarkRunning(rows);
        }
        finally
        {
            _refreshing = false;
            UpdateRefreshButton();
        }
    }

    private void UpdateRefreshButton()
    {
        var state = UsageStatus.RefreshButton(
            _refreshing || Rows.Any(r => r.Fetching), ProfileRows.HeldUntil(Rows), DateTimeOffset.UtcNow);
        RefreshButton.Content = state.Text;
        RefreshButton.IsEnabled = state.Enabled;
    }

    private void Manager_Click(object sender, RoutedEventArgs e)
    {
        OpenManagerRequested?.Invoke();
        DismissRequested?.Invoke();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        SupportRequested?.Invoke();
        DismissRequested?.Invoke();
    }

    private void ClaudeRestart_Requested()
    {
        DismissRequested?.Invoke();
        App.ConfirmRestartClaudes();
    }
}
