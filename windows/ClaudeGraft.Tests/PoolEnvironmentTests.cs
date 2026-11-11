using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public class PoolEnvironmentTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly string? previous = GraftPaths.ProfilesRootOverride;
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly string profile;
    public PoolEnvironmentTests() { GraftPaths.ProfilesRootOverride = temp.Path; profile = temp.Dir("Claude"); }
    private string Seal(object value) => Convert.ToBase64String(ClaudeCredentials.Encrypt(JsonSerializer.SerializeToUtf8Bytes(value), key));
    private JsonObject Outer() => JsonNode.Parse(File.ReadAllText(Path.Combine(profile, PoolEnvironment.FileName)))!.AsObject();
    private JsonObject Plain(string value) => JsonNode.Parse(ClaudeCredentials.Decrypt(Convert.FromBase64String(value), key)!)!.AsObject();
    private void Write(JsonObject outer) => File.WriteAllText(Path.Combine(profile, PoolEnvironment.FileName), outer.ToJsonString());
    public void Dispose() { GraftPaths.ProfilesRootOverride = previous; temp.Dispose(); }

    [Fact] public void ClosedProfileRoundTripsAndPreservesOtherFields()
    {
        var original = Seal(new { CUSTOM = "keep" }); Write(new() { ["envVars"] = original, ["other"] = 42 });
        var journal = new PoolEnvironment.Journal();
        var result = PoolEnvironment.InspectAndApply(profile, true, journal, false, true, key);
        Assert.True(result.HoldsLines); Assert.Equal(original, result.Entry.Plain);
        var current = Outer()["envVars"]!.GetValue<string>(); Assert.Equal(result.Entry.Pooled, current);
        Assert.Equal(PoolEnvironment.ProxyValue, Plain(current)[PoolEnvironment.ProxyName]!.GetValue<string>());
        PoolEnvironment.Settle(profile, () => false);
        Assert.Equal(original, Outer()["envVars"]!.GetValue<string>()); Assert.Equal(42, Outer()["other"]!.GetValue<int>());
    }
    [Fact] public void RunningProfileIsJournaledBeforeAnyChangeAndLauncherOnlySwapsKnownValues()
    {
        var original = Seal(new { CUSTOM = "before" }); Write(new() { ["envVars"] = original });
        var result = PoolEnvironment.InspectAndApply(profile, true, new(), true, true, key);
        Assert.True(result.Pending); Assert.Equal(original, Outer()["envVars"]!.GetValue<string>());
        Assert.Equal(result.Entry, PoolEnvironment.ReadJournal()!.Profiles[profile]);
        var edited = Seal(new { CUSTOM = "edited" }); Write(new() { ["envVars"] = edited });
        var called = false; PoolEnvironment.Settle(profile, () => { called = true; return true; });
        Assert.False(called); Assert.Equal(edited, Outer()["envVars"]!.GetValue<string>());
    }
    [Fact] public void CustomProxyIsPreservedWhileAnOwnedCertificateIsRemoved()
    {
        Write(new() { ["envVars"] = Seal(new Dictionary<string, string> { ["https_proxy"] = "http://corporate.invalid:8080", [PoolEnvironment.CertificateName] = UsagePool.CertificateFile, ["CUSTOM"] = "keep" }) });
        var result = PoolEnvironment.InspectAndApply(profile, true, new(), false, true, key);
        Assert.True(result.Conflict); Assert.False(result.Entry.Wanted); Assert.False(result.HoldsLines);
        var variables = Plain(Outer()["envVars"]!.GetValue<string>());
        Assert.Equal("http://corporate.invalid:8080", variables["https_proxy"]!.GetValue<string>()); Assert.Null(variables[PoolEnvironment.CertificateName]); Assert.Equal("keep", variables["CUSTOM"]!.GetValue<string>());
    }
    [Fact] public void EncryptedScopedCopiesAreRemovedButUserScopesRemain()
    {
        Write(new() { ["scopedEnvVars"] = Seal(new Dictionary<string, object> { [":machine:test"] = new Dictionary<string, object> { ["C:\\work"] = new Dictionary<string, string> { ["https_proxy"] = PoolEnvironment.ProxyValue, ["CUSTOM"] = "keep" } } }) });
        var result = PoolEnvironment.InspectAndApply(profile, false, new(), false, false, key);
        Assert.False(result.HoldsLines);
        var scopes = Plain(Outer()["scopedEnvVars"]!.GetValue<string>());
        var values = scopes[":machine:test"]!["C:\\work"]!; Assert.Null(values["https_proxy"]); Assert.Equal("keep", values["CUSTOM"]!.GetValue<string>());
    }
    [Fact] public void TornClaudeWritesAreNeverReplaced()
    {
        var path = Path.Combine(profile, PoolEnvironment.FileName); File.WriteAllText(path, "{");
        Assert.Throws<IOException>(() => PoolEnvironment.InspectAndApply(profile, true, new(), false, true, key)); Assert.Equal("{", File.ReadAllText(path));
        Write(new()); File.WriteAllText(path + ".journal", "in progress");
        Assert.Throws<IOException>(() => PoolEnvironment.InspectAndApply(profile, true, new(), false, true, key)); Assert.Equal("{}", File.ReadAllText(path));
    }
    [Fact] public void DisableRestoresClosedAndKeepsRunningProfilesPending()
    {
        Write(new()); var journal = new PoolEnvironment.Journal(); PoolEnvironment.InspectAndApply(profile, true, journal, false, true, key);
        Assert.Equal(new[] { profile }, PoolEnvironment.TakeOutLines(running: _ => true));
        Assert.False(PoolEnvironment.ReadJournal()!.Profiles[profile].Wanted);
        Assert.Empty(PoolEnvironment.TakeOutLines(running: _ => false)); Assert.Null(Outer()["envVars"]);
    }
    [Fact] public void SettingsRetainAssignmentsUntilTheirDayExpires()
    {
        var settings = new PoolSettings { Borrowers = new() { ["main"] = ["account:a"] } };
        settings.Assign("session", "account:a"); Assert.True(PoolSettings.Load().Enabled);
        Assert.Equal("account:a", PoolSettings.Load().Assignments["session"]);
        settings.DisableAndSave(); Assert.False(PoolSettings.Load().Enabled); Assert.Empty(PoolSettings.Load().Assignments);
    }
    [Fact] public void NullSettingsCannotSilentlyEraseAnExistingSharingConfiguration()
    {
        Directory.CreateDirectory(UsagePool.Directory); File.WriteAllText(UsagePool.SettingsFile, "{\"borrowers\":null}");
        Assert.Throws<IOException>(() => PoolSettings.Load()); Assert.Equal("{\"borrowers\":null}", File.ReadAllText(UsagePool.SettingsFile));
    }
    [Fact] public void ConversationChoicesExpireOnlyWhenBothSelectionAndActivityAreOld()
    {
        var settings = new PoolSettings { Assignments = new() { ["active"] = "lent", ["quiet"] = "lent" }, AssignedAt = new() { ["active"] = DateTimeOffset.UtcNow.AddDays(-2), ["quiet"] = DateTimeOffset.UtcNow.AddDays(-2) } };
        settings.Save(); Assert.Equal(2, PoolSettings.Load().Assignments.Count);
        var now = DateTimeOffset.UtcNow;
        Assert.True(settings.PruneAssignments([new("active", "own", "lent", now.AddMinutes(-1), 1, null, null, [], null, null)], now));
        Assert.Equal("lent", Assert.Single(settings.Assignments).Value); Assert.True(settings.Assignments.ContainsKey("active"));
    }
    [Fact] public void CorruptRecoveryJournalPreservesBothItsBytesAndThePooledProfile()
    {
        Write(new()); PoolEnvironment.InspectAndApply(profile, true, new(), false, true, key);
        var pooled = File.ReadAllBytes(Path.Combine(profile, PoolEnvironment.FileName));
        File.WriteAllText(PoolEnvironment.JournalFile, "{torn");
        Assert.Throws<IOException>(() => PoolEnvironment.TakeOutLines(running: _ => false));
        Assert.Throws<IOException>(() => PoolEnvironment.LoadJournal());
        Assert.Throws<IOException>(() => PoolEnvironment.WriteJournal(new()));
        Assert.Equal("{torn", File.ReadAllText(PoolEnvironment.JournalFile)); Assert.Equal(pooled, File.ReadAllBytes(Path.Combine(profile, PoolEnvironment.FileName)));
    }
    [Fact] public void RemovingTheOnlyLenderDisablesBorrowingAndRestoresTheClosedProfile()
    {
        Write(new()); var journal = new PoolEnvironment.Journal(); PoolEnvironment.InspectAndApply(profile, true, journal, false, true, key);
        var settings = new PoolSettings { Borrowers = new() { ["main"] = ["removed"], ["removed"] = ["main"] }, Assignments = new() { ["session"] = "removed" } };
        Assert.True(settings.PruneMembers(["main"], [])); Assert.False(settings.Enabled); Assert.False(settings.Borrowers.ContainsKey("removed")); Assert.Empty(settings.Assignments);
        PoolEnvironment.InspectAndApply(profile, settings.Enabled, journal, false, true, key);
        Assert.Null(Outer()["envVars"]);
    }
    [Fact] public void AScopedCopyMadeAfterTheJournalWasWrittenIsStillRecovered()
    {
        if (!OperatingSystem.IsWindows()) return;
        var wrapped = "DPAPI"u8.ToArray().Concat(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)).ToArray();
        File.WriteAllText(Path.Combine(profile, "Local State"), JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String(wrapped) } }));
        Write(new()); var journal = new PoolEnvironment.Journal(); PoolEnvironment.InspectAndApply(profile, true, journal, false, true, key);
        Assert.False(journal.Profiles[profile].Scoped);
        var edited = Outer(); edited["scopedEnvVars"] = Seal(new Dictionary<string, object> { ["account"] = new Dictionary<string, object> { [":machine:scope"] = new Dictionary<string, string> { [PoolEnvironment.ProxyName] = PoolEnvironment.ProxyValue, ["CUSTOM"] = "keep" } } }); Write(edited);
        Assert.Empty(PoolEnvironment.TakeOutLines(running: _ => false));
        Assert.Null(Outer()["envVars"]);
        var scope = Plain(Outer()["scopedEnvVars"]!.GetValue<string>())["account"]![":machine:scope"]!;
        Assert.Null(scope[PoolEnvironment.ProxyName]); Assert.Equal("keep", scope["CUSTOM"]!.GetValue<string>());
    }
    [Fact] public async Task UnacknowledgedDisableDoesNotClaimTheRelayStoppedLending()
    {
        Directory.CreateDirectory(UsagePool.Directory); File.WriteAllText(PoolEnvironment.JournalFile, "{torn");
        var error = await Assert.ThrowsAsync<IOException>(() => PoolControl.StopSharingAsync());
        Assert.Contains("could not be confirmed stopped", error.Message); Assert.DoesNotContain("Lending has been stopped", error.Message);
        Assert.Equal("{torn", File.ReadAllText(PoolEnvironment.JournalFile)); Assert.False(PoolSettings.Load().Enabled);
    }
    [Fact] public async Task CleanupReadsTheLatestJournalAfterWaitingForTheLaunchLock()
    {
        Write(new()); PoolEnvironment.InspectAndApply(profile, true, new(), false, true, key);
        var nextPlain = Seal(new { CUSTOM = "newer" });
        var nextPooled = Seal(new Dictionary<string, string> { ["CUSTOM"] = "newer", [PoolEnvironment.ProxyName] = PoolEnvironment.ProxyValue, [PoolEnvironment.CertificateName] = UsagePool.CertificateFile });
        using var began = new ManualResetEventSlim(); Task<string[]> cleanup;
        using (StateLock.Acquire())
        {
            cleanup = Task.Run(() => { began.Set(); return PoolEnvironment.TakeOutLines(running: _ => false); });
            Assert.True(began.Wait(TimeSpan.FromSeconds(2))); Thread.Sleep(100);
            var newer = new PoolEnvironment.Journal(); newer.Profiles[profile] = new(false, nextPlain, nextPooled);
            Write(new() { ["envVars"] = nextPooled }); PoolEnvironment.WriteJournal(newer);
        }
        Assert.Empty(await cleanup); Assert.Equal(nextPlain, Outer()["envVars"]!.GetValue<string>());
        Assert.Equal(nextPlain, PoolEnvironment.LoadJournal().Profiles[profile].Plain);
    }
}
