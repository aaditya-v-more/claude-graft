# Keep working in Claude Code when one account runs low by drawing on another

Platforms: macOS, Windows
Setup: on a Mac, macOS 15 or later. Graft running, the main Claude and Test A signed in to different
accounts, Claude Code available in the main Claude.

The goal: under Shared usage, let the main Claude draw on Test A's account,
start a Claude Code conversation in the main Claude, see which account answers
it and choose another for that conversation, then take Test A away again so
the main Claude is back on its own.
Expect along the way: at every point you can tell which account is answering;
taking the sharing away leaves the main Claude exactly as it was before.

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
