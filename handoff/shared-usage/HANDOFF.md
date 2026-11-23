# Shared usage, tested on a second Mac

Temporary. This folder is deleted once its results are read; nothing else in the
repository refers to it.

Shared usage is new on `main` and has so far been checked only by the suites on
the development machine. This plan builds `main` on another Mac and checks it
there: the suites first, then the feature live — Graft writing the proxy lines
into a real Claude, a sign-in from the pane, a conversation answered by the
other account, the dropdown moving it, a conversation that carries reasoning
staying where it was, a socket opened through the relay by Remote Control, and
Claude in Chrome working while shared.

It swaps the installed Graft for a development build and puts it back at the
end. Run it from a terminal outside any Claude, since step 6 opens and closes
one.

## What the Mac needs

- macOS 15 or later (sharing is off below that), Xcode or the Command Line
  Tools, and `python3` and `openssl`, which macOS has.
- Claude installed, its own profile (`~/Library/Application Support/Claude`)
  signed in to one account. Call it **Claude**.
- A second Claude made by Graft, signed in to a **different** account. Call it
  **Claude 2**; its folder is usually `Claude-2`. If the folders differ, pass
  both to `check.sh`: `./handoff/shared-usage/check.sh Claude <folder>`.
- Graft installed in `/Applications` is optional. Without it, skip "Putting the
  installed Graft back" except for its last paragraph.
- Both Claudes closed when you start.

`handoff/shared-usage/check.sh` is read-only and prints what every step is
checked against: which Graft is running, the relay, both Code environments
against what Graft left for a launcher, the lenders, any account signed in from
the pane without its token, and the last pool lines in the diagnostics log. Run
it after every step and paste anything surprising into the notes column.

## Procedure (read in order)

- <a id="do-not-touch"></a><a id="steps"></a>[Safety and test procedure](procedure/safety-and-test-procedure.md)
- <a id="putting-the-installed-graft-back"></a><a id="sending-the-results-back"></a><a id="results"></a>[Restore and report](procedure/restore-and-report.md)
