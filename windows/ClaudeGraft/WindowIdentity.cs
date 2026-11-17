using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI.Dispatching;

namespace ClaudeGraft;

/// <summary>
/// Gives a shortcut's running Claude a taskbar button of its own: its window
/// carries the shortcut's app ID, so it no longer stacks with the main Claude,
/// and wears the shortcut's icon. Pinning that button reopens it through the
/// launcher. The main Claude is never touched.
/// </summary>
public static class WindowIdentity
{
    private const uint EventObjectShow = 0x8002, EventObjectNameChange = 0x800C;
    private const uint WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;
    private const int GwOwner = 4, GwlExStyle = -20, WsExToolWindow = 0x80;
    private const uint WmSetIcon = 0x0080, WmGetIcon = 0x007F, WmSetText = 0x000C;
    private const int IconSmall = 0, IconBig = 1;

    private delegate void WinEventProc(IntPtr hook, uint @event, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    // Held so the collector cannot take the callback out from under the hook.
    private static WinEventProc? _proc, _nameProc;
    private static IntPtr _hook, _nameHook;
    private static DispatcherQueue? _ui;

    // pid -> what that process is; a start time guards against a reused pid.
    private sealed record Known(DateTime Started, string? Folder);
    private static readonly Dictionary<int, Known> Processes = new();
    private static readonly HashSet<int> Asking = new();
    // What each window wore before, so a quit hands Claude's own icons back
    // rather than leaving handles that die with this process.
    private static readonly Dictionary<IntPtr, (IntPtr Big, IntPtr Small)> Dressed = new();
    private static readonly Dictionary<string, (IntPtr Big, IntPtr Small)> Icons = new(StringComparer.OrdinalIgnoreCase);
    // Claude's own title for each named window, and the one this app put there,
    // so a title Claude sets again is told from an echo of ours.
    private static readonly Dictionary<IntPtr, (string Own, string Shown, string Name)> Titled = new();

    /// Raised, off the UI thread, for each shortcut's Claude process found:
    /// its pid, when it started, and the shortcut's folder.
    public static event Action<int, DateTime, string>? ShortcutFound;

    public static void Start(DispatcherQueue ui)
    {
        _ui = ui;
        _proc = OnShow;
        _hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _proc, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        _nameProc = OnNameChange;
        _nameHook = SetWinEventHook(EventObjectNameChange, EventObjectNameChange, IntPtr.Zero, _nameProc, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        Refresh();
    }

    /// Dresses every Claude window again: at start, and after a shortcut's
    /// name or icon changed.
    public static void Refresh()
    {
        if (_ui is null) return;
        lock (Processes) Processes.Clear();
        var windows = new List<(IntPtr Hwnd, int Pid)>();
        EnumWindows((hwnd, _) =>
        {
            if (IsAppWindow(hwnd) && ClaudePid(hwnd) is int pid) windows.Add((hwnd, pid));
            return true;
        }, IntPtr.Zero);
        foreach (var pid in windows.Select(w => w.Pid).Distinct()) Identify(pid);
    }

    /// Gives every dressed window its own icons and title back.
    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        if (_nameHook != IntPtr.Zero) UnhookWinEvent(_nameHook);
        _hook = _nameHook = IntPtr.Zero;
        foreach (var (hwnd, (own, _, _)) in Titled)
            if (IsWindow(hwnd)) SetTitle(hwnd, own);
        Titled.Clear();
        foreach (var (hwnd, (big, small)) in Dressed)
        {
            if (!IsWindow(hwnd)) continue;
            SendMessage(hwnd, WmSetIcon, IconBig, big);
            SendMessage(hwnd, WmSetIcon, IconSmall, small);
        }
        Dressed.Clear();
    }

    private static void OnShow(IntPtr hook, uint @event, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || !IsAppWindow(hwnd) || ClaudePid(hwnd) is not int pid) return;
        Known? known;
        lock (Processes) Processes.TryGetValue(pid, out known);
        if (known is not null && known.Started == StartTime(pid)) Dress(hwnd, known.Folder);
        else Identify(pid);
    }

    private static void OnNameChange(IntPtr hook, uint @event, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || !Titled.ContainsKey(hwnd)) return;
        // A Claude that rewrites its title as fast as it is corrected turned this into a
        // loop through the UI thread; corrected once a second at most, it ends.
        if (Correcting.TryGetValue(hwnd, out var last) && Environment.TickCount64 - last < 1000)
        {
            if (Pending.Add(hwnd))
                _ = Task.Delay(1000).ContinueWith(_ => _ui?.TryEnqueue(() => { Pending.Remove(hwnd); Correct(hwnd); }));
            return;
        }
        Correct(hwnd);
    }

    private static readonly Dictionary<IntPtr, long> Correcting = new();
    private static readonly HashSet<IntPtr> Pending = new();

    private static void Correct(IntPtr hwnd)
    {
        if (!Titled.TryGetValue(hwnd, out var titled) || !IsWindow(hwnd)) return;
        var current = Title(hwnd);
        if (current == titled.Shown) return;
        Correcting[hwnd] = Environment.TickCount64;
        Name(hwnd, current, titled.Name);
    }

    private static void Name(IntPtr hwnd, string own, string name)
    {
        var shown = ProfileTitle.Compose(own, name);
        Titled[hwnd] = (own, shown, name);
        if (Title(hwnd) != shown) SetTitle(hwnd, shown);
    }

    // The command line comes from WMI, which is too slow for the UI thread.
    private static void Identify(int pid)
    {
        lock (Processes) if (!Asking.Add(pid)) return;
        Task.Run(() =>
        {
            string? folder = null;
            try
            {
                var command = ClaudeProcesses.Enumerate().FirstOrDefault(p => p.pid == pid).command;
                if (command is not null) folder = ClaudeProcesses.ShortcutOf(command, App.Store.Shortcuts.ToList())?.Folder;
            }
            catch (Exception e) { Diagnostics.Note("identity.failed", new Dictionary<string, object?> { ["error"] = e.Message }); }
            var started = StartTime(pid);
            lock (Processes)
            {
                Asking.Remove(pid);
                Processes[pid] = new Known(started, folder);
            }
            if (folder is not null) ShortcutFound?.Invoke(pid, started, folder);
            _ui?.TryEnqueue(() =>
            {
                EnumWindows((hwnd, _) =>
                {
                    if (IsAppWindow(hwnd) && ClaudePid(hwnd) == pid) Dress(hwnd, folder);
                    return true;
                }, IntPtr.Zero);
            });
        });
    }

    private static void Dress(IntPtr hwnd, string? folder)
    {
        if (folder is null || App.Store.Shortcuts.FirstOrDefault(s => s.Folder == folder) is not Shortcut shortcut) return;
        if (Installer.LauncherExe is not string launcher) return;
        var (iconFile, index) = ShortcutIcons.LinkIcon(shortcut);
        try
        {
            SetWindowDetails(hwnd,
                "ClaudeGraft.Profile." + shortcut.Folder,
                $"\"{launcher}\" {shortcut.Folder}",
                shortcut.Name,
                iconFile.Length > 0 ? $"{iconFile},{index}" : null);
        }
        catch (Exception e)
        {
            Diagnostics.Note("identity.failed", new Dictionary<string, object?> { ["error"] = e.Message });
            return;
        }
        var own = Titled.TryGetValue(hwnd, out var titled) ? titled.Own : Title(hwnd);
        Name(hwnd, own, shortcut.Name);
        if (iconFile.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && IconsFrom(iconFile) is var (big, small))
        {
            if (!Dressed.ContainsKey(hwnd))
                Dressed[hwnd] = (SendMessage(hwnd, WmGetIcon, IconBig, IntPtr.Zero), SendMessage(hwnd, WmGetIcon, IconSmall, IntPtr.Zero));
            SendMessage(hwnd, WmSetIcon, IconBig, big);
            SendMessage(hwnd, WmSetIcon, IconSmall, small);
        }
    }

    private static (IntPtr Big, IntPtr Small)? IconsFrom(string file)
    {
        if (Icons.TryGetValue(file, out var cached)) return cached;
        const uint ImageIcon = 1, LoadFromFile = 0x10;
        var dpi = GetDpiForSystem();
        var big = LoadImage(IntPtr.Zero, file, ImageIcon, GetSystemMetricsForDpi(11, dpi), GetSystemMetricsForDpi(12, dpi), LoadFromFile);
        var small = LoadImage(IntPtr.Zero, file, ImageIcon, GetSystemMetricsForDpi(49, dpi), GetSystemMetricsForDpi(50, dpi), LoadFromFile);
        if (big == IntPtr.Zero || small == IntPtr.Zero) return null;
        // Kept for the life of the process: a window may still be wearing an
        // older one after its file was redrawn.
        Icons[file] = (big, small);
        return (big, small);
    }

    /// Forgets the drawn icons so the next dressing loads a redrawn file.
    public static void IconsChanged() => Icons.Clear();

    private static bool IsAppWindow(IntPtr hwnd) =>
        GetAncestor(hwnd, 2) == hwnd
        && GetWindow(hwnd, GwOwner) == IntPtr.Zero
        && (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) == 0;

    private static int? ClaudePid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid != 0 && ProcessFacts.IsClaude((int)pid) ? (int)pid : null;
    }

    private static DateTime StartTime(int pid) => ProcessFacts.StartTime(pid);

    private static string Title(IntPtr hwnd)
    {
        var buffer = new char[GetWindowTextLength(hwnd) + 1];
        var length = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private static void SetTitle(IntPtr hwnd, string title)
    {
        const uint AbortIfHung = 0x0002;
        SendMessageTimeoutText(hwnd, WmSetText, IntPtr.Zero, title, AbortIfHung, 500, out _);
    }

    // MARK: - The window's property store

    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private static void SetWindowDetails(IntPtr hwnd, string id, string relaunch, string name, string? icon)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try
        {
            // The relaunch details go first: the taskbar reads them when the ID
            // changes, and a pin made without them reopens plain Claude.
            Set(store, 2, relaunch);
            Set(store, 4, name);
            Set(store, 3, icon);
            Set(store, 5, id);
            store.Commit();
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static void Set(IPropertyStore store, uint pid, string? value)
    {
        var key = new PropertyKey { Format = AppUserModel, Id = pid };
        var variant = new PropVariant();
        if (value is not null) { variant.Type = 31; variant.Pointer = Marshal.StringToCoTaskMemUni(value); }
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant)); }
        finally { PropVariantClear(ref variant); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer;
        public IntPtr Pointer2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint ms, out IntPtr result);

    // A Claude that has stopped answering must not stop this app with it.
    private static IntPtr SendMessage(IntPtr hwnd, uint message, int wParam, IntPtr lParam)
    {
        const uint AbortIfHung = 0x0002;
        return SendMessageTimeout(hwnd, message, wParam, lParam, AbortIfHung, 500, out var result) == IntPtr.Zero
            ? IntPtr.Zero : result;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutText(IntPtr hwnd, uint message, IntPtr wParam, string lParam, uint flags, uint ms, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, char[] text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
