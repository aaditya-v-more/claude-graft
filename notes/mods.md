# Mods

Graft does not hold mods and does not hold themes. A mod is a Claude Code
plugin with a hooks module; a theme is what such a module hands the session with
`$.theme.set` as it starts, kept in that process's memory and drawn by the
desktop window. Nothing about a theme is written to disk or sent anywhere, so
there is nothing to copy between Claudes. What Graft can decide is which mods a
Claude loads.

## Why every Claude loads the same mods today

Every Claude here shares one `~/.claude`. A mod named in `enabledPlugins`, or a
folder named in `CLAUDE_CODE_PLUGIN_DIRS` under `env` in
`~/.claude/settings.json`, loads in all of them. Each profile keeps its own Claude
Code build under `<profile>/claude-code/<version>/`, but every build reads the
same settings.

## How a per-Claude list wins

Measured with Claude Code 2.1.325, the build the desktop app runs, on
2026-11-14. The `claude` on the PATH was 2.1.278 and does not read the variable
at all (below), so measure against the profile's own build.

`CLAUDE_CODE_PLUGIN_DIRS` in the process environment replaces the one in user
settings outright. The settings value is ignored with a warning that the desktop
app or runner supplies it; the two lists are never joined. With only the
settings value, its folders load; with only the process value, its folders load;
with both, only the process value's folders load.

The desktop app gives a session the profile's `envVars` from
`ccd-environment-config.json`, the field shared usage already writes. So a
per-Claude list goes in that field as one more name, and it must carry whatever
the person had in settings as well, or turning on one mod for a Claude would
silently turn off the ones they set globally.

## What carries over from shared usage

The field is written as the relay's lines are, for the same reasons
([shared-usage.md](shared-usage.md)): only into a closed Claude, under the launch
lock, and never over a value of the person's own under the same name. Unlike the
lines it needs no launcher: a mod left loaded for one more start costs nothing,
where a proxy left pointing at a stopped relay costs Claude Code. A copy a
folder's scoped values inherited is not yet looked for, and would keep the old
list for that folder.

## What the Windows build does

A mod is a folder with a `.claude-plugin/plugin.json` that names it. Graft
lists the copies it holds in `ClaudeGraft/mods/` (what Add Mod… copies in)
and, without being asked, the ones Claude Code keeps: `~/.claude/mods/<mod>` and
`~/.claude/dev-mods/<session>/<mod>`. A found mod is loaded from where it is, so
a mod still being written reaches a Claude as it stands at that Claude's next
start. A name met twice is Graft's copy first, then `mods/`, then the newest of
the sessions'. A mod already in settings starts ticked for every Claude, and
unticking one writes that Claude a list without it, `mods.json`'s `off`. An
empty value is a list too: measured on 2.1.325, `CLAUDE_CODE_PLUGIN_DIRS=""`
replaces the settings value and loads nothing, which is how the last settings
mod is left out.

A mod in `dev-mods` is already loaded in the session that made it, by that
session's own watch on its folder; the boxes say only what every other session
of that Claude loads. Ticking one copies it into `ClaudeGraft/mods/` first and
points the Claude at the copy, since the session's folder is swept with its
data. Deleting a mod sends its folder to the Recycle Bin and forgets the
choices made for it, but one that settings turns on for every Claude is
refused: Graft does not edit `~/.claude/settings.json`, and settings would go on
naming a folder that is not there. `ClaudeGraft/mods.json` keeps the
names chosen for each Claude and the exact value Graft last wrote into it; a
variable holding that value is Graft's, and any other is the person's and is
left alone. `ModEnvironment.Settle` runs in the same turn as the shared-usage
pass and just ahead of it, so the relay's two finished values are worked out
from the field this pass left. A Claude that is open is written when it is next
found closed, and Graft going takes its value out of every closed Claude beside
the relay's lines.

Settle wakes on the same thirty-second timer as sharing and asks each Claude's
key only where there is a choice or a value of Graft's to account for, so a
person with no mods pays nothing for it.

## What the documentation settles

Claude Code's plugin pages (loading, install, settings reference, desktop, read
2026-11-14) agree with the measurement above: for a session the desktop app
starts, "Claude Code ignores an `env` value from a settings file for a variable
that environment already sets." They also say:

- A mod Claude writes lives in `~/.claude/dev-mods/<session>/` and loads in that
  session alone. Its folder is swept with the session's other data once older
  than `cleanupPeriodDays` (default 30), so a Claude pointed at it outlives it.
  `~/.claude/mods/` is not named anywhere; it is where a mod was copied by hand.
- A mod in `~/.claude/skills/<name>/` loads in every session, on unless a
  settings file sets `"<name>@skills-dir": false` in `enabledPlugins`. The
  variable does not reach it, except that a folder of the same name given
  through the variable replaces it.
- Installed plugins are on or off per user, project or repository-local
  settings file; no scope follows a desktop profile.
- There is no per-profile setting: every Claude reads the one
  `~/.claude/settings.json`.

## The terminal CLI

Measured with the `claude` on the PATH, 2.1.278, on 2026-11-18, signed out and
against a throwaway `CLAUDE_CONFIG_DIR`, reading `--debug-file` and `plugin list
--json`. It ignores `CLAUDE_CODE_PLUGIN_DIRS` wherever it is set: in settings
`env` the name is read and nothing loads, in the process environment nothing
loads, and an empty value changes nothing. `--plugin-dir` loads its folder as
`<name>@inline` at session scope, repeatable, and is the only way in; with the
variable also set, only the flag's folder loads. So a terminal list can go only
through the flag, and it adds rather than replaces: with a plugin installed and
on in user settings, a session given `--plugin-dir` loaded the commands of both.
A flag folder with the same name as an installed plugin is listed beside it, as
`<name>@inline` next to `<name>@<marketplace>`. Whether a newer CLI starts reading the variable, and so starts
taking the desktop's per-Claude values from a shell that inherited them, is
unmeasured.

## Not yet measured

Whether a value set in the Code tab's own environment editor reaches a session
the same way as one Graft wrote; whether `enabledPlugins` can be narrowed per
Claude at all, since it lives only in settings; and how a hot-reloading session's
own mods folder sits beside the variable.

## Featured and Browse

The Mods page has three views: Installed, Featured and Browse, each mod a card.
Featured shows the featured picks of the community list at
`github.com/karanb192/awesome-claude-code-mods`, credited by a link at the top
of the view. The picks are the bullets under its README's Featured heading; the
list's `data/mods.json` is its scan of every mod it found, with the commit it
read and the validator's account of what the mod reaches. A featured link is
matched to the scan by repository and folder, or by name where one repository
holds several. The scan is about 24 MB, kept in `ClaudeGraft/catalogue/` and
asked for again only with its ETag. Nothing is asked for until the view is
shown or refreshed.

Browse is the whole scan, filtered and sorted in place with nothing asked of the
network. It keeps only entries of kind `mod` with a scanned commit: the list
also carries Claude Code's built-in mods, which ship with it, and fixtures,
catalogues and duplicates, which are not mods to install. Reach is filtered by
the scan's levels (0 draws and remembers, 1 reads, 2 writes or runs, 3
network); about 270 mods the scan could not place have no level and are left
out of any reach filter. The validation filter keeps `passed` and `warnings`.
Forty cards are drawn at a time. The scan's own fields can be `null` where a
mod was not placed, and a reader that assumes a number fails on the real file;
the live test, skipped unless run by hand, is what caught it.

Install downloads the scanned commit, never the newest, so what the scan
reported is what arrives; takes the mod's folder out of it; runs the validator
of the newest Claude Code the main profile keeps over it; and adds it to
Graft's folder with `graft-catalogue.json` naming where it came from. It asks
first, naming the repository, the commit and what the scan says the mod
reaches, and a mod installed this way loads in no Claude until one is ticked.
Nothing is updated by itself.

## What the list cannot say, and what Graft guesses

The scan records which parts of the screen a mod draws on, not which surfaces
it draws for: image-view decides that with one `if (e.surface !== 'terminal')`
in its drawing hook, and three unrelated entries share its name. So Graft
guesses, and says that it is guessing.

- **Mentions Desktop**, on by default in Browse, keeps mods whose own
  description names the desktop app or its Code tab, unless it goes on to say
  the mod does nothing there. On the 2026-11-14 list that leaves about 240 of
  8,461. It reads only what the author wrote.
- **Overlap.** A mod whose scan says it reads the sign-in, or whose description
  switches accounts, moves or manages Claude or desktop sessions, or switches
  where a session's requests go, carries a warning on its card: each could undo
  what Graft does or bypass the relay. On that list it flags about ten,
  ccswap, ClaudeDeck, Orca Strator and session-mover among them.
- **Terminal-only**, at install, from the downloaded code: some drawing hook
  turns away every surface but the terminal and nothing names `'desktop'`.
  Checked against the real mods, image-view is caught and cache-tax, which
  draws an SVG for the desktop, is not. It asks before installing and never
  refuses.
