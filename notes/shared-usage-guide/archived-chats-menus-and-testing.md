# Archived chats, menus and testing

[Back to shared-usage.md](../shared-usage.md)

**A conversation put away leaves the bar and the dropdown.** Archived means
every record its own Claude keeps for it says `isArchived`. A copy in a Claude
that shares the chats is not asked: the mirror carries a change across only on
its next pass, and read first, a copy kept a deleted conversation on the bar.
Deleted is a `deleted_` marker naming the session, which Claude writes only for
a record Graft filed, since those are named for their session; or a record seen
before and missing on two passes running, since one pass can land while Claude
rewrites the file. A store that will not open hides nothing, and the walk skips
stashes as the sweep does, or a deleted conversation's old record there would
keep it listed. Deleting the conversation on screen takes the latest focus with
its record, so the focus it had stays the word until Claude writes a newer one;
before that, the bar named the conversation focused before it. Once that record
has been missing on two passes running, the conversation is taken as deleted
and the bar names nothing, or one from before sharing would stay there by its
title. One pass is not enough, because the watch can wake a pass before
Claude's write has put the new file in place. A conversation
deleted while Graft was not running was never seen with a record, and the
dropdown goes on listing it for the rest of its half hour.

**The account menu marks the choice in force.** It is an inline `Picker`
inside the row's `Menu`, which draws macOS's own checkmark; a `Label` with a
`checkmark` image in a plain `Menu` loses the image. Automatic names the account
the ranking settled on, "Automatic · now Claude 2", and the row reads
"Auto · Claude 2" against a chosen account's bare name, because a conversation
answered by Claude 2 looked the same whether someone had chosen Claude 2 or the
ranking had. The right click's menu says the same.

The text gives way when the bar has no room for it. The wider item is not
refused by macOS but put past the edge of the screen, still reporting itself
visible, and on the development machine's MacBook Air, with a full bar beside
the notch, Graft's item vanished for the two minutes the text ran, refused a
right click as offscreen, and came back when the text went. Half a second after
the text goes in or grows, `keepReachable` asks `isShowing`; if the item was
reachable before and is not now, the text comes out for that conversation and
`menu.ticker withdrawn` is logged. The words stay in the item's spoken title
and the right click still moves the conversation, because those need no room.
The next run showed where the item had gone. It wasn't past the edge but
behind the notch: with Claude 2's icon in the bar too, the text widened Graft's
item to x 866–1054, and this MacBook Air's notch covers 751–959. Every
edge-of-screen test said yes, so `MenuBarPlacement` now treats any overlap with
a notch (the gap between `auxiliaryTopLeftArea` and `auxiliaryTopRightArea`) as
out of reach. That run's screenshots show no Graft item at all, even before the
text. The capture tool filters out apps it wasn't granted, so a check of the bar
needs `screencapture` instead.

The run after the notch fix saw the text come out about half a second after it
went in, and the item stay at x 1025, clear of the notch. A real right click
opened the account menu and moved the conversation. But the menu had been popped
up at a point inside the status button, which is not flipped, so the point lay
above the bar. The menu opened with its top above the screen and the
conversation's title behind a scroll arrow. It is now set as the item's menu for
that one click, so macOS places it as it places any menu-bar menu.

The relay is asked every three seconds while the dropdown is open and every
thirty otherwise, over its socket and never the service. `MenuBarController`
starts that watch as the popover shows and ends it in `popoverDidClose`,
because a view inside a popover is not reliably told it has gone, and a watch
nobody ends keeps the fast timer running for good.

## Testing it

`./test-relay.sh` builds the relay and drives it over loopback against a
stand-in for the service: which account each request reached, lending per
Claude, choices from the dropdown, the hold for as long as a cache lives, one
account counted once, and one relay handing over to the next. The suite drives
the environment, the swap a launcher makes and every refusal over sealed files
in a throwaway directory. The layout suite draws the pane and the dropdown's
conversations from fixtures through `UsagePoolController.attach`, which hands
over the store and the usage monitor and starts nothing — no relay, no timer, no
file. Rows are identified by what stays put under them, a lender by its profile
folder or account id and a conversation by its session id, since a shortcut can
be renamed and a conversation retitled while a script is still asking for it.

What has run against real accounts, and what is still only a plan, is in
[diagnostics-and-verification.md](../diagnostics-and-verification.md).

## Artifact tool arguments are not inference containers

A historical artifact call can have a string-valued `input.container` containing
a project reference. Scanning every byte for `"container":"` mistakes that local
tool argument for a server-side inference container and keeps all subsequent
turns on the sender, even after the user selects another account. Inspect the
request envelope for `container`; keep real file and server-side MCP references
pinned. The relay regression sends a historical artifact tool call through the
stand-in and checks that the other account answers, while real containers stay
on their owner.

Windows reads the request the same way (`PoolRequest.MustStayWithSender` in
`PoolPolicy.cs`), and a body that does not parse stays with its sender on both.
