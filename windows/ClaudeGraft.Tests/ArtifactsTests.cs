using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class ArtifactAPITests
{
    private const string Org = "8d22f6d4-6ac4-46cb-840d-3248ad76f2f7";

    [Fact(DisplayName = "a list is asked for exactly as claude.ai's own page asks, with a page token encoded")]
    public void Lists()
    {
        Assert.Equal("go", ArtifactAPI.Headers["X-Frame-CP"]);
        Assert.Equal($"/api/frame/artifacts?rel=mine&empty=exclude&limit=48&org={Org}&sort=last_viewed", ArtifactAPI.ListPath("mine", Org));
        Assert.EndsWith("&page_token=a%2Bb%3Dc", ArtifactAPI.ListPath("shared", Org, "a+b=c"));
    }

    [Fact(DisplayName = "permissions, invitations, renaming, copying and deleting go to the artifact service's own paths")]
    public void Paths()
    {
        Assert.Equal($"/api/frame/perm/aadd31ed-02a0?org={Org}", ArtifactAPI.PermPath("aadd31ed-02a0", Org));
        Assert.Equal($"/api/frame/invite/aadd31ed-02a0?org={Org}", ArtifactAPI.InvitePath("aadd31ed-02a0", Org));
        Assert.Equal($"/api/frame/retitle/aadd31ed?org={Org}", ArtifactAPI.RetitlePath("aadd31ed", Org));
        Assert.Equal($"/api/frame/duplicate/aadd31ed?org={Org}", ArtifactAPI.DuplicatePath("aadd31ed", Org));
        Assert.Equal($"/api/frame/aadd31ed?org={Org}", ArtifactAPI.ArtifactPath("aadd31ed", Org));
    }

    [Fact(DisplayName = "bodies are sent in the shapes the share panel sends")]
    public void Bodies()
    {
        Assert.Equal("""{"email":"a@example.com","role":"reader"}""", ArtifactAPI.InviteBody("a@example.com"));
        Assert.Equal("""{"email":"a@example.com","role":"editor"}""", ArtifactAPI.InviteBody("a@example.com", ArtifactRole.Editor));
        Assert.Equal("""{"read":{"mode":"public"}}""", ArtifactAPI.ReadModeBody(true, ["u1"]));
        Assert.Equal("""{"read":{"mode":"users","users":["u1"]}}""", ArtifactAPI.ReadModeBody(false, ["u1"]));
        Assert.Equal("""{"read":{"mode":"owner"}}""", ArtifactAPI.ReadModeBody(false, []));
        Assert.Equal("""{"title":"New \"name\""}""", ArtifactAPI.RetitleBody("New \"name\""));
    }

    [Fact(DisplayName = "an account in a team as well is asked about under the organization that chats")]
    public void Identity()
    {
        var account = """
            {"uuid":"acc-1","email_address":"me@example.com","memberships":[
              {"organization":{"uuid":"team-org","capabilities":["raven"]}},
              {"organization":{"uuid":"chat-org","capabilities":["chat","claude_pro"]}}]}
            """;
        Assert.Equal(new ArtifactAPI.Identity("acc-1", "me@example.com", "chat-org"), ArtifactAPI.IdentityFrom(account));
        Assert.Null(ArtifactAPI.IdentityFrom("""{"error":"unauthorized"}"""));
        Assert.Null(ArtifactAPI.IdentityFrom("<html>"));
    }

    [Fact(DisplayName = "a 401 anywhere is signed out, a 403 only from the account page, which is asked once otherwise")]
    public void SignedOut()
    {
        Assert.True(ArtifactAPI.MeansSignedOut(401, ArtifactAPI.ListPath("mine", Org)));
        Assert.True(ArtifactAPI.MeansSignedOut(403, ArtifactAPI.AccountPath));
        Assert.False(ArtifactAPI.MeansSignedOut(403, ArtifactAPI.PermPath("aadd31ed", Org)));
        Assert.False(ArtifactAPI.MeansSignedOut(403, "/v1/code/sessions?limit=200"));
        Assert.True(ArtifactAPI.NeedsAccountCheck(403, ArtifactAPI.InvitePath("aadd31ed", Org)));
        Assert.False(ArtifactAPI.NeedsAccountCheck(403, ArtifactAPI.AccountPath));
        Assert.False(ArtifactAPI.NeedsAccountCheck(404, ArtifactAPI.InvitePath("aadd31ed", Org)));
    }

    [Fact(DisplayName = "a page of artifacts is read with its link and next token, and a challenge page is no list")]
    public void Page()
    {
        var mine = Guid.NewGuid();
        var listing = """
            {"artifacts":[
              {"kind":"page","id":"a1","rel":"mine","title":"Pool check","audience":"owner",
               "open_url":"https://claude.ai/artifact/N6k75kB4","sort_time":"2026-11-07T10:00:00.000Z","owner_account":"acc-1",
               "renamable":true,"duplicable":true,"duplicate_available":false},
              {"kind":"page","id":"a2","rel":"shared","title":"","audience":"public","sort_time":"2026-11-08T10:00:00Z"}],
             "next_page_token":"next"}
            """;
        var (artifacts, next) = ArtifactAPI.Page(listing, mine, Org)!.Value;
        Assert.Equal("next", next);
        Assert.Equal("https://claude.ai/artifact/N6k75kB4", artifacts[0].OpenUrl);
        Assert.True(artifacts[0].Renamable && artifacts[0].Duplicable && !artifacts[0].DuplicateAvailable);
        Assert.Equal("Untitled", artifacts[1].Title);
        Assert.Equal(ArtifactAccess.AnyoneWithLink, artifacts[1].Access);
        Assert.False(artifacts[1].Renamable || artifacts[1].Duplicable);
        Assert.Null(ArtifactAPI.Page("<html>", mine, Org));
    }

    [Fact(DisplayName = "asking an artifact to be what it already is changes nothing, and the organization keeps its own entry")]
    public void Access()
    {
        Assert.Equal(ArtifactAccess.Invited, ArtifactAccessOf.Audience("users"));
        Assert.Equal(ArtifactAccess.OnlyYou, ArtifactAccessOf.Audience("owner"));
        Assert.Equal(ArtifactAccess.Organization, ArtifactAccessOf.Audience("org"));
        Assert.Equal(ArtifactAccess.Organization, ArtifactAccess.Organization.MenuChoice());
        Assert.Equal(ArtifactAccess.Invited, ArtifactAccess.OnlyYou.MenuChoice());
        Assert.False(ArtifactAccess.AnyoneWithLink.ChangesToPublic(true));
        Assert.False(ArtifactAccess.Invited.ChangesToPublic(false));
        Assert.False(ArtifactAccess.OnlyYou.ChangesToPublic(false));
        Assert.True(ArtifactAccess.Invited.ChangesToPublic(true));
        Assert.True(ArtifactAccess.Organization.ChangesToPublic(false));
    }

    [Fact(DisplayName = "where an artifact does not take a role, the person gets the most it does take and never more")]
    public void Roles()
    {
        var both = ArtifactAPI.PermissionsFrom("""{"version":"v7","read":{"mode":"users","users":["u1"]},"commentersEnabled":true,"editorsEnabled":true}""")!;
        var none = ArtifactAPI.PermissionsFrom("""{"version":"v7","read":{"mode":"owner"}}""")!;
        var older = ArtifactAPI.PermissionsFrom("""{"version":3,"writersEnabled":true}""")!;
        Assert.True(both.Commenters && both.Editors && both.Version == "v7" && both.Mode == "users");
        Assert.Equal(["u1"], both.Invited);
        Assert.True(!none.Commenters && !none.Editors && older.Editors && older.Version == "3" && older.Mode is null);
        var commentsOnly = none with { Commenters = true };
        Assert.Equal(ArtifactRole.Editor, ArtifactRole.Editor.OfferedBy(both));
        Assert.Equal(ArtifactRole.Commenter, ArtifactRole.Editor.OfferedBy(commentsOnly));
        Assert.Equal(ArtifactRole.Reader, ArtifactRole.Editor.OfferedBy(none));
        Assert.Equal(ArtifactRole.Reader, ArtifactRole.Commenter.OfferedBy(none));
        Assert.Equal(ArtifactRole.Reader, ArtifactRole.Reader.OfferedBy(both));
    }

    [Fact(DisplayName = "claude.ai's reason for a refusal is read wherever it puts it")]
    public void Reason()
    {
        Assert.Equal("title_invalid", ArtifactAPI.Reason("""{"reason":"title_invalid"}"""));
        Assert.Equal("org_mismatch", ArtifactAPI.Reason("""{"error":{"reason":"org_mismatch"}}"""));
    }

    [Fact(DisplayName = "an artifact's files are read from its own host, and none that could climb out of the folder")]
    public void Files()
    {
        var files = ArtifactAPI.SourceFrom("""
            {"ver":"v12","assetToken":"a.b.c.1900000000","title":"Plan","files":[
              {"path":"index.html"},{"path":"app.js"},{"path":"img/a b.png"},
              {"path":"../escape.html"},{"path":"/etc/passwd"},{"path":"a//b"},{"path":"c:\\x"}]}
            """)!;
        Assert.Equal(["index.html", "app.js", "img/a b.png"], files.Files);
        Assert.Null(ArtifactAPI.SourceFrom("""{"ver":"v1"}"""));
        Assert.Equal("https://aadd31ed-02a0.frame.claudeusercontent.com/_f/v12/_src/img/a%20b.png?__frame_t=a.b.c.1900000000",
            ArtifactAPI.FileUrl("AADD31ED-02A0", files, "img/a b.png"));
        Assert.Null(ArtifactAPI.FileUrl("x", files, "../a"));
    }

    [Fact(DisplayName = "an artifact two of your accounts can see is listed once, under the one that owns it, newest first")]
    public void Merge()
    {
        Guid mine = Guid.NewGuid(), other = Guid.NewGuid();
        var owned = new Artifact { Id = "x", Account = mine, Org = Org, Title = "Plan", Audience = "users", SortTime = DateTimeOffset.FromUnixTimeSeconds(10) };
        var shared = owned with { Account = other, Rel = "shared" };
        var newer = owned with { Id = "y", SortTime = DateTimeOffset.FromUnixTimeSeconds(20) };
        var merged = ArtifactAPI.Merge([[shared], [owned, newer]]);
        Assert.Equal(["y", "x"], merged.Select(a => a.Id));
        Assert.Equal(mine, merged[1].Account);
    }

    [Fact(DisplayName = "an export is named after the artifact, and a page of several files goes out zipped")]
    public void ExportNames()
    {
        Assert.Equal("Q3 plan- draft-2.md", ArtifactExport.FileName("Q3 plan: draft/2", ArtifactExportFormat.Markdown, true));
        Assert.Equal("Artifact.pdf", ArtifactExport.FileName("  ", ArtifactExportFormat.Pdf, true));
        Assert.Equal("Site.zip", ArtifactExport.FileName("Site", ArtifactExportFormat.Html, false));
        Assert.Equal("Site.docx", ArtifactExport.FileName("Site", ArtifactExportFormat.Word, false));
    }

    [Fact(DisplayName = "addresses pasted from a mail header are read once each, and what is not an address is dropped")]
    public void Addresses() =>
        Assert.Equal(["ana@example.com", "bo@example.org"],
            GroupAddresses.Parse("Ana <ANA@example.com>, bo@example.org; ana@example.com\nnot-an-address\nx@y"));
}

public class ArtifactAccountsTests
{
    private static readonly ArtifactAccount First = new(Guid.NewGuid(), "a@example.com", "acc-a", "org-a");
    private static readonly ArtifactAccount Second = new(Guid.NewGuid(), "b@example.com", "acc-b", "org-b");

    [Fact(DisplayName = "signing in again to a listed account keeps the new store, in the account's place, and lets the dead one go")]
    public void Again()
    {
        var fresh = Guid.NewGuid();
        var (accounts, retired) = ArtifactAccounts.SignedIn([First, Second], fresh, new("acc-a", "a@example.com", "org-a"));
        Assert.Equal([fresh, Second.Id], accounts.Select(a => a.Id));
        Assert.Equal([First.Id], retired);
    }

    [Fact(DisplayName = "a sign-in to an account not yet listed adds it and lets nothing go")]
    public void Added()
    {
        var fresh = Guid.NewGuid();
        var (accounts, retired) = ArtifactAccounts.SignedIn([First], fresh, new("acc-b", "b@example.com", "org-b"));
        Assert.Equal([First.Id, fresh], accounts.Select(a => a.Id));
        Assert.Empty(retired);
    }

    [Fact(DisplayName = "signing in again to one account as another listed one leaves that one listed once, on the store now holding it")]
    public void Crossed()
    {
        var (accounts, retired) = ArtifactAccounts.SignedIn([First, Second], First.Id, new("acc-b", "b@example.com", "org-b"));
        Assert.Equal([First.Id], accounts.Select(a => a.Id));
        Assert.Equal("acc-b", accounts[0].AccountUuid);
        Assert.Equal([Second.Id], retired);
    }

    [Fact(DisplayName = "signing in again to an account as itself keeps its store and its place")]
    public void Same()
    {
        var (accounts, retired) = ArtifactAccounts.SignedIn([First, Second], Second.Id, new("acc-b", "b@example.com", "org-b2"));
        Assert.Equal([First.Id, Second.Id], accounts.Select(a => a.Id));
        Assert.Equal("org-b2", accounts[1].Org);
        Assert.Empty(retired);
    }
}

[Collection("GlobalState")]
public sealed class ArtifactFilesTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public ArtifactFilesTests() => GraftPaths.ProfilesRootOverride = _tmp.Path;
    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _tmp.Dispose();
    }

    private const string Old = """{"emails":["a@example.com","b@example.com"],"id":"6e1f0f55-8c8b-4e64-8f73-6d3a50d3e5b1","name":"Design"}""";

    [Fact(DisplayName = "groups are kept in Graft's own folder and read back as they were saved")]
    public void Groups()
    {
        var groups = new List<ArtifactGroup> { new() { Id = Guid.NewGuid(), Name = "Design", Emails = ["a@example.com"] } };
        Assert.True(ArtifactFiles.Save(groups, ArtifactFiles.Groups));
        Assert.StartsWith(GraftPaths.OwnData, ArtifactFiles.Groups);
        var back = ArtifactFiles.ReadEach<ArtifactGroup>(ArtifactFiles.Groups);
        Assert.Equal(ArtifactFiles.State.Read, back.State);
        Assert.Equal(groups, back.Value.Values);
        Assert.Equal(0, back.Value.Lost);
    }

    [Fact(DisplayName = "a group saved before roles reads back as viewers and is written exactly as before")]
    public void OldGroups()
    {
        var group = JsonSerializer.Deserialize<ArtifactGroup>(Old)!;
        Assert.Equal(ArtifactRole.Reader, group.RoleOf("a@example.com"));
        Assert.Equal(ArtifactRole.Reader, group.CommonRole);
        Assert.Equal(Old, JsonSerializer.Serialize(group));
    }

    [Fact(DisplayName = "a mixed group keeps each person's role through a save, and can be made all one role again")]
    public void Mixed()
    {
        var mixed = new ArtifactGroup { Id = Guid.NewGuid(), Name = "Review", Emails = ["a@example.com", "b@example.com", "c@example.com"] };
        mixed.SetRole(ArtifactRole.Editor, "a@example.com");
        mixed.SetRole(ArtifactRole.Commenter, "b@example.com");
        var back = JsonSerializer.Deserialize<ArtifactGroup>(JsonSerializer.Serialize(mixed))!;
        Assert.Equal(mixed, back);
        Assert.Null(back.CommonRole);
        back.SetRoleForEveryone(ArtifactRole.Editor);
        Assert.Equal(ArtifactRole.Editor, back.CommonRole);
        back.SetRoleForEveryone(ArtifactRole.Reader);
        Assert.Empty(back.Roles);
    }

    [Fact(DisplayName = "a role this Graft does not know costs that one person their role, not the group")]
    public void UnknownRole()
    {
        var group = JsonSerializer.Deserialize<ArtifactGroup>("""{"emails":["a@example.com","b@example.com"],"id":"6e1f0f55-8c8b-4e64-8f73-6d3a50d3e5b1","name":"X","roles":{"a@example.com":"manager","b@example.com":"editor"}}""")!;
        Assert.Equal(ArtifactRole.Reader, group.RoleOf("a@example.com"));
        Assert.Equal(ArtifactRole.Editor, group.RoleOf("b@example.com"));
    }

    [Fact(DisplayName = "a missing file is told from an unreadable one, which is moved aside whole rather than saved over")]
    public void Unreadable()
    {
        var garbled = ArtifactFiles.Accounts;
        Directory.CreateDirectory(Path.GetDirectoryName(garbled)!);
        File.WriteAllText(garbled, "{not json");
        Assert.Equal(ArtifactFiles.State.Missing, ArtifactFiles.Read<List<ArtifactAccount>>(garbled + ".absent").State);
        Assert.Equal(ArtifactFiles.State.Unreadable, ArtifactFiles.Read<List<ArtifactAccount>>(garbled).State);
        var aside = ArtifactFiles.SetAside(garbled, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        Assert.Equal("artifact-accounts.unreadable-1700000000.json", Path.GetFileName(aside));
        Assert.False(File.Exists(garbled));
        Assert.Equal("{not json", File.ReadAllText(aside!));
    }

    [Fact(DisplayName = "one group that will not decode costs that group, not every group in the file")]
    public void OneBad()
    {
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(ArtifactFiles.Groups, """
            [{"id":"6e1f0f55-8c8b-4e64-8f73-6d3a50d3e5b1","name":"Design","emails":["a@example.com"]},
             {"id":"not a uuid","name":"Broken","emails":["b@example.com"]},
             {"id":"7e1f0f55-8c8b-4e64-8f73-6d3a50d3e5b1","name":"Review","emails":[]}]
            """);
        var found = ArtifactFiles.ReadEach<ArtifactGroup>(ArtifactFiles.Groups);
        Assert.Equal(["Design", "Review"], found.Value.Values.Select(g => g.Name));
        Assert.Equal(1, found.Value.Lost);
    }

    [Fact(DisplayName = "the accounts list reads back as it was saved")]
    public void Accounts()
    {
        var accounts = new List<ArtifactAccount> { new(Guid.NewGuid(), "a@example.com", "acc-a", "org-a") };
        Assert.True(ArtifactFiles.Save(accounts, ArtifactFiles.Accounts));
        Assert.Equal(accounts, ArtifactFiles.Read<List<ArtifactAccount>>(ArtifactFiles.Accounts).Value);
    }
}

public class WordDocumentTests
{
    [Fact(DisplayName = "a Word export carries the page as a part Word converts, and says how it is encoded")]
    public void CarriesThePage()
    {
        var bytes = WordDocument.FromHtml("<html><head><title>T</title></head><body><p>“Quoted”</p></body></html>");
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        string Read(string name) => new StreamReader(zip.GetEntry(name)!.Open()).ReadToEnd();
        Assert.Contains("altChunk r:id=\"page\"", Read("word/document.xml"));
        Assert.Contains("Target=\"page.html\"", Read("word/_rels/document.xml.rels"));
        Assert.Contains("PartName=\"/word/page.html\"", Read("[Content_Types].xml"));
        var page = Read("word/page.html");
        Assert.Contains("<meta charset=\"utf-8\">", page);
        Assert.Contains("“Quoted”", page);
    }

    [Fact(DisplayName = "a page that already names its encoding is carried as it is")]
    public void KeepsItsOwnCharset()
    {
        const string html = "<html><head><meta charset=\"utf-8\"></head><body></body></html>";
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(WordDocument.FromHtml(html)));
        Assert.Equal(html, new StreamReader(zip.GetEntry("word/page.html")!.Open()).ReadToEnd());
    }
}
