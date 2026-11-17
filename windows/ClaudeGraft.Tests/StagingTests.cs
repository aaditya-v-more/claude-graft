using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class StagingTests : IDisposable
{
    private readonly TempDir _temp = new();

    public StagingTests() => GraftPaths.ProfilesRootOverride = _temp.Dir("profiles");

    public void Dispose()
    {
        Staging.Clear();
        GraftPaths.ProfilesRootOverride = null;
        _temp.Dispose();
    }

    [Fact(DisplayName = "a staged figure is what a profile reports, without asking the service")]
    public async Task StagedUsage()
    {
        var profile = _temp.Dir("profiles", "Claude-2");
        var staged = new UsageEntry { Usage = new Usage { FiveHour = 86, Week = 91, Sampled = DateTimeOffset.UtcNow }, IsLive = true };
        Staging.Stage(profile, new AccountStage { SignedIn = true, Usage = staged });
        Assert.Same(staged, await UsageMonitor.ReadAsync(profile));
        Assert.Same(staged, UsageMonitor.Peek(profile));
        Assert.True(Onboarding.IsSignedIn(profile));
    }

    [Fact(DisplayName = "a staged busy lock refuses every pass, and clearing it lets them run")]
    public void StagedBusy()
    {
        Staging.StateFailure = () => new StateBusyException();
        var ran = false;
        Assert.Throws<StateBusyException>(() => StateLock.Run(() => ran = true));
        Assert.False(ran);
        Staging.Clear();
        StateLock.Run(() => ran = true);
        Assert.True(ran);
    }

    [Fact(DisplayName = "a staged link handler is the one Graft reports")]
    public void StagedLinkHandler()
    {
        Staging.LinkHandler = ClaudeLinkHandler.Other;
        Assert.Equal(ClaudeLinkHandler.Other, ClaudeLinkAssociation.Current());
    }

    [Fact(DisplayName = "clearing the stage puts every profile back as it really is")]
    public void ClearPutsItBack()
    {
        var profile = _temp.Dir("profiles", "Claude-2");
        Staging.Stage(profile, new AccountStage { SignedIn = true });
        Staging.Setup = SetupState.NotInstalled;
        Staging.Clear();
        Assert.False(Staging.Any);
        Assert.False(Onboarding.IsSignedIn(profile));
    }

    [Fact(DisplayName = "a shortcut list staged as unreadable refuses every save, and reads again once cleared")]
    public void StagedUnreadableList()
    {
        var store = new ShortcutStore();
        Assert.Null(store.LoadError);
        Staging.ListUnreadable = true;
        Assert.NotNull(store.LoadError);
        Assert.Throws<UnreadableStateException>(() => store.Add(new Shortcut { Name = "Staged", Folder = "Claude-Staged" }));
        Staging.Clear();
        Assert.Null(store.LoadError);
    }
}
