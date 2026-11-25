using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// The overlay dot on each Claude window's own taskbar button: red where it waits
/// on its person, green where a turn finished out of sight. Claude never sets one.
/// </summary>
// Called on the UI thread only: the taskbar's COM object lives in its apartment.
internal sealed class TaskbarBadges : IDisposable
{
    private ITaskbarList3? _list;
    // What each window wears now, so the taskbar is told only of a change.
    private readonly Dictionary<nint, (TaskbarBadge.Kind Kind, bool Light)> _worn = new();
    private readonly Dictionary<(TaskbarBadge.Kind Kind, bool Light, int Size), nint> _icons = new();

    public void Show(IReadOnlyDictionary<string, (nint Window, TaskbarBadge.Kind Kind)> wanted)
    {
        var light = TaskbarStrip.TaskbarIsLight();
        var windows = wanted.Values.Select(w => w.Window).ToHashSet();
        // A window that is gone takes its overlay with it; a new one starts bare.
        foreach (var gone in _worn.Keys.Where(w => !windows.Contains(w)).ToList()) _worn.Remove(gone);
        foreach (var (window, kind) in wanted.Values)
        {
            var was = _worn.TryGetValue(window, out var worn) ? worn.Kind : TaskbarBadge.Kind.None;
            if (kind == was && (kind == TaskbarBadge.Kind.None || worn.Light == light)) continue;
            if (Set(window, kind, light)) _worn[window] = (kind, light);
        }
    }

    public void Clear()
    {
        foreach (var window in _worn.Where(w => w.Value.Kind != TaskbarBadge.Kind.None).Select(w => w.Key).ToList())
            if (!IsWindow(window) || Set(window, TaskbarBadge.Kind.None, false)) _worn.Remove(window);
    }

    private bool Set(nint window, TaskbarBadge.Kind kind, bool light)
    {
        try
        {
            if (_list is null)
            {
                _list = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("56fdf344-fd6d-11d0-958a-006097c9a090"))!)!;
                _list.HrInit();
            }
            var icon = kind == TaskbarBadge.Kind.None ? 0 : IconFor(kind, light);
            var text = kind switch
            {
                TaskbarBadge.Kind.NeedsPerson => L10n.Text("Needs your attention"),
                TaskbarBadge.Kind.Done => L10n.Text("Turn finished"),
                _ => null,
            };
            if (_list.SetOverlayIcon(window, icon, text) >= 0) return true;
        }
        catch (Exception e) { Diagnostics.Note("badge.failed", new Dictionary<string, object?> { ["error"] = e.Message }); }
        // Explorer restarting leaves the object dead; it is made again on the next try.
        _list = null;
        return false;
    }

    private nint IconFor(TaskbarBadge.Kind kind, bool light)
    {
        var size = GetSystemMetricsForDpi(49, GetDpiForSystem());
        if (_icons.TryGetValue((kind, light, size), out var known)) return known;
        // The strip's red, and Windows' success green for the bar's light or dark.
        var fill = kind == TaskbarBadge.Kind.NeedsPerson ? Color.FromArgb(229, 72, 77)
            : light ? Color.FromArgb(15, 123, 15) : Color.FromArgb(108, 203, 95);
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var edge = Math.Max(1f, size / 16f);
            var dot = new RectangleF(edge, edge, size - 2 * edge, size - 2 * edge);
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, dot);
            // The overlay sits on an arbitrary icon, so the dot carries its own edge.
            using var pen = new Pen(Color.FromArgb(230, 255, 255, 255), edge);
            g.DrawEllipse(pen, dot);
        }
        return _icons[(kind, light, size)] = bitmap.GetHicon();
    }

    public void Dispose()
    {
        Clear();
        foreach (var icon in _icons.Values) DestroyIcon(icon);
        _icons.Clear();
        _list = null;
    }

    // Only the slots called are named; the rest hold their places in the vtable.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit(); void AddTab(); void DeleteTab(); void ActivateTab(); void SetActiveAlt();
        void MarkFullscreenWindow();
        void SetProgressValue(); void SetProgressState(); void RegisterTab(); void UnregisterTab();
        void SetTabOrder(); void SetTabActive(); void ThumbBarAddButtons(); void ThumbBarUpdateButtons();
        void ThumbBarSetImageList();
        [PreserveSig] int SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
