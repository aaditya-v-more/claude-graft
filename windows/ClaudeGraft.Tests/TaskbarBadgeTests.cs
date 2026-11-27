using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class TaskbarBadgeTests
{
    [Theory(DisplayName = "a Claude waiting on its person is flagged whether or not it was in front")]
    [InlineData(true)]
    [InlineData(false)]
    public void WaitingIsAlwaysFlagged(bool watched) =>
        Assert.Equal(TaskbarBadge.Kind.NeedsPerson, TaskbarBadge.For(ChatFocus.Turn.NeedsPerson, watched));

    [Fact(DisplayName = "a turn that finished out of sight is flagged done")]
    public void FinishedOutOfSight() =>
        Assert.Equal(TaskbarBadge.Kind.Done, TaskbarBadge.For(ChatFocus.Turn.Done, watched: false));

    [Fact(DisplayName = "a turn the person watched finish is not flagged")]
    public void FinishedInSight() =>
        Assert.Equal(TaskbarBadge.Kind.None, TaskbarBadge.For(ChatFocus.Turn.Done, watched: true));

    [Theory(DisplayName = "a Claude still working or with nothing to say has no badge")]
    [InlineData(ChatFocus.Turn.Working)]
    [InlineData(ChatFocus.Turn.None)]
    public void QuietStates(ChatFocus.Turn turn) =>
        Assert.Equal(TaskbarBadge.Kind.None, TaskbarBadge.For(turn, watched: false));
}
