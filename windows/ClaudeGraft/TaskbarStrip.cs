using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudeGraft.Core;
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
    public sealed record Face(IReadOnlyList<Mark> Marks, string? Usage, string? Title, Signal Signal = Signal.None, string? Name = null, string? Profile = null);

    private readonly Action<Windows.Graphics.PointInt32> _click;
    private readonly TaskbarStripWindow _window;
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
    private double _offset, _fromOffset;
    private DateTime _scrollFrom;
    // Scrolling moves by the clock, so a late tick catches up instead of slowing the title.
    private DateTime? _scrollAt;
    private double _scrollBase;
    private ScrollCache? _cache;
    private DibSurface? _surface;
    // The title, icons and usage sliding out, and the dot and rings easing in or out,
    // each against its own clock; one frame timer serves all and stops when they are done.
    private string? _titleFrom, _usageFrom;
    private string?[]? _iconsFrom;
    private bool _usageFell;
    private Signal _dotFrom;
    private System.Diagnostics.Stopwatch? _titleClock, _dotClock, _iconClock, _usageClock;
    private readonly Dictionary<int, System.Diagnostics.Stopwatch> _ringClocks = [];
    private System.Threading.Timer? _frames;
    private const double SlideMs = 200, PopMs = 250, FadeMs = 150;
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
        _window = new TaskbarStripWindow(Proc);
        // The taskbar moves and resizes without telling anyone, so it is asked again.
        _place = new System.Threading.Timer(_ =>
        {
            _widgetsLeft = WidgetsBesideTray() ? TaskbarButtons.LeftOf(FindWindow("Shell_TrayWnd", null), "WidgetsButton") ?? -1 : 0;
            _ui.TryEnqueue(Place);
        }, null, 0, 500);
    }

    public void Show(Face? face)
    {
        var before = _face;
        var changed = face != before && !(face is not null && before is not null && Same(face, before));
        _face = face;
        if (changed)
        {
            // The first show and a theme change take no part: only a face that was already up.
            if (_shown && before is not null && face is not null && ClientAreaAnimation()) Animate(before, face);
            if (before?.Title != face?.Title || before?.Profile != face?.Profile)
            {
                _fromOffset = _offset;
                _offset = 0;
                _scrollAt = null;
                _scrollFrom = DateTime.UtcNow.AddSeconds(2);
            }
        }
        Place();
        if (changed) Draw();
    }

    private static bool Same(Face a, Face b) =>
        a.Name == b.Name && a.Profile == b.Profile && a.Usage == b.Usage && a.Title == b.Title && a.Signal == b.Signal && a.Marks.SequenceEqual(b.Marks);

    // MARK: Placement

    private void Place()
    {
        if (_window.Attach(FindWindow("Shell_TrayWnd", null))) _shown = false;
        if (_window.Handle == 0) return;
        var where = Where();
        // The tray icon stays whatever runs; the strip is only for a Claude to report on.
        if (where is not RECT at || _face is null || System.Diagnostics.Process.GetProcessesByName("claude").Length == 0)
        {
            if (_shown) { ShowWindow(_window.Handle, 0); _shown = false; }
            StopScroll();
            StopFrames();
            return;
        }
        if (!_shown || !at.Equals(_at))
        {
            _at = at;
            Draw();
        }
        if (!_shown) { ShowWindow(_window.Handle, 8); _shown = true; }
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
        // The WinUI search flyout's frame spans the whole monitor though it draws only a panel.
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "ShellUXFrameWindowClass") return false;
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
        if (_window.Handle == 0 || _face is not { } face || _at.Right <= _at.Left) return;
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
        Color Faded(double opacity) => Color.FromArgb((int)Math.Round(ink.A * opacity), ink);
        void DrawIcon(Graphics g, Image image, Rectangle at, double opacity)
        {
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)opacity });
            g.DrawImage(image, at, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
        }
        void DrawTitle(Graphics t, string text, int textWidth, int room, double offset, float dy, double opacity)
        {
            using var brush = new SolidBrush(Faded(opacity));
            var ty = (height - Measure(text, font).Height) / 2f + dy;
            if (textWidth <= room) { t.DrawString(text, font, brush, 0, ty, StringFormat.GenericTypographic); return; }
            // Round and round: the title, a gap, the title again.
            var loop = textWidth + Px(40);
            var shift = (float)(offset * scale % loop);
            t.DrawString(text, font, brush, -shift, ty, StringFormat.GenericTypographic);
            t.DrawString(text, font, brush, -shift + loop, ty, StringFormat.GenericTypographic);
        }
        var name = string.IsNullOrWhiteSpace(face.Name) ? Size.Empty : Measure(face.Name!, font);
        var usage = face.Usage is null ? Size.Empty : Measure(StripFace.WidestUsage(face.Usage), bold);
        var title = string.IsNullOrWhiteSpace(face.Title) ? Size.Empty : Measure(face.Title!, font);
        var dot = Px(8);
        // The signal sits on the first icon; with no icon shown it has a slot of its own.
        var badged = face.Marks.Count > 0;
        var others = Enumerable.Repeat(icon, face.Marks.Count)
            .Concat(new[] { name.Width, usage.Width, badged ? 0 : dot }.Where(w => w > 0)).ToList();
        // One width whatever is showing: the usual icon, usage and title, with the title
        // taking what the other parts leave, and none where they leave nothing.
        var width = Math.Max(pad * 2 + icon + Measure("888%", bold).Width + Px(TextRoom) + gap * 2, height);
        var room = Math.Max(0, width - pad * 2 - others.Sum() - gap * others.Count);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // Never fully clear, or clicks fall through to the taskbar.
            var alpha = Math.Max(1, (int)Math.Round(_wash));
            // Edges on pixel boundaries: centred on them, the 1 px top line spread over
            // two rows at half strength and vanished.
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var outer = new Rectangle(0, 0, width, height);
            var line = Math.Max(1, Px(1));
            var inner = Rectangle.Inflate(outer, -line, -line);
            // One shape, border included: Explorer's border is the fill's own alpha on
            // hover and press, and a fill meeting a ring left a faint seam at each corner.
            using (var path = Rounded(outer, Px(4)))
            using (var brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255))) g.FillPath(brush, path);
            if (_hover && light) DrawLightEdge(g, outer, inner, Px(4), line, _pressed ? 0 : Px(3));
            else if (_hover && !_pressed) DrawEdge(g, outer, inner, Px(4), line, Px(3));
            g.PixelOffsetMode = PixelOffsetMode.Default;

            // Slides are cut at the strip's own edge, whichever part is moving.
            using var content = Rounded(inner, Math.Max(1, Px(4) - line));
            g.SetClip(content);
            var x = pad;
            var y = (height - icon) / 2;
            var travel = (float)StripFace.Travel(height);
            var firstIcon = Rectangle.Empty;
            for (var i = 0; i < face.Marks.Count; i++)
            {
                var mark = face.Marks[i];
                var slot = new Rectangle(x, y, icon, icon);
                if (Slide(_iconClock) is { } t0 && _iconsFrom is { } was && i < was.Length && was[i] != mark.IconFile)
                {
                    var e = StripFace.OutCubic(t0);
                    if (Icon(was[i]) is { } old) DrawIcon(g, old, new Rectangle(x, y - (int)Math.Round(travel * e), icon, icon), StripFace.OutgoingOpacity(t0));
                    if (Icon(mark.IconFile) is { } next) DrawIcon(g, next, new Rectangle(x, y + (int)Math.Round(travel * (1 - e)), icon, icon), StripFace.IncomingOpacity(t0));
                }
                else if (Icon(mark.IconFile) is { } image) g.DrawImage(image, slot);

                double ringScale = 1, ringAlpha = 1;
                if (_ringClocks.TryGetValue(i, out var ringClock))
                {
                    var t = ringClock.Elapsed.TotalMilliseconds / (mark.Answering ? PopMs : FadeMs);
                    if (mark.Answering) ringScale = StripFace.Pop(t);
                    else ringAlpha = 1 - Math.Min(1, t);
                }
                if ((mark.Answering || _ringClocks.ContainsKey(i)) && ringAlpha > 0)
                {
                    var side = (icon + Px(4)) * ringScale;
                    var ringBox = new RectangleF((float)(x + (icon - side) / 2), (float)(y + (icon - side) / 2), (float)side, (float)side);
                    if (side >= 2)
                    {
                        using var ring = new Pen(Color.FromArgb((int)Math.Round(255 * ringAlpha), 229, 72, 77), Math.Max(1.5f, (float)(1.5 * scale)));
                        using var path = Rounded(Rectangle.Round(ringBox), Math.Max(1, (int)Math.Min(Px(5) * ringScale, side / 2)));
                        g.DrawPath(ring, path);
                    }
                }
                if (i == 0) firstIcon = slot;
                x += icon + gap;
            }
            using var inkBrush = new SolidBrush(ink);
            if (name.Width > 0)
            {
                g.DrawString(face.Name, font, inkBrush, x, (height - name.Height) / 2f, StringFormat.GenericTypographic);
                x += name.Width + gap;
            }
            if (usage.Width > 0)
            {
                var uy = (height - usage.Height) / 2f;
                if (Slide(_usageClock) is { } t1 && _usageFrom is not null)
                {
                    var way = _usageFell ? -1 : 1;
                    var e = StripFace.OutCubic(t1);
                    using var old = new SolidBrush(Faded(StripFace.OutgoingOpacity(t1)));
                    using var next = new SolidBrush(Faded(StripFace.IncomingOpacity(t1)));
                    g.DrawString(_usageFrom, bold, old, x, uy - (float)(way * travel * e), StringFormat.GenericTypographic);
                    g.DrawString(face.Usage, bold, next, x, uy + (float)(way * travel * (1 - e)), StringFormat.GenericTypographic);
                }
                else g.DrawString(face.Usage, bold, inkBrush, x, uy, StringFormat.GenericTypographic);
                x += usage.Width + gap;
            }
            var shown = face.Signal;
            double dotScale = 1, dotAlpha = 1;
            if (_dotClock is { } dotClock)
            {
                var t = dotClock.Elapsed.TotalMilliseconds / (shown == Signal.None ? FadeMs : PopMs);
                if (shown == Signal.None) { shown = _dotFrom; dotAlpha = 1 - Math.Min(1, t); }
                else dotScale = StripFace.Pop(t);
            }
            if (shown != Signal.None && dotAlpha > 0)
            {
                // Windows' own success and caution colours, for the bar's light or dark.
                var colour = shown == Signal.Done
                    ? (light ? Color.FromArgb(255, 15, 123, 15) : Color.FromArgb(255, 108, 203, 95))
                    : (light ? Color.FromArgb(255, 157, 93, 0) : Color.FromArgb(255, 252, 225, 0));
                var alpha8 = (int)Math.Round(255 * dotAlpha);
                var size = (float)(dot * dotScale);
                // On the icon's upper-right corner, cut out of it by a ring in the bar's own colour.
                var centre = badged ? new PointF(firstIcon.Right - Px(1), firstIcon.Top + Px(1)) : new PointF(x + dot / 2f, height / 2f);
                if (badged)
                {
                    var cut = Math.Max(1, Px(1));
                    using var ringBrush = new SolidBrush(Color.FromArgb(alpha8, light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32)));
                    g.FillEllipse(ringBrush, centre.X - size / 2 - cut, centre.Y - size / 2 - cut, size + 2 * cut, size + 2 * cut);
                }
                using var signal = new SolidBrush(Color.FromArgb(alpha8, colour));
                g.FillEllipse(signal, centre.X - size / 2, centre.Y - size / 2, size, size);
            }
            if (!badged) x += dot + gap;
            if (room > 0 && title.Width > room && _at.Right - width - Px(8) >= _at.Left && !Animating && _washClock is null)
            {
                g.Flush();
                var key = new CacheKey(face, light, scale, _hover, _pressed, _wash, _at);
                var loop = title.Width + Px(40);
                _cache?.Dispose();
                _cache = ScrollCache.Build(key, bitmap, x, room, Math.Min(Px(10), room / 2), loop, t =>
                {
                    t.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    DrawTitle(t, face.Title!, title.Width, room, 0, 0, 1);
                });
            }
            if (room > 0)
            {
                // Drawn apart and faded there, so the fade thins the letters and not the wash behind.
                using var text = new Bitmap(room, height, PixelFormat.Format32bppPArgb);
                var slide = _titleFrom is null ? null : Slide(_titleClock);
                var feather = title.Width > room ? 1.0 : 0;
                using (var t = Graphics.FromImage(text))
                {
                    t.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    if (slide is { } t2)
                    {
                        var e = (float)StripFace.OutCubic(t2);
                        var oldWidth = Measure(_titleFrom!, font).Width;
                        DrawTitle(t, _titleFrom!, oldWidth, room, _fromOffset, -travel * e, StripFace.OutgoingOpacity(t2));
                        DrawTitle(t, face.Title!, title.Width, room, _offset, travel * (1 - e), StripFace.IncomingOpacity(t2));
                        feather = StripFace.Lerp(oldWidth > room ? 1 : 0, feather, t2);
                    }
                    else if (title.Width > 0) DrawTitle(t, face.Title!, title.Width, room, _offset, 0, 1);
                }
                if (feather > 0) Fade(text, new Rectangle(0, 0, room, height), Math.Min(Px(10), room / 2), feather);
                g.DrawImageUnscaled(text, x, 0);
            }
        }
        if (room > 0 && title.Width > room) StartScroll(); else StopScroll();

        var left = _at.Right - width - Px(8);
        var top = _at.Top + (_at.Bottom - _at.Top - height) / 2;
        if (left < _at.Left) return;
        // A child is placed in the taskbar's own coordinates.
        SetWindowPos(_window.Handle, 0, left - _at.Left, top - _at.Top, bitmap.Width, bitmap.Height, SWP_NOACTIVATE);
        Push(bitmap);
    }

    // Explorer's dark-bar hover lights the border's top: #1A white over the first third
    // of a 3px gradient, settling to the #0F fill below. Laid over the fill rather than
    // replacing it, so its edges cannot dip below the fill.
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

    // Explorer's light-bar border is the dark one flipped: #05 black all round, deepening
    // to #0F over the bottom third of a 3px gradient on hover. Flat #05 when pressed.
    private static void DrawLightEdge(Graphics g, Rectangle outer, Rectangle inner, int radius, int line, int shade)
    {
        using var ring = Rounded(outer, radius);
        ring.AddPath(Rounded(inner, Math.Max(1, radius - line)), false);
        ring.FillMode = FillMode.Alternate;
        using (var flat = new SolidBrush(Color.FromArgb(5, 0, 0, 0))) g.FillPath(flat, ring);
        if (shade == 0) return;
        // 10 over 5 makes 15: (15 - 5) / (255 - 5) of full black.
        var deep = Color.FromArgb(10, 0, 0, 0);
        var band = new Rectangle(outer.Left, outer.Bottom - shade, outer.Width, shade);
        using var dark = new LinearGradientBrush(new Rectangle(0, band.Top - 1, 1, shade + 1), Color.Empty, Color.Empty, LinearGradientMode.Vertical)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(0, 0, 0, 0), deep, deep],
                Positions = [0f, 0.67f, 1f],
            },
        };
        var clip = g.Clip;
        g.SetClip(band);
        g.FillPath(dark, ring);
        g.Clip = clip;
    }

    // The two ends of a running title fade out rather than cutting a letter in half;
    // strength eases that in or out as a long title comes or goes.
    private static void Fade(Bitmap bitmap, Rectangle band, int edge, double strength)
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
                    var keep = StripFace.FeatherKeep(i, edge, strength);
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
            UpdateLayeredWindow(_window.Handle, screen, 0, ref size, memory, ref from, 0, ref blend, 2);
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
    internal static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    // MARK: Animation, only where Windows' "Animation effects" is on.

    private static bool ClientAreaAnimation() => SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) && on != 0;

    private void Animate(Face before, Face now)
    {
        if (!string.IsNullOrWhiteSpace(before.Title) && !string.IsNullOrWhiteSpace(now.Title) && before.Title != now.Title)
        {
            _titleFrom = before.Title;
            _titleClock = System.Diagnostics.Stopwatch.StartNew();
        }
        if (before.Signal != now.Signal)
        {
            _dotFrom = before.Signal;
            _dotClock = System.Diagnostics.Stopwatch.StartNew();
        }
        // A different count changes the width anyway, so the marks just snap.
        _ringClocks.Clear();
        if (before.Marks.Count == now.Marks.Count)
        {
            if (before.Marks.Zip(now.Marks).Any(p => p.First.IconFile != p.Second.IconFile))
            {
                _iconsFrom = before.Marks.Select(m => m.IconFile).ToArray();
                _iconClock = System.Diagnostics.Stopwatch.StartNew();
            }
            for (var i = 0; i < now.Marks.Count; i++)
                if (before.Marks[i].Answering != now.Marks[i].Answering) _ringClocks[i] = System.Diagnostics.Stopwatch.StartNew();
        }
        else { _iconClock = null; _iconsFrom = null; }
        if (before.Usage is not null && now.Usage is not null && before.Usage != now.Usage)
        {
            _usageFrom = before.Usage;
            // Only the same Claude's own figure falling slides down; another Claude comes in from below like the rest.
            _usageFell = string.Equals(before.Profile, now.Profile, StringComparison.OrdinalIgnoreCase) && StripFace.UsageFell(before.Usage, now.Usage);
            _usageClock = System.Diagnostics.Stopwatch.StartNew();
        }
        if (Animating) _frames ??= new System.Threading.Timer(_ => _ui.TryEnqueue(Frame), null, 0, 16);
    }

    // How far a slide has gone, or null once it is over, so a finished one draws the settled
    // face whether or not a frame has cleared it.
    private static double? Slide(System.Diagnostics.Stopwatch? clock) =>
        clock is { } c && c.Elapsed.TotalMilliseconds < SlideMs ? c.Elapsed.TotalMilliseconds / SlideMs : null;

    private bool Animating => _titleClock is not null || _dotClock is not null || _iconClock is not null || _usageClock is not null || _ringClocks.Count > 0;

    private void Frame()
    {
        if (_titleClock?.Elapsed.TotalMilliseconds >= SlideMs) { _titleClock = null; _titleFrom = null; }
        if (_iconClock?.Elapsed.TotalMilliseconds >= SlideMs) { _iconClock = null; _iconsFrom = null; }
        if (_usageClock?.Elapsed.TotalMilliseconds >= SlideMs) { _usageClock = null; _usageFrom = null; }
        var dotMs = _face?.Signal == Signal.None ? FadeMs : PopMs;
        if (_dotClock?.Elapsed.TotalMilliseconds >= dotMs) _dotClock = null;
        foreach (var (i, clock) in _ringClocks.ToArray())
        {
            var answering = _face is not null && i < _face.Marks.Count && _face.Marks[i].Answering;
            if (clock.Elapsed.TotalMilliseconds >= (answering ? PopMs : FadeMs)) _ringClocks.Remove(i);
        }
        // The last frame is drawn settled, then the timer goes.
        Draw();
        if (!Animating) StopFrames();
    }

    private void StopFrames()
    {
        _frames?.Dispose();
        _frames = null;
        _titleClock = _dotClock = _iconClock = _usageClock = null;
        _titleFrom = _usageFrom = null;
        _iconsFrom = null;
        _ringClocks.Clear();
    }

    // MARK: Scrolling, with a timer only while a title is too long to stand still.

    private void StartScroll()
    {
        _scroll ??= new System.Threading.Timer(_ => _ui.TryEnqueue(() =>
        {
            var now = DateTime.UtcNow;
            if (now < _scrollFrom) return;
            if (_scrollAt is null) { _scrollAt = now; _scrollBase = _offset; }
            _offset = _scrollBase + (now - _scrollAt.Value).TotalSeconds * ScrollSpeed;
            if (!ScrollFrame()) Draw();
        }), null, 0, 33);
    }

    private void StopScroll()
    {
        _scroll?.Dispose();
        _scroll = null;
        _scrollAt = null;
        _cache?.Dispose();
        _cache = null;
    }

    // A tick that only moves the title: the cached face with the cached title cut at the
    // new offset. Anything that would change the face falls back to a full draw, which
    // rebuilds the cache.
    private bool ScrollFrame()
    {
        if (_cache is not { } c || Animating || _washClock is not null || _window.Handle == 0) return false;
        var k = c.Key;
        if (!ReferenceEquals(_face, k.Face) || _hover != k.Hover || _pressed != k.Pressed || _wash != k.Wash
            || !_at.Equals(k.At) || TaskbarIsLight() != k.Light || Scale(FindWindow("Shell_TrayWnd", null)) != k.Scale) return false;
        var surface = _surface is { } s && s.Width == c.Width && s.Height == c.Height ? s : (_surface = DibSurface.Replace(_surface, c.Width, c.Height));
        if (surface is null) return false;
        c.Paint(surface, (int)Math.Round(_offset * k.Scale % c.Loop));
        surface.Push(_window.Handle);
        return true;
    }

    // White alphas from Explorer's TaskListButton: hover #0F, pressed #0A on a dark bar;
    // hover #B3, pressed #4C on a light one.
    private double WashTarget()
    {
        var light = TaskbarIsLight();
        return !_hover ? 0 : _pressed ? (light ? 76 : 10) : (light ? 179 : 15);
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

    private nint? Proc(nint hwnd, uint msg, nint w, nint l)
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
                ClaudeGraft.Core.Diagnostics.Note("strip.click");
                _pressed = false;
                Ease();
                GetWindowRect(hwnd, out var me);
                _click(new Windows.Graphics.PointInt32((me.Left + me.Right) / 2, (me.Top + me.Bottom) / 2));
                return 0;
            case WM_MOUSEACTIVATE:
                return 3;   // MA_NOACTIVATE: a click here must not take focus from the app in front
        }
        return null;
    }

    public void Dispose()
    {
        _window.Dispose();
        _face = null;
        _washClock = null;
        _place.Dispose();
        _washTimer?.Dispose();
        StopScroll();
        StopFrames();
        _surface?.Dispose();
        _surface = null;
        foreach (var image in _icons.Values) image?.Dispose();
        _icons.Clear();
    }

    // MARK: Scroll cache

    private sealed record CacheKey(Face Face, bool Light, double Scale, bool Hover, bool Pressed, double Wash, RECT At);

    // The strip without its title, and the title laid out once for the whole loop, both as
    // plain bytes so a tick is row copies and a feather over the edge columns only.
    private sealed class ScrollCache : IDisposable
    {
        public CacheKey Key { get; }
        public int Width { get; }
        public int Height { get; }
        public int Loop { get; }
        private readonly byte[] _face, _title, _window;
        private readonly int _x, _room, _edge, _titleStride;
        private readonly double[] _keep;
        private readonly System.Runtime.InteropServices.GCHandle _pin;
        private readonly Bitmap _view;

        private ScrollCache(CacheKey key, byte[] face, byte[] title, int width, int height, int x, int room, int edge, int loop, int titleStride)
        {
            Key = key; _face = face; _title = title; Width = width; Height = height;
            _x = x; _room = room; _edge = edge; Loop = loop; _titleStride = titleStride;
            _keep = Enumerable.Range(0, edge).Select(i => StripFace.FeatherKeep(i, edge, 1)).ToArray();
            _window = new byte[room * 4 * height];
            _pin = System.Runtime.InteropServices.GCHandle.Alloc(_window, System.Runtime.InteropServices.GCHandleType.Pinned);
            _view = new Bitmap(room, height, room * 4, PixelFormat.Format32bppPArgb, _pin.AddrOfPinnedObject());
        }

        public static ScrollCache Build(CacheKey key, Bitmap face, int x, int room, int edge, int loop, Action<Graphics> drawTitle)
        {
            using var strip = new Bitmap(loop + room, face.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(strip)) drawTitle(g);
            var (faceBytes, _) = Bytes(face);
            var (titleBytes, titleStride) = Bytes(strip);
            return new ScrollCache(key, faceBytes, titleBytes, face.Width, face.Height, x, room, edge, loop, titleStride);
        }

        private static (byte[], int) Bytes(Bitmap bitmap)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bytes = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                return (bytes, data.Stride);
            }
            finally { bitmap.UnlockBits(data); }
        }

        public void Paint(DibSurface surface, int shift)
        {
            Marshal.Copy(_face, 0, surface.Bits, _face.Length);
            var row = _room * 4;
            for (var y = 0; y < Height; y++)
            {
                Buffer.BlockCopy(_title, y * _titleStride + shift * 4, _window, y * row, row);
                for (var i = 0; i < _edge; i++)
                {
                    var keep = _keep[i];
                    var left = y * row + i * 4;
                    var right = y * row + (_room - 1 - i) * 4;
                    for (var c = 0; c < 4; c++)
                    {
                        _window[left + c] = (byte)(_window[left + c] * keep);
                        _window[right + c] = (byte)(_window[right + c] * keep);
                    }
                }
            }
            surface.Graphics.DrawImage(_view, _x, 0, new Rectangle(0, 0, _room, Height), GraphicsUnit.Pixel);
            surface.Graphics.Flush();
        }

        public void Dispose()
        {
            _view.Dispose();
            _pin.Free();
        }
    }

    // A DIB section the strip is drawn into directly and pushed from, so a tick makes no
    // bitmap handle of its own.
    private sealed class DibSurface : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        public nint Bits { get; }
        public Graphics Graphics { get; }
        private readonly Bitmap _bitmap;
        private readonly nint _dc, _dib, _old;

        private DibSurface(int width, int height, nint dc, nint dib, nint bits)
        {
            Width = width; Height = height; _dc = dc; _dib = dib; Bits = bits;
            _old = SelectObject(dc, dib);
            _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            Graphics = Graphics.FromImage(_bitmap);
        }

        public static DibSurface? Replace(DibSurface? old, int width, int height)
        {
            old?.Dispose();
            var header = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            var dib = CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
            if (dib == 0) return null;
            var screen = GetDC(0);
            var dc = CreateCompatibleDC(screen);
            ReleaseDC(0, screen);
            return new DibSurface(width, height, dc, dib, bits);
        }

        public void Push(nint window)
        {
            var screen = GetDC(0);
            var size = new SIZE { cx = Width, cy = Height };
            var from = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(window, screen, 0, ref size, _dc, ref from, 0, ref blend, 2);
            ReleaseDC(0, screen);
        }

        public void Dispose()
        {
            Graphics.Dispose();
            _bitmap.Dispose();
            SelectObject(_dc, _old);
            DeleteObject(_dib);
            DeleteDC(_dc);
        }
    }

    // MARK: Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);

    private const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSELEAVE = 0x2A3, WM_MOUSEACTIVATE = 0x21;
    private const uint TME_LEAVE = 2, ABM_GETSTATE = 4, ABS_AUTOHIDE = 1;
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public uint cbSize; public nint hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public nint lParam; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string cls, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint dst, nint to, ref SIZE size, nint src, ref POINT from, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("shell32.dll")] private static extern uint SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
