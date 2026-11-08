# Artifacts across accounts

How the Artifacts pane reaches claude.ai, what it sends there, and how the
requests were found. Read this before changing `Artifacts.swift`,
`ArtifactCenter.swift` or `ArtifactsView.swift`.

## Why a second sign-in

Artifacts live behind claude.ai's web sign-in. The login Graft borrows from a
Claude profile for usage is a Claude Code OAuth token scoped to the model and
to usage figures, and the artifact service does not take it. The two ways left
were to decrypt each Claude profile's claude.ai cookie, or to sign in again.
Decrypting the cookie is taking a credential, not borrowing one, and would put
a keychain prompt in front of every profile; it was ruled out. So each account
signs in once more, on claude.ai's own page, in a window of Graft's.

The same sign-in serves the Projects and Search Chats panes; see
[projects-and-search.md](projects-and-search.md). An account signed in or out
from any of the three is signed in or out in all of them.

## Where the sign-in lives, and where it goes

Each account has a `WKWebsiteDataStore(forIdentifier:)` of its own, named by
the UUID kept in `artifact-accounts.json`. The sign-in window writes into it,
and nothing else is ever loaded into it but claude.ai. Requests are not built
by Graft: a hidden web view loads `https://claude.ai/api/account` into the
account's store, and every request is a `fetch` run in that page with
`callAsyncJavaScript`, `credentials: "same-origin"`. The cookie therefore goes
only where the browser engine sends it, to claude.ai, and Graft never holds
it. The suite fails if `httpCookieStore` or `HTTPCookieStorage` appears in the
app, or if the center starts using `URLSession`.

macOS 13 has no store that is both separate and kept, so there each account
gets a non-persistent store and the sign-in lasts until Graft quits. Signing
out removes the store with `WKWebsiteDataStore.remove(forIdentifier:)`.

The same account signed in twice keeps its first store; the second is removed
at once, so one account is never listed under two names.

## The requests

None of this is documented. Every shape here was read off claude.ai's own web
client in November 2026 (the `frame-shell` scripts on `assets-proxy`), and each
request is kept to exactly what that page sends.

- `GET /api/account` names the account and its memberships. Artifacts live
  under the organization whose capabilities include `chat`; an account that is
  also in a team belongs to both, and the first listed is not reliably the
  right one.
- `GET /api/frame/artifacts?rel=mine|shared&empty=exclude&limit=48&org=…&sort=last_viewed`
  lists one side, with `next_page_token` for the next page, followed as
  `page_token`. Five pages at most per side.
- `GET /api/frame/perm/<uuid>?org=…` says who can open one: `read.mode` is
  `owner`, `users` (with `read.users`), `org`, `space` or `public`, and
  `version` goes back as `If-Match` on a change.
- `PATCH /api/frame/perm/<uuid>?org=…` with `{"read":{"mode":"public"}}` opens
  it to anyone with the link. Closing it sends `users` with whoever was invited,
  or `owner` when nobody was, which is what the share panel does.
- `POST /api/frame/invite/<uuid>?org=…` with `{"email":…,"role":…}`
  invites one person. claude.ai emails them, as it does from its own panel.
  The share panel maps its levels as view → `reader`, comment → `commenter`
  and edit → `editor`, and offers comment and edit only where the permissions
  answer says `commentersEnabled` and `editorsEnabled` (`writersEnabled` in
  older answers). A flag that is missing offers nothing.
- `POST /api/frame/retitle/<uuid>?org=…` with `{"title":…}` renames it. A
  `400` with reason `title_invalid` is a title claude.ai will not take.
- `POST /api/frame/duplicate/<uuid>?org=…`, no body, copies it into the
  account asking and answers with the copy's `slug`. The list says whether it
  may (`duplicable`) and whether it may today (`duplicate_available`, a daily
  cap); a refusal carries a `reason`, `org_mismatch` among them.
- `DELETE /api/frame/<uuid>?org=…` deletes it, `204`. A `404` with reason
  `not_found` is one already gone. claude.ai keeps no bin for artifacts.
- `GET /api/frame/<uuid>?org=…` is what the viewer asks first. It names the
  version (`ver`), the files (`files[].path`) and `assetToken`, a token of a
  few minutes that the file host takes in the address:
  `https://<uuid>.frame.claudeusercontent.com/_f/<ver>/_src/<path>?__frame_t=<token>`.
  The viewer reads files from there with `credentials: "omit"`; so does
  Graft, from the same claude.ai page, so no cookie reaches the file host.

`renamable` comes with each row of the list too. Pinning (`/api/frame/favorite`)
was left out: the list does not say what is pinned, so Graft could only
offer it blind.

**Every `/api/frame` request carries `X-Frame-CP: go`** and
`X-Frame-Platform: web`, and no other request does: `ClaudeWebSession` adds
them by path, since the same page now asks the rest of claude.ai too. Without the first, the service answers
`404 not_found_error` to a request otherwise identical to the page's, which
reads exactly like an artifact that does not exist; that is how the first
attempt from page script was misread.

The artifact's UUID keys every request. The page address uses a shorter id of
its own, so `open_url` is kept as the list gives it.

## Groups

claude.ai has groups only for team organizations, and a personal account
cannot use them, so groups are Graft's own, in `artifact-groups.json`: a name,
a list of addresses, and what each person may do. Sharing with a group invites
each address in turn with its role; one that fails does not stop the rest, and
the result says how many went through. Addresses are read the way people paste
them, one per line or out of a mail header, each once, and join with the role
everyone in the group already has, so adding to a group of editors adds
editors.

Roles came after the first groups were saved, and those were shared as
viewers. So `roles` holds only the people who are not viewers, an address
missing from it is a viewer, and a group of viewers is written byte for byte
as before. That keeps both directions safe: an old file reads as it always
did, and a Graft from before roles, reading a new file, ignores `roles` and
invites everyone as a viewer, which is less than asked and never more. A role
a newer Graft wrote that this one does not know costs that person their role,
not the group.

Before inviting, the share asks the artifact's permissions. A person whose
role the artifact does not take gets the most it does take, never more: an
editor becomes a commenter where comments are on, a viewer otherwise. The
result says how many were given less than the group asked.

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
own download allows, and only paths that stay inside it. A page of one file
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
