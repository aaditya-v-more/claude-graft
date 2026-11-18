# A conversation that made an artifact can still move between accounts

Platforms: macOS (15 or later), Windows
Needs: two signed-in accounts (Claude and Test A)
Setup: Graft running, Test A closed and borrowing Claude (shared-usage.md
steps 1–3), so Test A's Code tab is answered through the relay.

1. Open Test A. In its Code tab start a new session in an empty scratch folder
   and ask: `Make an artifact: a one-line HTML page that says hello.`
   Expect: the answer arrives. Find the session's transcript (the newest
   `.jsonl` under `~/.claude/projects/`, or `%USERPROFILE%\.claude\projects\`
   on Windows) and check whether a tool call in it carries a `"container"`
   argument. If none does, write "not applicable: no artifact tool call" for
   steps 2–3 and stop here.
2. In the dropdown or flyout, choose for this conversation the account that
   did not answer step 1. Send `Reply with the word moved.`
   Expect: the answer arrives, and the account shown for the conversation is
   the one chosen.
3. Send `Reply with the word again.`
   Expect: the same account answers again; the conversation does not go back
   to the sender's account on its own. Then choose Automatic, quit Test A, and
   take the lending out as shared-usage.md step 6 does.

Pass when: a conversation carrying an artifact tool call moves to the chosen
account and stays there.
