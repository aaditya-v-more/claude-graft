using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public sealed class ArtifactTests
{
    private const string Id = "12345678-1234-1234-1234-123456789abc";

    [Fact(DisplayName = "artifact list requests keep the measured query shape and encode the account and continuation")]
    public void ListRequest()
    {
        Assert.Equal("/api/frame/artifacts?rel=mine&empty=exclude&limit=48&org=team%2Bwork&sort=last_viewed&page_token=a%26b%3D1",
            ArtifactApi.ListPath("mine", "team+work", "a&b=1"));
        Assert.Equal("/api/frame/perm/a%2Fb?org=org", ArtifactApi.Path("a/b", "org", "perm"));
    }

    [Fact(DisplayName = "the artifact owner wins duplicate listings and capabilities remain explicit")]
    public void ListsAndMerge()
    {
        var viewer = Guid.NewGuid(); var owner = Guid.NewGuid();
        var shared = ArtifactApi.Page(JsonNode.Parse("""{"artifacts":[{"id":"same","title":"Viewed","rel":"shared","open_url":"javascript:alert(1)"}],"next_page_token":"next"}"""), viewer, "org")!.Value;
        var mine = ArtifactApi.Page(JsonNode.Parse("""{"artifacts":[{"id":"same","title":"Mine","rel":"mine","renamable":true,"duplicable":true,"duplicate_available":false,"open_url":"https://claude.ai/public/artifacts/a","audience":"org"}]}"""), owner, "org")!.Value;
        Assert.Null(shared.Items.Single().OpenUrl);
        Assert.False(shared.Items.Single().Renamable);
        Assert.Equal("next", shared.Next);
        var merged = Assert.Single(ArtifactApi.Merge(new[] { shared.Items, mine.Items }));
        Assert.Equal(owner, merged.Account); Assert.True(merged.Renamable);
        Assert.False(merged.DuplicateAvailable); Assert.Equal(ArtifactAccess.Organization, merged.Access);
        Assert.NotNull(merged.OpenUrl);
    }

    [Theory(DisplayName = "choosing the existing access sends no change and organization access is distinct")]
    [InlineData("owner", false, false)]
    [InlineData("users", false, false)]
    [InlineData("public", true, false)]
    [InlineData("org", false, true)]
    [InlineData("org", true, true)]
    public void AccessChanges(string mode, bool makePublic, bool changes) =>
        Assert.Equal(changes, ArtifactApi.ChangesAccess(ArtifactApi.Access(mode), makePublic));

    [Fact(DisplayName = "making a link private preserves invitees and retains the permission version")]
    public void PermissionsAndPrivateAccess()
    {
        var permissions = ArtifactApi.Permissions(JsonNode.Parse("""{"version":4,"read":{"mode":"public","users":["one@example.com"]},"writersEnabled":true}"""))!;
        Assert.Equal("4", permissions.Version); Assert.True(permissions.Editors); Assert.False(permissions.Commenters);
        Assert.Equal("users", (string?)ArtifactApi.AccessBody(false, permissions.Invited)["read"]!["mode"]);
        Assert.Equal("one@example.com", (string?)ArtifactApi.AccessBody(false, permissions.Invited)["read"]!["users"]![0]);
        Assert.Equal("owner", (string?)ArtifactApi.AccessBody(false, [])["read"]!["mode"]);
        Assert.False(ArtifactApi.AccessBody(true, permissions.Invited)["read"]!.AsObject().ContainsKey("users"));
    }

    [Theory(DisplayName = "an invitation is never more permissive than the group requested or the artifact supports")]
    [InlineData(ArtifactRole.Editor, false, false, ArtifactRole.Reader)]
    [InlineData(ArtifactRole.Editor, true, false, ArtifactRole.Commenter)]
    [InlineData(ArtifactRole.Editor, false, true, ArtifactRole.Editor)]
    [InlineData(ArtifactRole.Commenter, false, true, ArtifactRole.Reader)]
    [InlineData(ArtifactRole.Reader, true, true, ArtifactRole.Reader)]
    public void RoleDowngrade(ArtifactRole wanted, bool comments, bool edits, ArtifactRole expected) =>
        Assert.Equal(expected, ArtifactApi.Offered(wanted, new(null, [], comments, edits, "owner")));

    [Theory(DisplayName = "export file paths cannot escape, name streams or alias reserved Windows files")]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("folder\\file")]
    [InlineData("file:stream")]
    [InlineData("folder//file")]
    [InlineData("CON.txt")]
    [InlineData("trailing. ")]
    public void RejectsFilePaths(string path) => Assert.False(ArtifactApi.IsSafeFilePath(path));

    [Fact(DisplayName = "export sources retain only safe unique paths and the asset token goes only to its own file host")]
    public void SourceFiles()
    {
        var source = ArtifactApi.Source(JsonNode.Parse("""{"ver":2,"assetToken":"t&a","files":[{"path":"../outside"},{"path":"styles/main.css"},{"path":"INDEX.HTML"}]}"""))!;
        Assert.Equal(new[] { "index.html", "styles/main.css" }, source.Files);
        var url = ArtifactApi.FileUrl(Id, source, "styles/main.css")!;
        Assert.Equal($"https://{Id}.frame.claudeusercontent.com/_f/2/_src/styles/main.css?__frame_t=t%26a", url.AbsoluteUri);
        Assert.True(WebAccountPolicy.IsArtifactFile(url));
        Assert.Null(ArtifactApi.FileUrl("bad.example", source, "index.html"));
        Assert.Null(ArtifactApi.Source(JsonNode.Parse("""{"ver":1}""")));
    }

    [Fact(DisplayName = "mail-header addresses are deduplicated and old or unknown roles remain viewers")]
    public void GroupsRemainCompatible()
    {
        Assert.Equal(new[] { "one@example.com", "two@example.com" }, ArtifactApi.Addresses("One <ONE@example.com>; two@example.com, one@example.com bad@domain"));
        using var t = new TempDir(); var id = Guid.NewGuid();
        var file = t.Write("groups.json", """[{"id":"GROUP_ID","name":"Team","emails":["one@example.com","two@example.com"],"roles":{"two@example.com":"future-role"}}]""".Replace("GROUP_ID", id.ToString()));
        var store = new ArtifactGroupStore(file); var group = Assert.Single(store.Groups);
        Assert.Equal(ArtifactRole.Reader, group.RoleOf("one@example.com")); Assert.Equal(ArtifactRole.Reader, group.RoleOf("two@example.com"));
        store.Save(); Assert.DoesNotContain("roles", File.ReadAllText(file));
        group.SetRoleForEveryone(ArtifactRole.Editor); store.Save();
        Assert.Equal(ArtifactRole.Editor, Assert.Single(new ArtifactGroupStore(file).Groups).CommonRole);
    }

    [Fact(DisplayName = "a damaged groups file is retained before its readable entries are saved")]
    public void KeepsDamagedGroups()
    {
        using var t = new TempDir(); var id = Guid.NewGuid();
        var original = """[{"id":"GROUP_ID","name":"Kept","emails":[]},{"new-shape":true}]""".Replace("GROUP_ID", id.ToString());
        var file = t.Write("groups.json", original);
        var store = new ArtifactGroupStore(file);
        Assert.Single(store.Groups); Assert.True(store.CanWrite); Assert.NotNull(store.Notice);
        var backup = Assert.Single(Directory.GetFiles(t.Path, "groups.unreadable-*.json"));
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Single(new ArtifactGroupStore(file).Groups);
    }

    [Fact(DisplayName = "Word export is an actual OOXML package and source export preserves every downloaded file")]
    public async Task ExportsAreRealDocuments()
    {
        using var t = new TempDir(); var word = Path.Combine(t.Path, "artifact.docx");
        ArtifactExport.WriteWord(word, "<html><body><h1>Rendered</h1></body></html>");
        using (var zip = ZipFile.OpenRead(word))
        {
            Assert.NotNull(zip.GetEntry("word/content.html"));
            using var xml = zip.GetEntry("word/document.xml")!.Open();
            Assert.NotNull(XDocument.Load(xml).Descendants(XName.Get("altChunk", "http://schemas.openxmlformats.org/wordprocessingml/2006/main")).SingleOrDefault());
            using var relationships = zip.GetEntry("word/_rels/document.xml.rels")!.Open();
            Assert.DoesNotContain(XDocument.Load(relationships).Descendants(), e => (string?)e.Attribute("TargetMode") == "External");
        }
        var folder = t.Dir("source"); File.WriteAllText(Path.Combine(folder, "index.html"), "<html>page</html>");
        File.WriteAllText(Path.Combine(folder, "style.css"), "body{color:red}");
        var source = Path.Combine(t.Path, "source.zip"); await ArtifactExport.WriteSourceAsync(folder, false, source);
        using var archive = ZipFile.OpenRead(source);
        Assert.Equal(new[] { "index.html", "style.css" }, archive.Entries.Select(e => e.FullName).Order());
        Assert.Contains("getComputedStyle", ArtifactExport.DocumentScript);
        Assert.Contains("textContent", ArtifactExport.MarkdownScript);
    }
}
