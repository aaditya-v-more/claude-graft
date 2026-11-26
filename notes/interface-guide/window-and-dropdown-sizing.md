# Window and dropdown sizing

[Back to interface.md](../interface.md)

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

The sidebar's foot is the opposite case: stacked under the list, not inset over
it. As an inset it had no backdrop, the update line being clear on purpose, and
with enough shortcuts to scroll the rows showed through the version and the
links. The layout check holds the list's scroll view above the foot.

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

The Accounts page is one row for each account, Claudes included: its place in
the order, its week, a Move menu and its Auto-start switch. As two lists, one
for the order and one for the switches, it was 31 points over at three
accounts and twice the height at twenty. A switch in a grouped form's row takes
the whole row for its label, which left the rows with no name in them, so the
switch hides its label and a caption beside it says what it is. That switch
reached assistive apps with its name and its value and no press of its own
(read on macOS 27.2, 26 November 2026: a pointer turned it and nothing else
could), so `AutoSessionToggle` gives it one, and `./test-layout.sh` now asks
every Auto-start for it. The sidebar
gave up its second line for the pages that belong to every account — Artifacts,
Projects, Search Chats and Accounts are one line each under "Every account" —
which is what leaves room for the Accounts row to be there always. The layout
suite draws twenty Claudes: every row keeps its name, switch and menu inside
the window, and the sidebar scrolls above its foot.

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

## Fitting the dropdown

**The account list scrolls; nothing under it does.** The dropdown grew a row
of three bars for every account, and someone with seven of them found it
running past the bottom of the screen: an `NSPopover` taller than its screen
is not shrunk, so the switches, Open Claude Graft and Quit were simply gone,
and with them the only way into Settings. `DropdownColumn` stacks the dropdown
in three parts and measures each; when the list needs more than the screen
leaves after the other two, only the list is put in a `ScrollView`, held to
what is left. The parts above and below are what people open the dropdown to
reach, and the list is what grows.

The limit is the status item's screen's `visibleFrame` less 40 points for the
arrow and the popover's inset, passed in by `MenuBarController` when it opens
the dropdown. The list keeps at least `leastList` whatever the rest takes, so
a long notice cannot leave no account in view.

A row's heading tries three shapes and takes the first that fits
(`ViewThatFits`): name, plan and both buttons on a line; the same without the
plan; and the name over the buttons. Russian's "Запустить сессию" left "Claude
2" wrapped onto two lines and the button cut short, and with the buttons held
whole the name was cut instead. Auto-start is a small checkbox at the end of
the five-hour bar's reset line, laid over it and outside `UsageBar`, whose
stack is replaced for accessibility by one progress indicator; beside the
buttons, where it was asked for, Russian had no width for it.

A list that fits is never wrapped: a `ScrollView` states no height of its own
to a popover, which sizes itself from what its content asks for, so wrapping
it always would need the measured height before the first frame and open the
dropdown at nothing for a moment. Nor does the scroll view carry an
identifier; one on a container is inherited by every control inside it. The
layout suite checks thirty accounts against a 400-point limit and a few against
their own height.
