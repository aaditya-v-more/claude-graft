using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

public sealed class PoolRelayServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly PoolState state = new();
    private readonly X509Certificate2 identity;
    private readonly Func<CancellationToken, Task<PoolWire>> upstream;
    private readonly CancellationTokenSource stopped = new(), exchanges = new();
    private readonly List<Task> clients = [];
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly string binary;
    private FileStream? lease;
    private Task? accept, control, removal;
    private PoolEnvironment.Journal? remembered;
    private int removalMisses;
    public PoolState State => state;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public PoolRelayServer(string binary) : this(binary, UsagePool.Port, null, null) { }
    internal PoolRelayServer(string binary, int port, X509Certificate2? certificate, Func<CancellationToken, Task<PoolWire>>? upstream)
    {
        this.binary = binary;
        PoolStorage.EnsurePrivateDirectory(UsagePool.Directory);
        // The exclusive file remains held for the listener's lifetime, including certificate renewal.
        lease = new FileStream(Path.Combine(UsagePool.Directory, "relay.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { identity = certificate ?? PoolCertificates.Ensure(); }
        catch { lease.Dispose(); throw; }
        this.upstream = upstream ?? ConnectService;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
    }
    private static async Task<PoolWire> ConnectService(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(UsagePool.Host, 443, timeout.Token);
            var tls = new SslStream(tcp.GetStream());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            { TargetHost = UsagePool.Host, ApplicationProtocols = [SslApplicationProtocol.Http11], EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
            return new(tls);
        }
        catch { tcp.Dispose(); throw; }
    }
    public void Start(bool monitorRemoval = true)
    {
        listener.Start(128); accept = AcceptAsync(); control = ControlAsync();
        if (monitorRemoval) removal = MonitorRemovalAsync();
    }
    public async Task RunAsync(CancellationToken cancellation = default)
    {
        Start();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopped.Token).Token); }
        catch (OperationCanceledException) { }
        StopListening();
        Task[] draining; lock (clients) draining = clients.ToArray();
        try { await Task.WhenAll(draining).WaitAsync(TimeSpan.FromMinutes(15)); } catch { }
    }
    private void StopListening()
    {
        if (stopped.IsCancellationRequested) return;
        listener.Stop(); lease?.Dispose(); lease = null; stopped.Cancel();
    }
    private async Task AcceptAsync()
    {
        while (!stopped.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stopped.Token); }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { break; }
            var task = HandleAsync(client); lock (clients) { clients.RemoveAll(t => t.IsCompleted); clients.Add(task); }
        }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var wire = new PoolWire(client.GetStream()))
        {
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(exchanges.Token); handshake.CancelAfter(TimeSpan.FromSeconds(20));
                var connect = await wire.HeadAsync(handshake.Token);
                if (connect is null || connect.Method != "CONNECT" || !Uri.TryCreate("https://" + connect.Target, UriKind.Absolute, out var destination)
                    || destination.UserInfo.Length > 0 || destination.AbsolutePath != "/" || destination.Query.Length > 0 || destination.Fragment.Length > 0)
                { await FailAsync(wire, 400, "Bad Request", exchanges.Token); return; }
                if (!destination.Host.Equals(UsagePool.Host, StringComparison.OrdinalIgnoreCase) || destination.Port != 443)
                {
                    using var remote = new TcpClient(); await remote.ConnectAsync(destination.Host, destination.Port, handshake.Token);
                    await wire.SendAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), exchanges.Token);
                    using var remoteWire = new PoolWire(remote.GetStream()); await Splice(wire, remoteWire, exchanges.Token); return;
                }
                if (wire.HasBuffered) { await FailAsync(wire, 400, "Bad Request", exchanges.Token); return; }
                await wire.SendAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), exchanges.Token);
                using var tls = new SslStream(wire.Stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = identity, ApplicationProtocols = [SslApplicationProtocol.Http11], EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, handshake.Token);
                using var secured = new PoolWire(tls);
                await new PoolExchange(secured, state, upstream).RunAsync(exchanges.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or AuthenticationException) { }
        }
    }
    internal static async Task Splice(PoolWire client, PoolWire upstream, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var up = client.CopyRemainingAsync(upstream.Stream, stop.Token); var down = upstream.CopyRemainingAsync(client.Stream, stop.Token);
        await Task.WhenAny(up, down); stop.Cancel();
        try { await Task.WhenAll(up, down); } catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }
    internal static async Task FailAsync(PoolWire client, int status, string reason, CancellationToken cancellation)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = new { type = "api_error", message = "The local usage relay could not complete this connection. Try again or disable Shared Usage in Graft." } });
        await client.SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), cancellation);
        await client.SendAsync(body, cancellation);
    }
    private async Task ControlAsync()
    {
        while (!stopped.IsCancellationRequested)
        {
            // A yielded relay may still be sending its last acknowledgement when
            // its successor binds. The file lease, not a lingering pipe handle,
            // decides which process is allowed to listen and configure requests.
            using var pipe = new NamedPipeServerStream(PoolControl.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stopped.Token);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var bytes = await PoolControl.ReadLineAsync(pipe, timeout.Token);
                if (OperatingSystem.IsWindows() && !IsSameUser(pipe)) continue;
                var command = JsonSerializer.Deserialize<PoolCommand>(bytes, UsagePool.Json) ?? throw new JsonException();
                if (command.Configure is { } config)
                {
                    if (config.Members is null || config.Members.Length > 512 || config.Members.Any(m => m is null || string.IsNullOrEmpty(m.Id) || m.Identify is null || m.Token?.IndexOfAny(['\r', '\n']) >= 0)
                        || config.Members.Select(m => m.Id).Distinct().Count() != config.Members.Length) throw new JsonException();
                    state.Configure(config, DateTimeOffset.UtcNow);
                }
                // Release the port and lease before acknowledging the successor's request.
                if (command.Quit) StopListening();
                var answer = state.Status(binary, Environment.ProcessId, started, DateTimeOffset.UtcNow);
                await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(answer, UsagePool.Json), timeout.Token);
                await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException) { }
        }
    }
    [SupportedOSPlatform("windows")]
    private static bool IsSameUser(NamedPipeServerStream pipe)
    {
        var own = WindowsIdentity.GetCurrent().User?.Value; string? peer = null;
        pipe.RunAsClient(() => peer = WindowsIdentity.GetCurrent(true)?.User?.Value);
        return own is not null && peer == own;
    }
    private async Task MonitorRemovalAsync()
    {
        while (!stopped.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stopped.Token); } catch (OperationCanceledException) { return; }
            CheckRemoval();
        }
    }
    internal bool CheckRemoval()
    {
        try
        {
            using var stateLock = StateLock.Acquire();
            // A manager can re-enable sharing while this timer waits. Recheck
            // both its current journal and settings under the cleanup lock.
            var current = PoolEnvironment.LoadJournal();
            if (File.Exists(PoolEnvironment.JournalFile)) remembered = current;
            var enabled = PoolSettings.Load().Enabled;
            var disabled = !enabled && remembered is not null && remembered.Profiles.Count > 0 && remembered.Profiles.Values.All(p => !p.Wanted);
            if (!disabled)
            {
                if (remembered?.Owner is not string owner || File.Exists(owner)) { removalMisses = 0; return false; }
                if (++removalMisses < 2) return false;
            }
            state.Configure(new(false, []), DateTimeOffset.UtcNow);
            if (PoolEnvironment.TakeOutLines(remembered).Length == 0) { StopListening(); return true; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }
    public async ValueTask DisposeAsync()
    {
        StopListening(); exchanges.Cancel();
        Task[] pending; lock (clients) pending = clients.ToArray();
        try { await Task.WhenAll(pending.Concat(new[] { accept, control, removal }.OfType<Task>())).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        identity.Dispose(); stopped.Dispose(); exchanges.Dispose(); lease?.Dispose();
    }
}

public sealed class PoolExchange(PoolWire client, PoolState state, Func<CancellationToken, Task<PoolWire>> upstream)
{
    private const long BodyLimit = 256L * 1024 * 1024;
    private PoolWire? connection;
    public async Task RunAsync(CancellationToken cancellation)
    {
        try { while (await ServeOne(cancellation)) { } }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or AuthenticationException) { }
        finally { connection?.Dispose(); }
    }
    private async Task<bool> ServeOne(CancellationToken cancellation)
    {
        var head = await client.HeadAsync(cancellation); if (head is null) return false;
        var framing = head.RequestBody();
        if (head.Method == "CONNECT") { await PoolRelayServer.FailAsync(client, 400, "Bad Request", cancellation); return false; }
        state.Began();
        try
        {
            if (head.Tokens("expect").Contains("100-continue")) await client.SendAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellation);
            var own = PoolRequest.Bearer(head.Fields) is string bearer ? state.Member(bearer, DateTimeOffset.UtcNow) : null;
            if (!PoolRequest.IsInference(head.Method, head.Target) || own is null || head.Upgrade
                || head.Tokens("transfer-encoding").Any(t => t != "chunked")) return await PassThrough(head, framing, cancellation);
            var body = await client.BodyAsync(framing, BodyLimit, cancellation);
            var shape = PoolRequest.Inspect(body, head.Value("content-encoding")); var session = head.Value("x-claude-code-session-id");
            var attempts = state.Plan(own, session, shape.Pinned, shape.Bound, DateTimeOffset.UtcNow);
            var passed = new List<PoolPass>();
            for (var index = 0; index < attempts.Count; index++)
            {
                var attempt = attempts[index]; var last = index == attempts.Count - 1; var borrowed = attempt.Token is not null;
                PoolHead answer;
                try { answer = await Send(head.Request(body, attempt.Token, shape.Continuing && borrowed), cancellation); }
                catch (Exception e) when (e is IOException or SocketException or AuthenticationException)
                {
                    Drop(); if (last) { await PoolRelayServer.FailAsync(client, 502, "Bad Gateway", cancellation); return false; }
                    passed.Add(new(attempt.Member!, "unreachable")); continue;
                }
                var now = DateTimeOffset.UtcNow; state.Observe(attempt.Member!, answer.Fields, now);
                var outcome = PoolPolicy.Evaluate(answer.Status, answer.Fields, borrowed, now);
                if (!last && outcome.Result == PoolPolicy.Result.RefusedForAccount && answer.Status == 404 && shape.Continuing
                    && (answer.Value("content-encoding") is null or "identity") && answer.ResponseBody(head.Method).Kind != PoolBodyKind.UntilClose)
                {
                    var error = await connection!.BodyAsync(answer.ResponseBody(head.Method), 65536, cancellation);
                    if (Encoding.UTF8.GetString(error).Contains("thread_not_found", StringComparison.Ordinal))
                    {
                        state.Served(attempt.Member!, own, session, answer.Status, passed.ToArray(), shape, now);
                        await client.SendAsync(answer.Raw, cancellation);
                        if (answer.ResponseBody(head.Method).Kind == PoolBodyKind.Chunked)
                        {
                            if (error.Length > 0) { await client.SendAsync(Encoding.ASCII.GetBytes(error.Length.ToString("x") + "\r\n"), cancellation); await client.SendAsync(error, cancellation); await client.SendAsync("\r\n"u8.ToArray(), cancellation); }
                            await client.SendAsync("0\r\n\r\n"u8.ToArray(), cancellation);
                        }
                        else await client.SendAsync(error, cancellation);
                        return Finish(head, answer);
                    }
                }
                if (outcome.Result != PoolPolicy.Result.Deliver && !last)
                {
                    state.Apply(outcome, attempt.Member!, now); Drop();
                    passed.Add(new(attempt.Member!, outcome.Result switch { PoolPolicy.Result.Exhausted => "full", PoolPolicy.Result.LoginRefused => "login", _ => "refused" }));
                    if (outcome.Result == PoolPolicy.Result.RefusedForAccount)
                        while (index + 1 < attempts.Count - 1 && attempts[index + 1].Token is not null) index++;
                    continue;
                }
                state.Served(attempt.Member!, own, session, answer.Status, passed.ToArray(), shape, now);
                return await Deliver(head, answer, cancellation);
            }
            return false;
        }
        finally { state.Ended(); }
    }
    private void Drop() { connection?.Dispose(); connection = null; }
    private async Task<PoolHead> Send(byte[] request, CancellationToken cancellation)
    {
        if (connection is not null)
        {
            var received = connection.Received;
            try { await connection.SendAsync(request, cancellation); return await connection.FinalHeadAsync(cancellation); }
            catch (IOException) when (connection.Received == received) { Drop(); }
        }
        connection = await upstream(cancellation);
        await connection.SendAsync(request, cancellation); return await connection.FinalHeadAsync(cancellation);
    }
    private async Task<bool> PassThrough(PoolHead head, PoolBody framing, CancellationToken cancellation)
    {
        if (!head.Upgrade && framing.Kind != PoolBodyKind.Chunked)
        {
            var body = await client.BodyAsync(framing, BodyLimit, cancellation);
            PoolHead answer;
            try { answer = await Send([.. head.Raw, .. body], cancellation); }
            catch (Exception e) when (e is IOException or SocketException or AuthenticationException)
            { await PoolRelayServer.FailAsync(client, 502, "Bad Gateway", cancellation); return false; }
            return await Deliver(head, answer, cancellation);
        }
        Drop(); connection = await upstream(cancellation);
        await connection.SendAsync(head.Raw, cancellation);
        // Upload and download overlap: Remote Control can keep its request body open
        // until it sees the response. Waiting for that body would deadlock the connection.
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var upload = client.CopyBodyAsync(framing, connection.Stream, false, long.MaxValue, exchange.Token);
        try
        {
            var answer = await connection.FinalHeadAsync(exchange.Token);
            if (head.Upgrade && answer.Status == 101)
            {
                await upload; await client.SendAsync(answer.Raw, exchange.Token);
                await PoolRelayServer.Splice(client, connection, exchange.Token); return false;
            }
            var more = await Deliver(head, answer, exchange.Token); await upload; return more;
        }
        finally { exchange.Cancel(); try { await upload; } catch (Exception e) when (e is IOException or OperationCanceledException) { } }
    }
    private async Task<bool> Deliver(PoolHead request, PoolHead answer, CancellationToken cancellation)
    {
        await client.SendAsync(answer.Raw, cancellation);
        await connection!.CopyBodyAsync(answer.ResponseBody(request.Method), client.Stream, false, long.MaxValue, cancellation);
        return Finish(request, answer);
    }
    private bool Finish(PoolHead request, PoolHead answer)
    {
        if (!answer.KeepAlive || connection?.HasBuffered == true || answer.ResponseBody(request.Method).Kind == PoolBodyKind.UntilClose) Drop();
        return request.KeepAlive && answer.KeepAlive && answer.ResponseBody(request.Method).Kind != PoolBodyKind.UntilClose;
    }
}
