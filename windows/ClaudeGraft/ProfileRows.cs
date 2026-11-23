using System.Threading.Tasks;
using ClaudeGraft.Core;

namespace ClaudeGraft;

/// The profile list the manager and the flyout both draw, and the two reads
/// that fill it in. Kept in one place so the two windows cannot drift into
/// showing different accounts or reporting usage two different ways.
internal static class ProfileRows
{
    /// The main Claude leads, the way it does in the Mac dropdown, with the
    /// grafted profiles after it in the order the store holds them.
    public static List<ShortcutRow> Build()
    {
        App.Store.Load();
        var rows = new List<ShortcutRow> { ShortcutRow.MainClaude() };
        rows.AddRange(App.Store.Shortcuts.Select(ShortcutRow.ForShortcut));
        return rows;
    }

    /// Whether a freshly built list shows exactly what the rows on screen already
    /// do. A rebuilt row is a new ProgressBar and a new CheckBox, and each plays its
    /// entrance — the bar sliding in, the tick drawing, sometimes frozen half-drawn —
    /// so a view keeps the rows it has whenever nothing about them changed.
    /// Rows kept still take the shortcut just read: what a row does not draw,
    /// its icon among them, is what the editor opens with.
    public static bool KeepShown(IReadOnlyList<ShortcutRow> shown, IReadOnlyList<ShortcutRow> built)
    {
        if (!SameAs(shown, built)) return false;
        foreach (var (kept, fresh) in shown.Zip(built)) kept.Shortcut = fresh.Shortcut;
        return true;
    }

    private static bool SameAs(IReadOnlyList<ShortcutRow> shown, IReadOnlyList<ShortcutRow> built) =>
        shown.Count == built.Count
        && shown.Zip(built).All(p => Fs.SamePath(p.First.ProfileDir, p.Second.ProfileDir)
            && p.First.Name == p.Second.Name && p.First.SourceLabel == p.Second.SourceLabel
            && p.First.Folder == p.Second.Folder && p.First.KeepWarm == p.Second.KeepWarm);

    /// When a press on Refresh could next reach any account: null unless every
    /// signed-in one is held off, since a press still refreshes the rest.
    public static DateTimeOffset? HeldUntil(IEnumerable<ShortcutRow> rows)
    {
        var held = rows.Where(r => r.SignedIn).Select(r => UsageMonitor.HeldUntil(r.ProfileDir)).ToList();
        return held.Count > 0 && held.All(h => h is not null) ? held.Min() : null;
    }

    public static void Open(ShortcutRow row) => _ = OpenAsync(row);

    /// Claude's window comes up some seconds after Launcher.Open returns, so the
    /// row stays opening until its process is there, but never for longer than this.
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(10);

    public static async Task OpenAsync(ShortcutRow row)
    {
        var config = row.Shortcut is Shortcut s
            ? App.Store.ConfigFor(s)
            : new GraftConfig { ProfileDir = row.ProfileDir, SourceDir = null };
        if (row.Opening) return;
        row.Problem = null;
        try
        {
            var started = false;
            var ran = await row.Flight.RunAsync(() => Task.Run(async () =>
            {
                Launcher.Open(config);
                var until = DateTime.UtcNow + WindowWait;
                while (!(started = ClaudeProcesses.IsRunning(config.ProfileDir)) && DateTime.UtcNow < until)
                    await Task.Delay(400);
            }));
            if (ran && !started) row.Problem = L10n.Text("Claude did not start. Try opening it again.");
        }
        catch (Exception e)
        {
            row.Problem = L10n.Format("Claude could not be opened: {0}", e.Message);
            // The press has no caller to hand a throw to, so it is written down where the other passes are.
            Diagnostics.Note("open.failed", new Dictionary<string, object?>
            {
                ["profile"] = Path.GetFileName(config.ProfileDir),
                ["error"] = e.GetType().Name + ": " + e.Message,
            });
        }
    }

    /// The usage read as a fire-and-forget task wants: a throw here has nowhere
    /// to surface and would vanish — which is exactly how the main account's
    /// missing usage once hid a null dereference — so a failure is written down
    /// and the row simply goes without its bars rather than taking the pass down.
    public static async Task<UsageEntry?> ReadUsageSafe(string profileDir, bool interactive)
    {
        try
        {
            return await UsageMonitor.ReadAsync(profileDir, interactive);
        }
        catch (Exception e)
        {
            Diagnostics.Note("usage.rowFailed", new Dictionary<string, object?>
            {
                ["profile"] = profileDir,
                ["error"] = e.GetType().Name + ": " + e.Message,
            });
            return null;
        }
    }
}
