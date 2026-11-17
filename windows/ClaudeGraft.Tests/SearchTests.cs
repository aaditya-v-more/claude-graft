using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class SearchTests
{
    private static readonly Guid Account = Guid.NewGuid();
    private static readonly DateTimeOffset Listed = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static LocalCodeSession Local(string bridge, string title, bool here = true, string owner = "acct", string org = "org",
        DateTimeOffset? active = null, bool carriedOn = false) => new()
    {
        BridgeIds = bridge.Length == 0 ? [] : [bridge], Owner = owner, Organization = org, Title = title,
        Folder = @"C:\work\graft", Transcript = $@"C:\t\{title}.jsonl", LastActive = active ?? Listed.AddHours(-1),
        FolderIsHere = here, CarriedOn = carriedOn,
    };

    private static List<SearchHit> Hits(IReadOnlyList<RemoteCodeSession> remote, IReadOnlyList<LocalCodeSession> local, bool complete = true) =>
        SearchAPI.CodeHits("graft", Account, "acct", "org", remote, complete, Listed, local);

    [Fact(DisplayName = "a search is asked as claude.ai's box asks it, twenty-five results with a hundred characters around each")]
    public void AskedAsTheBoxAsks()
    {
        Assert.Equal("/api/organizations/o/conversation/search/v2", SearchAPI.SearchPath("o"));
        Assert.Equal("""{"n":25,"query":"plan","target_snippet_size":100}""", SearchAPI.SearchBody("plan"));
        Assert.Equal("/v1/code/sessions?limit=200&cursor=a%2Fb", SearchAPI.CodeSessionsPath("a/b"));
        Assert.Equal("https://claude.ai/code/session_abc", SearchAPI.CodeSessionUrl("cse_abc"));
    }

    [Fact(DisplayName = "a chat hit reads its snippet, its matches, its project and whether it is archived")]
    public void ReadsChats()
    {
        const string json = """
            {"data": [{"conversation": {"uuid": "c1", "name": "Plan", "project_uuid": "p1", "is_archived": true, "is_starred": true,
                                         "updated_at": "2026-11-10T00:00:00Z"},
                       "matched_snippet": {"text": "the plan", "matches": [{"start": 4, "end": 8}, {"start": 5, "end": 5}]},
                       "title_matches": [{"start": 0, "end": 4}], "semantic_rank": 3},
                      {"conversation": {"uuid": "c2", "name": ""}, "title_matches": [{"start": 0, "end": 2}]}]}
            """;
        var hits = SearchAPI.Chats(json, Account)!;
        Assert.Equal(("Plan", "the plan", "p1", true, true, 3), (hits[0].Title, hits[0].Snippet, hits[0].Project, hits[0].IsArchived, hits[0].IsStarred, hits[0].Rank));
        Assert.Equal([(4, 8)], hits[0].SnippetMatches);
        Assert.Equal("https://claude.ai/chat/c1", hits[0].OpenUrl);
        Assert.Equal("Untitled", hits[1].Title);
        Assert.Empty(hits[1].TitleMatches);
        Assert.Equal(1, hits[1].Rank);
    }

    [Fact(DisplayName = "a Code session is placed by its transcript here, in the cloud by its environment, and elsewhere otherwise")]
    public void PlacesSessions()
    {
        var remote = new List<RemoteCodeSession>
        {
            new("cse_here", "graft work", false, false, null, null),
            new("cse_carried", "graft notes", false, false, null, null),
            new("cse_cloud", "graft cloud", false, true, null, null),
            new("cse_away", "graft away", false, false, null, null),
            new("cse_other", "unrelated", false, false, null, null),
        };
        var hits = Hits(remote, [Local("cse_here", "a"), Local("cse_carried", "b", here: false)]);
        Assert.Equal([SearchHit.Places.ThisPC, SearchHit.Places.OtherPC, SearchHit.Places.Cloud, SearchHit.Places.OtherPC],
            hits.Select(h => h.Place!.Value));
        Assert.Equal(@"C:\work\graft", hits[0].Snippet);
    }

    [Fact(DisplayName = "a transcript is called gone from claude.ai only against the whole list of its own account and organization")]
    public void GoneOnlyWhenSure()
    {
        var local = new List<LocalCodeSession>
        {
            Local("cse_lost", "graft lost"),
            Local("cse_other_account", "graft theirs", owner: "someone"),
            Local("cse_other_org", "graft team", org: "team"),
            Local("cse_since", "graft since", active: Listed.AddMinutes(1)),
            Local("cse_half", "graft half", carriedOn: true),
        };
        var gone = Assert.Single(Hits([], local));
        Assert.Equal((SearchHit.Places.GoneFromClaude, "graft lost", null), (gone.Place!.Value, gone.Title, gone.OpenUrl));
        Assert.Empty(Hits([], local, complete: false));
    }

    [Fact(DisplayName = "every word of the query must be somewhere, and the title's own matches are picked out")]
    public void MatchesWords()
    {
        Assert.Equal([(0, 5)], SearchAPI.CodeMatch("graft fix", "Graft window", "fix the bug"));
        Assert.Null(SearchAPI.CodeMatch("graft fix", "Graft window"));
        Assert.Null(SearchAPI.CodeMatch("  ", "anything"));
    }

    [Fact(DisplayName = "a read of the transcripts serves a list only when it is fresh and no older than the list")]
    public void LocalReadServes()
    {
        var life = TimeSpan.FromMinutes(2);
        Assert.True(SearchAPI.LocalReadServes(Listed, Listed, Listed.AddMinutes(1), life));
        Assert.False(SearchAPI.LocalReadServes(Listed.AddSeconds(-1), Listed, Listed, life));
        Assert.False(SearchAPI.LocalReadServes(Listed, Listed, Listed.AddMinutes(3), life));
    }

    [Fact(DisplayName = "accounts' chats are dealt in turn by rank, and Code sessions follow newest first")]
    public void Interleaves()
    {
        SearchHit Chat(string id, int rank) => new() { Id = id, Account = Account, Kind = SearchHit.Kinds.Chat, Title = id, Rank = rank };
        SearchHit Code(string id, int day) => new() { Id = id, Account = Account, Kind = SearchHit.Kinds.Code, Title = id, Updated = Listed.AddDays(day) };
        var merged = SearchAPI.Interleave([[Chat("a2", 2), Chat("a1", 1), Code("old", -3)], [Chat("b1", 1), Code("new", -1)]]);
        Assert.Equal(["a1", "b1", "a2", "new", "old"], merged.Select(h => h.Id));
    }

    [Fact(DisplayName = "the filters start at active, and a place, project or star leaves the other kind out")]
    public void Filters()
    {
        var chat = new SearchHit { Id = "c", Account = Account, Kind = SearchHit.Kinds.Chat, Title = "c", Updated = Listed };
        var code = new SearchHit { Id = "s", Account = Account, Kind = SearchHit.Kinds.Code, Title = "s", Place = SearchHit.Places.Cloud, Updated = Listed };
        var plain = new SearchFilters();
        Assert.False(plain.IsNarrowed);
        Assert.False(plain.Admits(chat with { IsArchived = true }, Listed));
        Assert.True(new SearchFilters { Place = SearchFilters.Places.Cloud }.Admits(code, Listed));
        Assert.False(new SearchFilters { Place = SearchFilters.Places.Cloud }.Admits(chat, Listed));
        Assert.True(new SearchFilters { Project = "" }.Admits(chat, Listed));
        Assert.False(new SearchFilters { Project = "" }.Admits(code, Listed));
        Assert.False(new SearchFilters { Age = SearchFilters.Ages.Day }.Admits(chat, Listed.AddDays(2)));
        Assert.False(new SearchFilters { StarredOnly = true }.Admits(chat, Listed));
    }
}
