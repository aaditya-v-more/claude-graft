using System.Runtime.CompilerServices;
using ClaudeGraft.Core;

namespace ClaudeGraft.Tests;

internal static class TestRun
{
    /// Most tests never move the profiles root, so without this every pass they
    /// drive notes itself into the real %APPDATA%\ClaudeGraft.
    [ModuleInitializer]
    internal static void KeepNotesOutOfAppData()
    {
        var folder = Path.Combine(Path.GetTempPath(), "claude-graft-tests-notes-" + Guid.NewGuid().ToString("N"));
        Diagnostics.FolderOverride = folder;
        // The suite reads English whatever language the machine running it is set to.
        L10n.Choose(L10n.English);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        };
    }
}
