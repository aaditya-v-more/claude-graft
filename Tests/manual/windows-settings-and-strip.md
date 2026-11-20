# Windows Settings links, the taskbar strip's switches and the recovered line

Platforms: Windows
Needs: steps 1-5 nothing special; step 6 needs Test A signed in and reading Claude's chats (see test-profiles.md)
Setup: Graft running, Claude open, Test A closed.

1. Open Graft's window, select Settings and scroll to About.
   Expect: a "Help and feedback" card with Help, What's New, Report a Problem
   and Email, laid out on one line inside the card at the default window size
   and when maximised.
2. Press Help, then What's New, then Report a Problem.
   Expect: the browser opens graft.aadityamore.com, the repository's releases
   page and a new-issue page, in turn.
3. Press Email.
   Expect: the mail app (or the "choose an app" box if none is set) opens a
   message to aadityavmore@gmail.com, subject "Claude Graft <version>", whose
   body ends with the Graft version and the Windows version. Close it unsent.
4. Under "Taskbar and sound", turn on "Show usage on the taskbar". Bring
   Claude forward.
   Expect: the strip beside the notification area shows Claude's icon and its
   figure. "Show the icon of the Claude in front" is on, "Show the name of the
   Claude in front" is off, and both switches are greyed while the strip is
   off.
5. Turn the name on, bring Claude forward; then turn the icon off and bring
   Claude forward again.
   Expect: first icon, "Claude" and the figure; then "Claude" and the figure
   with no icon. Turning both off leaves the figure alone. Restart Graft:
   the switches keep their positions. Put both back (icon on, name off) and
   turn the strip off.
6. Open Test A from Graft (Open Anyway if asked), start a Claude Code session
   in its Code tab, send `Reply with the word one.`, then quit Test A.
   About ten seconds later open the flyout.
   Expect: if the pass filed the session's record, a grey line above Claude
   Code reads "Recovered “<title>”, which had closed without a record" (or
   "Recovered N sessions…"). If no line shows, record whether the session is
   listed in Claude's own sidebar, and save diagnostics.log; a filed record
   is logged as `sweep.file`.
7. Close and reopen the flyout.
   Expect: the line is gone once a pass has filed nothing new.

Pass when: each link opens what it names, the two switches each change only
their own part of the strip and are remembered, and a recovered session is
reported once in the flyout.
