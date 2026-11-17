using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class ProjectsTests
{
    private static readonly Guid Account = Guid.NewGuid();

    [Fact(DisplayName = "projects are asked for as claude.ai's own Projects page asks, a page of thirty at a time")]
    public void AskedAsThePageAsks()
    {
        Assert.Equal("/api/organizations/org%201/projects?include_harmony_projects=true&limit=30&offset=60&order_by=latest_chat",
            ClaudeAPI.ProjectsPath("org 1", 60));
    }

    [Fact(DisplayName = "a project list reads each project, and leaves an archived one off as the page does")]
    public void ReadsProjects()
    {
        const string json = """
            {"data": [
              {"uuid": "p1", "name": "Plans", "description": "Q3", "is_private": false, "is_starred": true,
               "is_starter_project": false, "creator": {"full_name": "Sam"}, "updated_at": "2026-11-01T10:00:00.123Z"},
              {"uuid": "p2", "name": "", "archived_at": "2026-10-01T00:00:00Z"},
              {"uuid": "p3", "name": ""},
              {"name": "no id"}
            ], "pagination": {"has_more": true}}
            """;
        var (projects, hasMore) = ClaudeAPI.Projects(json, Account)!.Value;
        Assert.True(hasMore);
        Assert.Equal(["p1", "p3"], projects.Select(p => p.Id));
        var plans = projects[0];
        Assert.Equal(("Plans", "Q3", false, true, "Sam"), (plans.Name, plans.Summary, plans.IsPrivate, plans.IsStarred, plans.Creator));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 10, 0, 0, 123, TimeSpan.Zero), plans.Updated);
        Assert.Equal("Untitled", projects[1].Name);
        Assert.True(projects[1].IsPrivate);
        Assert.Equal("https://claude.ai/project/p1", plans.OpenUrl);
    }

    [Fact(DisplayName = "the bare list the Projects page is answered with is read, and only a full one has more after it")]
    public void ReadsTheBareList()
    {
        var (projects, hasMore) = ClaudeAPI.Projects("""[{"uuid": "p1", "name": "Mine"}]""", Account)!.Value;
        Assert.Equal("Mine", Assert.Single(projects).Name);
        Assert.False(hasMore);
        var full = "[" + string.Join(",", Enumerable.Range(0, 30).Select(i => $$"""{"uuid": "p{{i}}"}""")) + "]";
        Assert.True(ClaudeAPI.Projects(full, Account)!.Value.HasMore);
    }

    [Fact(DisplayName = "an answer that is not a project list is unreadable, not empty")]
    public void Unreadable()
    {
        Assert.Null(ClaudeAPI.Projects("""{"error": "x"}""", Account));
        Assert.Null(ClaudeAPI.Projects("not json", Account));
        Assert.Empty(ClaudeAPI.Projects("""{"data": []}""", Account)!.Value.Projects);
    }

    [Fact(DisplayName = "every account's projects are one list, starred first and then the latest")]
    public void Merges()
    {
        ClaudeProject P(string id, bool starred, int day) => new()
            { Id = id, Account = Account, Name = id, IsStarred = starred, Updated = new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero) };
        var merged = ClaudeAPI.Merge([[P("old", false, 1), P("star", true, 2)], [P("new", false, 9)]]);
        Assert.Equal(["star", "new", "old"], merged.Select(p => p.Id));
    }

    [Fact(DisplayName = "a Beta project is read from its channel, and an archived or disabled one is left off")]
    public void ReadsBetaProjects()
    {
        const string json = """
            {"data": [
              {"id": "ch1", "name": "Launch", "topic": "Plans", "visibility": "private", "archived_at": null, "disabled_at": null,
               "last_activity_at": "2026-11-10T08:00:00Z", "updated_at": "2026-11-01T00:00:00Z"},
              {"id": "ch2", "name": "Old", "archived_at": "2026-11-02T00:00:00Z"},
              {"id": "ch3", "name": "Off", "disabled_at": "2026-11-02T00:00:00Z"}
            ]}
            """;
        var launch = Assert.Single(ClaudeAPI.Channels(json, Account)!);
        Assert.Equal(("ch1", "Launch", "Plans", true, true), (launch.Id, launch.Name, launch.Summary, launch.IsPrivate, launch.IsBeta));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero), launch.Updated);
        Assert.Equal("https://claude.ai/code/project/ch1", launch.OpenUrl);
        Assert.Null(ClaudeAPI.Channels("[]", Account));
    }

    [Fact(DisplayName = "Beta projects are asked of the Code service with its beta, version and organization")]
    public void AsksTheCodeService()
    {
        Assert.Equal("/v1/code/channels?scope=joined&include_members=true&include_archived=true&limit=200", ClaudeAPI.ChannelsPath);
        Assert.Equal("org", ClaudeAPI.CodeHeaders("org")["x-organization-uuid"]);
        Assert.Equal("ccr-byoc-2025-07-29", ClaudeAPI.CodeHeaders("org")["anthropic-beta"]);
    }
}