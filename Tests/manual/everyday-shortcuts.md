# Make a Claude for a second account, give it a look of its own, rename it, and get rid of it

Platforms: macOS, Windows
Setup: Graft running with the main Claude signed in. A throwaway account isn't
needed: the new Claude can stay at its sign-in screen.

The goal: make a new Claude called "Everyday" from Graft, choose an icon or
colour for it, open it, and find it again the way you'd look for any app (Mac:
Dock, Applications, Spotlight; Windows: Start, desktop, taskbar). Rename it to
"Everyday Two", check every place it shows up says the new name, then delete it
with its profile once it is closed.
Expect along the way: you can tell this Claude apart from the others at a
glance wherever it appears, and nothing it leaves behind shows up afterwards.

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
