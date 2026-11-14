using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ProjectsTests
{
    private static readonly Guid Mine = Guid.NewGuid(), Other = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T12:00:00Z");
    private static JsonNode Json(string text) => JsonNode.Parse(text)!;

    [Fact(DisplayName = "project and chat responses retain metadata and reject an unreadable page")]
    public void ParseRequestsAndResponses()
    {
        Assert.Contains("org%2Fone/projects_v2?", ProjectApi.ProjectsPath("org/one", 30));
        Assert.Contains("offset=30", ProjectApi.ProjectsPath("org", 30));
        Assert.Equal("a \"quote\"", (string?)ProjectApi.SearchBody("a \"quote\"")["query"]);
        Assert.Equal("/v1/code/sessions?limit=200&cursor=a%7Cb%3D", ProjectApi.CodePath("a|b="));
        Assert.Equal("ccr-byoc-2025-07-29", ProjectApi.CodeHeaders("org")["anthropic-beta"]);
        Assert.Equal("https://claude.ai/code/session_one", ProjectApi.CodeUrl("cse_one").AbsoluteUri);
        var page = ProjectApi.ParseProjects(Json("""
            {"data":[{"uuid":"p1","name":"Graft","description":"the app","creator":{"full_name":"Owner"},"updated_at":"2026-11-07T10:00:00.000Z"},
            {"uuid":"p2","name":"","is_private":false,"is_starred":true,"is_starter_project":true},
            {"uuid":"old","archived_at":"2026-01-01T00:00:00Z"}],"pagination":{"has_more":true}}
            """), Mine);
        Assert.True(page.HasMore); Assert.Equal(2, page.Projects.Length); Assert.Equal("Owner", page.Projects[0].Creator);
        Assert.Equal("Untitled", page.Projects[1].Name); Assert.True(page.Projects[1].IsExample); Assert.False(page.Projects[1].IsPrivate);
        Assert.Equal("p2", ProjectApi.Merge([page.Projects])[0].Id);
        Assert.Throws<InvalidDataException>(() => ProjectApi.ParseProjects(Json("{}"), Mine));
        var hits = Chats();
        Assert.True(hits[0].IsArchived); Assert.True(hits[0].IsStarred); Assert.Equal("p1", hits[0].Project);
        Assert.Equal(new[] { new TextMatch(2, 11) }, hits[0].SnippetMatches);
        Assert.Equal("https://claude.ai/chat/c1", hits[1].OpenUrl!.AbsoluteUri);
        Assert.Equal(new[] { new TextMatch(3, 12) }, ProjectApi.ValidMatches("🙂 raspberry", [new(3, 12), new(40, 50), new(1, 2)]));
    }

    private static SearchHit[] Chats() => ProjectApi.ParseChats(Json("""
        {"data":[{"conversation":{"uuid":"c2","name":"Camera Pi","project_uuid":"p1","is_archived":true,"is_starred":true},
        "matched_snippet":{"text":"a raspberry camera","matches":[{"start":2,"end":11}]},"semantic_rank":1},
        {"conversation":{"uuid":"c1","name":"Raspberry Pi Pico"},"title_matches":[{"start":0,"end":9}],"semantic_rank":0}]}
        """), Mine);

    [Fact(DisplayName = "a local Code session is called gone only after its complete owner and organization list predates no activity")]
    public void CorroborationAndFilters()
    {
        var remote = ProjectApi.ParseCode(Json("""
            {"data":[{"id":"cse_here","title":"Fix graft build","environment_kind":"bridge"},
            {"id":"cse_cloud","title":"Graft notes","environment_kind":"anthropic_cloud","status":"archived"},
            {"id":"cse_far","title":"Graft on another PC","environment_kind":"bridge"},
            {"id":"cse_carried","title":"Graft imported"}],"next_cursor":"next"}
            """));
        Assert.Equal("next", remote.Next);
        var local = new[] {
            new LocalCodeSession(["cse_here"], "mine", "org", "Graft here", @"C:\Work", "here.jsonl", Now.AddMinutes(-2), true),
            new LocalCodeSession(["cse_carried"], "mine", "org", "Graft imported", @"Z:\Work", "carried.jsonl", Now.AddMinutes(-2), false),
            new LocalCodeSession(["cse_deleted"], "mine", "org", "Graft gone", @"C:\Work", "gone.jsonl", Now.AddMinutes(-2), true),
            new LocalCodeSession(["cse_other"], "other", "org", "Graft other", @"C:\Work", "other.jsonl", Now.AddMinutes(-2), true) };
        SearchHit[] Hits(bool complete = true, string? org = "org", DateTimeOffset? time = null, LocalCodeSession[]? transcripts = null) =>
            ProjectApi.CodeHits("graft", Mine, "mine", org, remote.Sessions, complete, time ?? Now, transcripts ?? local);
        var hits = Hits();
        Assert.Equal(SessionPlace.ThisComputer, hits.Single(hit => hit.Id == "cse_here").Place);
        Assert.Equal(SessionPlace.Cloud, hits.Single(hit => hit.Id == "cse_cloud").Place);
        Assert.Equal(SessionPlace.OtherComputer, hits.Single(hit => hit.Id == "cse_far").Place);
        Assert.Equal(SessionPlace.OtherComputer, hits.Single(hit => hit.Id == "cse_carried").Place);
        var gone = Assert.Single(hits, hit => hit.Place == SessionPlace.GoneFromClaude); Assert.Null(gone.OpenUrl); Assert.Equal("gone.jsonl", gone.Transcript);
        Assert.DoesNotContain(Hits(false), hit => hit.Place == SessionPlace.GoneFromClaude);
        Assert.DoesNotContain(Hits(org: "different"), hit => hit.Place == SessionPlace.GoneFromClaude);
        Assert.DoesNotContain(Hits(org: null), hit => hit.Place == SessionPlace.GoneFromClaude);
        Assert.DoesNotContain(Hits(time: Now.AddMinutes(-3)), hit => hit.Place == SessionPlace.GoneFromClaude);
        Assert.DoesNotContain(Hits(transcripts: local.Select(item => item with { CarriedOn = true }).ToArray()), hit => hit.Place == SessionPlace.GoneFromClaude);
        Assert.NotNull(ProjectApi.CodeMatch("build graft", "Fix graft build")); Assert.Null(ProjectApi.CodeMatch("graft cloud", "graft", "repository"));
        var all = Chats().Concat(hits).ToArray();
        Assert.DoesNotContain(all.Where(hit => new SearchFilters().Admits(hit, Now)), hit => hit.IsArchived);
        Assert.Equal(new[] { "c2", "cse_cloud" }, all.Where(hit => new SearchFilters { Status = SearchStatus.Archived }.Admits(hit, Now)).Select(hit => hit.Id));
        Assert.Single(all, hit => new SearchFilters { Status = SearchStatus.Any, Place = SessionPlace.ThisComputer }.Admits(hit, Now));
        Assert.Equal("c1", Assert.Single(all, hit => new SearchFilters { Status = SearchStatus.Any, NoProject = true }.Admits(hit, Now)).Id);
        Assert.Equal("c2", Assert.Single(all, hit => new SearchFilters { Status = SearchStatus.Any, StarredOnly = true }.Admits(hit, Now)).Id);
        var other = Chats().Take(1).Select(hit => hit with { Id = "other", Account = Other, Rank = 0 });
        Assert.Equal(new[] { "c1", "other", "c2" }, ProjectApi.Interleave([all, other]).Take(3).Select(hit => hit.Id));
        Assert.False(new SearchFilters().IsNarrowed); Assert.True(new SearchFilters { StarredOnly = true }.IsNarrowed);
        Assert.True(ProjectApi.LocalReadServes(Now, Now, Now, TimeSpan.FromMinutes(2)));
        Assert.False(ProjectApi.LocalReadServes(Now.AddSeconds(-1), Now, Now, TimeSpan.FromMinutes(2)));
        Assert.False(ProjectApi.LocalReadServes(Now, Now, Now.AddMinutes(3), TimeSpan.FromMinutes(2)));
    }

    private sealed class Accounts : IProjectAccounts
    {
        public List<ProjectAccount> List = [new(Mine, "mine", "org", "Mine", 1)];
        public IReadOnlyList<ProjectAccount> AccountsList => List;
        IReadOnlyList<ProjectAccount> IProjectAccounts.Accounts => List.ToArray();
        public event EventHandler? Changed;
        public readonly List<(string Method, string Path)> Requests = [];
        public Func<string, string, Task<JsonNode?>> Answer = (_, _) => Task.FromResult<JsonNode?>(Json("{\"data\":[]}"));
        public bool IsCurrent(ProjectAccount account) => List.Contains(account);
        public void NoteSignIn(ProjectAccount account, bool accepted) { }
        public void Update() => Changed?.Invoke(this, EventArgs.Empty);
        public Task<JsonNode?> RequestAsync(ProjectAccount account, string method, string path, JsonNode? body = null, IReadOnlyDictionary<string, string>? headers = null)
        { Requests.Add((method, path)); if (path.StartsWith("/v1/")) Assert.Equal("org", headers!["x-organization-uuid"]); return Answer(method, path); }
    }

    [Fact(DisplayName = "searches reuse Code lists, empty project lists and fresh local reads while filters send nothing")]
    public async Task SearchCachingAndFilters()
    {
        var accounts = new Accounts(); var localReads = 0; var now = Now;
        using var catalog = new ProjectCatalog(accounts, () => { localReads++; return Task.FromResult(Array.Empty<LocalCodeSession>()); }, () => now);
        await catalog.SearchAsync("one"); catalog.Filters = new() { Status = SearchStatus.Archived }; _ = catalog.Shown.ToArray();
        await catalog.SearchAsync("two");
        Assert.Equal(2, accounts.Requests.Count(request => request.Method == "POST"));
        Assert.Single(accounts.Requests, request => request.Path.Contains("projects_v2"));
        Assert.Single(accounts.Requests, request => request.Path.StartsWith("/v1/")); Assert.Equal(1, localReads);
        now = Now.AddMinutes(3); await catalog.SearchAsync("three");
        Assert.Equal(2, accounts.Requests.Count(request => request.Path.StartsWith("/v1/"))); Assert.Equal(2, localReads);
    }

    [Fact(DisplayName = "a refused or truncated Code list preserves chat results and never claims a transcript is gone")]
    public async Task FailedAndTruncatedCodeList()
    {
        var accounts = new Accounts(); var fail = true;
        accounts.Answer = (method, path) => {
            if (path.StartsWith("/v1/")) {
                if (fail) throw new ProjectRequestException(false);
                return Task.FromResult<JsonNode?>(Json("{\"data\":[],\"next_cursor\":\"more\"}"));
            }
            return Task.FromResult<JsonNode?>(method == "POST" ? Json("{\"data\":[{\"conversation\":{\"uuid\":\"chat\",\"name\":\"graft\"}}]}") : Json("{\"data\":[]}"));
        };
        using var catalog = new ProjectCatalog(accounts, () => Task.FromResult(new[] { new LocalCodeSession(["missing"], "mine", "org", "graft", "C:\\work", "gone", Now.AddDays(-1), true) }), () => Now);
        await catalog.SearchAsync("graft"); Assert.Equal("chat", Assert.Single(catalog.Results).Id); Assert.Equal(ProjectLoadState.Ready, catalog.SearchStates[Mine]);
        fail = false; await catalog.SearchAsync("graft"); Assert.Equal("chat", Assert.Single(catalog.Results).Id);
        Assert.Equal(1 + ProjectApi.MaximumCodePages, accounts.Requests.Count(request => request.Path.StartsWith("/v1/")));
    }

    [Fact(DisplayName = "a pending account response cannot restore signed-out rows or restart an in-flight search")]
    public async Task SignOutDropsPendingResponses()
    {
        var accounts = new Accounts(); var pending = new TaskCompletionSource<JsonNode?>();
        accounts.Answer = (method, _) => method == "POST" ? pending.Task : Task.FromResult<JsonNode?>(Json("{\"data\":[]}"));
        using var catalog = new ProjectCatalog(accounts, () => Task.FromResult(Array.Empty<LocalCodeSession>()));
        var search = catalog.SearchAsync("graft"); await catalog.SearchAsync("held return");
        Assert.Single(accounts.Requests, request => request.Method == "POST");
        accounts.List.Clear(); accounts.Update(); pending.SetResult(Json("{\"data\":[{\"conversation\":{\"uuid\":\"private\",\"name\":\"graft\"}}]}"));
        await search; Assert.Empty(catalog.Results); Assert.Empty(catalog.SearchStates);
    }

    [Fact(DisplayName = "a search reads transcript facts without changing the sweep cache and recognizes carried-over homes")]
    public void LocalTranscriptReadIsReadOnly()
    {
        using var temp = new TempDir();
        var oldRoot = GraftPaths.ProfilesRootOverride; var oldProjects = GraftPaths.ClaudeProjectsOverride;
        try
        {
            GraftPaths.ProfilesRootOverride = temp.Dir("profiles"); GraftPaths.ClaudeProjectsOverride = temp.Dir("projects");
            var directory = temp.Dir("projects", "work"); var file = Path.Combine(directory, "session.jsonl");
            File.WriteAllText(file, """
                {"type":"bridge-session","sessionId":"session","bridgeSessionId":"cse_local","ownerAccountUuid":"mine","ownerOrganizationUuid":"org"}
                {"promptId":"p1","cwd":"/Users/missing/work","type":"user","message":{"role":"user","content":"Graft here"},"timestamp":"2026-11-01T12:00:00.000Z"}
                {"type":"custom-title","customTitle":"Graft here"}
                """);
            Directory.CreateDirectory(GraftPaths.OwnData); var cache = Path.Combine(GraftPaths.OwnData, "transcript-cache.json"); File.WriteAllText(cache, "{}");
            var before = File.ReadAllBytes(cache); var session = Assert.Single(LocalCodeSessions.Read());
            Assert.Equal(new[] { "cse_local" }, session.BridgeIds); Assert.Equal("org", session.Organization); Assert.False(session.FolderIsHere);
            Assert.Equal(before, File.ReadAllBytes(cache)); Assert.Equal("Graft here", session.Title);
        }
        finally { GraftPaths.ProfilesRootOverride = oldRoot; GraftPaths.ClaudeProjectsOverride = oldProjects; }
    }

    [Fact(DisplayName = "a sign-in generation replaces a pending project list and project pagination stays bounded")]
    public async Task NewSignInSupersedesProjectList()
    {
        var accounts = new Accounts(); var old = new TaskCompletionSource<JsonNode?>(); var first = true;
        accounts.Answer = (_, _) => {
            if (first) { first = false; return old.Task; }
            return Task.FromResult<JsonNode?>(Json("{\"data\":[{\"uuid\":\"new\",\"name\":\"Current\"}],\"pagination\":{\"has_more\":true}}"));
        };
        using var catalog = new ProjectCatalog(accounts, () => Task.FromResult(Array.Empty<LocalCodeSession>()));
        var pending = catalog.RefreshProjectsAsync();
        accounts.List[0] = accounts.List[0] with { Generation = 2 }; accounts.Update();
        Assert.Equal("new", Assert.Single(catalog.Projects).Id);
        old.SetResult(Json("{\"data\":[{\"uuid\":\"old\",\"name\":\"Old sign-in\"}]}")); await pending;
        Assert.Equal("new", Assert.Single(catalog.Projects).Id);
        Assert.Equal(1 + ProjectApi.MaximumProjectPages, accounts.Requests.Count);
        accounts.List.Clear(); accounts.Update(); Assert.Empty(catalog.Projects);
    }

    [Fact(DisplayName = "an account with a newer session list shares only a local read started after that list")]
    public async Task LocalReadMustBeAtLeastAsNewAsRemoteList()
    {
        var accounts = new Accounts(); var reads = 0; var now = Now;
        using var catalog = new ProjectCatalog(accounts, () => { reads++; return Task.FromResult(Array.Empty<LocalCodeSession>()); }, () => now);
        await catalog.SearchAsync("graft"); Assert.Equal(1, reads);
        // A fresh sign-in invalidates the remote list inside its two-minute
        // lifetime; the still-young local cache must not predate that new list.
        now = Now.AddSeconds(30); accounts.List[0] = accounts.List[0] with { Generation = 2 }; accounts.Update();
        await catalog.SearchAsync("graft"); Assert.Equal(2, reads);
    }
}
