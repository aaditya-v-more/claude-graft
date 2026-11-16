using System.ComponentModel;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClaudeGraft;

/// One profile as the manager and the flyout both show it: where it reads its
/// chats from, the folder its data lives in, whether a Claude is holding it
/// right now, and — filled in asynchronously — its plan usage.
public sealed class ShortcutRow : INotifyPropertyChanged
{
    /// Null for the main Claude, which has no shortcut behind it.
    public Shortcut? Shortcut { get; set; }
    public string ProfileDir { get; set; } = "";
    public string Name { get; set; } = "";
    /// The profile as automation finds it: its folder, or main for Claude's own.
    public string Key => Shortcut?.Folder ?? "main";
    public string SourceLabel { get; set; } = "";
    public string Folder { get; set; } = "";

    /// The main account cannot be renamed or re-sourced, so its Edit is hidden.
    public bool IsEditable => Shortcut is not null;
    public Visibility EditVisibility => IsEditable ? Visibility.Visible : Visibility.Collapsed;

    /// Whether the app keeps this account's five-hour window open on its own. Held
    /// on the shortcut for a grafted profile and in the app's settings for the
    /// main one, which has no shortcut behind it; the checkbox reads it here and
    /// the page persists the change.
    public bool KeepWarm => Shortcut?.KeepWarm ?? App.Settings.KeepMainWarm;

    public static ShortcutRow ForShortcut(Shortcut s) => new()
    {
        Shortcut = s, ProfileDir = s.ProfileDir, Name = s.Name,
        SourceLabel = App.Store.Label(s.Source), Folder = s.Folder,
        _signedIn = Onboarding.IsSignedIn(s.ProfileDir),
        _fetching = Staging.For(s.ProfileDir)?.Fetching == true,
    };

    public static ShortcutRow MainClaude() => new()
    {
        ProfileDir = GraftPaths.DefaultProfile, Name = "Claude",
        SourceLabel = "Main account", Folder = GraftPaths.DefaultFolder,
        _signedIn = Onboarding.IsSignedIn(GraftPaths.DefaultProfile),
        _fetching = Staging.For(GraftPaths.DefaultProfile)?.Fetching == true,
    };

    /// What the debug window staged for this card; every setter below lets it win.
    private AccountStage? Stage => Staging.For(ProfileDir);

    private bool _signedIn;

    /// A profile with no login has no usage to show and no account to start a
    /// session on, so its card offers Sign In in place of all of that.
    public bool SignedIn => _signedIn;

    public void SetSignedIn(bool signedIn)
    {
        if (signedIn == _signedIn) return;
        _signedIn = signedIn;
        foreach (var name in new[]
        {
            nameof(SignedIn), nameof(SignInVisibility), nameof(SignedInVisibility), nameof(OpenLabel),
            nameof(BarsVisibility), nameof(NoUsageVisibility), nameof(DefaultAppsVisibility), nameof(QuickOpenVisibility),
        }) Notify(name);
        NotifyStatus();
    }

    /// The sidebar's open button: only for a Claude that can be opened as it is,
    /// as the page's own Open is, so a shortcut not created yet or not signed in
    /// gets none.
    public Visibility QuickOpenVisibility =>
        _signedIn && (Shortcut is null || Shortcut.InstalledName is not null) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SignInVisibility => _signedIn ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SignedInVisibility => _signedIn ? Visibility.Visible : Visibility.Collapsed;
    public string OpenLabel => _opening ? "Opening…" : _signedIn ? "Open" : "Sign In";

    private bool _opening;

    public ShortcutRow() => Flight.Changed += FlightChanged;

    /// From the press until the Claude is up or the open has failed; ProfileRows owns it.
    public bool Opening => _opening;
    public OpenInFlight Flight { get; } = new();
    public Visibility OpeningVisibility => _opening ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NotOpeningVisibility => _opening ? Visibility.Collapsed : Visibility.Visible;
    public bool CanPressOpen => !_opening;

    /// The sidebar's line under the name.
    public string SidebarCaption => _opening ? "Opening…"
        : Shortcut is null ? "Installed normally"
        : Shortcut.InstalledName is null ? "Not created yet" : SourceLabel;

    private void FlightChanged()
    {
        if (Flight.Active == _opening) return;
        _opening = Flight.Active;
        foreach (var name in new[]
        {
            nameof(Opening), nameof(OpenLabel), nameof(OpeningVisibility), nameof(NotOpeningVisibility),
            nameof(CanPressOpen), nameof(SidebarCaption),
        }) Notify(name);
    }

    /// Google and company sign-in come back through a claude:// link, which
    /// reaches the right profile only once Graft is the handler Windows picks.
    public string SignInNote => Shortcut is null
        ? "Not signed in yet. Sign In opens Claude so you can log in."
        : _handler == ClaudeLinkHandler.Graft
            ? "Not signed in yet. Graft asks which Claude a Google or company sign-in is for."
            : "Not signed in yet. Email sign-in works; Google or company sign-in needs Graft for Claude links.";

    public Visibility DefaultAppsVisibility =>
        Shortcut is not null && !_signedIn && _handler != ClaudeLinkHandler.Graft ? Visibility.Visible : Visibility.Collapsed;

    private ClaudeLinkHandler _handler = ClaudeLinkAssociation.Current();

    /// Asked again on every press: the choice is made in Windows, while Graft stays open.
    public void RecheckLinkHandler()
    {
        var handler = ClaudeLinkAssociation.Current();
        if (handler == _handler) return;
        _handler = handler;
        Notify(nameof(SignInNote));
        Notify(nameof(DefaultAppsVisibility));
    }

    private bool _running;

    /// A Claude is holding this profile. The dot beside the name reads it the
    /// way the Mac flyout's does — lit for the account with a window open.
    public void SetRunning(bool running)
    {
        if (running == _running) return;
        _running = running;
        Notify(nameof(StatusBrush));
        Notify(nameof(StatusLabel));
        Notify(nameof(StateLine));
        Notify(nameof(SpokenName));
    }

    /// The status dot is a shape, which a screen reader never reaches, so the
    /// row's name carries what it shows.
    public string SpokenName => $"{Name}, {(_running ? "open" : "not open")}";

    public string StatusLabel => _running ? "A Claude is open on this profile" : "No Claude is open on this profile";

    // Lit green when a Claude holds the profile; a faint, theme-neutral grey
    // when none does, so the dot is legible on either background without a
    // converter to reach the theme brushes.
    public Brush StatusBrush => _running
        ? new SolidColorBrush(Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5A))
        : new SolidColorBrush(Color.FromArgb(0x66, 0x88, 0x88, 0x88));

    private UsageEntry? _usage;
    private bool _usageKnown;

    /// <paramref name="sweep"/> overrides the row's own idea of a newer figure,
    /// for a view that rebuilds its rows and so has to remember across them.
    public void SetUsage(UsageEntry entry, bool? sweep = null)
    {
        var fresh = sweep ?? (!_usageKnown || entry.UpdatedAt != _usage?.UpdatedAt);
        _usage = entry;
        _usageKnown = true;
        foreach (var name in new[]
        {
            nameof(FiveHour), nameof(FiveHourBarVisibility), nameof(Week), nameof(FiveHourText), nameof(WeekText),
            nameof(Fable), nameof(FableText), nameof(FableVisibility), nameof(FiveHourPercent), nameof(FiveHourLeft), nameof(WeekPercent), nameof(WeekLeft), nameof(FablePercent), nameof(FableLeft), nameof(FiveHourNoLimitVisibility),
            nameof(FiveHourResets), nameof(WeekResets), nameof(FableResets), nameof(FiveHourResetsVisibility), nameof(WeekResetsVisibility), nameof(FableResetsVisibility),
            nameof(BarsVisibility), nameof(NoUsageVisibility), nameof(BarsOpacity),
        }) Notify(name);
        NotifyStatus();
        Notify(nameof(StartEnabled));
        Notify(nameof(StartTooltip));

        // Last, so the bars have this reading's values before the sweep reads them
        // off to fill back up to. Only a newer figure sweeps: replaying a cached one
        // said the usage was refreshed when nothing had been asked.
        if (!fresh) return;
        Pulse++;
        Notify(nameof(Pulse));
    }

    /// Recomputes everything that moves with the clock alone — the countdowns, a
    /// window rolling over at its reset, the figure's age — with no read at all.
    public void Tick()
    {
        if (!_usageKnown) return;
        foreach (var name in new[]
        {
            nameof(FiveHour), nameof(FiveHourBarVisibility), nameof(Week), nameof(Fable), nameof(FiveHourText), nameof(WeekText),
            nameof(FableText), nameof(BarsOpacity), nameof(StartEnabled), nameof(StartTooltip), nameof(FiveHourLeft), nameof(WeekLeft), nameof(FableLeft),
        }) Notify(name);
        NotifyStatus();
    }

    /// The figures as they stand now: a window past its reset has closed.
    private Usage? Current => _usage?.Usage is { } u ? UsageStatus.Rolled(u, DateTimeOffset.UtcNow) : null;

    /// A figure that is old, off disk, or stuck behind a refusal is drawn faint,
    /// so it reads as not current before the caption is read.
    public double BarsOpacity => UsageStatus.IsStale(_usage, DateTimeOffset.UtcNow) ? 0.45 : 1.0;

    /// A usage read that threw rather than returning a reading leaves the button
    /// gated for ever otherwise. The state is then unknown, which is the case the
    /// button is left enabled for — the same fallback SetUsage makes when the
    /// reading it got cannot say a window is open.
    public void MarkUsageUnavailable()
    {
        if (_usageKnown) return;
        _usageKnown = true;
        Notify(nameof(StartEnabled));
        Notify(nameof(StartTooltip));
    }

    // Only a live reading is trusted to say a window is open. On the stale disk
    // fallback, or with the endpoint refusing, the state is unknown — and greying
    // the one useful action out on a guess is worse than leaving it, so
    // uncertainty leaves it enabled.
    private bool NoFiveHourLimit => _usage?.IsLive == true && _usage.Usage is { FiveHour: null };

    private bool WindowOpen => _usage?.IsLive == true
        && (Current?.FiveHourReset is DateTimeOffset r && r > DateTimeOffset.UtcNow || Current?.FiveHour > 0);

    /// Start Session opens a five-hour window; there is nothing for it to do once
    /// one is open — a second message neither restarts nor extends it — so the
    /// button greys out while a window is confirmed open, and while a start of its
    /// own is already in flight. It is also held until the first usage reading
    /// lands: before then whether a window is open is unknown, and offering the
    /// button in that gap lets a press fire a message the account did not need.
    public bool StartEnabled => _usageKnown && !_starting && !WindowOpen && !NoFiveHourLimit;

    public string StartTooltip => !_usageKnown
        ? "Waiting for this account's usage to load"
        : NoFiveHourLimit
            ? "This account has no five-hour limit, so there is no window to open"
        : WindowOpen
            ? "This account's five-hour window is already open"
            : "Sends one short message to this account to open its five-hour window";

    /// Bumped on every usage read, to replay the bars' fill. See BarPulse.
    public int Pulse { get; private set; }

    public int FiveHour => Current?.FiveHour ?? 0;
    public Visibility FiveHourBarVisibility =>
        Current is { FiveHour: null } ? Visibility.Collapsed : Visibility.Visible;
    public int Week => Current?.Week ?? 0;

    public string FiveHourText => Current is { FiveHour: null }
        ? "5 hours · no limit"
        : Line("5 hours", FiveHour, Current?.FiveHourReset);
    public string WeekText => Line("Week", Week, Current?.WeekReset);

    public int Fable => Current?.Fable ?? 0;
    public string FableText => Line("Fable", Fable, Current?.FableReset);
    public Visibility FableVisibility => _usage?.Usage?.Fable is not null ? Visibility.Visible : Visibility.Collapsed;

    // The Mac's rows: the label, the bar, the figure and the time left, each in a column of its own.
    public string FiveHourPercent => $"{FiveHour}%";
    public string FiveHourLeft => Left(Current?.FiveHourReset);
    public Visibility FiveHourNoLimitVisibility => Current is { FiveHour: null } ? Visibility.Visible : Visibility.Collapsed;
    public string WeekPercent => $"{Week}%";
    public string WeekLeft => Left(Current?.WeekReset);
    public string FablePercent => $"{Fable}%";
    public string FableLeft => Left(Current?.FableReset);
    // The dropdown's rows: the time left under the bar, as the Mac's menu has it.
    public string FiveHourResets => Resets(FiveHourLeft);
    public string WeekResets => Resets(WeekLeft);
    public string FableResets => Resets(FableLeft);
    public Visibility FiveHourResetsVisibility => FiveHourLeft.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WeekResetsVisibility => WeekLeft.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FableResetsVisibility => FableLeft.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    private static string Resets(string left) => left.Length > 0 ? $"Resets in {left}" : "";
    private static string Left(DateTimeOffset? reset) => reset is DateTimeOffset r && Graft.Countdown(r) is string left ? left : "";

    /// Created or not, and running or not, under the page's title, as the Mac's subtitle.
    public string StateLine => (Shortcut is null ? "" : (Shortcut.InstalledName is null ? "Not created yet" : "Created") + " · ")
        + (_running ? "Running" : "Not running");

    private static string Line(string label, int percent, DateTimeOffset? reset)
    {
        var text = $"{label} · {percent}%";
        if (reset is DateTimeOffset r && Graft.Countdown(r) is string left) text += $" · resets in {left}";
        return text;
    }

    private bool _fetching;

    /// A live usage read is out for this profile, whoever started it — a press,
    /// the window's own poll, or the keep-warm sweep in the background.
    public void SetFetching(bool fetching)
    {
        fetching |= Stage?.Fetching == true;
        if (fetching == _fetching) return;
        _fetching = fetching;
        NotifyStatus();
    }

    private UsageStatus.Line? Status => _signedIn ? UsageStatus.Describe(_usage, _fetching, DateTimeOffset.UtcNow) : null;

    public string UsageStatusText => Status?.Text ?? "";
    public Visibility UsageStatusVisibility => Status is { IsWarning: false } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UsageWarningVisibility => Status is { IsWarning: true } ? Visibility.Visible : Visibility.Collapsed;
    public bool Fetching => _fetching;
    public Visibility FetchingVisibility => _fetching ? Visibility.Visible : Visibility.Collapsed;

    private void NotifyStatus()
    {
        foreach (var name in new[]
        {
            nameof(UsageStatusText), nameof(UsageStatusVisibility), nameof(UsageWarningVisibility),
            nameof(Fetching), nameof(FetchingVisibility),
        }) Notify(name);
    }

    public Visibility BarsVisibility =>
        _signedIn && _usage?.HasUsage == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoUsageVisibility =>
        _signedIn && _usageKnown && _usage?.HasUsage != true ? Visibility.Visible : Visibility.Collapsed;

    private bool _starting;

    /// A session start is in flight for this account. While it is, the button
    /// gives way to a spinner and refuses a second press — the Mac's per-account
    /// claim, shown.
    public bool Starting
    {
        get => _starting;
        set
        {
            if (value == _starting) return;
            _starting = value;
            foreach (var name in new[]
            {
                nameof(Starting), nameof(StartVisibility), nameof(StartingVisibility), nameof(StartEnabled),
            }) Notify(name);
        }
    }

    public Visibility StartVisibility => _starting ? Visibility.Collapsed : Visibility.Visible;
    public Visibility StartingVisibility => _starting ? Visibility.Visible : Visibility.Collapsed;

    private Graft.ChatsElsewhere? _elsewhere;

    /// Chats another profile holds for this profile's account. Filled in async
    /// like the usage figures, and only for a shortcut keeping its own chats.
    public Graft.ChatsElsewhere? Elsewhere => _elsewhere;

    public void SetChatsElsewhere(Graft.ChatsElsewhere? found)
    {
        if (Shortcut is not null && Stage?.ChatsElsewhere is { } staged) found = staged;
        _elsewhere = found;
        foreach (var name in new[]
        {
            nameof(ElsewhereVisibility), nameof(ElsewhereNote), nameof(AdoptLabel),
        }) Notify(name);
    }

    public Visibility ElsewhereVisibility =>
        _elsewhere is not null ? Visibility.Visible : Visibility.Collapsed;

    /// Named, counted and qualified: the profile says where, the count says
    /// whether this is the history being missed, merge-or-recovery says the stakes.
    public string ElsewhereNote => _elsewhere is not { } found
        ? ""
        : $"{App.Store.NameOfProfile(found.Profile)} is holding {Chats(found.Count)} for the account this "
          + "profile is signed into that this one has not got. "
          + (found.Merging
              ? "This profile has chats of its own too, so they are merged rather than either set being replaced."
              : "This profile has none of its own yet.");

    public string AdoptLabel => _elsewhere?.Merging == true ? "Merge Them Here" : "Copy Them Here";

    public static string Chats(int count) => count == 1 ? "1 chat" : $"{count} chats";

    private string? _sidebarStatus;

    /// Where pin/order sync stands for this profile: synced, waiting on a Claude
    /// to close, or a retry. Only shared profiles ever carry one.
    public string? SidebarStatus => _sidebarStatus;
    public Visibility SidebarStatusVisibility =>
        string.IsNullOrEmpty(_sidebarStatus) ? Visibility.Collapsed : Visibility.Visible;

    public void SetSidebarStatus(string? status)
    {
        status = Stage?.SidebarStatus ?? status;
        if (status == _sidebarStatus) return;
        _sidebarStatus = status;
        Notify(nameof(SidebarStatus));
        Notify(nameof(SidebarStatusVisibility));
    }

    private string? _copiedNote;

    /// What the last copy did, kept on screen because Claude may not be open to
    /// show the answer for itself.
    public string? CopiedNote
    {
        get => _copiedNote;
        set
        {
            _copiedNote = value;
            Notify(nameof(CopiedNote));
            Notify(nameof(CopiedNoteVisibility));
        }
    }

    public Visibility CopiedNoteVisibility =>
        string.IsNullOrEmpty(_copiedNote) ? Visibility.Collapsed : Visibility.Visible;

    private string? _problem;

    /// Why the last start could not open a window, in the words the Mac dropdown
    /// uses. Cleared when a fresh start is pressed.
    public string? Problem
    {
        get => _problem;
        set
        {
            _problem = value;
            Notify(nameof(Problem));
            Notify(nameof(ProblemVisibility));
        }
    }

    public Visibility ProblemVisibility =>
        string.IsNullOrEmpty(_problem) ? Visibility.Collapsed : Visibility.Visible;

    /// How shared usage knows this Claude: "main", or its shortcut's id.
    public string SharingKey => Shortcut?.Id.ToString() ?? UsageSharing.MainKey;

    private string _sharingLabel = "Shared usage: off";
    private (string Text, bool Attention)? _sharingNote;

    public string SharingLabel => _sharingLabel;
    public string SharingNote => _sharingNote?.Text ?? "";
    public Visibility SharingNoteVisibility => _sharingNote is null ? Visibility.Collapsed : Visibility.Visible;
    public Brush SharingNoteBrush => Themed.Brush(_sharingNote?.Attention == true
        ? "SystemFillColorCautionBrush" : "TextFillColorTertiaryBrush");

    public void SetSharing(string label, (string Text, bool Attention)? note)
    {
        if (Stage?.Sharing is { } staged) (label, note) = (staged.Label, staged.Note is { } text ? (text, staged.Attention) : null);
        if (label == _sharingLabel && note == _sharingNote) return;
        _sharingLabel = label;
        _sharingNote = note;
        foreach (var name in new[] { nameof(SharingLabel), nameof(SharingNote), nameof(SharingNoteVisibility), nameof(SharingNoteBrush) })
            Notify(name);
    }

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// The accounts this Claude draws on, one row each, as the Mac lists them.
    public IReadOnlyList<LenderLine> Lenders { get; private set; } = [];
    public Visibility NoLendersVisibility => Lenders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string AddLenderLabel => Lenders.Count == 0 ? "Add an Account" : "Add Another Account";

    public void SetLenders(IReadOnlyList<LenderLine> lenders)
    {
        if (Stage?.Sharing is { } staged)
            lenders = staged.Lenders.Select((l, i) => new LenderLine
            {
                Claude = this, Key = LenderLine.StagedPrefix + i, Name = l.Name, State = l.State,
                Attention = l.Attention, Figures = l.Figures, First = i == 0,
            }).ToList();
        Lenders = lenders;
        Notify(nameof(Lenders));
        Notify(nameof(NoLendersVisibility));
        Notify(nameof(AddLenderLabel));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// One account a Claude draws on: its name, how it stands, and its figures.
public sealed class LenderLine
{
    public required ShortcutRow Claude { get; init; }
    public required string Key { get; init; }

    /// Keys of lenders the debug window made up, which nothing may save.
    public const string StagedPrefix = "staged:";
    public bool Staged => Key.StartsWith(StagedPrefix, StringComparison.Ordinal);
    public required string Name { get; init; }
    public string State { get; init; } = "";
    public bool Attention { get; init; }
    public string Figures { get; init; } = "";
    /// Lines go between rows, so the first has none above it and the last none below.
    public bool First { get; init; }
    public Thickness Rule => new(0, First ? 0 : 1, 0, 0);
    /// The account's colour, as its Claude's icon wears it, so a row reads as the same account the sidebar shows.
    public Microsoft.UI.Xaml.Media.ImageSource? Icon { get; init; }
    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public string Spoken => State.Length == 0 ? Name : $"{Name}, {State}";
    public string RemoveName => $"Stop drawing on {Name}";
    public string RemoveId => Id + ".remove";
    public string Id => Spoken.Length == 0 ? "" : ClaudeGraft.Spoken.Id("manager", Claude.Key, "lender." + Key[^Math.Min(8, Key.Length)..]);
    public Brush StateBrush => Themed.Brush(Attention ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
}