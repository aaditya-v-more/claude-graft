using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class ClaudeLinkTests
{
    [Theory]
    [InlineData(0, "ClaudeGraft.Link", ClaudeLinkHandler.Graft)]
    [InlineData(0, "claudegraft.link", ClaudeLinkHandler.Graft)]
    [InlineData(0, "AppXClaudeDesktop", ClaudeLinkHandler.Other)]
    [InlineData(0, @"Applications\ClaudeGraft.exe", ClaudeLinkHandler.Other)]
    [InlineData(unchecked((int)0x80070483), "", ClaudeLinkHandler.Other)]
    [InlineData(0, "", ClaudeLinkHandler.Unknown)]
    [InlineData(1, "ClaudeGraft.Link", ClaudeLinkHandler.Unknown)]
    [InlineData(unchecked((int)0x80070005), "", ClaudeLinkHandler.Unknown)]
    public void SignInRecognizesTheHandlerWindowsActuallySelected(int status, string progId, ClaudeLinkHandler expected)
    {
        Assert.Equal(expected, ClaudeLinkAssociation.FromResult(status, progId));
    }

    [Theory]
    [InlineData("claude://login/google-auth?code=fixture&hop_nonce=fixture")]
    [InlineData("claude://claude.ai/sso-callback?state=fixture&error=access_denied")]
    [InlineData("claude://claude.ai/magic-link#fixture:fixture")]
    public void SignInGoesOnlyToTheChosenLiveProcess(string link)
    {
        var personal = new SignInTarget("Personal", GraftPaths.Profile("Personal"), 18, 91);
        var work = new SignInTarget("Work", GraftPaths.Profile("Work"), 26, 94);
        var launch = ClaudeLinks.Dispatch(link, work, [personal, work], "Claude.exe");
        Assert.False(launch.UseShellExecute);
        Assert.Equal(new[] { "--user-data-dir=" + work.Profile, link }, launch.ArgumentList);
        Assert.Throws<IOException>(() => ClaudeLinks.Dispatch(link, null, [personal, work], "Claude.exe"));
        Assert.Throws<IOException>(() => ClaudeLinks.Dispatch(link, work, [personal], "Claude.exe"));
        Assert.Throws<IOException>(() => ClaudeLinks.Dispatch(link, work, [work with { Started = 98 }], "Claude.exe"));
    }

    [Theory]
    [InlineData("claude://claude.ai/chat/fixture")]
    [InlineData("claude://claude.ai/mcp-auth-callback/sdk?state=fixture")]
    [InlineData("claude://code/new?q=hello%20world")]
    public void NormalLinksGoDirectlyToClaudeWithoutInvokingTheHandlerAgain(string link)
    {
        var launch = ClaudeLinks.Dispatch(link, null, [], "Claude.exe");
        Assert.Equal("Claude.exe", launch.FileName);
        Assert.False(launch.UseShellExecute);
        Assert.Equal(new[] { link }, launch.ArgumentList);
    }

    [Theory]
    [InlineData("https://claude.ai/sso-callback")]
    [InlineData("claude://login/other?code=fixture")]
    [InlineData("claude://login:8123/google-auth")]
    [InlineData("claude://example.com@claude.ai/sso-callback")]
    [InlineData("claude://claude.ai/other/../sso-callback")]
    [InlineData("claude://claude.ai/sso-callback/extra")]
    [InlineData("claude://claude.ai/ss%6f-callback")]
    [InlineData("claude://claude.ai/sso-callback\" --extra")]
    public void MalformedLoginLinksDoNotFallBackToTheMainProfile(string value)
    {
        Assert.Equal(ClaudeLinkKind.Invalid, ClaudeLinks.Classify(value));
        Assert.Throws<IOException>(() => ClaudeLinks.Dispatch(value, null, [], "Claude.exe"));
    }
}
