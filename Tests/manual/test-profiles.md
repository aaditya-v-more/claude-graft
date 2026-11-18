# Test A and Test B are made in Graft and open signed in to their own accounts

Platforms: macOS, Windows
Needs: sign-ins to two accounts other than the main Claude's, by emailed code
Setup: Graft running, the main Claude signed in. On Windows, Test A and Test B
may already exist (folders `Claude-Test-A`, `Claude-Test-B`); skip step 1 for
any that does.

1. In Graft's window press New Shortcut. Name it `Test A`, leave the folder as
   the name gives it, set Reads chats from to Claude, and create it. Then do
   the same for `Test B` with Its own chats.
   Expect: each appears in the sidebar with a colour no other Claude wears.
   The folders are `Claude-Test-A` and `Claude-Test-B`. Neither shows the
   main Claude's usage.
2. Open Test A from Graft. In its sign-in screen choose email, enter Test A's
   account and the code mailed to it. **Owner:** supplies the code.
   Expect: Test A is signed in to that account, and its account menu shows an
   email different from the main Claude's.
3. Repeat step 2 for Test B with its own account.
   Expect: the same, with a third email.
4. Quit both. Open Test A from Graft, then Test B.
   Expect: each opens its own window on its own account. On Windows each has
   its own taskbar button, name and icon. Opening Test A again while it is open
   brings its window forward instead of starting another.
5. Select each in Graft's window.
   Expect: each shows its own account's usage, different from the main
   Claude's.
6. Windows only, if a sign-in went through Google or another provider instead
   of email: when the browser asks to open Claude Graft, choose Open.
   Expect: Graft asks "Which Claude did you start this sign-in from?" listing
   every running Claude; the one chosen finishes signing in. The diagnostics
   log has `link.signInAsked` then `link.signInSent` naming that Claude's
   folder, and no link text.

Pass when: both test Claudes are signed in to their own accounts, and each
opens and is shown as itself.
