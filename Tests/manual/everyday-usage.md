# Keep an eye on each account's usage through a working day

Platforms: macOS, Windows
Setup: Graft running, the main Claude and Test A signed in to different
accounts, one of them used recently.

The goal: find out how much of each account's five-hour and weekly allowance is
left and when each resets, from Graft's window, from the menu bar or flyout,
and (Windows) from the taskbar strip beside the Claude in front. Leave Graft
running for half an hour while you use a Claude, then look again.
Expect along the way: the figures agree wherever they are shown, say how old
they are, update on their own, and an account with nothing to show says why.

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
