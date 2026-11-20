# Use Graft in Russian for the everyday tasks

Platforms: macOS, Windows
Setup: Graft running in Russian (Mac: macOS set to Russian, or Graft opened
with `-AppleLanguages '(ru)'`; Windows: started with `GRAFT_LANGUAGE=ru` as in
windows-russian.md), the main Claude and Test A signed in.

The goal: do a short version of a day in Russian: open each Claude, read both
accounts' usage, look through Settings, make a shortcut and delete it, and open
the menu bar or flyout. Put the language back afterwards.
Expect along the way: everything a person reads is Russian and reads naturally
(a count takes the right form of its noun for 1, 3 and 5); no word is
cut, broken mid-word or overflowing, at the window's smallest size too.

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
