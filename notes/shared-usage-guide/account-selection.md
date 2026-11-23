# Account selection

[Back to shared-usage.md](../shared-usage.md)

## Which account answers

**Only the sender and the accounts it names are asked,** one account counted
once. An account has room while its login has not been refused, the service has
not said no, its five-hour window is under 98% and its week under 99%, a window
whose reset has passed counting as empty. Those with room go in order of the
soonest weekly reset, an unknown reset after every known one, and a tie to the
sender, since staying costs nothing. The sender's own account takes its place in
that order while it has room and goes last when it has none; the request then
goes out as Claude sent it and the service decides.

Whatever its rank, the sender's account is also the last attempt, asked a
second time when it ranked first and was found full, because the last answer is
the one delivered and a lent account's answer is about a login Claude Code
never sent: a 401 for a lapsed lent login, a 403 for a model its plan lacks, a
429 with its own reset times. Until 2.0.1 the sender was appended only when it
had no room, so a sender ranked first — a sooner reset, or nothing known about
either account — and full on what the Chat tab had spent unseen passed the
request to the lender as the last attempt, and Claude Code got whatever the
lender said.

**A conversation stays while its cache lives,** unless it is held longer for its
reasoning, below. Keyed by
`X-Claude-Code-Session-Id`, it stays with the account that last answered it for
as long as its prompt cache lives there — five minutes from its last turn, or an
hour when the body asks for `"ttl":"1h"` — because moving it inside that time
pays for the whole context again, and moving it after costs nothing. It used to
stay an hour regardless, which held a conversation on an account long after
there was anything left to lose by moving it. A choice made in the dropdown goes
in front of both while that account has room, and is dropped once the
conversation has been quiet for a day. The relay learns limits from the
`anthropic-ratelimit-unified-*` headers on its own answers — fractions, resets
in Unix seconds — and Graft pushes what the usage endpoint said with every
configuration, so an account not yet asked is not a blank.

**A conversation carrying reasoning stays where the reasoning was written.**
Every request in a conversation carries back the reasoning the model wrote on
earlier turns, and for the models whose reasoning is bound to the account that
wrote it, the service honours those blocks only from that account. From any
other, in both directions, they are dropped without a word: the answer is still
a 200, and nothing reaches Claude Code. Measured by replaying unedited
conversations from both accounts on this machine and counting what the service
put back — input and cache tokens of the real request against `count_tokens` of
the same body with the reasoning taken out: kept on the writing account every
time, dropped on the other twelve times in twelve, and dropped again through
Desktop's own Claude Code resuming the transcript under the other profile. The
binding follows the token, not the body — rewriting `metadata.user_id` to the
borrower's own, or sending none, still drops it — and it follows the block, not
the model reading it, so nothing the relay could edit would save it. A model
measured keeping its reasoning across the move is listed in
`PoolRequest.reasoningTravels`; any other is taken to be bound, since holding one
needlessly costs a little sharing and moving one costs its reasoning.

So a request whose messages hold `thinking` or `redacted_thinking` blocks stays
with the account that last answered that conversation, past its cache and
whatever the ranking says, and moves only when that account has no room — then
stays where it went, so the reasoning is lost once rather than on every turn.
Losing it once costs a few percent more output at low effort and a fifth more
at the highest; losing it every turn costs up to two thirds. A conversation the
relay has not seen answered, from before it last started or with no session id,
stays on the sender's account, where it began, and so does one last answered by
an account no longer lent to it. The account that answered is held under the
name the relay now counts it by: when a Claude's own login lapses and the same
account signed in from the pane lends in its place, the name that answered is
no longer a candidate, and holding it by that name let the conversation go by
the ranking instead, away from an account that still had room. A request with no reasoning in it
has nothing to lose — a new conversation, or one just compacted — and is routed
like any other, which is where sharing still spreads the load. A choice from the
dropdown still moves a held conversation: a person asking is the one move, made
on purpose, and the conversation then stays on that account. The relay does not
add the header that would make the service report a drop, since nothing that
cannot change accounts is altered on the way.

**A conversation kept as a thread stays where the thread is.** Claude Code
2.1.317 keeps each conversation as a thread on the service: the first request
says `"thread":{"type":"create"}` and carries everything, and each one after
says `"thread":{"type":"continue","previous_message_id":…}` and carries only
the turns since. The rest is stored with the account that answered, and any
other account answers 404 `thread_not_found`; Claude Code takes that as the
thread having lapsed and sends the whole conversation again as a new one. The
first live run on two accounts found every moved turn refused this way — the
relay took the 404 for a plan that lacked something and sent the request back
where it came from, so a move from the dropdown never stuck, while a lapsed
thread on the sender's own account was recovered as designed. So a continuation
is held like reasoning, whatever the model, and when the dropdown or a full
account does move one, a `thread_not_found` from where it went is handed to
Claude Code rather than sent home: the new thread that follows is created on
the account the conversation moved to, and stays there. Telling that refusal
from any other 404 means reading it, and the service compresses even its
errors for a client that accepts it — a plain 401 comes back as brotli — so the
second live run's moved turns were sent home all the same. A continued thread
going to a borrowed account now asks for `Accept-Encoding: identity`; nothing
else about the answer changes, and a refusal still compressed is treated as
before. A refusal read and found to be something else leaves its error type on
the `relay passed over` line.

Only the conversation's own turns carry a thread. Claude Code asks for a title
and the like under the same session id with none, often of a smaller model,
and those were overwriting which account the relay remembered for the
conversation — the dropdown named the account that answered the title, and
the next turn was steered there to be refused. Once a session has been seen
with a thread, only an answer to a request with one says where it is.

What comes back decides what happens next, from the status line alone. A 429
marks the account full until the reset it names and the request goes on to the
next; a 401 on a borrowed login marks that login refused until Graft hands over
another; a 400, 403 or 404 on a borrowed login sends the request home — to the
sender's next attempt, not to the last in line, which with three accounts
ranked lender, sender, lender was the other lender — since
something in it belongs to the sender or the plan lacks it — except a 404 that
says `thread_not_found` to a continued thread, above, which is read and
delivered; anything else is delivered. Measured: with Claude 2's login swapped for a bogus one, the first
request was refused, retried on Claude's own account, and Claude Code saw only
the answer.

**Some requests never move.** A body naming `"file_id":`, `"container":` or
`"mcp_servers":` refers to an uploaded file, a code execution container or a
connector Anthropic runs, and each belongs to one organization; another account
asked for it answers that it does not exist. A body whose `Content-Encoding`
is not identity cannot be read to find out, so it stays too. Keys are matched
with their closing quote and colon, which JSON never escapes, so the same word
in a prompt does not pin anything — and with the start of a value that makes
them a reference (`"file_id":"`, `"container":"` or `"container":{"id":"`,
`"mcp_servers":[{`), because a connector's tool schema can have a property of
the same name. One does on this machine, a `"container":{"type":"object",…}`,
and every request listing the tools carried it: the third live run found that
pinned each full request to its sender, so the new thread Claude Code sends
after `thread_not_found` went home and a move from the dropdown never landed,
while the short continuations that leave the tools to the thread moved freely.
