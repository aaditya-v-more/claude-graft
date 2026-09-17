using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.System;
using WinRT;

namespace ClaudeGraft;

/// The tray flyout, as a window rather than a control. WinUI 3 gives a tray icon
/// no popup of its own, so this is a borderless window shaped and coloured to read
/// as a flyout — mica behind, rounded corners — that slides up from behind the
/// taskbar beside the icon and slides back down to dismiss, the way a menu does.
/// It is built once and reused: showing it again refreshes the list rather than
/// making a new window.
public sealed class FlyoutWindow : Window
{
    private readonly FlyoutView _view = new();
    private bool _shown;
    private bool _hiding;
    private bool _animating;
    private bool _refitPending;
    private PointInt32 _anchor;
    private FlyoutEdge _edge;
    private RectInt32 _rect;   // where the window rests, once the slide has settled
    private nint _priorForeground;   // the window to hand focus back to on dismiss
    private ISystemBackdropControllerWithTargets? _backdrop;
    private SystemBackdropConfiguration? _backdropConfig;

    public FlyoutWindow(Action openManager, Action quit)
    {
        Content = _view;
        _view.OpenManagerRequested += openManager;
        _view.QuitRequested += quit;
        _view.DismissRequested += Hide;

        // Borderless, always-on-top, out of the taskbar and the switcher. Not
        // the context-menu presenter it looks like it wants: that one shows
        // without ever activating the window, so it never hears the click that
        // lands elsewhere and never dismisses. A plain overlapped presenter
        // activates when shown, which is what makes losing focus mean something.
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        // Deliberately not always-on-top: the taskbar is topmost, so a flyout
        // that is not stays below it in z-order and is hidden by it while it
        // slides in and out from behind — no frame where it draws over the bar,
        // and no need to shuffle the taskbar's own z-order to get there. Brought
        // to the foreground on show, it still sits above ordinary windows.
        presenter.IsAlwaysOnTop = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        ApplyAppearance();
        App.SettingsChanged += ApplyAppearance;
        StyleFrame();
        RemoveNonClientFrame();

        // The list grows after it is shown, as each account's usage arrives and
        // its bars appear. The view is stretched to the window and cannot see its
        // own overflow, so it says when it has changed size and the window refits.
        _view.LayoutChanged += () => { if (_shown) Refit(); };

        // Esc dismisses wherever focus sits, so it rides an accelerator rather
        // than a key handler on one focused element.
        var esc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; Hide(); };
        _view.KeyboardAccelerators.Add(esc);
        // Otherwise WinUI shows the accelerator's key — "Esc" — as a tooltip on
        // hover anywhere over the view.
        _view.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        // Losing focus is the light-dismiss: a click anywhere else takes it away.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Hide();
        };
    }

    public void Toggle(PointInt32 anchor, nint priorForeground)
    {
        if (_shown) Hide();
        else Show(anchor, priorForeground);
    }

    private void Show(PointInt32 anchor, nint priorForeground)
    {
        _anchor = anchor;
        _shown = true;
        _hiding = false;
        // Whatever held focus before is handed it back on dismiss. Captured at the
        // click, before the shell could take it.
        _priorForeground = priorForeground;
        _view.Reload();
        Refit();   // sets _rect and puts the window at its resting corner

        // The whole flyout slides in from behind the taskbar, the way
        // EverythingToolbar does it: the window starts a full height below its
        // rest, so it sits entirely behind the bar, and rises into place. It is
        // not topmost, so the topmost taskbar hides the part still overlapping it
        // and the flyout reads as emerging from the bar rather than sliding over
        // it. The mica rides along because it is the window's own.
        var full = FullOffset(extra: 0);
        AppWindow.Move(new PointInt32(_rect.X + full.X, _rect.Y + full.Y));
        AppWindow.Show(activateWindow: true);
        Activate();
        StyleFrame();
        // A tray click leaves the shell in the foreground, not this process, so
        // the window comes up without focus and would never hear it being lost.
        // Pulling it to the foreground is what arms the click-away dismiss.
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));

        // The window is the whole motion; the content rides with it as one piece.
        // Parallaxing the content within the window was tried and dropped: the
        // view fills the window with the mica as its surface, so displacing it
        // relative to the frame bares a strip of empty window on the edge it
        // vacates — and pushing that strip behind the taskbar to hide it flips the
        // content's apparent direction, so it read as the content sliding the
        // opposite way to the flyout.
        SlideWindow(full, PointZero, 250, easeIn: false, onDone: () =>
        {
            // A grow deferred while the slide ran — usage bars arriving — is
            // applied now that the window is at rest.
            if (_refitPending) { _refitPending = false; Refit(); }
        });
    }

    private void Hide()
    {
        if (!_shown || _hiding) return;
        _hiding = true;
        // Dismiss is the reverse: the whole window slides back down behind the
        // taskbar and only then is put away. No fade — the bar swallows it.
        var full = FullOffset(extra: 50);
        SlideWindow(PointZero, full, 250, easeIn: true, onDone: () =>
        {
            _shown = false;
            _hiding = false;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // Order matters. First push the window right off every screen, so it is
            // no longer sitting behind the taskbar. Then, while it is still the
            // foreground window, hand focus back to whatever had it before: that is
            // the compose that makes the taskbar's acrylic re-sample — with nothing
            // behind it now — and it clears the shadow the slide left. The
            // thread-attach the handoff needs works only while we still hold the
            // foreground, which is why this comes before Hide, not after.
            AppWindow.Move(new PointInt32(100000, 100000));
            if (_priorForeground != 0 && _priorForeground != hwnd)
                ForceForeground(_priorForeground);
            AppWindow.Hide();
        });
    }

    private static readonly PointInt32 PointZero = new(0, 0);

    /// A full window dimension in the taskbar's direction, in physical pixels —
    /// far enough that the window starts, or ends, entirely behind the bar. The
    /// extra clears any last edge on the way out.
    private PointInt32 FullOffset(int extra)
    {
        return _edge switch
        {
            FlyoutEdge.Top => new PointInt32(0, -(_rect.Height + extra)),
            FlyoutEdge.Left => new PointInt32(-(_rect.Width + extra), 0),
            FlyoutEdge.Right => new PointInt32(_rect.Width + extra, 0),
            _ => new PointInt32(0, _rect.Height + extra),   // bottom
        };
    }

    /// Brings a window to the foreground even from a process that no longer owns
    /// the foreground, by briefly attaching to the current foreground thread's
    /// input queue — the standard way past the foreground lock. Ported from
    /// EverythingToolbar's helper of the same name.
    private static void ForceForeground(nint handle)
    {
        if (SetForegroundWindow(handle))
        {
            SetActiveWindow(handle);
            return;
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(handle, out _);

        if (foregroundThread != targetThread)
            AttachThreadInput(foregroundThread, targetThread, true);
        try
        {
            SetForegroundWindow(handle);
            SetActiveWindow(handle);
        }
        finally
        {
            if (foregroundThread != targetThread)
                AttachThreadInput(foregroundThread, targetThread, false);
        }
    }

    /// Walks the window from one offset of its resting corner to another over the
    /// given span, a frame at a time. AppWindow has no animation of its own, so
    /// the move is driven off the frame tick, synced to the compositor each frame
    /// so a full-height slide does not tear.
    private void SlideWindow(PointInt32 from, PointInt32 to, int ms, bool easeIn, Action onDone)
    {
        _animating = true;
        var clock = Stopwatch.StartNew();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += Tick;

        void Tick(object? sender, object e)
        {
            var t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / ms);
            // Power-5 in or out, to match EverythingToolbar's feel.
            var eased = easeIn ? Math.Pow(t, 5) : 1 - Math.Pow(1 - t, 5);
            var dx = (int)Math.Round(from.X + (to.X - from.X) * eased);
            var dy = (int)Math.Round(from.Y + (to.Y - from.Y) * eased);
            AppWindow.Move(new PointInt32(_rect.X + dx, _rect.Y + dy));
            DwmFlush();
            if (t >= 1.0)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Tick;
                _animating = false;
                onDone();
            }
        }
    }

    /// Sizes the window to the content and tucks it against the taskbar. Held off
    /// while a slide is running, so a late grow does not snap the window out from
    /// under the animation; it is applied when the slide settles instead.
    private void Refit()
    {
        // The scale of the monitor it will open on, not of the window as it sits
        // now: a hidden window keeps the DPI of wherever it last showed, which is
        // what made the placement wander.
        var scale = TrayAnchor.ScaleFor(_anchor);

        _view.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = new SizeInt32(
            (int)Math.Ceiling(_view.DesiredSize.Width * scale),
            (int)Math.Ceiling(_view.DesiredSize.Height * scale));
        if (size.Width <= 0 || size.Height <= 0) return;

        var at = TrayAnchor.Place(size, _anchor, out _edge);
        _rect = new RectInt32(at.X, at.Y, size.Width, size.Height);
        if (_animating) { _refitPending = true; return; }
        AppWindow.MoveAndResize(_rect);
    }

    // Kept alive as a field: the subclass procedure is called by Windows for the
    // life of the window, and a delegate handed to native code is collected the
    // moment nothing managed references it.
    private SubclassProc? _subclass;

    /// The white rim is the window's non-client frame — outside the client area,
    /// so no content painted over it and no border attribute took it off. Both the
    /// apps that get this right, WPF and Electron, avoid the frame by being truly
    /// transparent windows, which WinUI cannot be. What is left is to remove the
    /// non-client area outright: on WM_NCCALCSIZE the whole window is claimed as
    /// client, so there is no frame band left to paint — the borderless-Win32 move
    /// WPF's WindowChrome makes underneath.
    private void RemoveNonClientFrame()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _subclass = (h, msg, w, l, id, data) =>
        {
            if (msg == WM_NCCALCSIZE && w != 0) return 0;   // client area == whole window
            return DefSubclassProc(h, msg, w, l);
        };
        SetWindowSubclass(hwnd, _subclass, 1, 0);
    }

    /// Theme and backdrop from the current settings, applied on build and again
    /// whenever they change.
    ///
    /// The backdrop is driven through a controller rather than the SystemBackdrop
    /// property so its configuration is ours to set — with IsInputActive pinned
    /// true. Left to the default, WinUI drops the material to its opaque fallback
    /// colour whenever the window is not the active one, which is precisely what
    /// dismiss does: the surface turned an opaque dark grey as it slid down behind
    /// the taskbar, and that solid block was the shadow it left. Held active, it
    /// stays translucent the whole way out. The Solid setting has no controller at
    /// all — the flyout paints its own opaque surface instead of a material.
    private BackdropMaterial? _appliedBackdrop;

    private void ApplyAppearance()
    {
        _view.RequestedTheme = Appearance.ToElementTheme(App.Settings.Theme);

        _backdropConfig ??= new SystemBackdropConfiguration { IsInputActive = true };
        _backdropConfig.Theme = _view.ActualTheme switch
        {
            ElementTheme.Light => SystemBackdropTheme.Light,
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            _ => SystemBackdropTheme.Default,
        };

        // The controller reads its theme from the config object it already holds,
        // so a theme change needs no rebuild — only a change of material does, and
        // rebuilding one that has not changed is wasted work.
        if (_appliedBackdrop != App.Settings.Backdrop)
        {
            _backdrop?.Dispose();
            _backdrop = BuildController(App.Settings.Backdrop);
            if (_backdrop is not null)
            {
                _view.SetOpaqueSurface(false);
                _backdrop.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
                _backdrop.SetSystemBackdropConfiguration(_backdropConfig);
            }
            else
            {
                _view.SetOpaqueSurface(true);
            }
            _appliedBackdrop = App.Settings.Backdrop;
        }
    }

    /// The controller for a material, or null for Solid and for a material the
    /// machine cannot render — in which case the flyout falls back to its own
    /// opaque surface rather than a see-through window.
    private static ISystemBackdropControllerWithTargets? BuildController(BackdropMaterial material) => material switch
    {
        BackdropMaterial.Mica when MicaController.IsSupported() => new MicaController(),
        BackdropMaterial.MicaAlt when MicaController.IsSupported() => new MicaController { Kind = MicaKind.BaseAlt },
        BackdropMaterial.Acrylic when DesktopAcrylicController.IsSupported() => new DesktopAcrylicController(),
        _ => null,
    };

    private void StyleFrame()
    {
        // Only the rounded corner. The light rim a backdrop window otherwise
        // carries is gone another way — RemoveNonClientFrame drops the frame band
        // it lived in — so there is nothing to fight here.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const uint WM_NCCALCSIZE = 0x0083;

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint refData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);
}
