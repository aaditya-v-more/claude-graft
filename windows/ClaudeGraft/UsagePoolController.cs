using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeGraft.Core;
using Microsoft.UI.Dispatching;

namespace ClaudeGraft;

public sealed record PoolAccountRow(string Id, string Name, string? Profile, string? Account, string State,
    PoolLimits? Limits, bool SavedAccount, bool Ready);
public sealed record PoolBorrowerRow(string Id, string Name, string Profile, string[] Lenders, string? Note, bool Connected);
public sealed record PoolConversationRow(string Id, string Title, string From, string? ServedBy, string? Assigned,
    string[] Choices, DateTimeOffset LastAt, string? Note)
{
    public string? Answering => Note is null ? Assigned ?? ServedBy : ServedBy ?? Assigned;
}

/// <summary>Keeps the independent relay configured; disposing the manager never strands a running Code session.</summary>
public sealed class UsagePoolController : IDisposable
{
    private readonly ShortcutStore store;
    private readonly DispatcherQueue? dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, (PoolMember Member, DateTimeOffset At)> lastGood = [];
    private readonly Dictionary<string, DateTimeOffset> unreadableSince = [];
    private readonly Dictionary<string, (PoolLimits? Limits, DateTimeOffset Next, string? Note)> accountUsage = [];
    private Task? monitor;
    private CancellationTokenSource? signIn;
    private string? stagedRelay, stagedDigest;
    public IReadOnlyList<PoolAccountRow> Accounts { get; private set; } = [];
    public IReadOnlyList<PoolBorrowerRow> Borrowers { get; private set; } = [];
    public IReadOnlyList<PoolConversationRow> Conversations { get; private set; } = [];
    public PoolStatus? Status { get; private set; }
    public string? Problem { get; private set; }
    public bool SigningIn => signIn is not null;
    public string? SigningInFor { get; private set; }
    public event Action? Changed;

    public UsagePoolController(ShortcutStore store) => this.store = store;
    public void Start() => monitor ??= MonitorAsync();
    private async Task MonitorAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            await RefreshAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(10), stop.Token); } catch (OperationCanceledException) { return; }
        }
    }
    private void Notify()
    {
        if (dispatcher is null || dispatcher.HasThreadAccess) Changed?.Invoke();
        else dispatcher.TryEnqueue(() => Changed?.Invoke());
    }
    public string Name(string? id) => Accounts.FirstOrDefault(a => a.Id == id)?.Name ?? id ?? "Not answered yet";

    public async Task RefreshAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            var profiles = store.Shortcuts.Select(s => (Id: s.Id.ToString(), s.Name, Profile: s.ProfileDir)).Prepend(("main", "Main Claude", GraftPaths.DefaultProfile)).ToArray();
            var result = await Task.Run(() => RefreshCoreAsync(profiles), stop.Token);
            Accounts = result.Accounts; Borrowers = result.Borrowers; Conversations = result.Conversations; Status = result.Status; Problem = result.Problem;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidOperationException)
        { Problem = e is IOException ? e.Message : "Shared Usage could not refresh. Your saved settings have been preserved."; }
        finally { gate.Release(); Notify(); }
    }
    private sealed record Snapshot(PoolAccountRow[] Accounts, PoolBorrowerRow[] Borrowers, PoolConversationRow[] Conversations, PoolStatus? Status, string? Problem);
    private async Task<Snapshot> RefreshCoreAsync((string Id, string Name, string Profile)[] profiles)
    {
        PoolSettings settings; PoolAccount[] saved; PoolEnvironment.Journal journal;
        using (StateLock.Acquire())
        {
            settings = PoolSettings.Load(); saved = PoolAccount.Load(); journal = PoolEnvironment.LoadJournal();
            if (settings.PruneMembers(profiles.Select(p => p.Id), saved.Select(a => a.Id))) settings.Save();
        }
        var accounts = new List<PoolAccountRow>(); var members = new List<PoolMember>(); var now = DateTimeOffset.UtcNow;
        var configured = settings.Enabled || journal.Profiles.Count > 0;
        var involved = settings.Borrowers.Where(p => p.Value.Length > 0).SelectMany(p => p.Value.Prepend(p.Key)).ToHashSet();
        var credentialsComplete = true;
        foreach (var profile in profiles)
        {
            string? account = null; string? note = null; ClaudeCredentials.PoolLogin login = new([], null);
            PoolLimits? limits = null;
            try
            {
                account = Graft.Account(profile.Profile);
                if (configured) login = ClaudeCredentials.ReadPoolLogin(profile.Profile);
                unreadableSince.Remove(profile.Id);
                if (configured && login.Identify.Length > 0)
                {
                    var usage = await UsageMonitor.ReadAsync(profile.Profile);
                    if (usage.Usage is { } value) limits = new((value.FiveHour ?? 0) / 100d, value.FiveHourReset, value.Week / 100d, value.WeekReset, null, value.Sampled);
                    note = login.Lending is null ? "Open this Claude to renew its lending login." : null;
                }
            }
            catch (ClaudeCredentials.CredentialException e)
            {
                note = e.Message;
                if (involved.Contains(profile.Id))
                {
                    if (e.Reason == ClaudeCredentials.Failure.NoKey) credentialsComplete = false;
                    if (e.Reason == ClaudeCredentials.Failure.ConfigUnreadable)
                    {
                        unreadableSince.TryAdd(profile.Id, now);
                        if (!lastGood.ContainsKey(profile.Id) && now - unreadableSince[profile.Id] < TimeSpan.FromMinutes(10)) credentialsComplete = false;
                    }
                }
                if (e.Reason is ClaudeCredentials.Failure.ConfigUnreadable or ClaudeCredentials.Failure.NoKey && lastGood.TryGetValue(profile.Id, out var previous) && now - previous.At < TimeSpan.FromMinutes(10))
                {
                    var retained = previous.Member with { BorrowsFrom = settings.Borrowers.GetValueOrDefault(profile.Id) ?? [] };
                    members.Add(retained); accounts.Add(new(profile.Id, profile.Name, profile.Profile, retained.Account, "Login temporarily unreadable; retaining the last working login.", retained.Limits, false, retained.Token is not null)); continue;
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException) { note = "This profile's login could not be read. The next refresh will try again."; }
            var member = new PoolMember(profile.Id, profile.Name, account, login.Identify, login.Lending?.Value, limits, settings.Borrowers.GetValueOrDefault(profile.Id) ?? []);
            members.Add(member);
            if (login.Identify.Length > 0) lastGood[profile.Id] = (member, now);
            accounts.Add(new(profile.Id, profile.Name, profile.Profile, account, note ?? (account is null ? "Not signed in" : configured && login.Lending is null ? "Not ready to lend" : "Ready"), limits, false, account is not null && (!configured || login.Lending is not null)));
        }
        foreach (var account in saved)
        {
            string? token = null; var note = account.IsCurrent ? "Ready" : "Sign in again to renew this account.";
            if (account.IsCurrent && account.Scopes.Contains(ClaudeCredentials.InferenceScope))
            {
                try { token = account.Token(); }
                catch (Exception e) when (e is CryptographicException or FormatException)
                { note = "This saved account could not be unlocked for this Windows user."; if (involved.Contains(account.Id)) credentialsComplete = false; }
            }
            var known = accountUsage.GetValueOrDefault(account.Id);
            if (token is not null && settings.Borrowers.Values.Any(ids => ids.Contains(account.Id)) && now >= known.Next)
            {
                try
                {
                    var reading = await UsageApi.FetchAsync(token, stop.Token);
                    known = (new((reading.FiveHour ?? 0) / 100d, reading.FiveHourReset, reading.Week / 100d, reading.WeekReset, null, reading.Sampled), now.AddMinutes(5), null);
                }
                catch (UsageApi.Failure e) { known = (known.Limits, now + (e.RetryAfter ?? TimeSpan.FromMinutes(5)), "Usage will appear when this account answers a request."); }
                catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { known = (known.Limits, now.AddMinutes(2), "Usage could not refresh yet."); }
                accountUsage[account.Id] = known;
            }
            members.Add(new(account.Id, account.Name, account.AccountUuid, token is null ? [] : [UsagePool.Digest(token)], token, known.Limits, []));
            accounts.Add(new(account.Id, account.Name, null, account.AccountUuid, token is null ? note : known.Note ?? note, known.Limits, true, token is not null));
        }

        var wantsRelay = settings.Enabled || journal.Profiles.Count > 0;
        PoolStatus? status = null; string? problem = null; var ready = false;
        if (wantsRelay)
        {
            var unreadableReply = false;
            try
            {
                try { status = await PoolControl.SendAsync(new(), stop.Token); }
                catch (JsonException) { unreadableReply = true; }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
                ready = status is not null || unreadableReply;
                var bundled = Path.Combine(AppContext.BaseDirectory, "relay");
                if (stagedRelay is null) { stagedRelay = PoolControl.StageRelay(bundled); stagedDigest = PoolControl.BundleDigest(bundled); }
                journal.Relay = stagedRelay; journal.Owner = Path.Combine(AppContext.BaseDirectory, "ClaudeGraft.exe");
                var step = PoolDelivery.Decide(status, unreadableReply, stagedDigest!, credentialsComplete || !settings.Enabled);
                if (step == PoolDelivery.Step.Replace)
                {
                    try { await PoolControl.SendAsync(new(Quit: true), stop.Token); } catch (JsonException) { }
                    status = null; ready = false;
                }
                if (step != PoolDelivery.Step.Keep) ready = await PoolControl.EnsureRelayAsync(stagedRelay);
                if (!ready) problem = "The local usage relay could not start. Closed Claudes will open using their own accounts.";
                else if (!credentialsComplete && settings.Enabled) problem = "A lending login is temporarily unreadable. The existing relay keeps its last working configuration; refresh after the login can be read.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
            { problem = "The local usage relay could not be prepared. Closed Claudes will open using their own accounts."; }
        }

        var notes = new Dictionary<string, string?>(); var holds = false;
        using (StateLock.Acquire())
        {
            // Uninstall or another manager can disable sharing while credential reads
            // are in flight. Re-read under the same lock as environment settlement and
            // send synchronously here so the mutex is released by its acquiring thread.
            settings = PoolSettings.Load();
            journal = PoolEnvironment.LoadJournal();
            if (stagedRelay is not null) { journal.Relay = stagedRelay; journal.Owner = Path.Combine(AppContext.BaseDirectory, "ClaudeGraft.exe"); }
            if (status is not null && settings.PruneAssignments(status.Sessions, now)) settings.Save();
            members = members.Select(m => m with { BorrowsFrom = settings.Borrowers.GetValueOrDefault(m.Id) ?? [] }).ToList();
            if (ready)
            {
                try
                {
                    var command = PoolDelivery.Command(new(settings.Enabled, members.ToArray(), settings.Assignments), credentialsComplete, settings.RevocationPending);
                    status = PoolControl.SendAsync(command, stop.Token).GetAwaiter().GetResult();
                    if (settings.Acknowledge(command, status)) settings.Save();
                    if (!credentialsComplete && !status.Enabled && settings.Enabled) problem = "Lending is paused until the remaining logins can be read. Removed accounts are no longer used.";
                }
                catch (JsonException) when (!credentialsComplete && settings.Enabled) { }
                catch (Exception e) when (e is IOException or OperationCanceledException or JsonException)
                { ready = false; problem = "The local usage relay did not answer. Closed Claudes will open using their own accounts."; }
            }
            var processes = ClaudeProcesses.Enumerate();
            foreach (var profile in profiles)
            {
                var wanted = settings.Borrowers.GetValueOrDefault(profile.Id)?.Length > 0;
                if (!wanted && !journal.Profiles.ContainsKey(Path.GetFullPath(profile.Profile))) continue;
                try
                {
                    var inspection = PoolEnvironment.InspectAndApply(profile.Profile, wanted, journal, ClaudeProcesses.IsRunning(profile.Profile, processes), ready);
                    notes[profile.Id] = inspection.Note; holds |= inspection.HoldsLines;
                    if (!wanted && !inspection.HoldsLines) journal.Profiles.Remove(Path.GetFullPath(profile.Profile));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { notes[profile.Id] = e.Message; holds |= journal.Profiles.ContainsKey(Path.GetFullPath(profile.Profile)); }
            }
            // A removed or renamed shortcut still needs its old environment restored.
            foreach (var path in journal.Profiles.Keys.Except(profiles.Select(p => Path.GetFullPath(p.Profile)), StringComparer.OrdinalIgnoreCase).ToArray())
            {
                journal.Profiles[path] = journal.Profiles[path] with { Wanted = false };
                if (!Directory.Exists(path)) { journal.Profiles.Remove(path); continue; }
                if (ClaudeProcesses.IsRunning(path, processes)) { holds = true; continue; }
                try
                {
                    var inspection = PoolEnvironment.InspectAndApply(path, false, journal, false, false);
                    if (inspection.HoldsLines) holds = true; else journal.Profiles.Remove(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { holds = true; }
            }
            if (wantsRelay) PoolEnvironment.WriteJournal(journal);
            if (!settings.Enabled && !holds && ready)
            {
                try { PoolControl.SendAsync(new(new(false, []), Quit: true), stop.Token).GetAwaiter().GetResult(); }
                catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { }
            }
        }
        var borrowers = profiles.Select(p => new PoolBorrowerRow(p.Id, p.Name, p.Profile, settings.Borrowers.GetValueOrDefault(p.Id) ?? [], notes.GetValueOrDefault(p.Id),
            status?.Members.FirstOrDefault(m => m.Id == p.Id)?.LastSeen > now.AddMinutes(-2))).ToArray();
        var conversations = (status?.Sessions ?? []).Where(s => now - s.LastAt < TimeSpan.FromMinutes(30))
            .Select(s => (Session: s, Filed: profiles.FirstOrDefault(p => p.Id == s.From).Profile is string profile ? PoolConversationMetadata.Read(profile, s.Id) : new PoolConversationMetadata.Reading(null, false, null)))
            .Where(s => !s.Filed.Hidden).OrderByDescending(s => s.Filed.FocusedAt ?? s.Session.PromptedAt ?? s.Session.StartedAt ?? s.Session.LastAt)
            .Select(pair => { var s = pair.Session; return new PoolConversationRow(s.Id, pair.Filed.Title ?? (s.Model is null ? "Conversation " + s.Id[..Math.Min(8, s.Id.Length)] : s.Model + " · " + s.Id[..Math.Min(8, s.Id.Length)]),
                s.From, s.ServedBy, settings.Assignments.GetValueOrDefault(s.Id),
                new[] { s.From }.Concat(settings.Borrowers.GetValueOrDefault(s.From) ?? []).Distinct().ToArray(), s.LastAt,
                s.PassedOver.Length == 0 ? null : "An account was unavailable; this conversation continued with another available account."); })
            .Take(5).ToArray();
        var accountRows = accounts.Select(account =>
        {
            var observed = status?.Members.FirstOrDefault(m => m.Id == account.Id);
            return account with
            {
                Limits = observed?.Limits is { } latest && (account.Limits is null || latest.Observed >= account.Limits.Observed) ? latest : account.Limits,
                State = observed?.TokenRefused == true ? "This lending login was refused. Sign in again to renew it." : account.State,
                Ready = account.Ready && observed?.TokenRefused != true,
            };
        }).ToArray();
        return new(accountRows, borrowers, conversations, status, problem);
    }

    public async Task SetLenderAsync(string borrower, string lender, bool enabled)
    {
        await gate.WaitAsync();
        try
        {
            using var locked = StateLock.Acquire(); var settings = PoolSettings.Load();
            settings.SetLender(borrower, lender, enabled); settings.Save();
        }
        finally { gate.Release(); }
        await RefreshAsync();
    }
    public async Task AssignAsync(string session, string? member)
    {
        await gate.WaitAsync();
        try { using var locked = StateLock.Acquire(); PoolSettings.Load().Assign(session, member); }
        finally { gate.Release(); }
        await RefreshAsync();
    }
    public async Task SignInAsync(string borrower)
    {
        if (signIn is not null) return;
        signIn = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); SigningInFor = borrower; Problem = null; Notify();
        string? signInProblem = null;
        try
        {
            var own = Accounts.FirstOrDefault(a => a.Id == borrower)?.Account;
            var account = await PoolAccountSignIn.SignInAsync(uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), own, signIn.Token);
            await gate.WaitAsync();
            try
            {
                using var locked = StateLock.Acquire();
                PoolAccount.Save(PoolAccount.Load().Where(a => a.Id != account.Id).Append(account));
                var settings = PoolSettings.Load(); settings.Borrowers[borrower] = (settings.Borrowers.GetValueOrDefault(borrower) ?? []).Append(account.Id).Distinct().ToArray(); settings.Save();
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { signInProblem = "Account sign-in was cancelled or timed out."; }
        catch (Exception e) when (e is IOException or HttpRequestException or CryptographicException or System.ComponentModel.Win32Exception) { signInProblem = e is IOException ? e.Message : "The account sign-in could not be completed. Try again."; }
        finally { signIn.Dispose(); signIn = null; SigningInFor = null; Notify(); }
        await RefreshAsync();
        if (signInProblem is not null) { Problem = signInProblem; Notify(); }
    }
    public void CancelSignIn() => signIn?.Cancel();
    public async Task ForgetAccountAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            using var locked = StateLock.Acquire(); PoolAccount.Save(PoolAccount.Load().Where(a => a.Id != id));
            var settings = PoolSettings.Load();
            settings.ForgetLender(id);
            settings.Save(); accountUsage.Remove(id);
        }
        finally { gate.Release(); }
        await RefreshAsync();
    }
    public async Task StopSharingAsync()
    {
        await gate.WaitAsync();
        try { await PoolControl.StopSharingAsync(); }
        finally { gate.Release(); }
        await RefreshAsync();
    }
    public void Dispose() { stop.Cancel(); signIn?.Cancel(); }
}
