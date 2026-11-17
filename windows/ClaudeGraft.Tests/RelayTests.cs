using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class RelayWireTests
{
    [Theory(DisplayName = "a CONNECT target is read as host and port, brackets and all")]
    [InlineData("api.anthropic.com:443", "api.anthropic.com", 443)]
    [InlineData("[::1]:8443", "::1", 8443)]
    [InlineData("127.0.0.1:80", "127.0.0.1", 80)]
    public void Reads(string target, string host, int port) => Assert.Equal((host, port), RelayWire.Authority(target));

    [Theory(DisplayName = "a target with no usable port is not read at all")]
    [InlineData("api.anthropic.com")]
    [InlineData("api.anthropic.com:0")]
    [InlineData("api.anthropic.com:99999")]
    [InlineData("[::1]")]
    [InlineData(":443")]
    public void Refuses(string target) => Assert.Null(RelayWire.Authority(target));

    [Theory(DisplayName = "only the service on its TLS port is opened; anything else is a tunnel")]
    [InlineData("api.anthropic.com", 443, true)]
    [InlineData("API.Anthropic.com", 443, true)]
    [InlineData("api.anthropic.com", 80, false)]
    [InlineData("claude.ai", 443, false)]
    [InlineData("api.anthropic.com.evil.test", 443, false)]
    public void Intercepts(string host, int port, bool opened) => Assert.Equal(opened, RelayWire.Intercepts(host, port));
}

/// The relay run as its own process over loopback, against a stand-in for the
/// service, on a port and pipe and key of its own: never 47823 or the real key.
[Collection("GlobalState")]
public sealed class RelayLoopbackTests : IDisposable
{
    private const string KeyName = "Claude Graft relay (test)";
    private readonly TempDir _tmp = new();
    private readonly string _pipe = "ClaudeGraft.Relay.test." + Guid.NewGuid().ToString("N");
    private readonly int _port = FreePort();
    private readonly TcpListener _service = new(IPAddress.Loopback, 0);
    private readonly List<Process> _relays = new();

    public RelayLoopbackTests()
    {
        _service.Start();
        _ = Task.Run(StandIn);
    }

    public void Dispose()
    {
        foreach (var p in _relays) { try { if (!p.HasExited) p.Kill(); } catch { } p.Dispose(); }
        _service.Stop();
        if (OperatingSystem.IsWindows()) RelayCertificates.DeleteKey(KeyName);
        GraftPaths.ProfilesRootOverride = null;
        _tmp.Dispose();
    }

    private int ServicePort => ((IPEndPoint)_service.LocalEndpoint).Port;
    private string Pool => Path.Combine(_tmp.Path, "ClaudeGraft", "pool");

    // Answers each request with what it was sent, so a test sees the bytes
    // that crossed the relay.
    private async Task StandIn()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _service.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                using var _ = c;
                var s = c.GetStream();
                var buf = new byte[65536];
                var n = await s.ReadAsync(buf);
                var body = buf.AsSpan(0, n).ToArray();
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(head.Concat(body).ToArray());
            });
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string RelayExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "GraftRelay"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Directory.EnumerateFiles(Path.Combine(dir!.FullName, "GraftRelay", "bin"), "GraftRelay.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private Process Start()
    {
        var p = Process.Start(new ProcessStartInfo(RelayExe())
        {
            ArgumentList = { "--port", _port.ToString(), "--pipe", _pipe, "--key-name", KeyName,
                             "--support", _tmp.Path, "--upstream-loopback-port", ServicePort.ToString() },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        _relays.Add(p);
        return p;
    }

    private RelayStatus? Ask(RelayRequest? command = null) => RelayKeeper.Send(command ?? new RelayRequest(), _pipe, 2000);

    private RelayStatus Up()
    {
        for (var i = 0; i < 100; i++)
        {
            if (Ask() is { } s) return s;
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException("the relay never answered");
    }

    private X509Certificate2 Authority() =>
        X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(Pool, "relay-ca.pem")));

    private async Task<(Stream Stream, X509Certificate2? Served)> Connect(string target, bool tls)
    {
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, _port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n"));
        var reply = await ReadHead(s);
        Assert.StartsWith("HTTP/1.1 200", reply);
        if (!tls) return (s, null);
        using var ca = Authority();
        X509Certificate2? served = null;
        var ssl = new SslStream(s, false, (_, cert, _, _) =>
        {
            served = new X509Certificate2(cert!);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(served);
        });
        await ssl.AuthenticateAsClientAsync(UsagePool.Host);
        return (ssl, served);
    }

    private static async Task<string> ReadHead(Stream s)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await s.ReadAsync(one) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n")) break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task<string> ReadAll(Stream s)
    {
        using var reader = new StreamReader(s);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact(DisplayName = "the service's TLS is opened with the relay's own authority and the request goes across unchanged")]
    public async Task Opens()
    {
        if (!OperatingSystem.IsWindows()) return;
        Start();
        Up();
        var (s, served) = await Connect("api.anthropic.com:443", tls: true);
        Assert.Contains("CN=api.anthropic.com", served!.Subject);
        const string request = "POST /v1/messages HTTP/1.1\r\nHost: api.anthropic.com\r\nContent-Length: 2\r\n\r\n{}";
        await s.WriteAsync(Encoding.ASCII.GetBytes(request));
        var answer = await ReadAll(s);
        Assert.EndsWith(request, answer);
    }

    [Fact(DisplayName = "the authority can vouch for api.anthropic.com alone, and says so critically")]
    public void NameConstrained()
    {
        if (!OperatingSystem.IsWindows()) return;
        Start();
        Up();
        using var ca = Authority();
        var constraint = ca.Extensions["2.5.29.30"];
        Assert.NotNull(constraint);
        Assert.True(constraint!.Critical);
        Assert.Contains("api.anthropic.com", Encoding.ASCII.GetString(constraint.RawData));
        Assert.True(ca.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
    }

    [Fact(DisplayName = "any other host is a tunnel: its bytes go through untouched and nothing is opened")]
    public async Task Tunnels()
    {
        if (!OperatingSystem.IsWindows()) return;
        Start();
        Up();
        var (s, _) = await Connect($"127.0.0.1:{ServicePort}", tls: false);
        await s.WriteAsync("\x16\x03\x01 not really TLS"u8.ToArray());
        Assert.EndsWith("\x16\x03\x01 not really TLS", await ReadAll(s));
    }

    [Fact(DisplayName = "anything but CONNECT or plain http is refused rather than guessed at")]
    public async Task RefusesOrigin()
    {
        if (!OperatingSystem.IsWindows()) return;
        Start();
        Up();
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, _port);
        var s = c.GetStream();
        await s.WriteAsync("GET /v1/models HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray());
        Assert.StartsWith("HTTP/1.1 400", await ReadAll(s));
    }

    [Fact(DisplayName = "a second relay leaves at once and the first goes on answering")]
    public void OneAtATime()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = Start();
        var before = Up();
        var second = Start();
        Assert.True(second.WaitForExit(10000));
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(before.Pid, Up().Pid);
        Assert.False(first.HasExited);
    }

    [Fact(DisplayName = "a relay asked to step aside lets the next start straight away, which keeps the same authority")]
    public void Handover()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = Start();
        Up();
        var authority = File.ReadAllText(Path.Combine(Pool, "relay-ca.pem"));
        Assert.NotNull(Ask(new RelayRequest { Quit = true }));
        Assert.True(first.WaitForExit(10000));
        var second = Start();
        Assert.Equal(second.Id, Up().Pid);
        Assert.Equal(authority, File.ReadAllText(Path.Combine(Pool, "relay-ca.pem")));
    }

    [Fact(DisplayName = "the relay reports the binary it runs and remembers what it was configured with")]
    public void Status()
    {
        if (!OperatingSystem.IsWindows()) return;
        Start();
        var s = Up();
        Assert.Equal(RelayKeeper.Digest(RelayExe()), s.Binary);
        Assert.False(s.Configured);
        var after = Ask(new RelayRequest { Configure = new PoolConfiguration { Enabled = true } })!;
        Assert.True(after.Configured && after.Enabled);
    }
}
