using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class OpenInFlightTests
{
    [Fact(DisplayName = "an open that throws leaves the row no longer opening")]
    public async Task AFailedOpenClearsTheState()
    {
        var flight = new OpenInFlight();
        var seen = new List<bool>();
        flight.Changed += () => seen.Add(flight.Active);

        await Assert.ThrowsAsync<IOException>(() => flight.RunAsync(() => throw new IOException("no lock")));

        Assert.False(flight.Active);
        Assert.Equal(new[] { true, false }, seen);
    }

    [Fact(DisplayName = "a second open of the same row while one is running does nothing")]
    public async Task ASecondPressIsIgnored()
    {
        var flight = new OpenInFlight();
        var release = new TaskCompletionSource();
        var starts = 0;

        var first = flight.RunAsync(async () => { starts++; await release.Task; });
        var second = await flight.RunAsync(() => { starts++; return Task.CompletedTask; });

        Assert.False(second);
        Assert.True(flight.Active);
        release.SetResult();
        Assert.True(await first);
        Assert.Equal(1, starts);
        Assert.False(flight.Active);
    }

    [Fact(DisplayName = "a row can be opened again once the last open is over")]
    public async Task OpeningAgainAfterwardsWorks()
    {
        var flight = new OpenInFlight();
        Assert.True(await flight.RunAsync(() => Task.CompletedTask));
        Assert.True(await flight.RunAsync(() => Task.CompletedTask));
    }
}
