# Manual cases

Each file here is one case a person runs on a real machine: what to do, and
what should be true afterwards. They cover what the suites cannot: signed-in
accounts, a real Claude Desktop, the menu bar and tray, installs and updates.

The machines a case runs on have Claude Desktop installed, its own profile
signed in to one account, and a shortcut called **Test A** made in Graft and
signed in to a different account (**Test B** where a third is needed;
`test-profiles.md` makes them). Sign-ins are by emailed code. Only the owner,
or someone they have trusted with it, supplies a code or a password; a step
that needs one is marked **Owner:**, and whoever runs the case waits for it and
goes on. Anything else a device asks along the way, such as a keychain prompt or
an Accessibility, Screen Recording or other permission or setting, is for
whoever runs the case to answer, never the owner.

Graft writes `diagnostics.log` and `state-report.txt` in its own folder:
`~/Library/Application Support/ClaudeGraft` on a Mac, `%APPDATA%\ClaudeGraft`
on Windows. Save both with the results of any case that fails.

Cases named `everyday-*.md` are different: each is a goal a person has, done
the way a person would, and the tester reports anything that would stop or
irritate one, with a screenshot, whether or not the case asked about it. The
set covers the main journeys on both systems and runs before every release.
