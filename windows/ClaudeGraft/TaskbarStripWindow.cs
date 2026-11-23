using System.Runtime.InteropServices;

namespace ClaudeGraft;

internal sealed class TaskbarStripWindow(Func<nint, uint, nint, nint, nint?> receive) : IDisposable
{
    private const string ClassName = "ClaudeGraftTaskbarStrip";
    private const uint WM_NCDESTROY = 0x0082;
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const uint WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;

    // Windows keeps the class procedure after the first strip has been switched off and collected.
    private static readonly WindowProc ClassProc = Dispatch;
    private static readonly nint Module = GetModuleHandle(null);
    private static readonly ushort Atom = Register();
    [ThreadStatic] private static Dictionary<nint, TaskbarStripWindow>? _windows;
    private readonly Func<nint, uint, nint, nint, nint?> _receive = receive;
    private nint _parent;
    private bool _disposed;

    internal nint Handle { get; private set; }

    // Explorer can replace the taskbar, taking its child windows with it.
    internal bool Attach(nint parent)
    {
        if (_disposed || (Handle != 0 && parent == _parent)) return false;
        if (Handle != 0 && !DestroyWindow(Handle)) return false;
        _parent = parent;
        if (parent == 0 || Atom == 0) return false;
        Handle = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "Claude Graft",
            WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 1, 1, parent, 0, Module, 0);
        if (Handle == 0) return false;
        (_windows ??= [])[Handle] = this;
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        if (Handle != 0) DestroyWindow(Handle);
    }

    private static ushort Register()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ClassProc),
            hInstance = Module, lpszClassName = ClassName, hCursor = LoadCursor(0, 32512),
        };
        var atom = RegisterClassEx(ref wc);
        if (atom == 0) Core.Diagnostics.Note("strip.class-failed", new Dictionary<string, object?> { ["error"] = Marshal.GetLastWin32Error() });
        return atom;
    }

    private static nint Dispatch(nint hwnd, uint msg, nint w, nint l)
    {
        if (_windows is { } windows && windows.TryGetValue(hwnd, out var window))
        {
            if (msg == WM_NCDESTROY)
            {
                windows.Remove(hwnd);
                window.Handle = 0;
            }
            else if (!window._disposed && window._receive(hwnd, msg, w, l) is nint result) return result;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    private delegate nint WindowProc(nint hwnd, uint msg, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadCursor(nint instance, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
