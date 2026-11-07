# Projects and search across accounts

How the Projects and Search Chats panes reach claude.ai, what they read on this
Mac, and how the requests were found. Read this before changing `Projects.swift`,
`ProjectCenter.swift` or `ProjectsView.swift`. The sign-in they use is the
Artifacts pane's, and [artifacts.md](artifacts.md) says how that works.

## One sign-in, three panes

There is one list of claude.ai accounts, `ArtifactCenter.accounts`, kept in
`artifact-accounts.json`, and one web store per account. Projects and Search
Chats sign in through it and keep nothing of their own, so an account signed in
from any of the three panes is in all three. `ProjectCenter` watches the list
and `lastSignIn`: an account that goes is dropped from every list and result at
once, and one that arrives is asked for its projects (if the Projects pane has
been shown) and searched again (if a search is on screen). What one pane learns
about a sign-in, accepted or refused, goes back through `noteSignIn`, so the
account chips in all three agree.

The `X-Frame-*` headers are the artifact service's alone. `ClaudeWebSession`
adds them only to `/api/frame/` paths. The rest of claude.ai is asked as its
own pages ask, without them.

## The requests

Read off claude.ai's web client in November 2026 by watching its own Projects
page and its search box. None is documented.

- `GET /api/organizations/<org>/projects_v2?include_harmony_projects=true&limit=30&offset=N&order_by=latest_chat`
  answers `{data, pagination: {total, limit, offset, has_more}}`. The Projects
  page itself asks the older `/projects` with the same parameters but no
  offset, which answers a bare array of at most thirty; `projects_v2` answers
  the same rows and pages. An archived project carries `archived_at` and is left
  off, as the page leaves it off. Five pages at most.
- `POST /api/organizations/<org>/conversation/search/v2` with
  `{"query", "n": 25, "target_snippet_size": 100}` answers
  `{data: [{conversation, matched_snippet: {text, matches}, title_matches,
  semantic_rank, …}], next_page_token, executed_mode}`. Match ranges are
  `{start, end}` in UTF-16 units, as the page's script counts. **Archived chats
  come back with the rest**, marked `is_archived`; the box hides them until
  "Show archived" is pressed, and pressing it sends nothing new. A deleted chat
  never comes back, and nothing on this Mac keeps one, so the search cannot
  find it.
- The box's Code tab does not search on the server. It lists every Code session
  with `GET /v1/code/sessions?limit=200[&cursor=…]` and matches titles in the
  page. Without `anthropic-beta: ccr-byoc-2025-07-29`, `anthropic-version` and
  `x-organization-uuid`, that list answers 400. One walk with no `statuses`
  returns active and archived alike. On the development account it was 519
  sessions in three pages, 485 of them archived. The page also asks
  `statuses=archived`, and every session that list held was already in the
  first. A session is listed as `cse_X` and opened at `/code/session_X`.

A search is one `POST` per account, plus the session list, which is kept for
two minutes so a second search in a row does not walk it again. Nothing is
asked as the query is typed, only on Return or the Search button, and changing
a filter asks nothing.

## Where a Code session ran

A Remote Control session's transcript in `~/.claude/projects` carries a
`bridge-session` line whose `bridgeSessionId` is the same `cse_…` the server
lists. That is the whole link between the two, and it is exact:

- **On this Mac:** a transcript here carries its id, and its folder's home or
  drive exists here (`Graft.sessionFolderIsHere`, the same test the sweep uses).
- **On another Mac:** the server lists it as a `bridge` session and either no
  transcript here carries its id, or the one that does names a home this Mac
  does not have, because it was carried over. A session run here whose
  transcript has since been deleted reads as another Mac's. Nothing can tell
  those apart.
- **In the cloud:** `environment_kind` is `anthropic_cloud`.
- **Gone from claude.ai:** a transcript here, owned (`ownerAccountUuid`) by a
  signed-in account, whose ids are none of the ones that account lists. This is
  only claimed when the account's whole list was read. If the walk stopped at
  its page limit or failed partway, a session past the end is unknown, not
  gone. Such a hit has no link and opens its transcript in Finder.

The transcripts are read out of the sweep's `transcript-cache.json` wherever its
entry still matches the file's date and size. A transcript the sweep has not
reached is parsed with the sweep's own `sessionFacts`, and **the cache is never
written from here**. On the development Mac, 330 transcripts with a bridge line
took 2.9 seconds cold, so the read is kept for the same two minutes as the
session lists.

## Filters

Kind, status, where, age, account, project and starred, all behind the one
filter icon beside the Search button, filled while any narrows. Status starts at
Active, as claude.ai's box does. A place, a project or a star belongs to one
kind only, so asking for one leaves the other kind out rather than letting it
through: **No Project** means a chat outside every project, never a Code
session.

## What has been seen working

The request shapes above were read and replayed read-only from claude.ai in a
browser signed in to one account. The transcript reader was run read-only over
the development Mac's own transcripts. Nothing has yet been run inside Graft
against a real account; `handoff/projects-search/HANDOFF.md` is the plan for
that, on a second Mac.
