using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// Reads one transcript and pulls out what a record needs. The pass a launcher
/// runs in front of a window opening reads every transcript on the machine, so
/// this walks each line's bytes once and takes every "key":"value" pair off it
/// as it goes, rather than searching the line again for each of a dozen fields —
/// which took twelve seconds over two hundred megabytes on the Mac, against one
/// for this.
/// </summary>
public static class TranscriptParser
{
    /// Reads a transcript file. Null when no bridge line is in it: that is a
    /// session the terminal ran on its own, which was never going to be in a
    /// sidebar, so nothing is missing.
    public static SessionFacts? SessionFacts(string path, string cliSessionId)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return null; }
        return SessionFacts(bytes, cliSessionId);
    }

    private const byte Quote = (byte)'"';
    private const byte Backslash = (byte)'\\';
    private const byte Colon = (byte)':';
    private const byte Newline = (byte)'\n';

    public static SessionFacts? SessionFacts(byte[] bytes, string cliSessionId)
    {
        var bridgeIds = new List<string>();
        string ownerAccount = "", ownerOrganization = "";
        string? title = null, firstStamp = null, lastStamp = null;
        // A session that got no answer back leaves no line carrying these, so
        // they hold the last values the transcript ever mentioned rather than an
        // honest read; the record still needs something in them.
        string model = "claude-sonnet-5", effort = "medium", permissionMode = "auto", cwd = "";
        var promptIds = new HashSet<string>();
        var branches = new List<string>();

        var lineStart = 0;
        while (lineStart < bytes.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < bytes.Length && bytes[lineEnd] != Newline) lineEnd++;
            if (lineEnd <= lineStart) { lineStart = lineEnd + 1; continue; }

            string? type = null;
            var assistant = false;
            string? stamp = null, lineCwd = null, mode = null, branch = null;
            string? promptId = null, lineModel = null, lineEffort = null;
            string? bridgeId = null, account = null, organization = null, customTitle = null;

            // The one field read out of order. An answer is written
            // {"parentUuid":…,"message":{…,"type":"message",…},"type":"assistant"},
            // so the first `type` on the line belongs to the message nested
            // inside it and the line's own comes last — while every other field's
            // first value is the line's. Taking the first `type`, right for
            // everything else, quietly put every recovered session back at the
            // default model and effort. A marker line opens with its own `type`
            // and is read by that; an answer is recognised by carrying
            // `assistant` anywhere.
            foreach (var (keyStart, keyEnd, valStart, valEnd) in Pairs(bytes, lineStart, lineEnd))
            {
                var key = bytes.AsSpan(keyStart, keyEnd - keyStart);
                var value = bytes.AsSpan(valStart, valEnd - valStart);

                if (KeyIs(key, "type"))
                {
                    if (type is null) type = Text(value);
                    if (ValueIs(value, "assistant")) assistant = true;
                }
                else if (KeyIs(key, "timestamp")) First(ref stamp, value);
                else if (KeyIs(key, "cwd")) First(ref lineCwd, value);
                else if (KeyIs(key, "permissionMode")) First(ref mode, value);
                else if (KeyIs(key, "gitBranch")) First(ref branch, value);
                else if (KeyIs(key, "promptId")) First(ref promptId, value);
                else if (KeyIs(key, "model")) First(ref lineModel, value);
                else if (KeyIs(key, "effort")) First(ref lineEffort, value);
                else if (KeyIs(key, "bridgeSessionId")) First(ref bridgeId, value);
                else if (KeyIs(key, "ownerAccountUuid")) First(ref account, value);
                else if (KeyIs(key, "ownerOrganizationUuid")) First(ref organization, value);
                else if (KeyIs(key, "customTitle")) First(ref customTitle, value);
            }

            if (type == "bridge-session")
            {
                if (bridgeId is not null && !bridgeIds.Contains(bridgeId)) bridgeIds.Add(bridgeId);
                if (ownerAccount.Length == 0)
                {
                    ownerAccount = account ?? "";
                    ownerOrganization = organization ?? "";
                }
                lineStart = lineEnd + 1;
                continue;
            }
            if (type == "custom-title")
            {
                // The last one wins: a session re-named keeps the newer name.
                title = customTitle ?? title;
                lineStart = lineEnd + 1;
                continue;
            }
            if (stamp is not null)
            {
                firstStamp ??= stamp;
                lastStamp = stamp;
            }
            if (assistant)
            {
                if (lineModel is not null) model = lineModel;
                if (lineEffort is not null) effort = lineEffort;
            }
            if (lineCwd is not null) cwd = lineCwd;
            if (mode is not null) permissionMode = mode;
            if (branch is not null && !branches.Contains(branch)) branches.Add(branch);
            if (promptId is not null) promptIds.Add(promptId);

            lineStart = lineEnd + 1;
        }

        if (bridgeIds.Count == 0
            || firstStamp is null || lastStamp is null
            || EpochMilliseconds(firstStamp) is not double created
            || EpochMilliseconds(lastStamp) is not double active
            || ownerAccount.Length == 0 || ownerOrganization.Length == 0)
            return null;

        return new SessionFacts
        {
            CliSessionId = cliSessionId,
            BridgeIds = bridgeIds,
            OwnerAccount = ownerAccount,
            OwnerOrganization = ownerOrganization,
            Title = title ?? "New session",
            Cwd = cwd,
            CreatedAt = created,
            LastActivityAt = active,
            Model = model,
            Effort = effort,
            PermissionMode = permissionMode,
            Prompts = promptIds.Count,
            Branches = branches,
        };
    }

    private static void First(ref string? slot, ReadOnlySpan<byte> value)
    {
        if (slot is null) slot = Text(value);
    }

    /// Hands back every "key":"value" pair on one line as byte ranges, in the
    /// order they are written. Strings are walked the way JSON writes them,
    /// backslash escapes and all: a value that ends early would otherwise take
    /// the rest of the line with it, turning a quote pasted into a conversation
    /// into keys.
    private static IEnumerable<(int keyStart, int keyEnd, int valStart, int valEnd)>
        Pairs(byte[] bytes, int lower, int upper)
    {
        int? ClosingQuote(int i)
        {
            var j = i + 1;
            while (j < upper)
            {
                if (bytes[j] == Backslash) { j += 2; continue; }
                if (bytes[j] == Quote) return j;
                j++;
            }
            return null;
        }

        var i = lower;
        while (i < upper)
        {
            if (bytes[i] != Quote) { i++; continue; }
            if (ClosingQuote(i) is not int keyEnd) yield break;
            // A string with a colon then a quote after it is a key. Anything
            // else is a value, and what is inside a value is none of this scan's
            // business — skipping it whole is what keeps pasted text out.
            if (keyEnd + 2 >= upper || bytes[keyEnd + 1] != Colon || bytes[keyEnd + 2] != Quote)
            {
                i = keyEnd + 1;
                continue;
            }
            if (ClosingQuote(keyEnd + 2) is not int valueEnd) yield break;
            yield return (i + 1, keyEnd, keyEnd + 3, valueEnd);
            i = valueEnd + 1;
        }
    }

    private static bool KeyIs(ReadOnlySpan<byte> key, string name) => Utf8Equals(key, name);
    private static bool ValueIs(ReadOnlySpan<byte> value, string name) => Utf8Equals(value, name);

    private static bool Utf8Equals(ReadOnlySpan<byte> bytes, string ascii)
    {
        if (bytes.Length != ascii.Length) return false;
        for (var i = 0; i < ascii.Length; i++)
            if (bytes[i] != (byte)ascii[i]) return false;
        return true;
    }

    /// A pair's value as a string. The escaped case goes back through the JSON
    /// reader rather than being unpicked by hand, because a title is the one
    /// field here a person writes, and a mangled accent in a sidebar is worse
    /// than the cost of parsing a few bytes.
    private static string Text(ReadOnlySpan<byte> value)
    {
        if (value.IndexOf(Backslash) < 0)
            return System.Text.Encoding.UTF8.GetString(value);

        var quoted = new byte[value.Length + 2];
        quoted[0] = Quote;
        value.CopyTo(quoted.AsSpan(1));
        quoted[^1] = Quote;
        try
        {
            var reader = new Utf8JsonReader(quoted);
            if (reader.Read() && reader.TokenType == JsonTokenType.String)
                return reader.GetString() ?? System.Text.Encoding.UTF8.GetString(value);
        }
        catch { }
        return System.Text.Encoding.UTF8.GetString(value);
    }

    /// "2026-08-29T17:32:48.893Z" as milliseconds since the epoch. Every stamp
    /// transcripts have been seen carrying was fractional, but a whole one is
    /// still a stamp.
    private static double? EpochMilliseconds(string stamp)
    {
        if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return date.ToUnixTimeMilliseconds();
        return null;
    }
}
