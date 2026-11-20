# Have a second Claude read the main Claude's chats, and open both

Platforms: macOS, Windows
Setup: Graft running, the main Claude and Test A signed in, Test A reading
chats from Claude (test-profiles.md made it so), both closed.

The goal: confirm in Graft which Claude reads whose chats, then open Test A and
find a chat you made in the main Claude. Then open the main Claude while Test A
is still open and decide what to do when Graft asks.
Expect along the way: it is obvious from Graft's window which Claudes share
chats; the question when opening a second one says plainly what could go wrong
and what each button does.

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
