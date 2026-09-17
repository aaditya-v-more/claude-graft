using System.Runtime.InteropServices;
using Windows.Graphics;

namespace ClaudeGraft;

/// The screen edge the taskbar sits on, which is also the edge the flyout slides
/// in from.
internal enum FlyoutEdge { Bottom, Top, Left, Right }

/// Where the flyout sits. A tray flyout belongs in the corner of the screen by
/// the notification area, not under the pointer — which is how Windows' own
/// volume and network flyouts behave, and what makes them land in the same place
/// every time. Following the cursor instead put the flyout wherever on the icon
/// the click happened to fall, which is the wander this replaces. The click point
/// is still read, but only to choose which monitor's corner to use; the corner
/// itself comes from the taskbar's edge, read from how the work area is inset
/// from the monitor.
internal static class TrayAnchor
{
    private const int MarginDip = 12;

    /// Where the pointer is now, in physical pixels — read at the moment of the
    /// click, on the click's own thread, because by the time the window is built
    /// and measured the pointer has moved and the flyout would open under it
    /// rather than by the icon that was pressed.
    public static PointInt32 CursorNow()
    {
        GetCursorPos(out var p);
        return new PointInt32(p.X, p.Y);
    }

    /// The scale of the monitor the flyout will open on, read from that monitor
    /// rather than from the window. A window that is hidden, or was last shown on
    /// another monitor, still reports that monitor's DPI to GetDpiForWindow, so
    /// the size worked out from it lands wrong — the placement wobble that reads
    /// as the flyout never quite sitting in the same spot.
    public static double ScaleFor(PointInt32 anchor)
    {
        var point = new POINT { X = anchor.X, Y = anchor.Y };
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
            return dpiX / 96.0;
        return 1.0;
    }

    /// The top-left corner, in physical pixels, for a window of <paramref name="size"/>,
    /// on the monitor the icon at <paramref name="anchor"/> was clicked. The flyout
    /// tucks into the corner of that monitor's work area nearest the notification
    /// area: the far end of the taskbar — bottom-right for the usual bottom bar,
    /// top-right for a top one, and the near bottom corner for a side bar.
    public static PointInt32 Place(SizeInt32 size, PointInt32 anchor, out FlyoutEdge edge)
    {
        var point = new POINT { X = anchor.X, Y = anchor.Y };
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var margin = (int)Math.Round(MarginDip * ScaleFor(anchor));

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            edge = FlyoutEdge.Bottom;
            return new PointInt32(anchor.X - size.Width - margin, anchor.Y - size.Height - margin);
        }

        var work = info.rcWork;
        var full = info.rcMonitor;

        // Which edge the taskbar occupies shows in the side the work area is
        // pulled in from. The notification area sits at the far end of it, so the
        // flyout goes to that corner. Bottom is the common case and the fallback.
        int x, y;
        if (work.top > full.top)             // taskbar on top — notification area top-right
        {
            edge = FlyoutEdge.Top;
            x = work.right - size.Width - margin;
            y = work.top + margin;
        }
        else if (work.left > full.left)      // taskbar on the left — tray at its foot
        {
            edge = FlyoutEdge.Left;
            x = work.left + margin;
            y = work.bottom - size.Height - margin;
        }
        else if (work.right < full.right)    // taskbar on the right
        {
            edge = FlyoutEdge.Right;
            x = work.right - size.Width - margin;
            y = work.bottom - size.Height - margin;
        }
        else                                 // taskbar on the bottom
        {
            edge = FlyoutEdge.Bottom;
            x = work.right - size.Width - margin;
            y = work.bottom - size.Height - margin;
        }

        // Keep every edge inside the work area on a screen too small to hold it.
        x = Math.Clamp(x, work.left + margin, Math.Max(work.left + margin, work.right - size.Width - margin));
        y = Math.Clamp(y, work.top + margin, Math.Max(work.top + margin, work.bottom - size.Height - margin));
        return new PointInt32(x, y);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
