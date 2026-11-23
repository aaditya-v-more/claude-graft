# Restore and report

[Back to HANDOFF.md](../HANDOFF.md)

## Putting everything back

1. Quit the development build from **Quit** in its dropdown.
2. Run `open "/Applications/Claude Graft.app"`.
3. **If step 2 set up the borrowing:** quit Claude 2 from its own menu and wait
   for its process to go. Then, in Graft, select **Claude 2** and remove Claude
   under **Shared usage** with the minus button.
4. Leave Claude 2 open or closed, as step 0 found it.
5. Run `rm -rf /tmp/menu-bar-cpu`. The two scratch sessions stay in Claude 2's
   list.

Shows, within about a minute:

- **Graft:** `check.sh` shows one Graft, under `/Applications`.
- **Lenders:** as step 0 found them.
- **The relay:**
  - If one is listed, `pgrep -fl graft-relay` names `/Applications`.
  - If step 0 had no relay, there is none now.
  - If one is left while nothing holds the lines, run `pkill -f graft-relay`.
- **Claude:** running under step 0's PID.

## Report, then clean up

1. Fill in both tables below and the run notes.
2. Print this file and `cpu.txt` in the terminal, in full, so Aaditya can carry
   them back to the development Mac.
3. Copy the two recordings to `~/Desktop/menu-bar-cpu/` for him.
4. Delete this folder alone, with every `step-…` output, recording and
   `cpu.txt` in it:

       rm -rf handoff/menu-bar-cpu
       git status --short

   `git status --short` lists only `D handoff/menu-bar-cpu/…` lines. Leave the
   deletion uncommitted. The development Mac removes the folder in a commit of
   its own, and the next pull here then matches.
5. Commit and push nothing.

## Results

| Step | What it should show | Seen | Notes |
| ---- | ------------------- | ---- | ----- |
| 0 | Clean checkout; installed Graft 2.0.1; state, macOS and Claude's PID recorded | | |
| 1 | Suite passes; build names 2.0.1 | | |
| 2 | Claude 2 borrows Claude; relay on 47823 (or it already did) | | |
| 3 | First title moves and holds at its start; second stands still | | |
| 4 | Three installed measurements; ten seconds of the bar | | |
| 5 | Development Graft alone, with its own relay; lines still held; when the keychain asked | | |
| 6 | Same speed, hold and gap; text moves whole, leaving nothing behind; item unchanged; colour; spoken title | | |
| 7 | Dropdown and right-click menu while it moves; switching conversations | | |
| 8 | Development measurements: moving about half or less; still close to no text; memory | | |
| Back | Installed Graft alone; sharing and Claudes as step 0 found them | | |

## Measurements

Share of one core over two minutes, from `cpu.txt`.

| Condition | Graft, installed | Graft, development | Relay, installed → development | ControlCenter, installed → development | WindowServer, installed → development |
| --------- | ---------------- | ------------------ | ------------------------------ | -------------------------------------- | ------------------------------------- |
| No text | | | | | |
| Moving | | | | | |
| Still | | | | | |
| Graft's memory while moving | | | | | |

## Run notes
