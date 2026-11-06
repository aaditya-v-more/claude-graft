using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class SessionFilingTests
{
    private static readonly HashSet<string> None = new();

    private static SessionFacts Facts(string id = "sid", double lastActivity = 1000) => new()
    {
        CliSessionId = id,
        BridgeIds = new[] { "cse_x" },
        OwnerAccount = "ACCT",
        OwnerOrganization = "ORG",
        Title = "T",
        Cwd = "/w",
        CreatedAt = 0,
        LastActivityAt = lastActivity,
        Model = "m",
        Effort = "medium",
        PermissionMode = "auto",
        Prompts = 1,
        Branches = Array.Empty<string>(),
    };

    private static SessionFiling Decide(
        SessionFacts? facts,
        IReadOnlySet<string>? recorded = null,
        IReadOnlySet<string>? withdrawn = null,
        IReadOnlyCollection<double>? deletions = null,
        DateTime? lastWrite = null,
        string? ownerProfile = "C:\\profile",
        bool ownerIsRunning = true,
        DateTime? now = null,
        TimeSpan? quietWindow = null) =>
        Graft.DecideFiling(
            facts,
            recorded ?? None,
            withdrawn ?? None,
            deletions ?? Array.Empty<double>(),
            lastWrite ?? DateTime.UtcNow.AddMinutes(-5),
            ownerProfile,
            ownerIsRunning,
            now ?? DateTime.UtcNow,
            quietWindow ?? TimeSpan.FromSeconds(60));

    [Fact(DisplayName = "a transcript with no facts is not a desktop session")]
    public void NoFacts() => Assert.Equal(SessionFiling.NotADesktopSession, Decide(null));

    [Fact(DisplayName = "a session already recorded is left alone")]
    public void Recorded() =>
        Assert.Equal(SessionFiling.AlreadyRecorded,
            Decide(Facts(), recorded: new HashSet<string> { "sid" }));

    [Fact(DisplayName = "a session withdrawn once is never brought back")]
    public void Withdrawn() =>
        Assert.Equal(SessionFiling.Withdrawn,
            Decide(Facts(), withdrawn: new HashSet<string> { "sid" }));

    [Fact(DisplayName = "a deletion marker just after the last line withdraws the session it names by timing")]
    public void DeletionByTiming()
    {
        // last activity at 1000ms; a marker written at 1000..61000 covers it.
        Assert.Equal(SessionFiling.Withdrawn,
            Decide(Facts(lastActivity: 1000), deletions: new[] { 30_000d }));
    }

    [Fact(DisplayName = "a deletion marker before the last line is a different session and does not withdraw it")]
    public void DeletionTooEarly()
    {
        // marker at 500ms, session last active at 1000ms — the delete was for
        // something that had already gone quiet, not this.
        Assert.NotEqual(SessionFiling.Withdrawn,
            Decide(Facts(lastActivity: 1000), deletions: new[] { 500d },
                   lastWrite: DateTime.UtcNow.AddMinutes(-5)));
    }

    [Fact(DisplayName = "a transcript still warm is held back while the owner is running")]
    public void TooRecent() =>
        Assert.Equal(SessionFiling.TooRecent,
            Decide(Facts(), lastWrite: DateTime.UtcNow, ownerIsRunning: true));

    [Fact(DisplayName = "with no Claude signed into the owner's account running, the wait is skipped")]
    public void NoWaitWhenOwnerAbsent() =>
        // warm transcript, but owner not running, owner profile present -> filed
        Assert.Equal(SessionFiling.File,
            Decide(Facts(), lastWrite: DateTime.UtcNow, ownerIsRunning: false));

    [Fact(DisplayName = "a session whose owner lives on no profile here waits for one")]
    public void NoOwnerProfile() =>
        Assert.Equal(SessionFiling.NoOwnerProfile,
            Decide(Facts(), ownerProfile: null));

    [Fact(DisplayName = "a settled transcript whose owner has a profile is filed")]
    public void Filed() => Assert.Equal(SessionFiling.File, Decide(Facts()));
}

public class SessionUpdateTests
{
    private static SessionFacts Facts(string title) => new()
    {
        CliSessionId = "sid",
        BridgeIds = new[] { "cse_x" },
        OwnerAccount = "ACCT",
        OwnerOrganization = "ORG",
        Title = title,
        Cwd = "/w",
        CreatedAt = 0,
        LastActivityAt = 1,
        Model = "m",
        Effort = "medium",
        PermissionMode = "auto",
        Prompts = 1,
        Branches = Array.Empty<string>(),
    };

    [Fact(DisplayName = "a record this app never authored is left alone")]
    public void NotAuthored() =>
        Assert.Equal(SessionUpdate.Leave, Graft.DecideUpdate(null, Facts("T"), diskMatchesAuthored: false));

    [Fact(DisplayName = "a record already saying what the transcript says needs no write")]
    public void Unchanged() =>
        Assert.Equal(SessionUpdate.Leave, Graft.DecideUpdate(Facts("T"), Facts("T"), diskMatchesAuthored: true));

    [Fact(DisplayName = "a moved-on transcript refreshes a record that is still ours byte for byte")]
    public void Refresh() =>
        Assert.Equal(SessionUpdate.Refresh, Graft.DecideUpdate(Facts("old"), Facts("new"), diskMatchesAuthored: true));

    [Fact(DisplayName = "a record Claude has rewritten is never touched again")]
    public void TakenOver() =>
        Assert.Equal(SessionUpdate.TakenOver, Graft.DecideUpdate(Facts("old"), Facts("new"), diskMatchesAuthored: false));
}

public class MayFileRecordsTests
{
    [Fact(DisplayName = "a present folder this pass actually read may be filed into")]
    public void PresentAndRead()
    {
        using var t = new TempDir();
        var org = t.Dir("store", "ACCT", "ORG");
        var read = new HashSet<string> { Fs.Resolve(org) };
        Assert.True(Graft.MayFileRecords(org, read));
    }

    [Fact(DisplayName = "a present folder this pass could not read is left alone")]
    public void PresentButUnread()
    {
        using var t = new TempDir();
        var org = t.Dir("store", "ACCT", "ORG");
        Assert.False(Graft.MayFileRecords(org, new HashSet<string>()));
    }

    [Fact(DisplayName = "a folder simply not there yet gets a profile's first record")]
    public void AbsentAndFresh()
    {
        using var t = new TempDir();
        var org = Path.Combine(t.Path, "store", "ACCT", "ORG");   // never created
        Assert.True(Graft.MayFileRecords(org, new HashSet<string>()));
    }

    [Fact(DisplayName = "a folder this app stashed away is not filed into, however absent it looks")]
    public void AbsentBecauseStashed()
    {
        using var t = new TempDir();
        var account = t.Dir("store", "ACCT");
        var org = Path.Combine(account, "ORG");                   // absent
        Directory.CreateDirectory(Path.Combine(account, ".ORG.graft-own"));  // the stash beside it
        Assert.False(Graft.MayFileRecords(org, new HashSet<string>()));
    }
}
