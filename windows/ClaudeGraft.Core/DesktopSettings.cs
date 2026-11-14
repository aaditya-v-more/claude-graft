using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public static partial class SharedSettings
{
    private const string DesktopConfig = "claude_desktop_config.json";

    // Desktop saves permission choices beside MCP definitions. The old Windows
    // journal reconciled the whole file in both directions, so its normal
    // Restore would send the borrower's edits back to the source one last time.
    internal static void KeepDesktopSettingsLocal(string profile, string? source)
    {
        var destination = Path.Combine(profile, DesktopConfig);
        var journal = StatePath(destination);
        var baseline = ReadOptional(journal);
        if (source is null && baseline is null) return;
        if (Junction.IsLink(destination))
            throw new IOException("Restore the profile's desktop settings to a local file before sharing.");

        try
        {
            var active = ReadOptional(destination);
            var local = active;
            if (baseline is not null)
            {
                var state = JsonSerializer.Deserialize<State>(baseline,
                    new JsonSerializerOptions { RespectRequiredConstructorParameters = true });
                if (state is null || string.IsNullOrEmpty(state.Source) || string.IsNullOrEmpty(state.Digest))
                    throw new JsonException("Incomplete settings journal.");
                local = state.HadOwn
                    ? ReadOptional(Graft.StashPath(destination)) ?? throw new IOException("The original desktop settings stash is missing.")
                    : null;
            }

            var own = ReadDesktopConfig(local);
            var from = source is null ? null : ReadOptional(Path.Combine(source, DesktopConfig));
            var borrowed = ReadDesktopConfig(from);
            var servers = (JsonObject?)own["mcpServers"];
            var added = false;
            if (borrowed["mcpServers"] is JsonObject definitions)
            {
                foreach (var (name, definition) in definitions)
                {
                    if (servers?.ContainsKey(name) == true) continue;
                    if (servers is null) own["mcpServers"] = servers = new JsonObject();
                    servers.Add(name, definition?.DeepClone());
                    added = true;
                }
            }

            if (!added && baseline is null) return;
            var result = added || local is null ? JsonSerializer.SerializeToUtf8Bytes(own) : local;
            if (active is null || !active.AsSpan().SequenceEqual(result))
            {
                if (baseline is not null && active is not null)
                {
                    // A later edit to a whole-file share cannot be attributed
                    // to either profile. Keep the old live copy for recovery.
                    var backup = destination + ".graft-before-local-" + Graft.Digest(active);
                    var previous = ReadOptional(backup);
                    if (previous is null) AtomicWrite.Bytes(backup, active);
                    else if (!active.AsSpan().SequenceEqual(previous)) throw new IOException("The desktop settings backup already contains different data.");
                }
                AtomicWrite.Bytes(destination, result);
            }
            if (baseline is not null) File.Delete(journal);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Diagnostics.Note("settings.unread", new Dictionary<string, object?> { ["profile"] = profile });
            if (baseline is not null)
                throw new IOException("Could not restore this profile's own desktop settings. Check its settings files and retry.", error);
        }
    }

    private static JsonObject ReadDesktopConfig(byte[]? bytes)
    {
        if (bytes is null) return new JsonObject();
        if (JsonNode.Parse(bytes) is not JsonObject config ||
            (config.ContainsKey("mcpServers") && config["mcpServers"] is not JsonObject))
            throw new JsonException("Invalid desktop settings.");
        return config;
    }

    private static byte[]? ReadOptional(string path)
    {
        if (Junction.IsLink(path)) throw new IOException("Desktop settings must be local files.");
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
