using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// Which conversation a Claude has on screen. Claude writes lastFocusedAt into
/// a conversation's record the moment its window switches to it, so the
/// newest focus in a Claude's own store is the one in front.
///
/// A focus belongs to the store it turned up in first: the chat mirror copies
/// a record across with its focus, and read as the second store's own, it
/// would name the other Claude's conversation. On the first look every store
/// holding a focus owns it, since nothing then says which wrote it.
/// </summary>
public sealed class ChatFocus
{
    public sealed record Record(string Session, string? Title, long? FocusedAt, bool Archived, DateTime Written, string? Cli = null,
        long? TurnEdgeAt = null, long? PromptedAt = null, bool AsksPerson = false, long? ErrorAt = null);

    public enum Turn { None, Working, Done, NeedsPerson }

    /// Where a conversation's last turn stands. A prompt newer than the last turn's
    /// end is one still being answered; an ended turn that left a question, or broke
    /// after the prompt, waits on the person. A permission asked mid-turn is never
    /// written down, so only the hook's report of one (<paramref name="askedAt"/>) says so.
    public static Turn TurnOf(Record r, long? askedAt = null)
    {
        // A permission the hook reported, newer than the turn's end, holds the turn up.
        if (askedAt is long asking && asking > (r.TurnEdgeAt ?? 0) && asking > (r.PromptedAt ?? 0)) return Turn.NeedsPerson;
        if (r.PromptedAt is long asked && (r.TurnEdgeAt is not long ended || asked > ended)) return Turn.Working;
        if (r.TurnEdgeAt is null) return Turn.None;
        if (r.AsksPerson || (r.ErrorAt is long broke && broke >= (r.PromptedAt ?? 0))) return Turn.NeedsPerson;
        return Turn.Done;
    }

    public static Record? Parse(ReadOnlySpan<byte> json, DateTime written)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String) return null;
            return new Record(
                id.GetString()!,
                root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                root.TryGetProperty("lastFocusedAt", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out var at) ? at : null,
                root.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True,
                written,
                root.TryGetProperty("cliSessionId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                Stamp(root, "lastTurnEdgeAt"),
                Stamp(root, "latestUserFrameAt"),
                root.TryGetProperty("turnWrapUp", out var w) && w.ValueKind == JsonValueKind.Object
                    && w.TryGetProperty("needsUser", out var n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Length > 0,
                Stamp(root, "errorAt"));
        }
        catch (JsonException) { return null; }
    }

    private static long? Stamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var at) ? at : null;

    private readonly Dictionary<(string Session, long At), HashSet<string>> _owners = new();
    private readonly HashSet<string> _looked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Session, long At, string? Title, int Missing)> _shown = new(StringComparer.OrdinalIgnoreCase);

    /// Takes in one look at every store, keyed by store, and settles which
    /// focuses each store owns. Call before asking any store's conversation.
    public void Look(IReadOnlyDictionary<string, IReadOnlyList<Record>> stores)
    {
        var fresh = new Dictionary<(string, long), List<(string Store, DateTime Written)>>();
        foreach (var (store, records) in stores)
            foreach (var r in records)
                if (r.FocusedAt is long at)
                {
                    var key = (r.Session, at);
                    if (_owners.TryGetValue(key, out var owners))
                    {
                        if (!_looked.Contains(store)) owners.Add(store);
                    }
                    else (fresh.TryGetValue(key, out var l) ? l : fresh[key] = new()).Add((store, r.Written));
                }

        foreach (var (key, holders) in fresh)
        {
            var firstTime = holders.Where(h => !_looked.Contains(h.Store)).ToList();
            var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A store seen for the first time cannot say who wrote what it holds.
            foreach (var h in firstTime) owners.Add(h.Store);
            // Seen in several stores at once after that, the copy came later.
            var known = holders.Except(firstTime).OrderBy(h => h.Written).ToList();
            if (known.Count > 0) owners.Add(known[0].Store);
            _owners[key] = owners;
        }
        foreach (var store in stores.Keys) _looked.Add(store);
    }

    /// The record id last named on screen in a store, whatever OnScreen returned.
    public string? ShownSession(string store) => _shown.TryGetValue(store, out var s) ? s.Session : null;

    /// The title of the conversation on screen in one store, or null for none.
    public string? OnScreen(string store, IReadOnlyList<Record> records)
    {
        var best = records
            .Where(r => r.FocusedAt is long at && _owners.TryGetValue((r.Session, at), out var o) && o.Contains(store))
            .MaxBy(r => r.FocusedAt);

        if (_shown.TryGetValue(store, out var shown))
        {
            var stillThere = records.Any(r => r.Session == shown.Session);
            if (!stillThere && (best is null || best.FocusedAt <= shown.At))
            {
                // One look can land while Claude rewrites the file; two cannot.
                var missing = shown.Missing + 1;
                _shown[store] = shown with { Missing = missing };
                return missing >= 2 ? null : shown.Title;
            }
        }
        if (best is null) return null;
        _shown[store] = (best.Session, best.FocusedAt!.Value, best.Title, 0);
        // A conversation put away leaves the tray rather than handing it to an older one.
        return best.Archived ? null : best.Title;
    }
}
