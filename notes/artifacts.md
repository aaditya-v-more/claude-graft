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
- `POST /api/frame/invite/<uuid>?org=…` with `{"email":…,"role":"reader"}`
  invites one person. claude.ai emails them, as it does from its own panel.

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
cannot use them, so groups are Graft's own, in `artifact-groups.json`: a name
and a list of addresses. Sharing with a group invites each address in turn as
a viewer; one that fails does not stop the rest, and the result says how many
went through. Addresses are read the way people paste them, one per line or
out of a mail header, each once.

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

With Google, claude.ai opens Google in a window of its own, and that window
hands its answer back to the page that opened it. The sign-in window loaded
Google over claude.ai instead, so the answer had nowhere to go: Google said yes
and claude.ai never heard. The provider now gets a real window, built from the
configuration WebKit passes, which is the opener's and so writes into the same
store; when it closes, the window asks again.

A 200 that does not read as an account is written to the diagnostics log as
`artifacts.signin unreadable` with the keys it had, never its body, since that
is the one failure here nothing on screen would show.

## What has been seen working

The request shapes above were exercised read-only from claude.ai in a browser:
the list, the permissions and the invitations answered 200 with the headers,
404 without. The live run on 8 November showed the signed-out pane right (one line, its ⓘ,
Sign In and Groups) and a group made, renamed, saved and deleted. Signing in
did not complete by either route, for the reasons above; the fix has not been
run against a real account yet, and nothing past signing in — the list against
claude.ai, changing who can open an artifact, inviting a group, a second
account, sign-ins surviving a restart — has been seen.
