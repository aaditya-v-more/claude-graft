using ClaudeGraft.Core;

try
{
    if (args.Length == 2 && args[0] == "--test-data-root")
    {
        var root = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(root, ".graft-test-root"))) return 2;
        GraftPaths.ProfilesRootOverride = root;
    }
    else if (args.Length != 0) return 2;
    var binary = PoolControl.BundleDigest(AppContext.BaseDirectory);
    await using var relay = new PoolRelayServer(binary);
    await relay.RunAsync();
    return 0;
}
catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
{
    // Another relay may already own the listener. Do not log tokens, bodies,
    // certificate material, or underlying network exception messages.
    return 1;
}
