using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// Every signed-in account's artifacts in one list, and the groups they are
/// shared with. Lives on the UI thread; Changed is raised there.
/// </summary>
public sealed class ArtifactCenter
{
    public enum AccountState { Unknown, Loading, Ready, SignedOut, Failed }

    private static ArtifactCenter? _shared;
    public static ArtifactCenter Shared => _shared ??= new ArtifactCenter();

    public event Action? Changed;

    public List<ArtifactAccount> Accounts { get; private set; } = [];
    public List<Artifact> Artifacts { get; private set; } = [];
    public List<ArtifactGroup> Groups { get; private set; } = [];
    public string? Status { get; private set; }
    /// The last sign-in to finish, from whichever pane, so every pane can ask
    /// for that account at once.
    public (Guid Account, DateTimeOffset At)? LastSignIn { get; private set; }

    private readonly Dictionary<Guid, AccountState> _states = new();
    private readonly Dictionary<Guid, ClaudeWebSession> _sessions = new();
    private readonly Dictionary<Guid, List<Artifact>> _lists = new();
    /// Which load of each account is the latest, so one a sign-in has since
    /// superseded writes nothing over the one that followed it.
    private readonly Dictionary<Guid, int> _generations = new();
    private ArtifactSignInWindow? _signIn;
    /// False only when an unreadable file could not be set aside either, so
    /// saving would write over it.
    private bool _mayWriteAccounts = true, _mayWriteGroups = true;

    private ArtifactCenter()
    {
        var accountsWereRead = false;
        var accounts = ArtifactFiles.Read<List<ArtifactAccount>>(ArtifactFiles.Accounts);
        switch (accounts.State)
        {
            case ArtifactFiles.State.Read: Accounts = accounts.Value!; accountsWereRead = true; break;
            case ArtifactFiles.State.Unreadable: _mayWriteAccounts = SetAside(ArtifactFiles.Accounts); break;
        }
        var groups = ArtifactFiles.ReadEach<ArtifactGroup>(ArtifactFiles.Groups);
        switch (groups.State)
        {
            case ArtifactFiles.State.Read:
                Groups = groups.Value.Values;
                // The whole file goes aside before the groups that did read are
                // written back, so the one that did not is still on disk.
                if (groups.Value.Lost > 0)
                {
                    _mayWriteGroups = SetAside(ArtifactFiles.Groups);
                    if (_mayWriteGroups) ArtifactFiles.Save(Groups, ArtifactFiles.Groups);
                }
                break;
            case ArtifactFiles.State.Unreadable: _mayWriteGroups = SetAside(ArtifactFiles.Groups); break;
        }
        // Only against a list that was read: one set aside, or never written,
        // may name the accounts these folders belong to.
        if (accountsWereRead) _ = RemoveStrayStores();
    }

    private async Task RemoveStrayStores()
    {
        foreach (var id in ClaudeWebSession.StoreIdentifiers())
        {
            if (Account(id) is not null || _sessions.ContainsKey(id) || _signIn?.Account == id) continue;
            Diagnostics.Note("artifacts.store stray", new Dictionary<string, object?> { ["store"] = id.ToString()[..8] });
            await ClaudeWebSession.Forget(id);
        }
    }

    private static bool SetAside(string file)
    {
        var moved = ArtifactFiles.SetAside(file);
        Diagnostics.Note("artifacts.file unreadable", new Dictionary<string, object?> { ["file"] = Path.GetFileName(file), ["setAside"] = moved is null ? "could not move" : Path.GetFileName(moved) });
        return moved is not null;
    }

    private void SaveAccounts()
    {
        if (_mayWriteAccounts) ArtifactFiles.Save(Accounts, ArtifactFiles.Accounts);
    }

    public void SaveGroups(List<ArtifactGroup> groups)
    {
        Groups = groups;
        if (_mayWriteGroups) ArtifactFiles.Save(Groups, ArtifactFiles.Groups);
        Changed?.Invoke();
    }

    public AccountState StateOf(Guid account) => _states.GetValueOrDefault(account);
    public ArtifactAccount? Account(Guid id) => Accounts.FirstOrDefault(a => a.Id == id);

    /// The Claude an account is signed in to here, when there is one, since that
    /// is the name a person knows it by; its address otherwise.
    public string NameOf(Guid id)
    {
        if (Account(id) is not { } account) return "";
        if (account.AccountUuid is { } uuid && _claudeNames.TryGetValue(uuid, out var claude)) return claude;
        return account.Email ?? "Signing in…";
    }

    private Dictionary<string, string> _claudeNames = new();

    /// Requests under way, by action and artifact, so a control waits for its own.
    public HashSet<string> Busy { get; } = [];
    public bool AnyLoading => _states.Values.Contains(AccountState.Loading);
    public void ClearStatus() { Status = null; Changed?.Invoke(); }

    /// What another pane learned about a sign-in, so every pane says the same.
    public void NoteSignIn(Guid account, bool accepted)
    {
        if (Account(account) is null || StateOf(account) == AccountState.Loading) return;
        _states[account] = accepted ? AccountState.Ready : AccountState.SignedOut;
        Changed?.Invoke();
    }

    public void SignInNewAccount() => SignIn(Guid.NewGuid());

    public void SignInAgain(Guid id)
    {
        if (Account(id) is not null) SignIn(id);
    }

    private void SignIn(Guid id)
    {
        _signIn?.Close();
        ArtifactSignInWindow? window = null;
        window = new ArtifactSignInWindow(id, identity => SignedIn(id, identity), () =>
        {
            if (_signIn != window) return;
            _signIn = null;
            // A folder made for a new account that never finished signing in
            // holds nothing worth keeping.
            if (Account(id) is null && !_sessions.ContainsKey(id)) _ = ClaudeWebSession.Forget(id);
        });
        _signIn = window;
        window.Show();
    }

    private void SignedIn(Guid id, ArtifactAPI.Identity identity)
    {
        _signIn = null;
        var wasAnother = Account(id) is { } before && before.AccountUuid != identity.AccountUuid;
        var (accounts, retired) = ArtifactAccounts.SignedIn(Accounts, id, identity);
        Accounts = accounts;
        SaveAccounts();
        foreach (var earlier in retired) Retire(earlier);
        if (wasAnother) _lists.Remove(id);
        if (retired.Count > 0) Status = $"Signed in to {identity.Email} again";
        _sessions.GetValueOrDefault(id)?.Reset();
        LastSignIn = (id, DateTimeOffset.UtcNow);
        Changed?.Invoke();
        _ = Load(id, again: true);
    }

    public void SignOut(Guid id)
    {
        if (_signIn?.Account == id)
        {
            _signIn.Close();
            _signIn = null;
        }
        Accounts = Accounts.Where(a => a.Id != id).ToList();
        SaveAccounts();
        Retire(id);
        Artifacts = ArtifactAPI.Merge(Accounts.Where(a => _lists.ContainsKey(a.Id)).Select(a => _lists[a.Id]));
        Changed?.Invoke();
    }

    /// Lets go of every page on an account's folder, then removes the folder,
    /// which WebView2 holds open while any page on it is alive.
    private void Retire(Guid id)
    {
        _sessions.Remove(id, out var session);
        session?.Close();
        ClaudePageWindow.CloseAll(id);
        _lists.Remove(id);
        _states.Remove(id);
        _generations.Remove(id);
        _ = RemoveWhenReleased(session, id);
    }

    private static async Task RemoveWhenReleased(ClaudeWebSession? session, Guid id)
    {
        if (session is not null) await session.Released();
        await ClaudeWebSession.Forget(id);
    }

    /// When the pane is shown and from its refresh button, never on a timer:
    /// each pass is several requests per account to a service that did not ask.
    public void Refresh()
    {
        foreach (var account in Accounts.ToList()) _ = Load(account.Id);
        _ = RefreshNames();
    }

    private async Task RefreshNames()
    {
        var names = await Task.Run(ClaudeNamesHere);
        _claudeNames = names;
        Changed?.Invoke();
    }

    /// Off the UI thread: each Claude's account is read out of its profile.
    private static Dictionary<string, string> ClaudeNamesHere()
    {
        var names = new Dictionary<string, string>();
        var claudes = new List<(string Profile, string Name)> { (GraftPaths.DefaultProfile, "Claude") };
        claudes.AddRange(new ShortcutStore().Shortcuts.Select(s => (s.ProfileDir, s.Name)));
        foreach (var (profile, name) in claudes)
            if (Graft.Account(profile) is { } uuid) names.TryAdd(uuid, name);
        return names;
    }

    // Sharing and changing an artifact. Each runs once at a time per artifact,
    // and says how it went in the status line.

    private async Task Run(string key, Guid account, Func<Task> work)
    {
        if (!Busy.Add(key)) return;
        Changed?.Invoke();
        try { await work(); }
        catch (Exception e) when (e is ClaudeWebSession.WebFailure or System.Text.Json.JsonException)
        {
            Status = MessageFor(e, account);
        }
        finally
        {
            Busy.Remove(key);
            Changed?.Invoke();
        }
    }

    public void SetPublic(Artifact artifact, bool isPublic)
    {
        if (!artifact.Access.ChangesToPublic(isPublic)) return;
        _ = Run($"access-{artifact.Id}", artifact.Account, async () =>
        {
            var session = Session(artifact.Account);
            var permissions = await PermissionsOf(artifact, session);
            // Changed elsewhere since the list was read: what it is now is shown,
            // and nothing is sent to make it what it already is.
            if (permissions.Mode is { } mode && !ArtifactAccessOf.Audience(mode).ChangesToPublic(isPublic))
            {
                Replace(artifact.Id, a => a with { Audience = mode });
                return;
            }
            var headers = permissions.Version is { } version ? new Dictionary<string, string> { ["If-Match"] = version } : null;
            var (status, _) = await session.Send("PATCH", ArtifactAPI.PermPath(artifact.Id, artifact.Org),
                ArtifactAPI.ReadModeBody(isPublic, permissions.Invited), headers);
            if (status is < 200 or >= 300)
            {
                Status = $"claude.ai refused the change ({status})";
                return;
            }
            Status = isPublic ? $"Anyone with the link can view “{artifact.Title}”" : $"“{artifact.Title}” is private again";
            Replace(artifact.Id, a => a with { Audience = isPublic ? "public" : permissions.Invited.Count == 0 ? "owner" : "users" });
        });
    }

    /// One invitation per address, as claude.ai sends from its own panel, which
    /// also emails the person; an address that fails does not stop the rest.
    public void Share(Artifact artifact, ArtifactGroup group)
    {
        if (group.Emails.Count == 0) return;
        _ = Run($"share-{artifact.Id}", artifact.Account, async () =>
        {
            var session = Session(artifact.Account);
            var permissions = await PermissionsOf(artifact, session);
            int invited = 0, lowered = 0;
            string? refusal = null;
            foreach (var email in group.Emails.ToList())
            {
                var wanted = group.RoleOf(email);
                var role = wanted.OfferedBy(permissions);
                try
                {
                    var (status, _) = await session.Send("POST", ArtifactAPI.InvitePath(artifact.Id, artifact.Org), ArtifactAPI.InviteBody(email, role));
                    if (status is >= 200 and < 300)
                    {
                        invited++;
                        if (role != wanted) lowered++;
                    }
                    else refusal ??= status.ToString();
                }
                // Every address after this one would be turned away the same way.
                catch (ClaudeWebSession.WebFailure e) when (e.Reason != ClaudeWebSession.Failure.SignedOut) { refusal ??= "unanswered"; }
            }
            Status = $"Shared “{artifact.Title}” with {invited} of {group.Emails.Count} in {group.Name}"
                + (lowered == 0 ? "" : $", {lowered} with less than asked");
            if (invited > 0 && artifact.Access == ArtifactAccess.OnlyYou) Replace(artifact.Id, a => a with { Audience = "users" });
            if (refusal is not null) Diagnostics.Note("artifacts.invite refused", new Dictionary<string, object?> { ["status"] = refusal });
        });
    }

    private static async Task<ArtifactAPI.Permissions> PermissionsOf(Artifact artifact, ClaudeWebSession session)
    {
        var (status, body) = await session.Send("GET", ArtifactAPI.PermPath(artifact.Id, artifact.Org));
        if (status != 200 || ArtifactAPI.PermissionsFrom(body) is not { } permissions)
            throw new ClaudeWebSession.WebFailure(ClaudeWebSession.Failure.Unreadable);
        return permissions;
    }

    public void Rename(Artifact artifact, string title)
    {
        title = title.Trim();
        if (!artifact.IsOwn || title.Length == 0 || title == artifact.Title) return;
        _ = Run($"rename-{artifact.Id}", artifact.Account, async () =>
        {
            var answer = await Session(artifact.Account).Send("POST", ArtifactAPI.RetitlePath(artifact.Id, artifact.Org), ArtifactAPI.RetitleBody(title));
            if (answer.Status is < 200 or >= 300) { Status = Refused(answer); return; }
            Replace(artifact.Id, a => a with { Title = title });
            Status = $"Renamed “{title}”";
        });
    }

    /// The copy belongs to the account that made it, so the list is asked again
    /// for it rather than guessed.
    public void Duplicate(Artifact artifact)
    {
        if (!artifact.Duplicable) return;
        _ = Run($"duplicate-{artifact.Id}", artifact.Account, async () =>
        {
            var answer = await Session(artifact.Account).Send("POST", ArtifactAPI.DuplicatePath(artifact.Id, artifact.Org));
            if (answer.Status is < 200 or >= 300) { Status = Refused(answer); return; }
            Status = $"Made a copy of “{artifact.Title}”";
            await Load(artifact.Account);
        });
    }

    /// claude.ai keeps no bin for artifacts, so the pane asks first and says so.
    public void Delete(Artifact artifact)
    {
        if (!artifact.IsOwn) return;
        _ = Run($"delete-{artifact.Id}", artifact.Account, async () =>
        {
            var answer = await Session(artifact.Account).Send("DELETE", ArtifactAPI.ArtifactPath(artifact.Id, artifact.Org));
            // Already gone is what was wanted.
            if (answer.Status is < 200 or >= 300 && !(answer.Status == 404 && ArtifactAPI.Reason(answer.Body) == "not_found"))
            {
                Status = Refused(answer);
                return;
            }
            foreach (var key in _lists.Keys.ToList()) _lists[key] = _lists[key].Where(a => a.Id != artifact.Id).ToList();
            Artifacts = Artifacts.Where(a => a.Id != artifact.Id).ToList();
            Status = $"Deleted “{artifact.Title}”";
        });
    }

    /// Reads the artifact's files as its viewer does, asks where to put the
    /// export, and makes it here. Nothing of it goes anywhere but the file chosen.
    public void Export(Artifact artifact, ArtifactExportFormat format, Func<string, Task<string?>> askWhere)
    {
        _ = Run($"export-{artifact.Id}", artifact.Account, async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"graft-export-{Guid.NewGuid():N}");
            try
            {
                var files = await FetchFiles(artifact, folder);
                var single = files.Count == 1;
                if (await askWhere(ArtifactExport.FileName(artifact.Title, format, single)) is not { } destination) return;
                await ArtifactExporter.Write(folder, format, single, destination);
                Status = $"Exported “{artifact.Title}” to {Path.GetFileName(destination)}";
            }
            catch (ArtifactExporter.ExportFailure failure)
            {
                Status = failure.Text;
                Diagnostics.Note("artifacts.export failed", new Dictionary<string, object?> { ["reason"] = failure.Problem.ToString(), ["format"] = format.ToString() });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Status = new ArtifactExporter.ExportFailure(ArtifactExporter.Problem.Unwritable).Text;
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        });
    }

    private async Task<List<string>> FetchFiles(Artifact artifact, string folder)
    {
        var session = Session(artifact.Account);
        var (status, body) = await session.Send("GET", ArtifactAPI.ArtifactPath(artifact.Id, artifact.Org));
        if (status != 200) throw new ArtifactExporter.ExportFailure(status == 404 ? ArtifactExporter.Problem.Gone : ArtifactExporter.Problem.Refused, status);
        var source = ArtifactAPI.SourceFrom(body) ?? throw new ClaudeWebSession.WebFailure(ClaudeWebSession.Failure.Unreadable);
        long total = 0;
        var written = new List<string>();
        foreach (var path in source.Files)
        {
            if (ArtifactAPI.FileUrl(artifact.Id, source, path) is not { } url) continue;
            var file = await session.Download(url, ArtifactAPI.MaximumExportBytes - total);
            if (file.Status != 200)
            {
                // Only the page itself is needed; a file it lists that will not
                // come is left out, as a broken link in the page would be.
                if (path == "index.html") throw new ArtifactExporter.ExportFailure(ArtifactExporter.Problem.Refused, file.Status);
                continue;
            }
            total += file.Data.Length;
            if (total > ArtifactAPI.MaximumExportBytes) throw new ArtifactExporter.ExportFailure(ArtifactExporter.Problem.TooLarge);
            var target = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, file.Data);
            written.Add(path);
        }
        return written;
    }

    public void CopyLink(Artifact artifact)
    {
        if (artifact.OpenUrl is null) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(artifact.OpenUrl);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Status = artifact.Access is ArtifactAccess.OnlyYou or ArtifactAccess.Invited
            ? "Link copied · only people with access can open it"
            : "Link copied";
        Changed?.Invoke();
    }

    private static string Refused((int Status, string Body) answer) => ArtifactAPI.Reason(answer.Body) switch
    {
        "title_invalid" => "claude.ai won't take that title",
        "org_mismatch" => "claude.ai won't copy it into this account",
        { } reason when reason.Contains("limit") || reason.Contains("cap") => "claude.ai won't make more copies today",
        _ => $"claude.ai refused the change ({answer.Status})",
    };

    private string MessageFor(Exception error, Guid account)
    {
        if (error is ClaudeWebSession.WebFailure { Reason: ClaudeWebSession.Failure.SignedOut })
        {
            _states[account] = AccountState.SignedOut;
            return $"Sign in to {NameOf(account)} again";
        }
        return "claude.ai didn't answer";
    }

    private void Replace(string id, Func<Artifact, Artifact> change)
    {
        Artifacts = Artifacts.Select(a => a.Id == id ? change(a) : a).ToList();
        foreach (var key in _lists.Keys.ToList()) _lists[key] = _lists[key].Select(a => a.Id == id ? change(a) : a).ToList();
    }

    public ArtifactGroup AddGroup(string name)
    {
        var group = new ArtifactGroup { Id = Guid.NewGuid(), Name = name };
        SaveGroups([.. Groups, group]);
        return group;
    }

    public void DeleteGroup(Guid id) => SaveGroups(Groups.Where(g => g.Id != id).ToList());

    /// After an edit made in place on one of the groups.
    public void GroupsChanged() => SaveGroups(Groups);

    /// The organization an account's things live under, asked once and kept.
    public async Task<string> OrganizationOf(Guid id)
    {
        var account = Account(id) ?? new ArtifactAccount(id, null, null, null);
        if (account.Org is null || account.Email is null)
        {
            var identity = await Session(id).Identity();
            account = account with { Email = identity.Email, AccountUuid = identity.AccountUuid, Org = identity.Org };
            var index = Accounts.FindIndex(a => a.Id == id);
            if (index >= 0)
            {
                Accounts[index] = account;
                SaveAccounts();
            }
        }
        return account.Org ?? throw new ClaudeWebSession.WebFailure(ClaudeWebSession.Failure.Unreadable);
    }

    /// Shared with Projects and Search later. An account no longer listed gets
    /// one that answers signed out and loads nothing, so a request still
    /// running after a sign-out cannot build a page on a folder being removed.
    public ClaudeWebSession Session(Guid id)
    {
        if (_sessions.TryGetValue(id, out var existing)) return existing;
        var session = new ClaudeWebSession(id);
        if (Account(id) is null)
        {
            session.Close();
            return session;
        }
        _sessions[id] = session;
        return session;
    }

    private async Task Load(Guid id, bool again = false)
    {
        if (Account(id) is null || (!again && StateOf(id) == AccountState.Loading)) return;
        var generation = _generations.GetValueOrDefault(id) + 1;
        _generations[id] = generation;
        _states[id] = AccountState.Loading;
        Changed?.Invoke();
        var session = Session(id);
        List<Artifact>? found = null;
        Exception? failure = null;
        try
        {
            var org = await OrganizationOf(id);
            found = [];
            foreach (var rel in new[] { "mine", "shared" })
            {
                string? token = null;
                for (var i = 0; i < ArtifactAPI.MaximumPages; i++)
                {
                    var (status, body) = await session.Send("GET", ArtifactAPI.ListPath(rel, org, token));
                    if (status != 200 || ArtifactAPI.Page(body, id, org) is not { } page)
                        throw new ClaudeWebSession.WebFailure(ClaudeWebSession.Failure.Unreadable);
                    found.AddRange(page.Artifacts);
                    token = page.Next;
                    if (token is null) break;
                }
            }
        }
        catch (Exception e) { failure = e; }
        // A newer load owns what is shown now, and an account signed out while
        // this ran owns nothing.
        if (_generations.GetValueOrDefault(id) != generation || Account(id) is null) return;
        if (failure is null)
        {
            _lists[id] = found!;
            _states[id] = AccountState.Ready;
        }
        else if (failure is ClaudeWebSession.WebFailure { Reason: ClaudeWebSession.Failure.SignedOut })
        {
            _lists.Remove(id);
            _states[id] = AccountState.SignedOut;
        }
        else
        {
            _states[id] = AccountState.Failed;
            Diagnostics.Note("artifacts.list failed", new Dictionary<string, object?> { ["reason"] = failure.Message });
        }
        Artifacts = ArtifactAPI.Merge(Accounts.Where(a => _lists.ContainsKey(a.Id)).Select(a => _lists[a.Id]));
        Changed?.Invoke();
    }
}
