using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class WebAccountTests
{
    private static WebAccount Account(string uuid, Guid? store = null) => new() {
        Id = store ?? Guid.NewGuid(), AccountUuid = uuid, Email = uuid + "@example.test", OrganizationId = "org-" + uuid };

    [Fact(DisplayName = "web identity prefers the chat organization and retains only account metadata")]
    public void IdentityUsesChatOrganization()
    {
        var store = Guid.NewGuid();
        var identity = WebAccountPolicy.Identity(store, JsonNode.Parse("""
            {"uuid":"account","email_address":"one@example.test","memberships":[
              {"organization":{"uuid":"team","name":"Team","capabilities":["api"]}},
              {"organization":{"uuid":"chat-org","name":"Personal","capabilities":["chat"]}}],
             "cookie":"not-account-metadata","session_key":"not-account-metadata"}
            """));
        Assert.NotNull(identity); Assert.Equal(store, identity.Id); Assert.Equal("account", identity.AccountUuid);
        Assert.Equal("chat-org", identity.OrganizationId); Assert.Equal("Personal", identity.OrganizationName);
        Assert.Equal("one@example.test", identity.DisplayName);
        var serialized = JsonSerializer.Serialize(identity);
        Assert.DoesNotContain("not-account-metadata", serialized); Assert.DoesNotContain("session_key", serialized);
        var fallback = WebAccountPolicy.Identity(store, JsonNode.Parse("""
            {"uuid":"account","email_address":"one@example.test","memberships":[{"organization":{"uuid":"only"}}]}
            """));
        Assert.Equal("only", fallback!.OrganizationId);
    }

    [Theory(DisplayName = "incomplete account responses cannot create a listed account")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"uuid\":\"account\",\"memberships\":[]}")]
    [InlineData("{\"uuid\":\"account\",\"email_address\":\"one@example.test\",\"memberships\":[]}")]
    [InlineData("{\"uuid\":\"account\",\"email_address\":\"one@example.test\",\"memberships\":[{\"organization\":{\"uuid\":42}}]}")]
    public void IdentityRejectsMissingFields(string json) => Assert.Null(WebAccountPolicy.Identity(Guid.NewGuid(), JsonNode.Parse(json)));

    [Fact(DisplayName = "a working replacement sign-in keeps account order and retires the old store")]
    public void SignedInAccountKeepsItsPlace()
    {
        var a = Account("a"); var b = Account("b"); var c = Account("c"); var replacement = Account("b");
        var result = WebAccountPolicy.SignedIn([a, b, c], replacement);
        Assert.Equal(new[] { a, replacement, c }, result.Accounts); Assert.Equal(new[] { b.Id }, result.Retired);
        var appended = Account("d");
        Assert.Equal(new[] { a, b, c, appended }, WebAccountPolicy.SignedIn([a, b, c], appended).Accounts);
        var updated = b with { Email = "renamed@example.test" };
        var sameStore = WebAccountPolicy.SignedIn([a, b, c], updated);
        Assert.Equal(new[] { a, updated, c }, sameStore.Accounts); Assert.Empty(sameStore.Retired);
    }

    [Fact(DisplayName = "signing into A as an already listed B removes A and retains B at its old relative position")]
    public void SigningIntoAnotherListedAccountRemovesBothOldIdentities()
    {
        var a = Account("a"); var middle = Account("middle"); var b = Account("b"); var newStore = Guid.NewGuid();
        var result = WebAccountPolicy.SignedIn([a with { Id = newStore }, middle, b], b with { Id = newStore });
        Assert.Equal(new[] { middle.Id, newStore }, result.Accounts.Select(account => account.Id));
        Assert.Equal(new[] { "middle", "b" }, result.Accounts.Select(account => account.AccountUuid));
        Assert.Equal(new[] { b.Id }, result.Retired);
        Assert.DoesNotContain(result.Accounts, account => account.AccountUuid == "a");
    }

    [Theory(DisplayName = "requests accept only relative claude.ai API paths")]
    [InlineData("/api/account", true)]
    [InlineData("/api/account?include=memberships", true)]
    [InlineData("/v1/code/sessions?cursor=a%2Fb%3D", true)]
    [InlineData("https://claude.ai/api/account", false)]
    [InlineData("//example.test/api/account", false)]
    [InlineData("/\\example.test/api/account", false)]
    [InlineData("/api/../../outside", false)]
    [InlineData("/login", false)]
    [InlineData("/api/account\r\n", false)]
    public void RequestPathScope(string path, bool expected) => Assert.Equal(expected, WebAccountPolicy.IsApiPath(path));

    [Theory(DisplayName = "the account browser trusts only the exact HTTPS claude.ai origin")]
    [InlineData("https://claude.ai/login", true)]
    [InlineData("https://CLAUDE.AI:443/api/account", true)]
    [InlineData("http://claude.ai/api/account", false)]
    [InlineData("https://claude.ai:444/api/account", false)]
    [InlineData("https://sub.claude.ai/api/account", false)]
    [InlineData("https://claude.ai.example.test/api/account", false)]
    [InlineData("https://name@claude.ai/api/account", false)]
    [InlineData("file:///api/account", false)]
    [InlineData("/api/account", false)]
    public void OriginScope(string address, bool expected) => Assert.Equal(expected, WebAccountPolicy.IsClaudeOrigin(address));

    [Fact(DisplayName = "artifact headers stay on frame requests while Code requests carry their required headers")]
    public void HeadersHaveRouteSpecificScope()
    {
        var artifact = WebAccountPolicy.Headers("/api/frame/artifacts?org=org", true, new Dictionary<string, string> { ["If-Match"] = "version" });
        Assert.Equal("go", artifact["X-Frame-CP"]); Assert.Equal("web", artifact["X-Frame-Platform"]);
        Assert.Equal("application/json", artifact["Content-Type"]); Assert.Equal("version", artifact["If-Match"]);
        foreach (var path in new[] { "/api/account", "/api/organizations/org/projects_v2", "/api/organizations/org/conversation/search/v2", "/v1/code/sessions" })
        {
            var headers = WebAccountPolicy.Headers(path, false);
            Assert.False(headers.ContainsKey("X-Frame-CP")); Assert.False(headers.ContainsKey("X-Frame-Platform")); Assert.False(headers.ContainsKey("Content-Type"));
        }
        var code = WebAccountPolicy.Headers(ProjectApi.CodePath(), false, ProjectApi.CodeHeaders("org"));
        Assert.Equal("ccr-byoc-2025-07-29", code["anthropic-beta"]); Assert.Equal("2023-06-01", code["anthropic-version"]); Assert.Equal("org", code["x-organization-uuid"]);
        foreach (var key in new[] { "Cookie", "Authorization", "Host", "X-Frame-CP", "Origin" })
            Assert.Throws<ArgumentException>(() => WebAccountPolicy.Headers("/api/account", false, new Dictionary<string, string> { [key] = "unexpected" }));
    }

    [Theory(DisplayName = "a resource forbidden response checks account sign-in instead of signing the account out")]
    [InlineData(401, "/api/frame/artifacts", true, false)]
    [InlineData(403, "/api/account", true, false)]
    [InlineData(403, "/api/account?include=memberships", true, false)]
    [InlineData(403, "/api/frame/artifacts", false, true)]
    [InlineData(403, "/v1/code/sessions", false, true)]
    [InlineData(404, "/api/frame/missing", false, false)]
    [InlineData(500, "/api/account", false, false)]
    public void SignOutClassification(int status, string path, bool signedOut, bool check)
    { Assert.Equal(signedOut, WebAccountPolicy.MeansSignedOut(status, path)); Assert.Equal(check, WebAccountPolicy.NeedsAccountCheck(status, path)); }

    [Fact(DisplayName = "the account catalog preserves order and signing out removes just the selected account")]
    public void SavedCatalogRoundTripsAndRemovesOneAccount()
    {
        using var temp = new TempDir(); var previous = GraftPaths.ProfilesRootOverride;
        try
        {
            GraftPaths.ProfilesRootOverride = temp.Dir("root"); var file = Path.Combine(temp.Path, "catalog.json");
            var store = new WebAccountStore(file); Assert.Empty(store.Read()); Assert.False(store.ReadExisting); Assert.True(store.CanWrite);
            var a = Account("a"); var b = Account("b"); store.Save([b, a]);
            var reopened = new WebAccountStore(file); Assert.Equal(new[] { b, a }, reopened.Read()); Assert.True(reopened.ReadExisting);
            reopened.Save([a]); Assert.Equal(a, Assert.Single(new WebAccountStore(file).Read()));
            reopened.Save([]); var empty = new WebAccountStore(file); Assert.Empty(empty.Read()); Assert.True(empty.ReadExisting);
        }
        finally { GraftPaths.ProfilesRootOverride = previous; }
    }

    [Theory(DisplayName = "an unreadable catalog is preserved exactly and does not authorize orphan-store cleanup")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    public void CorruptCatalogIsPreserved(string text)
    {
        using var temp = new TempDir(); var file = temp.Write("catalog.json", text); var before = File.ReadAllBytes(file);
        var store = new WebAccountStore(file); Assert.Empty(store.Read()); Assert.False(store.ReadExisting); Assert.True(store.CanWrite); Assert.NotNull(store.Notice);
        Assert.False(File.Exists(file)); var saved = Assert.Single(Directory.GetFiles(temp.Path, "catalog.json.unreadable-*")); Assert.Equal(before, File.ReadAllBytes(saved));
    }

    [Fact(DisplayName = "duplicate stores and identities are preserved as an unreadable catalog instead of silently merged")]
    public void DuplicateCatalogIsPreserved()
    {
        using var temp = new TempDir(); var a = Account("a"); var b = Account("b");
        foreach (var accounts in new[] { new[] { a, b with { Id = a.Id } }, new[] { a, a with { Id = Guid.NewGuid() } } })
        {
            var file = temp.Write(Guid.NewGuid() + ".json", JsonSerializer.Serialize(accounts)); var store = new WebAccountStore(file);
            Assert.Empty(store.Read()); Assert.False(store.ReadExisting); Assert.True(store.CanWrite); Assert.False(File.Exists(file));
        }
    }

    [Fact(DisplayName = "a corrupt catalog that cannot be preserved remains untouched and blocks replacement")]
    public void LockedCorruptCatalogBlocksOverwrite()
    {
        using var temp = new TempDir(); var file = temp.Write("catalog.json", "{unreadable"); var before = File.ReadAllBytes(file);
        using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var store = new WebAccountStore(file); Assert.Empty(store.Read()); Assert.False(store.CanWrite); Assert.False(store.ReadExisting);
        Assert.Throws<IOException>(() => store.Save([Account("new")])); Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(temp.Path, "catalog.json.unreadable-*"));
    }
}
