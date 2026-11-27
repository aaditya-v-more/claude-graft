namespace ClaudeGraft.Core;

public enum UsageLevel { Normal, Warning, Critical }

public static class UsageLevels
{
    // Claude Desktop's own cut-offs, so a bar changes colour at the same figure in both.
    public static UsageLevel For(int percent) =>
        percent >= 90 ? UsageLevel.Critical : percent >= 75 ? UsageLevel.Warning : UsageLevel.Normal;
}
