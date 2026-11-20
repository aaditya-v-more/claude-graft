# A new Claude never lands on another Claude's folder or login

Platforms: macOS, Windows
Needs: Test A signed in (see test-profiles.md)
Setup: Graft running. Test A exists, created, signed in, its folder
`Claude-Test-A`, and closed. No shortcut is called "Test A renamed" or uses a
folder starting `Claude-Test-A-`.

1. Select Test A. Rename it to `Test A renamed` and save (Mac: Update
   Shortcut; Windows: Save).
   Expect: the name changes; its folder stays `Claude-Test-A`.
2. Press New Shortcut and type the name `Test A`.
   Expect: the folder box shows `Claude-Test-A-2` whole, never `Claude-Test-A`.
   The draft shows no usage figures.
3. Clear the name field completely, one backspace at a time, watching the
   folder box. Then type `Test A` again.
   Expect: at no point does the folder box show a folder another Claude in the
   list uses. With the name empty it shows `Claude-Profile` only if no Claude
   uses that, otherwise a numbered one.
4. Type `Claude-Test-A` into the folder box by hand and press Create (Mac) or
   Save (Windows).
   Expect: refused, with a message naming "Test A renamed" and the folder
   `Claude-Test-A`. Nothing is created.
5. Windows only: set the folder back to `Claude-Test-A-2`, change the name to
   `test a renamed` and save.
   Expect: refused, saying another Claude is already called "Test A renamed".
6. Set the name to `Test A` and the folder to `Claude-Test-A-2`, then create
   it and open it.
   Expect: the new Claude opens on its sign-in screen, not signed in to Test
   A's account. Quit it without signing in.
7. Put things back: delete the new "Test A" with its data, then rename "Test A
   renamed" back to `Test A`.
   Expect: Test A still opens signed in to its own account, and its folder is
   `Claude-Test-A`.

Pass when: no new Claude is offered, given or allowed another Claude's folder,
and the one created opens signed out rather than on Test A's login.
