using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>
/// Graft's half of PoolEnvironment: reading a Claude's Code environment,
/// deciding what it should hold, and leaving both finished values in the
/// journal for whichever opens that Claude next. Each profile seals with its
/// own safe-storage key, which DPAPI hands over without a dialog.
/// </summary>
public static class ClaudeCodeEnvironment
{
    public abstract record State
    {
        public sealed record Off : State;
        public sealed record On : State;
        /// Wanted, with that Claude open: it takes the lines when it next starts.
        public sealed record Waiting : State;
        /// No longer wanted, with that Claude open: they come out when it
        /// closes, and the relay stays up until then.
        public sealed record Leaving : State;
        /// The person sends Claude Code somewhere of their own or trusts a
        /// certificate of their own; replacing either would quietly break it.
        public sealed record Conflict(string Name, bool Holding) : State;
        public sealed record Unreadable : State;

        public bool HoldsLines => this is On or Leaving || this is Conflict { Holding: true };
    }

    public sealed record Reading(PoolEnvironment.Entry Entry, string? Current, bool Holding, string? Conflict);

    /// What the profile's environment holds now and the two values to swap
    /// between; the one matching the disk is the disk's, byte for byte. A name
    /// holding exactly one of Graft's values is Graft's whatever sits beside it.
    public static Reading? Read(string profile, bool wanted, byte[] key)
    {
        if (PoolEnvironment.Outer(profile) is not { } outer) return null;
        string? current = null;
        if (outer.TryGetPropertyValue(PoolEnvironment.Field, out var node) && node is not null)
        {
            if (node is not JsonValue v || !v.TryGetValue<string>(out var text)) return null;
            current = text;
        }
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (current is not null)
        {
            if (Unseal(current, key) is not JsonObject obj) return null;
            foreach (var (name, value) in obj)
            {
                if (value is not JsonValue sv || !sv.TryGetValue<string>(out var s)) return null;
                variables[name] = s;
            }
        }
        var conflict = ConflictIn(variables);
        // Scoped values are laid over these as a session starts, so one naming
        // either variable would win over the lines without a word.
        if (outer[PoolEnvironment.ScopedField] is JsonValue scopedValue && scopedValue.TryGetValue<string>(out var scoped))
        {
            if (Unseal(scoped, key) is not { } nested) return null;
            conflict ??= ConflictNested(nested);
        }

        var lines = PoolEnvironment.Lines;
        var baseVars = variables.Where(v => !IsLine(v.Key, v.Value)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        var holding = baseVars.Count < variables.Count;
        var merged = new Dictionary<string, string>(baseVars, StringComparer.Ordinal);
        foreach (var (k, v) in lines) merged[k] = v;
        var exact = variables.Count == merged.Count && merged.All(m => variables.TryGetValue(m.Key, out var x) && x == m.Value);

        var plain = holding ? (baseVars.Count == 0 ? null : Seal(baseVars, key)) : current;
        // The lines never go in beside a value of the person's own, so the only
        // swap left is the one taking Graft's out.
        var pooled = conflict is not null
            ? (holding ? current : Seal(baseVars, key))
            : (exact ? current : Seal(merged, key));
        if (pooled is null) return null;
        return new Reading(new PoolEnvironment.Entry { Wanted = wanted && conflict is null, Plain = plain, Pooled = pooled },
            current, holding, conflict);
    }

    /// One of Graft's names, whatever its case, holding exactly Graft's value.
    public static bool IsLine(string name, string value) =>
        PoolEnvironment.Lines.Any(l => name.Equals(l.Key, StringComparison.OrdinalIgnoreCase) && value == l.Value);

    /// Either name set to anything but Graft's value; a lower-case proxy
    /// variable is read too.
    public static string? ConflictIn(IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (name, value) in variables)
            foreach (var (ours, wanted) in PoolEnvironment.Lines)
                if (name.Equals(ours, StringComparison.OrdinalIgnoreCase) && value != wanted) return ours;
        return null;
    }

    private static string? ConflictNested(JsonNode node)
    {
        if (node is not JsonObject obj) return null;
        var strings = new Dictionary<string, string>();
        foreach (var (k, v) in obj)
        {
            if (v is JsonValue sv && sv.TryGetValue<string>(out var s)) strings[k] = s;
            else if (v is not null && ConflictNested(v) is string found) return found;
        }
        return ConflictIn(strings);
    }

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// Compact and in a fixed order, so the same variables always seal alike.
    public static string Seal(IReadOnlyDictionary<string, string> variables, byte[] key)
    {
        var sorted = new SortedDictionary<string, string>(variables.ToDictionary(), StringComparer.Ordinal);
        return SealNode(JsonSerializer.SerializeToUtf8Bytes(sorted, Compact), key);
    }

    private static string SealNode(byte[] json, byte[] key) => Convert.ToBase64String(ClaudeCredentials.Seal(json, key));

    public static JsonNode? Unseal(string sealedValue, byte[] key)
    {
        try
        {
            var plain = ClaudeCredentials.Decrypt(Convert.FromBase64String(sealedValue), key);
            return plain is null ? null : JsonNode.Parse(plain);
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    /// One pass over every profile that wants the lines or may still hold them:
    /// the journal first, then each closed profile settled under the launch
    /// lock, the same lock and swap a launcher uses. A profile is never given
    /// the lines on the strength of a relay that is not there.
    public static Dictionary<string, State> Settle(IReadOnlyDictionary<string, bool> wanted, Func<string, byte[]?> keyFor,
        string? relay, bool relayReady, Func<string, bool> running)
    {
        var journal = PoolEnvironment.ReadJournal();
        var profiles = new Dictionary<string, (string Path, bool Wants)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, wants) in wanted) profiles[PoolEnvironment.Key(path)] = (path, wants);
        foreach (var path in journal.Profiles.Keys) profiles.TryAdd(path, (path, false));

        var entries = new Dictionary<string, PoolEnvironment.Entry>(journal.Profiles, StringComparer.OrdinalIgnoreCase);
        var readings = new Dictionary<string, (Reading Reading, byte[] Key)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, profile) in profiles)
        {
            if (keyFor(profile.Path) is not { } k || Read(profile.Path, profile.Wants, k) is not { } reading) continue;
            readings[key] = (reading, k);
            entries[key] = reading.Entry;
        }
        PoolEnvironment.WriteJournal(journal with { Relay = relay, Profiles = entries });

        var states = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, profile) in profiles)
        {
            if (!readings.TryGetValue(key, out var r))
            {
                states[key] = new State.Unreadable();
                continue;
            }
            var holds = r.Reading.Holding || ScopedCopies(profile.Path, r.Key);
            if (!running(profile.Path))
            {
                SidebarSync.WithLaunchLock(() =>
                {
                    if (running(profile.Path)) return;
                    // Part of the lines, left by an edit: neither finished
                    // value matches, so no swap could take them out. Graft read
                    // this very value and holds the key, so it fixes it first.
                    var entry = r.Reading.Entry;
                    if (r.Reading.Holding && r.Reading.Current != entry.Plain && r.Reading.Current != entry.Pooled
                        && PoolEnvironment.Read(profile.Path) == PoolEnvironment.FieldValue.Of(r.Reading.Current))
                    {
                        var written = PoolEnvironment.Write(entry.Plain, profile.Path);
                        Diagnostics.Note("pool.environment-partial-lines-removed", new Dictionary<string, object?> { ["profile"] = Path.GetFileName(profile.Path), ["ok"] = written });
                    }
                    PoolEnvironment.SettleBeforeOpening(profile.Path, () => relayReady);
                    RemoveScopedCopies(profile.Path, r.Key);
                });
                holds = HoldsLines(profile.Path, r.Key);
            }
            if (r.Reading.Conflict is string name && profile.Wants)
            {
                states[key] = new State.Conflict(name, holds);
                continue;
            }
            states[key] = (profile.Wants, holds) switch
            {
                (true, true) => new State.On(),
                (true, false) => new State.Waiting(),
                (false, true) => new State.Leaving(),
                _ => new State.Off(),
            };
        }

        // Wanting nothing and holding nothing, a profile is finished with.
        var finished = states.Where(s => s.Value is State.Off).Select(s => s.Key).ToList();
        if (finished.Count > 0)
        {
            var cleaned = PoolEnvironment.ReadJournal();
            foreach (var path in finished) cleaned.Profiles.Remove(path);
            PoolEnvironment.WriteJournal(cleaned);
        }
        return states;
    }

    /// Whether this Claude's Claude Code may still point at the relay, which is
    /// what keeps the relay up.
    public static bool HoldsLines(string profile, byte[] key) =>
        ScopedCopies(profile, key) || (Read(profile, false, key)?.Holding ?? false);

    /// What a folder's environment inherited is kept under scopedEnvVars and
    /// laid over envVars as a session starts; a copy of the lines there would
    /// outlive their removal and no launcher's swap could reach it.
    public static bool ScopedCopies(string profile, byte[] key) =>
        ScopedObject(profile, key) is { } obj && WithoutLines(obj).Found;

    public static bool RemoveScopedCopies(string profile, byte[] key)
    {
        if (ScopedObject(profile, key) is not { } obj) return false;
        var (kept, found) = WithoutLines(obj);
        if (!found) return false;
        var written = PoolEnvironment.Write(SealNode(Encoding.UTF8.GetBytes(kept.ToJsonString(Compact)), key), profile, PoolEnvironment.ScopedField);
        Diagnostics.Note("pool.environment-scoped-copies-removed", new Dictionary<string, object?> { ["profile"] = Path.GetFileName(profile), ["ok"] = written });
        return written;
    }

    private static JsonNode? ScopedObject(string profile, byte[] key) =>
        PoolEnvironment.Read(profile, PoolEnvironment.ScopedField).Text is string scoped ? Unseal(scoped, key) : null;

    private static (JsonNode Kept, bool Found) WithoutLines(JsonNode node)
    {
        if (node is not JsonObject obj) return (node.DeepClone(), false);
        var kept = new JsonObject();
        var found = false;
        foreach (var (name, value) in obj)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var text) && IsLine(name, text))
            {
                found = true;
                continue;
            }
            if (value is null) { kept[name] = null; continue; }
            var inner = WithoutLines(value);
            found |= inner.Found;
            kept[name] = inner.Kept;
        }
        return (kept, found);
    }
}
