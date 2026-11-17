using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// Every signed-in account's projects in one list. The accounts are the
/// Artifacts pane's, in the same browser stores and asked from the same pages;
/// nothing here signs in, so an account added or signed out in either pane is
/// added or gone in both. Lives on the UI thread.
/// </summary>
public sealed class ProjectCenter
{
    public enum State { Unknown, Loading, Ready, SignedOut, Failed }

    private static ProjectCenter? _shared;
    public static ProjectCenter Shared => _shared ??= new ProjectCenter(ArtifactCenter.Shared);

    public event Action? Changed;
    public ArtifactCenter Accounts { get; }
    public List<ClaudeProject> Projects { get; private set; } = [];
    public Dictionary<Guid, State> ProjectStates { get; } = new();

    private readonly Dictionary<Guid, List<ClaudeProject>> _lists = new();
    private HashSet<Guid> _known;
    private (Guid, DateTimeOffset)? _lastSignIn;
    /// Whether the pane has asked yet: a sign-in made before it was ever shown
    /// waits for it, like every other account.
    private bool _asked;

    private ProjectCenter(ArtifactCenter accounts)
    {
        Accounts = accounts;
        _known = accounts.Accounts.Select(a => a.Id).ToHashSet();
        _lastSignIn = accounts.LastSignIn;
        accounts.Changed += AccountsChanged;
    }

    public bool Loading => ProjectStates.Values.Contains(State.Loading);

    /// The accounts claude.ai would not answer for, by name, for the line under
    /// the list.
    public List<string> Trouble() => Accounts.Accounts.Select(a => ProjectStates.GetValueOrDefault(a.Id) switch
    {
        State.SignedOut => $"Sign in to {Accounts.NameOf(a.Id)} again",
        State.Failed => $"{Accounts.NameOf(a.Id)} didn't answer",
        _ => null,
    }).OfType<string>().ToList();

    private void AccountsChanged()
    {
        var ids = Accounts.Accounts.Select(a => a.Id).ToHashSet();
        if (!ids.SetEquals(_known))
        {
            // An account signed out anywhere leaves this list with it.
            _known = ids;
            foreach (var gone in _lists.Keys.Where(k => !ids.Contains(k)).ToList()) _lists.Remove(gone);
            foreach (var gone in ProjectStates.Keys.Where(k => !ids.Contains(k)).ToList()) ProjectStates.Remove(gone);
            foreach (var gone in SearchStates.Keys.Where(k => !ids.Contains(k)).ToList()) SearchStates.Remove(gone);
            foreach (var gone in _codeLists.Keys.Where(k => !ids.Contains(k)).ToList()) _codeLists.Remove(gone);
            Results = Results.Where(h => ids.Contains(h.Account)).ToList();
            if (Filters.Account is { } chosen && !ids.Contains(chosen)) Filters = Filters with { Account = null };
            Merge();
        }
        // A sign-in from either pane is asked for here too, since a person just
        // asked for that account.
        if (Accounts.LastSignIn is { } signIn && signIn != _lastSignIn)
        {
            _lastSignIn = signIn;
            ProjectStates.Remove(signIn.Account);
            _codeLists.Remove(signIn.Account);
            if (_asked) _ = Load(signIn.Account);
            // A search on screen is run again so it covers the new account too.
            if (Searched is { } query) Search(query);
        }
        Changed?.Invoke();
    }

    /// When the pane is shown and from its refresh button, never on a timer.
    public void Refresh()
    {
        _asked = true;
        foreach (var account in Accounts.Accounts.ToList()) _ = Load(account.Id);
    }

    private async Task Load(Guid id)
    {
        if (Accounts.Account(id) is null || ProjectStates.GetValueOrDefault(id) == State.Loading) return;
        ProjectStates[id] = State.Loading;
        Changed?.Invoke();
        try
        {
            var org = await Accounts.OrganizationOf(id);
            var session = Accounts.Session(id);
            var found = new List<ClaudeProject>();
            for (var page = 0; page < ClaudeAPI.MaximumProjectPages; page++)
            {
                var (status, body) = await session.Send("GET", ClaudeAPI.ProjectsPath(org, page * ClaudeAPI.ProjectPageSize));
                if (status != 200 || ClaudeAPI.Projects(body, id) is not { } list)
                    throw new ClaudeWebSession.WebFailure(ClaudeWebSession.Failure.Unreadable);
                // The page itself asks no offset, so one that is ignored answers
                // the first page again; a page with nothing new is the end.
                var fresh = list.Projects.Where(p => found.All(f => f.Id != p.Id)).ToList();
                found.AddRange(fresh);
                if (!list.HasMore || fresh.Count == 0) break;
            }
            // An account without the Beta, or a Beta list that will not answer,
            // still has its classic projects listed.
            var (channelStatus, channels) = await session.Send("GET", ClaudeAPI.ChannelsPath, extraHeaders: ClaudeAPI.CodeHeaders(org));
            if (channelStatus == 200 && ClaudeAPI.Channels(channels, id) is { } beta) found.AddRange(beta);
            else Diagnostics.Note("projects.beta refused", new Dictionary<string, object?> { ["status"] = channelStatus });
            // Signed out while this ran: what it found goes with the account.
            if (Accounts.Account(id) is null) return;
            _lists[id] = found;
            ProjectStates[id] = State.Ready;
            Accounts.NoteSignIn(id, accepted: true);
        }
        catch (ClaudeWebSession.WebFailure e) when (e.Reason == ClaudeWebSession.Failure.SignedOut)
        {
            if (Accounts.Account(id) is null) return;
            _lists.Remove(id);
            ProjectStates[id] = State.SignedOut;
            Accounts.NoteSignIn(id, accepted: false);
        }
        catch (Exception e) when (e is ClaudeWebSession.WebFailure or System.Text.Json.JsonException)
        {
            if (Accounts.Account(id) is null) return;
            ProjectStates[id] = State.Failed;
            Diagnostics.Note("projects.list failed", new Dictionary<string, object?> { ["reason"] = e.Message });
        }
        finally
        {
            Merge();
            Changed?.Invoke();
        }
    }

    // Search. Everything the last search found, before the filters, and the
    // query it answers, which is not always what is in the box.

    public List<SearchHit> Results { get; private set; } = [];
    public Dictionary<Guid, State> SearchStates { get; } = new();
    public string? Searched { get; private set; }
    public SearchFilters Filters { get; set; } = new();
    public bool Searching => SearchStates.Values.Contains(State.Loading);

    private int _searchGeneration;
    /// Each account's whole Code session list, kept for the searches after the
    /// one that read it, since several pages of two hundred is a lot to ask again.
    private readonly Dictionary<Guid, (DateTimeOffset Read, List<RemoteCodeSession> Sessions, bool Complete)> _codeLists = new();
    public static readonly TimeSpan CodeListLifetime = TimeSpan.FromMinutes(2);
    /// This PC's transcripts, kept as long as the lists: a cold read of
    /// hundreds takes seconds, too long to stand in front of every search.
    private (DateTimeOffset Read, List<LocalCodeSession> Sessions)? _localRead;
    private Task<(DateTimeOffset Read, List<LocalCodeSession> Sessions)>? _localReading;

    public List<SearchHit> Shown()
    {
        var now = DateTimeOffset.UtcNow;
        return Results.Where(h => Filters.Admits(h, now)).ToList();
    }

    public List<string> SearchTrouble() => Accounts.Accounts.Select(a => SearchStates.GetValueOrDefault(a.Id) switch
    {
        State.SignedOut => $"Sign in to {Accounts.NameOf(a.Id)} again",
        State.Failed => $"{Accounts.NameOf(a.Id)} didn't answer",
        _ => null,
    }).OfType<string>().ToList();

    public string? ProjectName(string? id, Guid account) =>
        id is null ? null : Projects.FirstOrDefault(p => p.Id == id && p.Account == account)?.Name;

    /// On Return or the button, never as a person types: each search asks every
    /// account. The filters narrow what came back and ask nothing.
    public void Search(string text)
    {
        var query = text.Trim();
        var generation = ++_searchGeneration;
        if (query.Length == 0) { ClearSearch(); return; }
        Searched = query;
        Results = [];
        var ids = Accounts.Accounts.Select(a => a.Id).ToList();
        foreach (var id in ids) SearchStates[id] = State.Loading;
        // Only accounts never asked, so a project a chat is in can be named.
        foreach (var id in ids.Where(i => !ProjectStates.ContainsKey(i)).ToList()) { _asked = true; _ = Load(id); }
        Changed?.Invoke();
        var lists = new Dictionary<Guid, List<SearchHit>>();
        foreach (var id in ids) _ = SearchOne(id, query, generation, lists);
    }

    private async Task SearchOne(Guid id, string query, int generation, Dictionary<Guid, List<SearchHit>> lists)
    {
        List<SearchHit>? found = null;
        Exception? failure = null;
        try
        {
            var org = await Accounts.OrganizationOf(id);
            var session = Accounts.Session(id);
            var (status, body) = await session.Send("POST", SearchAPI.SearchPath(org), SearchAPI.SearchBody(query));
            if (status != 200 || SearchAPI.Chats(body, id) is not { } chats)
                throw new ClaudeWebSession.WebFailure(ClaudeWebSession.Failure.Unreadable);
            var code = await CodeSessions(id, org, session);
            var local = await LocalSessions(code.Read);
            found = [.. chats, .. SearchAPI.CodeHits(query, id, Accounts.Account(id)?.AccountUuid, org,
                code.Sessions, code.Complete, code.Read, local)];
        }
        catch (Exception e) when (e is ClaudeWebSession.WebFailure or System.Text.Json.JsonException) { failure = e; }
        // A newer search owns the list now, and an account signed out while this
        // ran would bring its hits back with it.
        if (generation != _searchGeneration || Accounts.Account(id) is null) return;
        if (found is not null)
        {
            lists[id] = found;
            SearchStates[id] = State.Ready;
            Accounts.NoteSignIn(id, accepted: true);
        }
        else if (failure is ClaudeWebSession.WebFailure { Reason: ClaudeWebSession.Failure.SignedOut })
        {
            SearchStates[id] = State.SignedOut;
            Accounts.NoteSignIn(id, accepted: false);
        }
        else
        {
            SearchStates[id] = State.Failed;
            Diagnostics.Note("search failed", new Dictionary<string, object?> { ["reason"] = failure?.Message });
        }
        Results = SearchAPI.Interleave(Accounts.Accounts.Where(a => lists.ContainsKey(a.Id)).Select(a => lists[a.Id]));
        Changed?.Invoke();
    }

    /// An account whose session list will not answer still has its chats
    /// searched; only the chats' failure fails it.
    private async Task<(List<RemoteCodeSession> Sessions, bool Complete, DateTimeOffset Read)> CodeSessions(Guid id, string org, ClaudeWebSession session)
    {
        if (_codeLists.TryGetValue(id, out var kept) && DateTimeOffset.UtcNow - kept.Read < CodeListLifetime)
            return (kept.Sessions, kept.Complete, kept.Read);
        // From before the walk: a session begun while it ran may be on a page already read.
        var started = DateTimeOffset.UtcNow;
        var found = new List<RemoteCodeSession>();
        string? cursor = null;
        var complete = false;
        try
        {
            for (var i = 0; i < SearchAPI.MaximumCodePages; i++)
            {
                var (status, body) = await session.Send("GET", SearchAPI.CodeSessionsPath(cursor), extraHeaders: ClaudeAPI.CodeHeaders(org));
                if (status != 200 || SearchAPI.CodeSessions(body) is not { } page)
                {
                    Diagnostics.Note("search.code-sessions refused", new Dictionary<string, object?> { ["status"] = status });
                    return (found, false, started);
                }
                found.AddRange(page.Sessions);
                cursor = page.Next;
                if (cursor is null) { complete = true; break; }
            }
        }
        catch (ClaudeWebSession.WebFailure) { return (found, false, started); }
        _codeLists[id] = (started, found, complete);
        return (found, complete, started);
    }

    /// This PC's transcripts, read no earlier than the list they stand beside.
    private async Task<List<LocalCodeSession>> LocalSessions(DateTimeOffset list)
    {
        var now = DateTimeOffset.UtcNow;
        if (_localRead is { } kept && SearchAPI.LocalReadServes(kept.Read, list, now, CodeListLifetime)) return kept.Sessions;
        if (_localReading is { } reading)
        {
            var done = await reading;
            if (SearchAPI.LocalReadServes(done.Read, list, DateTimeOffset.UtcNow, CodeListLifetime)) return done.Sessions;
        }
        var fresh = Task.Run(() => (DateTimeOffset.UtcNow, Graft.LocalCodeSessions()));
        _localReading = fresh;
        var result = await fresh;
        if (_localReading == fresh) _localReading = null;
        if (_localRead is not { } older || older.Read < result.Item1) _localRead = result;
        return result.Item2;
    }

    public void ClearSearch()
    {
        _searchGeneration++;
        Searched = null;
        Results = [];
        SearchStates.Clear();
        Changed?.Invoke();
    }

    private void Merge() =>
        Projects = ClaudeAPI.Merge(Accounts.Accounts.Where(a => _lists.ContainsKey(a.Id)).Select(a => _lists[a.Id]));
}
