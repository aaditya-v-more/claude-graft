# Dock and platform details

[Back to interface.md](../interface.md)

## The Dock, the icon and the Claude in front

**A running Claude shows the icon of the bundle it runs from, and nothing
outside Claude can change that.** A shortcut's icon belongs to the shortcut; the
process it starts is `/Applications/Claude.app`, so the tile in the Dock is
Claude's own. The suggestion that keeps arriving is `app.dock.setIcon` from
inside Claude, and Claude's own build shuts every way in, measured against
4.16336.0: its Electron fuses refuse `ELECTRON_RUN_AS_NODE`, `NODE_OPTIONS` and
`--inspect`, enforce the asar integrity hash and load only from the asar; the
hardened runtime grants nothing for `DYLD_` variables or unsigned libraries; and
the app itself refuses `--remote-debugging-port`, `--remote-debugging-pipe` and
the `*-cmd-prefix` switches. What is left is editing `app.asar`, which is an app
Graft did not create, breaks Anthropic's signature and is undone by the next
update.

Nor can another process set the tile. A shortcut that `exec`s Claude's binary
from inside its own bundle looks as though it should keep the shortcut's tile,
and was measured with two throwaway apps not doing so: LaunchServices records
the shortcut at launch and swaps in the bundle the binary lives in the moment
that binary checks in.

Which leaves a copy of Claude per shortcut — the only thing that changes the
running icon, and the reason it is not done. Its `Info.plist` is bound into the
signature, so the icon could only be a Finder custom icon over an untouched
copy; re-signing instead gives up the keychain groups and application
identifier that Anthropic's provisioning profile grants. Claude's updater
updates whichever bundle is running, taking the icon with it and splitting the
versions. LaunchServices would hold several `com.anthropic.claudefordesktop`
bundles and could open the main profile from a shortcut's copy. And Claude
writes machine-wide paths derived from its own executable —
`com.anthropic.claude_browser_extension.json` names
`<bundle>/Contents/Helpers/chrome-native-host` — so one copy repoints Claude in
Chrome for every profile. That is too much to risk for an icon. The limitation
is stated beside the icon picker and in the README instead.

**The name on hover is the same wall, and so is a badge.** The Dock labels a
running app with the display name LaunchServices holds for it, and that comes
from the bundle, so every Claude reads "Claude" whichever profile it is on.
`_LSSetApplicationInformationItem` — the call Chromium renames its own process
with — accepts a new name or badge for another app, answers with success and
changes nothing. Measured on macOS 27 against a throwaway app: from a plain
tool, from `lsappinfo setinfo`, and from a checked-in app the way Graft is one,
with `lsappinfo info` and the Dock's accessibility labels unchanged each time,
while the same check read a badge the app had set on itself. `lsappinfo` carries
no LaunchServices entitlement and is refused like the rest. An app renaming
itself does change what LaunchServices holds, and still leaves the label on its
existing tile as it was.

What does work is the shortcut's own tile. Kept in the Dock it has the
shortcut's name and icon, and pressing it while that profile is open brings
that Claude forward instead of starting another — the first guard in
`Graft.run`.

**The menu bar is where it can be said instead.** `FrontmostClaude` watches
which app comes forward and matches a Claude to its profile through its command
line, the lookup a usage refresh already needed when a Claude is brought
forward, so one `ps` serves both. The status item then carries that shortcut's
icon, drawn by `Installer.previewIcon` — the code that drew it for Finder, so
the bar shows what the person picked rather than a second likeness of it — and,
when asked for, its name on the icon's colour, darkened only as far as white
text needs.

Graft coming forward leaves it where it was. That is somebody opening the
dropdown to ask about the Claude they were just using, and dropping it then
would also narrow the item under the popover anchored to it. A shortcut's own
launcher is not a Claude, whatever it is called; it is in front for a moment on
its way to bringing one forward. A lookup spawns a process, so a flick past one
Claude to another can land the two out of order, and a ticket keeps only the
newest — though the account a late one names is still asked for its figure.

The figure follows what is shown. With a Claude in the bar the number beside it
is that account's and never the tightest window's, or one account's figure
would stand beside another's icon, and a Claude nothing could be drawn for is
not shown and neither is its figure. Each account is weighed by the figure it
would show: the five-hour one, or for an account with no five-hour limit its
week, which is drawn as `W 94%` (`Н` in Russian) so that it is never read as a
five-hour figure. The figure carries no colour, so it keeps
the one the bar picks for the wallpaper behind it. The words go into the
accessibility title, since pictures in a title read out as nothing.

The icon is on for everybody and the name for nobody until asked. Both widen
the item, and a full bar drops an item rather than squeezing it — the case
`isShowing` exists for — but the icon costs one icon's width and only while a
Claude is in front, where a name can take six times that and is cut short at
`FrontTag.maximumWidth`. The icon's key is read with `object(forKey:)`, an
absent one meaning on, because every machine that has it arrived by updating.

**The pictures are lowered by hand.** The status item centres its title as one
block, a point above the button's middle, and a picture taller than the figure
is that block: its centre lands on the block's whatever offset it is given, and
only the figure moves. Graft's own symbol sits half a point below the middle,
which left the icon and the name a point and a half above it — 10.0 points down
a 22-point button against the symbol's 11.5, read off a status item placed in
the real bar. `FrontTitle.drop` is that much empty space on top of each picture:
what can be seen goes down to the symbol's line, and the block, with the figure
in it, stays where it was.

The figure is AppKit's text placement, 10.2 points down against the symbol's
11.4, and once the pictures had moved it was the one thing left high.
`figureDrop` lowers it a point, in every state, so it does not jump as a Claude
comes forward. A point and not a point and a half because text lands on whole
pixels: the larger figure put it half a pixel below the symbol. Measure all of
this on a status item in the bar.

**Graft's own mark leaves the bar while a Claude in front is drawn there.** The
front Claude's icon or name already says whose item it is, and the half-filled
circle beside it was width a full bar could not spare. It comes back when no
Claude is shown, and an update's symbol stays whatever is in front, since that
is news the front Claude cannot carry. With no mark, the title drops the space
that parted it from one (`FrontTitle.make(afterImage:)`).

**Each Claude and each lent account has a colour of its own unless a person
picks a shared one.** `IconPreset.next(besides:)` hands out the first colour
nothing wears, in `handedOut` order, and never Claude's own original, which
is the main profile's. It is given once: a new shortcut takes it when made,
and an account when it is added to the pool. Nothing recolours what exists, so
a Dock icon somebody knows does not change under them. Accounts saved before
accounts had colours get one on the first pass that knows which account each
Claude is signed into (`learnAccounts`), so an account that is also a Claude
here wears that Claude's colour instead of using up a free one; it follows that
Claude's colour afterwards through `UsagePoolController.preset(of:)`.

**A Claude that draws on other accounts shows all of them in the menu bar**
(`PoolStrip`), its own first and always in the same order. The account
answering the conversation on screen is drawn full size in a red ring and the
rest small and faint, so which allowance is being spent reads off the bar.
Size alone was easy to miss, so 2.0.1 added the ring: a one-point `systemRed`
stroke at the edge of the lit icon's own square, with the icon drawn two points
smaller inside it, because Claude's icon fills nearly all of its square and is
as warm as the ring, and at full size the two ran together. The ring stays
inside the square because a taller strip would no longer line up with the
figure beside it. A conversation no account has answered lights nothing. The
dropdown leads with the front Claude's latest conversation, usually the same
one, and the others wait behind "Other conversations · n".

## The Help menu

SwiftUI fills the Help menu with one item that says help isn't available,
which reads as an app nobody looks after, so `GraftHelpCommands` replaces it.
It leads to the site, the release list, a new issue, an email, the
developer's LinkedIn profile, the source and the tip jar. The menu is only on
screen while the window is, since the app is an accessory the rest of the
time; the dropdown keeps its own Donate item for that reason.

The tip jar is called Donate on both systems. On Mac only the filled heart is
coloured: #E0492B on a light background, #EE5A3A on a dark one. Windows uses
an outline heart in the text colour. Its [flyout case](../../Tests/manual/windows-flyout-rows-and-donate.md)
opens Donate before the manager has been shown.

The email opens already addressed, with a subject naming the Graft version
and a body ending in the Graft and macOS versions. The first reply to a
problem report is otherwise always a question asking for exactly those. The
address and the LinkedIn handle are spelled out in `Links`, not derived from
the GitHub or Ko-fi names, and the suite reads them back.

## Windows: a capped column inside a ScrollViewer

WinUI's `ScrollViewer` places its content by the width the content asked for
in measure, not the width it was arranged at. Settings' column is capped at
1064 and asked for only what its widest card needed (846 at 100%), so once the
window was wide enough for the cap to apply (about 1300 DIPs), the column was
centred as if it were 846 wide: 109 px right of centre, its right side past the
window. Disabling horizontal scrolling made no difference; the old single
column needed an explicit width on resize. Settings now caps the grid *outside*
the scroller. The section picker stays above it, and only the selected section
takes space. The ordinary sections should fit the smallest window; scrolling
remains available on a short screen or with enlarged text.

The Russian Help links used up the card's trailing Auto column, leaving its
title and description in a nearly zero-width column. They now follow the
description instead of competing with it. The usage heading likewise gives
its freshness line a row when the words would otherwise be cut off. At short
heights the sidebar gives the space from its captions back to the shortcuts.
[The manager fit check](../../Tests/Layout/windows-manager.ps1) reads bounds
and scroll ranges without bringing an off-screen item into view.

## Windows: TextChanged comes late

A WinUI `TextBox` raises `TextChanged` after the change, not inside the setter,
so a `_filling` flag set around a programmatic `Text =` is already clear when the
event lands. The profile editor's folder field took its own refill for a hand
edit and stopped following the name at once. It now counts the folder as typed
only while it differs from the name's folder (`ProfileEditor.FolderBox_TextChanged`).
Seen on the second PC making Test A and Test B, 18 Nov 2026.

## Windows: the title bar copies the page's heading

The Claude's name and its Running or Not running line live in `MainWindow`'s
title bar, copied from `MainPage.Heading`. It was copied only when the selection
or the list changed, so a Claude opening or quitting reached the row and its dot
but not the line above it. `MainPage.HeadingChanged` now fires whenever the
chosen row's state line moves.
