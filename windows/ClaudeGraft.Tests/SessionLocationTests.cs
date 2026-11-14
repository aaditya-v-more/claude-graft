using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public sealed class SessionLocationTests
{
    [Theory(DisplayName = "session filing checks the home or drive rather than a cleaned-up working directory")]
    [InlineData(@"C:\Users\Someone\project\gone-worktree", @"C:\Users\Someone")]
    [InlineData(@"D:\Work\project\gone-worktree", @"D:\")]
    [InlineData(@"\\server\share\project\gone-worktree", @"\\server\share")]
    public void ChecksAnchor(string cwd, string expected)
    {
        var read = "";
        Assert.True(Graft.SessionFolderIsHere(cwd, path => { read = path; return true; }));
        Assert.Equal(expected, read, ignoreCase: true);
        Assert.False(Graft.SessionFolderIsHere(cwd, _ => false));
    }

    [Theory(DisplayName = "foreign Mac and Linux home paths do not become native Windows sidebar records")]
    [InlineData("/Users/someone/project")]
    [InlineData("/Volumes/absent/project")]
    [InlineData("/home/someone/project")]
    public void ForeignHomeDoesNotFile(string cwd) => Assert.False(Graft.SessionFolderIsHere(cwd, _ => true));

    [Theory(DisplayName = "an unspecified or relative legacy working directory remains inconclusive")]
    [InlineData("")]
    [InlineData("relative-project")]
    public void UnknownCwdDoesNotExclude(string cwd) => Assert.True(Graft.SessionFolderIsHere(cwd, _ => false));
}
