using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// Plays what the page does: each Claude shown gets a new switch, whose binding
/// sets it to that Claude's saved value, and the first click on it arrives with
/// no focus. The handler's decision is KeepWarmSwitch.ToSave.
public class KeepWarmSwitchTests
{
    private sealed class Card
    {
        private readonly Dictionary<string, bool> _saved;
        private readonly string _claude;
        public bool IsOn { get; private set; }

        public Card(Dictionary<string, bool> saved, string claude)
        {
            _saved = saved;
            _claude = claude;
            Flip(saved[claude]);
        }

        public void Flip(bool on)
        {
            if (on == IsOn) return;
            IsOn = on;
            if (KeepWarmSwitch.ToSave(IsOn, _saved[_claude]) is bool save) _saved[_claude] = save;
        }
    }

    [Fact(DisplayName = "the first click on a new card's switch is saved, and is still on when the Claude is shown again")]
    public void FirstClickOnANewCardIsSaved()
    {
        var saved = new Dictionary<string, bool> { ["Claude 2"] = false, ["Claude 3"] = true };
        new Card(saved, "Claude 2").Flip(true);
        _ = new Card(saved, "Claude 3");
        Assert.True(new Card(saved, "Claude 2").IsOn);
    }

    [Fact(DisplayName = "showing Claudes with opposite settings, one after another, changes neither")]
    public void ShowingCardsSavesNothing()
    {
        var saved = new Dictionary<string, bool> { ["Claude 2"] = false, ["Claude 3"] = true };
        for (var i = 0; i < 3; i++)
        {
            _ = new Card(saved, "Claude 3");
            _ = new Card(saved, "Claude 2");
        }
        Assert.True(saved["Claude 3"]);
        Assert.False(saved["Claude 2"]);
    }

    [Fact(DisplayName = "a switch that only agrees with what is saved has nothing to save")]
    public void AgreeingFlipSavesNothing()
    {
        Assert.Null(KeepWarmSwitch.ToSave(true, true));
        Assert.Null(KeepWarmSwitch.ToSave(false, false));
        Assert.True(KeepWarmSwitch.ToSave(true, false));
        Assert.False(KeepWarmSwitch.ToSave(false, true));
    }
}
