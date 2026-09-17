using System.Runtime.InteropServices;
using ClaudeGraft.Core;

namespace ClaudeGraft.Platform;

public static class DesktopInteraction
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string message, string title, uint flags);

    public static bool ConfirmSharing(IReadOnlyList<string> profiles) => MessageBox(IntPtr.Zero,
        "These profiles are already using the same chats: " + string.Join(", ", profiles)
        + "\n\nOpening the same conversation in both at once can lose messages. Open anyway?",
        "Chats are already open", 0x00000134) == 6;

    public static void Error(string message) => MessageBox(IntPtr.Zero, message, "Claude Graft", 0x00000010);
    public static void Open(GraftConfig config)
    {
        try { Launcher.Open(config, ConfirmSharing); }
        catch (Exception e) { Error(e.Message); }
    }
}
