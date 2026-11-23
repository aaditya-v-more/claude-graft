# The window, the menu bar, colours and Artifacts, tested on a second Mac

This folder is temporary. It is deleted once its results are read.

`main` adds four things that the suites can't fully check:

- **The window fits.** Graft's window fits its smallest size without
  scrolling, with one-line notes and the long text behind ⓘ buttons. The
  Claude Desktop update line now sits in the sidebar.
- **The menu bar.**
  - Graft's own half-filled circle leaves the menu bar while a Claude in front
    is shown there.
  - A Claude that borrows from other accounts shows every account's icon, with
    the one answering its newest conversation lit.
  - The right-click menu shows its title.
  - The dropdown leads with the conversation in front.
- **Colours.** A new Claude takes a colour nothing else wears. An account lent
  in shared usage gets a colour too, or its Claude's colour if it is one.
- **Artifacts.** A new pane lists and shares the artifacts of every claude.ai
  account signed in inside Graft, including sharing with groups Graft keeps.

The plan builds `main`, swaps the installed Graft for the development build,
checks all four live, and puts everything back. Run it from a terminal
outside any Claude, in the repository folder.

## What the Mac needs

- macOS 15 or later, with Xcode or the Command Line Tools, and `python3`.
- **Claude**: Claude installed, its own profile signed in to one account.
- **Claude 2**: a second Claude made by Graft and signed in to a **different**
  account. Its folder is usually `Claude-2`. If the folders differ, pass both to
  `check.sh`: `./handoff/shared-usage/check.sh Claude <folder>`.
- **Aaditya at the keyboard** for the two claude.ai sign-ins in Part B, and for
  any keychain prompt.
- Graft installed in `/Applications` is optional. Without it, skip the
  put-back steps that reopen it.

## Procedure (read in order)

- <a id="tools"></a><a id="do-not-touch"></a>[Tools and safety](procedure/tools-and-safety.md)
- <a id="part-a-the-window-and-the-menu-bar"></a>[Window and menu bar](procedure/window-and-menu-bar.md)
- <a id="part-b-artifacts"></a>[Artifacts](procedure/artifacts.md)
- <a id="putting-everything-back"></a><a id="report-then-clean-up"></a><a id="results"></a><a id="run-notes"></a>[Restore and report](procedure/restore-and-report.md)
