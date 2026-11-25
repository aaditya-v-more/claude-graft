# Windows Settings links, the taskbar strip's switches and the recovered line

Platforms: Windows
Needs: steps 1-5 nothing special; step 6 needs Test A signed in and reading Claude's chats (see test-profiles.md)
Setup: Graft running, Claude open, Test A closed. Note the strip switches as
found. Use only the strip to reach the flyout, and `ClaudeGraft.exe --quit`
to quit Graft.

1. Open Graft's window and choose About from the section picker in Settings.
   Expect: a "Help and feedback" card with Help, What's New, Report a Problem
   and Email, laid out on one line inside the card at the default window size
   and when maximised.
2. Press Help, then What's New, then Report a Problem.
   Expect: the browser opens graft.aadityamore.com, the repository's releases
   page and a new-issue page, in turn. If GitHub asks for sign-in, record the
   requested URL and the redirect; do not sign into another account.
3. Press Email.
   Expect: the mail app (or the "choose an app" box if none is set) opens a
   message to aadityavmore@gmail.com, subject "Claude Graft <version>", whose
   body ends with the Graft version and the Windows version. Close it unsent.
   If Windows only offers a chooser, dismiss it and mark the message contents
   unverified; do not change the default mail app.
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
   turn the strip off and on again so the flyout is reachable.
6. Note Graft's process ID. Open Test A from Graft (Open Anyway if asked);
   in its Code tab send `Reply with the word one.` and note whether its
   session is already in Test A's sidebar. Use File → Exit in Test A.
   About ten seconds later check the same Graft is running and click its
   strip to open the flyout. A restart breaks this check.
   Expect: if the pass filed the session's record, a grey line above Claude
   Code reads "Recovered “<title>”, which had closed without a record" (or
   "Recovered N sessions…"). If no line shows, record whether the session is
   listed in Claude's own sidebar, and save diagnostics.log; a filed record
   is logged as `sweep.file`.
7. Close and reopen the flyout.
   Expect: the line is gone once a pass has filed nothing new.

8. With the usage figure and the conversation title on, switch between two or
   three chats in Claude's sidebar, one with a short title and one long enough
   to run, and note the strip's left edge each time.
   Expect: the strip keeps one width and its left edge whatever is showing: any
   Claude, chat, usage figure, signal, name or number of icons. The title area
   takes whatever room the other parts leave, so a short title sits at the left
   and a long one runs; with many icons and a long name the title area shrinks
   to nothing and the strip still does not widen. Only a change of display
   scale changes its width. The usage figure's slot is two digits wide, growing
   only once the figure reaches 100%.
9. Switch chats again with Windows' Animation effects on (Settings,
   Accessibility, Visual effects).
   Expect: the old title slides up about half the strip's height and fades out
   while the new one slides up into place and fades in, in about a fifth of a
   second. Nothing is drawn above or below the strip's rounded edge, and the old
   and new titles are never both strong. A running title starts from its
   beginning and does not restart its scroll on every frame. Between a long
   title and a short one, or between Claudes, the soft edges of the long one
   fade in or out with the slide rather than switching at once.
10. Make the strip's dot appear (step 4 of
    [the taskbar badges case](windows-taskbar-badges.md)), then change it
    between green and amber.
    Expect: the dot sits as a badge on the lower-right corner of the first
    Claude icon, with a thin ring in the taskbar's own colour round it so it
    reads over the icon; it pops to a little over full size and settles back, and
    when it goes it fades out. The strip does not widen or narrow as it comes and
    goes. With the icon switch off the dot has a small slot of its own at the left.
11. With Animation effects on, change which Claude is in front (a different
    account's window), and let a usage figure change, once rising and once
    falling where a window resets.
    Expect: changing which Claude is in front slides everything that differs
    upward together: the old icon up and out, fading, while the new one slides
    up in, fading in, the usage figure and the title likewise, even where the new figure is
    lower. A mark's red ring pops when it appears and fades when it goes. Within
    one Claude the usage figure slides up as it rises and downward only when the
    same Claude's number went down. A change in the count of icons snaps without
    sliding. When every slide has ended the strip shows exactly the current icon,
    figure and title, with no trace of the old ones.
12. Turn Animation effects off, then repeat steps 9 to 11. Turn it back on.
    Expect: titles, icons, usage figures, the dot and the rings change at once,
    with no slide, pop or fade. The first appearance of the strip and a light or
    dark change never animate.

Restore the three switches as found, and Animation effects too. If Claude had already written its own
record and there was no `sweep.file`, report the positive recovery check as
not exercised; do not remove a real record to manufacture it.

Pass when: each link opens what it names, the two switches each change only
their own part of the strip and are remembered, a recovered session is
reported once in the flyout, the strip keeps one width whatever it shows, and the
title, icon, usage figure, dot and ring animate only while Animation effects is on.
