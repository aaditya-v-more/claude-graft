using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ClaudeGraft.Tests;

/// <summary>
/// A pass that cannot get the state lock throws instead of running, so every
/// way into one from the app or a launcher has to expect that. A caller that
/// forgets crashes a button or a launcher on nothing worse than a slow sweep.
/// </summary>
public class StateBusyAuditTests
{
    /// Each method that takes the lock, and how a caller outside the core names it.
    /// `Launcher.Open` and `Sweep` handle busy themselves, so they have no callers to check.
    private static readonly Dictionary<string, string?> Entries = new()
    {
        ["Apply"] = "Graft.Apply(",
        ["AdoptChats"] = "Graft.AdoptChats(",
        ["MoveProfileFolder"] = "Graft.MoveProfileFolder(",
        ["MirrorKnownPairs"] = "Graft.MirrorKnownPairs(",
        ["MirrorChatFolders"] = "Graft.MirrorChatFolders(",
        ["GraftInto"] = "Graft.GraftInto(",
        ["FileMissingSessionRecords"] = "Graft.FileMissingSessionRecords(",
        ["ForgetMirrors"] = "Graft.ForgetMirrors(",
        ["UnmirrorChatStores"] = "Graft.UnmirrorChatStores(",
        ["Ungraft"] = "Graft.Ungraft(",
        ["SetEnabled"] = "ManualUpdates.SetEnabled(",
        ["Synchronize"] = "ManualUpdates.Synchronize(",
        ["Delete"] = "Store.Delete(",
        ["Open"] = null,
        ["Sweep"] = null,
    };

    private static readonly Regex Declaration = new(@"\b(?:public|private|internal)\b[^=;(]*?\b(\w+)\s*(?:<[^>]*>)?\(");
    private static readonly Regex Guard = new(@"catch \((?:StateBusyException|IOException|Exception)\b");

    [Fact(DisplayName = "every method that takes the state lock is one this audit knows about")]
    public void EveryLockTakerIsListed()
    {
        var takers = new SortedSet<string>();
        foreach (var file in Files("ClaudeGraft.Core").Where(f => Path.GetFileName(f) != "StateLock.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("StateLock.Run(")) continue;
                var name = Enumerable.Range(0, i + 1).Reverse()
                    .Select(j => Declaration.Match(lines[j])).First(m => m.Success).Groups[1].Value;
                takers.Add(name);
            }
        }
        Assert.Equal(new SortedSet<string>(Entries.Keys), takers);
    }

    [Fact(DisplayName = "every call into a locked pass from the app or a launcher sits under a catch of a busy lock")]
    public void EveryCallerCatchesBusy()
    {
        var unguarded = new List<string>();
        foreach (var file in Files("ClaudeGraft", "GraftLaunch"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Entries.Values.Any(call => call is not null && lines[i].Contains(call))) continue;
                var tried = Enumerable.Range(Math.Max(0, i - 2), i - Math.Max(0, i - 2) + 1)
                    .Any(j => Regex.IsMatch(lines[j], @"\btry\b"));
                var caught = lines.Skip(i).Take(15).Any(Guard.IsMatch);
                if (!tried || !caught) unguarded.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }
        Assert.Empty(unguarded);
    }

    private static IEnumerable<string> Files(params string[] projects) => projects
        .Select(p => Path.Combine(WindowsRoot(), p))
        .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Directory.GetParent(here)!.Parent!.FullName;
}
