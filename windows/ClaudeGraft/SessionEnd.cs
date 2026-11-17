using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace ClaudeGraft;

/// Quits when Windows ends the session or an installer's Restart Manager asks
/// the app to close. Both arrive as WM_ENDSESSION on a top-level window, and a
/// window that only ever hides to the tray would otherwise sit through them
/// until setup gave up and killed the process.
internal static class SessionEnd
{
    private const uint WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    // Held for the life of the process: the native side calls through it.
    private static SubclassProc? _proc;
    private static Action? _quit;

    public static void Watch(Window window, Action quit)
    {
        _quit = quit;
        _proc = Handle;
        SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(window), _proc, 1, 0);
    }

    private static nint Handle(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == WM_QUERYENDSESSION) return 1;
        if (msg == WM_ENDSESSION && wParam != 0)
        {
            _quit?.Invoke();
            return 0;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);
}
