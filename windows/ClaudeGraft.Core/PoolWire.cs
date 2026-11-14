using System.Globalization;
using System.Text;

namespace ClaudeGraft.Core;

public enum PoolBodyKind { None, Length, Chunked, UntilClose }
public sealed record PoolBody(PoolBodyKind Kind, long Length = 0);
public sealed record PoolHead(string[] Line, KeyValuePair<string, string>[] Fields, byte[] Raw)
{
    public string Method => Line[0];
    public string Target => Line[1];
    public int Status => int.TryParse(Line[1], out var status) ? status : 502;
    public string Version => Line[0].StartsWith("HTTP/", StringComparison.Ordinal) ? Line[0] : Line.Length == 3 ? Line[2] : "";
    public string? Value(string name) => Fields.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public string[] Tokens(string name) => Fields.Where(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
        .SelectMany(x => x.Value.Split(',')).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();
    public bool Upgrade => Value("upgrade") is not null && Tokens("connection").Contains("upgrade");
    public bool KeepAlive => Version == "HTTP/1.0" ? Tokens("connection").Contains("keep-alive") : !Tokens("connection").Contains("close");
    public PoolBody RequestBody()
    {
        var lengths = Fields.Where(x => x.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).Distinct().ToArray();
        if (Value("transfer-encoding") is not null)
        {
            if (lengths.Length != 0 || Tokens("transfer-encoding").LastOrDefault() != "chunked") throw new IOException("Invalid HTTP framing.");
            return new(PoolBodyKind.Chunked);
        }
        if (lengths.Length > 1 || lengths.Length == 1 && (!long.TryParse(lengths[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0))
            throw new IOException("Invalid HTTP length.");
        return lengths.Length == 0 ? new(PoolBodyKind.None) : new(PoolBodyKind.Length, long.Parse(lengths[0], CultureInfo.InvariantCulture));
    }
    public PoolBody ResponseBody(string method)
    {
        if (method == "HEAD" || Status is >= 100 and < 200 or 204 or 304) return new(PoolBodyKind.None);
        if (Value("transfer-encoding") is not null) return new(Tokens("transfer-encoding").LastOrDefault() == "chunked" ? PoolBodyKind.Chunked : PoolBodyKind.UntilClose);
        return long.TryParse(Value("content-length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length >= 0
            ? new(PoolBodyKind.Length, length) : new(PoolBodyKind.UntilClose);
    }
    public static PoolHead Parse(byte[] raw)
    {
        var lines = Encoding.Latin1.GetString(raw).Split("\r\n");
        var first = lines[0].Split(' ', 3);
        if (first.Length != 3 || first.Any(x => x.Length == 0)) throw new IOException("Invalid HTTP line.");
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(c => !(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))) throw new IOException("Invalid HTTP field.");
            fields.Add(new(line[..colon], line[(colon + 1)..].Trim()));
        }
        return new(first, fields.ToArray(), raw);
    }
    public byte[] Request(byte[] body, string? token, bool plainAnswer)
    {
        string[] hop = ["connection", "keep-alive", "proxy-connection", "proxy-authorization", "proxy-authenticate", "te", "trailer", "transfer-encoding", "upgrade", "expect", "content-length"];
        var excluded = hop.Concat(Tokens("connection")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder($"{Method} {Target} HTTP/1.1\r\n"); var host = false;
        foreach (var pair in Fields)
        {
            if (excluded.Contains(pair.Key) || token is not null && pair.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                || plainAnswer && pair.Key.Equals("accept-encoding", StringComparison.OrdinalIgnoreCase)) continue;
            host |= pair.Key.Equals("host", StringComparison.OrdinalIgnoreCase);
            output.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
        }
        if (!host) output.Append("Host: ").Append(UsagePool.Host).Append("\r\n");
        if (token is not null) output.Append("Authorization: Bearer ").Append(token).Append("\r\n");
        if (plainAnswer) output.Append("Accept-Encoding: identity\r\n");
        output.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
        return [.. Encoding.Latin1.GetBytes(output.ToString()), .. body];
    }
}

/// <summary>Buffered HTTP framing without transforming streamed response bytes or consuming the next message.</summary>
public sealed class PoolWire(Stream stream) : IDisposable
{
    private readonly byte[] buffer = new byte[65536];
    private int offset, count;
    public Stream Stream { get; } = stream;
    public long Received { get; private set; }
    public bool HasBuffered => count > offset;
    private async Task<bool> Fill(CancellationToken cancellation)
    {
        if (HasBuffered) return true;
        offset = 0; count = await Stream.ReadAsync(buffer, cancellation); Received += count; return count > 0;
    }
    public async Task<byte[]?> LineAsync(int maximum, CancellationToken cancellation)
    {
        using var line = new MemoryStream();
        while (await Fill(cancellation))
        {
            var next = buffer[offset++]; line.WriteByte(next);
            if (line.Length > maximum) throw new IOException("HTTP line too large.");
            if (next == 10)
            {
                var bytes = line.ToArray();
                if (bytes.Length < 2 || bytes[^2] != 13) throw new IOException("Invalid HTTP line ending.");
                return bytes;
            }
        }
        if (line.Length > 0) throw new EndOfStreamException();
        return null;
    }
    public async Task<PoolHead?> HeadAsync(CancellationToken cancellation)
    {
        using var bytes = new MemoryStream();
        while (true)
        {
            var line = await LineAsync(65536, cancellation);
            if (line is null) { if (bytes.Length == 0) return null; throw new EndOfStreamException(); }
            bytes.Write(line);
            if (bytes.Length > 65536) throw new IOException("HTTP headers too large.");
            if (line.Length == 2) return PoolHead.Parse(bytes.ToArray());
        }
    }
    public async Task<PoolHead> FinalHeadAsync(CancellationToken cancellation)
    {
        while (true)
        {
            var head = await HeadAsync(cancellation) ?? throw new EndOfStreamException();
            if (head.Status is < 100 or >= 200 or 101) return head;
        }
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation) => Stream.WriteAsync(bytes, cancellation);
    private async Task CopyExact(long length, Stream target, CancellationToken cancellation)
    {
        while (length > 0)
        {
            if (!await Fill(cancellation)) throw new EndOfStreamException();
            var amount = (int)Math.Min(length, count - offset);
            await target.WriteAsync(buffer.AsMemory(offset, amount), cancellation); offset += amount; length -= amount;
        }
    }
    public async Task CopyBodyAsync(PoolBody framing, Stream target, bool decoded, long limit, CancellationToken cancellation)
    {
        if (framing.Kind == PoolBodyKind.None) return;
        if (framing.Kind == PoolBodyKind.Length)
        {
            if (framing.Length > limit) throw new IOException("HTTP body too large.");
            await CopyExact(framing.Length, target, cancellation); return;
        }
        if (framing.Kind == PoolBodyKind.UntilClose)
        {
            long received = 0;
            while (await Fill(cancellation))
            {
                received += count - offset; if (received > limit) throw new IOException("HTTP body too large.");
                await target.WriteAsync(buffer.AsMemory(offset, count - offset), cancellation); offset = count;
            }
            return;
        }
        long total = 0;
        while (true)
        {
            var line = await LineAsync(16384, cancellation) ?? throw new EndOfStreamException();
            var digits = Encoding.ASCII.GetString(line)[..^2].Split(';', 2)[0].Trim();
            if (!long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length) || length < 0 || length > limit - total) throw new IOException("Invalid HTTP chunk.");
            total += length;
            if (!decoded) await target.WriteAsync(line, cancellation);
            if (length == 0)
            {
                var trailers = 0;
                do
                {
                    line = await LineAsync(16384, cancellation) ?? throw new EndOfStreamException(); trailers += line.Length;
                    if (trailers > 65536) throw new IOException("HTTP trailers too large.");
                    if (!decoded) await target.WriteAsync(line, cancellation);
                } while (line.Length != 2);
                return;
            }
            await CopyExact(length, target, cancellation);
            var end = await LineAsync(2, cancellation) ?? throw new EndOfStreamException();
            if (!decoded) await target.WriteAsync(end, cancellation);
        }
    }
    public async Task<byte[]> BodyAsync(PoolBody framing, long limit, CancellationToken cancellation)
    {
        using var body = new MemoryStream(); await CopyBodyAsync(framing, body, true, limit, cancellation); return body.ToArray();
    }
    public async Task CopyRemainingAsync(Stream target, CancellationToken cancellation)
    {
        if (HasBuffered) { await target.WriteAsync(buffer.AsMemory(offset, count - offset), cancellation); offset = count; }
        await Stream.CopyToAsync(target, cancellation);
    }
    public void Dispose() => Stream.Dispose();
}
