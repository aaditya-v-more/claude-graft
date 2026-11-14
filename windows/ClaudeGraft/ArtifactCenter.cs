using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;

namespace ClaudeGraft;

public sealed class ArtifactCenter : IDisposable
{
    private readonly AccountCenter _accounts;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private readonly Dictionary<Guid, List<Artifact>> _lists = new();
    private readonly Dictionary<Guid, long> _loads = new();
    private readonly HashSet<Guid> _loading = new();
    private readonly HashSet<string> _busy = new();
    public ObservableCollection<Artifact> Artifacts { get; } = new();
    public ArtifactGroupStore GroupStore { get; } = new();
    public string? Status { get; private set; }
    public bool Loading => _loading.Count != 0;
    public bool IsBusy(Artifact artifact) => _busy.Contains(artifact.Id);
    public event EventHandler? Changed;

    public ArtifactCenter(AccountCenter accounts)
    {
        _accounts = accounts;
        Status = GroupStore.Notice;
        accounts.Changed += AccountsChanged;
    }

    private void AccountsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // A store can be signed into as a different person. Its old list
        // must not remain visible under the new identity while loading.
        _lists.Clear();
        foreach (var id in _loads.Keys.ToList()) _loads[id]++;
        _loading.Clear();
        Rebuild();
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accounts.Changed -= AccountsChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Changed = null;
    }

    public async Task RefreshAsync()
    {
        foreach (var account in _accounts.Accounts.ToArray()) await LoadAsync(account);
    }

    private async Task LoadAsync(WebAccount account)
    {
        if (_disposed || !_accounts.IsCurrent(account)) return;
        var generation = _loads.GetValueOrDefault(account.Id) + 1;
        _loads[account.Id] = generation;
        var signIn = _accounts.Generation(account.Id);
        _loading.Add(account.Id); Notify();
        bool Current() => !_disposed && _accounts.IsCurrent(account) && signIn == _accounts.Generation(account.Id)
            && generation == _loads.GetValueOrDefault(account.Id);
        try
        {
            var found = new List<Artifact>();
            foreach (var relation in new[] { "mine", "shared" })
            {
                string? token = null;
                for (var page = 0; page < ArtifactApi.MaximumPages; page++)
                {
                    var json = await _accounts.RequestAsync(account, "GET", ArtifactApi.ListPath(relation, account.OrganizationId, token), cancellationToken: _lifetime.Token);
                    if (!Current()) return;
                    var parsed = ArtifactApi.Page(json, account.Id, account.OrganizationId)
                        ?? throw new IOException("claude.ai sent an unreadable artifact list.");
                    found.AddRange(parsed.Items); token = parsed.Next;
                    if (token is null) break;
                }
            }
            if (Current()) _lists[account.Id] = found;
        }
        catch (Exception e)
        {
            if (!Current()) return;
            if (e is WebAccountException { IsSignedOut: true }) _lists.Remove(account.Id);
            Status = Message(e, account);
        }
        finally
        {
            if (generation == _loads.GetValueOrDefault(account.Id)) _loading.Remove(account.Id);
            Rebuild();
        }
    }

    private void Rebuild()
    {
        if (_disposed) return;
        var merged = ArtifactApi.Merge(_accounts.Accounts.Select(a => _lists.GetValueOrDefault(a.Id) ?? []));
        Artifacts.Clear(); foreach (var artifact in merged) Artifacts.Add(artifact);
        Notify();
    }

    private async Task ActAsync(Artifact artifact, Func<WebAccount, Task> work)
    {
        if (_disposed || !_busy.Add(artifact.Id)) return;
        var account = _accounts.Accounts.FirstOrDefault(a => a.Id == artifact.Account);
        try
        {
            Notify();
            if (account is null || !_accounts.IsCurrent(account)) throw new IOException("Sign in to this account again.");
            await work(account);
        }
        catch (Exception e) { Status = account is null ? e.Message : Message(e, account); }
        finally { _busy.Remove(artifact.Id); Notify(); }
    }

    private static string Message(Exception e, WebAccount account) => e is WebAccountException { IsSignedOut: true }
        ? $"Sign in to {account.DisplayName} again." : e is OperationCanceledException ? "Canceled." : e.Message;
    private static void Accepted(WebResponse answer)
    {
        if (answer.StatusCode is >= 200 and < 300) return;
        var reason = ArtifactApi.Reason(answer.Json);
        throw new IOException(reason switch
        {
            "title_invalid" => "claude.ai won't take that title.", "org_mismatch" => "claude.ai won't copy it into this account.",
            { } r when r.Contains("limit") || r.Contains("cap") => "claude.ai won't make more copies today.",
            _ => $"claude.ai refused the change ({answer.StatusCode}).",
        });
    }

    private void Replace(Artifact artifact, Func<Artifact, Artifact> change)
    {
        if (!_accounts.Accounts.Any(a => a.Id == artifact.Account)) return;
        foreach (var list in _lists.Values)
            for (var i = 0; i < list.Count; i++) if (list[i].Id == artifact.Id) list[i] = change(list[i]);
        Rebuild();
    }

    private async Task<ArtifactPermissions> PermissionsAsync(WebAccount account, Artifact artifact) =>
        ArtifactApi.Permissions(await _accounts.RequestAsync(account, "GET", ArtifactApi.Path(artifact.Id, artifact.Organization, "perm")))
            ?? throw new IOException("claude.ai sent unreadable sharing permissions.");

    public Task SetPublicAsync(Artifact artifact, bool makePublic)
    {
        if (!artifact.IsOwn || !ArtifactApi.ChangesAccess(artifact.Access, makePublic)) return Task.CompletedTask;
        return ActAsync(artifact, async account =>
        {
            var permissions = await PermissionsAsync(account, artifact);
            if (permissions.Mode is { } mode && !ArtifactApi.ChangesAccess(ArtifactApi.Access(mode), makePublic))
            { Replace(artifact, a => a with { Audience = mode }); return; }
            var headers = permissions.Version is null ? null : new Dictionary<string, string> { ["If-Match"] = permissions.Version };
            Accepted(await _accounts.SendAsync(account, "PATCH", ArtifactApi.Path(artifact.Id, artifact.Organization, "perm"),
                ArtifactApi.AccessBody(makePublic, permissions.Invited), headers: headers));
            Replace(artifact, a => a with { Audience = makePublic ? "public" : permissions.Invited.Length == 0 ? "owner" : "users" });
            Status = makePublic ? $"Anyone with the link can view “{artifact.Title}”." : $"“{artifact.Title}” is private again.";
        });
    }

    public Task ShareAsync(Artifact artifact, ArtifactGroup group)
    {
        if (!artifact.IsOwn || group.Emails.Count == 0) return Task.CompletedTask;
        return ActAsync(artifact, async account =>
        {
            var permissions = await PermissionsAsync(account, artifact);
            var invited = 0; var lowered = 0;
            foreach (var email in group.Emails.ToArray())
            {
                var wanted = group.RoleOf(email); var role = ArtifactApi.Offered(wanted, permissions);
                try
                {
                    var answer = await _accounts.SendAsync(account, "POST", ArtifactApi.Path(artifact.Id, artifact.Organization, "invite"),
                        new JsonObject { ["email"] = email, ["role"] = role.ToString().ToLowerInvariant() });
                    if (answer.StatusCode is >= 200 and < 300) { invited++; if (wanted != role) lowered++; }
                }
                catch (Exception e) when (e is not WebAccountException { IsSignedOut: true }) { }
            }
            if (invited > 0 && artifact.Access == ArtifactAccess.OnlyYou) Replace(artifact, a => a with { Audience = "users" });
            Status = $"Shared “{artifact.Title}” with {invited} of {group.Emails.Count} in {group.Name}"
                + (lowered == 0 ? "." : $", {lowered} with less access than requested.");
        });
    }

    public Task RenameAsync(Artifact artifact, string title)
    {
        title = title.Trim();
        if (!artifact.IsOwn || !artifact.Renamable || title.Length == 0 || title == artifact.Title) return Task.CompletedTask;
        return ActAsync(artifact, async account =>
        {
            Accepted(await _accounts.SendAsync(account, "POST", ArtifactApi.Path(artifact.Id, artifact.Organization, "retitle"), new JsonObject { ["title"] = title }));
            Replace(artifact, a => a with { Title = title }); Status = $"Renamed “{title}”.";
        });
    }

    public Task DuplicateAsync(Artifact artifact) => !artifact.Duplicable || !artifact.DuplicateAvailable ? Task.CompletedTask : ActAsync(artifact, async account =>
    {
        Accepted(await _accounts.SendAsync(account, "POST", ArtifactApi.Path(artifact.Id, artifact.Organization, "duplicate")));
        Status = $"Made a copy of “{artifact.Title}”."; await LoadAsync(account);
    });

    public Task DeleteAsync(Artifact artifact) => !artifact.IsOwn ? Task.CompletedTask : ActAsync(artifact, async account =>
    {
        var answer = await _accounts.SendAsync(account, "DELETE", ArtifactApi.Path(artifact.Id, artifact.Organization));
        if (!(answer.StatusCode == 404 && ArtifactApi.Reason(answer.Json) == "not_found")) Accepted(answer);
        foreach (var list in _lists.Values) list.RemoveAll(a => a.Id == artifact.Id);
        Rebuild(); Status = $"Deleted “{artifact.Title}”.";
    });

    public Task ExportAsync(Artifact artifact, ArtifactExportFormat format, Func<bool, Task<string?>> chooseDestination) => ActAsync(artifact, async account =>
    {
        var cancellation = _lifetime.Token;
        var folder = Path.Combine(Path.GetTempPath(), "graft-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = ArtifactApi.Source(await _accounts.RequestAsync(account, "GET", ArtifactApi.Path(artifact.Id, artifact.Organization), cancellationToken: cancellation))
                ?? throw new IOException("claude.ai sent unreadable artifact files.");
            var total = 0; var count = 0;
            foreach (var path in source.Files)
            {
                if (total >= ArtifactApi.MaximumExportBytes) throw new IOException("That artifact is too large to export.");
                var url = ArtifactApi.FileUrl(artifact.Id, source, path) ?? throw new IOException("The artifact has an invalid file address.");
                byte[] bytes;
                try { bytes = await _accounts.DownloadAsync(account, url, ArtifactApi.MaximumExportBytes - total, cancellation); }
                catch (WebAccountException e) when (!e.IsSignedOut && e.StatusCode > 0 && path != "index.html") { continue; }
                total += bytes.Length;
                if (total > ArtifactApi.MaximumExportBytes) throw new IOException("That artifact is too large to export.");
                var destination = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
                if (!Fs.IsInside(destination, folder)) throw new IOException("The artifact contains an unsafe file path.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, bytes, cancellation); count++;
            }
            cancellation.ThrowIfCancellationRequested();
            var target = await chooseDestination(count == 1);
            if (target is null) return;
            cancellation.ThrowIfCancellationRequested();
            if (format == ArtifactExportFormat.Source) await ArtifactExport.WriteSourceAsync(folder, count == 1, target, cancellation);
            else await ArtifactRenderer.WriteAsync(folder, format, target, cancellation);
            Status = $"Exported “{artifact.Title}” to {Path.GetFileName(target)}.";
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    });

    public void Report(string text) { Status = text; Notify(); }
    private void Notify() { if (!_disposed) Changed?.Invoke(this, EventArgs.Empty); }
}
