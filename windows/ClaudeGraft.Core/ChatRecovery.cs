using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public static class ChatRecovery
{
    public sealed record Preview(string Title, DateTimeOffset LastActive);
    public sealed record Offer(string Source, string Account, int Count, bool HasOwnChats, Preview[] Recent);
    private sealed record Entry(string Store, string Org, string File)
    {
        public string Name => Path.GetFileName(File);
        public string Key => Store + "/" + Name;
        public bool IsRecord => Name.StartsWith("local_", StringComparison.Ordinal);
    }

    public static Offer? Find(string profile)
    {
        using var stateLock = StateLock.Acquire();
        var account = OwnAccount(profile);
        if (account is null) return null;
        var own = Entries(profile, account).Where(e => e.IsRecord).Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Offer? best = null;
        var candidates = Graft.SessionStoreProfiles().Concat(Graft.ShortcutProfiles()).Append(GraftPaths.DefaultProfile)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var source in candidates)
        {
            if (Fs.SamePath(source, profile) || Junction.IsLink(source)) continue;
            var missing = Missing(source, profile, account, own).Where(e => e.IsRecord).ToList();
            if (missing.Count <= (best?.Count ?? 0)) continue;
            var recent = missing.OrderByDescending(e => File.GetLastWriteTimeUtc(e.File)).Take(20)
                .Select(ReadPreview).OfType<Preview>().OrderByDescending(p => p.LastActive).Take(5).ToArray();
            best = new(source, account, missing.Count, own.Count != 0, recent);
        }
        return best;
    }

    public static int Copy(string profile, Offer offer)
    {
        using var stateLock = StateLock.Acquire();
        Graft.ValidateProfilePath(offer.Source, allowDefault: true);
        if (Fs.SamePath(offer.Source, profile) || OwnAccount(profile) != offer.Account)
            throw new IOException("The account or chat source has changed. Review the profile again.");
        RequireClaudeClosed();
        var owned = Entries(profile, offer.Account).Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = Missing(offer.Source, profile, offer.Account, owned).OrderBy(e => e.IsRecord).ToList();
        // Install deletion markers first. If a copy stops part-way, a retry
        // can still find the missing records and cannot reintroduce a deletion.
        if (!entries.Any(e => e.IsRecord)) return 0;
        RequireClaudeClosed();
        var copied = 0;
        foreach (var entry in entries)
        {
            var destination = Destination(profile, offer.Account, entry);
            if (destination is null || Fs.Exists(destination)) continue;
            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".graft-copy-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(entry.File, temporary);
                if (entry.IsRecord)
                {
                    using var record = JsonDocument.Parse(File.ReadAllBytes(temporary));
                    if (record.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Invalid chat record.");
                }
                File.Move(temporary, destination, overwrite: false);
                if (entry.IsRecord) copied++;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Diagnostics.Note("adopt", new Dictionary<string, object?> { ["from"] = Path.GetFileName(offer.Source),
            ["into"] = Path.GetFileName(profile), ["copied"] = copied });
        return copied;
    }

    private static void RequireClaudeClosed()
    {
        if (ClaudeProcesses.Enumerate().Any(p => ClaudeProcesses.IsClaudeDesktop(p.command)))
            throw new IOException("Quit every Claude Desktop instance before copying its chat records, then try again.");
    }

    private static string? OwnAccount(string profile)
    {
        Graft.ValidateProfilePath(profile);
        if (Junction.IsLink(Path.Combine(profile, "config.json"))) return null;
        var config = Graft.ReadableConfigJson(profile);
        if (config?["lastKnownAccountUuid"] is not JsonValue value ||
            !value.TryGetValue<string>(out var account) || !SafeName(account)) return null;
        var store = new ShortcutStore();
        if (store.LoadError is not null || store.Shortcuts.Any(s => Fs.SamePath(s.ProfileDir, profile) && s.Source.Kind != SourceKind.Own)
            || Graft.MirrorPairsBorrowedBy(profile).Count != 0) return null;
        return account;
    }

    private static bool SafeName(string name) => Graft.ValidateWindowsName(name) is null &&
        !name.StartsWith('.') && !name.EndsWith(Graft.StashSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool OwnDirectory(string profile, string directory) =>
        !Junction.IsLink(directory) && !Graft.IsStashedAway(directory) &&
        Fs.SamePath(Fs.Resolve(directory), Path.Combine(Fs.Resolve(profile), Path.GetRelativePath(profile, directory)));

    private static IEnumerable<Entry> Entries(string profile, string account)
    {
        foreach (var storeName in GraftPaths.ChatStoreNames)
        {
            var store = Path.Combine(profile, storeName);
            if (!OwnDirectory(profile, store) || Graft.CounterpartDirectory(store, account) is not string accountName) continue;
            var under = Path.Combine(store, accountName);
            if (!OwnDirectory(profile, under)) continue;
            foreach (var org in Directory.EnumerateDirectories(under).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!SafeName(Path.GetFileName(org)) || !OwnDirectory(profile, org)) continue;
                foreach (var file in Directory.EnumerateFiles(org))
                    if (Graft.IsMirrored(Path.GetFileName(file)) && !Junction.IsLink(file))
                        yield return new(storeName, Path.GetFileName(org), file);
            }
        }
    }

    private static IEnumerable<Entry> Missing(string source, string profile, string account, ISet<string> owned)
    {
        // A merged directory can hold another account's borrowed records under
        // this account's name. Only a profile's own independent history counts.
        if (Graft.MirrorPairsBorrowedBy(source).Count != 0) return [];
        return Entries(source, account).Where(e => !owned.Contains(e.Key) && Destination(profile, account, e) is not null)
            .DistinctBy(e => e.Key, StringComparer.OrdinalIgnoreCase);
    }

    private static string? Destination(string profile, string account, Entry entry)
    {
        var store = Path.Combine(profile, entry.Store);
        if (!OwnDirectory(profile, store)) return null;
        var accountDir = Path.Combine(store, Graft.CounterpartDirectory(store, account) ?? account);
        if (!OwnDirectory(profile, accountDir)) return null;
        var org = Path.Combine(accountDir, Graft.CounterpartDirectory(accountDir, entry.Org) ?? entry.Org);
        if (!OwnDirectory(profile, org)) return null;
        var file = Path.Combine(org, entry.Name);
        return Junction.IsLink(file) ? null : file;
    }

    private static Preview? ReadPreview(Entry entry)
    {
        try
        {
            using var record = JsonDocument.Parse(File.ReadAllBytes(entry.File));
            var root = record.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("title", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 } title) return null;
            var time = root.TryGetProperty("lastActivityAt", out var stamp) && stamp.ValueKind == JsonValueKind.Number &&
                       stamp.TryGetInt64(out var milliseconds) ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : File.GetLastWriteTimeUtc(entry.File);
            return new(title, time);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException) { return null; }
    }
}
