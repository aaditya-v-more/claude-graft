# Tools and safety

[Back to HANDOFF.md](../HANDOFF.md)

## Tools

- `./handoff/shared-usage/check.sh` (read-only): the Grafts running, the relay,
  each profile's Code environment and the lenders.
- `./handoff/menu-bar-cpu/cpu.sh <seconds> <label>` (read-only).
  - It first gives you 15 seconds to bring forward what the step names and let
    the bar catch up.
  - It then prints the share of one core that Graft, its relay, ControlCenter
    and the WindowServer used over the stretch, with each one's memory. For
    Graft and the relay it also prints which build is running.
  - Each result is also added to `cpu.txt` beside it.
  - Start it, bring forward what the step says, then touch nothing until it
    prints.
- **The item's frame and spoken title**, read-only, using Graft's PID from
  `check.sh`:

      osascript -e 'tell application "System Events" to tell (first process whose unix id is <PID>) to get {position, size, description} of every menu bar item of menu bar 2'

- **Ten seconds of the bar:**

      sleep 5; screencapture -x -V 10 -R <x>,0,<width>,40 handoff/menu-bar-cpu/<name>.mov

  - `<x>` is 40 points left of Graft's leftmost item in the frame command's
    output. `<width>` reaches 40 points past its right edge.
  - The five seconds are for bringing a Claude forward after pressing Return.
  - If no file appears, drop `-R …` to record the whole screen, and note it.
  - If the recording shows only wallpaper, the terminal lacks Screen
    Recording.
- **Diagnostics:**
  `grep -E '"(pool\.|relay|menu\.)' ~/Library/Application\ Support/ClaudeGraft/diagnostics.log | tail`

Save every output and recording in this folder as `step-…`, with the time each
was taken. Git does not ignore them, so commit none of them.

## Do not touch

- **Chats:** only the two scratch sessions in step 3 are created. No other
  chat is opened or written to.
- **Claudes:** Claude's main profile is never quit. Only Claude 2 is opened
  and quit, and it ends open or closed as step 0 found it.
- **Settings:** leave Open at Login, **Update Claude…**, any Graft update and
  the hold setting alone.
- **The menu bar:** don't move, hide or rearrange its items. Change neither the
  appearance nor the wallpaper, since either changes what the bar draws.
- **Repository:** nothing is committed, pushed or released. Create or switch
  no branch.
- **Credentials:** a keychain prompt for "Claude Safe Storage" is Aaditya's to
  answer, with **Allow** (the one-time option). Record which step it came at.
- **Sharing:** the only change allowed is Claude 2 borrowing Claude. Step 2
  sets that up if it isn't already, and the put-back undoes it if step 2 set it
  up.
