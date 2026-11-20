# Projects and Search Chats, tested on a second Mac

This folder is temporary. It is deleted once its results are read.

`main` adds two panes to Graft's window, both reading claude.ai through the
same sign-in the Artifacts pane uses:

- **Projects** lists the projects of every signed-in account, starred first,
  then the most recently changed. It only lists. Opening a project goes to
  claude.ai in the browser.
- **Search Chats** searches every signed-in account's chats with claude.ai's
  own search, and their Code sessions by title and folder. One filter icon
  beside the Search button narrows the results by kind, status, where a Code
  session ran, age, account, project and star.
- **One sign-in for all three panes.** An account signed in or out from any
  pane is signed in or out in the other two at once, without a refresh.

The suites check the request shapes, the filters and the layout against sample
rows. This plan checks them against real accounts. It builds `main`, swaps the
installed Graft for the development build, and puts it back at the end. Run it
from a terminal outside any Claude, in the repository folder.

## What the Mac needs

- macOS 14 or later, with Xcode or the Command Line Tools.
- Claude installed, its own profile signed in to one account. Claude 2, if
  this Mac has one, is signed in to a **different** account.
- **Aaditya at the keyboard** for the claude.ai sign-ins in steps 3 and 4 and
  the sign-in again in step 10.
- Code sessions on both accounts, for the filters in step 8: at least one run
  on this Mac by Remote Control, and at least one run on the development Mac.
  A cloud session or an archived one is useful if either account has one, but
  is not needed.
- A word that appears in chats of both accounts. Note it in the run notes.
- Graft installed in `/Applications` is optional. Without it, skip the
  put-back step that reopens it.

## Procedure (read in order)

- <a id="tools"></a><a id="do-not-touch"></a>[Tools and safety](procedure/tools-and-safety.md)
- <a id="steps"></a>[Run the search checks](procedure/run-the-search-checks.md)
- <a id="putting-everything-back"></a><a id="report-then-clean-up"></a><a id="results"></a><a id="run-notes"></a>[Restore and report](procedure/restore-and-report.md)
