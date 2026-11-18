# Manual cases

Each file here is one case a person runs on a real machine: what to do, and
what should be true afterwards. They cover what the suites cannot: signed-in
accounts, a real Claude Desktop, the menu bar and tray, installs and updates.

The machines a case runs on have Claude Desktop installed, its own profile
signed in to one account, and a shortcut called **Test A** made in Graft and
signed in to a different account (**Test B** where a third is needed). Only the
machine's owner types an email, password or sign-in code. A step that needs one
is marked **Owner:**; whoever runs the case waits for it and goes on.

Graft writes `diagnostics.log` and `state-report.txt` in its own folder:
`~/Library/Application Support/ClaudeGraft` on a Mac, `%APPDATA%\ClaudeGraft`
on Windows. Save both with the results of any case that fails.
