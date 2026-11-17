using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// Profiles here are folders in a throwaway directory sealed with a key made
/// for the test; nothing reaches a real Claude or its key.
[Collection("GlobalState")]
public sealed class PoolEnvironmentTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public PoolEnvironmentTests() => GraftPaths.ProfilesRootOverride = _tmp.Path;

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _tmp.Dispose();
    }

    private string Profile(string name, IReadOnlyDictionary<string, string>? env = null, IReadOnlyDictionary<string, string>? scoped = null)
    {
        var dir = _tmp.Dir(name);
        var outer = new JsonObject { ["kept"] = 1 };
        if (env is not null) outer["envVars"] = ClaudeCodeEnvironment.Seal(env, _key);
        if (scoped is not null)
        {
            var nested = new JsonObject { [":machine:C:/work"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(scoped)) };
            outer["scopedEnvVars"] = Convert.ToBase64String(ClaudeCredentials.Seal(Encoding.UTF8.GetBytes(nested.ToJsonString()), _key));
        }
        File.WriteAllText(Path.Combine(dir, PoolEnvironment.FileName), outer.ToJsonString());
        return dir;
    }

    private Dictionary<string, string> Env(string profile)
    {
        if (PoolEnvironment.Read(profile).Text is not string text) return new();
        return ClaudeCodeEnvironment.Unseal(text, _key)!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
    }

    private Dictionary<string, ClaudeCodeEnvironment.State> Settle(Dictionary<string, bool> wanted, bool relayReady = true, Func<string, bool>? running = null) =>
        ClaudeCodeEnvironment.Settle(wanted, _ => _key, "C:\\nowhere\\GraftRelay.exe", relayReady, running ?? (_ => false));

    [Fact(DisplayName = "a borrowing Claude that is closed gets the two lines beside what it already held")]
    public void LinesGoIn()
    {
        var p = Profile("Claude-2", new Dictionary<string, string> { ["FOO"] = "bar" });
        var states = Settle(new() { [p] = true });
        Assert.IsType<ClaudeCodeEnvironment.State.On>(states[PoolEnvironment.Key(p)]);
        var env = Env(p);
        Assert.Equal("bar", env["FOO"]);
        Assert.Equal(PoolEnvironment.ProxyValue, env["HTTPS_PROXY"]);
        Assert.Equal(PoolEnvironment.CertificateValue, env["NODE_EXTRA_CA_CERTS"]);
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(Path.Combine(p, PoolEnvironment.FileName)))!["kept"]!.GetValue<int>());
    }

    [Fact(DisplayName = "and comes back out as it was once it stops borrowing, the journal forgetting it")]
    public void LinesComeOut()
    {
        var p = Profile("Claude-2");
        Settle(new() { [p] = true });
        var states = Settle(new() { [p] = false });
        Assert.IsType<ClaudeCodeEnvironment.State.Off>(states[PoolEnvironment.Key(p)]);
        Assert.IsType<PoolEnvironment.FieldValue.Absent>(PoolEnvironment.Read(p));
        Assert.Empty(PoolEnvironment.ReadJournal().Profiles);
    }

    [Fact(DisplayName = "a Claude that is open is left alone and waits, and one that stops borrowing while open is leaving")]
    public void OpenWaits()
    {
        var p = Profile("Claude-2");
        Assert.IsType<ClaudeCodeEnvironment.State.Waiting>(Settle(new() { [p] = true }, running: _ => true)[PoolEnvironment.Key(p)]);
        Assert.IsType<PoolEnvironment.FieldValue.Absent>(PoolEnvironment.Read(p));
        Settle(new() { [p] = true });
        Assert.IsType<ClaudeCodeEnvironment.State.Leaving>(Settle(new() { [p] = false }, running: _ => true)[PoolEnvironment.Key(p)]);
        Assert.Contains("HTTPS_PROXY", Env(p).Keys);
    }

    [Fact(DisplayName = "no relay, no lines: a Claude never points at a proxy that is not there")]
    public void NoRelayNoLines()
    {
        var p = Profile("Claude-2");
        Assert.IsType<ClaudeCodeEnvironment.State.Waiting>(Settle(new() { [p] = true }, relayReady: false)[PoolEnvironment.Key(p)]);
        Assert.IsType<PoolEnvironment.FieldValue.Absent>(PoolEnvironment.Read(p));
    }

    [Fact(DisplayName = "a proxy or certificate the person set is left alone and the lines never go in beside it")]
    public void PersonsOwn()
    {
        var p = Profile("Claude-2", new Dictionary<string, string> { ["https_proxy"] = "http://corp:8080" });
        var state = Settle(new() { [p] = true })[PoolEnvironment.Key(p)];
        Assert.Equal(new ClaudeCodeEnvironment.State.Conflict("HTTPS_PROXY", false), state);
        Assert.Equal(new Dictionary<string, string> { ["https_proxy"] = "http://corp:8080" }, Env(p));
    }

    [Fact(DisplayName = "a line of Graft's beside a value of the person's own still comes out")]
    public void GraftsLineBesideTheirs()
    {
        var p = Profile("Claude-2", new Dictionary<string, string> { ["HTTPS_PROXY"] = PoolEnvironment.ProxyValue, ["NODE_EXTRA_CA_CERTS"] = "C:\\mine.pem" });
        Settle(new() { [p] = true });
        Assert.Equal(new Dictionary<string, string> { ["NODE_EXTRA_CA_CERTS"] = "C:\\mine.pem" }, Env(p));
    }

    [Fact(DisplayName = "copies of the lines a folder inherited are taken out, and a scoped proxy of the person's own blocks the lines")]
    public void Scoped()
    {
        var p = Profile("Claude-2", scoped: new Dictionary<string, string> { ["HTTPS_PROXY"] = PoolEnvironment.ProxyValue, ["KEEP"] = "1" });
        Assert.True(ClaudeCodeEnvironment.ScopedCopies(p, _key));
        Settle(new() { [p] = false });
        Assert.False(ClaudeCodeEnvironment.ScopedCopies(p, _key));
        var q = Profile("Claude-3", scoped: new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://corp:8080" });
        Assert.IsType<ClaudeCodeEnvironment.State.Conflict>(Settle(new() { [q] = true })[PoolEnvironment.Key(q)]);
    }

    [Fact(DisplayName = "a launcher swaps only the exact value the journal expects; an edit Graft has not read is left for Graft")]
    public void LauncherSwap()
    {
        var p = Profile("Claude-2", new Dictionary<string, string> { ["FOO"] = "bar" });
        Settle(new() { [p] = true }, relayReady: false);
        Assert.False(Env(p).ContainsKey("HTTPS_PROXY"));
        PoolEnvironment.SettleBeforeOpening(p, () => true);
        Assert.True(Env(p).ContainsKey("HTTPS_PROXY"));
        PoolEnvironment.SettleBeforeOpening(p, () => false);
        Assert.False(Env(p).ContainsKey("HTTPS_PROXY"));

        PoolEnvironment.Write(ClaudeCodeEnvironment.Seal(new Dictionary<string, string> { ["FOO"] = "edited" }, _key), p);
        PoolEnvironment.SettleBeforeOpening(p, () => true);
        Assert.Equal(new Dictionary<string, string> { ["FOO"] = "edited" }, Env(p));
    }

    [Fact(DisplayName = "a file Claude is part way through writing is neither read nor written")]
    public void MidWrite()
    {
        var p = Profile("Claude-2");
        File.WriteAllText(Path.Combine(p, PoolEnvironment.FileName + ".journal"), "");
        Assert.IsType<ClaudeCodeEnvironment.State.Unreadable>(Settle(new() { [p] = true })[PoolEnvironment.Key(p)]);
        Assert.False(PoolEnvironment.Write("x", p));
    }

    [Fact(DisplayName = "stopping sharing puts each closed Claude back, leaves an open one, and marks every entry unwanted")]
    public void StopSharing()
    {
        var closed = Profile("Claude-2", new Dictionary<string, string> { ["FOO"] = "bar" });
        var open = Profile("Claude-3");
        Settle(new() { [closed] = true, [open] = true });
        var left = PoolEnvironment.TakeOutLines(null, path => PoolEnvironment.Key(path) == PoolEnvironment.Key(open));
        Assert.Equal(new Dictionary<string, string> { ["FOO"] = "bar" }, Env(closed));
        Assert.Equal([PoolEnvironment.Key(open)], left);
        Assert.All(PoolEnvironment.ReadJournal().Profiles.Values, e => Assert.False(e.Wanted));
        PoolEnvironment.SettleBeforeOpening(closed, () => true);
        Assert.False(Env(closed).ContainsKey("HTTPS_PROXY"));
    }

    [Fact(DisplayName = "the same variables always seal to values that read back alike, in a fixed order")]
    public void SealsInOrder()
    {
        var sealedValue = ClaudeCodeEnvironment.Seal(new Dictionary<string, string> { ["b"] = "2", ["a"] = "C:\\x" }, _key);
        Assert.Equal("""{"a":"C:\\x","b":"2"}""", Encoding.UTF8.GetString(ClaudeCredentials.Decrypt(Convert.FromBase64String(sealedValue), _key)!));
    }
}

/// The relay left running after Graft is uninstalled, run as its own process.
[Collection("GlobalState")]
public sealed class RelayRemovalTests : IDisposable
{
    private const string KeyName = "Claude Graft relay (removal test)";
    private readonly TempDir _tmp = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public RelayRemovalTests() => GraftPaths.ProfilesRootOverride = _tmp.Path;

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        if (OperatingSystem.IsWindows()) RelayCertificates.DeleteKey(KeyName);
        _tmp.Dispose();
    }

    [Fact(DisplayName = "once Graft's install has gone, the relay puts each closed Claude back and leaves, taking its key")]
    public void LeavesAfterUninstall()
    {
        if (!OperatingSystem.IsWindows()) return;
        var install = _tmp.Write("install/relay/GraftRelay.exe", "stand-in");
        var profile = _tmp.Dir("Claude-2");
        var plain = ClaudeCodeEnvironment.Seal(new Dictionary<string, string> { ["FOO"] = "bar" }, _key);
        var pooled = ClaudeCodeEnvironment.Seal(new Dictionary<string, string> { ["FOO"] = "bar", ["HTTPS_PROXY"] = PoolEnvironment.ProxyValue }, _key);
        PoolEnvironment.Write(pooled, profile);
        PoolEnvironment.WriteJournal(new PoolEnvironment.Journal
        {
            Relay = install,
            Profiles = new(StringComparer.OrdinalIgnoreCase) { [PoolEnvironment.Key(profile)] = new() { Wanted = true, Plain = plain, Pooled = pooled } },
        });

        var exe = Directory.EnumerateFiles(Path.Combine(Root(), "GraftRelay", "bin"), "GraftRelay.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var relay = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "--port", port.ToString(), "--pipe", "ClaudeGraft.Relay.test." + Guid.NewGuid().ToString("N"),
                             "--key-name", KeyName, "--support", _tmp.Path, "--removal-check", "0.3" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            Thread.Sleep(1500);
            Assert.False(relay.HasExited);
            Assert.Equal(pooled, PoolEnvironment.Read(profile).Text);

            File.Delete(install);
            Assert.True(relay.WaitForExit(15000));
            Assert.Equal(plain, PoolEnvironment.Read(profile).Text);
            Assert.False(CngKeyExists());
        }
        finally { if (!relay.HasExited) relay.Kill(); }
    }

    private static bool CngKeyExists() =>
        OperatingSystem.IsWindows() && CngKey.Exists(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "GraftRelay"))) dir = dir.Parent;
        return dir!.FullName;
    }
}

public class InheritedLinesTests
{
    [Fact(DisplayName = "a process started from a borrowing Claude drops Graft's lines and keeps a person's own proxy")]
    public void DropsOnlyGraftsOwnValues()
    {
        var saved = (Environment.GetEnvironmentVariable("HTTPS_PROXY"), Environment.GetEnvironmentVariable("NODE_EXTRA_CA_CERTS"));
        try
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", PoolEnvironment.ProxyValue);
            Environment.SetEnvironmentVariable("NODE_EXTRA_CA_CERTS", @"C:\certs\mine.pem");
            PoolEnvironment.ForgetInheritedLines();
            Assert.Null(Environment.GetEnvironmentVariable("HTTPS_PROXY"));
            Assert.Equal(@"C:\certs\mine.pem", Environment.GetEnvironmentVariable("NODE_EXTRA_CA_CERTS"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", saved.Item1);
            Environment.SetEnvironmentVariable("NODE_EXTRA_CA_CERTS", saved.Item2);
        }
    }
}
