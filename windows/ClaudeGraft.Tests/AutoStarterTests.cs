using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class AutoStarterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static UsageEntry Live(int fiveHour, DateTimeOffset? reset) => new()
    {
        IsLive = true,
        Usage = new Usage { FiveHour = fiveHour, Week = 0, Sampled = Now, FiveHourReset = reset },
    };

    [Fact(DisplayName = "a closed window on an opted-in profile past its cooldown is started")]
    public void StartsAClosedWindow()
    {
        Assert.True(AutoStarter.ShouldStart(optedIn: true, Live(0, reset: null), Now, cooldownUntil: default));
    }

    [Fact(DisplayName = "a profile whose owner did not opt in is left alone")]
    public void LeavesTheUnoptedAlone()
    {
        Assert.False(AutoStarter.ShouldStart(optedIn: false, Live(0, reset: null), Now, cooldownUntil: default));
    }

    [Fact(DisplayName = "a window already open, by its reset time or its usage, is not started again")]
    public void DoesNotReopenAnOpenWindow()
    {
        Assert.False(AutoStarter.ShouldStart(true, Live(0, reset: Now.AddHours(3)), Now, default));
        Assert.False(AutoStarter.ShouldStart(true, Live(12, reset: null), Now, default));
    }

    [Fact(DisplayName = "a reset time already past reads as a closed window, not an open one")]
    public void APastResetIsClosed()
    {
        Assert.True(AutoStarter.ShouldStart(true, Live(0, reset: Now.AddHours(-1)), Now, default));
    }

    [Fact(DisplayName = "nothing is started while the cooldown from a recent attempt still holds")]
    public void HoldsOffDuringCooldown()
    {
        Assert.False(AutoStarter.ShouldStart(true, Live(0, reset: null), Now, cooldownUntil: Now.AddMinutes(10)));
    }

    [Fact(DisplayName = "an uncertain reading — none at all, or off disk — starts nothing")]
    public void UncertaintyStartsNothing()
    {
        Assert.False(AutoStarter.ShouldStart(true, null, Now, default));
        var disk = new UsageEntry { IsLive = false, Usage = new Usage { FiveHour = 0, Week = 0, Sampled = Now } };
        Assert.False(AutoStarter.ShouldStart(true, disk, Now, default));
    }
}

/// The cooldown has to outlive the process — an in-memory one would be empty on
/// every relaunch, and a startup crash-loop firing the sweep seconds after each
/// launch is the very runaway the cooldown exists to stop. So it is on disk, and
/// a fresh read is a stand-in for a restart.
///
/// In the shared collection like every other class that redirects the profiles
/// root: it sets <c>GraftPaths.ProfilesRootOverride</c>, a process-wide static,
/// so running in parallel with the serialized collection raced that override out
/// from under whichever graft was mid-pass.
[Collection("GlobalState")]
public sealed class AutoStartCooldownTests : IDisposable
{
    private readonly TempDir _t = new();

    public AutoStartCooldownTests() => GraftPaths.ProfilesRootOverride = _t.Dir("root");

    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        _t.Dispose();
    }

    [Fact(DisplayName = "a profile never tried has no cooldown standing against it")]
    public void UnknownProfileIsFree()
    {
        Assert.Equal(default, AutoStarter.CooldownUntil(@"C:\never\tried"));
    }

    [Fact(DisplayName = "an armed cooldown is read back from disk, so a restart still honours it")]
    public void ArmedCooldownSurvivesAReread()
    {
        var profile = _t.Dir("root", "Claude-Work");
        AutoStarter.ArmCooldown(profile);

        // Nothing is cached in the process — CooldownUntil reads the file every
        // time — so this read is exactly what the next launch would see.
        var until = AutoStarter.CooldownUntil(profile);
        Assert.True(until > DateTimeOffset.UtcNow);
        Assert.True(until <= DateTimeOffset.UtcNow + AutoStarter.Cooldown);
        Assert.True(File.Exists(AutoStarter.StatePath));
    }

    [Fact(DisplayName = "the same window closed on disk still refuses a start while the cooldown holds")]
    public void CooldownGatesTheDecision()
    {
        var profile = _t.Dir("root", "Claude-Work");
        AutoStarter.ArmCooldown(profile);

        var closed = new UsageEntry
        {
            IsLive = true,
            Usage = new Usage { FiveHour = 0, Week = 0, Sampled = DateTimeOffset.UtcNow, FiveHourReset = null },
        };
        Assert.False(AutoStarter.ShouldStart(
            true, closed, DateTimeOffset.UtcNow, AutoStarter.CooldownUntil(profile)));
    }
}
