using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace ClaudeGraft.Tests;

public class TrayLifetimeTests
{
    // A message-only parent keeps these windows off the screen and out of Explorer.
    private static readonly nint MessageOnly = -3;
    private const uint ProbeMessage = 0x8001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hwnd, uint msg, nint w, nint l);

    [Fact(DisplayName = "switching the strip back on delivers messages to the new strip, not the first one")]
    public void SwitchBackOn()
    {
        using var first = new TaskbarStripWindow((_, msg, _, _) => msg == ProbeMessage ? 1 : null);
        Assert.True(first.Attach(MessageOnly));
        Assert.Equal((nint)1, SendMessage(first.Handle, ProbeMessage, 0, 0));
        first.Dispose();
        Assert.Equal((nint)0, first.Handle);

        using var second = new TaskbarStripWindow((_, msg, _, _) => msg == ProbeMessage ? 2 : null);
        Assert.True(second.Attach(MessageOnly));
        // A placement tick already on the queue cannot bring the old window back.
        Assert.False(first.Attach(MessageOnly));
        Assert.Equal((nint)0, first.Handle);
        GC.Collect();
        Assert.Equal((nint)2, SendMessage(second.Handle, ProbeMessage, 0, 0));
        GC.KeepAlive(first);
    }

    [Fact(DisplayName = "losing the parent releases the strip window, and the replacement receives its messages")]
    public void ReplaceParent()
    {
        using var parent = new TaskbarStripWindow((_, _, _, _) => null);
        using var child = new TaskbarStripWindow((_, msg, _, _) => msg == ProbeMessage ? 3 : null);
        Assert.True(parent.Attach(MessageOnly));
        Assert.True(child.Attach(parent.Handle));
        Assert.False(child.Attach(parent.Handle));
        parent.Dispose();
        Assert.Equal((nint)0, child.Handle);
        Assert.True(child.Attach(MessageOnly));
        Assert.Equal((nint)3, SendMessage(child.Handle, ProbeMessage, 0, 0));
    }

    [Fact(DisplayName = "Quit detaches the flyout material and waits for its last move before exiting XAML")]
    public void ReleaseBeforeExit()
    {
        var app = AppSource("App.xaml.cs");
        var shutdown = app[app.IndexOf("private async Task ShutDown()", StringComparison.Ordinal)..];
        InOrder(shutdown, "_quitting = true;", "_strip?.Dispose();", "await _flyout.PrepareForExitAsync();", "Exit();");
        var flyout = AppSource("FlyoutWindow.cs");
        var teardown = flyout[flyout.IndexOf("internal async Task PrepareForExitAsync()", StringComparison.Ordinal)..];
        InOrder(teardown, "Interlocked.Increment(ref _slideId);", "await _slide;", "ReleaseBackdrop();");
        InOrder(teardown, "private void ReleaseBackdrop()", "controller.RemoveAllSystemBackdropTargets();", "controller.Dispose();");
    }

    private static void InOrder(string source, params string[] steps)
    {
        var start = 0;
        foreach (var step in steps)
        {
            var at = source.IndexOf(step, start, StringComparison.Ordinal);
            Assert.True(at >= 0, $"missing or out of order: {step}");
            start = at + step.Length;
        }
    }

    private static string AppSource(string file, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "ClaudeGraft", file));
}
