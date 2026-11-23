# How chats are shared between profiles

Grafting links or mirrors one profile's chat store into another, and every way that has gone wrong is written up here. Read this before changing `Graft.swift`'s graft, stash, mirror or ungraft paths, or anything that decides where a profile's chats live.

## How a chat store is laid out

Chat history is `<store>/<accountUuid>/<orgUuid>/`, in both
`claude-code-sessions` and `local-agent-mode-sessions`. An instance reads only
the account it is signed into, so linking a whole store does nothing across two
accounts — the link is made one level deeper, mapping this profile's
`<account>/<org>` onto the source's active one.

**Except that a store does not always spell those two in full.** A profile in
local mode was measured naming both halves by their first eight characters —
`local-agent-mode-sessions/ed417e0f/00000000` against
`claude-code-sessions/ed417e0f-5edd-…/00000000-0000-…`, the same account and the
same organization in the same profile, one store each way. Such a profile
carries `<org>.profile-origin.json` saying `{"mode":"local"}`, which is the only
thing that marks it out, and asking a store like that for the full uuid finds
nothing at all. `counterpartDirectory` is the rule: an exact name, or the one
directory there whose name is a prefix of what was asked for — at least eight
characters of it, and only where there is exactly one, since nothing may put one
account's chats into another's folder. Anything ambiguous comes back nil and the
caller falls back to the name it was given.

**`<org>.profile-origin.json` is a plain file sitting beside the organization
folders,** written as the organization is created, which makes it for a moment
the newest thing in the account directory. `newestChild` returned it as an
organization, and the graft that followed stashed the profile's own copy of the
file and built a directory where it had been. Every walk of an account
directory asks `isDirectory`.

**`skills-plugin` sits beside the account directories without being one.** It
is keyed by organization, so a walk that took it for an account reported its
organization folders as chat stores. `nonAccountStoreItems` names it, and both
the sweep's walk and the state report skip it. Grafting links it, on either
path — it used to be linked only across accounts, having been written after the
same-account branch's early exit — and the link is made after the store is
opened, since a same-account graft puts the whole store away and builds it back
and would take the link to the stash with everything else.

## What a new Claude reads

**A new Claude starts with chats of its own.** Borrowing another Claude's
chats was the way to use one account's history from another's usage, and the
usage pool now lends usage without sharing anything, so the default moved to
the choice that touches nothing: `ContentView.add()` makes a shortcut with
`source: .own`, and borrowing is chosen afterwards in its detail, as before.

The move is only there. `Shortcut`'s own default and its decoding fallback
stay `.main`, because a `shortcuts.json` written before `source` existed means
by a missing source exactly that, and a Claude that has been reading Claude's
chats since must go on reading them after an update. Nothing is migrated, no
graft is undone, and a Claude already sharing keeps sharing. The suite holds
all three: the new default, the old one, and `add()` as the only place a new
shortcut is made.

## Grafting, stashing and mirroring

**Never destroy what a profile owns.** Anything a link replaces is moved to a
hidden `.<name>.graft-own` sibling first, and restored when the shortcut goes
back to its own chats. A profile must never be grafted from itself — guarded in
`graft`, `relink` and `install`, because self-grafting stashes away every file
the profile has and leaves links pointing at their own names.

**A stash already sitting there is not proof of a duplicate.** Claude writes
`config.json`, and recreates chat directories, by renaming a temporary over
them, and a rename leaves a real file where the symlink was. The profile then
goes back to writing its own copy while the stash still holds the pre-graft
state. `stash` used to read that as drift and delete it, which threw away every
chat written since the graft; it now folds the two together and keeps the copy
the profile is actually using. `unstash` used to bail whenever something already
sat at the link, which is what left the stash there to arm the next graft.

**An unreadable `config.json` is not an empty one.** A profile that has never
been signed in has no file at all and can share the whole store safely. A file
that will not parse is one caught mid-rename, and treating that as "no account,
so the same account as the source" linked an entire store away. `readableConfigJSON`
tells the two apart; nothing may write that file, or decide where a profile's
chats go, without asking it.

**A mirrored folder holds both histories, and the stash says which was whose.**
Copying is the way round the rule in [session-records.md](session-records.md) —
a real folder is one a profile will write a record into, a link is not — and it works by giving the profile its own
copy of the source's chats and carrying changes both ways. Sharing a history
merges the two rather than standing one in for the other, so the profile's own
chats go into that folder as well and both sidebars end up holding the union.

What keeps that reversible is the stash. On the first pass the profile's own
chats still go to the hidden `.<name>.graft-own` sibling a link would have moved
them to, and `seedOwnChats` copies them straight back into the shared set — the
stash keeping the copy that says which of the merged records this profile
brought, the folder getting the copy the person actually reads. Where its own
copy went depends on how the two are signed in, so `stashedCounterpart` looks
for both shapes: a cross-account graft stashes the organization folder itself,
two profiles on one account stash the whole store above it, and the same records
are then one level further down.

`unmirrorChatStores` is where that record is spent. A record goes only when the
other side holds the same bytes, which after the handover pass describes the
profile's own chats exactly as well as the borrowed ones — so the ones the stash
names are kept rather than removed, or going back would hand this profile's
whole history to the one it borrowed from and keep nothing back. Kept rather
than removed and fetched out of the stash afterwards, too, so that a chat of its
own the profile archived while the graft was up stays archived: restoring the
stashed copy over it would put the conversation back unarchived, which is the
symptom this app gets reported for most.

Seeding runs on the first pass alone, and it is the same clause that stops the
second launch stashing away everything the first one mirrored in. On every
launch it would fetch back every merged chat the person had since deleted,
which is that endless loop pointed the other way.

What is merged into the source stays there. Nothing here takes a chat out of the
profile holding it, so going back leaves those copies where they are, and the
merge is the one thing this app does that it cannot undo. That belongs in the
window rather than in a comment, and `ShortcutDetail.mergeNote` is where it is
said.

Which pass is the first one is asked of `mirrored-chats.json` and never of the
stash. `apply` runs on every launch, so a profile that had nothing to put away
— and therefore left no stash — would look new for ever, and looking new for
ever means the second launch stashes away everything the first one mirrored in
and fetches it all back. `openForMirror` is the rule, `firstPass` the clause.

## Topic guides

- [Handovers and conflict resolution](chat-sharing-guide/handovers-and-conflict-resolution.md)
- [Locking, stashes and re-pairing](chat-sharing-guide/locking-stashes-and-re-pairing.md)
- <a id="unfiled-chats-across-profiles"></a>[Account switches and unfiled chats](chat-sharing-guide/account-switches-and-unfiled-chats.md)
