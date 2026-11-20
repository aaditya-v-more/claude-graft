# Get the new version of Graft without losing anything

Platforms: macOS, Windows
Setup: the previous release of Graft installed from its own download and in
use: the main Claude and Test A signed in, a shortcut made, a few settings
changed. The new version staged as release-install.md describes, and this
machine pointed at it as that case's steps 5 (Mac) or 8 (Windows) say.

The goal: notice the update the way a person would, let it install, and carry
on. Then look through Graft for anything that changed that you didn't ask for.
Undo the feed override afterwards.
Expect along the way: it is clear an update is coming or came, Graft is not
gone or broken in between, and afterwards your Claudes, shortcuts, sign-ins and
settings are exactly as they were.

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
