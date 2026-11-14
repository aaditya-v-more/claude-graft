using System.Globalization;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

// A resumed transcript keeps the line IDs of the answers it copied. Store only
// fingerprints and byte counts in the cache, never prompts or answers.
internal sealed class ConversationTrace
{
    internal const int EditWeight = 1_000_000;
    [JsonPropertyName("prompts")] public List<string> Prompts { get; init; } = [];
    [JsonPropertyName("openingSize")] public int OpeningSize { get; set; }
    [JsonPropertyName("work")] public Dictionary<string, long> Work { get; init; } = [];

    // FNV-1a over the JSON-escaped bytes, folding actual and escaped whitespace.
    // As in GraftCore.swift, a repaste with different spacing has the same hash.
    internal static (string Hash, int Size)? Fingerprint(ReadOnlySpan<byte> value)
    {
        const ulong prime = 0x100000001b3;
        ulong hash = 0xcbf29ce484222325;
        var size = 0;
        var gap = false;
        void Mix(byte b)
        {
            if (gap && size > 0)
            {
                hash = unchecked((hash ^ (byte)' ') * prime);
                size++;
            }
            gap = false;
            hash = unchecked((hash ^ b) * prime);
            size++;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var b = value[i];
            if (b == (byte)'\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                if (next is (byte)'n' or (byte)'t' or (byte)'r') gap = true;
                else { Mix(b); Mix(next); }
            }
            else if (b is (byte)' ' or (byte)'\n' or (byte)'\t' or (byte)'\r') gap = true;
            else Mix(b);
        }
        return size == 0 ? null : (hash.ToString("x", CultureInfo.InvariantCulture), size);
    }

    internal static HashSet<string> ResumedCopies(
        IEnumerable<(SessionFacts Facts, ConversationTrace Trace)> sessions,
        IReadOnlyDictionary<string, string> forks)
    {
        const int openingAtLeast = 20;
        var copies = new HashSet<string>();
        var groups = sessions.Where(s => s.Trace.OpeningSize >= openingAtLeast && s.Trace.Prompts.Count > 0)
            .GroupBy(s => (s.Facts.OwnerAccount, s.Facts.OwnerOrganization, s.Facts.Cwd, s.Trace.Prompts[0]));
        foreach (var group in groups)
        {
            if (group.Count() < 2) continue;
            var candidates = group.Select(s => (s.Facts, s.Trace, Asked: s.Trace.Prompts.ToHashSet())).ToList();
            foreach (var copy in candidates)
            {
                var id = copy.Facts.CliSessionId;
                foreach (var other in candidates)
                {
                    var otherId = other.Facts.CliSessionId;
                    if (otherId == id || forks.GetValueOrDefault(id) == otherId || forks.GetValueOrDefault(otherId) == id)
                        continue;
                    if (!copy.Asked.IsSubsetOf(other.Asked)) continue;
                    if (copy.Asked.Count == other.Asked.Count &&
                        (other.Facts.LastActivityAt < copy.Facts.LastActivityAt ||
                         (other.Facts.LastActivityAt == copy.Facts.LastActivityAt && string.CompareOrdinal(otherId, id) <= 0)))
                        continue;
                    if (HasOwnWork(copy.Trace.Work, other.Trace.Work)) continue;
                    copies.Add(id);
                    break;
                }
            }
        }
        return copies;
    }

    // A few introductory sentences are typical of an interrupted retry. Keep a
    // unique substantive answer, and always keep a unique file edit.
    private static bool HasOwnWork(Dictionary<string, long> candidate, Dictionary<string, long> other)
    {
        long bytes = 0;
        foreach (var (answer, size) in candidate)
        {
            if (other.ContainsKey(answer)) continue;
            bytes += size;
            if (bytes > 500) return true;
        }
        return false;
    }
}
