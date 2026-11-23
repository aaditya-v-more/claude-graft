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

## Topic guides

- <a id="fitting-the-window"></a><a id="fitting-the-dropdown"></a>[Window and dropdown sizing](interface-guide/window-and-dropdown-sizing.md)
- <a id="the-dock-the-icon-and-the-claude-in-front"></a><a id="the-help-menu"></a><a id="windows-a-capped-column-inside-a-scrollviewer"></a><a id="windows-textchanged-comes-late"></a><a id="windows-the-title-bar-copies-the-pages-heading"></a>[Dock and platform details](interface-guide/dock-and-platform-details.md)
- <a id="windows-russian"></a>[Windows in Russian](interface-guide/windows-russian.md)
