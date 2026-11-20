# Start the day: open each Claude, switch between them, and start a session

Platforms: macOS, Windows
Setup: Graft running, the main Claude and Test A signed in to different
accounts, both closed.

The goal: open the main Claude and Test A each from Graft's window and from the
menu bar (Mac) or flyout (Windows), switch between them the way you'd switch
apps, open one that is already open, and press Start Session on one of them.
Close one and open it from its own shortcut instead of Graft.
Expect along the way: each opens on its own account in its own window, with its
own name and icon wherever the system shows them; opening one twice brings it
forward rather than starting another; Start Session says what it did.

Do it the way a person would, with the mouse and keyboard, without knowing the code.
Along the way, check and report anything that fails these:
- Every screen, panel, popover and search has an obvious way back (Esc, ×, Back, clicking out).
- The window moves by dragging its title/top bar, resizes, and nothing spills outside it.
- Lists and popovers stay inside the window when scrolled, long or short; menus open next
  to what opened them.
- Alignment, font sizes and spacing match their neighbours; nothing clipped or overlapping;
  scroll bars stay inside their area; light and dark both look right.
- Controls respond at once; nothing freezes, flickers or jumps.
- Text is clear; errors say what to do.

On wintest2, open the flyout from the taskbar strip, never from the notification
area, and quit Graft with `ClaudeGraft.exe --quit`, never by ending it. A keychain
prompt or permission on the way is yours to answer (Allow, never Always Allow).

Report every annoyance with a screenshot, even outside the goal, ranked by how much a
person would mind. "Works as written" is not a pass if a person would be stuck or
irritated.

Pass when: the goal was reached with nothing on that list failing.
