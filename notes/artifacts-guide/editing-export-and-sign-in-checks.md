# Editing, export and sign-in checks

[Back to artifacts.md](../artifacts.md)

## Changing and exporting

Each row has a menu with what claude.ai's own menu offers: Rename and Delete
for an artifact the account owns, Duplicate where the list says it may, and
Export. Each action is offered only where the list says claude.ai would allow
it, so nothing is offered to be refused. Delete asks first and says that
claude.ai keeps no copy.

claude.ai exports Word, PDF, Markdown, Google Docs and Notion, and downloads
the page's source. Graft makes the first three and the source on this Mac and
leaves out Google Docs and Notion, each of which is a connection to another
service rather than a file. The files are read as the viewer reads them (the
last two requests above) into a temporary folder, at most 200MB as claude.ai's
own download allows, and only paths that stay inside it. The budget is held to
in the page, file by file, against `Content-Length` and then against the bytes
as they stream in: a file read whole and encoded in the page before it was
measured could take the page's process down. A page of one file
goes out as that file; a page of several goes out zipped, since its page alone
would open with pieces missing.

Markdown, PDF and Word are made from the page as drawn, not its source,
because an artifact that builds itself with script has almost nothing in its
source to export. `ArtifactRenderer` loads the folder into a web view with a
non-persistent store, holding no sign-in, never shown, and following no link
out of the folder. Markdown walks that page the way claude.ai's own export
walks its source, leaving out scripts, styles, drawings, controls and what is
hidden; PDF is the page at Letter width as one tall page; Word is the drawn
page with scripts, frames and anything from the web taken out, since Word's
importer would fetch them while everything waited. The layout suite renders a
sample page through all three, offline.

## When it asks

When the pane is shown and when its refresh button is pressed, never on a
timer: a pass is several requests per account to a service that did not ask
for them.

## Knowing the sign-in is through

claude.ai never says a sign-in is done; the window asks `/api/account` until it
answers. It used to ask only when a page finished loading, and a live run on 8
November signed in both ways claude.ai offers without Graft noticing either.
With an emailed code, claude.ai finishes by moving to its chat page inside the
page already loaded, which ends no navigation, so the last question had been
asked on the sign-in page and was answered 401. The person was left looking at
their chats in Graft's window, and Graft still said "Sign in". So the window
watches the web view's address, which every way of signing in changes.

**Signing in with an emailed code is the one way Graft supports.** Both
sign-ins of the second live run on 8 November went that way, the first of them
written down at the time as Google, and it was settled after: Google support is
not wanted. So every Sign In button has one line under it saying to choose
Continue with email and type the code, the sign-in window says the same above
claude.ai's page, and the ⓘ says it is the one way supported.

The window still gives a provider's sign-in a window of its own, because
loading it over claude.ai left its answer nowhere to go: Google said yes and
claude.ai never heard. That costs nothing and is not offered as a way in; it
has never been seen to finish.

A 200 that does not read as an account is written to the diagnostics log as
`artifacts.signin unreadable` with the keys it had, never its body, since that
is the one failure here nothing on screen would show.

## What has been seen working

The request shapes were first exercised read-only from claude.ai in a browser:
the list, the permissions and the invitations answered 200 with the headers,
404 without. The first live run on 8 November showed the signed-out pane right
and a group made, renamed, saved and deleted, and no sign-in finishing.

The second run that day, with the sign-in fix, finished both sign-ins by
emailed code, each window closing by itself. Seen working against claude.ai:
the list of both accounts beside claude.ai's own, the filters and search,
copying a link, one artifact made public and private again, a group invite
arriving as a viewer, a second account, both sign-ins surviving a restart, and
signing out. It found two things the suites had passed: the chips' dots were
not drawn, since a borderless menu draws its label as a native title and drops
any shape beside it, and the groups editor's minus button worked but was
missing from what assistive apps were given. The dot now stands outside the
menu, and both buttons carry their names in their own labels; the layout suite
reads the editor from outside to hold that.

Not yet seen against claude.ai: commenter and editor invitations, a role
lowered for an artifact that does not take it, renaming, duplicating,
deleting, and the exports of a real artifact.
