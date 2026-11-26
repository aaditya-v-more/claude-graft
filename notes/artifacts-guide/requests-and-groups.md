# Requests and groups

[Back to artifacts.md](../artifacts.md)

## The requests

None of this is documented. Every shape here was read off claude.ai's own web
client in November 2026 (the `frame-shell` scripts on `assets-proxy`), and each
request is kept to exactly what that page sends.

- `GET /api/account` names the account and its memberships. Artifacts live
  under the organization whose capabilities include `chat`; an account that is
  also in a team belongs to both, and the first listed is not reliably the
  right one. One address in two teams can chat under each, and each has its
  own artifacts, projects and chats. Taking the first of them showed such a
  person the wrong team with no way to the other: claude.ai sends a sign-in
  straight back, so there is no moment to choose, and a second sign-in is the
  same account and replaces the first. So every chat organization is kept on
  the account (`organizations`), the account's menu offers them where there is
  more than one, and the choice outlives signing in again for as long as the
  account still belongs there (`ArtifactAccounts.organization(chosen:among:)`).
  A change is told to every pane as a sign-in is (`lastSignIn`, `moved`), and
  what was listed under the organization left is dropped at once, not when
  the new list arrives. A record kept before the list was has none and is
  asked again. Whether the membership's `name` is always there has not been
  seen for every kind of organization; one without goes by its place in the
  list.
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
