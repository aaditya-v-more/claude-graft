# Make Graft behave the way you like, and find help when you need it

Platforms: macOS, Windows
Setup: Graft running.

The goal: go through Settings and set Graft up the way you would want it:
starting at login, where it shows, how it updates, what the menu bar or
taskbar strip shows about the Claude in front, and every other switch there.
After each change, check that Graft does what the switch says. Quit and reopen
Graft and check your choices stuck. Then find help, see what's new, start a
problem report and an email to the developer, and find how to donate; cancel
the report and the email before sending. Put every setting back as it was.
Expect along the way: each switch says what it does in words a person
understands, and its effect is visible straight away or says when it will be.

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
