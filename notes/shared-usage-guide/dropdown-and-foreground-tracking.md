# Dropdown and foreground tracking

[Back to shared-usage.md](../shared-usage.md)

## The dropdown

**The dropdown lists every account once unless told otherwise,** and by Claude
on the switch above the list (`usageByAccount`, on unless turned off). The
switch shows only while something is shared: with nothing lent the two are the
same list. By Claude was the default for a day, on the reasoning that the
question asked is what the Claude in use has left; the owner, using it, asked
for the list of accounts back as the default (26 November 2026), since the
same account repeated under each Claude made the list long before it said
anything new.

By Claude, each Claude is a line with its name, Start Session and Open
(`ClaudeHeading`), and under it come its own account and then the accounts it
draws on (`drawnOn(by:)`, its `members` without itself), every one the same
row (`ClaudeAccountRow`). The first version drew the Claude's own account as
the old tall row and the lent ones as short rows under it, and the owner's
first look at it was that three accounts in one list should look the same. An
account lent to two Claudes is listed under both. Start Session is on the
Claude's line and starts its own account; a button on every row was asked to
go, so an account with no Claude of its own is started from the list by
account or from Accounts.

**Both lists draw an account the same way,** in three lines: who it is, its
windows three abreast (`UsageMeters`, each a `UsageMeter` with the figure over
the bar and the reset under it), then Auto-start, when the figure was taken
and the refresh (`AccountFoot`). The meters are always thirds, so a week
stands under the week above it on an account without the other two. One bar
to a line with eleven-point text was five lines an account, hard to read and
mostly empty at the right. The list's scroll bar is forced to the overlay kind
(`OverlayScroller`): with a mouse attached macOS gave it a gutter of its own.

A Claude's line carries its icon in both lists, beside the dot that says it is
running. A name too long to share the line with Start Session and Open takes a
line of its own with the buttons under it, cut in the middle if it is longer
than the row; a plan beside it is never squeezed. In Russian the two buttons
leave a name about nine letters, so "Claude 2" still shares the line there.

Auto-start goes by the account, not by the row (`autoStartKey`): an account
that is also a Claude here uses that Claude's switch, so turning it under one
Claude turns it under every other, and in Shared usage. A lent account's row
is identified as `dropdown.<Claude's folder>.<lender>.<control>`, the lender
being a signed-in account's saved id or another Claude's folder; the Claude's
own keeps `dropdown.<Claude's folder>.<control>`, as in the list by account.

**The dropdown lists what the relay carried.** The relay keeps the
conversations it has seen by `X-Claude-Code-Session-Id`, which is the
`cliSessionId` a record names, so a title comes from that record or, before one
exists, from the title line in the transcript. Listed are conversations from a
Claude with something to borrow, active in the last half hour and not put away,
the Claude in front first and five at most.

That list lives in the relay's memory and nowhere else, so a relay replaced by
an update starts it empty. The live run on 9 November saw the conversation on
screen go from ringed to named alone the moment the new relay took over; its
next message through the new one brings the ring back. The choices made from
the dropdown are not lost with it, since Graft keeps them in
`usage-sharing.json` and configures the new relay with them. Handing the list
over was weighed and left: it is lost once per update, for one message.

**The figure in the bar is the answering account's.** It used to be the Claude
in front's own, whoever answered, and with no Claude named the tightest window
among the Claudes alone. So a Claude drawing on an account signed in through
Graft showed its own untouched figure while that account ran down unseen, and
such an account never reached the bar at all, only its tooltip. Now, beside a
Claude in front, the figure belongs to the account answering the conversation
on its screen — the one the strip lights, found among that Claude's own names
for its accounts by `UsagePoolController.answering`, since the relay may know
an account by another name — and it is that Claude's own until something says
who answers. With no Claude named, accounts signed in here are weighed beside
the Claudes (`accountsWeighed`). A Claude is open while it runs; an account
with no Claude is open while it answers a conversation heard from in the last
half hour, in a Claude that is running (`spent`), so one sitting full and
answering nothing does not shout over the account in use. The spoken title says
the Claude's name before a figure that is another account's, and the tooltip
names an account that is also a Claude here once.

**Which Claude is in front is `FrontmostClaude`'s answer,** the one the status
item draws. Matching a Claude to its profile costs a `ps`, and the list used to
run its own on every activation beside the one the status item was already
paying for. Its answer is also the right one for a dropdown: Graft coming
forward leaves it where it was, so opening the dropdown keeps the Claude the
person was just using at the top.

**The conversation on screen runs across the menu bar** for as long as it is
there: its title and the account answering it, and a right click on the item
moves it with the same choices as its row. Until
2.0.1 it was only a conversation that had just begun, for its first two
minutes, and a person who switched chats saw the bar go on naming one no longer
in front, or "New session" for a test chat begun minutes earlier.

Which conversation is on screen is Claude's word alone. It writes
`lastFocusedAt`, in milliseconds, into a conversation's record the moment its
window switches to it, so the bar names the conversation the front Claude's
store focused last. One the relay has not carried — an old chat opened to read,
which has sent nothing since sharing began — is named by its record's title
alone, with nothing lit and no account beside it, since none has answered it.
The first 2.0.1 candidate named nothing there, and on a machine whose chats
mostly began before 2.0 the text went whenever one of them was opened. Keeping
the conversation before instead would have named the one thing certain not to
be on screen.
One the relay did carry is left to its row, which an archived or deleted one
has already left; named from its record, it would be back on the bar. A Claude
whose records say nothing of focus falls back on the
conversation typed into last, `promptedAt`: a turn on a conversation's thread
whose last user message is not a tool's answer. A lone message with no thread
does not count, because the title Claude Code asks for beside a conversation is
exactly that, and a first message is marked by `startedAt` anyway, set only
when the first request the relay sees for a session carries no answer — a relay
restarted part way through sees every conversation for the first time. Each
change of conversation is logged as `menu.chat`, with the first eight
characters of its session, whether an account was chosen for it and whether
the relay carried it, so a run can be checked against what was on screen.

A new chat is the one wait the bar cannot shorten. Claude's first record of it
lands about three seconds after its first message and already carries its
title, so there is no "New chat" stage to show, and until it lands the bar goes
on naming the chat before. The relay sees that first message at once, but a
conversation beginning is not always the one on screen: a scheduled task, or a
session another one started, begins the same way, and only the records say
which conversation the window shows. The second live run timed it: each new
chat's name was on the bar within a tenth of a second of Claude's first record.

**A focus belongs to the store it turned up in first.** Claudes that share
chats each keep a mirrored folder, and the mirror copies a changed record
across with its `lastFocusedAt`. Read as the second store's own, the copy made
the conversation on one Claude's screen the newest focus in the other's, and
once old chats were named from their records, the other Claude's bar would
have named it too. So each focus, a session and the moment it was focused, is
held against the stores that had it when it was first seen, and a Claude's
focus is the newest of its own. On the first pass after Graft starts every
store holding a focus owns it, as every store did before; a copy made after
that is told apart. The mirror can also write the other Claude's copy of a
record over this one's, and an older focus with it. The focus this store had
then stays the word, as for a deleted record, since this Claude still shows
that conversation.

The text is drawn into a picture of fixed width and scrolled inside it, because
text scrolled a character at a time changes width with every letter, and a
status item that changes width moves every item to its left with it. Following
the screen costs a look at each record's modification date while anything is
shared, with a record parsed again only when it changed and a missing
transcript looked for once a minute; a full pass reads logins as well, which is
why that one waits half a minute. The look ran every two seconds, and a switch
took up to four to reach the bar: Claude writes the record about a second after
its window changes conversation, and the look could come two seconds after
that. `StoreWatch` now has FSEvents say when a store changes, a tenth of a
second after it does, which leaves Claude's own second as the wait. It leaves
out Graft's own writes, since the sweep files records and the mirror copies
them and neither says what is on screen; the suite wakes it with `touch` for
that reason. A look woken while another is under way runs as soon as that one
ends, and the two-second look stays for the relay's news, which no file
carries. The timer, not a look, decides which stores are watched, because the
watch itself wakes a look and must not be torn down from inside its own call.

**What following the screen costs.** Since 2.0 the text runs whenever a
conversation is on screen, so whatever a tick costs is paid all day. Each tick
used to hand the item a new title: the text measured again, a new picture, the
title rebuilt, and then AppKit measuring the item, giving the bar its new
length and rebuilding the spoken title, twelve and a half times a second. With
the look's walk beside it, that held 2.0.1 at about 9% of a core on the
development machine on 9 November, with nothing else asked of it. Now the text
does not move by Graft's hand at all. Moving it by redrawing the button's end
on every tick, with the title, length and spoken title left alone, cut the
moving cost on the Mac Mini only from 4.8 points of a core over idle to 3.7:
AppKit drawing the button was nearly all of it, and drawing the text was
little. So text too long for the width leaves the title a blank picture of the
same width, `NewChatTicker.Picture`'s `image`, and runs in its `strip`, a
clipped view laid over the blank, as a plain layer holding the text twice with
the gap between. A discrete keyframe animation on that layer's position holds
the start for the pause and then steps it at the old speed, so the render
server moves the text and Graft wakes for none of it; the main thread has no
timer and the button is never asked to draw again. The layer is drawn once, and
again only when the bar's appearance or the display's scale changes. It is a
layer rather than a view because AppKit draws a layer-backed view only as far
as it can be seen, which left the text blank past its first 150 points. A
picture rebuilt for the same conversation, new words for its account, carries
on from where the pass was (`tickerStarted`); a new conversation starts its
pass again. Text short enough to stand still is drawn into the title as before.

Where to lay the strip is not published, and working it out failed. Laying
the title out as the button's cell does, centred in its title rect with the
blank at its baseline and moved to the next whole pixel, matched the old
picture on five titles of different widths, all without an icon. The third run
on the Mac Mini, with the icons in front, found still text a pixel above the
same text moving: the icons make the line taller than the text, the title rect
then starts on a half point, and AppKit rounds that half point the other way.
Fitting a rounding rule to nine title shapes still left one wrong, so the rule
was dropped. `Picture.place` instead draws the title once into a picture with
the blank filled in magenta, and lays the strip where the first row the magenta
fills across the whole width begins. Nothing else in a title is 150 points
wide. Over the same nine shapes in the bar, each moving title then started on
the row the still one did.

**The mark is never given to the button itself.** Up to 2.2.1 the blank in the
real title drew itself magenta for the length of a `cacheDisplay` of the real
button. On macOS 27 that call renders the button's layer, and a layer waiting
to be drawn is drawn for the screen first, inside the call, mark and all
(`-[NSViewBackingLayer display]` under `renderInContext:`, read off the stack):
a block of magenta stayed on the bar behind the moving text until something
next changed the title. All it took was a title set again between a drawing
and the look that follows it, which any pass can do. Now a copy of the
button's cell is given a copy of the title whose last picture is a solid one,
and draws it into a bitmap of Graft's own, so nothing attached to the bar can
draw the mark whatever AppKit chooses to draw, and when. On 26 November 2026,
on a status item on the development machine (macOS 27.2.1, both displays 1x),
the copy found the same corner as the real button drawn marked on twelve title
shapes, at one, two and three times scale, the larger two drawn into a picture
and not shown. Copying the cell can ask the button to draw once more, and the
look after that drawing finds the strip already placed. A plain button, as the
suite builds one, rounds a centred title to whole pixels when it draws itself
and the copy does not, so there the column can be a point out and the check
allows it; the row, which is what the icons once threw off, agreed everywhere.
A twin button drawn by AppKit in a window of its own matched the plain button
and stood a point low for the status item, whose button the bar itself places.
Older systems have not been looked at.

The blank is kept out of the image cache (`cacheMode = .never`) so each drawing
of the button is heard of, and still text with it so the two are drawn the same
way; the button draws either only when it draws itself, which is not on any
tick. Its handler calls `place` a turn of the run loop later, so the measuring
uses the geometry the title was drawn in. A title of the same pieces, size and
scale it has already placed for is not measured again. The title is built anew
on every pass, so it is compared by its words, its fonts and the sizes of its
pictures rather than as an object, which never matched and had every pass
measure again. A title no longer ending in that blank hides the strip. Captured from the bar, the
strip and the old picture differed by at most 19 of 255 in any channel, all on
the text's edges, which is the blending of a separate layer. The fourth run on the Mac Mini
saw still and moving text begin on the same row and column as 2.0.1's. Only 1x
has been measured on a screen. At 2x the picture is in the button's own pixels,
so the same reading holds, unchecked on glass.

The walk dates each
record with one `lstat` on a string path (`Graft.modificationDate(atPath:)`),
since `attributesOfItem` builds a dictionary of every attribute: over the 699
records here it took 20 ms a look against 2.5, and the walk was close to a
third of the samples in a profile of the running app. Neither follows a link.
