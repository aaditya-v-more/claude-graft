# Running the Linux window and tray by hand

Everything here starts Graft's own window and tray item, which a session run
from inside a Claude that Graft manages must not do to itself. Run it from a
terminal outside any Claude. Nothing here opens, quits or brings forward a
Claude except step 12, which only brings forward one that is already open.

The suite (`linux/test.sh`) already covers the rules: the quit and window
policies, reachability, which routes ask before opening, who may start a
session, colours handed out once, the tray never retitling on a timer. What
it cannot do is put a window on a real desktop. That is this plan.

## What not to touch

- Do not press Open, Start Session, Create Shortcut, Copy Them Here or any
  sign-in in steps 1 to 11: they run against the throwaway world, but Open
  would start a real Claude on a throwaway profile.
- Do not run step 12 against your real home unless you accept what an
  ordinary start of Graft does there: a sweep that files missing session
  records, launcher entries rewritten to this build's version stamp, and a
  shared-usage pass that settles each closed Claude's Code environment.

## Setup

From the repository root, outside any Claude:

    cd linux
    ./sdk.sh cargo build -p graft-app -p graft-launch -p graft-relay
    export WORLD=/tmp/graft-gui
    rm -rf "$WORLD" && mkdir -p "$WORLD/home/.config" "$WORLD/home/.local/share"

Run the window inside the SDK with the display, the session bus and the
throwaway world (the session bus is what the tray item and `flatpak-spawn
--host` need):

    flatpak run --share=network --share=ipc --socket=wayland --socket=fallback-x11 \
        --socket=session-bus --device=dri \
        --filesystem="$PWD" --filesystem="$WORLD" \
        --env=GRAFT_HOME="$WORLD/home" \
        --env=GRAFT_CONFIG_HOME="$WORLD/home/.config" \
        --env=GRAFT_DATA_HOME="$WORLD/home/.local/share" \
        --env=GRAFT_CLAUDE_LAUNCHER=/nonexistent \
        --command=./target/debug/graft org.gnome.Sdk//51

`GRAFT_CLAUDE_LAUNCHER=/nonexistent` makes Graft find no Claude, so nothing
it does can start one; the window then says "Claude Desktop was not found."
For the GTK inspector, add `--env=GTK_DEBUG=interactive`.

To try the Flatpak instead, `./build.sh --install`, then run
`flatpak run --env=GRAFT_HOME=... (the same three variables) io.github.aaditya_v_more.ClaudeGraft`.

## Steps

| # | Do | Should show | Where to check |
| --- | --- | --- | --- |
| 1 | `./sdk.sh ./target/debug/graft --help`, then `--version` | Usage text, exit 0; `Claude Graft 2.0.1` | terminal |
| 2 | Start Graft (Setup) | A window 820 by 560 titled Claude, sidebar from top to bottom with its own header, title only over the content column | screen |
| 3 | Try to drag the window smaller | It stops at 820 by 560 | screen |
| 4 | Look at the sidebar | Claude, Artifacts, Projects, Search Chats under "Claude"; "Shortcuts" with nothing under it; New Shortcut at the foot of the list; Claude Desktop line, Support and Source at the bottom | screen |
| 5 | Ctrl+N | A "Claude 2" page, "Not created yet" in the subtitle, the icon Blue | screen; `$WORLD/home/.config/ClaudeGraft/shortcuts.json` has `"iconPreset": "blue"` |
| 6 | Ctrl+N again, then right-click the new row, Delete Shortcut... | Claude 3 is Violet; a draft goes without a question | screen, shortcuts.json |
| 7 | Look at the tray (KDE, or GNOME with AppIndicator) | Graft's icon; tooltip "Claude Graft" with "No usage reported yet" | panel |
| 8 | Click the tray item | A small window "Claude Graft Menu": Claude Desktop line, "No Claude profiles yet" or the main profile row, the four switches, Refresh Usage, Open Claude Graft, Support, Quit | screen |
| 9 | Click somewhere else | The small window closes | screen |
| 10 | Close the main window with the item visible | The window goes, the process stays (`pgrep -f target/debug/graft`), `app-settings.json` has `"mainWindowOpen": false`; the item's menu, Open Claude Graft, brings it back | panel, `$WORLD/home/.config/ClaudeGraft/app-settings.json` |
| 11 | Ctrl+Q with the item visible; then Quit Claude Graft from the item's menu | Ctrl+Q only hides the window; Quit ends the process | `pgrep` |
| 12 | Close the window, Quit from the tray, start again | No window appears; after three seconds still none while the item is visible | screen |
| 13 | On GNOME without the AppIndicator extension (or with it turned off), start Graft | Banner "No tray here shows Claude Graft's item..." in the content column; closing the window quits; a hidden start puts the window up after three seconds | screen, `$WORLD/home/.config/ClaudeGraft/diagnostics.log` has `window.shown` |
| 14 | Ctrl+, (Settings); toggle Open at Login on, then off | `$WORLD/home/.config/autostart/io.github.aaditya_v_more.ClaudeGraft.desktop` appears and goes | file |
| 15 | Turn Show in Tray off from Settings | The item leaves the panel; closing the window now quits | panel |
| 16 | With `GTK_DEBUG=interactive`, inspect controls | Widget names such as `window.sidebar.Claude`, `window.Claude.open`, `window.Claude.start-session`, `dropdown.quit`, `settings.show-in-tray`; every button has an accessible label | GTK inspector |
| 17 | Select Artifacts, Projects, Search Chats | Each page draws inside the window without scrolling the window; Search sends nothing while typing | screen |
| 18 | Only if you accept the note above: run the installed Flatpak with your real home and a Claude already open; click into that Claude's window, then into another app | The tray icon becomes that Claude's icon and the tooltip leads with its name and figure; it goes back to Graft's mark when the other app comes forward; opening the tray's window keeps the Claude's name | panel, tooltip, `diagnostics.log` |

## Putting things back

Quit Graft from its tray menu (or `pkill -f target/debug/graft`), then
`rm -rf /tmp/graft-gui`. The development build installs nothing. If step 18
used the Flatpak and it was not installed before, `flatpak uninstall --user
io.github.aaditya_v_more.ClaudeGraft`.

## Results

| # | Desktop and session (KDE/GNOME, Wayland/X11) | Result | Notes |
| --- | --- | --- | --- |
| 1 | | | |
| 2 | | | |
| 3 | | | |
| 4 | | | |
| 5 | | | |
| 6 | | | |
| 7 | | | |
| 8 | | | |
| 9 | | | |
| 10 | | | |
| 11 | | | |
| 12 | | | |
| 13 | | | |
| 14 | | | |
| 15 | | | |
| 16 | | | |
| 17 | | | |
| 18 | | | |
