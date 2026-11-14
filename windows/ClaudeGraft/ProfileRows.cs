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
        var rows = new List<ShortcutRow> { ShortcutRow.MainProfile() };
        rows.AddRange(App.Store.Shortcuts.Select(ShortcutRow.ForShortcut));
        return rows;
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
