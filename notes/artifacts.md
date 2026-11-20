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

## Topic guides

- <a id="the-requests"></a><a id="groups"></a>[Requests and groups](artifacts-guide/requests-and-groups.md)
- <a id="changing-and-exporting"></a><a id="when-it-asks"></a><a id="knowing-the-sign-in-is-through"></a><a id="what-has-been-seen-working"></a>[Editing, export and sign-in checks](artifacts-guide/editing-export-and-sign-in-checks.md)
