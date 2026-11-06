using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace ClaudeGraft;

internal readonly record struct TrayAnchor(PointInt32 Point, Rectangle Screen, Rectangle Work,
    double Scale, Rectangle? Overflow)
{
    internal static TrayAnchor Capture(Guid? iconId)
    {
        // The click may be in an open overflow even when Explorer reports the
        // chevron as the registered icon's position. Capture it before activating.
        bool hasCursor = GetCursorPos(out var cursor);
        var cursorAnchor = AtPoint(new PointInt32(cursor.X, cursor.Y));
        if (hasCursor && cursorAnchor.Overflow.HasValue) return cursorAnchor;

        // Keyboard invocation can leave the pointer on another monitor.
        if (iconId is Guid id && id != Guid.Empty)
        {
            var icon = new IconIdentifier { Size = Marshal.SizeOf<IconIdentifier>(), Id = id };
            if (Shell_NotifyIconGetRect(in icon, out var r) == 0 && r.Right > r.Left && r.Bottom > r.Top)
                return AtPoint(new PointInt32(r.Left + (r.Right - r.Left) / 2, r.Top + (r.Bottom - r.Top) / 2));
        }
        return cursorAnchor;
    }

    internal static TrayAnchor AtPoint(PointInt32 point)
    {
        var monitor = MonitorFromPoint(new Point(point.X, point.Y), 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        double scale = GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _) == 0
            ? dpi / 96.0 : 1.0;
        return new(point, info.Screen.Rectangle, info.Work.Rectangle, scale, OverflowAt(point));
    }

    internal PointInt32 Place(SizeInt32 size, out FlyoutEdge edge)
    {
        int margin = (int)Math.Round(12 * Scale);
        if (Work.IsEmpty)
        {
            edge = FlyoutEdge.Bottom;
            return new PointInt32(Point.X - size.Width - margin, Point.Y - size.Height - margin);
        }
        var at = TrayPlacement.Place(Screen, Work, new Size(size.Width, size.Height), margin, Overflow, out edge);
        return new PointInt32(at.X, at.Y);
    }

    // Windows 11 uses a XAML island; older versions of Explorer use a Win32
    // overflow window. A hidden panel or one on another screen isn't an anchor.
    private static Rectangle? OverflowAt(PointInt32 point)
    {
        foreach (var name in new[] { "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow" })
        {
            nint window = 0;
            while ((window = FindWindowEx(0, window, name, null)) != 0)
                if (IsWindowVisible(window) && GetWindowRect(window, out var bounds)
                    && bounds.Rectangle.Contains(point.X, point.Y))
                    return bounds.Rectangle;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Screen, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier
    {
        public int Size;
        public nint Window;
        public uint Icon;
        public Guid Id;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(in IconIdentifier icon, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
}
