using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public static partial class Graft
{
    /// Give a profile the source's MCP server definitions without touching its
    /// permission choices, which live in the same
    /// <c>claude_desktop_config.json</c>. On Windows this file was never shared at
    /// all — a graft links with a junction, and a junction cannot point at a file
    /// — so the copy is additive: only the servers this profile lacks are merged
    /// into its own real file, and every permission grant and existing definition,
    /// on both sides, is left as it was.
    public static void CopyDesktopServers(string source, string profile)
    {
        if (Fs.SamePath(source, profile)) return;
        const string name = "claude_desktop_config.json";
        var destination = Path.Combine(profile, name);
        // An older build shared this file, which on Windows left a broken junction
        // the profile's Claude cannot read. Restore its own copy before merging.
        RestoreOwnFile(destination);

        // Missing reads as empty to merge into; unparseable (caught mid-write)
        // reads as null so the caller bails rather than clobbering it. mcpServers
        // must be an object where present, or the merge has nowhere to go.
        static JsonObject? Read(string path)
        {
            if (!Fs.Exists(path)) return new JsonObject();
            try
            {
                if (JsonNode.Parse(File.ReadAllBytes(path)) is not JsonObject node) return null;
                if (node["mcpServers"] is not (null or JsonObject)) return null;
                return node;
            }
            catch { return null; }
        }

        var own = Read(destination);
        var borrowed = Read(Path.Combine(source, name));
        if (own is null || borrowed is null)
        {
            Diagnostics.Note("settings.unread", new Dictionary<string, object?>
            {
                ["profile"] = Path.GetFileName(profile),
            });
            return;
        }

        // An existing object is mutated in place; a node already parented cannot
        // be reassigned to its own key, so a fresh one is only attached when the
        // profile had none.
        var existing = own["mcpServers"] as JsonObject;
        var servers = existing ?? new JsonObject();
        var added = false;
        foreach (var (serverName, definition) in (JsonObject)(borrowed["mcpServers"] ?? new JsonObject()))
            if (servers[serverName] is null)
            {
                servers[serverName] = definition?.DeepClone();
                added = true;
            }
        if (!added) return;
        if (existing is null) own["mcpServers"] = servers;

        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(own, new JsonSerializerOptions { WriteIndented = true });
            AtomicWrite.Bytes(destination, data);
            Diagnostics.Note("settings.local", new Dictionary<string, object?>
            {
                ["profile"] = Path.GetFileName(profile),
            });
        }
        catch (Exception e)
        {
            Diagnostics.Note("settings.failed", new Dictionary<string, object?>
            {
                ["profile"] = Path.GetFileName(profile), ["error"] = e.Message,
            });
        }
    }
}
