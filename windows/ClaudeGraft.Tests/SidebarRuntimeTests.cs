using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public sealed class SidebarRuntimeTests
{
    [Fact(DisplayName = "a bundled Electron on the same major as Claude's is allowed to run")]
    public void SameMajor() => Assert.Null(SidebarStorage.RuntimeMismatch(44, 44));

    [Theory(DisplayName = "a bundled Electron on a different major than Claude's is refused")]
    [InlineData(45, 44)]
    [InlineData(43, 44)]
    public void DifferentMajor(int bundled, int claude) =>
        Assert.StartsWith("runtime-mismatch", SidebarStorage.RuntimeMismatch(bundled, claude));

    [Fact(DisplayName = "a Claude whose Electron version cannot be read is refused")]
    public void UnknownClaude() => Assert.Contains("unknown", SidebarStorage.RuntimeMismatch(44, null));

    [Theory(DisplayName = "a version string is read down to its major")]
    [InlineData("44.9.3", 44)]
    [InlineData("v44.9.3\n", 44)]
    [InlineData("", null)]
    [InlineData("beta", null)]
    public void MajorOf(string text, int? expected) => Assert.Equal(expected, SidebarStorage.MajorOf(text));
}
