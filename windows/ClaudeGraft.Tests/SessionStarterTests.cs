using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class SessionStarterTests
{
    [Fact(DisplayName = "a refused login is described as something the person can act on")]
    public void RefusedLogin()
    {
        Assert.Contains("Open it once", SessionStarter.Describe(401, null));
        Assert.Contains("Open it once", SessionStarter.Describe(403, "{}"));
    }

    [Fact(DisplayName = "a rate limit says so plainly")]
    public void RateLimited()
    {
        Assert.Contains("rate-limited", SessionStarter.Describe(429, null));
    }

    [Fact(DisplayName = "any other answer carries Anthropic's own words when it gave them")]
    public void CarriesTheServiceMessage()
    {
        var payload = "{\"error\":{\"message\":\"model not found\"}}";
        Assert.Equal("Anthropic answered 400: model not found", SessionStarter.Describe(400, payload));
    }

    [Fact(DisplayName = "a body with nothing to quote gives just the status")]
    public void NoMessageToQuote()
    {
        Assert.Equal("Anthropic answered 500.", SessionStarter.Describe(500, "not json at all"));
        Assert.Equal("Anthropic answered 500.", SessionStarter.Describe(500, null));
    }
}
