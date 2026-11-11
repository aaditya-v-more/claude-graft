using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Microsoft.UI.Dispatching;

namespace ClaudeGraft;

public sealed class AccountCenter : IDisposable
{
    private readonly WebAccountStore _store = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<Guid, AccountWebSession> _sessions = new();
    private readonly Dictionary<Guid, long> _generations = new();
    private readonly Dictionary<Guid, bool?> _states = new();
    private readonly Dictionary<Guid, (AccountSignInWindow Window, Guid? Previous)> _signIns = new();
    private readonly HashSet<Guid> _retired = [];
    private bool _initialized;
    private bool _disposed;
    public ObservableCollection<WebAccount> Accounts { get; } = new();
    public string? Notice { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler? StateChanged;
    private static string RemovalJournal => Path.Combine(GraftPaths.OwnData, "web-account-removals.json");

    public AccountCenter()
    {
        foreach (var account in _store.Read()) Accounts.Add(account);
        Notice = _store.Notice;
    }

    public bool IsCurrent(WebAccount account) => !_disposed && Accounts.Any(a => a.Id == account.Id
        && a.AccountUuid == account.AccountUuid && a.OrganizationId == account.OrganizationId);
    public long Generation(Guid id) => _generations.GetValueOrDefault(id);
    public bool? State(Guid id) => _states.GetValueOrDefault(id);
    public void NoteSignIn(WebAccount account, bool accepted)
    {
        if (!IsCurrent(account) || _states.GetValueOrDefault(account.Id) == accepted) return;
        _states[account.Id] = accepted;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task InitializeAsync() => OnUiAsync(async () =>
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        try
        {
            if (File.Exists(RemovalJournal))
                foreach (var id in JsonSerializer.Deserialize<Guid[]>(File.ReadAllBytes(RemovalJournal)) ?? [])
                    if (id != Guid.Empty && Accounts.All(a => a.Id != id)) _retired.Add(id);
        }
        catch { Notice = "Some signed-out browser data still needs cleanup. It will be retried."; }
        try
        {
            if (_store.ReadExisting && Directory.Exists(AccountWebSession.StoresRoot)
                && (File.GetAttributes(AccountWebSession.StoresRoot) & FileAttributes.ReparsePoint) == 0)
                foreach (var directory in Directory.EnumerateDirectories(AccountWebSession.StoresRoot))
                    if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)
                        && Accounts.All(a => a.Id != id)
                        && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) _retired.Add(id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Notice = "Some browser data could not be checked yet. Cleanup will retry when Graft opens."; }
        foreach (var id in _retired.ToArray()) await RetireAsync(id);
    });

    public Task SignInAsync(WebAccount? again = null) => OnUiAsync(async () =>
    {
        if (_disposed) return;
        if (!_store.CanWrite) throw new IOException(Notice);
        if (again is not null && !IsCurrent(again)) return;
        var existing = _signIns.Values.FirstOrDefault(s => s.Previous == again?.Id);
        if (existing.Window is not null) { existing.Window.Activate(); return; }
        var id = Guid.NewGuid();
        var window = new AccountSignInWindow(id);
        _signIns[id] = (window, again?.Id);
        try
        {
            var identity = await window.ShowAsync();
            if (identity is null || _disposed || again is not null && !IsCurrent(again))
            { await RetireAsync(id); return; }
            // Re-signing into A as B replaces A's store and moves B's record to
            // the newly working sign-in. No cookie is copied between stores.
            var before = Accounts.Select(a => a.Id == again?.Id ? a with { Id = id } : a).ToArray();
            var (accounts, retired) = WebAccountPolicy.SignedIn(before, identity);
            _store.Save(accounts);
            foreach (var old in Accounts) _generations[old.Id] = Generation(old.Id) + 1;
            Accounts.Clear();
            foreach (var account in accounts) Accounts.Add(account);
            _states[id] = true;
            Notice = null;
            Changed?.Invoke(this, EventArgs.Empty);
            foreach (var old in retired.Concat(again is null ? [] : new[] { again.Id }).Distinct())
                if (old != id) await RetireAsync(old);
        }
        catch
        {
            if (Accounts.All(a => a.Id != id)) await RetireAsync(id);
            throw;
        }
        finally { _signIns.Remove(id); window.CloseSafely(); }
    });

    public Task SignOutAsync(WebAccount account) => OnUiAsync(async () =>
    {
        if (!IsCurrent(account)) return;
        var remaining = Accounts.Where(a => a.Id != account.Id).ToArray();
        _store.Save(remaining);
        foreach (var signIn in _signIns.Values.Where(s => s.Previous == account.Id).ToArray()) signIn.Window.CloseSafely();
        _generations[account.Id] = Generation(account.Id) + 1;
        Accounts.Remove(Accounts.Single(a => a.Id == account.Id));
        _states.Remove(account.Id);
        Changed?.Invoke(this, EventArgs.Empty);
        await RetireAsync(account.Id);
    });

    public Task<WebResponse> SendAsync(WebAccount account, string method, string path, JsonNode? body = null,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? headers = null) => OnUiAsync(async () =>
    {
        if (!IsCurrent(account)) throw SignedOut();
        var generation = Generation(account.Id);
        try
        {
            var response = await Session(account).SendAsync(method, path, body, headers, cancellationToken);
            if (!IsCurrent(account) || generation != Generation(account.Id)) throw SignedOut();
            if (WebAccountPolicy.MeansSignedOut(response.StatusCode, path))
            { NoteSignIn(account, false); throw SignedOut(); }
            if (WebAccountPolicy.NeedsAccountCheck(response.StatusCode, path))
            {
                var check = await Session(account).SendAsync("GET", "/api/account", null, null, cancellationToken);
                if (!IsCurrent(account) || generation != Generation(account.Id)) throw SignedOut();
                if (WebAccountPolicy.MeansSignedOut(check.StatusCode, "/api/account"))
                { NoteSignIn(account, false); throw SignedOut(); }
            }
            if (response.StatusCode is >= 200 and < 300) NoteSignIn(account, true);
            return response;
        }
        catch (WebAccountException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException) { throw; }
        catch (Exception e)
        {
            Diagnostics.Note("web-account.request.failed", new Dictionary<string, object?> { ["error"] = e.GetType().Name });
            throw new WebAccountException("The account browser could not complete this request. Check that Microsoft Edge WebView2 Runtime is installed, then try Refresh.");
        }
    });

    public async Task<JsonNode?> RequestAsync(WebAccount account, string method, string path, JsonNode? body = null,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = await SendAsync(account, method, path, body, cancellationToken, headers);
        if (response.StatusCode is < 200 or >= 300)
            throw new WebAccountException($"claude.ai refused this request ({response.StatusCode}).", response.StatusCode);
        if (response.Json is null && response.StatusCode != 204)
            throw new WebAccountException("claude.ai sent an unreadable response.");
        return response.Json;
    }

    public Task<byte[]> DownloadAsync(WebAccount account, Uri uri, int limit, CancellationToken cancellationToken = default) => OnUiAsync(async () =>
    {
        if (!IsCurrent(account)) throw SignedOut();
        var generation = Generation(account.Id);
        var result = await Session(account).DownloadAsync(uri, limit, cancellationToken);
        if (!IsCurrent(account) || generation != Generation(account.Id)) throw SignedOut();
        return result;
    });

    private AccountWebSession Session(WebAccount account)
    {
        if (!IsCurrent(account) || _retired.Contains(account.Id)) throw SignedOut();
        if (!_sessions.TryGetValue(account.Id, out var session)) _sessions[account.Id] = session = new(account.Id);
        return session;
    }

    private async Task RetireAsync(Guid id)
    {
        if (Accounts.Any(a => a.Id == id)) return;
        _retired.Add(id);
        SaveRetirements();
        _sessions.Remove(id, out var session);
        session ??= new(id);
        try
        {
            await session.RetireAsync();
            _retired.Remove(id);
            SaveRetirements();
            var folder = AccountWebSession.Folder(id);
            // Only Graft's UUID-named browser store is eligible. A busy browser
            // may keep cache files open after its sign-in has already been erased.
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                try { Directory.Delete(folder, true); } catch (IOException) { }
        }
        catch (Exception e)
        {
            Notice = "Signed out. Some browser data could not be removed yet; cleanup will retry when Graft opens.";
            Diagnostics.Note("web-account.removal.failed", new Dictionary<string, object?> { ["error"] = e.GetType().Name });
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { session.Dispose(); }
    }

    private void SaveRetirements()
    {
        try { Directory.CreateDirectory(GraftPaths.OwnData); AtomicWrite.Bytes(RemovalJournal, JsonSerializer.SerializeToUtf8Bytes(_retired)); }
        catch { Notice = "Browser cleanup could not be recorded. Unlisted account stores will be checked at the next launch."; }
    }
    private static WebAccountException SignedOut() => new("Sign in to this claude.ai account again.", 401, true);
    private Task OnUiAsync(Func<Task> action) => OnUiAsync(async () => { await action(); return true; });
    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception e) { completion.TrySetException(e); }
        })) completion.TrySetException(new ObjectDisposedException(nameof(AccountCenter)));
        return completion.Task;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var signIn in _signIns.Values.ToArray()) signIn.Window.CloseSafely();
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
    }
}
