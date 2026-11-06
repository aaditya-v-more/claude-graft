using ClaudeGraft.Core;
using ClaudeGraft.Platform;

try
{
    if (args.Length == 2 && args[0] == "--config")
        Launcher.Open(ShortcutManifest.Read(args[1]), DesktopInteraction.ConfirmSharing);
    else if (args.Length == 1)
        Launcher.OpenByFolder(args[0], DesktopInteraction.ConfirmSharing);
    else throw new IOException("This shortcut needs a Graft profile configuration.");
    return 0;
}
catch (Exception e)
{
    Diagnostics.Note("launcher.failed", new Dictionary<string, object?> { ["error"] = e.GetType().Name });
    DesktopInteraction.Error(e.Message);
    return 1;
}
