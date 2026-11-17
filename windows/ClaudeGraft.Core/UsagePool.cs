using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// <summary>
/// Shared usage's fixed points: where the relay listens, where its files live,
/// and the door Graft talks to it through.
/// </summary>
public static class UsagePool
{
    /// Fixed, because a session reads it once as it starts; a port that moved
    /// would leave every session already running pointing at nothing.
    public const int Port = 47823;
    public const string Host = "api.anthropic.com";

    public static string Directory => Path.Combine(GraftPaths.OwnData, "pool");
    public static string CertificateFile => Path.Combine(Directory, "relay-ca.pem");
    public static string LeafFile => Path.Combine(Directory, "relay-leaf.pem");

    /// Where the relay runs from: a copy per version outside the install
    /// folder, so an install can replace Graft's files while it keeps running.
    public static string RelayHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeGraft", "relay");

    /// SChannel will not serve a key held only in memory, so the leaf's key is
    /// one named key in the user's own store, replaced when the leaf renews.
    public const string KeyName = "Claude Graft relay";

    /// Held open with no sharing for as long as a relay answers: one at a time,
    /// and the one stepping aside lets go before it replies.
    public static string LockFile => Path.Combine(Directory, "relay.lock");

    /// Pipes share one namespace across users; the SID keeps them apart, and
    /// the pipe's own ACL keeps anybody else out.
    public static string PipeName =>
        "ClaudeGraft.Relay." + (OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value : Environment.UserName);
}

public sealed record RelayRequest
{
    public PoolConfiguration? Configure { get; init; }
    /// Asks a relay built from a different binary to step aside: it stops
    /// listening at once and leaves when its last exchange finishes.
    public bool? Quit { get; init; }
}

public sealed record RelayStatus
{
    /// Digest of the executable running, so Graft can tell one left over from
    /// an earlier version.
    public string Binary { get; init; } = "";
    public int Pid { get; init; }
    public DateTimeOffset Started { get; init; }
    public bool Enabled { get; init; }
    public bool Configured { get; init; }
    public IReadOnlyList<PoolMemberStatus> Members { get; init; } = [];
    public IReadOnlyList<PoolRouting> Recent { get; init; } = [];
    /// Most recent first, and only the last day's.
    public IReadOnlyList<PoolSession> Sessions { get; init; } = [];
    public int OpenExchanges { get; init; }
}

public static class RelayWire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// A CONNECT target, host and port; brackets around an IPv6 host.
    public static (string Host, int Port)? Authority(string target)
    {
        string host, port;
        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']');
            if (close < 0 || close + 1 >= target.Length || target[close + 1] != ':') return null;
            host = target[1..close];
            port = target[(close + 2)..];
        }
        else
        {
            var colon = target.LastIndexOf(':');
            if (colon <= 0) return null;
            host = target[..colon];
            port = target[(colon + 1)..];
        }
        return int.TryParse(port, out var p) && p is > 0 and < 65536 && host.Length > 0 ? (host, p) : null;
    }

    /// Only the service on its TLS port is opened; everything else is a tunnel.
    public static bool Intercepts(string host, int port) =>
        port == 443 && host.Equals(UsagePool.Host, StringComparison.OrdinalIgnoreCase);
}
