# The strip and the flyout at shutdown

[Back to interface.md](../interface.md)

The Windows class belongs to the process, not to a particular taskbar strip.
Registering the same name again did not replace its procedure. After switching
the strip off and on, Windows kept sending messages through the first strip's
delegate. Once that delegate was collected the runtime terminated Graft in
`TaskbarStrip.Draw`, inside `SetWindowPos`. The process was already gone when
the test Claude later chose File → Exit; the missing flyout was not a failed
click or a chat-recovery decision.

`TaskbarStripWindow` registers one permanent callback. Messages belong to the
strip for that HWND on its owning thread, from creation to `WM_NCDESTROY`.
A placement tick queued before disposal cannot create a new taskbar child.
The Windows suite uses ordinary hidden parent windows, never Explorer:
Windows rejects a layered child on a message-only parent.

A second failure looked like a clean Quit: the process disappeared promptly.
Windows recorded an access violation at offset `0x7f9640` in
`Microsoft.UI.Xaml.dll` 3.2.3.0 on both 2.1.1 and the 2.2.0 candidate. Its
matching public symbol is `DirectUI::DesktopWindowImpl::put_SystemBackdrop`,
not the strip procedure. The offset alone does not give the caller's stack.

The flyout attaches its own backdrop controller to the XAML window. Releasing
its targets clears their brushes and must happen while the window still exists,
on its dispatcher. Quit also cancels and drains the slide before leaving XAML;
an overlapping hide must not finish an old show against a closing window.
Opening or refreshing the tray must not recreate the strip during that interval.

[The real-window check](../../Tests/manual/windows-strip-lifetime.md) reads the
running process's exit code and the Application log. An `app.quit` with its PID
shows the request arrived, not that the exit was clean.
