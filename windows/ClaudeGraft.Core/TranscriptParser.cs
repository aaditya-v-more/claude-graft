using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// What a session asked and what it answered, in the shape
/// <see cref="Graft.ResumedCopies"/> compares. Kept beside every transcript in
/// the cache, so it holds fingerprints and sizes, never anything anybody wrote.
/// </summary>
public sealed class ConversationTrace
{
    /// What a person typed, in order: a user line with a promptId that answers
    /// no tool and is not isMeta.
    public List<string> Prompts { get; set; } = new();
    /// How much the opening prompt says. A "hi" opens a hundred unrelated chats
    /// and identifies none of them.
    public int OpeningSize { get; set; }
    /// Every answer that produced something, by its line's own id. A resumed
    /// copy keeps the ids of everything it copied, so what a session did that
    /// another did not is exactly the ids the other lacks.
    public Dictionary<string, int> Work { get; set; } = new();

    /// An edit is work however short its explanation.
    public const int EditWeight = 1_000_000;
}

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
    public static SessionFacts? SessionFacts(string path, string cliSessionId) => Read(path, cliSessionId).facts;

    public static (SessionFacts? facts, ConversationTrace trace) Read(string path, string cliSessionId)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return (null, new ConversationTrace()); }
        return Read(bytes, cliSessionId);
    }

    private const byte Quote = (byte)'"';
    private const byte Backslash = (byte)'\\';
    private const byte Colon = (byte)':';
    private const byte Newline = (byte)'\n';

    public static SessionFacts? SessionFacts(byte[] bytes, string cliSessionId) => Read(bytes, cliSessionId).facts;

    public static (SessionFacts? facts, ConversationTrace trace) Read(byte[] bytes, string cliSessionId)
    {
        var trace = new ConversationTrace();
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
            (int start, int end)? content = null, textBlock = null, lineId = null;
            var answersTool = false;
            // An answer's own `type` comes last, after the text it carries, so
            // what it produced is counted on every line and kept only once the
            // line turns out to have been an answer.
            var produced = 0;
            var edited = false;

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
                // Taken as byte ranges and only on a prompt's own line: an answer
                // carries megabytes in these keys, and decoding it is what would
                // make a pass slow again.
                else if (KeyIs(key, "content"))
                {
                    if (type == "user" && !answersTool && content is null) content = (valStart, valEnd);
                }
                else if (KeyIs(key, "text"))
                {
                    produced += valEnd - valStart;
                    if (type == "user" && !answersTool && textBlock is null) textBlock = (valStart, valEnd);
                }
                else if (KeyIs(key, "tool_use_id")) answersTool = true;
                // The line's own id is its last; a tool input nested earlier may carry one.
                else if (KeyIs(key, "uuid")) lineId = (valStart, valEnd);
                else if (KeyIs(key, "name"))
                {
                    if (ValueIs(value, "Edit") || ValueIs(value, "Write") || ValueIs(value, "MultiEdit")
                        || ValueIs(value, "NotebookEdit")) edited = true;
                }
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

            if (type == "user" && promptId is not null && !answersTool
                && !LineContains(bytes, lineStart, lineEnd, "\"isMeta\":true")
                && (content ?? textBlock) is var (wordsStart, wordsEnd)
                && !StartsWith(bytes.AsSpan(wordsStart, wordsEnd - wordsStart), "[Request interrupted by user")
                && Fingerprint(bytes.AsSpan(wordsStart, wordsEnd - wordsStart)) is var (print, size))
            {
                if (trace.Prompts.Count == 0) trace.OpeningSize = size;
                trace.Prompts.Add(print);
            }
            // An overloaded or failed request is written as an answer too, and
            // produced nothing anybody would go back for.
            if (assistant && lineId is var (idStart, idEnd) && (produced > 0 || edited)
                && !LineContains(bytes, lineStart, lineEnd, "\"isApiErrorMessage\":true")
                && Fingerprint(bytes.AsSpan(idStart, idEnd - idStart)) is var (id, _))
            {
                trace.Work[id] = trace.Work.GetValueOrDefault(id) + produced + (edited ? ConversationTrace.EditWeight : 0);
            }

            lineStart = lineEnd + 1;
        }

        if (bridgeIds.Count == 0
            || firstStamp is null || lastStamp is null
            || EpochMilliseconds(firstStamp) is not double created
            || EpochMilliseconds(lastStamp) is not double active
            || ownerAccount.Length == 0 || ownerOrganization.Length == 0)
            return (null, trace);

        return (new SessionFacts
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
        }, trace);
    }

    /// FNV-1a over a value still as JSON wrote it, whitespace folded. Decoding
    /// first is what a compaction summary or a pasted log would otherwise cost;
    /// one writer escapes one text one way, so the escaped bytes identify it as
    /// well as the words, and only the whitespace escapes are folded.
    internal static (string print, int size)? Fingerprint(ReadOnlySpan<byte> value)
    {
        ulong hash = 0xcbf29ce484222325;
        bool wrote = false, gap = false;
        var size = 0;
        void Mix(byte b)
        {
            if (gap && wrote) { hash ^= (byte)' '; hash *= 0x100000001b3; size++; }
            gap = false;
            wrote = true;
            size++;
            hash ^= b;
            hash *= 0x100000001b3;
        }
        var i = 0;
        while (i < value.Length)
        {
            var b = value[i];
            if (b == Backslash && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next is (byte)'n' or (byte)'t' or (byte)'r') gap = true;
                else { Mix(b); Mix(next); }
                i += 2;
                continue;
            }
            if (b is 0x20 or 0x0a or 0x09 or 0x0d) gap = true; else Mix(b);
            i++;
        }
        return wrote ? (hash.ToString("x"), size) : null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, string ascii)
    {
        if (value.Length < ascii.Length) return false;
        for (var i = 0; i < ascii.Length; i++)
            if (value[i] != (byte)ascii[i]) return false;
        return true;
    }

    private static bool LineContains(byte[] bytes, int lower, int upper, string needle) =>
        bytes.AsSpan(lower, upper - lower).IndexOf(System.Text.Encoding.ASCII.GetBytes(needle)) >= 0;

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
