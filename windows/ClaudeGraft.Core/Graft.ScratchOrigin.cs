using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeGraft.Core;

public static partial class Graft
{
    private static (JsonObject record, string cwd, string suffix)? ScratchRecord(byte[] data)
    {
        var record = RecordObject(data);
        if (RecordString(record, "sessionId") is null || record!.ContainsKey("sshConfig") || record.ContainsKey("wslConfig")) return null;
        var cwd = RecordString(record, "cwd");
        var origin = RecordString(record, "originCwd");
        if (cwd is null || origin is null) return null;
        string? Suffix(string path)
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GraftPaths.ProfilesRoot)) + Path.DirectorySeparatorChar;
            // Reject traversal before normalizing: only the exact scratch shape
            // belongs here, never an arbitrary path that happens to end there.
            var normalized = path.Replace('/', Path.DirectorySeparatorChar);
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var parts = normalized[prefix.Length..].Split(Path.DirectorySeparatorChar);
            if (parts.Length != 5 || parts[1] != "scratch-workspaces"
                || parts.Any(p => ValidateWindowsName(p) is not null || p is "." or "..")
                || !Regex.IsMatch(parts[4], "^scratch-[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9a-f]{6}$")) return null;
            return Path.Combine(parts[2..]);
        }
        var suffix = Suffix(cwd);
        return suffix is not null && string.Equals(suffix, Suffix(origin), StringComparison.OrdinalIgnoreCase)
            ? (record, cwd, suffix) : null;
    }

    internal static byte[] MirrorData(byte[] data)
    {
        if (ScratchRecord(data) is not { } scratch) return data;
        var (record, cwd, _) = scratch;
        record["originCwd"] = cwd;
        return RecordBytes(record);
    }

    private static byte[] LocalScratchOrigin(byte[] data, string folder)
    {
        if (ScratchRecord(data) is not { } scratch) return data;
        var (record, cwd, suffix) = scratch;
        var store = Path.GetDirectoryName(Path.GetDirectoryName(folder)!)!;
        if (!string.Equals(Path.GetFileName(store), "claude-code-sessions", StringComparison.OrdinalIgnoreCase)) return data;
        var profile = Path.GetDirectoryName(store)!;
        if (!Fs.SamePath(Path.GetDirectoryName(profile)!, GraftPaths.ProfilesRoot)) return data;
        var local = Path.Combine(profile, "scratch-workspaces", suffix);
        if (RecordString(record, "originCwd") == local) return data;
        try
        {
            if (!Fs.SamePath(Fs.Resolve(local), Fs.Resolve(cwd)))
            {
                if (!Directory.Exists(cwd) || Fs.Exists(local)) return data;
                // A junction needs no Developer Mode and is only an alias to
                // the existing local working directory. Never redirect a parent.
                var parent = Path.GetDirectoryName(local)!;
                if (!Fs.IsInside(Fs.Resolve(parent), Fs.Resolve(profile))) return data;
                Directory.CreateDirectory(parent);
                Junction.Create(local, cwd);
            }
            record["originCwd"] = local;
            return RecordBytes(record);
        }
        catch (Exception e)
        {
            Diagnostics.Note("scratch.origin-failed", new Dictionary<string, object?>
                { ["profile"] = Path.GetFileName(profile), ["reason"] = e.GetType().Name });
            return data;
        }
    }
}
