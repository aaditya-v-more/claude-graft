using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClaudeGraft.Core;

namespace ClaudeGraft.Relay;

/// <summary>
/// Shared usage's relay: a proxy on loopback that Claude Code is pointed at
/// through its own environment. A process of its own so it goes on answering
/// while Graft is quit, since a Claude told to use a proxy that is not there
/// cannot reach the service at all.
/// </summary>
public sealed class Relay
{
    public sealed record Options
    {
        public int Port { get; init; } = UsagePool.Port;
        public string Pipe { get; init; } = UsagePool.PipeName;
        public string KeyName { get; init; } = UsagePool.KeyName;
        /// A test's stand-in for the service, spoken to in plain TCP.
        public int? UpstreamLoopbackPort { get; init; }
        /// How often the relay looks for Graft's install; a test shortens it.
        public TimeSpan RemovalCheck { get; init; } = TimeSpan.FromMinutes(1);
    }

    private readonly Options _options;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly string _binary = RelayKeeper.Digest(Environment.ProcessPath ?? "");
    private readonly CancellationTokenSource _stop = new();
    private FileStream? _lock;
    private TcpListener? _front;
    private X509Certificate2? _identity;
    private readonly PoolState _state = new();
    private Upstream _upstream = null!;
    private int _quitting;
    private int _missing;
    private PoolEnvironment.Journal? _remembered;

    public Relay(Options options)
    {
        _options = options;
        _upstream = new Upstream(options.UpstreamLoopbackPort);
    }

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(UsagePool.Directory);
        // Settled before the certificate: a second copy that renewed it on its
        // way to finding the lock taken would leave the running relay serving a
        // certificate from an authority new sessions no longer trust.
        try { _lock = new FileStream(UsagePool.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            Diagnostics.Note("relay.already-running");
            return 0;
        }
        try { _identity = RelayCertificates.Identity(_options.KeyName); }
        catch (Exception e) { return Fail("certificate", e); }

        // Just after a handover the old relay's port can take a moment to free.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _front = new TcpListener(IPAddress.Loopback, _options.Port) { ExclusiveAddressUse = true };
                _front.Start();
                break;
            }
            catch (SocketException e) when (attempt < 20)
            {
                _front = null;
                if (attempt == 19) return Fail("listener", e);
                await Task.Delay(250);
            }
            catch (SocketException e) { return Fail("listener", e); }
        }
        Diagnostics.Note("relay.started", new Dictionary<string, object?> { ["port"] = _options.Port, ["binary"] = _binary[..Math.Min(12, _binary.Length)] });

        var control = Task.Run(ControlLoop);
        _ = Task.Run(WatchForRemoval);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _front.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Front(client));
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { return Fail("accept", e); }
        await WaitOutInFlight();
        return 0;
    }

    private int Fail(string stage, Exception e)
    {
        Diagnostics.Note("relay.could-not-start", new Dictionary<string, object?> { ["stage"] = stage, ["error"] = e.Message });
        return 1;
    }

    // MARK: - Control

    private async Task ControlLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(_options.Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException e)
            {
                Diagnostics.Note("relay.control-failed", new Dictionary<string, object?> { ["error"] = e.Message });
                await Task.Delay(1000);
                continue;
            }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) return;
            var command = JsonSerializer.Deserialize<RelayRequest>(line, RelayWire.Json) ?? new RelayRequest();
            if (command.Configure is { } configuration) _state.Configure(configuration);
            if (command.Quit == true) await StepAside();
            var reply = JsonSerializer.Serialize(Status(), RelayWire.Json) + "\n";
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException) { }
    }

    private RelayStatus Status() => _state.Status(_binary, Environment.ProcessId, _started);

    /// The relay outlives Graft, so it is the one left to notice Graft has gone.
    /// It runs from a copy, so what it watches is the install's relay the
    /// journal names; missing at two looks a minute apart, which an update
    /// never is, it stops lending, puts each closed Claude back as it was, and
    /// leaves once none still points here, taking its key with it.
    private async Task WatchForRemoval()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await Task.Delay(_options.RemovalCheck, _stop.Token); }
            catch (OperationCanceledException) { return; }
            if (PoolEnvironment.JournalOnDisk() is { } journal) _remembered = journal;
            if (_remembered?.Relay is not string source || File.Exists(source))
            {
                _missing = 0;
                continue;
            }
            if (++_missing < 2) continue;
            if (_missing == 2) _state.Configure(new PoolConfiguration { Enabled = false });
            var left = PoolEnvironment.TakeOutLines(_remembered, ClaudeProcesses.IsRunning);
            if (left.Count > 0 || _state.InFlight > 0) continue;
            try { RelayCertificates.DeleteKey(_options.KeyName); } catch { }
            await StepAside();
            return;
        }
    }

    /// Stops listening and lets go of the lock before answering, so the relay
    /// replacing this one can start the moment Graft hears back.
    private Task StepAside()
    {
        if (Interlocked.Exchange(ref _quitting, 1) == 1) return Task.CompletedTask;
        _front?.Stop();
        _lock?.Dispose();
        _lock = null;
        Diagnostics.Note("relay.stepping-aside");
        _stop.Cancel();
        return Task.CompletedTask;
    }

    /// An exchange under way finishes, up to a quarter of an hour; a tunnel
    /// that outlasts that (a socket Remote Control holds open) reconnects to
    /// the new relay as it would after any dropped network.
    private async Task WaitOutInFlight()
    {
        var deadline = DateTime.UtcNow.AddMinutes(15);
        await Task.Delay(500);
        while (_state.InFlight > 0 && DateTime.UtcNow < deadline) await Task.Delay(1000);
    }

    // MARK: - The front door

    private async Task Front(TcpClient client)
    {
        using var _ = client;
        var inbound = client.GetStream();
        try
        {
            var (head, raw) = await ReadHead(inbound);
            if (head is null) return;
            var parts = head.Split("\r\n")[0].Split(' ');
            if (parts.Length < 3)
            {
                await Refuse(inbound, 400, "Bad Request", "The relay could not read that request.");
                return;
            }
            if (parts[0] == "CONNECT")
            {
                if (RelayWire.Authority(parts[1]) is not var (host, port))
                {
                    await Refuse(inbound, 400, "Bad Request", "The relay could not read that address.");
                    return;
                }
                if (RelayWire.Intercepts(host, port)) await Open(inbound);
                else await Tunnel(inbound, host, port, null);
            }
            else if (Uri.TryCreate(parts[1], UriKind.Absolute, out var url) && url.Scheme == "http")
            {
                await Tunnel(inbound, url.Host, url.Port, raw);
            }
            else await Refuse(inbound, 400, "Bad Request", "The relay only forwards CONNECT.");
        }
        catch (Exception e) when (e is IOException or SocketException or System.Security.Authentication.AuthenticationException or ObjectDisposedException) { }
    }

    /// Anything but the service goes through as bytes; an http:// request in
    /// absolute form is passed on whole for a client that sends everything here.
    private static async Task Tunnel(NetworkStream inbound, string host, int port, byte[]? head)
    {
        using var far = new TcpClient();
        try { await far.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (Exception)
        {
            await Refuse(inbound, 502, "Bad Gateway", $"The relay could not reach {host}.");
            return;
        }
        var outbound = far.GetStream();
        if (head is null) await inbound.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
        else await outbound.WriteAsync(head);
        await Splice(inbound, outbound);
    }

    /// The service's TLS is taken apart here, and each request in it is
    /// spoken again to the service under whichever account answers it.
    private async Task Open(NetworkStream inbound)
    {
        await inbound.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
        var tls = new SslStream(inbound, leaveInnerStreamOpen: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _identity,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
        }, timeout.Token);
        await new Exchange(new Wire(tls), _state, _upstream).Run();
    }

    private static async Task Splice(Stream a, Stream b)
    {
        static async Task Copy(Stream from, Stream to)
        {
            try { await from.CopyToAsync(to); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Security.Authentication.AuthenticationException) { }
            // Half-closing is not offered by every stream here, so one side
            // ending ends the pair.
            try { to.Close(); } catch { }
            try { from.Close(); } catch { }
        }
        await Task.WhenAll(Copy(a, b), Copy(b, a));
    }

    private static async Task<(string? Head, byte[] Raw)> ReadHead(Stream s)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (bytes.Count < 16384)
        {
            if (await s.ReadAsync(one, timeout.Token) != 1) return (null, []);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^1] == '\n' && bytes[^2] == '\r' && bytes[^3] == '\n' && bytes[^4] == '\r')
                return (Encoding.ASCII.GetString(bytes.ToArray()), bytes.ToArray());
        }
        return (null, []);
    }

    private static Task Refuse(Stream s, int code, string reason, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return s.WriteAsync(head.Concat(body).ToArray()).AsTask();
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        PoolEnvironment.ForgetInheritedLines();
        var options = new Relay.Options();
        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--port" when int.TryParse(next, out var port): options = options with { Port = port }; i++; break;
                case "--pipe" when next is not null: options = options with { Pipe = next }; i++; break;
                case "--key-name" when next is not null: options = options with { KeyName = next }; i++; break;
                case "--support" when next is not null: GraftPaths.ProfilesRootOverride = next; i++; break;
                case "--upstream-loopback-port" when int.TryParse(next, out var up): options = options with { UpstreamLoopbackPort = up }; i++; break;
                case "--removal-check" when double.TryParse(next, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0:
                    options = options with { RemovalCheck = TimeSpan.FromSeconds(s) }; i++; break;
            }
        }
        return await new Relay(options).RunAsync();
    }
}
