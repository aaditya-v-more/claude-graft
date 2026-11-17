using ClaudeGraft.Core;
using Microsoft.Win32;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ClaudeLinkTests : IDisposable
{
    private readonly TempDir _temp = new();

    public ClaudeLinkTests() => GraftPaths.ProfilesRootOverride = _temp.Dir("profiles");

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _temp.Dispose();
    }

    [Theory(DisplayName = "the three sign-in routes are sign-ins")]
    [InlineData("claude://login/google-auth?code=fixture&hop_nonce=fixture")]
    [InlineData("claude://claude.ai/sso-callback?state=fixture")]
    [InlineData("claude://claude.com/magic-link#fixture:fixture")]
    public void SignInRoutes(string link) => Assert.Equal(ClaudeLinkKind.SignIn, ClaudeLinks.Classify(link));

    [Theory(DisplayName = "an ordinary claude link goes to the main profile")]
    [InlineData("claude://claude.ai/chat/fixture")]
    [InlineData("claude://claude.ai/mcp-auth-callback/sdk?state=fixture")]
    [InlineData("claude://code/new?q=hello%20world")]
    public void OrdinaryLinks(string link)
    {
        Assert.Equal(ClaudeLinkKind.OpenClaude, ClaudeLinks.Classify(link));
        Assert.Equal(GraftPaths.DefaultProfile, ClaudeLinks.Destination(link, null, []));
    }

    [Theory(DisplayName = "a variation on a sign-in link is refused rather than sent to the main profile")]
    [InlineData("claude://login/./google-auth")]
    [InlineData("claude://claude.ai/other/../sso-callback")]
    [InlineData("claude://claude.ai/sso-callback/extra")]
    [InlineData("claude://claude.ai/ss%6f-callback")]
    [InlineData("claude://login/other?code=fixture")]
    [InlineData("https://claude.ai/sso-callback")]
    [InlineData("claude://login:8123/google-auth")]
    [InlineData("claude://example.com@claude.ai/sso-callback")]
    [InlineData("claude://claude.ai/sso-callback\"--extra")]
    [InlineData("claude://claude.ai/sso-callback\\x")]
    [InlineData("claude://claude.ai/sso-callback --extra")]
    public void Variations(string value)
    {
        Assert.Equal(ClaudeLinkKind.Invalid, ClaudeLinks.Classify(value));
        Assert.Throws<IOException>(() => ClaudeLinks.Destination(value, null, []));
    }

    [Fact(DisplayName = "a link too long to be a real one is refused")]
    public void OverLong() =>
        Assert.Equal(ClaudeLinkKind.Invalid, ClaudeLinks.Classify("claude://claude.ai/chat/" + new string('a', ClaudeLinks.LongestLink)));

    [Fact(DisplayName = "a sign-in link goes only to the profile that was chosen and is still running")]
    public void OnlyTheChosenLiveProfile()
    {
        const string link = "claude://login/google-auth?code=fixture";
        var personal = new SignInTarget("Personal", GraftPaths.Profile("Personal"), 18, 91);
        var work = new SignInTarget("Work", GraftPaths.Profile("Work"), 26, 94);
        Assert.Equal(work.Profile, ClaudeLinks.Destination(link, work, [personal, work]));
        Assert.Throws<IOException>(() => ClaudeLinks.Destination(link, null, [personal, work]));
        Assert.Throws<IOException>(() => ClaudeLinks.Destination(link, work, [personal]));
        Assert.Throws<IOException>(() => ClaudeLinks.Destination(link, work, [work with { Started = 98 }]));
        Assert.Throws<IOException>(() => ClaudeLinks.Destination(link, work, [work with { Pid = 27 }]));
    }

    [Fact(DisplayName = "a sign-in link is passed to Claude as one argument")]
    public void OneArgument()
    {
        const string link = "claude://claude.ai/magic-link#a:b&c=d";
        var work = GraftPaths.Profile("Work");
        var start = Launcher.StartInfo(@"C:\Claude\claude.exe", work, link);
        Assert.False(start.UseShellExecute);
        Assert.Equal("", start.Arguments);
        Assert.Equal(new[] { "--user-data-dir=" + work, link }, start.ArgumentList);
        Assert.Equal(work, start.Environment["CLAUDE_USER_DATA_DIR"]);
    }

    [Fact(DisplayName = "the main profile is started with no data directory, so a link reaches the Claude already on it")]
    public void MainProfileHasNoDataDirectory()
    {
        const string link = "claude://login/google-auth?code=fixture";
        var start = Launcher.StartInfo(@"C:\Claude\claude.exe", GraftPaths.DefaultProfile, link);
        Assert.Equal(new[] { link }, start.ArgumentList);
        Assert.False(start.Environment.ContainsKey("CLAUDE_USER_DATA_DIR"));
    }

    [Fact(DisplayName = "registration names only Graft's own ProgID, and removing it leaves nothing behind")]
    public void RegistrationStaysInItsKeys()
    {
        var path = @"Software\ClaudeGraftTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            LinkRegistration.Write(root, @"C:\Graft\ClaudeGraft.exe");
            Assert.Equal(new[] { "Software" }, root.GetSubKeyNames());
            using (var software = root.OpenSubKey("Software")!)
                Assert.Equal(new[] { "Classes", "ClaudeGraft", "RegisteredApplications" }, software.GetSubKeyNames().Order());
            using (var classes = root.OpenSubKey(@"Software\Classes")!)
                Assert.Equal(new[] { LinkRegistration.ProgId }, classes.GetSubKeyNames());
            using (var registered = root.OpenSubKey(@"Software\RegisteredApplications")!)
                Assert.Equal(new[] { LinkRegistration.ApplicationName }, registered.GetValueNames());
            using (var command = root.OpenSubKey(@"Software\Classes\ClaudeGraft.Link\shell\open\command")!)
                Assert.Equal("\"C:\\Graft\\ClaudeGraft.exe\" --open-link \"%1\"", command.GetValue(""));
            using (var open = root.OpenSubKey(@"Software\Classes\ClaudeGraft.Link\shell\open")!)
                Assert.Equal("Claude Graft", open.GetValue("FriendlyAppName"));
            using (var application = root.OpenSubKey(@"Software\Classes\ClaudeGraft.Link\Application")!)
                Assert.Equal("Claude Graft", application.GetValue("ApplicationName"));

            LinkRegistration.Remove(root);
            using (var software = root.OpenSubKey("Software")!)
            {
                using var classes = software.OpenSubKey("Classes")!;
                using var registered = software.OpenSubKey("RegisteredApplications")!;
                Assert.Empty(classes.GetSubKeyNames());
                Assert.Empty(registered.GetValueNames());
                Assert.DoesNotContain("ClaudeGraft", software.GetSubKeyNames());
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\ClaudeGraftTests", throwOnMissingSubKey: false); }
    }

    [Theory(DisplayName = "the handler Windows reports is read as Graft only when it is Graft's ProgID")]
    [InlineData(0, "ClaudeGraft.Link", ClaudeLinkHandler.Graft)]
    [InlineData(0, "claudegraft.link", ClaudeLinkHandler.Graft)]
    [InlineData(0, "AppXClaudeDesktop", ClaudeLinkHandler.Other)]
    [InlineData(unchecked((int)0x80070483), "", ClaudeLinkHandler.Other)]
    [InlineData(0, "", ClaudeLinkHandler.Unknown)]
    [InlineData(unchecked((int)0x80070005), "", ClaudeLinkHandler.Unknown)]
    public void HandlerMapping(int status, string progId, ClaudeLinkHandler expected) =>
        Assert.Equal(expected, ClaudeLinkAssociation.FromResult(status, progId));

    [Theory(DisplayName = "the link Graft opens to bring up Windows' picker is known whichever way the shell hands it back")]
    [InlineData("claude://claude-graft/handler-chosen")]
    [InlineData("claude://claude-graft/handler-chosen/")]
    [InlineData("CLAUDE://Claude-Graft/Handler-Chosen")]
    public void ProbeIsKnown(string link) => Assert.True(LinkRegistration.IsProbe(link));

    [Fact(DisplayName = "a sign-in link is never taken for the picker's link")]
    public void SignInIsNotTheProbe() => Assert.False(LinkRegistration.IsProbe("claude://login/google-auth?code=fixture"));

    [Fact(DisplayName = "making Graft new to Windows again forgets only Graft's own entry")]
    public void MarkUnseenForgetsOnlyGraft()
    {
        var path = @"Software\ClaudeGraftTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            using (var seen = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts"))
            {
                seen.SetValue("ClaudeGraft.Link_claude", 0);
                seen.SetValue("AppXClaude_claude", 0);
                seen.SetValue("MSEdgeHTM_http", 0);
            }
            LinkRegistration.MarkUnseen(root);
            using var after = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts")!;
            Assert.Equal(new[] { "AppXClaude_claude", "MSEdgeHTM_http" }, after.GetValueNames().Order());
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\ClaudeGraftTests", throwOnMissingSubKey: false); }
    }
}
