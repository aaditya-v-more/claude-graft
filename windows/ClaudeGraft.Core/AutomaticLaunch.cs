namespace ClaudeGraft.Core;

/// <summary>
/// Tells a launch nobody asked to see from one a person started. Start hidden
/// is for the first kind only: someone who opens Graft from Start or the desktop
/// and gets no window has to open it a second time to find out it is running.
/// </summary>
public static class AutomaticLaunch
{
    /// Carried by the Startup shortcut, and by the restart after an update Graft installed on its own.
    public const string Flag = "--autostart";

    public static bool Is(IEnumerable<string> arguments) =>
        arguments.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    /// Whether this launch opens the manager window.
    public static bool ShowsManager(bool startHidden, IEnumerable<string> arguments) =>
        !startHidden || !Is(arguments);

    /// Whether a second launch, finding Graft already running, asks it for its window.
    public static bool AsksForWindow(IEnumerable<string> arguments) => !Is(arguments);
}
