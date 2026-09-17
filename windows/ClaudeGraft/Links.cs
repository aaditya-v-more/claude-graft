using System.Diagnostics;

namespace ClaudeGraft;

public static class Links
{
    public const string Support = "https://ko-fi.com/aadityavmore";
    public const string Source = "https://github.com/aaditya-v-more/claude-graft";
    public const string Website = "https://aaditya-v-more.github.io/claude-graft/";
    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
