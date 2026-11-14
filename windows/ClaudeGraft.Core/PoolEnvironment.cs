using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>Presealed, compare-and-swap environment fields let launchers recover without accessing credentials.</summary>
public static class PoolEnvironment
{
    public const string FileName = "ccd-environment-config.json";
    public const string ProxyName = "HTTPS_PROXY", CertificateName = "NODE_EXTRA_CA_CERTS";
    public static string ProxyValue => "http://127.0.0.1:" + UsagePool.Port;
    public static string JournalFile => Path.Combine(UsagePool.Directory, "environment.json");
    public sealed record Entry(bool Wanted, string? Plain, string Pooled, bool Scoped = false);
    public sealed class Journal
    {
        public string? Relay { get; set; }
        public string? Owner { get; set; }
        public Dictionary<string, Entry> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    public sealed record Inspection(Entry Entry, bool Conflict, bool HoldsLines, bool Pending, string? Note);
    private sealed record Outer(JsonObject Json, byte[]? Bytes);

    public static Journal? ReadJournal()
    {
        try
        {
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(JournalFile), UsagePool.Json);
            if (journal?.Profiles is null || journal.Profiles.Any(p => p.Value is null || !Path.IsPathFullyQualified(p.Key))) return null;
            journal.Profiles = new(journal.Profiles, StringComparer.OrdinalIgnoreCase);
            return journal;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public static Journal LoadJournal()
    {
        var journal = ReadJournal();
        if (journal is null && File.Exists(JournalFile)) throw new IOException("The Shared Usage recovery journal is unreadable. It has been preserved and the relay must remain running until the profile environments are recovered.");
        return journal ?? new();
    }
    public static void WriteJournal(Journal journal)
    {
        if (File.Exists(JournalFile) && ReadJournal() is null) throw new IOException("The Shared Usage recovery journal changed or is unreadable. Its bytes have been preserved.");
        PoolStorage.Write(JournalFile, JsonSerializer.SerializeToUtf8Bytes(journal, UsagePool.Json));
    }
    private static Outer? Read(string profile)
    {
        var path = Path.Combine(profile, FileName);
        if (File.Exists(path + ".journal")) return null;
        try
        {
            if (!File.Exists(path)) return new(new(), null);
            var bytes = File.ReadAllBytes(path);
            return JsonNode.Parse(bytes) is JsonObject json ? new(json, bytes) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static bool StringField(JsonObject outer, string key, out string? text)
    {
        text = null;
        return outer[key] is null || outer[key] is JsonValue value && value.TryGetValue(out text);
    }
    private static Dictionary<string, string>? Decode(string? value, byte[] key)
    {
        if (value is null) return new(StringComparer.OrdinalIgnoreCase);
        byte[]? plain = null;
        try
        {
            plain = ClaudeCredentials.Decrypt(Convert.FromBase64String(value), key);
            if (plain is null) return null;
            using var document = JsonDocument.Parse(plain);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || !map.TryAdd(property.Name, property.Value.GetString()!)) return null;
            }
            return map;
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    private static string Encode(Dictionary<string, string> map, byte[] key)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(map);
        try { return Convert.ToBase64String(ClaudeCredentials.Encrypt(bytes, key)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static bool Owned(string name, string value) => name.Equals(ProxyName, StringComparison.OrdinalIgnoreCase) && value == ProxyValue
        || name.Equals(CertificateName, StringComparison.OrdinalIgnoreCase) && value == UsagePool.CertificateFile;
    private static bool Conflicts(Dictionary<string, string> map) => map.Any(p =>
        (p.Key.Equals(ProxyName, StringComparison.OrdinalIgnoreCase) || p.Key.Equals(CertificateName, StringComparison.OrdinalIgnoreCase)) && !Owned(p.Key, p.Value));

    // Claude seals the entire nested scope map as one field. Preserve every unrelated
    // value and stop when a field cannot be understood, rather than replacing it.
    private static (JsonNode? Cleaned, bool Holds, bool Conflict) CleanScopes(JsonNode? node, byte[] key)
    {
        if (node is null) return (null, false, false);
        if (node is not JsonValue text || !text.TryGetValue<string>(out var encoded)) throw new IOException("The scoped Code environment has an unfamiliar format.");
        byte[]? plain = null;
        try
        {
            plain = ClaudeCredentials.Decrypt(Convert.FromBase64String(encoded), key);
            if (plain is null || JsonNode.Parse(plain) is not JsonObject scopes) throw new IOException("A scoped Code environment could not be read.");
            var holds = false; var conflict = false;
            void Clean(JsonObject map)
            {
                foreach (var item in map.ToArray())
                {
                    if (item.Value is JsonObject nested) { Clean(nested); continue; }
                    if (item.Value is not JsonValue value || !value.TryGetValue<string>(out var s)) continue;
                    if (Owned(item.Key, s)) { holds = true; map.Remove(item.Key); }
                    else if (item.Key.Equals(ProxyName, StringComparison.OrdinalIgnoreCase) || item.Key.Equals(CertificateName, StringComparison.OrdinalIgnoreCase)) conflict = true;
                }
            }
            Clean(scopes);
            if (!holds) return (JsonValue.Create(encoded), false, conflict);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(scopes);
            try { return (JsonValue.Create(Convert.ToBase64String(ClaudeCredentials.Encrypt(bytes, key))), true, conflict); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception e) when (e is FormatException or JsonException) { throw new IOException("A scoped Code environment could not be read."); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Call under StateLock. A durable journal always lands before the profile changes.</summary>
    public static Inspection InspectAndApply(string profile, bool wanted, Journal journal, bool running, bool relayReady, byte[]? keyOverride = null)
    {
        Graft.ValidateProfilePath(profile, allowDefault: true);
        var path = Path.GetFullPath(profile);
        var outer = Read(path) ?? throw new IOException("Claude is saving its Code environment. The next refresh will try again.");
        if (!StringField(outer.Json, "envVars", out var current)) throw new IOException("The Code environment has an unfamiliar format.");
        var key = keyOverride ?? (OperatingSystem.IsWindows() ? ClaudeCredentials.SafeStorageKey(profile) : null)
            ?? throw new IOException("Open this Claude once to create its encrypted Code environment.");
        try
        {
            var values = Decode(current, key) ?? throw new IOException("This Claude's Code environment could not be unlocked.");
            var scopes = CleanScopes(outer.Json["scopedEnvVars"], key);
            var owned = values.Where(p => Owned(p.Key, p.Value)).Select(p => p.Key).ToArray();
            var conflict = Conflicts(values) || scopes.Conflict;
            foreach (var name in owned) values.Remove(name);
            var plain = owned.Length == 0 ? current : values.Count == 0 ? null : Encode(values, key);
            string pooled;
            if (conflict) pooled = current ?? Encode(values, key);
            else
            {
                values[ProxyName] = ProxyValue; values[CertificateName] = UsagePool.CertificateFile;
                pooled = owned.Length == 2 ? current! : Encode(values, key);
            }
            var entry = new Entry(wanted && !conflict, plain, pooled, running && scopes.Holds);
            journal.Profiles[path] = entry;
            WriteJournal(journal);
            var target = entry.Wanted && relayReady ? entry.Pooled : entry.Plain;
            var change = current != target || scopes.Holds;
            if (!running && change)
            {
                outer.Json["envVars"] = target;
                if (scopes.Holds) outer.Json["scopedEnvVars"] = scopes.Cleaned;
                if (!WriteCompared(profile, outer)) throw new IOException("Claude's Code environment changed during the update. The next refresh will try again.");
            }
            return new(entry, conflict, running ? owned.Length > 0 || scopes.Holds : target == pooled && target != plain,
                running && change, conflict ? "A custom proxy or certificate is configured. Graft preserves it; remove the conflict in Claude's Code settings to share usage." : running && change ? "Quit and reopen this Claude to apply usage sharing." : null);
        }
        finally { if (keyOverride is null) CryptographicOperations.ZeroMemory(key); }
    }

    private static bool WriteCompared(string profile, Outer original)
    {
        if (!Directory.Exists(profile)) return false;
        var current = Read(profile);
        if (current is null || !((current.Bytes is null && original.Bytes is null) || current.Bytes is not null && original.Bytes is not null && current.Bytes.AsSpan().SequenceEqual(original.Bytes))) return false;
        PoolStorage.AtomicWrite(Path.Combine(profile, FileName), JsonSerializer.SerializeToUtf8Bytes(original.Json, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    public static void Settle(string profile, Func<bool> relayReady)
    {
        var journal = LoadJournal();
        if (journal?.Profiles.GetValueOrDefault(Path.GetFullPath(profile)) is not Entry entry) return;
        var outer = Read(profile);
        if (outer is null || !StringField(outer.Json, "envVars", out var current) || current != entry.Plain && current != entry.Pooled) return;
        var target = entry.Wanted && relayReady() ? entry.Pooled : entry.Plain;
        if (target == current) return;
        outer.Json["envVars"] = target;
        if (!WriteCompared(profile, outer)) throw new IOException("Claude's Code environment could not be restored before opening it. Try again after it finishes saving.");
    }

    public static void PrepareForLaunch(string profile) => Settle(profile, () => PoolControl.EnsureRelayAsync(LoadJournal().Relay).GetAwaiter().GetResult());

    public static string[] TakeOutLines(Journal? remembered = null, Func<string, bool>? running = null)
    {
        // Read only after acquiring the mutex: a waiting cleanup must not later
        // overwrite a journal that a manager refreshed while it was waiting.
        using var stateLock = StateLock.Acquire();
        running ??= ClaudeProcesses.IsRunning;
        var onDisk = ReadJournal();
        if (onDisk is null && File.Exists(JournalFile)) throw new IOException("Shared Usage was disabled, but its recovery journal is unreadable. The relay is staying available to avoid interrupting open Code sessions.");
        var journal = onDisk ?? remembered ?? new();
        var present = Directory.Exists(GraftPaths.OwnData);
        var pending = new List<string>();
        foreach (var (profile, entry) in journal.Profiles.ToArray())
        {
            journal.Profiles[profile] = entry with { Wanted = false };
            if (!Directory.Exists(profile)) continue;
            if (running(profile)) { pending.Add(profile); continue; }
            var outer = Read(profile);
            if (outer is null || !StringField(outer.Json, "envVars", out var value)) { pending.Add(profile); continue; }
            // Claude can copy the proxy into a scope after the last manager pass.
            // Inspect the current scoped field, not the journal's older boolean.
            if (outer.Json["scopedEnvVars"] is not null)
            {
                if (!present) { pending.Add(profile); continue; }
                try
                {
                    var cleaned = InspectAndApply(profile, false, journal, false, false);
                    if (cleaned.HoldsLines) pending.Add(profile);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { pending.Add(profile); }
                continue;
            }
            if (value == entry.Plain) continue;
            if (value != entry.Pooled) { pending.Add(profile); continue; }
            outer.Json["envVars"] = entry.Plain;
            if (!WriteCompared(profile, outer)) pending.Add(profile);
        }
        if (present && File.Exists(JournalFile)) WriteJournal(journal);
        return pending.ToArray();
    }
}
