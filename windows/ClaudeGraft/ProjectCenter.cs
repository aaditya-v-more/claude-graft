using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;

namespace ClaudeGraft;

public sealed class ProjectCenter : IDisposable
{
    private static readonly ConditionalWeakTable<AccountCenter, ProjectCenter> Centers = new();
    private readonly Adapter _adapter;
    public AccountCenter Accounts { get; }
    public ProjectCatalog Catalog { get; }
    public static ProjectCenter For(AccountCenter accounts) => Centers.GetValue(accounts, value => new(value));
    public ProjectCenter(AccountCenter accounts) { Accounts = accounts; _adapter = new(accounts); Catalog = new(_adapter); }
    public static void Release(AccountCenter accounts)
    {
        if (!Centers.TryGetValue(accounts, out var center)) return;
        Centers.Remove(accounts);
        center.Dispose();
    }
    public void Dispose() { Catalog.Dispose(); _adapter.Dispose(); }

    private sealed class Adapter : IProjectAccounts, IDisposable
    {
        private readonly AccountCenter _center;
        public Adapter(AccountCenter center) { _center = center; center.Changed += ForwardChanged; }
        private void ForwardChanged(object? sender, EventArgs e) => Changed?.Invoke(this, e);
        public void Dispose() => _center.Changed -= ForwardChanged;
        public IReadOnlyList<ProjectAccount> Accounts => _center.Accounts.Select(account => new ProjectAccount(account.Id,
            account.AccountUuid, account.OrganizationId, account.DisplayName, _center.Generation(account.Id))).ToArray();
        public event EventHandler? Changed;
        private WebAccount? Resolve(ProjectAccount account) => _center.Accounts.FirstOrDefault(value => value.Id == account.Id
            && value.AccountUuid == account.AccountUuid && value.OrganizationId == account.OrganizationId
            && _center.Generation(value.Id) == account.Generation && _center.IsCurrent(value));
        public bool IsCurrent(ProjectAccount account) => Resolve(account) is not null;
        public void NoteSignIn(ProjectAccount account, bool accepted) { if (Resolve(account) is { } current) _center.NoteSignIn(current, accepted); }
        public async Task<JsonNode?> RequestAsync(ProjectAccount account, string method, string path, JsonNode? body = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            var current = Resolve(account) ?? throw new ProjectRequestException(true);
            try { return await _center.RequestAsync(current, method, path, body, headers: headers); }
            catch (WebAccountException e) { throw new ProjectRequestException(e.IsSignedOut, e); }
        }
    }
}
