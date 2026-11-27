# Dictation

A key held in any app, what is said typed where the cursor is. Read this before changing anything named `Dictation…` in `Sources/App`. It is on the Mac alone, and off until somebody turns it on, on its own page in the window.

## The service

Claude Desktop's own dictation is not tied to a chat. Its window opens a WebSocket to `/api/ws/speech_to_text/voice_stream`, sends sound, and gets text back for the app to put in whichever box has focus; `conversation_uuid` and `organization_uuid` are optional parameters it adds when a chat happens to be there. Read out of Claude Desktop 6.28899.6 on 26 November 2026, and the command line client names the same path and the same two control messages.

Measured the same day against `api.anthropic.com` with the logins one profile held:

- With no login the handshake still upgrades; the service then sends an `error` frame, `permission_error` with `error_code` `account_session_invalid`, and closes with 1008.
- A bearer token carrying `user:inference` is accepted, with or without the other scopes, and with or without the `anthropic-beta` header. A `user:profile` token alone is refused the same way as no login. So dictation asks `ClaudeCredentials` for the scope Start Session asks for, and the usage token is no use to it.
- Sound is 16 kHz, one channel, sixteen-bit little-endian, as binary frames; `{"type":"KeepAlive"}` and `{"type":"CloseStream"}` are text frames. Back come `TranscriptInterim`, `TranscriptText` and `TranscriptEndpoint`, each with its words in `data`, and `TranscriptError`.
- A synthesized sentence of 5.8 seconds came back word for word as one `TranscriptText`, 0.7 seconds after `CloseStream`, followed by `TranscriptEndpoint` and a close with 1000. No interim frame arrived with the parameters `DictationWire.request` sent then.

**Guesses and corrections.** Claude Desktop asks with `forward_interims=typed`, and without it the service says nothing until an utterance is over. With it a `TranscriptInterim` carries the utterance being said as the service guesses it, a non-empty `TranscriptText` carries the same utterance corrected, and `TranscriptEndpoint` closes it. Desktop keeps one slot that each guess and each correction overwrites, appends the slot to its transcript at an endpoint, and appends whatever is left in it when the stream ends. `DictationTranscript` is that model: `spoken` is the settled utterances and the open one, and it is both what the pill shows while the key is down and what is typed. The pill grows upward to twenty lines before the oldest words give way to an ellipsis; at two, a recording of any length showed its last few words and dots for the rest. So the corrected words are typed whenever the service got to correct them, an utterance cut off before its correction is typed as it was guessed, and nothing waits for a correction beyond the stream's own end, which is where typing started before.

Not known: whether a recording counts against the account's usage, what limit the service puts on a recording's length or on how often one may be made, which languages it takes, and whether `TranscriptText` arrives once per utterance in a long recording or once at the end. `DictationTranscript` is written for either: utterances are joined in order, and an utterance that repeats everything settled so far replaces it. The path is private to Claude and can change without notice.

## Which login

The first that may be used: the Claude in front, then Claude's own profile, then the other Claudes in order, then accounts signed in here. The keychain is asked the way a usage pass asks it, once if shut and never again after a decline, so a key pressed in another app cannot put a dialog up every time. A refusal is said on screen and not retried with the next login.

## The key

Only keys that type nothing are offered. A monitor sees a key in another app and cannot keep it from that app, so a letter would be dictated over. Right Command is the default, asked for by the owner on 26 Nov: every Mac keyboard has one, where a MacBook's own has no right Control. The others are for people who make their shortcuts with the right Command.

The key is a modifier, and every shortcut that uses it begins exactly as dictation does. So the microphone opens with the press, because people speak as they press, and nothing is sent or shown until the press has outlasted a tap (`DictationGesture.tap`) or been doubled. Another key under the held one is a shortcut, and so is a click or a scroll: the recording is thrown away. A scroll still coasting from an earlier flick is not one. Hands-free, typing is no reason to stop and only Escape discards.

A release can go unseen. While a password field has the keyboard macOS hands key presses to no monitor, and a locked screen does the same, so the clock asks the keyboard itself whether the key is still down and takes three looks in a row as a release. For a modifier that question is put to the session's flags (`CGEventSource.flagsState`) and never to `CGEventSource.keyState`, which answers that a held modifier is up. Asked of `keyState`, every held recording ended 0.3 s after the press, with the log showing `dictation.began` and `dictation.ended` in the same millisecond and no characters; the owner hit it on the Air on 27 Nov with a real keyboard, and a simulated key showed the same. Only the F keys, which are not modifiers, are asked of `keyState`.

## When a recording ends by itself

At ten minutes, held or not; the last half minute is counted down on screen. After forty-five seconds with nothing louder than -50 dBFS, hands-free alone, since a held key is its own sign that somebody is there. When the microphone is unplugged or changed. Hands-free, when a password field takes the keyboard: no key reaches a monitor from there, the one that would finish the recording among them. Each of those types what was heard. Sleep, a locked screen, another person's session, the switch going off and a change of key discard it, and so the screen is kept from sleeping for as long as a recording lasts: read aloud hands-free, nothing else touches the Mac.

Sound is kept for at most fifteen seconds while the connection opens, which fails at eight by the stream's own clock. The request's timeout is longer than any recording, because the service sends nothing while it listens and a short one would count that silence as a dead connection. The login is fetched off the queue that stops the microphone: a keychain dialog left unanswered must not hold the microphone open behind it. After the close the words are waited for fifteen seconds and then whatever has arrived is used. Nothing said is written to disk, and the diagnostics log carries counts and reasons, never words.

## Typing

Words go in as key presses carrying text, twenty UTF-16 units to a press, with the modifier flags cleared so that a key still held cannot turn them into shortcuts. Never a paste: that would cost whatever had been copied. Each press is stamped (`DictationTyping.mark`) so the monitor knows its own. Everything is put on one line, since a line break typed into a chat box sends the message.

A space goes in front where the words would otherwise run into what is there. The app is asked for the one character before the caret; many say nothing, and then a space is added only when dictation was the last thing typed into that app within forty-five seconds.

A password field is never typed into. There, and wherever typing is not possible, the words go to the clipboard and the screen says so. The same when the person has moved to another app while the words were being written down: they belong to the app that was in front when the recording ended. With “Also copy what was said to the clipboard” on, which it is not until asked for, words that were typed are put on the clipboard as well; the clipboard is still never read, and typing still never goes through it. Until they are typed, Escape, sleep, a locked screen and the switch going off take them back.

## History

Off until somebody turns it on, on the Dictation page, where they also say how many to keep: the last 10, 50, 200 or 1000. `DictationHistory` keeps the words and the time of each dictation that was delivered, typed or put on the clipboard, in `dictation-history.json` beside the other files in Application Support, readable by its owner alone. Never the sound, which exists only in the frames sent to the service and is held nowhere. Nothing said at a password field is kept, since it may be the password, and a recording taken back with Escape was never delivered and so is never kept.

Turning the count down drops what is over it at once, and turning History off deletes the file; there is no keeping "for later" behind an off switch. The five files that record, send and type still write nothing to disk, and the suite holds them to it: the history is the one place words are kept, and it is its own file so that stays easy to see. The log still never carries the words.

This changed the app's promise from "keeps neither the sound nor the words" to "never keeps the sound, and keeps the words only if you turn History on". The page, the README and the release notes all say it that way; change one and change the others.

## Permissions

Accessibility, to see the key in other apps and to type into them, and the microphone. Both are asked from the switch on the Dictation page and never from a key press; the microphone is asked again whenever Graft comes to the front with the switch on and macOS not yet asked. A grant made in System Settings tells nobody, so the monitors are put in place again once trust appears, and the page asks again each time it is looked at.

Graft ships ad-hoc signed, so macOS knows each version by its code hash, as the keychain does. Expect both grants to need giving again after an update until releases are signed with a Developer ID; a release signed with the hardened runtime would also need the audio-input entitlement, which nothing passes to `codesign` today. After a copy with a different signature replaces the old one, the Accessibility switch in System Settings still shows on and is not honoured; switching it off and on did not cure it on the Air, removing the row and adding the app again did. The Dictation page says Accessibility is needed the whole time, which is the truth, but nothing tells the person the switch they are looking at is stale.

## Verification state

Run with a voice on the owner's MacBook Air on 27 November 2026 (macOS 27.2, a wired microphone, a build signed with a development certificate). The owner held the key and spoke, and double tapped and spoke, and both typed what was said into TextEdit; the socket opened with the borrowed login on the first try and answered within half a second of the release. That run found the held recording ending 0.3 s in, which "The key" above explains, and passed once the release check read the flags.

Guesses were asked for the next day, and the owner's own dictations on the Air, six of them in the log, each brought between 4 and 73 `TranscriptInterim` frames and exactly one correction, which came at the stop: 27 guesses and one correction for eleven seconds of speech, typed 0.67 s after the key came up. So the service corrects once, at the end, for a held recording, the pill shows the guesses the whole time, and what is typed is the correction. The owner saw the words in the pill and asked for more than the two lines it then had.

Still unproven, the history and the clipboard switch with a voice, the list of Claudes under a real pointer, and steps 5 to 13 of `Tests/manual/dictation.md`: Escape, a shortcut and a click under the key, a stray tap, the silence limit, a password field, no network, another key, a locked screen, and taking words back. A simulated key cannot settle them on a Mac somebody is using: the keyboard's real state overwrites the simulated modifier, so a simulated hold ends early or late by itself. Also unproven: typing into a terminal and into Claude's own message box.

One cost is known and not yet weighed on a device. The microphone opens with every press of the key, shortcuts included, so the microphone light flickers at each right Command shortcut and a Bluetooth headset may drop to its call sound for that moment. Opening it only once the press has outlasted a tap would lose the first third of a second of speech.
