# What the menu bar costs, measured on the Mac Mini

This folder is temporary. It is deleted once its results are read.

Since 2.0, the conversation on screen runs across Graft's item in the menu bar
whenever there is one. On the development Mac, the installed 2.0.1 then used
about 9% of a core with nothing else asked of it. `main` makes three things
cheaper:

- **The moving text.** Each step used to give the status item a whole new
  title, twelve and a half times a second, and AppKit measured, sized and read
  the item out again every time. The item now keeps one picture of the text and
  is only asked to draw it again, a little further along. Text short enough to
  stand still runs no timer at all.
- **The look at Claude's session records**, every two seconds, dates each
  record with one file-system call instead of reading all its attributes. That
  is eight times cheaper.
- **The process check**, every five seconds, works out where Claude's installer
  lives once per listing instead of once per line.

The suite proves that the picture moves within itself and that a step asks only
for a redraw. It also proves the new date and process checks. It cannot show
that the menu bar puts a redrawn picture on screen, or what Graft spends live.

This plan measures the installed Graft first. Then it builds `main`, swaps the
build in, and checks that the text moves exactly as before. It measures again
under the same conditions and puts the installed Graft back. Run it from a
terminal outside any Claude, in the repository folder.

## What the Mac needs

- macOS 15 or later, with Xcode or the Command Line Tools, and `python3`.
- **Graft 2.0.1** in `/Applications`. Everything is measured against it.
- **Claude**: its own profile, signed in to one account. It stays open
  throughout.
- **Claude 2**: a second Claude made by Graft, signed in to a **different**
  account. Its folder is usually `Claude-2`. If the folders differ, pass both
  to `check.sh`: `./handoff/shared-usage/check.sh Claude <folder>`.
- **Aaditya at the keyboard** for any keychain prompt.
- **Screen Recording** for the terminal, for `screencapture`, and
  **Accessibility**, for the frame command. If either is missing, say so in the
  run notes and carry on with what works.

## Procedure (read in order)

- <a id="tools"></a><a id="do-not-touch"></a>[Tools and safety](procedure/tools-and-safety.md)
- <a id="steps"></a>[Measure the menu bar](procedure/measure-the-menu-bar.md)
- <a id="putting-everything-back"></a><a id="report-then-clean-up"></a><a id="results"></a><a id="measurements"></a><a id="run-notes"></a>[Restore and report](procedure/restore-and-report.md)
