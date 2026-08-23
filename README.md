# Claude Graft

Claude Desktop signs into one account at a time. Graft makes as many extra
Claude shortcuts as you want — each with its own name, its own Dock icon, and
its own login — and lets you choose, per shortcut, whether it keeps its own
Claude Code history or reads someone else's.

That second part is the point. Every other way of doing this gives you an
isolated second profile with nothing in it. Graft lets a work login open your
personal Claude Code chats, connectors and extensions while the two accounts
stay completely separate.

## Build

```
./build.sh
open "build/Claude Graft.app"
```

Requires the Swift toolchain that ships with Xcode. No project file, no
dependencies — `swiftc` compiles the app and a small launcher binary.

## Using it

Add a shortcut, name it, pick where its chats come from, press **Create
Shortcut**. You get `/Applications/<Name>.app`. Launch it, sign in with the
other account, and both instances run side by side.

Each shortcut is a self-contained bundle holding the launcher binary and a JSON
description of its profile, so shortcuts keep working whether or not Graft is
installed. Every launch re-establishes the links before opening Claude, which
matters because Claude rewrites some config files by replacing them — that
silently detaches a symlink, and the next launch puts it back.

If Claude is already running on that profile, the shortcut skips the sync and
just opens another window, rather than rewriting files a live instance holds
open.

## How it works

A shortcut launches `Claude.app` with `--user-data-dir` pointing at its own
folder under `~/Library/Application Support/`. Everything Claude Desktop keeps
per profile — login, chats, connectors, preferences — lives in that folder.

Grafting then links selected parts of one profile's folder into another's.

### Shared

| | |
|---|---|
| `claude-code-sessions`, `local-agent-mode-sessions` | Claude Code chats and agent sessions, including scheduled tasks |
| `claude_desktop_config.json` | Trusted folders, permission mode, launch-preview settings, UI state |
| `Claude Extensions`, `Claude Extensions Settings`, `extensions-installations.json` | Installed extensions |
| `window-state.json`, `git-worktrees.json` | Window geometry and the worktree list |
| `claude-ssh-remote`, `ssh_configs.json` | SSH remote configuration |
| `userThemeMode`, `locale` | Copied out of `config.json`, not linked — see below |

### Deliberately not shared

**`config.json`** holds the OAuth token cache and account identity next to the
theme and locale. Linking it would merge the two logins, so only those two keys
are copied.

**`Local Storage`, `IndexedDB`, `Session Storage`** are LevelDB stores that take
an exclusive lock per directory. Two running instances cannot share one — the
second to start would fail to open it. Copying them instead is worse than
useless: the copy is a frozen snapshot of the other profile's chat list, showing
stale entries that resolve to nothing when opened.

**`extensions-blocklist.json`** caches one organization's blocklist, fetched from
a URL containing that organization's id. Two accounts sharing it overwrite each
other's copy.

**Cookies and device identity** stay separate, which is what lets the two
shortcuts hold different sessions at once.

### The account/organization problem

Chat history is stored as `<store>/<accountUuid>/<orgUuid>/`, and an instance
only ever reads the account it is signed into. Linking the whole store between
two profiles therefore does nothing when they use different accounts — each side
looks up its own account and finds its own separate history.

So when the accounts differ, Graft links one level deeper: the destination's own
`<account>/<org>` directory is pointed at the source's active one. Both are
resolved at launch from each profile's `config.json`, so the link re-points
itself if either side switches accounts.

### Nothing is destroyed

Before replacing anything with a link, Graft moves the profile's own copy to a
hidden sibling ending in `.graft-own`. Switching a shortcut back to *Its own
chats* removes the links and restores what was there. The hidden name also keeps
a stashed organization folder from being mistaken for a real one.

## Worth knowing

Two instances sharing a chat store write to the same files — don't open the same
chat in both at once.

Claude Code prunes transcripts under `~/.claude/projects/` after 30 days, while
the desktop's session records are permanent. Older chats therefore appear in the
list but open to "Session not found on disk". That is unrelated to grafting and
happens in a normal single-account setup too; raise `cleanupPeriodDays` in
`~/.claude/settings.json` to keep them longer.

`~/.claude` itself — settings, `CLAUDE.md`, skills, plugins, MCP servers and all
session transcripts — is shared by every instance already, since it is resolved
from `$HOME` rather than the profile directory.

Chats that were synced to the server under an account will still appear when you
sign into that account, whatever the local links say. Graft only touches local
storage.

## Layout

```
Sources/Shared/GraftCore.swift   linking, account mapping, launching
Sources/Launcher/main.swift      the executable each shortcut bundle contains
Sources/App/                     the SwiftUI manager
build.sh                         builds both
```
