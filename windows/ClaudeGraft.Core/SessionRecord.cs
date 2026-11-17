using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public static class SessionRecord
{
    /// The record that puts a session back in a sidebar, shaped like the ones
    /// Claude Desktop writes for the sessions it owns.
    ///
    /// The bridge id is the one thing renamed on the way: the transcript calls
    /// it cse_…, the record calls the same id session_… — measured against a
    /// session that had both written down. The record's name is fixed by the
    /// session too, local_&lt;cliSessionId&gt;, so a later pass can only ever
    /// find the record where this one put it, and the marker a delete leaves
    /// behind names the session by the same id.
    public static JsonObject For(SessionFacts facts)
    {
        var bridges = new JsonArray();
        foreach (var id in facts.BridgeIds)
            bridges.Add(id.StartsWith("cse_", StringComparison.Ordinal) ? "session_" + id[4..] : id);

        var record = new JsonObject
        {
            ["sessionId"] = $"local_{facts.CliSessionId}",
            ["cliSessionId"] = facts.CliSessionId,
            ["cwd"] = facts.Cwd,
            ["originCwd"] = facts.Cwd,
            ["lastFocusedAt"] = facts.LastActivityAt,
            ["createdAt"] = facts.CreatedAt,
            ["lastActivityAt"] = facts.LastActivityAt,
            ["model"] = facts.Model,
            ["effort"] = facts.Effort,
            ["isArchived"] = false,
            ["title"] = facts.Title,
            ["titleSource"] = "auto",
            ["permissionMode"] = facts.PermissionMode,
            // A record written by the desktop carries its MCP server config here.
            // Nothing the transcript holds can recover that, and an empty list is
            // what a session sees when none is configured.
            ["remoteMcpServersConfig"] = new JsonArray(),
            ["chromePermissionMode"] = "skip_all_permission_checks",
            ["completedTurns"] = facts.Prompts,
            ["lastSpawnRootDetected"] = false,
            ["bridgeSessionIds"] = bridges,
            ["remoteControlAutoEligible"] = true,
            ["alwaysAllowedReasons"] = new JsonArray(),
            ["sessionPermissionUpdates"] = new JsonArray(),
            ["classifierSummaryEnabled"] = true,
            ["reportFindingsCard"] = true,
            ["spawnSeed"] = new JsonObject(),
        };
        if (facts.Branches.Count > 0)
        {
            var written = new JsonArray();
            foreach (var b in facts.Branches) written.Add(b);
            record["writtenBranches"] = written;
        }
        return record;
    }

    public static string Serialize(JsonObject record) =>
        record.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
