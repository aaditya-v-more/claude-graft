# Test A and Test B open on their own accounts after signing in through Graft

Platforms: Windows
Needs: the owner, for the Test A and Test B sign-ins
Setup: Graft installed by its setup, "Choose Graft for Claude links" on. Test A
and Test B made through Graft (folders `Claude-Test-A`, `Claude-Test-B`). The
main Claude signed in.

1. Open Test A from Graft. In it choose Continue with Google. **Owner:** signs
   in; when the browser asks to open Claude Graft, choose Open.
   Expect: Graft comes forward asking "Which Claude did you start this sign-in
   from?" listing every running Claude. Choose Test A and Continue Sign-In.
   Test A is then signed in. Graft's diagnostics log has `link.signInAsked`
   then `link.signInSent` naming `Claude-Test-A`, and no link text.
2. Repeat step 1 for Test B with its own account.
   Expect: the same, naming `Claude-Test-B`.
3. Quit both. Open Test A from Graft, then Test B.
   Expect: each opens its own window on its own account (its account menu
   shows a different email from the main Claude's). Each has its own taskbar
   button, name and icon. Opening Test A again while it is open brings its
   window forward instead of starting another.
4. Select each in Graft's window.
   Expect: each shows its own account's usage, different from the main
   Claude's.

Pass when: both test Claudes are signed in to their own accounts through the
chooser, and each opens and is shown as itself.
