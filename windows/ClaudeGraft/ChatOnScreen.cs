using ClaudeGraft.Core;
using Microsoft.UI.Dispatching;

namespace ClaudeGraft;

/// <summary>
/// The title of the conversation each Claude has on screen, kept current from
/// its records. A look is woken by a store changing rather than by a timer,
/// since Claude writes the record about a second after its window switches.
/// </summary>
public static class ChatOnScreen
{
    private static DispatcherQueue? _ui;
    private static readonly ChatFocus Focus = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Written, ChatFocus.Record? Record)> Parsed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> Titles = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, SharedChats.Record> _byCli = new();
    private static readonly Dictionary<string, string?> OnScreenCli = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (ChatFocus.Turn Turn, long? EndedAt)> Turns = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, (string Profile, ChatFocus.Record Record)> _byCliRecord = new();
    // Permissions the hook reported waiting, by Claude Code session, and when.
    private static readonly Dictionary<string, long> Asking = new();
    private static readonly List<FileSystemWatcher> Watchers = new();
    private static Timer? _debounce;

    /// Raised on the UI thread when a title changes.
    public static event Action? Changed;

    public static void Start(DispatcherQueue ui)
    {
        _ui = ui;
        _debounce = new Timer(_ => Look(), null, Timeout.Infinite, Timeout.Infinite);
        Rewatch();
    }

    /// After the list of shortcuts changed: the stores to watch changed with it.
    public static void Rewatch()
    {
        lock (Gate)
        {
            foreach (var w in Watchers) w.Dispose();
            Watchers.Clear();
            foreach (var root in Stores().Values.Where(Directory.Exists))
            {
                try
                {
                    var watcher = new FileSystemWatcher(root, "local_*.json") { IncludeSubdirectories = true };
                    watcher.Changed += Woken;
                    watcher.Created += Woken;
                    watcher.Deleted += Woken;
                    watcher.Renamed += Woken;
                    watcher.EnableRaisingEvents = true;
                    Watchers.Add(watcher);
                }
                catch (Exception e) { Diagnostics.Note("chat.watch-failed", new Dictionary<string, object?> { ["error"] = e.Message }); }
            }
        }
        _debounce?.Change(0, Timeout.Infinite);
    }

    // A save is several file events; one look after they settle.
    private static void Woken(object sender, FileSystemEventArgs e) => _debounce?.Change(300, Timeout.Infinite);

    public static string? TitleFor(string profile)
    {
        lock (Gate) return Titles.GetValueOrDefault(Path.GetFullPath(profile));
    }

    /// A conversation's record by the Claude Code session the relay sees.
    public static SharedChats.Record? RecordFor(string cliSession)
    {
        lock (Gate) return _byCli.GetValueOrDefault(cliSession);
    }

    /// Where the conversation on screen in a profile stands, and when its last turn ended.
    public static (ChatFocus.Turn Turn, long? EndedAt) TurnFor(string profile)
    {
        lock (Gate) return Turns.GetValueOrDefault(Path.GetFullPath(profile));
    }

    /// The profile a Claude Code session's conversation lives in, and where its turn stands.
    public static (string? Profile, ChatFocus.Turn Turn) SessionState(string cliSession)
    {
        lock (Gate)
            return _byCliRecord.TryGetValue(cliSession, out var r)
                ? (r.Profile, ChatFocus.TurnOf(r.Record, Asking.TryGetValue(cliSession, out var at) ? at : null))
                : (null, ChatFocus.Turn.None);
    }

    /// A permission the hook says a session now waits on, or null once it no longer does.
    public static void SetAsking(string cliSession, long? at)
    {
        lock (Gate)
        {
            if (at is long when) Asking[cliSession] = when;
            else if (!Asking.Remove(cliSession)) return;
        }
        _debounce?.Change(0, Timeout.Infinite);
    }

    /// The Claude Code session of the conversation on screen in a profile.
    public static string? OnScreenSession(string profile)
    {
        lock (Gate) return OnScreenCli.GetValueOrDefault(Path.GetFullPath(profile));
    }

    private static Dictionary<string, string> Stores()
    {
        var profiles = new List<string> { GraftPaths.DefaultProfile };
        profiles.AddRange(App.Store.Shortcuts.ToList().Select(s => s.ProfileDir));
        return profiles.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => Path.GetFullPath(p), p => Path.Combine(p, "claude-code-sessions"), StringComparer.OrdinalIgnoreCase);
    }

    private static void Look()
    {
        try
        {
            bool changed;
            lock (Gate)
            {
                var stores = Stores().ToDictionary(s => s.Key, s => Read(s.Value), StringComparer.OrdinalIgnoreCase);
                Focus.Look(stores.ToDictionary(s => s.Key, s => (IReadOnlyList<ChatFocus.Record>)s.Value));
                changed = false;
                var byCli = new Dictionary<string, SharedChats.Record>();
                // Under two accounts a session has two records; the newest focus speaks for it.
                var byCliRecord = new Dictionary<string, (string, ChatFocus.Record)>();
                foreach (var (profile, r) in stores.SelectMany(s => s.Value.Select(r => (s.Key, r))).Where(p => p.r.Cli is not null).OrderBy(p => p.r.FocusedAt ?? 0))
                {
                    byCli[r.Cli!] = new SharedChats.Record(r.Title, r.Archived);
                    byCliRecord[r.Cli!] = (profile, r);
                }
                _byCliRecord = byCliRecord;
                if (byCli.Count != _byCli.Count || byCli.Any(b => _byCli.GetValueOrDefault(b.Key) != b.Value)) changed = true;
                _byCli = byCli;
                foreach (var (profile, records) in stores)
                {
                    var title = Focus.OnScreen(profile, records);
                    var shown = Focus.ShownSession(profile);
                    var record = records.FirstOrDefault(r => r.Session == shown);
                    var cli = record?.Cli;
                    var turn = record is null || title is null ? (ChatFocus.Turn.None, null) : (ChatFocus.TurnOf(record, record.Cli is string c && Asking.TryGetValue(c, out var asked) ? asked : null), record.TurnEdgeAt);
                    if (Turns.GetValueOrDefault(profile) != turn) { Turns[profile] = turn; changed = true; }
                    if (OnScreenCli.GetValueOrDefault(profile) != cli) { OnScreenCli[profile] = cli; changed = true; }
                    if (Titles.GetValueOrDefault(profile) == title) continue;
                    Titles[profile] = title;
                    changed = true;
                }
            }
            if (changed) _ui?.TryEnqueue(() => Changed?.Invoke());
        }
        catch (Exception e) { Diagnostics.Note("chat.look-failed", new Dictionary<string, object?> { ["error"] = e.Message }); }
    }

    // A record is parsed again only when its file changed. Stashes are skipped
    // as the sweep skips them: an old record there is not this Claude's.
    private static List<ChatFocus.Record> Read(string root)
    {
        var records = new List<ChatFocus.Record>();
        if (!Directory.Exists(root)) return records;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "local_*.json", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => !f[root.Length..].Split('\\').Any(part => part.StartsWith('.') && part.EndsWith(".graft-own")))
                .ToList();
        }
        catch (IOException) { return records; }
        foreach (var file in files)
        {
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(file); }
            catch { continue; }
            if (!Parsed.TryGetValue(file, out var cached) || cached.Written != written)
            {
                ChatFocus.Record? record = null;
                try { record = ChatFocus.Parse(File.ReadAllBytes(file), written); }
                catch (IOException) { }
                cached = (written, record);
                Parsed[file] = cached;
            }
            if (cached.Record is { } r) records.Add(r);
        }
        return records;
    }
}
