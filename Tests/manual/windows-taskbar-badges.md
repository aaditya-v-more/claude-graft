# Badges on each Claude's taskbar button

Platforms: Windows
Needs: Test A signed in and reading Claude's chats (see test-profiles.md)
Setup: Graft running, Claude and Test A open, each with its own taskbar button.
Note "Badges on Claude's taskbar buttons" (Settings, Taskbar and sound) as
found. Use `ClaudeGraft.exe --quit` to quit Graft.

1. Open Settings and find "Badges on Claude's taskbar buttons".
   Expect: it is on in a profile that never changed it, and it has a short
   description naming red and green.
2. In Test A's Code tab send a prompt that makes Claude stop and ask you a
   question, for example `Ask me which colour I prefer and wait.` Switch to
   Claude's own window before it answers.
   Expect: while it works, Test A's taskbar button has no badge. When it asks,
   a red dot appears at the corner of Test A's button and not on Claude's.
3. Open Test A and answer the question.
   Expect: the red dot goes away once Test A is in front with the answer sent.
4. Send `Reply with the word one.` in Test A and switch to Claude's own window
   before it finishes.
   Expect: a green dot appears on Test A's button when the turn ends, and Claude's
   own button stays bare. Hovering the button's preview reads "Turn finished"
   to a screen reader or in Narrator; the red dot reads "Needs your attention".
5. Click Test A's button so it is in front.
   Expect: the green dot goes away and does not return.
6. Repeat step 4 but stay in Test A while it finishes.
   Expect: no green dot, since the turn was watched finish.
7. Repeat step 4 and, with the green dot showing, use File → Exit in Test A and
   open it again.
   Expect: the new window's button has no dot left over from the old one.
8. Repeat step 4 once with the taskbar in light and once in dark mode (Settings
   → Personalization → Colors, Choose your mode, only the taskbar's own
   setting if it is split).
   Expect: the green is darker on the light taskbar and lighter on the dark one;
   the red is the same. Put the mode back as found.
9. With a green dot showing, turn the badges setting off, then on.
   Expect: the dot goes at once when off, and returns when on if the turn is
   still unwatched.
10. Switch the display scale to 150% (Settings, System, Display) and repeat step
    4. Put it back.
    Expect: the dot stays round and crisp, not blurred or stretched.

Restore the badges setting, the taskbar mode and the display scale as found.

Pass when: red marks only a Claude waiting on you, green only a turn that
finished out of sight, each on that Claude's own button, and both clear when
the Claude is looked at, closed or the setting is turned off.
