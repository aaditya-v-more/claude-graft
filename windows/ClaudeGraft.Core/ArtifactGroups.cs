using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public sealed class ArtifactGroupStore
{
    private readonly string _file;
    public bool CanWrite { get; private set; } = true;
    public string? Notice { get; private set; }
    public List<ArtifactGroup> Groups { get; } = new();
    public ArtifactGroupStore(string? file = null)
    {
        _file = file ?? Path.Combine(GraftPaths.OwnData, "artifact-groups.json");
        if (!File.Exists(_file)) return;
        var lost = false;
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(_file)) is not JsonArray list) throw new IOException();
            foreach (var row in list)
            {
                if (row is not JsonObject obj || !Guid.TryParse(ArtifactApi.Text(obj["id"]), out var id)
                    || ArtifactApi.Text(obj["name"]) is not string name || obj["emails"] is not JsonArray emails
                    || emails.Any(e => ArtifactApi.Text(e) is null)) { lost = true; continue; }
                var group = new ArtifactGroup { Id = id, Name = name, Emails = emails.Select(ArtifactApi.Text).OfType<string>().ToList() };
                if (obj["roles"] is JsonObject roles)
                    foreach (var (email, value) in roles)
                        if (Enum.TryParse<ArtifactRole>(ArtifactApi.Text(value), true, out var role) && Enum.IsDefined(role)) group.Roles[email] = role;
                Groups.Add(group);
            }
        }
        catch { lost = true; }
        if (!lost) return;
        try
        {
            var backup = Path.Combine(Path.GetDirectoryName(_file)!, Path.GetFileNameWithoutExtension(_file)
                + ".unreadable-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.Move(_file, backup);
            Notice = "Some saved groups could not be read. The original file was kept beside the new one.";
            Save();
        }
        catch { CanWrite = false; Notice = "The saved groups could not be read or backed up. Group changes cannot be saved yet."; }
    }

    public void Save()
    {
        if (!CanWrite) throw new IOException(Notice);
        var list = new JsonArray();
        foreach (var group in Groups)
        {
            var row = new JsonObject { ["id"] = group.Id.ToString(), ["name"] = group.Name,
                ["emails"] = new JsonArray(group.Emails.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) };
            var roles = new JsonObject();
            foreach (var email in group.Emails)
                if (group.RoleOf(email) != ArtifactRole.Reader) roles[email] = group.RoleOf(email).ToString().ToLowerInvariant();
            if (roles.Count != 0) row["roles"] = roles;
            list.Add(row);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        AtomicWrite.Bytes(_file, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(list));
    }
}
