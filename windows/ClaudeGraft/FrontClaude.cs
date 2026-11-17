using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI.Dispatching;

namespace ClaudeGraft;

/// <summary>
/// Which profile's Claude is in front, so the tray can say so and its figure
/// can be kept current while it is being spent.
/// </summary>
public static class FrontClaude
{
    private const uint EventSystemForeground = 0x0003;

    private delegate void WinEventProc(IntPtr hook, uint @event, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private static WinEventProc? _proc;
    private static IntPtr _hook;
    private static DispatcherQueue? _ui;
    private static int _generation;
    private static readonly Dictionary<int, (DateTime Started, string? Profile)> Known = new();

    /// Null while anything other than a Claude is in front.
    public static string? Profile { get; private set; }

    /// Raised on the UI thread whenever <see cref="Profile"/> changes.
    public static event Action? Changed;

    /// Raised on the UI thread with the profile of every Claude somebody brings
    /// forward, which is when its figure matters.
    public static event Action<string>? Activated;

    public static void Start(DispatcherQueue ui)
    {
        _ui = ui;
        _proc = (_, _, hwnd, _, _, _, _) => Arrived(hwnd, switched: true);
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, 0);
        // Whatever is in front as this app starts is a state to show, not a
        // switch: nobody brought it forward, so it asks for no figures.
        Arrived(GetForegroundWindow(), switched: false);
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private static void Arrived(IntPtr hwnd, bool switched)
    {
        GetWindowThreadProcessId(hwnd, out var raw);
        var pid = (int)raw;
        // This app's own flyout in front is a look at the Claude just in use.
        if (pid == 0 || pid == Environment.ProcessId) return;
        var ticket = ++_generation;
        // Nothing here waits on the UI thread: a wait there lets the next foreground
        // event in, and a run of them nested until the window never finished loading.
        // A flick past one Claude to another can land the older lookup last, so only
        // the newest ticket is shown.
        Task.Run(() =>
        {
            if (!IsClaude(pid, out var started))
            {
                _ui?.TryEnqueue(() => Show(ticket, null, switched: false));
                return;
            }
            lock (Known)
                if (Known.TryGetValue(pid, out var known) && known.Started == started)
                {
                    _ui?.TryEnqueue(() => Show(ticket, known.Profile, switched));
                    return;
                }
            string? profile = null;
            try
            {
                var command = ClaudeProcesses.Enumerate().FirstOrDefault(p => p.pid == pid).command;
                if (command is not null) profile = ClaudeProcesses.ProfileOf(command, App.Store.Shortcuts.ToList());
            }
            catch (Exception e) { Diagnostics.Note("front.failed", new Dictionary<string, object?> { ["error"] = e.Message }); }
            lock (Known) Known[pid] = (started, profile);
            _ui?.TryEnqueue(() => Show(ticket, profile, switched));
        });
    }

    private static void Show(int ticket, string? profile, bool switched)
    {
        if (switched && profile is not null) Activated?.Invoke(profile);
        if (ticket != _generation || Profile == profile) return;
        Profile = profile;
        Changed?.Invoke();
    }

    private static bool IsClaude(int pid, out DateTime started)
    {
        started = default;
        if (!ProcessFacts.IsClaude(pid)) return false;
        started = ProcessFacts.StartTime(pid);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
