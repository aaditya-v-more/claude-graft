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
    public string SourceLabel { get; set; } = "";
    public string Folder { get; set; } = "";

    /// The main account cannot be renamed or re-sourced, so its Edit is hidden.
    public bool IsEditable => Shortcut is not null;
    public Visibility EditVisibility => IsEditable ? Visibility.Visible : Visibility.Collapsed;

    public static ShortcutRow ForShortcut(Shortcut s) => new()
    {
        Shortcut = s, ProfileDir = s.ProfileDir, Name = s.Name,
        SourceLabel = App.Store.Label(s.Source), Folder = s.Folder,
    };

    public static ShortcutRow MainProfile() => new()
    {
        ProfileDir = GraftPaths.DefaultProfile, Name = "Claude",
        SourceLabel = "Main account", Folder = "Claude",
    };

    private bool _running;

    /// A Claude is holding this profile. The dot beside the name reads it the
    /// way the Mac flyout's does — lit for the account with a window open.
    public void SetRunning(bool running)
    {
        if (running == _running) return;
        _running = running;
        Notify(nameof(StatusBrush));
        Notify(nameof(StatusLabel));
    }

    public string StatusLabel => _running ? "A Claude is open on this profile" : "No Claude is open on this profile";

    // Lit green when a Claude holds the profile; a faint, theme-neutral grey
    // when none does, so the dot is legible on either background without a
    // converter to reach the theme brushes.
    public Brush StatusBrush => _running
        ? new SolidColorBrush(Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5A))
        : new SolidColorBrush(Color.FromArgb(0x66, 0x88, 0x88, 0x88));

    private UsageEntry? _usage;
    private bool _usageKnown;

    public void SetUsage(UsageEntry entry)
    {
        _usage = entry;
        _usageKnown = true;
        foreach (var name in new[]
        {
            nameof(FiveHour), nameof(Week), nameof(FiveHourText), nameof(WeekText),
            nameof(BarsVisibility), nameof(NoUsageVisibility),
            nameof(Fable), nameof(FableText), nameof(FableVisibility),
        }) Notify(name);
        // Only a live reading is trusted to say a window is open. On the stale
        // disk fallback, or with the endpoint refusing, the state is unknown — and
        // greying the one useful action out on a guess is worse than leaving it, so
        // uncertainty leaves it enabled.
        _windowOpen = entry.IsLive
            && (entry.Usage?.FiveHourReset is DateTimeOffset r && r > DateTimeOffset.UtcNow
                || entry.Usage?.FiveHour > 0);
        Notify(nameof(StartEnabled));
        Notify(nameof(StartTooltip));

        // Last, so the bars have this reading's values before the sweep reads them
        // off to fill back up to. A bump each read is what makes the fill a sign
        // the usage was refreshed rather than only that a number moved.
        Pulse++;
        Notify(nameof(Pulse));
    }

    private bool _windowOpen;

    /// Start Session opens a five-hour window; there is nothing for it to do once
    /// one is open — a second message neither restarts nor extends it — so the
    /// button greys out while a window is confirmed open, and while a start of its
    /// own is already in flight.
    public bool StartEnabled => !_starting && !_windowOpen;

    public string StartTooltip => _windowOpen
        ? "This account's five-hour window is already open"
        : "Sends one short message to this account to open its five-hour window";

    /// Bumped on every usage read, to replay the bars' fill. See BarPulse.
    public int Pulse { get; private set; }

    public int FiveHour => _usage?.Usage?.FiveHour ?? 0;
    public int Week => _usage?.Usage?.Week ?? 0;
    public int Fable => _usage?.Usage?.Fable ?? 0;
    public string FableText => Line("Fable", Fable, _usage?.Usage?.FableReset);
    public Visibility FableVisibility => _usage?.Usage?.Fable is null ? Visibility.Collapsed : Visibility.Visible;

    public string FiveHourText => Line("5 hours", FiveHour, _usage?.Usage?.FiveHourReset);
    public string WeekText => Line("Week", Week, _usage?.Usage?.WeekReset);

    private static string Line(string label, int percent, DateTimeOffset? reset)
    {
        var text = $"{label} · {percent}%";
        if (reset is DateTimeOffset r && Graft.Countdown(r) is string left) text += $" · resets in {left}";
        return text;
    }

    public Visibility BarsVisibility => _usage?.HasUsage == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoUsageVisibility =>
        _usageKnown && _usage?.HasUsage != true ? Visibility.Visible : Visibility.Collapsed;

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

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
