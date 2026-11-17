using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// An account signed in through Graft rather than through a Claude, kept only
/// so it can lend. Its token is sealed with DPAPI for this Windows user, which
/// unwraps silently for them and for nobody else.
/// </summary>
public sealed record PoolAccount
{
    public const string Prefix = "account:";

    public required string Id { get; init; }
    public string? AccountUuid { get; init; }
    public string? Email { get; init; }
    public string? OrganizationName { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public DateTimeOffset Expires { get; init; }
    public DateTimeOffset Added { get; init; }
    public required string Sealed { get; init; }
    /// The colour it wears beside its name, handed out once; null until given one.
    public string? IconPreset { get; init; }

    public string Name => Email ?? OrganizationName ?? "Signed-in account";
    public static bool IsAccount(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);
}

public static class PoolAccounts
{
    public static string File => Path.Combine(UsagePool.Directory, "accounts.json");

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static List<PoolAccount> Load()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<List<PoolAccount>>(System.IO.File.ReadAllText(File), Json) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public static void Save(IReadOnlyList<PoolAccount> accounts)
    {
        Directory.CreateDirectory(UsagePool.Directory);
        var temp = File + ".new";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(accounts, Json));
        System.IO.File.Move(temp, File, overwrite: true);
    }

    /// Gives each account without a colour one, once and in the order they were added:
    /// the colour of the Claude signed in to the same account, or the first nobody wears.
    /// Answers whether anything changed, so the caller saves only then.
    public static bool Colour(List<PoolAccount> accounts, IReadOnlyCollection<string> claudeColours, Func<PoolAccount, string?> sameAccountClaude)
    {
        if (accounts.All(a => a.IconPreset is not null)) return false;
        foreach (var index in Enumerable.Range(0, accounts.Count).OrderBy(i => accounts[i].Added))
        {
            if (accounts[index].IconPreset is not null) continue;
            var inUse = claudeColours.Prepend(Core.IconPreset.Original)
                .Concat(accounts.Where(a => sameAccountClaude(a) is null && a.IconPreset is not null).Select(a => a.IconPreset!));
            accounts[index] = accounts[index] with { IconPreset = sameAccountClaude(accounts[index]) ?? Core.IconPreset.Next(inUse) };
        }
        return true;
    }

    /// A second sign-in to an account already here replaces its login and keeps its id.
    public static PoolAccount Add(PoolAccount account)
    {
        var accounts = Load();
        var index = account.AccountUuid is null ? -1 : accounts.FindIndex(a => a.AccountUuid == account.AccountUuid);
        var stored = index >= 0 ? account with { Id = accounts[index].Id, IconPreset = accounts[index].IconPreset ?? account.IconPreset } : account;
        if (index >= 0) accounts[index] = stored;
        else accounts.Add(stored);
        Save(accounts);
        return stored;
    }

    /// An account nobody draws on any more is forgotten with its login.
    public static void ForgetUnused(UsageSharing sharing)
    {
        var used = sharing.Lenders.Values.SelectMany(l => l).ToHashSet();
        var accounts = Load();
        var kept = accounts.Where(a => used.Contains(a.Id)).ToList();
        if (kept.Count != accounts.Count) Save(kept);
    }

    [SupportedOSPlatform("windows")]
    public static string Seal(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    public static string? Unseal(string sealedToken)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedToken), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
}
