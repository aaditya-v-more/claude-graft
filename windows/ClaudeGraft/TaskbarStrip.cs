using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeGraft;

/// <summary>
/// The Mac menu bar item's face, on the taskbar just left of the notification
/// area: the Claude in front and the accounts it draws on, the one answering
/// ringed, its usage, and the conversation on screen running past.
/// </summary>
// A layered Win32 window drawn by hand rather than a WinUI one: only per-pixel
// alpha lets it sit over the taskbar's own material without a box of its own.
internal sealed class TaskbarStrip : IDisposable
{
    public sealed record Mark(string? IconFile, bool Answering);
    public enum Signal { None, Done, NeedsPerson }
    public sealed record Face(IReadOnlyList<Mark> Marks, string? Usage, string? Title, Signal Signal = Signal.None);

    private readonly Action<Windows.Graphics.PointInt32> _click;
    private readonly WndProc _proc;
    private readonly nint _instance;
    private nint _hwnd, _parent;
    private readonly System.Threading.Timer _place;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;
    private System.Threading.Timer? _scroll;
    private Face? _face;
    private bool _hover, _pressed, _shown;
    // The fill's alpha, fading toward what the pointer asks for. Explorer's own
    // buttons fade only the fill, linearly over 83 ms; their border swaps at once.
    private double _wash, _washFrom;
    private System.Diagnostics.Stopwatch? _washClock;
    private System.Threading.Timer? _washTimer;
    private const double WashMs = 83;
    private double _offset;
    private DateTime _scrollFrom;
    private RECT _at;
    // From the timer's thread: 0 where Widgets is not beside the tray, -1 where it is
    // but could not be found, otherwise its left edge.
    private volatile int _widgetsLeft;
    private readonly Dictionary<string, Image?> _icons = new(StringComparer.OrdinalIgnoreCase);

    private const int TextRoom = 190;   // device-independent, before scale
    private const double ScrollSpeed = 30;   // device-independent pixels a second

    public TaskbarStrip(Action<Windows.Graphics.PointInt32> click)
    {
        _click = click;
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _proc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null), lpszClassName = "ClaudeGraftTaskbarStrip", hCursor = LoadCursor(0, 32512),
        };
        RegisterClassEx(ref wc);
        _instance = wc.hInstance;
        // The taskbar moves and resizes without telling anyone, so it is asked again.
        _place = new System.Threading.Timer(_ =>
        {
            _widgetsLeft = WidgetsBesideTray() ? TaskbarButtons.LeftOf(FindWindow("Shell_TrayWnd", null), "WidgetsButton") ?? -1 : 0;
            _ui.TryEnqueue(Place);
        }, null, 0, 500);
    }

    public void Show(Face? face)
    {
        var changed = face != _face && !(face is not null && _face is not null && Same(face, _face));
        _face = face;
        if (changed) { _offset = 0; _scrollFrom = DateTime.UtcNow.AddSeconds(2); }
        Place();
        if (changed) Draw();
    }

    private static bool Same(Face a, Face b) =>
        a.Usage == b.Usage && a.Title == b.Title && a.Signal == b.Signal && a.Marks.SequenceEqual(b.Marks);

    // MARK: Placement

    // A child of the taskbar rather than a topmost window over it: the taskbar lifts
    // itself over every topmost window whenever it takes focus, and the strip blinked.
    // A taskbar restarted with Explorer takes its children with it, so the strip is
    // made again whenever the one it sat in is gone.
    private void Adopt()
    {
        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == _parent && IsWindow(_hwnd)) return;
        if (_hwnd != 0 && IsWindow(_hwnd)) DestroyWindow(_hwnd);
        _parent = bar;
        _shown = false;
        _hwnd = bar == 0 ? 0 : CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "ClaudeGraftTaskbarStrip", "Claude Graft", WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 1, 1, bar, 0, _instance, 0);
    }

    private void Place()
    {
        Adopt();
        if (_hwnd == 0) return;
        var where = Where();
        // The tray icon stays whatever runs; the strip is only for a Claude to report on.
        if (where is not RECT at || _face is null || System.Diagnostics.Process.GetProcessesByName("claude").Length == 0)
        {
            if (_shown) { ShowWindow(_hwnd, 0); _shown = false; }
            StopScroll();
            return;
        }
        if (!_shown || !at.Equals(_at))
        {
            _at = at;
            Draw();
        }
        if (!_shown) { ShowWindow(_hwnd, 8); _shown = true; }
    }

    /// The band left of the notification area on a top or bottom taskbar, or null where
    /// the strip has nowhere it can sit.
    private RECT? Where()
    {
        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == 0 || !GetWindowRect(bar, out var taskbar)) return null;
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = bar };
        if ((SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0) return null;
        // Not QUNS_BUSY: Windows reports it while the taskbar itself has focus, and the
        // strip hid at every click on the bar. A full-screen window is looked for instead.
        if (SHQueryUserNotificationState(out var state) == 0 && state is 3 or 4) return null;
        if (FullScreenInFront()) return null;
        var monitor = MonitorFromWindow(bar, 2);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        // A side taskbar is too narrow for a line of text; top and bottom both hold one.
        if ((taskbar.Bottom != info.rcMonitor.Bottom && taskbar.Top != info.rcMonitor.Top) || taskbar.Right - taskbar.Left < (info.rcMonitor.Right - info.rcMonitor.Left) * 3 / 4) return null;
        var tray = FindWindowEx(bar, 0, "TrayNotifyWnd", null);
        var right = tray != 0 && GetWindowRect(tray, out var t) && t.Left > taskbar.Left ? t.Left : taskbar.Right - (int)(260 * Scale(bar));
        // With the icons on the left, Windows puts the Widgets button beside the tray.
        var widgets = _widgetsLeft;
        if (widgets > taskbar.Left && widgets < right) right = widgets;
        else if (widgets == -1) right -= (int)(WidgetsRoom * Scale(bar));
        return new RECT { Left = taskbar.Left, Top = taskbar.Top, Right = right, Bottom = taskbar.Bottom };
    }

    private const int WidgetsRoom = 150;   // device-independent; the weather's width when it cannot be read

    private static bool WidgetsBesideTray()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        var left = key?.GetValue("TaskbarAl") is int al && al == 0;
        // Absent reads as shown, as Windows treats it.
        var widgets = key?.GetValue("TaskbarDa") is not int da || da != 0;
        return left && widgets;
    }

    private static bool FullScreenInFront()
    {
        var front = GetForegroundWindow();
        if (front == 0 || !GetWindowRect(front, out var r)) return false;
        var name = new System.Text.StringBuilder(64);
        GetClassName(front, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(front, 2), ref info)
            && r.Left <= info.rcMonitor.Left && r.Top <= info.rcMonitor.Top
            && r.Right >= info.rcMonitor.Right && r.Bottom >= info.rcMonitor.Bottom;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int max);

    private static double Scale(nint hwnd) => GetDpiForWindow(hwnd) / 96.0;

    // MARK: Drawing

    private void Draw()
    {
        if (_face is not { } face || _at.Right <= _at.Left) return;
        var scale = Scale(FindWindow("Shell_TrayWnd", null));
        int Px(double dip) => (int)Math.Round(dip * scale);
        var light = TaskbarIsLight();
        var ink = light ? Color.FromArgb(235, 0, 0, 0) : Color.FromArgb(240, 255, 255, 255);
        using var font = new Font("Segoe UI", (float)(9 * scale), FontStyle.Regular, GraphicsUnit.Point);
        using var bold = new Font("Segoe UI Semibold", (float)(9 * scale), FontStyle.Regular, GraphicsUnit.Point);
        // Explorer's task buttons are 40 px squares in a 48 px bar.
        var height = Math.Min(_at.Bottom - _at.Top - Px(8), Px(40));
        var icon = Px(18);
        var gap = Px(6);
        var pad = Px(10);

        Size Measure(string text, Font f)
        {
            using var probe = new Bitmap(1, 1);
            using var g = Graphics.FromImage(probe);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var size = g.MeasureString(text, f, int.MaxValue, StringFormat.GenericTypographic);
            return new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
        }
        var usage = face.Usage is null ? Size.Empty : Measure(face.Usage, bold);
        var title = string.IsNullOrWhiteSpace(face.Title) ? Size.Empty : Measure(face.Title!, font);
        var room = Math.Min(title.Width, Px(TextRoom));
        var dot = face.Signal == Signal.None ? 0 : Px(8);
        var width = pad + face.Marks.Count * (icon + gap) + (usage.Width > 0 ? usage.Width + gap : 0) + (dot > 0 ? dot + gap : 0) + (room > 0 ? room + gap : 0) - gap + pad;
        width = Math.Max(width, height);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // Never fully clear, or clicks fall through to the taskbar.
            var alpha = Math.Max(1, (int)Math.Round(_wash));
            var wash = light ? 0 : 255;
            // Edges on pixel boundaries: centred on them, the 1 px top line spread over
            // two rows at half strength and vanished.
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var outer = new Rectangle(0, 0, width, height);
            var line = Math.Max(1, Px(1));
            var inner = Rectangle.Inflate(outer, -line, -line);
            // One shape, border included: Explorer's border is the fill's own alpha on
            // hover and press, and a fill meeting a ring left a faint seam at each corner.
            using (var path = Rounded(outer, Px(4)))
            using (var brush = new SolidBrush(Color.FromArgb(alpha, wash, wash, wash))) g.FillPath(brush, path);
            if (_hover && !_pressed && !light) DrawEdge(g, outer, inner, Px(4), line, Px(3));
            g.PixelOffsetMode = PixelOffsetMode.Default;

            var x = pad;
            var y = (height - icon) / 2;
            foreach (var mark in face.Marks)
            {
                if (Icon(mark.IconFile) is { } image) g.DrawImage(image, new Rectangle(x, y, icon, icon));
                if (mark.Answering)
                {
                    using var ring = new Pen(Color.FromArgb(255, 229, 72, 77), Math.Max(1.5f, (float)(1.5 * scale)));
                    using var path = Rounded(new Rectangle(x - Px(2), y - Px(2), icon + Px(4), icon + Px(4)), Px(5));
                    g.DrawPath(ring, path);
                }
                x += icon + gap;
            }
            using var inkBrush = new SolidBrush(ink);
            if (usage.Width > 0)
            {
                g.DrawString(face.Usage, bold, inkBrush, x, (height - usage.Height) / 2f, StringFormat.GenericTypographic);
                x += usage.Width + gap;
            }
            if (dot > 0)
            {
                // Windows' own success and caution colours, for the bar's light or dark.
                var colour = face.Signal == Signal.Done
                    ? (light ? Color.FromArgb(255, 15, 123, 15) : Color.FromArgb(255, 108, 203, 95))
                    : (light ? Color.FromArgb(255, 157, 93, 0) : Color.FromArgb(255, 252, 225, 0));
                using var signal = new SolidBrush(colour);
                g.FillEllipse(signal, x, (height - dot) / 2, dot, dot);
                x += dot + gap;
            }
            if (room > 0)
            {
                var clip = new Rectangle(x, 0, room, height);
                g.SetClip(clip);
                var ty = (height - title.Height) / 2f;
                if (title.Width <= room) g.DrawString(face.Title, font, inkBrush, x, ty, StringFormat.GenericTypographic);
                else
                {
                    // Round and round: the title, a gap, the title again. Drawn apart and
                    // faded there, so the fade thins the letters and not the wash behind.
                    var loop = title.Width + Px(40);
                    var shift = (float)(_offset * scale % loop);
                    using var text = new Bitmap(room, height, PixelFormat.Format32bppPArgb);
                    using (var t = Graphics.FromImage(text))
                    {
                        t.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        t.DrawString(face.Title, font, inkBrush, -shift, ty, StringFormat.GenericTypographic);
                        t.DrawString(face.Title, font, inkBrush, -shift + loop, ty, StringFormat.GenericTypographic);
                    }
                    Fade(text, new Rectangle(0, 0, room, height), Px(10));
                    g.DrawImageUnscaled(text, x, 0);
                }
                g.ResetClip();
            }
        }
        if (title.Width > room) StartScroll(); else StopScroll();

        var left = _at.Right - width - Px(8);
        var top = _at.Top + (_at.Bottom - _at.Top - height) / 2;
        if (left < _at.Left || _hwnd == 0) return;
        // A child is placed in the taskbar's own coordinates.
        SetWindowPos(_hwnd, 0, left - _at.Left, top - _at.Top, bitmap.Width, bitmap.Height, SWP_NOACTIVATE);
        Push(bitmap);
    }

    // Explorer's dark-bar hover lights the border's top: #1A white over the first third
    // of a 3px gradient, settling to the #0F fill below. Laid over the fill rather than
    // replacing it, so its edges cannot dip below the fill. Light bar unmeasured.
    private static void DrawEdge(Graphics g, Rectangle outer, Rectangle inner, int radius, int line, int glow)
    {
        using var ring = Rounded(outer, radius);
        ring.AddPath(Rounded(inner, Math.Max(1, radius - line)), false);
        ring.FillMode = FillMode.Alternate;
        // 12 over 15 makes 26: (26 - 15) / (255 - 15) of full white.
        var lift = Color.FromArgb(12, 255, 255, 255);
        using var lit = new LinearGradientBrush(new Rectangle(0, 0, 1, glow + 1), Color.Empty, Color.Empty, LinearGradientMode.Vertical)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [lift, lift, Color.FromArgb(0, 255, 255, 255)],
                Positions = [0f, 0.33f, 1f],
            },
        };
        var clip = g.Clip;
        g.SetClip(new Rectangle(outer.Left, outer.Top, outer.Width, glow));
        g.FillPath(lit, ring);
        g.Clip = clip;
    }

    // The two ends of a running title fade out rather than cutting a letter in half.
    private static void Fade(Bitmap bitmap, Rectangle band, int edge)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[data.Stride];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var at = data.Scan0 + y * data.Stride;
                Marshal.Copy(at, row, 0, row.Length);
                for (var i = 0; i < edge; i++)
                {
                    var keep = (i + 0.5) / edge;
                    foreach (var x in new[] { band.Left + i, band.Right - 1 - i })
                        for (var c = 0; c < 4; c++) row[x * 4 + c] = (byte)(row[x * 4 + c] * keep);
                }
                Marshal.Copy(row, 0, at, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    private void Push(Bitmap bitmap)
    {
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memory, hbitmap);
        try
        {
            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var from = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(_hwnd, screen, 0, ref size, memory, ref from, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(hbitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    private Image? Icon(string? file)
    {
        if (file is null) return null;
        if (_icons.TryGetValue(file, out var known)) return known;
        try { return _icons[file] = Image.FromFile(file); }
        catch { return _icons[file] = null; }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// The taskbar keeps its own light or dark, apart from apps' and Graft's.
    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    // MARK: Scrolling, with a timer only while a title is too long to stand still.

    private void StartScroll()
    {
        _scroll ??= new System.Threading.Timer(_ => _ui.TryEnqueue(() =>
        {
            if (DateTime.UtcNow < _scrollFrom) return;
            _offset += ScrollSpeed / 30;
            Draw();
        }), null, 0, 33);
    }

    private void StopScroll()
    {
        _scroll?.Dispose();
        _scroll = null;
    }

    // Alphas from Explorer's TaskListButton: hover #0F, pressed #0A on a dark bar.
    private double WashTarget()
    {
        var light = TaskbarIsLight();
        return !_hover ? 0 : _pressed ? (light ? 6 : 10) : (light ? 9 : 15);
    }

    // A frame every 16 ms while the wash moves, and no timer once it has arrived.
    private void Ease()
    {
        _washFrom = _wash;
        _washClock = System.Diagnostics.Stopwatch.StartNew();
        _washTimer ??= new System.Threading.Timer(_ => _ui.TryEnqueue(() =>
        {
            if (_washClock is null) return;
            var t = Math.Min(1, _washClock.Elapsed.TotalMilliseconds / WashMs);
            _wash = _washFrom + (WashTarget() - _washFrom) * t;
            Draw();
            if (t >= 1)
            {
                _washClock = null;
                _washTimer?.Dispose();
                _washTimer = null;
            }
        }), null, 0, 16);
    }

    // MARK: Window

    private nint Proc(nint hwnd, uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case WM_MOUSEMOVE when !_hover:
                _hover = true;
                var track = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = hwnd };
                TrackMouseEvent(ref track);
                Ease();
                return 0;
            case WM_MOUSELEAVE:
                _hover = _pressed = false;
                Ease();
                return 0;
            case WM_LBUTTONDOWN:
                _pressed = true;
                Ease();
                return 0;
            case WM_LBUTTONUP:
                _pressed = false;
                Ease();
                GetWindowRect(hwnd, out var me);
                _click(new Windows.Graphics.PointInt32((me.Left + me.Right) / 2, (me.Top + me.Bottom) / 2));
                return 0;
            case WM_MOUSEACTIVATE:
                return 3;   // MA_NOACTIVATE: a click here must not take focus from the app in front
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    public void Dispose()
    {
        _place.Dispose();
        _washTimer?.Dispose();
        StopScroll();
        if (IsWindow(_hwnd)) DestroyWindow(_hwnd);
        foreach (var image in _icons.Values) image?.Dispose();
    }

    // MARK: Win32

    private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSELEAVE = 0x2A3, WM_MOUSEACTIVATE = 0x21;
    private const uint TME_LEAVE = 2, ABM_GETSTATE = 4, ABS_AUTOHIDE = 1;
    private const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

    private delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    private const uint WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public uint cbSize; public nint hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public nint lParam; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string cls, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int id);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint dst, nint to, ref SIZE size, nint src, ref POINT from, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("shell32.dll")] private static extern uint SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
