using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class OnboardingTests
{
    [Fact(DisplayName = "with Claude Desktop missing nothing else is asked")]
    public void NotInstalled()
    {
        using var tmp = new TempDir();
        tmp.Write("Claude/config.json", """{"lastKnownAccountUuid":"a"}""");
        Assert.Equal(SetupState.NotInstalled, Onboarding.Check(false, new[] { tmp.Dir("Claude") }));
    }

    [Fact(DisplayName = "a profile folder that was never created is not signed in")]
    public void NoProfile()
    {
        using var tmp = new TempDir();
        var missing = System.IO.Path.Combine(tmp.Path, "Claude");
        Assert.Equal(SetupState.NotSignedIn, Onboarding.Check(true, new[] { missing }));
    }

    [Fact(DisplayName = "a config with no login in it is not signed in")]
    public void NoLogin()
    {
        using var tmp = new TempDir();
        tmp.Write("Claude/config.json", """{"locale":"en-US"}""");
        Assert.False(Onboarding.IsSignedIn(tmp.Dir("Claude")));
    }

    [Fact(DisplayName = "a cached token is a sign-in")]
    public void CachedToken()
    {
        using var tmp = new TempDir();
        tmp.Write("Claude/config.json", """{"oauth:tokenCacheV2":"djEw"}""");
        Assert.True(Onboarding.IsSignedIn(tmp.Dir("Claude")));
    }

    [Fact(DisplayName = "a config caught mid-rename is not taken for a signed-out one")]
    public void Unreadable()
    {
        using var tmp = new TempDir();
        tmp.Write("Claude/config.json", """{"lastKnownAcc""");
        Assert.True(Onboarding.IsSignedIn(tmp.Dir("Claude")));
    }

    [Fact(DisplayName = "one signed-in profile anywhere is enough to be ready")]
    public void AnyProfile()
    {
        using var tmp = new TempDir();
        var main = tmp.Dir("Claude");
        tmp.Write("Claude-Work/config.json", """{"lastKnownAccountUuid":"b"}""");
        Assert.Equal(SetupState.Ready, Onboarding.Check(true, new[] { main, tmp.Dir("Claude-Work") }));
    }
}
