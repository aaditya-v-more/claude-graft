using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// <summary>
/// The two lines that send a Claude's Claude Code through the relay, put into
/// that Claude's own Code environment and taken out again.
///
/// The running app decrypts ccd-environment-config.json once and its editor
/// saves over the whole field, so every write happens with the profile closed,
/// under the launch lock. Graft leaves two finished values per profile in the
/// journal, and a launcher only puts one in place of the other, and only while
/// the file holds exactly the one expected.
/// </summary>
public static class PoolEnvironment
{
    public const string FileName = "ccd-environment-config.json";
    public const string Field = "envVars";
    public const string ScopedField = "scopedEnvVars";
    public const string ProxyName = "HTTPS_PROXY";
    public const string CertificateName = "NODE_EXTRA_CA_CERTS";

    public static string ProxyValue => $"http://127.0.0.1:{UsagePool.Port}";
    public static string CertificateValue => UsagePool.CertificateFile;
    public static IReadOnlyDictionary<string, string> Lines => new Dictionary<string, string>
    {
        [ProxyName] = ProxyValue,
        [CertificateName] = CertificateValue,
    };

    /// <summary>
    /// Drops the lines from this process when they hold exactly Graft's values, as
    /// they do in anything started from a borrowing Claude's terminal. Graft's own
    /// requests would otherwise go into its own relay, which no .NET client trusts,
    /// and a Claude it opened would borrow without having been given the lines.
    /// </summary>
    public static void ForgetInheritedLines()
    {
        foreach (var (name, value) in Lines)
            foreach (var spelling in new[] { name, name.ToLowerInvariant() })
                if (string.Equals(Environment.GetEnvironmentVariable(spelling), value, StringComparison.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable(spelling, null);
    }

    public static string JournalFile => Path.Combine(UsagePool.Directory, "environment.json");

    public sealed record Journal
    {
        /// The relay a launcher may start, as the Graft that wrote this knows it.
        public string? Relay { get; init; }
        /// By profile path.
        public Dictionary<string, Entry> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record Entry
    {
        public bool Wanted { get; init; }
        /// The field exactly as it reads without the lines; null is no field.
        public string? Plain { get; init; }
        public required string Pooled { get; init; }
    }

    /// The field as it is on disk, read without decrypting anything. Unreadable
    /// is a file that will not parse, or one Claude is part way through writing:
    /// nothing is decided on it and nothing is written.
    public abstract record FieldValue
    {
        public sealed record Absent : FieldValue;
        public sealed record Value(string Content) : FieldValue;
        public sealed record Unreadable : FieldValue;

        public string? Text => this is Value v ? v.Content : null;
        public static FieldValue Of(string? text) => text is null ? new Absent() : new Value(text);
    }

    public static string Key(string profile) => Path.GetFullPath(profile).TrimEnd('\\');
    public static string FileIn(string profile) => Path.Combine(profile, FileName);

    /// Null when the file cannot be read as an object; empty when there is none.
    public static JsonObject? Outer(string profile)
    {
        var path = FileIn(profile);
        if (File.Exists(path + ".journal")) return null;
        if (!File.Exists(path)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static FieldValue Read(string profile, string name = Field)
    {
        if (Outer(profile) is not { } outer) return new FieldValue.Unreadable();
        if (!outer.TryGetPropertyValue(name, out var node) || node is null) return new FieldValue.Absent();
        return node is JsonValue v && v.TryGetValue<string>(out var text) ? new FieldValue.Value(text) : new FieldValue.Unreadable();
    }

    /// Replaces the one field and nothing else, by moving a complete file over
    /// the old one, which is how Claude's own store writes it too.
    public static bool Write(string? value, string profile, string name = Field)
    {
        if (Outer(profile) is not { } outer) return false;
        if (value is null) outer.Remove(name);
        else outer[name] = value;
        var temp = Path.Combine(profile, $".{FileName}.graft-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(temp, outer.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FileIn(profile), overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch { }
            return false;
        }
    }

    private static readonly JsonSerializerOptions JournalJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static Journal ReadJournal() => JournalOnDisk() ?? new Journal();

    /// Null when there is no journal or none that reads: an uninstall takes the
    /// folder holding it, and the relay works from the copy it read last.
    public static Journal? JournalOnDisk()
    {
        try
        {
            if (!File.Exists(JournalFile)) return null;
            var read = JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalFile), JournalJson);
            return read is null ? null : read with { Profiles = new(read.Profiles, StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void WriteJournal(Journal journal)
    {
        try
        {
            Directory.CreateDirectory(UsagePool.Directory);
            var temp = Path.Combine(UsagePool.Directory, $".environment-{Guid.NewGuid():N}");
            File.WriteAllText(temp, JsonSerializer.Serialize(journal, JournalJson));
            File.Move(temp, JournalFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Puts the right value in place for a profile about to be opened, under
    /// the launch lock with the profile known closed. relayReady is asked only
    /// when the lines are or are about to be in place: a proxy that is not there
    /// leaves Claude Code unable to reach the service at all, so such a Claude
    /// opens without the lines rather than without Claude Code.
    public static void SettleBeforeOpening(string profile, Func<bool> relayReady)
    {
        if (!ReadJournal().Profiles.TryGetValue(Key(profile), out var entry)) return;
        var current = Read(profile);
        var name = Path.GetFileName(profile);
        if (current is FieldValue.Unreadable)
        {
            Diagnostics.Note("pool.environment-unreadable", new Dictionary<string, object?> { ["profile"] = name });
            return;
        }
        var value = current.Text;
        var wanted = entry.Wanted;
        if (wanted && (value == entry.Pooled || value == entry.Plain) && !relayReady())
        {
            wanted = false;
            Diagnostics.Note("pool.environment-no-relay", new Dictionary<string, object?> { ["profile"] = name });
        }
        var target = wanted ? entry.Pooled : entry.Plain;
        if (value == target) return;
        if (value != (wanted ? entry.Plain : entry.Pooled))
        {
            Diagnostics.Note("pool.environment-changed-since-read", new Dictionary<string, object?> { ["profile"] = name });
            return;
        }
        var written = Write(target, profile);
        Diagnostics.Note("pool.environment-written", new Dictionary<string, object?> { ["profile"] = name, ["lines"] = wanted, ["ok"] = written });
    }

    /// Puts every closed profile back as it was without the lines, for when
    /// Graft itself is going: --stop-sharing from the uninstaller, and the relay
    /// once Graft's install has gone. Every entry is marked unwanted so a
    /// launcher left behind puts nothing back. Answers the profiles that may
    /// still point at the relay: open ones, and any whose field is neither value.
    public static List<string> TakeOutLines(Journal? remembered, Func<string, bool> running)
    {
        var onDisk = JournalOnDisk();
        var journal = onDisk ?? remembered ?? new Journal();
        // The uninstaller took the folder: taking the lock or noting anything
        // would make it again.
        var present = Directory.Exists(GraftPaths.OwnData);
        var left = new List<string>();
        var profiles = new Dictionary<string, Entry>(journal.Profiles, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in journal.Profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            bool Restore()
            {
                if (!Directory.Exists(path)) return true;
                if (running(path)) return false;
                var current = Read(path);
                if (current is FieldValue.Unreadable) return false;
                if (current.Text == entry.Plain) return true;
                if (current.Text != entry.Pooled) return false;
                var written = Write(entry.Plain, path);
                if (present)
                    Diagnostics.Note("pool.environment-taken-out", new Dictionary<string, object?> { ["profile"] = Path.GetFileName(path), ["ok"] = written });
                return written;
            }
            if (!(present ? SidebarSync.WithLaunchLock(Restore) : Restore())) left.Add(path);
            profiles[path] = entry with { Wanted = false };
        }
        if (onDisk is not null) WriteJournal(journal with { Profiles = profiles });
        return left;
    }

    /// What a launcher asks: the relay a running Graft keeps up, or one it
    /// starts from the path Graft left, which passes everything through until
    /// Graft tells it more.
    public static bool EnsureRelay(string? source)
    {
        if (RelayKeeper.Send(new RelayRequest(), timeoutMs: 1500) is not null) return true;
        if (source is null || !File.Exists(source) || RelayKeeper.EnsureRunning(source) == RelayKeeper.Step.Failed) return false;
        for (var i = 0; i < 60; i++)
        {
            Thread.Sleep(250);
            if (RelayKeeper.Send(new RelayRequest(), timeoutMs: 1000) is not null) return true;
        }
        return false;
    }
}
