# The window, the dropdown and the menu bar

SwiftUI and AppKit behaviour this app learned the hard way, accessibility, quitting, and what the menu bar can and cannot say about the Claude in front. Read this before changing anything in `Sources/App`.

## Quitting and the status item

**⌘Q does not quit.** `applicationShouldTerminate` cancels an unasked-for
terminate and closes the window instead, because the menu bar item is the part
that does the reporting and closing a window is not asking for it to stop.
`AppDelegate.quit()`, wired only to Quit in the dropdown, is what really ends
it, along with a logout — refusing that stalls the shutdown on a dialog — and
along with an update. Sparkle starts the new version by asking this one to
terminate; refusing that left 1.0.0 running with 1.0.1 already staged, put the
window away on the way past, and wrote `mainWindowOpen` false, so every launch
after came up with no window. One refused terminate, three symptoms, none of
which named the cause. `Updater.isRelaunchingForUpdate` is the clause that
fixes it.

Window bookkeeping stops once a terminate is allowed. `AppDelegate.isTerminating`
guards it, because windows closing on the way out are the app shutting down, not
someone putting a window away, and recording it as the latter brings the next
launch — an update's own relaunch above all — up hidden. The
rule lives in `QuitPolicy` so the suite can drive it, and its last clause is
the way out: with nothing reachable in the bar, `MenuBarController.isShowing`
reads false and a quit is a quit. Ask that, not the setting; the setting says
what was wanted, not whether the bar had room.

**A status item that exists is not a status item you can click.** macOS drops
items it has no room for — a full bar, and a notch taking the middle — without
telling the app, and the dropped item is still an object that answers
`isVisible` true. `isShowing` used to be `item != nil`, which said yes to a bar
with nowhere to put it, so the clause above would have cancelled a quit and left
Force Quit as the only way out. What actually gives it away is placement:
filling the bar with throwaway items put the overflow at x of -71, -115, -160
and -204. `MenuBarPlacement.isReachable` is the rule, horizontal only because
the bar sits above `NSScreen.frame` and a full containment test rejects every
item there is. Seen for real: Graft vanished from the bar with the process alive
and polling, and came back when another app's item was removed.

## SwiftUI and accessibility

**The profile selector is a split view, not a navigation stack.** On macOS 27,
`NavigationSplitView` below the notices adds another titlebar backdrop inside
the detail pane. It covers the first section heading, and hiding scroll-edge
effects or the toolbar background does not remove it. Use `HSplitView` with
the toolbar on the outer view. The layout suite checks for this extra backdrop
in both appearances, both languages and each supported window size.

**Notices above the split view need their own layout space.** A top
`safeAreaInset` on `NavigationSplitView` left its native sidebar and form
drawing beneath the update banner. The first account and the top of the form
were hidden even though accessibility still reported them. Keep the notices
and split view as siblings in a vertical stack. `test-layout.sh` checks the
native view bounds as the update state, window size and language change.

**Every control is named and identified by hand.** SwiftUI names a button
holding only a symbol after the symbol, so the "!" beside every section read out
as "Error" and Show in Finder as "Move", and it leaves an identifier only where
a symbol happens to supply one. From outside, the only ways to find a switch
were where it was drawn and the words on it, which change with the language.
`identified` gives each control `<surface>.<profile>.<control>`: the surface
because the dropdown and Settings carry the same switches and can be open at
once, the profile's folder because no two profiles share one and it reads the
same in every language. It goes on the control itself and never on a stack,
where it lands on every control inside and one identifier naming several
controls names none of them. A button whose label is a picture or a spinner
gets an `accessibilityLabel` saying what it does and to which account, since
every account row has an Open of its own and six of them reading "Open" say
nothing.

**A label and its value are one element.** `LabeledContent` reaches VoiceOver
as the label, the value, and the value a second time. `spoken` makes the pair
one element and marks it as text, because an element SwiftUI is told to make
comes out `AXUnknown` with no role description at all. A usage bar is a
progress indicator through `accessibilityRepresentation`, carrying "6% used,
resets in 4h 1m" as the value description VoiceOver reads in place of the bare
fraction. The status item is AppKit's, so its identifier and spoken title are
set on the button in `MenuBarController`.

**The layout suite reads the windows the way VoiceOver does, from outside.**
SwiftUI builds a window's accessibility tree only once a request arrives from
another process, and only after `finishLaunching`; asked from inside, every
window measured as an empty tree, and the elements it builds then do not adopt
`NSAccessibilityProtocol` anyway. So `test-layout.sh` reads each surface
through the `AXUIElement` calls an assistive app makes, from a second thread
while the main one answers, and needs Accessibility allowed for whatever starts
it. Two things it follows that a plain walk would miss: a control in a grouped
form has no name of its own and is linked to the label beside it through
`AXTitleUIElement`, which is what VoiceOver reads; and an SF Symbol leaves its
own name as an image's identifier, which is why the check asks for this app's
prefix rather than for any identifier at all.

**A popover is not one of the app's windows until it is put there.** AppKit
hangs an `NSPopover`'s window under the status item that opened it, as an
`AXPopover`, and leaves it out of the app's `AXWindows` — the one list most
clients that drive an app from outside search. Every control in the dropdown
was named and identified and could still be neither found nor pressed; only a
walk that starts at the status item got there. `setAccessibilityParent(NSApp)`
lists the popover among the windows while it is open, still reading as a
popover, and the list is back to what it was once it closes.
`NSApp.setAccessibilityWindows` looks like the direct way and is the one never
to use: handed a list and then nil, it was measured leaving the app with no
windows at all, the main window and Settings included, and it stayed that way.
The suite fails if it appears.

The popover is titled as well, "Claude Graft Menu", and the title is set twice:
`title` for the window server and `setAccessibilityTitle` for VoiceOver.
Untitled, VoiceOver announced a bare "popover" and nothing could ask for the
dropdown by name; with only one of the two set, a client that checks the window
server's name against the accessibility one refused to act in a window where
they disagreed. Even titled, a press aimed straight at an identifier inside the
popover is refused by that same client, so a script reads the window first and
presses what the reading found.

Settings switches are found by identifier, never by name. A switch in a grouped
form has no name of its own, only the `AXTitleUIElement` link to the label
beside it; VoiceOver follows the link, and a client that does not shows the
switch's value, 1 or 0, as its name. An `accessibilityLabel` on the toggle to
suit that client puts a second name beside the link, which VoiceOver then reads
twice, so it was tried and taken back out.

**No `MenuBarExtra`.** Alongside a `WindowGroup` it loops on macOS 26 — every
scene update rebuilds the main menu, which invalidates the scene. 100% CPU and
about a gigabyte a minute, but only while a window is open. Bisected: not the
label, not the content, not the style. The status item is an AppKit
`NSStatusItem` in `MenuBarController`.

**Nothing blocking in a view body.** No filesystem, no process spawn, no
network. `Graft.isRunning` once used `waitUntilExit()` inside a body; that spins
the run loop, re-enters AppKit layout mid-update, and segfaults with a null
program counter. Every process wait now uses a semaphore, and status is read on
a background queue into `@State`.

**Long-lived objects live in `enum Shared`, not `@StateObject` on the App.**
Observing them from the App value invalidates the whole scene on every poll.

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
