using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public class PoolRelayTests
{
    private static readonly byte[] RequestBody = "{\"model\":\"claude-test\",\"messages\":[{\"role\":\"user\",\"content\":\"fixture\"}]}"u8.ToArray();
    private static PoolMember Member(string id, string token, int resetHours, params string[] lenders) => new(id, id, id,
        [UsagePool.Digest(token)], token, new(0, null, 0, DateTimeOffset.UtcNow.AddHours(resetHours), null, DateTimeOffset.UtcNow), lenders);
    private static PoolState State(int ownReset = 10, Dictionary<string, string>? assignments = null)
    {
        var state = new PoolState(); state.Configure(new(true,
            [Member("own", "sender-token", ownReset, "lent", "other"), Member("lent", "lending-token", 1), Member("other", "other-token", 2)], assignments), DateTimeOffset.UtcNow); return state;
    }
    private static byte[] Request(string path = "/v1/messages", byte[]? body = null)
    {
        body ??= RequestBody;
        return [.. Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer sender-token\r\nX-Claude-Code-Session-Id: fixture-session\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), .. body];
    }
    private static async Task Answer(PoolWire wire, int status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await wire.SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), ct);
        await wire.SendAsync(bytes, ct);
    }
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TcpListener inbound = new(IPAddress.Loopback, 0);
        private readonly TcpListener upstream = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        private readonly List<Task> upstreamTasks = [];
        private readonly Task service, exchange;
        private readonly TcpClient client;
        public PoolWire Wire { get; }
        public CancellationToken Cancellation => stop.Token;
        private Harness(PoolState state, Func<PoolWire, CancellationToken, Task> handler)
        {
            inbound.Start(); upstream.Start();
            service = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var tcp = await upstream.AcceptTcpClientAsync(stop.Token);
                        var task = Task.Run(async () => { using (tcp) using (var wire = new PoolWire(tcp.GetStream())) await handler(wire, stop.Token); });
                        lock (upstreamTasks) upstreamTasks.Add(task);
                    }
                }
                catch (OperationCanceledException) { }
            });
            exchange = Task.Run(async () =>
            {
                using var tcp = await inbound.AcceptTcpClientAsync(stop.Token); using var wire = new PoolWire(tcp.GetStream());
                await new PoolExchange(wire, state, async ct =>
                {
                    var remote = new TcpClient(); await remote.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)upstream.LocalEndpoint).Port, ct); return new PoolWire(remote.GetStream());
                }).RunAsync(stop.Token);
            });
            client = new TcpClient(); client.Connect(IPAddress.Loopback, ((IPEndPoint)inbound.LocalEndpoint).Port); Wire = new(client.GetStream());
        }
        public static Harness Start(PoolState state, Func<PoolWire, CancellationToken, Task> handler) => new(state, handler);
        public async ValueTask DisposeAsync()
        {
            Wire.Dispose(); client.Dispose(); stop.Cancel(); inbound.Stop(); upstream.Stop();
            try { await Task.WhenAll(service, exchange); } catch (OperationCanceledException) { }
            Task[] pending; lock (upstreamTasks) pending = upstreamTasks.ToArray();
            try { await Task.WhenAll(pending); } catch (OperationCanceledException) { }
            stop.Dispose();
        }
    }
    [Fact] public async Task ExhaustedBorrowerRetriesAndStreamsSenderAnswer()
    {
        var seen = new List<string?>(); var state = State();
        await using var fixture = Harness.Start(state, async (wire, ct) =>
        {
            var head = (await wire.HeadAsync(ct))!; var token = PoolRequest.Bearer(head.Fields); lock (seen) seen.Add(token);
            Assert.Equal(RequestBody, await wire.BodyAsync(head.RequestBody(), 65536, ct));
            await Answer(wire, token == "lending-token" ? 429 : 200, token == "lending-token" ? "full" : "answer", ct);
        });
        await fixture.Wire.SendAsync(Request(), fixture.Cancellation);
        var answer = (await fixture.Wire.HeadAsync(fixture.Cancellation))!;
        Assert.Equal(200, answer.Status); Assert.Equal("answer", Encoding.UTF8.GetString(await fixture.Wire.BodyAsync(answer.ResponseBody("POST"), 65536, fixture.Cancellation)));
        Assert.Equal(new[] { "lending-token", "other-token" }, seen);
        var session = Assert.Single(state.Status("binary", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow).Sessions);
        Assert.Equal("other", session.ServedBy); Assert.Equal("full", Assert.Single(session.PassedOver).Reason);
    }
    [Fact] public async Task BorrowedPlanRefusalReturnsDirectlyToSender()
    {
        var seen = new List<string?>();
        await using var fixture = Harness.Start(State(), async (wire, ct) =>
        {
            var head = (await wire.HeadAsync(ct))!; var token = PoolRequest.Bearer(head.Fields); lock (seen) seen.Add(token);
            await wire.BodyAsync(head.RequestBody(), 65536, ct); await Answer(wire, token == "lending-token" ? 403 : 200, "ok", ct);
        });
        await fixture.Wire.SendAsync(Request(), fixture.Cancellation); var answer = (await fixture.Wire.HeadAsync(fixture.Cancellation))!;
        Assert.Equal(200, answer.Status); await fixture.Wire.BodyAsync(answer.ResponseBody("POST"), 65536, fixture.Cancellation);
        Assert.Equal(new[] { "lending-token", "sender-token" }, seen);
    }
    [Fact] public async Task FinalRefusalAlwaysBelongsToTheSenderEvenIfItRankedFirst()
    {
        var seen = new List<string?>(); var state = State(1);
        await using var fixture = Harness.Start(state, async (wire, ct) =>
        {
            var head = (await wire.HeadAsync(ct))!; var token = PoolRequest.Bearer(head.Fields); lock (seen) seen.Add(token);
            await wire.BodyAsync(head.RequestBody(), 65536, ct); await Answer(wire, token == "sender-token" ? 429 : 401, token == "sender-token" ? "sender-limit" : "lender-login", ct);
        });
        await fixture.Wire.SendAsync(Request(), fixture.Cancellation); var answer = (await fixture.Wire.HeadAsync(fixture.Cancellation))!;
        Assert.Equal(429, answer.Status); Assert.Equal("sender-limit", Encoding.UTF8.GetString(await fixture.Wire.BodyAsync(answer.ResponseBody("POST"), 65536, fixture.Cancellation)));
        Assert.Equal(new[] { "sender-token", "lending-token", "other-token", "sender-token" }, seen);
    }
    [Fact] public async Task ThreadMissingReachesClaudeWithoutUndoingAnExplicitMove()
    {
        var state = State(assignments: new() { ["fixture-session"] = "lent" }); var count = 0;
        await using var fixture = Harness.Start(state, async (wire, ct) =>
        {
            var head = (await wire.HeadAsync(ct))!; Interlocked.Increment(ref count);
            Assert.Equal("lending-token", PoolRequest.Bearer(head.Fields)); Assert.Equal("identity", head.Value("accept-encoding"));
            await wire.BodyAsync(head.RequestBody(), 65536, ct); await Answer(wire, 404, "{\"error\":{\"type\":\"thread_not_found\"}}", ct);
        });
        await fixture.Wire.SendAsync(Request(body: "{\"thread\":{\"type\":\"continue\"}}"u8.ToArray()), fixture.Cancellation);
        var answer = (await fixture.Wire.HeadAsync(fixture.Cancellation))!; Assert.Equal(404, answer.Status);
        Assert.Contains("thread_not_found", Encoding.UTF8.GetString(await fixture.Wire.BodyAsync(answer.ResponseBody("POST"), 65536, fixture.Cancellation)));
        Assert.Equal(1, count); Assert.Equal("lent", state.Plan("own", "fixture-session", false, true, DateTimeOffset.UtcNow)[0].Member);
    }
    [Fact] public async Task ArtifactRequestPreservesTheExactHeadAndSender()
    {
        var request = Request("/api/artifacts"); byte[]? received = null;
        await using var fixture = Harness.Start(State(), async (wire, ct) =>
        {
            var head = (await wire.HeadAsync(ct))!; received = [.. head.Raw, .. await wire.BodyAsync(head.RequestBody(), 65536, ct)]; await Answer(wire, 200, "artifact", ct);
        });
        await fixture.Wire.SendAsync(request, fixture.Cancellation); var answer = (await fixture.Wire.HeadAsync(fixture.Cancellation))!;
        await fixture.Wire.BodyAsync(answer.ResponseBody("POST"), 65536, fixture.Cancellation); Assert.Equal(request, received);
    }
    [Fact] public async Task ChunkedRemoteControlRespondsBeforeTheUploadEnds()
    {
        await using var fixture = Harness.Start(State(), async (wire, ct) =>
        {
            var head = (await wire.HeadAsync(ct))!; Assert.Equal("sender-token", PoolRequest.Bearer(head.Fields));
            await wire.SendAsync("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5\r\nearly\r\n"u8.ToArray(), ct);
            Assert.Equal("firstlast", Encoding.ASCII.GetString(await wire.BodyAsync(head.RequestBody(), 65536, ct)));
            await wire.SendAsync("0\r\n\r\n"u8.ToArray(), ct);
        });
        await fixture.Wire.SendAsync("POST /api/remote-control HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer sender-token\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5\r\nfirst\r\n"u8.ToArray(), fixture.Cancellation);
        var head = await fixture.Wire.HeadAsync(fixture.Cancellation); Assert.Equal(200, head!.Status);
        Assert.Equal("5\r\n", Encoding.ASCII.GetString((await fixture.Wire.LineAsync(100, fixture.Cancellation))!));
        Assert.Equal("early\r\n", Encoding.ASCII.GetString((await fixture.Wire.LineAsync(100, fixture.Cancellation))!));
        await fixture.Wire.SendAsync("4\r\nlast\r\n0\r\n\r\n"u8.ToArray(), fixture.Cancellation);
        Assert.Equal("0\r\n", Encoding.ASCII.GetString((await fixture.Wire.LineAsync(100, fixture.Cancellation))!));
    }
    [Fact] public async Task WebSocketUpgradePreservesBothDirections()
    {
        await using var fixture = Harness.Start(State(), async (wire, ct) =>
        {
            var head = (await wire.HeadAsync(ct))!; Assert.True(head.Upgrade); Assert.Equal("sender-token", PoolRequest.Bearer(head.Fields));
            await wire.SendAsync("HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\nserver\r\n"u8.ToArray(), ct);
            Assert.Equal("client\r\n", Encoding.ASCII.GetString((await wire.LineAsync(100, ct))!));
        });
        await fixture.Wire.SendAsync("GET /api/remote-control HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer sender-token\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n"u8.ToArray(), fixture.Cancellation);
        Assert.Equal(101, (await fixture.Wire.HeadAsync(fixture.Cancellation))!.Status);
        Assert.Equal("server\r\n", Encoding.ASCII.GetString((await fixture.Wire.LineAsync(100, fixture.Cancellation))!));
        await fixture.Wire.SendAsync("client\r\n"u8.ToArray(), fixture.Cancellation);
    }
    [Fact] public async Task RealTlsAndSameUserControlUseOnlyDisposablePerProcessTrust()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir(); var previous = GraftPaths.ProfilesRootOverride; GraftPaths.ProfilesRootOverride = temp.Path;
        try
        {
            await using var server = new PoolRelayServer("fixture-binary", 0, null, null); server.Start(monitorRemoval: false);
            var status = await PoolControl.SendAsync(new(new(true, [Member("own", "fixture-secret", 1)])));
            Assert.True(status.Configured); Assert.Equal("fixture-binary", status.Binary);
            Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(status, UsagePool.Json));
            using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(UsagePool.CertificateFile));
            Assert.True(ca.Extensions["2.5.29.30"]!.Critical); Assert.False(ca.HasPrivateKey);
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            using var wire = new PoolWire(tcp.GetStream()); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await wire.SendAsync("CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n"u8.ToArray(), timeout.Token);
            Assert.Equal(200, (await wire.HeadAsync(timeout.Token))!.Status);
            using var tls = new SslStream(wire.Stream, true, (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(ca); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                using var leaf = new X509Certificate2(certificate); return chain.Build(leaf);
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = UsagePool.Host, EnabledSslProtocols = SslProtocols.Tls12, ApplicationProtocols = [SslApplicationProtocol.Http11] }, timeout.Token);
            Assert.True(tls.IsAuthenticated);
            var identity = File.ReadAllBytes(UsagePool.IdentityFile); Assert.DoesNotContain("PRIVATE KEY", Encoding.ASCII.GetString(identity));
            await PoolControl.SendAsync(new(Quit: true));
        }
        finally { GraftPaths.ProfilesRootOverride = previous; }
    }
    [Fact] public async Task CorruptJournalDisablesLendingWithoutQuittingTheRelayOrDestroyingRecovery()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir(); var previous = GraftPaths.ProfilesRootOverride; GraftPaths.ProfilesRootOverride = temp.Path;
        try
        {
            await using var server = new PoolRelayServer("fixture-binary", 0, null, null); server.Start(monitorRemoval: false);
            await PoolControl.SendAsync(new(new(true, [Member("own", "fixture-secret", 1)])));
            File.WriteAllText(PoolEnvironment.JournalFile, "{torn");
            await Assert.ThrowsAsync<IOException>(() => PoolControl.StopSharingAsync());
            var status = await PoolControl.SendAsync(new()); Assert.False(status.Enabled); Assert.Empty(status.Members);
            Assert.Equal("{torn", File.ReadAllText(PoolEnvironment.JournalFile));
            Assert.False(PoolSettings.Load().Enabled);
        }
        finally { GraftPaths.ProfilesRootOverride = previous; }
    }
    [Fact] public async Task YieldAcknowledgementLetsTheSuccessorBindBeforeOldDisposal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir(); var previous = GraftPaths.ProfilesRootOverride; GraftPaths.ProfilesRootOverride = temp.Path;
        try
        {
            await using var older = new PoolRelayServer("older", 0, null, null); older.Start(monitorRemoval: false);
            var port = older.Port; await PoolControl.SendAsync(new(Quit: true));
            await using var replacement = new PoolRelayServer("replacement", port, null, null); replacement.Start(monitorRemoval: false);
            var status = await PoolControl.SendAsync(new()); Assert.Equal("replacement", status.Binary); Assert.False(status.Configured);
        }
        finally { GraftPaths.ProfilesRootOverride = previous; }
    }
    [Fact] public async Task RevokedLenderCannotKeepServingWhileAnotherLoginIsUnreadable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir(); var previous = GraftPaths.ProfilesRootOverride; GraftPaths.ProfilesRootOverride = temp.Path;
        try
        {
            await using var relay = new PoolRelayServer("fixture", 0, null, null); relay.Start(monitorRemoval: false);
            await PoolControl.SendAsync(new(new(true, [Member("own", "sender", 5, "revoked", "unreadable"), Member("revoked", "old-token", 1), Member("unreadable", "other-token", 2)])));
            var settings = new PoolSettings { Borrowers = new() { ["own"] = ["revoked", "unreadable"] } };
            settings.SetLender("own", "revoked", false); settings.Save();
            Assert.True(PoolSettings.Load().RevocationPending); // Survives an unsuccessful IPC attempt or manager restart.
            var incomplete = new PoolConfiguration(true, [Member("own", "sender", 5, "unreadable")]);
            var command = PoolDelivery.Command(incomplete, false, PoolSettings.Load().RevocationPending);
            Assert.False(command.Quit); Assert.False(command.Configure!.Enabled);
            var reply = await PoolControl.SendAsync(command); Assert.False(reply.Enabled); Assert.Empty(reply.Members);
            Assert.Null(Assert.Single(relay.State.Plan("own", null, false, false, DateTimeOffset.UtcNow)).Token);
            Assert.True(settings.Acknowledge(command, reply)); settings.Save(); Assert.False(PoolSettings.Load().RevocationPending);
            Assert.False((await PoolControl.SendAsync(new())).Enabled); // The proxy remains reachable for sender traffic.
            var complete = new PoolConfiguration(true, [Member("own", "sender", 5, "unreadable"), Member("unreadable", "renewed-token", 1)]);
            await PoolControl.SendAsync(PoolDelivery.Command(complete, true));
            Assert.Equal("renewed-token", relay.State.Plan("own", null, false, false, DateTimeOffset.UtcNow)[0].Token);
        }
        finally { GraftPaths.ProfilesRootOverride = previous; }
    }
    [Fact] public async Task RemovalTimerRechecksReenabledSettingsBeforeCleaningAnOlderJournal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir(); var previous = GraftPaths.ProfilesRootOverride; GraftPaths.ProfilesRootOverride = temp.Path;
        try
        {
            var owner = temp.Write("manager.exe", "fixture owner"); var profile = temp.Dir("Claude");
            await using var relay = new PoolRelayServer("fixture", 0, null, null); relay.Start(monitorRemoval: false);
            await PoolControl.SendAsync(new(new(true, [Member("own", "sender", 1)])));
            var journal = new PoolEnvironment.Journal { Owner = owner }; journal.Profiles[profile] = new(false, null, "pooled"); PoolEnvironment.WriteJournal(journal);
            var settings = new PoolSettings { Borrowers = new() { ["own"] = ["lender"] } }; settings.Save();
            Assert.False(relay.CheckRemoval()); Assert.True((await PoolControl.SendAsync(new())).Enabled);
            Assert.Equal("pooled", PoolEnvironment.LoadJournal().Profiles[profile].Pooled);
        }
        finally { GraftPaths.ProfilesRootOverride = previous; }
    }
}
