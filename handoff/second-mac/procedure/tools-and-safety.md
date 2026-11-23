# Tools and safety

[Back to HANDOFF.md](../HANDOFF.md)

## Tools

- `./handoff/shared-usage/check.sh` (read-only): Grafts running, the relay, each
  profile's Code environment and the lenders.
- `python3 handoff/second-mac/status.py [file.json]` (read-only): what the relay
  carried and which account answered. Prints no token.
- **Diagnostics:**
  `grep -E '"(pool\.|relay|menu\.|artifacts\.)' ~/Library/Application\ Support/ClaudeGraft/diagnostics.log | tail`
- **Menu-bar picture:** `screencapture -x -R 0,0,<screen width>,40 handoff/second-mac/<name>.png`.
  If it captures only wallpaper, the terminal lacks Screen Recording; say so and
  use a full-screen `screencapture -x`.
- **Item frame and title, read-only.** Use the development Graft's PID from
  `check.sh`:

      osascript -e 'tell application "System Events" to tell (first process whose unix id is <PID>) to get {position, size, description, title} of every menu bar item of menu bar 2'

- **The notch, if this Mac has one.** Run this once and record the x range it
  prints:

      swift -e 'import AppKit; for s in NSScreen.screens { if let l = s.auxiliaryTopLeftArea, let r = s.auxiliaryTopRightArea { print(l.maxX, r.minX) } }'

- **Graft's files, read-only:** `ls ~/Library/Application\ Support/ClaudeGraft/`,
  with `shortcuts.json`, `artifact-accounts.json` and `artifact-groups.json`
  printed with `cat`.

Save every output and picture in this folder as `step-…`, with the time each
was taken. Git does not ignore them, so commit none of them: the last step
deletes them with the folder.

## Do not touch

- **Chats:** only the one scratch conversation in A6 is created. No other chat
  is opened or written to.
- **Claudes:** Claude's main profile is never quit. Only Claude 2 is opened and
  quit, and it ends closed.
- **Settings:** leave Open at Login, **Update Claude…**, any Graft update and
  the hold setting alone. Don't move, hide or rearrange menu-bar items.
- **Repository:** nothing is committed, pushed or released. Create or switch no
  branch.
- **Credentials:** claude.ai sign-ins, passwords and email codes are typed by
  Aaditya alone, in Graft's sign-in window. A keychain prompt for "Claude Safe
  Storage" is also for Aaditya, with **Allow** (the one-time option). Record
  which step it came at.
- **Sharing:** only the scratch artifact chosen in B0 has its sharing changed,
  and it ends exactly as it started.
