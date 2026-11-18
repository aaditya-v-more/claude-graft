# Artifacts are listed, shared and made private again from Graft

Platforms: macOS, Windows
Needs: the owner, for a claude.ai sign-in by emailed code
Setup: Graft running, no account signed in under Artifacts. One artifact the
owner has chosen as scratch, private ("Only people invited", nobody else with
access); the owner names it before the run.

1. Select Artifacts in Graft's sidebar.
   Expect: one line, "Sign in to see artifacts", an ⓘ, a Sign In… button and
   Groups (0). Nothing scrolls.
2. Press Sign In…. **Owner:** signs in with Claude's account by emailed code.
   Expect: the sign-in window closes by itself. A chip for the account appears
   with a green dot, and the list fills with "N artifacts".
3. Compare the list with claude.ai/artifacts for that account.
   Expect: the same artifacts, newest first. Yours and Shared with You match
   claude.ai's tabs. Each row's lock, people or globe agrees with claude.ai.
4. On the scratch artifact, copy its link, then open it.
   Expect: the clipboard holds its address, and the browser opens on it.
5. Set the scratch artifact to Anyone with the Link.
   Expect: a globe. claude.ai's Share panel for it, reloaded, says Anyone with
   the link.
6. Set it back to Only People Invited.
   Expect: "is private again" and the lock. claude.ai's Share panel says Only
   people invited, with nobody but the owner.
7. Quit Graft and start it again. Select Artifacts.
   Expect: the chip is back, green, and the list loads without a sign-in
   window.
8. Sign out from the chip's menu.
   Expect: "Sign in to see artifacts" again. The diagnostics log has no
   `artifacts.list failed` line.

Pass when: the list matches claude.ai, sharing changes made in Graft show on
claude.ai, the scratch artifact ends private, and the sign-in survives a
restart.
