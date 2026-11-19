# Open Graft for the first time, find your way around every screen, and come back to it later

Platforms: macOS, Windows
Setup: best on the clean machine of release-install.md, right after Graft is
installed there, so the first launch is a real one; otherwise on a machine
where Graft is already set up, skipping the first-launch look. The main Claude
signed in. Light appearance first.

The goal: get your bearings. Open Graft's window, read what it tells you, then
visit every place in it: each Claude in the sidebar, Shared usage, Artifacts,
Projects, Search Chats, Settings, Donate and Source (Mac) or Help and feedback
(Windows). Get out of each the way you'd expect to. Move the window, make it as
small as it goes and as large as the screen, switch to Dark and back, then quit
Graft and open it again from where a person would (Mac: Applications or
Spotlight; Windows: Start).
Expect along the way: it is clear what Graft is for and what to do first, and
it comes back where you left it.

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
