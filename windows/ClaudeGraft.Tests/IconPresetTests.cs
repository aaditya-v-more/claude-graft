using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class IconPresetTests
{
    [Fact(DisplayName = "a new shortcut takes the first colour nothing wears")]
    public void FirstFree() =>
        Assert.Equal("violet", IconPreset.Next(new[] { "original", "blue" }));

    [Fact(DisplayName = "Claude's own icon is never handed out")]
    public void NeverOriginal() =>
        Assert.Equal("blue", IconPreset.Next(Array.Empty<string>()));

    [Fact(DisplayName = "once every colour is worn, the least used comes round")]
    public void LeastUsed()
    {
        var taken = IconPreset.HandedOut.Concat(IconPreset.HandedOut).Where(p => p != "teal").ToList();
        Assert.Equal("teal", IconPreset.Next(taken));
    }

    [Fact(DisplayName = "a preset this build does not know wears Claude's own icon")]
    public void UnknownIsOriginal()
    {
        Assert.Equal(IconPreset.Original, IconPreset.Normalize("sunset"));
        Assert.Equal(IconPreset.Original, IconPreset.Normalize(null));
    }

    [Fact(DisplayName = "a shortcut saved before icons existed keeps Claude's own")]
    public void OlderListKeepsOriginal()
    {
        var s = JsonSerializer.Deserialize<Shortcut>("""{"id":"5b0b6f1e-0000-0000-0000-000000000001","name":"A","folder":"Claude-A"}""")!;
        Assert.Equal(IconPreset.Original, s.IconPreset);
    }

    [Fact(DisplayName = "a preset is spelled as the Mac build spells it")]
    public void MacSpelling()
    {
        var json = JsonSerializer.Serialize(new Shortcut { Name = "A", Folder = "Claude-A", IconPreset = "research" });
        Assert.Contains("\"iconPreset\":\"research\"", json);
    }

    [Fact(DisplayName = "a badged preset shares its colour with the plain one")]
    public void BadgeSharesColour()
    {
        Assert.Equal(IconPreset.Style("blue").HueAngle, IconPreset.Style("work").HueAngle);
        Assert.Null(IconPreset.Style("blue").Badge);
        Assert.Equal("work", IconPreset.Style("work").Badge);
    }

    [Fact(DisplayName = "editing a copy of a shortcut keeps what the editor does not show")]
    public void CopyKeepsEverything()
    {
        var s = new Shortcut { Name = "A", Folder = "Claude-A", KeepWarm = true, StopAskingChatsFor = "acct", IconPreset = "teal" };
        var copy = s.Copy();
        copy.Name = "B";
        Assert.Equal("A", s.Name);
        Assert.True(copy.KeepWarm);
        Assert.Equal("acct", copy.StopAskingChatsFor);
        Assert.Equal("teal", copy.IconPreset);
    }
}
