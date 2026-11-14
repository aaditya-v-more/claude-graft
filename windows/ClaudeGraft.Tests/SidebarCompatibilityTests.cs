using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class SidebarCompatibilityTests
{
    [Theory]
    [InlineData("4.16336.0", "44.9.3", true)]
    [InlineData("4.23118.1", "44.9.3", true)]
    [InlineData("4.23118.1", "45.0.0", false)]
    [InlineData("4.23118.2", "44.9.3", false)]
    [InlineData(null, "44.9.3", false)]
    [InlineData("4.23118.1", null, false)]
    public void OnlyInspectedBuildsUseTheExactlyMatchedEngine(string? claude, string? engine, bool expected) =>
        Assert.Equal(expected, SidebarBrowser.CompatibleVersions(claude, engine));
}
