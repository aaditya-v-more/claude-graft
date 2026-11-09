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

The notch is the second place to vanish, and it is on the screen. An item laid
out across it, measured at x 866–1054 against a notch of 751–959, passed the
edge test while no pointer could reach it. `isReachable` also takes the notches,
each one the gap between a screen's `auxiliaryTopLeftArea` and
`auxiliaryTopRightArea`, and any overlap with one counts as out of reach.

Quitting was not the only place that asked the setting. Closing the last window
made the app an accessory whenever Show in Menu Bar was on, and a login launch
whose window had been put away last time came up the same way, so with the item
dropped or behind the notch Graft was running with no window, no Dock icon and
nothing in the bar — reachable again only by opening it from Finder.
`WindowPolicy` is the rule for both. A closing window takes the Dock icon only
when `isShowing` says yes; otherwise nothing changes, and the window is still
recorded as open so the next launch brings one up. A hidden launch cannot ask at
once, since its window closes before the item has been placed, so it asks again
`placementGrace` later and puts the window up if the item is still out of
reach.

## SwiftUI and accessibility

**The profile selector is a `NavigationSplitView`, and the notices sit inside
its detail column.** For a while it was an `HSplitView` under a stack of
notices, because on macOS 27 a `NavigationSplitView` *below* the notices drew a
second titlebar backdrop inside the detail pane, over the first section
heading. That split did what it was asked and looked wrong for it: the sidebar
had no sidebar material, stopped short of the titlebar, and the window's title
sat across the divider as two halves of one bar. The backdrop was never the
split view's fault but the stack's — a split view that is not the window's
root cannot reach the titlebar, so it draws its own. With the split view as the
root and `UpdateRecoveryNotice` at the top of the detail column, the sidebar
runs the full height under the titlebar, the system's Hide Sidebar button and
View › Show Sidebar come with it, and the title belongs to the detail column
alone.

What the old arrangement protected still holds, and `test-layout.sh` checks it
in both appearances, both languages and each window size. The first split item
must have `.sidebar` behaviour and run from the top of the window to the bottom.
The title field must lie wholly on one side of the sidebar's edge. A titlebar
backdrop may sit only at the top, no taller than the titlebar itself: the split
view draws one per column there, which is how it should look, and a backdrop
anywhere lower is the old fault back. No page's content may start under the
toolbar. The suite's window bridges the toolbar and title as the real scene does
(`sceneBridgingOptions`), because a window without a toolbar has 52 points the
real one does not and hid the overflow that started all this.

The system's sidebar button is the one control nobody here names. Its
accessibility name is the label of its toolbar item, whose identifier ends in
`navigationSplitView.toggleSidebar`, and the identifier check lets that one
through by that label and fails instead if the button is missing.

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

A button's action is not a body, but the same holds for anything that waits.
Create and Update Shortcut signed the app, waited on the update-settings lock
and ran a mirror pass that can copy every chat a profile has, all on the main
thread, so the window stopped answering for as long as that took and a second
press queued a second install behind the first. `ShortcutDetail.install` runs
`Installer.install` on a background queue and holds the page still, with a
spinner in the button, until it answers; the result is written back to the
shortcut found by id, since the list may have moved under it meanwhile.

**Long-lived objects live in `enum Shared`, not `@StateObject` on the App.**
Observing them from the App value invalidates the whole scene on every poll.

## Fitting the window

Both detail views fit the smallest window, 820 by 560, without scrolling.

That size has to be the smallest in fact, not only in the notes. The scene
asked for 720 by 460 for months, and a live run on 8 November dragged the
window to 777 by 519 with every page laid out for more. A limit SwiftUI takes
from a view belongs to the root view, which does not span the window: the
titlebar and toolbar sit outside it by an amount AppKit decides, so a plain
820 by 560 frame would have made the smallest window taller than 560 instead.
`SmallestWindow` measures what lies outside the root and asks for 820 by 560
less that; until the first measurement it asks for less, so the first layout
cannot grow a saved frame.
A note people skimmed was what made them scroll: three-line explanations of
what a graft is, where chats are read from or why Anthropic did not answer
pushed the buttons people came for below the fold. So a note is one line
(`OneLineNote`), and the paragraph it used to be sits behind an
`InfoButton` beside it. A one-line note still says everything a person needs
to act: a usage figure says the time it is from, a wait says how long.

The layout check measures the document view against the clip view minus the
larger of the two content insets at each edge. The grouped form's bottom bar is
a `safeAreaInset`, so a check that ignored the inset passed with the last row
hidden under the buttons; it was 97 points over when first measured properly.
Adding the two together was wrong the other way: in a window with a toolbar the
clip view's insets repeat the scroll view's, and the sum counted the toolbar
twice.

What keeps the fullest page in, a shortcut lending two accounts with all three
bars showing, is that nothing takes a row it does not need. Whether a Claude is
running is the window's subtitle. When the figures are from, the refresh button
and Add sit on the section heading's right (`SectionHeader`'s trailing view).
A reset time sits on its bar's line, after the percentage. Shared usage shows at
most one note row, a problem before the environment before Open at Login, with
the others behind its ⓘ. A shortcut's name, folder and Show in Finder share a
row, and where it reads chats from is a labelled picker with the sharing note
under its label. At 820 by 560 that page has 6 points to spare, in both
languages; a new row there needs another row to give one up.

CI found the next row nobody had counted. Its runner has no Claude.app, and
the "Claude.app was not found." notice was a section of its own, 46 points
that put that page 40 over; on this machine, with Claude installed, the page
had never drawn it. The notice is now the window's subtitle, in place of
Running or Not running, which a Mac without Claude cannot be anyway. A
failing fit check prints every row's position (`ROWS …`), so a page over on a
machine nobody can sit at says which row grew.

Settings is a grouped form too, and a grouped form scrolls, so it asks for no
height and a window sized from it opens as a bare titlebar. It is held to its
rows with `fixedSize(horizontal: false, vertical: true)`.

The freshness line on the usage heading reads "Updated … from Anthropic" when
the figure is current. "· figure from …" appears only beside the reason a
refresh failed, where the time says how old the figure being shown is; a plan
that expects it on a healthy account is expecting the wrong line.

A row with its own button never takes `.spoken` on the whole row: the
button inherits the row's name, and VoiceOver reads the account twice and
the action never. Name the label part, and let the button name itself.

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
time; the dropdown keeps its own Support item for that reason.

The email opens already addressed, with a subject naming the Graft version
and a body ending in the Graft and macOS versions. The first reply to a
problem report is otherwise always a question asking for exactly those. The
address and the LinkedIn handle are spelled out in `Links`, not derived from
the GitHub or Ko-fi names, and the suite reads them back.

## Linux

The Linux window is GTK 4 and libadwaita, in `linux/crates/app`, and every
rule above holds with these translations.

**Quitting and the tray item.** Ctrl+Q stands for Cmd+Q and goes through
`QuitPolicy::ends_the_app`, which on Linux has no update clause: Flatpak
installs a new version beside the running one and never asks it to stop, so
there is no relaunch to let through. The session ending needs no clause
either. It ends Graft with a signal whose default is to stop, and nothing in
Graft refuses it. Quit in the tray's window and in the item's menu is the one
quit that is asked for.

Reachability is `TrayPlacement::is_reachable`: the item registered, a
`StatusNotifierWatcher` on the session bus, and `IsStatusNotifierHostRegistered`
true. GNOME without the AppIndicator extension has a watcher-less bus; a
panel whose tray applet was removed can leave a watcher with no host, which
takes the item and shows it nowhere, exactly the item that exists without
being clickable. The bus is asked on a thread of its own every five seconds
(`reach::watch`) and the item's own `watcher_online` and `watcher_offline`
say when its registration comes and goes, so the main loop only ever reads an
answer.

A Linux app has no Dock icon to keep, so `WindowPolicy::hides_window` decides
between hiding the window and quitting. With the item reachable, closing the
window hides it and records it put away; without, closing quits and leaves
the window recorded as open, so the next start brings one up. A hidden start
asks again `PLACEMENT_GRACE` later and shows the window when the item still
cannot be reached. A banner inside the content column says so while the
item is wanted and unreachable, since a window that quits on close is
otherwise a surprise.

**The split view.** The root is an `AdwNavigationSplitView`, with the
sidebar's own `AdwToolbarView` and header and the content column's, so the
sidebar runs the full height and the title belongs to the content alone; the
notice is the first child of the content column. The header bars are inside
the window's content, so `set_size_request(820, 560)` is the whole window's
smallest size, where the Mac had to measure what the titlebar took. The pages
are data: `window::Page` names each one, `window::rows` lists them for the
sidebar and `window::page_widget` builds them, which is where Artifacts,
Projects and Search Chats come in from `graft-web`, over one `ArtifactCenter`
and one `ProjectCenter` made after GTK starts.

**Naming controls.** `ui::identified` (the web crate's, reused) sets a widget
name of `<surface>.<profile>.<control>` and an accessible label, on the
control itself. A row with a button keeps the name on the words and lets the
button name itself, as on the Mac. The suite counts, per file, that no file
makes more controls than it names.

**Nothing blocking on the main loop.** `ui::off_main` runs work on GIO's
blocking pool and hands the answer back to the main loop through a local
future, so the answer may touch widgets and the work may not. Every process
listing, sweep, keyring read, install, delete, open and pool pass goes
through it, and the suite fails on one that does not. Long-lived objects are
in `shared::Shared`, one per process, and a view that shows one of them
listens for its topic (`shared::listen`) and stops when the view is
destroyed.

**The tray's window.** A tray item cannot host a popover, so activating it
opens a small undecorated window titled "Claude Graft Menu", which closes
when it loses the focus. A libadwaita dialog is drawn inside its parent
window, so the conflict question asked from it keeps it open, where the Mac
had to use an `NSAlert` because a popover gives up key window to any sheet.
Wayland lets nobody place a window, so it opens where the compositor puts it.
The item also has a menu, with the conversation on screen and its accounts as
a radio group and then Open, Refresh Usage and Quit: that is the Mac's right
click, and it is the way in on a host that shows a menu on every click.

**The Claude in front.** Claude here runs under XWayland
(`--ozone-platform=x11`), so `front::watch_x11` follows `_NET_ACTIVE_WINDOW`
on the root window, reads the window's `_NET_WM_PID` and `WM_CLASS`, and a
process listing on the host matches the pid to a profile. Inside the Flatpak
the pid is the host's, which is what `flatpak-spawn --host ps` lists, so the
two agree. A Claude's class starts `ClaudeGraft.` for a grafted one and names
Claude for Claude's own; Graft's own class, or Graft's window being active
while the X server names nothing (a Graft drawn natively on Wayland is no X
window), is the Mac's "Graft coming forward leaves it where it was". The
lookup waits 150 ms first, because the X server reports focus moving to
Graft's window before GTK marks it active. With no X server at all the front
Claude is the running one whose own records were focused last, through the
pool's `RecordWatch`, which is the only word a pure Wayland session gives.

**What the item says.** A tray host draws an item's title and lays out the
item itself, so the Mac's pictures (the name on the icon's colour, the
lowered figure, the strip of lent accounts) are said in words: the title is
`[name] figure [conversation]`, the tooltip's heading is what the Mac's
spoken title says and its lines are the Mac's tooltip. The icon is the front
Claude's own preset, drawn by `graft_profiles::icon`; the account answering
the conversation on screen is the overlay icon, where the Mac lights it in a
red ring. Graft's own mark comes back when no Claude is shown. A tick moves
nothing: the text is handed over once per change, never on a timer, because
every new title is a bus signal and a relayout in the host, the cost the
Mac's ticker exists to avoid; and a host lays out its own items, so text can
never push the item out of reach and `keepReachable` has nothing to do.

**Colours.** A new Claude takes `IconPreset::next` over the pool's
`colours_in_use`, which counts every shortcut and every lent account that is
not also a Claude here, once, in `window::add_shortcut`.

**A running Claude's icon.** On Linux a grafted Claude does show its own
icon: the launcher passes `--class=ClaudeGraft.<id>`, the entry carries the
same `StartupWMClass`, and the dock matches the window to the entry. The
icon picker says so.

Still unproven on a real desktop: the tray item on KDE and on GNOME with the
AppIndicator extension, the X11 front-window watch under XWayland, the
dropdown's focus-loss close on each compositor, and the window fitting 820 by
560 with the fullest shortcut page. `linux/HANDOFF-app.md` has the steps.
