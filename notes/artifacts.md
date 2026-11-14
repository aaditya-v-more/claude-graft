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

The hidden page is dropped and loaded again after a sign-in, since only a page
loaded after it sees it. Dropping it while it was still loading used to leave
the requests waiting on it unanswered, and since the next request queued
behind them never started a page of its own, every request for that account
hung until Graft quit. Now the waiting requests wait for the new page instead,
a page dropped like that cannot report in afterwards and mark the new one
loaded, no page may take more than thirty seconds to load, and a page whose
content process has died is dropped too, where before it still read as loaded
and every request sent to it failed as though claude.ai were down. Each
account's list load has a generation, so one a sign-in has superseded writes
nothing over the one that followed it.

macOS 13 has no store that is both separate and kept, so there each account
gets a non-persistent store and the sign-in lasts until Graft quits. Signing
out removes the store with `WKWebsiteDataStore.remove(forIdentifier:)`.

That call fails while any web view using the store is alive (the SDK header
says so), and the failure used to be dropped with `try?`. Two paths met it
every time: a sign-in handed on before its window let go of its page, and a
sign-out while a request, a load or a search was still running, which kept
the old page alive or built a new one on the id being removed. What was left
on macOS 14 and later was a kept store holding a live claude.ai sign-in, and
a Google one too if Continue with Google had been tried, since its popup
shares the store, with nothing listing it and nothing to remove it. So:

- `retire` closes the account's session first. A closed `ClaudeWebSession`
  fails whatever waits on it as signed out and loads no page again, and
  `session(_:)` hands an id that is no longer listed a closed one rather than
  a new page. The store is removed once `released()` says every request
  already sent has come back.
- Signing out closes that account's sign-in window, and a sign-in window lets
  go of its page and its popups before its sign-in is acted on.
- A removal that fails is written to the diagnostics log as
  `artifacts.store removal failed` and tried twice more.
- At each launch on macOS 14 and later, before any page exists, every store
  identifier not in `artifact-accounts.json` is removed, and only when that
  file was read: one set aside, or never written, may name the accounts those
  stores belong to.

That launch sweep crashed Graft the first time it ran for real, on macOS 27.0.1:
opening Artifacts, Projects or Search Chats, whichever came first, brought
`ArtifactCenter` up, and its first look at the stores took the process down in
`WebsiteDataStore::fetchAllDataStoreIdentifiers` four times out of four. The
suite never saw it, since nothing there makes a real store. A throwaway program
pinned it down: listing or removing stores crashes in a process where no store
has done any work yet, from the main thread or not, through the `async` form or
the completion one, while the same calls after any store had answered a records
lookup went through every time, forty seconds later and after that store was
gone. So `prepareStores` asks a non-persistent store for its memory cache
records before every listing and every removal. That store loads no page,
holds no sign-in and is never shown claude.ai, and the suite fails if a store
is listed or removed anywhere else or without it.

The same account signed in twice keeps its newest store, and its record moves
there, keeping its place in the list; the store it was under before is
removed, so one account is never listed under two names. Keeping the first
store, as this once did, was the wrong half. A second sign-in to a listed
account is made because the first stopped working: Projects and Search say
"Sign in to A again" with Add Account… the button in view, and on macOS 13,
where no store is kept, that is every account after every restart. Add Account
signed in under a new id, found A already listed, removed the new store and
reloaded A from its old, empty one, so A stayed signed out while the status
line said it was already signed in, however often it was tried. A sign-in
cannot be moved from one store to another without reading it, which the rules
forbid, so the record is what moves. Sign In Again… on A finished as some
other account B is the same move: B's record goes to A's store, B's old store
is removed, and A's record goes with the sign-in it had. `ArtifactAccounts.signedIn`
decides all of this, and the suite holds it.

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

The access menu offers invited and public, and checks an entry of its own for
an artifact shared with its organization, which Graft does not offer to set.
It used to check the invited entry for one, so choosing what looked like the
current state sent `users` and took the organization's access away without a
word. Choosing the entry already checked sends nothing, and `setPublic` asks
the permissions first and sends nothing when `read.mode` is already what was
asked for, showing that instead.
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

**Only a 401, or a 403 from `/api/account`, is signed out.** Every 403 used
to be, before any caller saw the status, so a refusal of one thing signed the
account out of all three panes: an account whose `/v1/code/sessions` answers
403 (one without Claude Code, or in an organization that turned it off)
failed every search with "Sign in to X again", and making a link public where
the organization forbids it did the same, while the "refused the change"
messages could never show a 403. Any signed-in account may read its own
account page, so a 403 anywhere else asks that page once
(`ArtifactAPI.needsAccountCheck`): signed out there is signed out, and
otherwise the 403 goes back to the caller as the refusal it was. Which of
these claude.ai actually answers with 403 was not checked. A group share
counts a refused or unanswered address and goes on to the next; only a
sign-out stops it.

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

An unreadable `artifact-groups.json` or `artifact-accounts.json` is not an
empty one, the same mistake `readableConfigJSON` exists for in the chat
mirror. Read as empty, the first edit saved an empty list over every group,
and the first sign-in saved one account over all the others and left their
stores with nobody to remove them. So `ArtifactFiles.read` tells a missing
file from one that will not decode, and the second is moved aside whole, as
`<name>.unreadable-<seconds>.json` beside where it was, before anything is
saved there; if it cannot be moved, nothing is saved over it that session.
Groups are read one at a time, so a group a newer Graft or a damaged write
left unreadable costs that group: the file is set aside with it and the
groups that did read are written back at once.

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

## Windows port

The Windows pane uses the shared `AccountCenter` browser sessions for the same
list, permission and mutation requests. It never reads Desktop cookies or uses
a model token for artifacts. List generations and the account generation both
have to match before a reply is shown; changing a store's signed-in identity
discards its previous list. Sharing groups keep the same viewer fallback and
preserve an unreadable source file before saving recoverable entries.

The file-path check also refuses Windows reserved device names, alternate data
streams and trailing-dot aliases. Page-source exports contain the downloaded
files; Markdown comes from the canonical rendered-page walker. Word exports
are OOXML packages with an embedded rendered HTML part. Before that part is
written, automatic external and local resource references, active elements and
event handlers are removed; safe computed text/table styles and inline data
images remain. Word does not need the temporary browser folder after export.
The Word package structure is covered by fixtures, but opening it in Word and
exporting an actual signed-in artifact have not yet been verified on Windows.
