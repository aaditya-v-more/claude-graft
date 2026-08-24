# Claude Graft

Claude Desktop signs into one account at a time. Graft makes as many extra
Claude shortcuts as you want — each with its own name, its own Dock icon, and
its own login — and lets you choose, per shortcut, whether it keeps its own
Claude Code history or reads someone else's.

That second part is the point. Every other way of doing this gives you an
isolated second profile with nothing in it. Graft lets a work login open your
personal Claude Code chats, connectors and extensions while the two accounts
stay completely separate.

## Install

```
./release.sh --install
```

Runs the tests, builds for both architectures, draws the icon, signs, packages
`dist/ClaudeGraft-<version>.zip` and puts the app in `/Applications`. It signs
with a Developer ID if `security find-identity` finds one — set
`GRAFT_SIGNING_IDENTITY` to choose a particular one — and falls back to an
ad-hoc signature, which runs on the machine that built it but needs right-click
→ Open anywhere else. The script prints the `notarytool` invocation for
distributing it properly.

For development, `./build.sh` alone builds into `build/`, for this machine only
and in one compile; `GRAFT_UNIVERSAL=1` gets you both slices. Requires the Swift
toolchain that ships with Xcode; no project file.

The version lives in one file, `VERSION`, and the build stamps it into the
bundle. Bump it, run `./release.sh`, publish what it names — releasing the same
number twice leaves two different binaries answering to one version, so the
script refuses when the tag already exists.

## Using it

Add a shortcut, name it, pick where its chats come from, press **Create
Shortcut**. You get `/Applications/<Name>.app`. Launch it, sign in with the
other account, and both instances run side by side.

A shortcut is a draft until you create it: it sits in the list marked *Not
created yet*, and discarding one at that stage just removes the row.

The profile folder is editable. Point it at a folder that already exists and the
shortcut adopts that profile — useful if you set one up by hand and would rather
not sign in again.

Each shortcut is a self-contained bundle holding the launcher binary and a JSON
description of its profile, so shortcuts keep working whether or not Graft is
installed. Every launch re-establishes the links before opening Claude, which
matters because Claude rewrites some config files by replacing them — that
silently detaches a symlink, and the next launch puts it back.

If Claude is already running on that profile, the shortcut skips the sync and
just opens another window, rather than rewriting files a live instance holds
open.

## Menu bar

Graft lives in the menu bar, on by default. The bar shows the five-hour figure
for whichever Claude is open — an idle account sitting at its limit should not
shout over the one being used — falling back to the tightest window when
nothing is running, and naming the account it is reporting in its tooltip. The
dropdown breaks it down per account,
five hours and week, each with how long until it resets — `2d 3h 40m`, or
`3h 40m` once there is less than a day. A dot marks whichever instances are
running, and each has its own **Open**.

Claude's own installation appears in both the dropdown and the app's sidebar,
alongside the shortcuts and read-only: Graft borrows from it but never changes
it. It is recognised by having no `--user-data-dir` at all, which is how Claude
launches when opened the ordinary way; the shortcuts are matched on their exact
profile path, anchored, since one profile's folder sits inside another's.

Closing the window does not quit — the menu bar item carries on reporting, and
the Dock icon steps aside. Neither does ⌘Q, which puts the window away and
leaves the item where it is; **Quit** in the dropdown is the way out, and the
only one, unless the item is switched off or a logout is under way. The window
comes back the way you left it: close it before quitting and the next launch
stays in the menu bar, which is what makes **Open at Login** bearable.

The figures come from Anthropic's own endpoint, `GET /api/oauth/usage` — the
one Claude Code reads — asked once per profile every five minutes. That gives
current numbers even for a profile that is not running, exact reset times, and
the plan name.

Reaching it needs that profile's login, which Claude Desktop keeps encrypted in
its own folder. Graft borrows it read-only, and macOS gates the borrowing: the
first read asks permission for the `Claude Safe Storage` keychain item, and
choosing Always Allow makes later reads silent. macOS grants that permission to
one exact build, so a new version has to ask again — which it does, once, rather
than quietly showing you worse numbers. It asks nothing while it is starting up
into the menu bar at login, and nothing again once you have said no; **Refresh
Usage** in the dropdown is the way back. Only the
access token is decrypted — never the refresh token, because Anthropic rotates
those and using one would sign Claude Desktop out — nothing is written back to
Claude's config or keychain, and the token goes to `api.anthropic.com` and
nowhere else.

### How often it asks

Once per profile every five minutes, and no more. The thirty-second timer only
reads the local file and checks whether that Claude is running; the network call
sits behind its own gate. Two profiles is twenty-four calls an hour against the
endpoint Claude Code itself polls.

A failed call is not simply retried on the next tick — that would turn one
refusal into a hundred and twenty attempts an hour, which is how a client earns
a rate limit. Failures back off one minute, two, five, fifteen, then half an
hour, and a success clears the count. When the service sends `Retry-After` that
wins, and it is the one wait pressing **Refresh Usage** cannot skip.

Each pass writes what it found to
`~/Library/Application Support/ClaudeGraft/usage-status.json` — the figures and
whether they came from the API — so the state of live usage can be checked
without reading it off the screen. Figures only; no token goes in it.

Decline the prompt and the app still works: it falls back to
`plan-usage-history.json`, which each profile writes while it runs. That file
carries no reset times, so those are worked out from the history — a window
closing shows up as the figure dropping to nothing, which dates the window that
followed, and weekly resets are a cycle so the last one seen is rolled forward.
Figures from a profile that has not run for hours are greyed out, since a
five-hour window that has already rolled over says nothing useful.

### Start Session

Every account has its own **Start Session**, which opens that account's
five-hour window by sending it one short message on Haiku. Nothing appears on
screen — no window is launched and nothing is typed.

It goes through the same borrowed login as the usage figures, so it really is
per account: the request is made as that profile, with `max_tokens` set as low
as the API allows, and the reply is discarded. The only thing it costs is the
handful of tokens needed to make the window start counting.

It happens because the button was pressed and for no other reason. Nothing on
the launch path can reach it, nothing on a timer can, and the suite reads the
app's own source to check that no fourth caller has appeared — a session wired
into a view refresh would start a window on every account every thirty seconds.
The same account cannot have two starts in flight at once either, since the
window and the dropdown each carry their own button for it.

## Deleting

Deleting a shortcut asks whether the profile folder should go with it. Keeping
it is the default reading of the dialog; deleting it destroys that account's
login and chat history, so it is guarded: Claude's own profile can never be
removed, nor anything outside `~/Library/Application Support`, nor a profile
that is currently running or that another shortcut still points at.

Graft also refuses to touch any application it did not create. Bundles it owns
are identified by the description file they carry, never by name, and the names
Claude uses are reserved.

## Tests

```
./test.sh
```

A hundred and sixty-nine checks, all against a throwaway Application Support
and Applications directory, so nothing they do can reach a real profile or a
real app.

They cover: that applications Graft did not create survive install, uninstall
and delete; that updating a shortcut rewrites one bundle rather than leaving
copies behind, and that a rename onto an occupied name changes nothing; that
grafting preserves the login, stashes whatever it replaces, and reverses
cleanly; that a profile cannot be grafted from itself and a folder name cannot
climb out of Application Support; that a name with an ampersand in it still
produces a plist macOS can read; that every guard on profile deletion fires for
the reason it was meant to; that both usage sources parse, including the two
reset-time formats and a history with figures missing; that a refused call
backs off instead of retrying on the next tick, and that `Retry-After` is the
one wait a person cannot skip; that one profile's path being a prefix of
another's no longer makes every profile look like it is running; and that the
figure in the menu bar follows whichever Claude is open.

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

Claude does not always leave the links alone. It writes its settings file, and
recreates chat directories, by renaming a temporary over them, and a rename puts
an ordinary file back where the link was — after which the profile carries on
writing its own copy while the stash still holds what it had before the graft.
When the next launch finds both, it folds them into one: chats from either side
are kept, and where the same file exists twice the one the profile has been
using wins. Nothing is deleted for having a stash beside it.

## Worth knowing

Two instances sharing a chat store write to the same files, so opening the same
conversation in both at once can lose messages. Opening a shortcut from Graft
checks first and warns when something else is already on those chats, with the
choice to go ahead anyway. Launching the shortcut from the Dock does not check.

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

## Working on it

`CLAUDE.md` carries the notes that are not obvious from the code: what Claude
Desktop keeps where, the rules about borrowed credentials, and the several
things that had to go wrong before this app worked. Read it before changing
anything that touches profiles or the menu bar.

## Layout

```
Sources/Shared/GraftCore.swift     linking, account mapping, launching
Sources/Launcher/main.swift        the executable each shortcut bundle contains
Sources/App/ClaudeGraftApp.swift   scenes, app lifecycle, Dock behaviour
Sources/App/ContentView.swift      sidebar and deletion
Sources/App/ShortcutDetail.swift   one shortcut's settings
Sources/App/MainProfileDetail.swift  Claude's own profile, read-only
Sources/App/Installer.swift        builds a shortcut's .app bundle
Sources/App/Model.swift            shortcuts and where each reads chats from
Sources/App/MenuBarController.swift  the status item
Sources/App/MenuBarContent.swift   what drops down from it
Sources/App/UsageMonitor.swift     polling, caching, backoff
Sources/App/UsageAPI.swift         Anthropic's usage endpoint
Sources/App/ClaudeCredentials.swift  borrowing a profile's access token
Sources/App/SessionStarter.swift   opening a five-hour window
Sources/App/Settings.swift         preferences and the login item
Tools/make-icon.swift              draws the app icon
build.sh                           builds the app and the launcher
test.sh                            builds and runs the tests
release.sh                         tests, builds, signs, packages
Tests/main.swift                   the suite
```
