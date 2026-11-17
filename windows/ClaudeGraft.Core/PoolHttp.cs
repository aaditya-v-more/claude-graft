using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// As much HTTP/1.1 as the relay needs to find where one message ends and the
/// next begins. It never rewrites an answer, so all it has to understand about
/// one is its length.
/// </summary>
public abstract record HttpBody
{
    public sealed record None : HttpBody;
    public sealed record Length(long Count) : HttpBody;
    public sealed record Chunked : HttpBody;
    /// Only an answer ends this way; it lasts until the server hangs up.
    public sealed record UntilClose : HttpBody;

    public static readonly HttpBody Empty = new None();
}

public sealed class HttpHead
{
    /// The request or status line, split on its first two spaces.
    public IReadOnlyList<string> Line { get; }
    public IReadOnlyList<(string Name, string Value)> Fields { get; }

    private HttpHead(List<string> line, List<(string, string)> fields)
    {
        Line = line;
        Fields = fields;
    }

    /// How many bytes the head takes, blank line included, once all of it is in.
    public static int? LengthIn(ReadOnlySpan<byte> buffer)
    {
        var at = buffer.IndexOf("\r\n\r\n"u8);
        return at < 0 ? null : at + 4;
    }

    /// Latin-1 maps every byte to one character and back, so a field holding
    /// UTF-8 goes out exactly as it came in.
    public static HttpHead? Parse(ReadOnlySpan<byte> data)
    {
        var lines = Encoding.Latin1.GetString(data).Split("\r\n").ToList();
        while (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return null;
        var line = lines[0].Split(' ', 3).ToList();
        if (line.Count < 2 || line[0].Length == 0 || line[1].Length == 0) return null;
        var fields = new List<(string, string)>();
        foreach (var raw in lines.Skip(1))
        {
            if (raw.StartsWith(' ') || raw.StartsWith('\t'))
            {
                if (fields.Count == 0) return null;
                fields[^1] = (fields[^1].Item1, fields[^1].Item2 + " " + raw.Trim());
                continue;
            }
            var colon = raw.IndexOf(':');
            if (colon <= 0) return null;
            var name = raw[..colon];
            if (name.Contains(' ') || name.Contains('\t')) return null;
            fields.Add((name, raw[(colon + 1)..].Trim(' ', '\t')));
        }
        return new HttpHead(line, fields);
    }

    public string Method => Line[0];
    public string Target => Line[1];
    public int? Status => int.TryParse(Line[1], out var s) ? s : null;
    public string Version => Line[0].StartsWith("HTTP/") ? Line[0] : Line.Count > 2 ? Line[2] : "";

    public string? Value(string name) =>
        Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// Every comma-separated token of a field, across however many lines it
    /// was sent on, lowercased.
    public IReadOnlyList<string> Tokens(string name) =>
        Fields.Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.Value.Split(','))
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

    /// A server honours Upgrade only when Connection names it, so both count.
    public bool AsksToUpgrade => Value("upgrade") is not null && Tokens("connection").Contains("upgrade");

    public bool KeepsAlive
    {
        get
        {
            var connection = Tokens("connection");
            return Version == "HTTP/1.0" ? connection.Contains("keep-alive") : !connection.Contains("close");
        }
    }

    /// Null when the request cannot be framed safely: a body read to the wrong
    /// length takes the next request with it, so that is refused, not guessed.
    public HttpBody? RequestBody
    {
        get
        {
            if (Value("transfer-encoding") is not null)
                return Tokens("transfer-encoding").LastOrDefault() == "chunked" ? new HttpBody.Chunked() : null;
            var lengths = Fields.Where(f => f.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Value).Distinct().ToList();
            if (lengths.Count > 1) return null;
            if (lengths.Count == 0) return HttpBody.Empty;
            if (!long.TryParse(lengths[0], out var count) || count < 0) return null;
            return count == 0 ? HttpBody.Empty : new HttpBody.Length(count);
        }
    }

    public HttpBody ResponseBody(string method)
    {
        if (Status is not int status) return new HttpBody.UntilClose();
        if (method == "HEAD" || status is >= 100 and < 200 || status == 204 || status == 304) return HttpBody.Empty;
        if (Value("transfer-encoding") is not null)
            return Tokens("transfer-encoding").LastOrDefault() == "chunked" ? new HttpBody.Chunked() : new HttpBody.UntilClose();
        if (long.TryParse(Value("content-length"), out var length) && length >= 0)
            return length == 0 ? HttpBody.Empty : new HttpBody.Length(length);
        return new HttpBody.UntilClose();
    }
}

/// <summary>
/// Follows a chunked body through however many reads it arrives in: for an
/// answer only where it ends, for a request its payload as well.
/// </summary>
public sealed class ChunkedFraming
{
    private enum State { Size, Data, DataEnd, Trailer, Done }
    private State _state = State.Size;
    private long _remaining;
    private readonly List<byte> _line = new();

    public bool IsDone => _state == State.Done;

    /// Takes the bytes of input that belong to this body and returns how many
    /// that was; anything after the end belongs to the next message.
    public int Consume(ReadOnlySpan<byte> input, List<byte>? payload)
    {
        var index = 0;
        while (index < input.Length && _state != State.Done)
        {
            if (_state == State.Data)
            {
                var count = (int)Math.Min(_remaining, input.Length - index);
                if (payload is not null) foreach (var b in input.Slice(index, count)) payload.Add(b);
                index += count;
                _remaining -= count;
                if (_remaining == 0) _state = State.DataEnd;
                continue;
            }
            var feed = input[index..].IndexOf((byte)'\n');
            if (feed < 0)
            {
                foreach (var b in input[index..]) _line.Add(b);
                if (_line.Count > 16384) throw new FormatException("chunk line too long");
                return input.Length;
            }
            foreach (var b in input.Slice(index, feed)) _line.Add(b);
            index += feed + 1;
            if (_line.Count > 0 && _line[^1] == '\r') _line.RemoveAt(_line.Count - 1);
            var text = Encoding.ASCII.GetString(_line.ToArray());
            _line.Clear();
            switch (_state)
            {
                case State.Size:
                    var digits = text.Split(';', 2)[0].Trim();
                    if (!long.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var size)
                        || size < 0 || size >= 1L << 40) throw new FormatException("bad chunk size");
                    if (size == 0) _state = State.Trailer;
                    else { _state = State.Data; _remaining = size; }
                    break;
                case State.DataEnd:
                    if (text.Length != 0) throw new FormatException("bad chunk end");
                    _state = State.Size;
                    break;
                default:
                    if (text.Length == 0) _state = State.Done;
                    break;
            }
        }
        return index;
    }
}

public static class HttpMessage
{
    /// Fields that describe one hop rather than the request, and the ones the
    /// relay writes for itself.
    private static readonly HashSet<string> HopFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-connection", "proxy-authorization", "proxy-authenticate",
        "te", "trailer", "transfer-encoding", "upgrade", "expect", "content-length",
    };

    /// The request as it goes upstream: body whole with its length, every
    /// end-to-end field in Claude Code's order, Authorization replaced only
    /// when another account answers. plainAnswer asks for an uncompressed one,
    /// for a refusal the relay must read: the service compresses even errors.
    public static byte[] Request(HttpHead head, byte[] body, string? authorization, bool plainAnswer = false)
    {
        var named = head.Tokens("connection").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder($"{head.Method} {head.Target} HTTP/1.1\r\n");
        var sawHost = false;
        foreach (var (name, value) in head.Fields)
        {
            if (HopFields.Contains(name) || named.Contains(name)) continue;
            if (authorization is not null && name.Equals("authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (plainAnswer && name.Equals("accept-encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase)) sawHost = true;
            text.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        if (!sawHost) text.Append("Host: ").Append(UsagePool.Host).Append("\r\n");
        if (authorization is not null) text.Append("Authorization: ").Append(authorization).Append("\r\n");
        if (plainAnswer) text.Append("Accept-Encoding: identity\r\n");
        if (body.Length > 0 || head.Method is "POST" or "PUT" or "PATCH") text.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        text.Append("\r\n");
        return [.. Encoding.Latin1.GetBytes(text.ToString()), .. body];
    }

    /// Shaped like the service's own errors, so Claude Code shows the sentence
    /// rather than a parse failure.
    public static byte[] Failure(int status, string reason, string message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = new { type = "api_error", message } });
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        return [.. Encoding.ASCII.GetBytes(head), .. body];
    }
}
