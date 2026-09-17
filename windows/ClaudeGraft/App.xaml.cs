using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClaudeGraft;

/// <summary>
/// Claude Graft lives in the notification area, not in a window — the same
/// tray-first shape the Mac build has in the menu bar. It comes up with no
/// window shown; the tray icon's menu opens a profile or the manager, and the
/// manager window hides back to the tray rather than quitting.
/// </summary>
public partial class App : Application
{
    public static ShortcutStore Store { get; private set; } = null!;

    /// The app's preferences, applied by every window as it is built and again
    /// whenever they change. Held here, saved on change, and announced so the
    /// open windows re-dress themselves without being hunted down individually.
    public static GraftSettings Settings { get; private set; } = new();
    public static event Action? SettingsChanged;

    public static void ApplySettings(GraftSettings updated)
    {
        // Pressing Done with nothing touched should be as quiet as Cancel: no
        // save, and above all no re-apply, since reassigning a window's backdrop
        // flashes it even when the material is the same.
        if (updated.Theme == Settings.Theme
            && updated.Backdrop == Settings.Backdrop
            && updated.StartHidden == Settings.StartHidden) return;

        Settings = updated;
        updated.Save();
        SettingsChanged?.Invoke();
    }

    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private FlyoutWindow? _flyout;

    // The tray's click callbacks arrive on H.NotifyIcon's message-window
    // thread, not this one; anything touching a WinUI window has to hop back to
    // the UI thread or it faults. Captured here, on the thread that owns the UI.
    private DispatcherQueue? _ui;

    public App()
    {
        UnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(GraftPaths.OwnData);
                File.WriteAllText(Path.Combine(GraftPaths.OwnData, "app-error.txt"), e.Message + "\n" + e.Exception.ToString());
            }
            catch { }
        };
        InitializeComponent();
        DebugSettings.IsXamlResourceReferenceTracingEnabled = Environment.GetCommandLineArgs().Contains("--test-data-root");
        DebugSettings.XamlResourceReferenceFailed += (_, e) =>
        {
            try { Directory.CreateDirectory(GraftPaths.OwnData); File.AppendAllText(Path.Combine(GraftPaths.OwnData, "xaml-errors.txt"), e.Message + "\n"); }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var testIndex = Array.IndexOf(arguments, "--test-data-root");
        if (testIndex >= 0 && testIndex + 1 < arguments.Length)
        {
            var testRoot = Path.GetFullPath(arguments[testIndex + 1]);
            if (!File.Exists(Path.Combine(testRoot, ".graft-test-root")))
                throw new IOException("The test directory must carry a .graft-test-root marker.");
            GraftPaths.ProfilesRootOverride = Path.Combine(testRoot, "profiles");
            GraftPaths.ClaudeProjectsOverride = Path.Combine(testRoot, "transcripts");
            ClaudeProcesses.Enumerate = () => Array.Empty<(int, string)>();
        }
        Store = new ShortcutStore();
        Settings = GraftSettings.Load();
        if (testIndex >= 0 || arguments.Contains("--show")) Settings.StartHidden = false;
        _ui = DispatcherQueue.GetForCurrentThread();
        var instanceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(GraftPaths.OwnData.ToUpperInvariant())));
        _instance = new Mutex(true, @"Local\ClaudeGraft.App." + instanceKey, out var firstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeGraft.Show." + instanceKey);
        if (!firstInstance) { _showEvent.Set(); Exit(); return; }
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => ShowManager(), null, Timeout.Infinite, false);

        // No ContextFlyout: the library renders that as a native popup owned by
        // its own message-only window, which can never take the foreground a
        // popup menu needs to register clicks — the menu draws but every
        // selection is dropped. The right-click menu is built and shown by hand
        // instead, from TrayMenu, with an owner window that can go foreground.
        _tray = new TaskbarIcon
        {
            ToolTipText = "Claude Graft",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            // Without this a left click is held back the length of the
            // double-click timeout, in case a second one follows — half a second
            // of nothing between pressing the icon and the flyout appearing.
            // Nothing here wants the double click, so the single one fires at once.
            NoLeftClickDelay = true,
        };
        // A left click opens the flyout — the account list with its usage, the
        // Mac menu bar item's whole face — while the right click keeps the plain
        // menu as a fallback that needs no window to draw.
        _tray.LeftClickCommand = new RelayCommand(ToggleFlyout);
        _tray.RightClickCommand = new RelayCommand(ShowMenu);
        _tray.ForceCreate();

        // Built now, hidden, so the first left click shows it rather than paying
        // to construct a window and its backdrop before anything appears.
        _flyout = new FlyoutWindow(ShowManager, Quit);

        // A tray app comes up hidden by default — the notification-area icon is
        // the whole of it until asked for more. Turned off, it opens the manager
        // straight away, for someone who would rather see the window on launch.
        if (!Settings.StartHidden) ShowManager();
        if (Store.LoadError is null && testIndex < 0)
            foreach (var shortcut in Store.Shortcuts)
                try { Installer.Install(shortcut); } catch (Exception e) { Diagnostics.Note("shortcut.refresh.failed", new Dictionary<string, object?> { ["error"] = e.GetType().Name }); }
    }

    private Mutex? _instance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    private void ToggleFlyout()
    {
        // Read on this thread, the click's own, before hopping to the UI thread:
        // the pointer is on the icon now and will have moved by the time the
        // window is measured and placed. The foreground window is read here too,
        // as early as the click allows, so the flyout can hand focus back to the
        // app the person was in when it dismisses — before the click has had a
        // chance to move it to the shell.
        var anchor = TrayAnchor.CursorNow();
        var priorForeground = GetForegroundWindow();
        OnUi(() =>
        {
            _flyout ??= new FlyoutWindow(ShowManager, Quit);
            _flyout.Toggle(anchor, priorForeground);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private void ShowMenu()
    {
        Store.Load();

        var items = new List<(string Text, bool Enabled, Action? Invoke)>();
        if (Store.Shortcuts.Count == 0)
        {
            items.Add(("No profiles yet", false, null));
        }
        else
        {
            foreach (var shortcut in Store.Shortcuts)
            {
                var config = Store.ConfigFor(shortcut);
                items.Add(("Open " + shortcut.Name, true, () => Task.Run(() => ClaudeGraft.Platform.DesktopInteraction.Open(config))));
            }
        }
        items.Add((TrayMenu.Separator, false, null));
        items.Add(("Manage Profiles…", true, ShowManager));
        items.Add(("Settings…", true, ShowSettings));
        items.Add(("Support Claude Graft…", true, () => Links.Open(Links.Support)));
        items.Add(("Source", true, () => Links.Open(Links.Source)));
        items.Add(("Quit", true, Quit));

        TrayMenu.Show(items);
    }

    private void ShowManager() => OnUi(() =>
    {
        _window ??= new MainWindow();
        _window.Show();
    });

    /// Settings live in a dialog on the manager window, so opening them from the
    /// tray brings the window up first — a dialog needs a window to sit in.
    private void ShowSettings() => OnUi(() =>
    {
        _window ??= new MainWindow();
        _window.ShowSettings();
    });

    private void Quit() => OnUi(() =>
    {
        _tray?.Dispose();
        Exit();
    });

    /// Runs on the UI thread whether the caller is already there or on the
    /// tray's message-window thread.
    private void OnUi(Action action)
    {
        if (_ui is null || _ui.HasThreadAccess) action();
        else _ui.TryEnqueue(() => action());
    }
}

/// A minimal ICommand for the tray's double- and right-click, which take no parameter.
public sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}

/// <summary>
/// A native Win32 popup menu at the cursor. A popup menu only registers clicks
/// while its owner window holds the foreground, and the tray's own window is
/// message-only and cannot — so this shows a throwaway, fully transparent
/// top-level window at the cursor, makes it foreground, and owns the menu with
/// it. TrackPopupMenuEx returns the chosen id, so no WndProc is needed.
/// </summary>
internal static class TrayMenu
{
    internal const string Separator = "__graft-menu-separator__";

    public static void Show(List<(string Text, bool Enabled, Action? Invoke)> items)
    {
        nint owner = CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TOPMOST, "STATIC", string.Empty,
            WS_POPUP, 0, 0, 1, 1, 0, 0, 0, 0);
        if (owner == 0) return;

        nint menu = CreatePopupMenu();
        if (menu == 0) { DestroyWindow(owner); return; }

        try
        {
            var actions = new List<Action?>();
            uint id = 1;
            foreach (var (text, enabled, invoke) in items)
            {
                if (text == Separator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                    continue;
                }
                uint flags = MF_STRING | (enabled ? 0u : MF_GRAYED);
                AppendMenu(menu, flags, id, text);
                actions.Add(invoke);
                id++;
            }

            GetCursorPos(out var pt);

            // The window has to be visible for SetForegroundWindow to take, so
            // it is shown — but at one transparent pixel it is shown to nobody.
            SetLayeredWindowAttributes(owner, 0, 0, LWA_ALPHA);
            ShowWindow(owner, SW_SHOW);
            SetForegroundWindow(owner);

            uint chosen = TrackPopupMenuEx(menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, owner, 0);

            // The documented trailer: without a message posted to the owner, the
            // menu can leave a stale mouse-capture that eats the next click.
            PostMessage(owner, WM_NULL, 0, 0);

            if (chosen >= 1 && chosen <= actions.Count)
                actions[(int)chosen - 1]?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
            DestroyWindow(owner);
        }
    }

    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x0080, WS_EX_LAYERED = 0x00080000, WS_EX_TOPMOST = 0x0008;
    private const uint LWA_ALPHA = 0x0002;
    private const int SW_SHOW = 5;
    private const uint WM_NULL = 0x0000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint hMenu, uint flags, nuint idNewItem, string? newItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint hMenu, uint flags, int x, int y, nint hWnd, nint tpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
}
