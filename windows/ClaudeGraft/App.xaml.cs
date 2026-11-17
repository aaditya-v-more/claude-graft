using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace ClaudeGraft;

/// <summary>
/// Claude Graft lives in the notification area, not in a window — the same
/// tray-first shape the Mac build has in the menu bar. It comes up with no
/// window shown; the tray icon's menu opens a profile or the manager, and the
/// manager window hides back to the tray rather than quitting.
/// </summary>
public partial class App : Application
{
    public static ShortcutStore Store { get; } = new();

    /// The app's preferences, applied by every window as it is built and again
    /// whenever they change. Held here, saved on change, and announced so the
    /// open windows re-dress themselves without being hunted down individually.
    public static GraftSettings Settings { get; private set; } = GraftSettings.Load();
    public static event Action? SettingsChanged;

    /// Has every window dress itself again, as a change of look in Settings does.
    public static void LooksChanged()
    {
        Theming.Apply(Settings);
        Themed.Refresh();
        SettingsChanged?.Invoke();
    }


    // Launched in the mode it will show, so a mode the app keeps needs no swap at all.
    private static ApplicationTheme ModeInEffect() =>
        Appearance.ToElementTheme(Settings.Theme) == ElementTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;

    internal static App Instance => (App)Current;

    /// Held here so a background check and a manual one share one answer, and the
    /// flyout can show it the moment it opens rather than waiting on a fresh check.
    public static Updater.Release? AvailableUpdate { get; private set; }

    /// Whether a check has answered since launch, so "Up to date" is never said before one has.
    public static bool UpdateChecked { get; private set; }

    /// How far the update's download has got, 0 to 1, or null when none is
    /// running. The flyout draws it; the tray tooltip and the notification are
    /// kept in step from here.
    public static double? UpdateDownload { get; private set; }
    public static event Action? UpdateProgressChanged;

    public static void ApplySettings(GraftSettings updated)
    {
        // Pressing Done with nothing touched should be as quiet as Cancel: no
        // save, and above all no re-apply, since reassigning a window's backdrop
        // flashes it even when the material is the same.
        if (updated.Theme == Settings.Theme
            && updated.Backdrop == Settings.Backdrop
            && updated.Palette == Settings.Palette
            && updated.StartHidden == Settings.StartHidden
            && updated.HoldUpdates == Settings.HoldUpdates
            && updated.TaskbarStrip == Settings.TaskbarStrip
            && updated.Chime == Settings.Chime) return;

        // Only a change of look redraws the windows; a redraw resets the backdrop, which flashes.
        var looks = updated.Theme != Settings.Theme || updated.Backdrop != Settings.Backdrop || updated.Palette != Settings.Palette;
        var chimeChanged = updated.Chime != Settings.Chime;
        Settings = updated;
        updated.Save();
        if (chimeChanged) _ = Task.Run(SyncChimeHook);
        if (looks) LooksChanged();
        (Current as App)?.ShowTray();
    }

    private static void SyncChimeHook()
    {
        var launcher = Path.Combine(AppContext.BaseDirectory, "GraftLaunch.exe");
        var done = Settings.Chime
            ? File.Exists(launcher) && ChimeHook.Install(ChimeHook.SettingsFile, launcher)
            : ChimeHook.Remove(ChimeHook.SettingsFile);
        if (!done) Diagnostics.Note("chime.hook-unchanged", new Dictionary<string, object?> { ["wanted"] = Settings.Chime, ["launcher"] = launcher });
    }

    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private FlyoutWindow? _flyout;
    private Timer? _warmTimer;
    private Timer? _updateTimer;
    private Timer? _frontTimer;
    private readonly Dictionary<string, Usage?> _frontUsage = new(StringComparer.OrdinalIgnoreCase);
    private string? _trayIconFile;
    private string? _notifiedVersion;

    // The tray's click callbacks arrive on H.NotifyIcon's message-window
    // thread, not this one; anything touching a WinUI window has to hop back to
    // the UI thread or it faults. Captured here, on the thread that owns the UI.
    private DispatcherQueue? _ui;

    public App()
    {
        // Before any HttpClient reads the proxy from the environment.
        PoolEnvironment.ForgetInheritedLines();
        // Wired before InitializeComponent and before OnLaunched build any window,
        // because the crash this catches is a managed throw on the XAML startup
        // path: it surfaces to the Windows event log as a stowed exception in
        // Microsoft.UI.Xaml with no type, message or stack, and left nothing at
        // all in this app's own diagnostics — a launch that freezes and dies with
        // no record of why. These three cover the three ways a throw goes
        // unobserved: the UI dispatcher, a background thread, and a dropped task.
        // A failure while routing a link can carry the link, and with it a one-time code.
        if (Program.Link is null) WatchForCrashes();
        InitializeComponent();
        RequestedTheme = ModeInEffect();
        // The manager closes for real, and the tray app outlives it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    private void WatchForCrashes()
    {
        UnhandledException += (_, e) => Record("app.unhandled", e.Exception);
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => Record("domain.unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException +=
            (_, e) => Record("task.unobserved", e.Exception);
    }

    // Nothing is swallowed — the exception is written down and left to take the
    // process down as it would have, since a half-built window kept alive is a
    // worse state than the crash. The chain is walked so the real cause, wrapped
    // a few layers deep by the time WinUI marshals it, is not lost.
    private static void Record(string @event, Exception? error)
    {
        var fields = new Dictionary<string, object?>();
        var depth = 0;
        for (var e = error; e is not null && depth < 5; e = e.InnerException, depth++)
        {
            var suffix = depth == 0 ? "" : $".inner{depth}";
            fields["type" + suffix] = e.GetType().FullName;
            fields["message" + suffix] = e.Message;
            fields["stack" + suffix] = e.StackTrace;
        }
        if (error is null) fields["type"] = "(none)";
        Diagnostics.Note(@event, fields);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Themed.Publish();
        if (LinkRegistration.IsProbe(Program.Link)) { Exit(); return; }
        if (Program.Link is { } link)
        {
            var window = new ClaudeLinkWindow(link);
            window.Closed += (_, _) => Exit();
            _ = window.OpenAsync();
            return;
        }
        _ui = DispatcherQueue.GetForCurrentThread();

        // Shortcuts made before the launcher moved in beside the app still point
        // at the old per-user copy; carried over in the background, since the
        // folder being cleared out holds a whole Electron.
        System.Threading.Tasks.Task.Run(() => { try { Installer.MigrateLegacyLauncher(); } catch { } });
        System.Threading.Tasks.Task.Run(LegacyInstall.MoveOff);
        new Thread(() => { while (Program.ShowRequest.WaitOne()) ShowManager(); }) { IsBackground = true, Name = "show-request" }.Start();
        new Thread(() => { if (Program.QuitRequest.WaitOne()) Quit(); }) { IsBackground = true, Name = "quit-request" }.Start();
        // Links keep pointing at the same icon files; a Claude update redraws them.
        var shortcuts = Store.Shortcuts.ToList();
        System.Threading.Tasks.Task.Run(() => ShortcutIcons.Refresh(shortcuts));
        // Shortcuts made before they went in the Start menu as well.
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var s in shortcuts.Where(s => Installer.InstalledLink(s) is not null))
                Installer.AddToStartMenu(s);
        });
        // Recovery listens before the first windows are identified, or a
        // shortcut's Claude already open at start would never be watched.
        UpdateRecovery.StrayOpened += UpdateToast.StrayClaude;
        UpdateRecovery.Start();
        WindowIdentity.Start(_ui);
        ClaudeUpdateCenter.Start(_ui);
        // Read from disk on the watcher's thread rather than from the window's list.
        _quitWatch = new ClaudeQuitWatch(() => new ShortcutStore().Shortcuts
            .Where(s => s.InstalledName is not null).Select(s => s.ProfileDir)
            .Prepend(GraftPaths.DefaultProfile).ToList());
        System.Threading.Tasks.Task.Run(() =>
        {
            try { ManualUpdates.Synchronize(); }
            catch (Exception e) { Record("updates.manual-sync", e); }
        });

        // No ContextFlyout: the library renders that as a native popup owned by
        // its own message-only window, which can never take the foreground a
        // popup menu needs to register clicks — the menu draws but every
        // selection is dropped. The right-click menu is built and shown by hand
        // instead, from TrayMenu, with an owner window that can go foreground.
        _tray = new TaskbarIcon
        {
            ToolTipText = "Claude Graft",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            // Without this a left click is held back the length of the
            // double-click timeout, in case a second one follows — half a second
            // of nothing between pressing the icon and the flyout appearing.
            // Nothing here wants the double click, so the single one fires at once.
            NoLeftClickDelay = true,
        };
        // A left click opens the flyout — the account list with its usage, the
        // Mac menu bar item's whole face — while the right click keeps the plain
        // menu as a fallback that needs no window to draw.
        _tray.LeftClickCommand = new RelayCommand(ToggleFlyout);
        _tray.RightClickCommand = new RelayCommand(ShowMenu);
        _tray.ForceCreate();

        FrontClaude.Changed += ShowTray;
        ChatOnScreen.Changed += ShowTray;
        SharingChanged += ShowTray;
        ChatOnScreen.Start(_ui);
        Chime.Start(_ui);
        // An update can move the launcher the entry names; it is put right at every start.
        if (Settings.Chime) _ = Task.Run(SyncChimeHook);
        FrontClaude.Activated += profile => _ = RefreshFront(profile);
        FrontClaude.Start(_ui);
        // A Claude in front is spending its figure, so it is asked after more
        // often than the five minutes the rest get.
        _frontTimer = new Timer(_ => { if (FrontClaude.Profile is string front) _ = RefreshFront(front); },
            null, TimeSpan.FromSeconds(30), UsageMonitor.OpenProfileTtl);

        RegisterNotifications();

        // Built now, hidden, so the first left click shows it rather than paying
        // to construct a window and its backdrop before anything appears.
        _flyout = new FlyoutWindow(ShowManager, ShowSupport, Quit);
        SessionEnd.Watch(_flyout, Quit);

        // A tray app comes up hidden by default — the notification-area icon is
        // the whole of it until asked for more. Turned off, it opens the manager
        // straight away, for someone who would rather see the window on launch.
        if (!Settings.StartHidden) ShowManager();

        // The one thing the app does off nobody's press: keep an opted-in
        // account's five-hour window open. It runs on its own timer, from launch,
        // window or no window — the app spends most of its life as a tray icon
        // with no manager window built at all, so the manager's usage timer, which
        // only ticks once that window exists, could never carry it. The threadpool
        // timer touches no UI, so it does not need one either. First pass after a
        // short beat, so a login-time launch is not asking the network before it
        // is up; every five minutes after, which the usage cache mostly answers
        // for free.
        _warmTimer = new Timer(
            _ => _ = SweepWarmProfiles(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5));

        // A minute in, so a login-time launch is not on the network before it is
        // up; hourly after, as the Mac checks. A check is at most eleven of
        // GitHub's sixty unauthenticated requests an hour: the list, and each
        // of the last ten releases' feed.
        _updateTimer = new Timer(
            _ => _ = CheckForUpdates(), null, TimeSpan.FromSeconds(60), TimeSpan.FromHours(1));

        _sharingTimer = new Timer(_ => SyncSharing(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    private Timer? _sharingTimer;
    public static readonly SharedUsage Sharing = new();
    public static SharedUsage.Result? SharingResult { get; private set; }
    /// Raised on the UI thread after each pass.
    public static event Action? SharingChanged;

    internal void SyncSharingSoon() => Task.Run(SyncSharing);

    /// What a sharing key is called on screen: a Claude by its name, an account
    /// signed in here by its email.
    public static string SharingName(string key)
    {
        if (key == UsageSharing.MainKey) return "Claude";
        if (PoolAccount.IsAccount(key)) return PoolAccounts.Load().FirstOrDefault(a => a.Id == key)?.Name ?? "Signed-in account";
        return Store.Shortcuts.FirstOrDefault(s => s.Id.ToString() == key)?.Name ?? key;
    }
    private int _sharingBusy;
    private string? _lastSharingProblem;

    /// Logins renew and Claudes open and close, so the relay is told again and
    /// each environment settled on a timer. Nothing is touched for a person
    /// who shares nothing and holds no lines.
    internal void SyncSharing()
    {
        if (Interlocked.Exchange(ref _sharingBusy, 1) == 1) return;
        try
        {
            if (UsageSharing.Load().Lenders.All(l => l.Value.Count == 0) && PoolEnvironment.ReadJournal().Profiles.Count == 0
                && RelayKeeper.Send(new RelayRequest(), timeoutMs: 500) is null)
            {
                if (SharingResult is not null)
                {
                    SharingResult = null;
                    _ui?.TryEnqueue(() => SharingChanged?.Invoke());
                }
                return;
            }
            var claudes = new List<(string, string, string)> { (UsageSharing.MainKey, "Claude", GraftPaths.DefaultProfile) };
            claudes.AddRange(Store.Shortcuts.ToList().Select(s => (s.Id.ToString(), s.Name, s.ProfileDir)));
            SharingResult = Sharing.Pass(claudes, profile => UsageMonitor.Peek(profile) is { Usage: { } u, UpdatedAt: { } at }
                ? PoolLimits.FromPercent(u.FiveHour, u.FiveHourReset, u.Week, u.WeekReset, at) : null,
                ClaudeProcesses.IsRunning);
            if (SharingResult.Problem is string problem && problem != _lastSharingProblem)
                Diagnostics.Note("pool.problem", new Dictionary<string, object?> { ["problem"] = problem });
            _lastSharingProblem = SharingResult.Problem;
            _ui?.TryEnqueue(() => SharingChanged?.Invoke());
        }
        catch (Exception e) { Record("pool.sync", e); }
        finally { Volatile.Write(ref _sharingBusy, 0); }
    }

    /// The classic toast path, not WinUI's AppNotificationManager, which needs a
    /// resource DLL the self-contained runtime does not ship and throws at
    /// registration. This one registers its own activator for an unpackaged app.
    private void RegisterNotifications()
    {
        try { ToastNotificationManagerCompat.OnActivated += OnToastActivated; }
        catch (Exception e) { Record("notify.register", e); }
    }

    // Only Install reaches here — Dismiss is handled by the toast itself — and the
    // callback is off a background thread, so it hops to the UI thread to act.
    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (!args.TryGetValue("action", out var action)) return;
        if (action == "closeStray"
            && args.TryGetValue("pid", out var pid) && int.TryParse(pid, out var p)
            && args.TryGetValue("started", out var started) && long.TryParse(started, out var ticks))
        {
            var target = new GracefulQuit.Target(p, new DateTime(ticks, DateTimeKind.Local));
            System.Threading.Tasks.Task.Run(() => UpdateRecovery.CloseStray(target));
            return;
        }
        if (action != "install") return;
        OnUi(() => { if (AvailableUpdate is { } release) _ = InstallUpdateAsync(release); });
    }

    private async Task CheckForUpdates()
    {
        if (!GraftUpdates.IsInstalled) return;
        try
        {
            var found = await GraftUpdates.CheckAsync();
            OnUi(() =>
            {
                SetAvailableUpdate(found);
                if (found is not null && Updater.InstallsOnItsOwn(Settings.HoldUpdates, GraftWindowOpen(), Updater.IsDevelopment))
                    _ = InstallUpdateAsync(found, quiet: true);
            });
        }
        catch (Exception e) { Record("update.check", e); }
    }

    /// Throws through to the caller so a failed check can be told from one that
    /// found nothing — the button says something different for each.
    internal async Task<Updater.Release?> RunUpdateCheckAsync()
    {
        try
        {
            var found = await GraftUpdates.CheckAsync();
            SetAvailableUpdate(found);
            return found;
        }
        catch (Exception e)
        {
            Record("update.check", e);
            throw;
        }
    }

    private void SetAvailableUpdate(Updater.Release? release)
    {
        // A check landing while one is staged would put the real answer over it.
        if (_stagedUpdate is not null && !ReferenceEquals(release, _stagedUpdate)) return;
        AvailableUpdate = release;
        UpdateChecked = true;
        UpdateProgressChanged?.Invoke();

        // The notify half of the feature: the icon a person is not looking at
        // still says an update is waiting. Once per version, so a check every six
        // hours does not keep rewriting the same tooltip.
        var version = release?.Version.ToString();
        // Only a held update is announced; one that installs itself says nothing.
        if (version is not null && version != _notifiedVersion && Settings.HoldUpdates)
        {
            _notifiedVersion = version;
            UpdateToast.Available(release!);
        }
        ShowTray();
    }

    private Task<bool>? _installing;
    private ClaudeQuitWatch? _quitWatch;

    /// Fetches the package and hands off to Velopack, then quits — the folder
    /// cannot be swapped while this process holds it open.
    /// A second press while one is under way joins it rather than starting
    /// another download. False when the download failed and nothing was handed off.
    internal Task<bool> InstallUpdateAsync(Updater.Release release, bool quiet = false) =>
        ReferenceEquals(release, _stagedUpdate) ? Task.FromResult(false) : _installing ??= DownloadAndInstall(release, quiet);

    private Updater.Release? _stagedUpdate;

    /// An update of Graft that is never fetched or installed, for the debug window;
    /// a <paramref name="downloaded"/> fraction holds its progress bar there.
    internal void StageUpdate(bool staged, double? downloaded = null)
    {
        _stagedUpdate = staged ? new Updater.Release { Version = new Version(Updater.Current.Major, Updater.Current.Minor + 1, 0), Notes = "Staged from the debug window." } : null;
        UpdateDownload = staged ? downloaded : null;
        SetAvailableUpdate(_stagedUpdate);
        if (!staged) _ = CheckForUpdates();
    }

    internal bool UpdateStaged => _stagedUpdate is not null;

    private bool GraftWindowOpen() =>
        _window?.AppWindow.IsVisible == true || _flyout?.AppWindow.IsVisible == true;

    private async Task<bool> DownloadAndInstall(Updater.Release release, bool quiet)
    {
        if (!quiet) UpdateToast.Downloading(release);
        SetUpdateDownload(0);
        // Created here, on the UI thread, so each report lands back on it.
        var progress = new Progress<double>(fraction =>
        {
            SetUpdateDownload(fraction);
            if (!quiet) UpdateToast.Report(fraction);
        });
        try { await GraftUpdates.DownloadAsync(release, progress); }
        catch (Exception e)
        {
            Record("update.download", e);
            _installing = null;
            SetUpdateDownload(null);
            // One that installs itself tries again at the next check.
            if (!quiet) UpdateToast.Failed(release);
            return false;
        }

        if (!quiet) UpdateToast.Installing();
        Diagnostics.Note("update.install", new Dictionary<string, object?>
        {
            ["version"] = release.Version.ToString(), ["onItsOwn"] = quiet,
        });
        try { GraftUpdates.ApplyOnExit(release); }
        catch (Exception e)
        {
            Record("update.apply", e);
            _installing = null;
            SetUpdateDownload(null);
            if (!quiet) UpdateToast.Failed(release);
            return false;
        }
        await ShutDown();
        return true;
    }

    private void SetUpdateDownload(double? fraction)
    {
        UpdateDownload = fraction;
        ShowTray();
        UpdateProgressChanged?.Invoke();
    }

    /// Another account answering the conversation on screen, by name; null
    /// when it is this Claude's own or nothing says.
    private static string? AnsweredBy(string front, Shortcut? shortcut) =>
        AnsweringKey(front, shortcut) is string by && by != (shortcut?.Id.ToString() ?? UsageSharing.MainKey) ? SharingName(by) : null;

    private static string? AnsweringKey(string front, Shortcut? shortcut)
    {
        var key = shortcut?.Id.ToString() ?? UsageSharing.MainKey;
        var cli = ChatOnScreen.OnScreenSession(front);
        if (cli is null || SharingResult?.Status?.Sessions.FirstOrDefault(s => s.Id == cli) is null) return null;
        var chat = SharedChats.Build(SharingResult.Status, UsageSharing.Load(), _ => null, key, cli).FirstOrDefault(c => c.Id == cli);
        return chat is null ? null : SharedChats.AnsweringKey(chat);
    }

    private TaskbarStrip? _strip;
    private string? _stripProfile;
    // The end of each profile's last turn that was on screen with its Claude in front:
    // a finished turn the person watched finish has nothing left to tell them.
    private readonly Dictionary<string, long?> _turnSeen = new(StringComparer.OrdinalIgnoreCase);

    /// The taskbar strip, when it is wanted: the Claude in front and every account
    /// it draws on, the one answering ringed, its headline figure and the conversation.
    private void ShowStrip(string? front, Shortcut? shortcut)
    {
        if (!Settings.TaskbarStrip)
        {
            _strip?.Dispose();
            _strip = null;
            return;
        }
        _strip ??= new TaskbarStrip(centre => ToggleFlyout(centre));
        // Like the Mac's item it stays put while another app is in front, on the last
        // Claude that was, and on Claude's own before any has been.
        var claudeInFront = front is not null;
        if (front is not null) _stripProfile = front;
        front = _stripProfile ??= GraftPaths.DefaultProfile;
        shortcut = Store.Shortcuts.FirstOrDefault(s => Fs.SamePath(s.ProfileDir, front));
        if (!_frontUsage.ContainsKey(front)) { _frontUsage[front] = null; _ = RefreshFront(front); }
        var key = shortcut?.Id.ToString() ?? UsageSharing.MainKey;
        var lenders = UsageSharing.Load().Lenders.GetValueOrDefault(key) ?? new List<string>();
        var answering = lenders.Count == 0 ? null : AnsweringKey(front, shortcut) ?? key;
        var accounts = PoolAccounts.Load();
        var marks = new[] { key }.Concat(lenders.Where(l => l != key))
            .Select(k => new TaskbarStrip.Mark(ShortcutIcons.Preview(MainPage.PresetOf(k, accounts)), k == answering)).ToList();
        var usage = _frontUsage.GetValueOrDefault(front) is { } u ? UsageStatus.Rolled(u, DateTimeOffset.UtcNow) : null;
        // No five-hour limit is not 0%: such an account shows its week, marked as one.
        var figure = usage is null ? null : usage.FiveHour is int five ? $"{five}%" : $"W {usage.Week}%";
        var (turn, ended) = ChatOnScreen.TurnFor(front);
        if (claudeInFront && turn == ChatFocus.Turn.Done) _turnSeen[front] = ended;
        var signal = turn switch
        {
            ChatFocus.Turn.NeedsPerson => TaskbarStrip.Signal.NeedsPerson,
            ChatFocus.Turn.Done when _turnSeen.GetValueOrDefault(front) != ended => TaskbarStrip.Signal.Done,
            _ => TaskbarStrip.Signal.None,
        };
        _strip.Show(new TaskbarStrip.Face(marks, figure, ChatOnScreen.TitleFor(front), signal));
    }

    /// The tray says which Claude is in front and what its account has spent,
    /// wearing that shortcut's icon; with none in front it is Graft's own.
    private void ShowTray()
    {
        if (_tray is null) return;
        var front = FrontClaude.Profile;
        var shortcut = front is null ? null : Store.Shortcuts.FirstOrDefault(s => Fs.SamePath(s.ProfileDir, front));
        if (UpdateDownload is { } f)
            _tray.ToolTipText = $"Claude Graft — downloading update, {(int)Math.Round(f * 100)}%";
        else if (front is not null)
            _tray.ToolTipText = FrontLabel.Tooltip(shortcut?.Name ?? "Claude",
                _frontUsage.GetValueOrDefault(front) is { } u ? UsageStatus.Rolled(u, DateTimeOffset.UtcNow) : null,
                ChatOnScreen.TitleFor(front), AnsweredBy(front, shortcut));
        else
            _tray.ToolTipText = AvailableUpdate is { } ready ? $"Claude Graft — update {ready.Version} ready" : "Claude Graft";

        ShowStrip(front, shortcut);
        var icon = shortcut is null ? null : ShortcutIcons.LinkIcon(shortcut).File;
        if (icon is not null && !icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) icon = null;
        if (icon == _trayIconFile) return;
        _trayIconFile = icon;
        _tray.IconSource = new BitmapImage(new Uri(icon ?? "ms-appx:///Assets/AppIcon.ico"));
    }

    /// After a shortcut's name or icon changed, from the UI thread.
    internal static void ShortcutsChanged()
    {
        ChatOnScreen.Rewatch();
        (Current as App)?.ShowTray();
    }

    private async Task RefreshFront(string profile)
    {
        try
        {
            var entry = await UsageMonitor.ReadAsync(profile, interactive: false, freshFor: UsageMonitor.OpenProfileTtl);
            OnUi(() => { _frontUsage[profile] = entry.Usage; ShowTray(); });
        }
        catch (Exception e) { Record("front.usage", e); }
    }

    private async Task SweepWarmProfiles()
    {
        try { await AutoStarter.SweepAsync(WarmProfiles()); }
        catch (Exception e) { Record("autostart.sweep", e); }
    }

    /// The profiles whose owners asked to keep their window open — the main Claude
    /// when its box is ticked, and each grafted shortcut carrying the flag. Read
    /// off the list already in memory rather than reloaded, so a sweep on the
    /// threadpool thread does not reassign the collection the windows are drawing.
    private static IEnumerable<string> WarmProfiles()
    {
        var profiles = new List<string>();
        if (Settings.KeepMainWarm) profiles.Add(GraftPaths.DefaultProfile);
        // Snapshot first: this runs on the threadpool while the windows may be
        // editing the list, and enumerating one mid-change throws.
        profiles.AddRange(Store.Shortcuts.ToList().Where(s => s.KeepWarm).Select(s => s.ProfileDir));
        return profiles;
    }

    private void ToggleFlyout() => ToggleFlyout(null);

    /// <paramref name="from"/>: the centre of what was clicked on the taskbar, for a
    /// flyout to stand over; the tray icon's own click leaves it in the corner.
    private void ToggleFlyout(Windows.Graphics.PointInt32? from)
    {
        // Read on this thread, the click's own, before hopping to the UI thread:
        // the pointer is on the icon now and will have moved by the time the
        // window is measured and placed. The foreground window is read here too,
        // as early as the click allows, so the flyout can hand focus back to the
        // app the person was in when it dismisses — before the click has had a
        // chance to move it to the shell.
        var anchor = from ?? TrayAnchor.CursorNow();
        var priorForeground = GetForegroundWindow();
        OnUi(() =>
        {
            _flyout ??= new FlyoutWindow(ShowManager, ShowSupport, Quit);
            _flyout.Toggle(anchor, priorForeground, centred: from is not null);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private void ShowMenu()
    {
        Store.Load();

        var items = new List<(string Text, bool Enabled, Action? Invoke)>();
        var setup = Onboarding.Check(Store);
        if (setup != SetupState.Ready)
        {
            items.Add(setup == SetupState.NotInstalled
                ? ("Install Claude Desktop…", true, ShowManager)
                : ("Sign in to Claude…", true, ShowManager));
        }
        else if (Store.Shortcuts.Count == 0)
        {
            items.Add(("No profiles yet", false, null));
        }
        else
        {
            foreach (var shortcut in Store.Shortcuts)
            {
                var config = Store.ConfigFor(shortcut);
                items.Add(("Open " + shortcut.Name, true, () => Task.Run(() => Launcher.Open(config))));
            }
        }
        items.Add((TrayMenu.Separator, false, null));
        // The Mac's wording: "Version X is available" installs it, otherwise the
        // plain "Check for Updates".
        items.Add(AvailableUpdate is { } ready
            ? ($"Version {ready.Version} is available", true, () => _ = InstallUpdateAsync(ready))
            : ("Check for Updates", true, () => _ = CheckForUpdates()));
        items.Add(("Manage Profiles…", true, ShowManager));
        items.Add(("Settings…", true, ShowSettings));
        items.Add(("Quit", true, Quit));

        TrayMenu.Show(items);
    }

    /// The warning before closing Claudes needs a window to sit in, and the
    /// flyout closes as soon as anything else takes the focus.
    internal static void ConfirmRestartClaudes() => (Current as App)?.OnUi(() =>
    {
        var app = (App)Current;
        app.Manager().ConfirmRestartClaudes();
    });

    /// Built afresh for each open and closed for real: Windows plays the opening
    /// animation only for a new window, and a hidden one re-shown flashes black first.
    // One closing out is let go rather than shown again halfway through.
    internal IntPtr ManagerHandle => _window is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : IntPtr.Zero;

    private MainWindow Manager() => _window is { IsClosing: false } open ? open : _window = NewManager();

    private MainWindow NewManager()
    {
        var window = new MainWindow();
        window.Closed += (_, _) => { if (_window == window) _window = null; };
        return window;
    }

    private void ShowManager() => OnUi(() =>
    {
        Manager().Show();
    });

    /// Settings live in a dialog on the manager window, so opening them from the
    /// tray brings the window up first — a dialog needs a window to sit in.
    private void ShowSettings() => OnUi(() =>
    {
        Manager().ShowSettings();
    });

    private void ShowSupport() => OnUi(() =>
    {
        Manager().ShowSupport();
    });

    private void Quit() => OnUi(() => _ = ShutDown());

    // The strip is a child of Explorer's taskbar: one whose process just ends stalls
    // Explorer until Windows notices. The manager goes as a close does, hidden first.
    private async Task ShutDown()
    {
        _strip?.Dispose();
        _strip = null;
        if (_window is { } window && window.AppWindow.IsVisible)
        {
            window.AppWindow.Hide();
            await Task.Delay(300);
        }
        _tray?.Dispose();
        WindowIdentity.Stop();
        FrontClaude.Stop();
        Exit();
    }

    /// Runs on the UI thread whether the caller is already there or on the
    /// tray's message-window thread.
    private void OnUi(Action action)
    {
        if (_ui is null || _ui.HasThreadAccess) action();
        else _ui.TryEnqueue(() => action());
    }
}

/// A minimal ICommand for the tray's double- and right-click, which take no parameter.
public sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
{
    // Always enabled, so never raised.
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}

/// <summary>
/// A native Win32 popup menu at the cursor. A popup menu only registers clicks
/// while its owner window holds the foreground, and the tray's own window is
/// message-only and cannot — so this shows a throwaway, fully transparent
/// top-level window at the cursor, makes it foreground, and owns the menu with
/// it. TrackPopupMenuEx returns the chosen id, so no WndProc is needed.
/// </summary>
internal static class TrayMenu
{
    internal const string Separator = "__graft-menu-separator__";

    public static void Show(List<(string Text, bool Enabled, Action? Invoke)> items)
    {
        nint owner = CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TOPMOST, "STATIC", string.Empty,
            WS_POPUP, 0, 0, 1, 1, 0, 0, 0, 0);
        if (owner == 0) return;

        nint menu = CreatePopupMenu();
        if (menu == 0) { DestroyWindow(owner); return; }

        try
        {
            var actions = new List<Action?>();
            uint id = 1;
            foreach (var (text, enabled, invoke) in items)
            {
                if (text == Separator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                    continue;
                }
                uint flags = MF_STRING | (enabled ? 0u : MF_GRAYED);
                AppendMenu(menu, flags, id, text);
                actions.Add(invoke);
                id++;
            }

            GetCursorPos(out var pt);

            // The window has to be visible for SetForegroundWindow to take, so
            // it is shown — but at one transparent pixel it is shown to nobody.
            SetLayeredWindowAttributes(owner, 0, 0, LWA_ALPHA);
            ShowWindow(owner, SW_SHOW);
            SetForegroundWindow(owner);

            uint chosen = TrackPopupMenuEx(menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, owner, 0);

            // The documented trailer: without a message posted to the owner, the
            // menu can leave a stale mouse-capture that eats the next click.
            PostMessage(owner, WM_NULL, 0, 0);

            if (chosen >= 1 && chosen <= actions.Count)
                actions[(int)chosen - 1]?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
            DestroyWindow(owner);
        }
    }

    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x0080, WS_EX_LAYERED = 0x00080000, WS_EX_TOPMOST = 0x0008;
    private const uint LWA_ALPHA = 0x0002;
    private const int SW_SHOW = 5;
    private const uint WM_NULL = 0x0000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint hMenu, uint flags, nuint idNewItem, string? newItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint hMenu, uint flags, int x, int y, nint hWnd, nint tpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
}
