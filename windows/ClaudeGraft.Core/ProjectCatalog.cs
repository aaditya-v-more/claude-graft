using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

public sealed record ProjectAccount(Guid Id, string AccountUuid, string OrganizationId, string Name, long Generation);
public interface IProjectAccounts
{
    IReadOnlyList<ProjectAccount> Accounts { get; }
    event EventHandler? Changed;
    bool IsCurrent(ProjectAccount account);
    void NoteSignIn(ProjectAccount account, bool accepted);
    Task<JsonNode?> RequestAsync(ProjectAccount account, string method, string path, JsonNode? body = null,
        IReadOnlyDictionary<string, string>? headers = null);
}
public sealed class ProjectRequestException(bool signedOut, Exception? inner = null) : Exception("claude.ai did not answer.", inner)
{
    public bool SignedOut { get; } = signedOut;
}
public enum ProjectLoadState { Loading, Ready, SignedOut, Failed }

// All continuations return to the caller's synchronization context. The manager
// owns one catalog across its panes, so changing a filter cannot start a request.
public sealed class ProjectCatalog : IDisposable
{
    private readonly IProjectAccounts _accounts;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Task<LocalCodeSession[]>> _readLocal;
    private readonly Dictionary<Guid, ClaudeProject[]> _projectLists = [];
    private readonly Dictionary<Guid, CodeList> _codeLists = [];
    private Dictionary<Guid, long> _generations = [];
    private LocalRead? _local;
    private Task<LocalRead>? _localReading;
    private long _searchGeneration;
    private bool _askedProjects;
    private bool _disposed;
    private sealed record CodeList(DateTimeOffset Read, RemoteCodeSession[] Sessions, bool Complete);
    private sealed record LocalRead(DateTimeOffset Read, LocalCodeSession[] Sessions);
    public static readonly TimeSpan ListLifetime = TimeSpan.FromMinutes(2);
    public event EventHandler? Changed;
    public ClaudeProject[] Projects { get; private set; } = [];
    public SearchHit[] Results { get; private set; } = [];
    public Dictionary<Guid, ProjectLoadState> ProjectStates { get; } = [];
    public Dictionary<Guid, ProjectLoadState> SearchStates { get; } = [];
    public string? Searched { get; private set; }
    public bool LoadingProjects => ProjectStates.Values.Contains(ProjectLoadState.Loading);
    public bool Searching => SearchStates.Values.Contains(ProjectLoadState.Loading);
    public SearchFilters Filters { get; set; } = new();
    public IEnumerable<SearchHit> Shown => Results.Where(hit => Filters.Admits(hit, _now()));

    public ProjectCatalog(IProjectAccounts accounts, Func<Task<LocalCodeSession[]>>? readLocal = null, Func<DateTimeOffset>? now = null)
    {
        _accounts = accounts; _now = now ?? (() => DateTimeOffset.UtcNow);
        _readLocal = readLocal ?? (() => Task.Run(LocalCodeSessions.Read));
        _generations = accounts.Accounts.ToDictionary(account => account.Id, account => account.Generation);
        accounts.Changed += AccountsChanged;
    }

    private void AccountsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var current = _accounts.Accounts;
        var changed = current.Where(account => !_generations.TryGetValue(account.Id, out var gen) || gen != account.Generation).ToArray();
        var ids = current.Select(account => account.Id).ToHashSet();
        foreach (var id in _generations.Keys.Where(id => !ids.Contains(id)).Concat(changed.Select(a => a.Id)).Distinct())
        {
            _projectLists.Remove(id); _codeLists.Remove(id); ProjectStates.Remove(id); SearchStates.Remove(id);
        }
        _generations = current.ToDictionary(account => account.Id, account => account.Generation);
        Results = Results.Where(hit => ids.Contains(hit.Account) && !changed.Any(a => a.Id == hit.Account)).ToArray();
        if (Filters.Account is Guid selected && !ids.Contains(selected)) Filters = Filters with { Account = null };
        MergeProjects(); Notify();
        if (_askedProjects) foreach (var account in changed) _ = LoadProjectsAsync(account);
        if (Searched is { } query && changed.Length > 0) _ = SearchAsync(query, replace: true);
    }

    public Task RefreshProjectsAsync()
    {
        _askedProjects = true;
        return Task.WhenAll(_accounts.Accounts.Select(LoadProjectsAsync));
    }

    private async Task LoadProjectsAsync(ProjectAccount account)
    {
        if (!Current(account) || ProjectStates.GetValueOrDefault(account.Id) == ProjectLoadState.Loading && ProjectStates.ContainsKey(account.Id)) return;
        ProjectStates[account.Id] = ProjectLoadState.Loading; Notify();
        try
        {
            var found = new List<ClaudeProject>();
            for (var page = 0; page < ProjectApi.MaximumProjectPages; page++)
            {
                var response = await _accounts.RequestAsync(account, "GET", ProjectApi.ProjectsPath(account.OrganizationId, page * ProjectApi.ProjectPageSize));
                if (!Current(account)) return;
                var parsed = ProjectApi.ParseProjects(response, account.Id); found.AddRange(parsed.Projects);
                if (!parsed.HasMore) break;
            }
            if (!Current(account)) return;
            _projectLists[account.Id] = found.DistinctBy(project => project.Id).ToArray();
            ProjectStates[account.Id] = ProjectLoadState.Ready; _accounts.NoteSignIn(account, true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (!Current(account)) return;
            var signedOut = e is ProjectRequestException { SignedOut: true };
            ProjectStates[account.Id] = signedOut ? ProjectLoadState.SignedOut : ProjectLoadState.Failed;
            if (signedOut) { _projectLists.Remove(account.Id); _accounts.NoteSignIn(account, false); }
        }
        MergeProjects(); Notify();
    }

    public Task SearchAsync(string text) => SearchAsync(text, replace: false);
    private async Task SearchAsync(string text, bool replace)
    {
        if (_disposed || Searching && !replace) return;
        var query = text.Trim();
        var generation = ++_searchGeneration;
        Results = []; SearchStates.Clear(); Searched = query.Length == 0 ? null : query;
        if (query.Length == 0) { Notify(); return; }
        var accounts = _accounts.Accounts.ToArray();
        foreach (var account in accounts) SearchStates[account.Id] = ProjectLoadState.Loading;
        _askedProjects = true;
        foreach (var account in accounts.Where(account => !ProjectStates.ContainsKey(account.Id))) _ = LoadProjectsAsync(account);
        Notify();
        var lists = new Dictionary<Guid, SearchHit[]>();
        await Task.WhenAll(accounts.Select(async account =>
        {
            try
            {
                var response = await _accounts.RequestAsync(account, "POST", ProjectApi.SearchPath(account.OrganizationId), ProjectApi.SearchBody(query));
                if (!Current(account) || generation != _searchGeneration) return;
                var chats = ProjectApi.ParseChats(response, account.Id);
                var code = await CodeSessionsAsync(account);
                if (!Current(account) || generation != _searchGeneration) return;
                var local = await LocalSessionsAsync(code.Read);
                if (!Current(account) || generation != _searchGeneration) return;
                lists[account.Id] = chats.Concat(ProjectApi.CodeHits(query, account.Id, account.AccountUuid, account.OrganizationId,
                    code.Sessions, code.Complete, code.Read, local)).ToArray();
                SearchStates[account.Id] = ProjectLoadState.Ready; _accounts.NoteSignIn(account, true);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (!Current(account) || generation != _searchGeneration) return;
                var signedOut = e is ProjectRequestException { SignedOut: true };
                SearchStates[account.Id] = signedOut ? ProjectLoadState.SignedOut : ProjectLoadState.Failed;
                if (signedOut) _accounts.NoteSignIn(account, false);
            }
            if (!Current(account) || generation != _searchGeneration) return;
            Results = ProjectApi.Interleave(_accounts.Accounts.Select(a => lists.GetValueOrDefault(a.Id) ?? [])); Notify();
        }));
    }

    private async Task<CodeList> CodeSessionsAsync(ProjectAccount account)
    {
        if (_codeLists.TryGetValue(account.Id, out var cached) && _now() - cached.Read < ListLifetime) return cached;
        var started = _now(); var found = new List<RemoteCodeSession>(); string? cursor = null; var complete = false;
        try
        {
            for (var page = 0; page < ProjectApi.MaximumCodePages; page++)
            {
                var response = await _accounts.RequestAsync(account, "GET", ProjectApi.CodePath(cursor), headers: ProjectApi.CodeHeaders(account.OrganizationId));
                var parsed = ProjectApi.ParseCode(response); found.AddRange(parsed.Sessions); cursor = parsed.Next;
                if (!Current(account)) return new(started, [], false);
                if (cursor is null) { complete = true; break; }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return new(started, found.ToArray(), false); }
        var result = new CodeList(started, found.DistinctBy(session => session.Id).ToArray(), complete);
        if (Current(account)) _codeLists[account.Id] = result;
        return result;
    }

    private async Task<LocalCodeSession[]> LocalSessionsAsync(DateTimeOffset list)
    {
        if (_local is { } cached && ProjectApi.LocalReadServes(cached.Read, list, _now(), ListLifetime)) return cached.Sessions;
        if (_localReading is { } pending)
        {
            var done = await pending;
            if (ProjectApi.LocalReadServes(done.Read, list, _now(), ListLifetime)) return done.Sessions;
        }
        async Task<LocalRead> Read() { var started = _now(); return new(started, await _readLocal()); }
        var reading = Read(); _localReading = reading;
        try
        {
            var done = await reading;
            if (_local is null || _local.Read < done.Read) _local = done;
            return done.Sessions;
        }
        finally { if (ReferenceEquals(_localReading, reading)) _localReading = null; }
    }

    public string[] Trouble(bool search) => _accounts.Accounts.Select(account => (Account: account, State: (search ? SearchStates : ProjectStates).GetValueOrDefault(account.Id)))
        .Where(item => item.State is ProjectLoadState.SignedOut or ProjectLoadState.Failed)
        .Select(item => item.State == ProjectLoadState.SignedOut ? "Sign in to " + item.Account.Name + " again" : item.Account.Name + " didn't answer").ToArray();
    private bool Current(ProjectAccount account) => !_disposed && _accounts.IsCurrent(account);
    private void MergeProjects() => Projects = ProjectApi.Merge(_accounts.Accounts.Select(account => _projectLists.GetValueOrDefault(account.Id) ?? []));
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() { _disposed = true; _searchGeneration++; _accounts.Changed -= AccountsChanged; }
}
