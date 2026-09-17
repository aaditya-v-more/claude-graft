using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ClaudeGraft;

// Keep Windows' actual move, resize, maximize and keyboard behavior behind the
// macOS title-bar presentation; painted controls must not make a window immovable.
internal sealed class WindowChrome : IDisposable
{
    private readonly nint _window;
    private readonly Window _owner;
    private readonly Func<bool> _sidebar;
    private readonly SubclassProc _callback;
    public double Scale => GetDpiForWindow(_window) / 96.0;

    public WindowChrome(Window owner, Func<bool> sidebar)
    {
        _owner = owner; _sidebar = sidebar;
        _window = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        _callback = HandleMessage;
        if (!SetWindowSubclass(_window, _callback, 2, 0)) throw new InvalidOperationException("Could not attach the window controls.");
        int corners = 2;
        DwmSetWindowAttribute(_window, 33, ref corners, sizeof(int));
        SetWindowPos(_window, 0, 0, 0, 0, 0, 0x0027);
    }
    public void Dispose() => RemoveWindowSubclass(_window, _callback, 2);

    private nint HandleMessage(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0083 && wParam != 0) return 0;
        if (message == 0x0024)
        {
            var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            bounds.MinTrack = new Point { X = (int)(720 * Scale), Y = (int)(460 * Scale) };
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor))
            {
                bounds.MaxPosition = new Point { X = monitor.Work.Left - monitor.Monitor.Left, Y = monitor.Work.Top - monitor.Monitor.Top };
                bounds.MaxSize = new Point { X = monitor.Work.Right - monitor.Work.Left, Y = monitor.Work.Bottom - monitor.Work.Top };
            }
            Marshal.StructureToPtr(bounds, lParam, false);
            return 0;
        }
        if (message == 0x0084)
        {
            GetWindowRect(window, out var rect);
            var x = (short)((long)lParam & 0xffff) - rect.Left;
            var y = (short)(((long)lParam >> 16) & 0xffff) - rect.Top;
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            var edge = (int)(6 * Scale);
            var maximized = _owner.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (!maximized)
            {
                if (y < edge) return x < edge ? 13 : x >= width - edge ? 14 : 12;
                if (y >= height - edge) return x < edge ? 16 : x >= width - edge ? 17 : 15;
                if (x < edge) return 10;
                if (x >= width - edge) return 11;
            }
            var logicalX = x / Scale;
            var sidebarWidth = _sidebar() ? 220 : 154;
            var overControls = logicalX < 90 || (logicalX > sidebarWidth - 78 && logicalX < sidebarWidth);
            if (y < 52 * Scale && !overControls) return 2;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
