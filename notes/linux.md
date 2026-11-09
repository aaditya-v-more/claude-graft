# Graft on Linux

The Linux build is a separate program in `linux/`, written in Rust with GTK 4
and libadwaita and shipped as a Flatpak. It follows every rule in `CLAUDE.md`;
this note says what each Mac mechanism becomes on Linux, and which of them have
no Linux counterpart. Read it, then the note for the area you are changing.

## The Claude it manages

Anthropic publishes no Claude Desktop for Linux. People run a repackaging of
the Windows build (deb, rpm, AppImage, Nix), which installs a launcher script on
the `PATH` and a main binary whose path ends in `claude-desktop`. The one on the
development machine is `/usr/bin/claude-desktop-unofficial`, a bash script that
sets Electron's switches and passes the rest of its arguments through to
`/usr/lib/claude-desktop-unofficial/claude-desktop`. `graft_core::claude::launcher`
picks the first of `LAUNCHER_CANDIDATES` that exists, or `GRAFT_CLAUDE_LAUNCHER`.

Claude is always started through the launcher, never the binary: the script is
where the package makes Claude work on this desktop, and it runs reapers that
kill helpers they cannot match to a live Claude. Those reapers recognise a live
Claude by any main process whose executable path holds `claude-desktop`, so a
grafted Claude started through the same script is one of them.

Its profile is `~/.config/Claude`, Electron's `userData` for an app named
Claude, so `~/.config` stands where Application Support stands on a Mac. The
chat stores inside it (`claude-code-sessions`, `local-agent-mode-sessions`,
`skills-plugin` beside the accounts) have the same layout as on a Mac.

This build of Claude takes Electron's single-instance lock, which the Mac build
does not. A second start on a profile already open hands its arguments to the
first and exits, and the first shows its window. That is what `reveal` becomes
on Linux: start the launcher again with the same arguments. It still goes
through `is_running` first, as on a Mac, so the sweep and the conflict question
behave the same way; the lock is a second guard, not the first.

## Paths

| Mac | Linux |
| --- | --- |
| `~/Library/Application Support/<folder>` | `~/.config/<folder>` (`paths::profile_dir`) |
| `~/Library/Application Support/ClaudeGraft` | `~/.config/ClaudeGraft` (`paths::graft_folder`) |
| `/Applications/<name>.app` with `graft.json` inside | `~/.local/share/applications/claude-graft-<id>.desktop` plus `~/.local/share/claude-graft/shortcuts/<id>/graft.json` |
| bundle icon | `~/.local/share/icons/hicolor/256x256/apps/claude-graft-<id>.png` |
| `~/.claude.json` | `~/.claude.json` |

Every host path is built from `$HOME`, never `$XDG_CONFIG_HOME` or
`$XDG_DATA_HOME`: inside a Flatpak those point at the sandbox's private folders
under `~/.var/app`, where Claude is not. The suite redirects all three through
`graft_core::testing::Sandbox`; scripts use `GRAFT_HOME`, `GRAFT_CONFIG_HOME`
and `GRAFT_DATA_HOME`.

## The sandbox

The Flatpak is granted the host's files (`--filesystem=host`), the session bus
names `org.freedesktop.Flatpak` (for `flatpak-spawn --host`),
`org.freedesktop.secrets` (Claude's safe-storage password) and
`org.kde.StatusNotifierWatcher` (the tray), plus the network, X11 and Wayland.
That is close to no sandbox at all, and it has to be: Graft starts and watches
host processes and reads another app's data.

Inside the sandbox `ps` lists only Graft's own processes and `/usr` is the
runtime's, not the host's. So every process listing, every start of Claude and
every look at `/usr` goes through `graft_core::host`, which wraps the command in
`flatpak-spawn --host` when `/.flatpak-info` exists and runs it directly
otherwise. Nothing else calls `std::process::Command` for a host tool.

## What each Mac mechanism becomes

**Shortcut bundle.** A `.desktop` file Graft wrote, recognised by the
`X-Claude-Graft-Config` key naming its `graft.json`, never by its name or file
name. Its `Exec` runs `graft-launch <id>` (inside the Flatpak,
`flatpak run --command=graft-launch <app id> <id>`). Its `StartupWMClass` is
`ClaudeGraft.<id>`, which the launcher passes to Claude as `--class`, so the
dock shows each grafted Claude under its own name and icon. The launcher
version is stamped as `X-Claude-Graft-Version`, which is what
`refresh_launchers` compares. The Mac rule that a launcher behaves like the
Graft that built it becomes: the `.desktop` file is rewritten when its stamp
differs, keeping its name.

**Process checks.** `ps -eo pid=,args=` on the host. `claude::is_claude_process`
is the main binary (`claude-desktop` as the executable's file name) without
`--type=`. The main profile is the Claude with no `--user-data-dir`, matched as
on a Mac, and a data folder match is anchored at the end of the argument.

**Keychain.** Electron's safe storage on Linux is Chromium's: `v10` under a key
from the fixed password `peanuts`, `v11` under a key from a password in the
desktop's Secret Service; AES-128-CBC, IV of sixteen spaces, PBKDF2-SHA1 with
one round over `saltysalt`. `graft-credentials` reads the password from the
host's Secret Service over D-Bus, never through the secret portal, which would
give Graft a keyring of its own. A locked keyring is `Shut`; `Prompting` decides
whether to call `Unlock`, which is what puts the desktop's dialog up. The rule
that the keychain is asked once, and only when asking is the way through, holds
unchanged. Sealing a value Claude reads back uses the scheme the profile's own
token cache uses.

**Menu bar item.** A StatusNotifierItem (the `ksni` crate), which KDE shows and
GNOME shows with the AppIndicator extension. `MenuBarPlacement.isReachable`
becomes whether a `StatusNotifierWatcher` is on the bus with a host registered;
without one the window is the only way in and closing it quits, as a Mac Graft
whose item is behind the notch keeps its Dock icon. A tray item has no text of
its own in KDE and a label only through the AppIndicator extension, so the
conversation ticker is drawn as the item's title where the host shows one and
as its tooltip everywhere.

**Dropdown.** A tray item cannot host a popover. Activating it opens a small
undecorated window; Wayland lets nobody place a window, so it opens where the
compositor puts it.

**Web views.** WebKitGTK 6 (`webkit6`). One `NetworkSession` per artifact
account, with its data and cache in `~/.config/ClaudeGraft/artifact-stores/<id>`,
standing for one `WKWebsiteDataStore`. Requests go out from a claude.ai page by
`call_async_javascript_function`, never from the app's own HTTP client, and
nothing reads the session's cookie manager.

**Relay.** `graft-relay` is the same relay over tokio and rustls. The leaf for
`api.anthropic.com` is signed by an authority made for it with `rcgen`, whose
key is dropped once it has signed; the leaf key lives only in the relay's
memory and its own file, readable by the user alone. Port 47823, `HTTPS_PROXY`
and `NODE_EXTRA_CA_CERTS` are unchanged. The sandbox shares the host's network
namespace, so Claude Code on the host reaches the relay on loopback.

**Updates.** Graft's own updates are Flatpak's (`flatpak update`), so Sparkle,
its feed and the update relaunch markers have no Linux counterpart. Claude's
updates come from whatever package installed it; Graft reports the installed
version and never installs one. Every rule about Claude's updater and Graft's
feed is Mac-only.

**Quit.** Ctrl+Q stands for Cmd+Q: it closes the window and leaves the tray
item while the item is reachable.

## What is still unproven on Linux

The safe-storage attribute Chromium files the password under (`application`
set to the app's name) is read from Chromium's source, not from this machine's
keyring, since reading the keyring from a session is exactly what a person
should answer for. `dist/linux/HANDOFF.md` has the steps that settle it, along
with every check that starts or quits a Claude.
