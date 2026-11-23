# Restore and report

[Back to HANDOFF.md](../HANDOFF.md)

## Putting everything back

1. Quit the development build from **Quit** in its dropdown.
2. If an installed Graft existed, run `open "/Applications/Claude Graft.app"`.
3. `check.sh`: at most one Graft, under `/Applications`. No relay,
   `lenders: {}`, and no Code environment lines in either profile.
4. Claude is running, and Claude 2 is closed.
5. The scratch artifact's Share panel reads **Only people invited**, with
   nobody else.
6. If a relay is still listed while nothing holds the lines, run
   `pkill -f graft-relay`. Run `rm -rf /tmp/pool-check-second`.

## Report, then clean up

1. Fill in the table below and the run notes.
2. Print both in the terminal, in full, so Aaditya can carry them back to the
   development Mac.
3. Delete the whole testing folder from this checkout, including every
   `step-…` output and picture, and the shared-usage tools it borrowed:

       rm -rf handoff
       git status --short

   `git status --short` should list only `D handoff/…` lines: the deletion is
   left uncommitted. The development Mac removes the folder from the repository
   in a commit of its own, and the next pull here then matches.
4. Commit and push nothing.

## Results

| Step | What it should show | Seen | Notes |
| ---- | ------------------- | ---- | ----- |
| A1 | Suite passes; development Graft alone; update line in sidebar; Artifacts listed | | |
| A2 | Smallest window: no scrolling, one-line notes, ⓘ opens, icon grid | | |
| A3 | Claude in front: no half circle; what showed with Graft in front | | |
| A4 | New draft takes an unused colour; Discard leaves shortcuts.json unchanged | | |
| A5 | Claude 2 borrows Claude; lender row shows Claude's icon | | |
| A6 | Both icons shown; answering one lit; clear of any notch | | |
| A7 | Right-click menu shows its title; light follows the move | | |
| A8 | Dropdown leads with the conversation; others behind a count | | |
| A9 | Unshared, no relay; one icon again | | |
| B0 | Scratch artifact chosen, private at the start | | |
| B1 | Signed-out pane: one line, ⓘ, Sign In | | |
| B2 | Sign-in closes itself; chip and list; file holds no secret | | |
| B3 | List, filters, icons and search agree with claude.ai | | |
| B4 | Link copied and opened | | |
| B5 | Anyone with the link, seen on claude.ai | | |
| B6 | Group made and saved | | |
| B7 | Second account listed | | |
| B8 | Shared with the group, listed once, put back | | |
| B9 | Private again from Graft, seen on claude.ai | | |
| B10 | Sign-ins survive a restart | | |
| B11 | Signing out removes the account | | |
| B12 | Last sign-out; no failures logged | | |
| Back | Installed Graft alone; nothing shared; scratch artifact private | | |

## Run notes
