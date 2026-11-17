using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// A stand-in for the service on loopback that answers as the service does
/// for each token it knows, and writes down every request that reached it.
/// </summary>
public sealed class StandInService : IDisposable
{
    public sealed class Account
    {
        public double FiveHour = 0.1, Week = 0.1;
        public DateTimeOffset WeekReset;
        public bool Reject;
        public int? Refuse;
    }

    public sealed record Seen(string Method, string Path, string Token, byte[] Body, string? Session,
        string? Connection = null, bool Chunked = false, string? Accept = null);

    public static readonly string[] Stream =
    [
        "event: message_start\ndata: {\"n\":1}\n\n",
        "event: content_block_delta\ndata: {\"n\":2}\n\n",
        "event: message_stop\ndata: {\"n\":3}\n\n",
    ];

    public readonly Dictionary<string, Account> Accounts = new();
    /// Which token holds each thread; only that one can continue it.
    public readonly Dictionary<string, string> Threads = new();
    private readonly List<Seen> _seen = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    public readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public StandInService()
    {
        _listener.Start();
        _ = Task.Run(Accept);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Count { get { lock (_seen) return _seen.Count; } }
    public List<Seen> Since(int before) { lock (_seen) return _seen.Skip(before).ToList(); }

    public void Dispose() => _listener.Stop();

    private async Task Accept()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                using var _ = c;
                var reader = new Reader(c.GetStream());
                try { while (await Answer(reader, c.GetStream())) { } }
                catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
            });
        }
    }

    private void Record(Seen s) { lock (_seen) _seen.Add(s); }

    private static byte[] Head(int status, string reason, params (string, string)[] fields)
    {
        var text = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");
        foreach (var (k, v) in fields) text.Append(k).Append(": ").Append(v).Append("\r\n");
        return Encoding.ASCII.GetBytes(text.Append("\r\n").ToString());
    }

    private static byte[] Chunk(byte[] data) => [.. Encoding.ASCII.GetBytes(data.Length.ToString("x") + "\r\n"), .. data, .. "\r\n"u8.ToArray()];

    private async Task<bool> Answer(Reader reader, NetworkStream s)
    {
        if (await reader.Head() is not { } head) return false;
        var auth = head.Value("authorization") ?? "";
        var token = auth.Length > 7 ? auth[7..] : "";
        if ((head.Value("upgrade") ?? "").Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            Record(new(head.Method, head.Target, token, [], null, Connection: head.Value("connection")));
            await s.WriteAsync(Head(101, "Switching Protocols", ("Upgrade", "websocket"), ("Connection", "Upgrade")));
            var buf = new byte[65536];
            if (reader.Pending is { Length: > 0 } early) await s.WriteAsync(early);
            int n;
            while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
            return false;
        }
        if (head.RequestBody is HttpBody.Chunked)
        {
            Record(new(head.Method, head.Target, token, [], null, Chunked: true));
            await s.WriteAsync(Head(200, "OK", ("Transfer-Encoding", "chunked")));
            while (true)
            {
                var size = Convert.ToInt32((await reader.Line()).Trim() is { Length: > 0 } t ? t : "0", 16);
                if (size == 0) { await reader.Line(); break; }
                var data = await reader.Bytes(size);
                await reader.Line();
                await s.WriteAsync(Chunk([.. "got "u8.ToArray(), .. data]));
            }
            await s.WriteAsync("0\r\n\r\n"u8.ToArray());
            return true;
        }
        var length = head.RequestBody is HttpBody.Length l ? (int)l.Count : 0;
        var body = await reader.Bytes(length);
        var seen = new Seen(head.Method, head.Target, token, body, head.Value("x-claude-code-session-id"), Accept: head.Value("accept-encoding"));
        Record(seen);
        Account? account;
        lock (Accounts) Accounts.TryGetValue(token, out account);
        if (head.Target.Split('?')[0] != "/v1/messages" || head.Method != "POST" || account is null)
        {
            var payload = Encoding.UTF8.GetBytes($"{{\"token\": \"{token}\", \"path\": \"{head.Target}\"}}");
            await s.WriteAsync((byte[])[.. Head(200, "OK", ("Content-Type", "application/json"), ("Content-Length", payload.Length.ToString())), .. payload]);
            return true;
        }
        if (account.Refuse is int refuse)
        {
            var payload = """{"type":"error","error":{"type":"authentication_error"}}"""u8.ToArray();
            await s.WriteAsync((byte[])[.. Head(refuse, "Refused", ("Content-Length", payload.Length.ToString())), .. payload]);
            return true;
        }
        (string, string)[] limits =
        [
            ("anthropic-ratelimit-unified-5h-utilization", account.FiveHour.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("anthropic-ratelimit-unified-7d-utilization", account.Week.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("anthropic-ratelimit-unified-5h-reset", (Now + TimeSpan.FromHours(1)).ToUnixTimeSeconds().ToString()),
            ("anthropic-ratelimit-unified-7d-reset", account.WeekReset.ToUnixTimeSeconds().ToString()),
        ];
        if (account.Reject)
        {
            var payload = """{"type":"error","error":{"type":"rate_limit_error"}}"""u8.ToArray();
            await s.WriteAsync((byte[])[.. Head(429, "Too Many Requests", [.. limits, ("anthropic-ratelimit-unified-status", "rejected"),
                ("anthropic-ratelimit-unified-reset", (Now + TimeSpan.FromMinutes(30)).ToUnixTimeSeconds().ToString()),
                ("Content-Length", payload.Length.ToString())]), .. payload]);
            return true;
        }
        var continued = Regex.Match(Encoding.UTF8.GetString(body), "\"thread\":\\{\"type\":\"continue\",\"previous_message_id\":\"([^\"]+)\"");
        string? holder = null;
        if (continued.Success) lock (Accounts) Threads.TryGetValue(continued.Groups[1].Value, out holder);
        if (continued.Success && holder != token)
        {
            var payload = """{"type":"error","error":{"type":"not_found_error","message":"thread_not_found"}}"""u8.ToArray();
            var fields = new List<(string, string)> { ("Content-Type", "application/json"), ("Transfer-Encoding", "chunked") };
            // As the service does: even an error is compressed for a client that accepts it.
            if ((head.Value("accept-encoding") ?? "").Contains("gzip"))
            {
                using var packed = new MemoryStream();
                using (var gz = new GZipStream(packed, CompressionLevel.Fastest)) gz.Write(payload);
                payload = packed.ToArray();
                fields.Add(("Content-Encoding", "gzip"));
            }
            await s.WriteAsync((byte[])[.. Head(404, "Not Found", [.. fields]), .. Chunk(payload), .. "0\r\n\r\n"u8.ToArray()]);
            return true;
        }
        await s.WriteAsync(Head(200, "OK", [.. limits, ("anthropic-ratelimit-unified-status", "allowed"),
            ("Content-Type", "text/event-stream"), ("Transfer-Encoding", "chunked")]));
        foreach (var e in Stream)
        {
            await s.WriteAsync(Chunk(Encoding.ASCII.GetBytes(e)));
            await Task.Delay(10);
        }
        await s.WriteAsync("0\r\n\r\n"u8.ToArray());
        return true;
    }

    /// Just enough reading for both ends of these tests.
    public sealed class Reader(Stream s)
    {
        private readonly List<byte> _buf = new();
        public byte[]? Pending => _buf.ToArray();

        private async Task<bool> More()
        {
            var b = new byte[65536];
            var n = await s.ReadAsync(b);
            if (n == 0) return false;
            _buf.AddRange(b.AsSpan(0, n).ToArray());
            return true;
        }

        public async Task<HttpHead?> Head()
        {
            while (true)
            {
                if (HttpHead.LengthIn(_buf.ToArray()) is int len)
                {
                    var raw = _buf.Take(len).ToArray();
                    _buf.RemoveRange(0, len);
                    return HttpHead.Parse(raw);
                }
                if (!await More()) return null;
            }
        }

        public async Task<string> Line()
        {
            while (true)
            {
                var at = _buf.IndexOf((byte)'\n');
                if (at >= 0)
                {
                    var line = Encoding.ASCII.GetString(_buf.Take(at).ToArray()).TrimEnd('\r');
                    _buf.RemoveRange(0, at + 1);
                    return line;
                }
                if (!await More()) throw new IOException("ended");
            }
        }

        public async Task<byte[]> Bytes(int count)
        {
            while (_buf.Count < count) if (!await More()) throw new IOException("ended");
            var taken = _buf.Take(count).ToArray();
            _buf.RemoveRange(0, count);
            return taken;
        }

        public async Task<byte[]> Rest()
        {
            while (await More()) { }
            var all = _buf.ToArray();
            _buf.Clear();
            return all;
        }

        /// An answer's body as framed, raw: a chunked one is unchunked.
        public async Task<byte[]> Body(HttpHead head, string method)
        {
            switch (head.ResponseBody(method))
            {
                case HttpBody.Length l: return await Bytes((int)l.Count);
                case HttpBody.Chunked:
                    var all = new List<byte>();
                    while (true)
                    {
                        var size = Convert.ToInt32((await Line()).Split(';')[0].Trim(), 16);
                        if (size == 0) { await Line(); return all.ToArray(); }
                        all.AddRange(await Bytes(size));
                        await Line();
                    }
                case HttpBody.UntilClose: return await Rest();
                default: return [];
            }
        }
    }
}

[Collection("GlobalState")]
public sealed class RelayRoutingTests : IDisposable
{
    private const string KeyName = "Claude Graft relay (routing test)";
    private readonly TempDir _tmp = new();
    private readonly string _pipe = "ClaudeGraft.Relay.test." + Guid.NewGuid().ToString("N");
    private readonly int _port;
    private readonly StandInService _service = new();
    private readonly Process _relay;
    private readonly DateTimeOffset _now;
    private int _observed = 0;

    public RelayRoutingTests()
    {
        _now = _service.Now;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Account("tok-A", 5);
        Account("tok-B", 2);
        Account("tok-C", 3).Refuse = 401;
        _relay = Process.Start(new ProcessStartInfo(RelayExe())
        {
            ArgumentList = { "--port", _port.ToString(), "--pipe", _pipe, "--key-name", KeyName,
                             "--support", _tmp.Path, "--upstream-loopback-port", _service.Port.ToString() },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        for (var i = 0; i < 100 && Control() is null; i++) Thread.Sleep(100);
    }

    public void Dispose()
    {
        try { if (!_relay.HasExited) _relay.Kill(); } catch { }
        _relay.Dispose();
        _service.Dispose();
        if (OperatingSystem.IsWindows()) RelayCertificates.DeleteKey(KeyName);
        _tmp.Dispose();
    }

    private static string RelayExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "GraftRelay"))) dir = dir.Parent;
        return Directory.EnumerateFiles(Path.Combine(dir!.FullName, "GraftRelay", "bin"), "GraftRelay.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private StandInService.Account Account(string token, double weekResetDays)
    {
        var a = new StandInService.Account { WeekReset = _now + TimeSpan.FromDays(weekResetDays) };
        lock (_service.Accounts) _service.Accounts[token] = a;
        return a;
    }

    private RelayStatus? Control(RelayRequest? request = null) => RelayKeeper.Send(request ?? new RelayRequest(), _pipe, 3000);

    private static PoolMember Member(string id, string token, bool lend = true, string[]? borrows = null, string? account = null, PoolLimits? limits = null) =>
        new() { Id = id, Name = id, Identify = [PoolRequest.Digest(token)], Token = lend ? token : null, BorrowsFrom = borrows, Account = account, Limits = limits };

    /// Limits as Graft hands them over, newer than anything the relay has heard.
    private PoolLimits Pushed(TimeSpan weekReset) =>
        new() { FiveHour = 0.1, Week = 0.1, Observed = DateTimeOffset.UtcNow.AddSeconds(++_observed * 10 + 20), WeekReset = _now + weekReset };

    private void Configure(bool enabled, PoolMember[] members, Dictionary<string, string>? assignments = null) =>
        Assert.NotNull(Control(new RelayRequest { Configure = new PoolConfiguration { Enabled = enabled, Members = members, Assignments = assignments } }));

    private sealed class Conn(SslStream s) : IDisposable
    {
        public readonly SslStream Stream = s;
        public readonly StandInService.Reader Reader = new(s);
        public void Dispose() => Stream.Dispose();
    }

    private async Task<SslStream> Tunnel()
    {
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, _port);
        var s = c.GetStream();
        await s.WriteAsync("CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n"u8.ToArray());
        var reply = new List<byte>();
        var one = new byte[1];
        while (!Encoding.ASCII.GetString(reply.ToArray()).EndsWith("\r\n\r\n") && await s.ReadAsync(one) == 1) reply.Add(one[0]);
        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(reply.ToArray()));
        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(_tmp.Path, "ClaudeGraft", "pool", "relay-ca.pem")));
        var ssl = new SslStream(s, false, (_, cert, _, _) =>
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(cert!));
        });
        await ssl.AuthenticateAsClientAsync(UsagePool.Host);
        return ssl;
    }

    private async Task<Conn> Connection() => new(await Tunnel());

    private const string Plain = """{"model":"claude-test","messages":[]}""";

    private async Task<(int Status, HttpHead Head, byte[] Payload, List<string> Went)> Ask(Conn conn, string token,
        string body = Plain, string path = "/v1/messages?beta=true", string method = "POST", string? session = "s1",
        params (string, string)[] extra)
    {
        var before = _service.Count;
        var bytes = Encoding.UTF8.GetBytes(body);
        var text = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\n");
        if (session is not null) text.Append($"X-Claude-Code-Session-Id: {session}\r\n");
        foreach (var (k, v) in extra) text.Append($"{k}: {v}\r\n");
        if (method == "POST") text.Append($"Content-Length: {bytes.Length}\r\n");
        text.Append("\r\n");
        await conn.Stream.WriteAsync(method == "POST" ? [.. Encoding.ASCII.GetBytes(text.ToString()), .. bytes] : Encoding.ASCII.GetBytes(text.ToString()));
        var head = (await conn.Reader.Head().WaitAsync(TimeSpan.FromSeconds(10)))!;
        var payload = await conn.Reader.Body(head, method).WaitAsync(TimeSpan.FromSeconds(10));
        if (head.Value("content-encoding") == "gzip")
        {
            using var gz = new GZipStream(new MemoryStream(payload), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gz.CopyTo(plain);
            payload = plain.ToArray();
        }
        return (head.Status!.Value, head, payload, _service.Since(before).Select(w => w.Token).ToList());
    }

    private static readonly string Streamed = string.Concat(StandInService.Stream);

    private static string Route((int Status, HttpHead Head, byte[] Payload, List<string> Went) r) => r.Status + " " + string.Join(",", r.Went);

    [Fact(DisplayName = "an unconfigured relay passes a request through as sent, streamed back byte for byte")]
    public async Task Unconfigured()
    {
        using var conn = await Connection();
        var (status, _, payload, went) = await Ask(conn, "tok-A");
        Assert.Equal("200 tok-A", status + " " + string.Join(",", went));
        Assert.Equal(Streamed, Encoding.ASCII.GetString(payload));
    }

    [Fact(DisplayName = "the account whose week resets soonest answers another member's request, and only model requests move")]
    public async Task Lends()
    {
        using var conn = await Connection();
        Configure(true, [Member("A", "tok-A", borrows: ["B", "C"]), Member("B", "tok-B")]);
        Assert.True(Control()!.Configured);
        Assert.Equal(["tok-A"], (await Ask(conn, "tok-A")).Went);

        Configure(true, [Member("A", "tok-A", borrows: ["B", "C"]), Member("B", "tok-B", limits: Pushed(TimeSpan.FromDays(2)))]);
        var before = _service.Count;
        var (_, _, payload, went) = await Ask(conn, "tok-A", session: "s3");
        Assert.Equal(["tok-B"], went);
        Assert.Equal(Streamed, Encoding.ASCII.GetString(payload));
        Assert.Equal("s3", _service.Since(before)[0].Session);

        foreach (var (path, method, body) in new[] { ("/v1/messages/count_tokens?beta=true", "POST", """{"model":"x"}"""),
            ("/api/oauth/profile", "GET", ""), ("/api/frame/abc", "GET", "") })
            Assert.Equal(["tok-A"], (await Ask(conn, "tok-A", body, path, method)).Went);

        foreach (var marker in new[] { "\"file_id\":\"f1\"", "\"container\":\"c1\"", "\"container\":{\"id\":\"c1\",\"skills\":[]}", "\"mcp_servers\":[{\"type\":\"url\",\"url\":\"u\"}]" })
            Assert.Equal(["tok-A"], (await Ask(conn, "tok-A", "{\"model\":\"x\",\"messages\":[" + marker + "]}", session: null)).Went);
        var schema = """{"model":"x","messages":[],"tools":[{"name":"batch","input_schema":{"type":"object","properties":{"container":{"type":"object"},"file_id":{"type":"string"},"mcp_servers":{"type":"array"}}}}]}""";
        Assert.Equal(["tok-B"], (await Ask(conn, "tok-A", schema, session: null)).Went);
        Assert.Equal(["tok-unknown"], (await Ask(conn, "tok-unknown")).Went);
    }

    [Fact(DisplayName = "a full account is passed over within the request and not asked again; a refused login likewise")]
    public async Task PassesOver()
    {
        using var conn = await Connection();
        Configure(true, [Member("A", "tok-A", borrows: ["B", "C"]), Member("B", "tok-B", limits: Pushed(TimeSpan.FromDays(2)))]);
        _service.Accounts["tok-B"].Reject = true;
        var (status, _, payload, went) = await Ask(conn, "tok-A", session: "s4");
        Assert.Equal("200 tok-B,tok-A", status + " " + string.Join(",", went));
        Assert.Equal(Streamed, Encoding.ASCII.GetString(payload));
        Assert.Equal(["tok-A"], (await Ask(conn, "tok-A", session: "s5")).Went);
        var recent = Control()!.Recent[^2];
        Assert.Equal("A", recent.ServedBy);
        Assert.Equal([new PoolPass("B", PoolPassReason.Full)], recent.PassedOver);
        _service.Accounts["tok-B"].Reject = false;

        Configure(true, [Member("A", "tok-A", borrows: ["B", "C"]), Member("C", "tok-C", limits: Pushed(TimeSpan.FromDays(1)))]);
        Assert.Equal("200 tok-C,tok-A", Route(await Ask(conn, "tok-A", session: "s6")));
        Assert.Equal(["tok-A"], (await Ask(conn, "tok-A", session: "s7")).Went);
        Assert.True(Control()!.Members[1].TokenRefused);
    }

    [Fact(DisplayName = "lending runs one way, never from a Claude's own account, and a hand move outranks the ranking")]
    public async Task Directions()
    {
        using var conn = await Connection();
        Account("tok-A", 1);
        Account("tok-D", 1.0 / 24);
        Configure(true, [Member("A", "tok-A", borrows: [], limits: Pushed(TimeSpan.FromDays(1))), Member("B", "tok-B", borrows: ["A"], limits: Pushed(TimeSpan.FromDays(4)))]);
        Assert.Equal(["tok-A"], (await Ask(conn, "tok-A", session: "s12")).Went);
        Assert.Equal(["tok-A"], (await Ask(conn, "tok-B", session: "s13")).Went);
        Configure(true, [Member("B", "tok-B", borrows: ["D"], account: "acct-B", limits: Pushed(TimeSpan.FromDays(4))), Member("D", "tok-D", account: "acct-B", limits: Pushed(TimeSpan.FromHours(1)))]);
        Assert.Equal(["tok-B"], (await Ask(conn, "tok-B", session: "s14")).Went);

        Configure(true, [Member("A", "tok-A", borrows: ["B"], limits: Pushed(TimeSpan.FromDays(1))), Member("B", "tok-B", limits: Pushed(TimeSpan.FromDays(4)))],
            new() { ["s15"] = "B" });
        Assert.Equal(["tok-B"], (await Ask(conn, "tok-A", session: "s15")).Went);
        Assert.Equal(["tok-A"], (await Ask(conn, "tok-A", session: "s16")).Went);
        await Ask(conn, "tok-A", """{"model":"x","system":[{"cache_control":{"type":"ephemeral","ttl":"1h"}}]}""", session: "s18");
        var sessions = Control()!.Sessions.ToDictionary(s => s.Id);
        Assert.Equal(("B", "A"), (sessions["s15"].ServedBy, sessions["s15"].From));
        Assert.Equal(TimeSpan.FromHours(1), sessions["s18"].CachedUntil - sessions["s18"].LastAt);
        Assert.Equal(TimeSpan.FromMinutes(5), sessions["s16"].CachedUntil - sessions["s16"].LastAt);
        _service.Accounts["tok-B"].Reject = true;
        Assert.Equal(["tok-B", "tok-A"], (await Ask(conn, "tok-A", session: "s15")).Went);
        _service.Accounts["tok-B"].Reject = false;

        Configure(false, [Member("A", "tok-A", borrows: ["B"]), Member("B", "tok-B")]);
        Assert.Equal(["tok-A"], (await Ask(conn, "tok-A", session: "s10")).Went);
    }

    [Fact(DisplayName = "a socket asked for is answered by the service under its own token and carries bytes both ways")]
    public async Task Upgrade()
    {
        Configure(true, [Member("A", "tok-A", limits: Pushed(TimeSpan.FromDays(1))), Member("B", "tok-B", borrows: ["A"], limits: Pushed(TimeSpan.FromDays(4)))]);
        var before = _service.Count;
        using var s = await Tunnel();
        var reader = new StandInService.Reader(s);
        await s.WriteAsync("GET /v1/sessions/ws/abc/subscribe HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer tok-B\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"u8.ToArray());
        Assert.Equal(101, (await reader.Head().WaitAsync(TimeSpan.FromSeconds(10)))!.Status);
        var went = _service.Since(before);
        Assert.Equal("tok-B", Assert.Single(went).Token);
        Assert.Contains("upgrade", went[0].Connection!.ToLowerInvariant());
        byte[] frame = [0x81, 0x05, .. "hello"u8];
        await s.WriteAsync(frame);
        Assert.Equal(frame, await reader.Bytes(7).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(DisplayName = "a request still being written is answered as it goes, still chunked, under its own token")]
    public async Task ChunkedUpload()
    {
        Configure(true, [Member("A", "tok-A", limits: Pushed(TimeSpan.FromDays(1))), Member("B", "tok-B", borrows: ["A"], limits: Pushed(TimeSpan.FromDays(4)))]);
        var before = _service.Count;
        using var s = await Tunnel();
        var reader = new StandInService.Reader(s);
        await s.WriteAsync("POST /v1/sessions/abc/events HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer tok-B\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nfirst\r\n"u8.ToArray());
        await reader.Head().WaitAsync(TimeSpan.FromSeconds(10));
        await reader.Line();
        Assert.Equal("got first", await reader.Line().WaitAsync(TimeSpan.FromSeconds(10)));
        await s.WriteAsync("6\r\nsecond\r\n0\r\n\r\n"u8.ToArray());
        await reader.Line();
        Assert.Equal("got second", await reader.Line().WaitAsync(TimeSpan.FromSeconds(10)));
        var went = Assert.Single(_service.Since(before));
        Assert.True(went.Chunked && went.Token == "tok-B");

        using var side = await Connection();
        await Ask(side, "tok-B", "", "/api/oauth/profile", "GET");
        Assert.Equal(["tok-A"], (await Ask(side, "tok-B", session: "s21")).Went);
    }

    [Fact(DisplayName = "a conversation carrying reasoning stays where it was written until that account has no room")]
    public async Task Reasoning()
    {
        using var conn = await Connection();
        Account("tok-D", 1.0 / 24);
        Account("tok-E", 5);
        Configure(true, [Member("D", "tok-D", limits: Pushed(TimeSpan.FromHours(1))), Member("E", "tok-E", borrows: ["D"], limits: Pushed(TimeSpan.FromDays(5)))],
            new() { ["s32"] = "D" });
        const string plain = """{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"hi"}]}""";
        const string reasoned = """{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"hi"},{"role":"assistant","content":[{"type":"thinking","thinking":"t","signature":"s"},{"type":"text","text":"x"}]},{"role":"user","content":"and?"}]}""";
        Assert.Equal(["tok-D"], (await Ask(conn, "tok-E", plain, session: "s30")).Went);
        Assert.Equal(["tok-E"], (await Ask(conn, "tok-E", reasoned, session: "s31")).Went);
        Assert.Equal(["tok-E"], (await Ask(conn, "tok-E", reasoned, session: "s31")).Went);
        Assert.Equal(["tok-D"], (await Ask(conn, "tok-E", reasoned, session: "s30")).Went);
        Assert.Equal(["tok-D"], (await Ask(conn, "tok-E", reasoned, session: "s32")).Went);
        Assert.Equal(["tok-D"], (await Ask(conn, "tok-E", reasoned.Replace("claude-sonnet-5-5", "claude-opus-5-5"), session: "s33")).Went);
        _service.Accounts["tok-D"].Reject = true;
        Assert.Equal(["tok-D", "tok-E"], (await Ask(conn, "tok-E", reasoned, session: "s30")).Went);
        _service.Accounts["tok-D"].Reject = false;
        Assert.Equal(["tok-E"], (await Ask(conn, "tok-E", reasoned, session: "s30")).Went);
    }

    private static string Threaded(string kind, string? previous = null) =>
        """{"model":"claude-opus-5-5","messages":[{"role":"user","content":"hi"}],"thread":""" +
        (kind == "create" ? """{"type":"create"}""" : $$"""{"type":"continue","previous_message_id":"{{previous}}"}""") + "}";

    [Fact(DisplayName = "a thread stays on the account holding it, and a moved one restarts where it was moved")]
    public async Task Threads()
    {
        using var conn = await Connection();
        Account("tok-G", 1.0 / 24);
        Account("tok-H", 5);
        Account("tok-F", 1.0 / 48).Refuse = 404;
        void Pool(string first, Dictionary<string, string>? assignments = null)
        {
            var resets = first == "G" ? (G: TimeSpan.FromHours(1), H: TimeSpan.FromDays(5)) : (G: TimeSpan.FromDays(5), H: TimeSpan.FromHours(1));
            Configure(true, [Member("G", "tok-G", limits: Pushed(resets.G)), Member("H", "tok-H", borrows: ["G"], limits: Pushed(resets.H))], assignments ?? new());
        }
        Task<(int Status, HttpHead Head, byte[] Payload, List<string> Went)> AskThread(string session, string body) =>
            Ask(conn, "tok-H", body, session: session, extra: ("Accept-Encoding", "gzip, deflate, br"));
        string? Holder(string session) => Control()!.Sessions.FirstOrDefault(s => s.Id == session)?.ServedBy;
        const string title = """{"model":"claude-haiku-5-5","messages":[{"role":"user","content":"name this"}]}""";

        Pool("G");
        Assert.Equal(["tok-G"], (await AskThread("s40", Threaded("create"))).Went);
        _service.Threads["m1"] = "tok-G";
        Pool("H");
        var r = await AskThread("s40", Threaded("continue", "m1"));
        Assert.Equal("200 tok-G", Route(r));
        Assert.Equal(["tok-H"], (await AskThread("s41", Threaded("continue", "m9"))).Went);

        Pool("G", new() { ["s40"] = "H" });
        r = await AskThread("s40", Threaded("continue", "m1"));
        Assert.Equal("404 tok-H", Route(r));
        Assert.Contains("thread_not_found", Encoding.UTF8.GetString(r.Payload));
        Assert.Equal(["tok-H"], (await AskThread("s40", Threaded("create"))).Went);
        _service.Threads["m4"] = "tok-H";

        Pool("G", new() { ["s40"] = "G" });
        Assert.Equal(["tok-G"], (await Ask(conn, "tok-H", title, session: "s40")).Went);
        Assert.Equal("H", Holder("s40"));
        var before = _service.Count;
        r = await AskThread("s40", Threaded("continue", "m4"));
        Assert.Equal("404 tok-G", Route(r));
        Assert.Contains("thread_not_found", Encoding.UTF8.GetString(r.Payload));
        Assert.Equal("identity", _service.Since(before)[0].Accept);
        r = await AskThread("s40", Threaded("create"));
        Assert.Equal("200 tok-G", Route(r));
        _service.Threads["m2"] = "tok-G";
        Assert.Equal(["tok-G"], (await AskThread("s40", Threaded("continue", "m2"))).Went);
        Assert.Equal("G", Holder("s40"));

        var sessions = Control()!.Sessions.ToDictionary(s => s.Id);
        Assert.NotNull(sessions["s40"].StartedAt);
        await AskThread("s43", """{"model":"claude-opus-5-5","messages":[{"role":"user","content":"a"},{"role":"assistant","content":"b"},{"role":"user","content":"c"}]}""");
        sessions = Control()!.Sessions.ToDictionary(s => s.Id);
        Assert.Null(sessions["s43"].StartedAt);
        var typedAt = sessions["s40"].PromptedAt;
        Assert.NotNull(typedAt);
        Assert.Null(sessions["s43"].PromptedAt);
        await AskThread("s40", """{"model":"claude-opus-5-5","messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"u1","content":"17 x 23"}]}],"thread":{"type":"continue","previous_message_id":"m2"}}""");
        await AskThread("s40", title);
        var held = Control()!.Sessions.First(s => s.Id == "s40");
        Assert.Equal(typedAt, held.PromptedAt);
        Assert.True(held.LastAt > typedAt);
        await Task.Delay(20);
        await AskThread("s40", Threaded("continue", "m2"));
        Assert.True(Control()!.Sessions.First(s => s.Id == "s40").PromptedAt > typedAt);

        Configure(true, [Member("F", "tok-F", limits: Pushed(TimeSpan.FromMinutes(30))), Member("H", "tok-H", borrows: ["F"], limits: Pushed(TimeSpan.FromDays(5)))],
            new() { ["s42"] = "F" });
        _service.Threads["m3"] = "tok-H";
        r = await AskThread("s42", Threaded("continue", "m3"));
        Assert.Equal("200 tok-F,tok-H", Route(r));
    }

    [Fact(DisplayName = "whatever the lenders say, Claude Code hears its own account's answer, and a lender's refusal goes home")]
    public async Task HomeHasTheLastWord()
    {
        using var conn = await Connection();
        foreach (var (sender, lender, refusal) in new[] { ("J1", "K", 403), ("J2", "L", 401) })
        {
            Account("tok-" + sender, 1.0 / 24).Reject = true;
            Account("tok-" + lender, 3).Refuse = refusal;
            Configure(true, [Member(sender, "tok-" + sender, borrows: [lender], limits: Pushed(TimeSpan.FromHours(1))), Member(lender, "tok-" + lender, limits: Pushed(TimeSpan.FromDays(3)))]);
            var r = await Ask(conn, "tok-" + sender, session: "s6" + sender);
            Assert.Equal($"429 tok-{sender},tok-{lender},tok-{sender}", Route(r));
        }
        Account("tok-P", 1.0 / 48).Refuse = 403;
        Account("tok-Q", 1);
        Account("tok-R", 5);
        Configure(true, [Member("P", "tok-P", limits: Pushed(TimeSpan.FromMinutes(30))), Member("Q", "tok-Q", borrows: ["P", "R"], limits: Pushed(TimeSpan.FromDays(1))), Member("R", "tok-R", limits: Pushed(TimeSpan.FromDays(5)))]);
        var q = await Ask(conn, "tok-Q", session: "s62");
        Assert.Equal("200 tok-P,tok-Q", Route(q));
    }

    [Fact(DisplayName = "an account under two names is counted once, follows its conversations, and lends under its second name when the first cannot")]
    public async Task TwoNames()
    {
        using var conn = await Connection();
        const string plain = """{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"hi"}]}""";
        const string reasoned = """{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"hi"},{"role":"assistant","content":[{"type":"thinking","thinking":"t","signature":"s"}]},{"role":"user","content":"and?"}]}""";
        Account("tok-S", 1.0 / 48);
        Account("tok-X", 1.0 / 96);
        Account("tok-Y", 5);
        Account("tok-W", 1.0 / 144);
        Configure(true, [Member("S", "tok-S", borrows: ["X", "Y"], account: "acct-S", limits: Pushed(TimeSpan.FromDays(1))),
            Member("X", "tok-X", account: "acct-X", limits: Pushed(TimeSpan.FromMinutes(15))), Member("Y", "tok-Y", account: "acct-X", limits: Pushed(TimeSpan.FromMinutes(15)))]);
        Assert.Equal(["tok-X"], (await Ask(conn, "tok-S", plain, session: "s63")).Went);
        await Ask(conn, "tok-S", plain, session: "s64");
        Configure(true, [Member("S", "tok-S", borrows: ["X", "Y"], account: "acct-S", limits: Pushed(TimeSpan.FromMinutes(30))),
            Member("X", "tok-X", lend: false, account: "acct-X", limits: Pushed(TimeSpan.FromDays(5))), Member("Y", "tok-Y", account: "acct-X", limits: Pushed(TimeSpan.FromDays(5)))]);
        Assert.Equal(["tok-Y"], (await Ask(conn, "tok-S", reasoned, session: "s63")).Went);
        Configure(true, [Member("S", "tok-S", borrows: ["W"], account: "acct-S", limits: Pushed(TimeSpan.FromMinutes(30))), Member("W", "tok-W", account: "acct-W", limits: Pushed(TimeSpan.FromMinutes(10)))]);
        Assert.Equal(["tok-S"], (await Ask(conn, "tok-S", reasoned, session: "s64")).Went);

        Account("tok-E", 1.0 / 24);
        Configure(true, [Member("A", "tok-A", borrows: ["B", "E"], account: "acct-A", limits: Pushed(TimeSpan.FromDays(1))),
            Member("B", "tok-B", account: "acct-B", limits: Pushed(TimeSpan.FromDays(4))), Member("E", "tok-E", account: "acct-B", limits: Pushed(TimeSpan.FromDays(4)))],
            new() { ["s19"] = "E" });
        Assert.Equal(["tok-B"], (await Ask(conn, "tok-A", session: "s19")).Went);
        Configure(true, [Member("A", "tok-A", borrows: ["B", "E"], account: "acct-A", limits: Pushed(TimeSpan.FromDays(4))),
            Member("B", "tok-B", lend: false, account: "acct-B", limits: Pushed(TimeSpan.FromDays(1))), Member("E", "tok-E", account: "acct-B", limits: Pushed(TimeSpan.FromDays(1)))]);
        Assert.Equal(["tok-E"], (await Ask(conn, "tok-A", session: "s20")).Went);
    }
}
