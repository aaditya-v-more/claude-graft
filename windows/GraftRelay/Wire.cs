using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using ClaudeGraft.Core;

namespace ClaudeGraft.Relay;

public class RelayException(string message) : IOException(message);

/// <summary>
/// One connection read into a buffer. Every send is awaited before the next
/// read, so a slow reader on one side slows the other rather than filling memory.
/// </summary>
public sealed class Wire : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private byte[] _buffer = new byte[64 * 1024];
    private int _start, _end;
    private bool _closed;

    public Wire(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
    }

    public bool Ended { get; private set; }
    /// Bytes ever received, so a reused connection that died before answering
    /// can be told from one that died partway through an answer.
    public long Received { get; private set; }
    public int Buffered => _end - _start;
    public ReadOnlySpan<byte> Buffer => _buffer.AsSpan(_start, Buffered);

    /// False once the other side has finished sending.
    public async Task<bool> Fill()
    {
        if (Ended) return false;
        if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                System.Buffer.BlockCopy(_buffer, _start, _buffer, 0, Buffered);
                _end -= _start;
                _start = 0;
            }
            if (_end == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        }
        var n = await _stream.ReadAsync(_buffer.AsMemory(_end, Math.Min(256 * 1024, _buffer.Length - _end)));
        if (n == 0)
        {
            Ended = true;
            return false;
        }
        _end += n;
        Received += n;
        return true;
    }

    public byte[] Take(int count)
    {
        count = Math.Min(count, Buffered);
        var taken = _buffer.AsSpan(_start, count).ToArray();
        _start += count;
        if (_start == _end) _start = _end = 0;
        return taken;
    }

    public byte[] TakeAll() => Take(Buffered);

    /// Null on a clean end before a byte of a new head.
    public async Task<(HttpHead Head, byte[] Raw)?> ReadHead(int limit)
    {
        while (true)
        {
            if (HttpHead.LengthIn(Buffer) is int length)
            {
                var raw = Take(length);
                return HttpHead.Parse(raw) is { } head ? (head, raw) : throw new RelayException("malformed head");
            }
            if (Buffered > limit) throw new RelayException("head too large");
            if (!await Fill())
            {
                if (Buffered == 0) return null;
                throw new RelayException("truncated head");
            }
        }
    }

    /// A request is read whole, because it may have to be sent twice.
    public async Task<byte[]> ReadBody(HttpBody framing, long limit)
    {
        switch (framing)
        {
            case HttpBody.Length l:
                if (l.Count > limit) throw new TooLarge();
                while (Buffered < l.Count)
                    if (!await Fill()) throw new RelayException("truncated body");
                return Take((int)l.Count);
            case HttpBody.Chunked:
                var chunked = new ChunkedFraming();
                var body = new List<byte>();
                while (true)
                {
                    if (Buffered > 0) Take(chunked.Consume(Buffer, body));
                    if (chunked.IsDone) return body.ToArray();
                    if (body.Count > limit) throw new TooLarge();
                    if (!await Fill()) throw new RelayException("truncated body");
                }
            case HttpBody.UntilClose:
                throw new RelayException("request with no length");
            default:
                return [];
        }
    }

    public async Task Send(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty) return;
        await _stream.WriteAsync(data);
        await _stream.FlushAsync();
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _stream.Dispose(); } catch { }
        try { _owner?.Dispose(); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    public sealed class TooLarge() : RelayException("too large");

    /// Bytes both ways until either side is done. Half-closing is not offered
    /// by every stream here, so one side ending ends the pair.
    public static async Task Splice(Wire a, Wire b)
    {
        static async Task Pump(Wire from, Wire to)
        {
            try
            {
                if (from.Buffered > 0) await to.Send(from.TakeAll());
                while (await from.Fill()) await to.Send(from.TakeAll());
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
            from.Close();
            to.Close();
        }
        await Task.WhenAll(Pump(a, b), Pump(b, a));
    }
}

/// <summary>
/// Where requests for the service go. Only a test can point it at a plain
/// loopback port, so the relay never speaks to the real service without TLS.
/// </summary>
public sealed class Upstream(int? loopbackPort)
{
    public async Task<Wire> Connect()
    {
        var tcp = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (loopbackPort is int port)
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return new Wire(tcp.GetStream(), tcp);
            }
            await tcp.ConnectAsync(UsagePool.Host, 443, timeout.Token);
            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = UsagePool.Host,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
            }, timeout.Token);
            return new Wire(tls, tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }
}
