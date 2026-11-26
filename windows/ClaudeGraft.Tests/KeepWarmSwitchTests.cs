using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

/// Replays what the card's one reused switch goes through, with the same
/// decision the page's Toggled handler makes. It cannot build the page, so the
/// binding's order is played as the worst case: the switch flips to the next
/// Claude's value before anything else about the card has caught up.
public class KeepWarmSwitchTests
{
    private sealed class Card(Dictionary<string, bool> saved)
    {
        public string? Bound { get; private set; }
        public bool IsOn { get; private set; }

        public void Show(string claude)
        {
            Bound = claude;
            Flip(saved[claude]);
        }

        public void Flip(bool on)
        {
            if (on == IsOn) return;
            IsOn = on;
            if (Bound is string claude && KeepWarmSwitch.ToSave(IsOn, saved[claude]) is bool save)
                saved[claude] = save;
        }
    }

    [Fact(DisplayName = "moving the card between Claudes with opposite settings changes neither")]
    public void RebindingSavesNothing()
    {
        var saved = new Dictionary<string, bool> { ["Claude 3"] = true, ["Claude 2"] = false };
        var card = new Card(saved);
        card.Show("Claude 3");
        card.Show("Claude 2");
        card.Show("Claude 3");
        card.Show("Claude 2");
        Assert.True(saved["Claude 3"]);
        Assert.False(saved["Claude 2"]);
    }

    [Fact(DisplayName = "a flip after the card has moved saves to the Claude now showing")]
    public void DeliberateFlipSavesToTheShownClaude()
    {
        var saved = new Dictionary<string, bool> { ["Claude 3"] = true, ["Claude 2"] = false };
        var card = new Card(saved);
        card.Show("Claude 3");
        card.Show("Claude 2");
        card.Flip(true);
        Assert.True(saved["Claude 2"]);
        Assert.True(saved["Claude 3"]);
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
