# Dictation

A key held in any app, what is said typed where the cursor is. Read this before changing anything named `Dictation…` in `Sources/App`. It is on the Mac alone, and off until somebody turns it on in Settings.

## The service

Claude Desktop's own dictation is not tied to a chat. Its window opens a WebSocket to `/api/ws/speech_to_text/voice_stream`, sends sound, and gets text back for the app to put in whichever box has focus; `conversation_uuid` and `organization_uuid` are optional parameters it adds when a chat happens to be there. Read out of Claude Desktop 6.28899.6 on 26 November 2026, and the command line client names the same path and the same two control messages.

Measured the same day against `api.anthropic.com` with the logins one profile held:

- With no login the handshake still upgrades; the service then sends an `error` frame, `permission_error` with `error_code` `account_session_invalid`, and closes with 1008.
- A bearer token carrying `user:inference` is accepted, with or without the other scopes, and with or without the `anthropic-beta` header. A `user:profile` token alone is refused the same way as no login. So dictation asks `ClaudeCredentials` for the scope Start Session asks for, and the usage token is no use to it.
- Sound is 16 kHz, one channel, sixteen-bit little-endian, as binary frames; `{"type":"KeepAlive"}` and `{"type":"CloseStream"}` are text frames. Back come `TranscriptInterim`, `TranscriptText` and `TranscriptEndpoint`, each with its words in `data`, and `TranscriptError`.
- A synthesized sentence of 5.8 seconds came back word for word as one `TranscriptText`, 0.7 seconds after `CloseStream`, followed by `TranscriptEndpoint` and a close with 1000. No interim frame arrived with the parameters `DictationWire.request` sends.

Not known: whether a recording counts against the account's usage, what limit the service puts on a recording's length or on how often one may be made, which languages it takes, and whether `TranscriptText` arrives once per utterance in a long recording or once at the end. `DictationTranscript` is written for either: utterances are joined in order, and a frame that repeats everything so far replaces it. The path is private to Claude and can change without notice.

## Which login

The first that may be used: the Claude in front, then Claude's own profile, then the other Claudes in order, then accounts signed in here. The keychain is asked the way a usage pass asks it, once if shut and never again after a decline, so a key pressed in another app cannot put a dialog up every time. A refusal is said on screen and not retried with the next login.

## The key

Only keys that type nothing are offered. A monitor sees a key in another app and cannot keep it from that app, so a letter would be dictated over. Right Command is the default, asked for by the owner on 26 Nov: every Mac keyboard has one, where a MacBook's own has no right Control. The others are for people who make their shortcuts with the right Command.

The key is a modifier, and every shortcut that uses it begins exactly as dictation does. So the microphone opens with the press, because people speak as they press, and nothing is sent or shown until the press has outlasted a tap (`DictationGesture.tap`) or been doubled. Another key under the held one is a shortcut, and so is a click or a scroll: the recording is thrown away. A scroll still coasting from an earlier flick is not one. Hands-free, typing is no reason to stop and only Escape discards.

A release can go unseen. While a password field has the keyboard macOS hands key presses to no monitor, and a locked screen does the same, so the clock asks the keyboard itself whether the key is still down and takes three looks in a row as a release.

## When a recording ends by itself

At ten minutes, held or not; the last half minute is counted down on screen. After forty-five seconds with nothing louder than -50 dBFS, hands-free alone, since a held key is its own sign that somebody is there. When the microphone is unplugged or changed. Hands-free, when a password field takes the keyboard: no key reaches a monitor from there, the one that would finish the recording among them. Each of those types what was heard. Sleep, a locked screen, another person's session, the switch going off and a change of key discard it, and so the screen is kept from sleeping for as long as a recording lasts: read aloud hands-free, nothing else touches the Mac.

Sound is kept for at most fifteen seconds while the connection opens, which fails at eight by the stream's own clock. The request's timeout is longer than any recording, because the service sends nothing while it listens and a short one would count that silence as a dead connection. The login is fetched off the queue that stops the microphone: a keychain dialog left unanswered must not hold the microphone open behind it. After the close the words are waited for fifteen seconds and then whatever has arrived is used. Nothing said is written to disk, and the diagnostics log carries counts and reasons, never words.

## Typing

Words go in as key presses carrying text, twenty UTF-16 units to a press, with the modifier flags cleared so that a key still held cannot turn them into shortcuts. Never a paste: that would cost whatever had been copied. Each press is stamped (`DictationTyping.mark`) so the monitor knows its own. Everything is put on one line, since a line break typed into a chat box sends the message.

A space goes in front where the words would otherwise run into what is there. The app is asked for the one character before the caret; many say nothing, and then a space is added only when dictation was the last thing typed into that app within forty-five seconds.

A password field is never typed into. There, and wherever typing is not possible, the words go to the clipboard and the screen says so. The same when the person has moved to another app while the words were being written down: they belong to the app that was in front when the recording ended. Until they are typed, Escape, sleep, a locked screen and the switch going off take them back.

## Permissions

Accessibility, to see the key in other apps and to type into them, and the microphone. Both are asked from the switch in Settings and never from a key press; the microphone is asked again whenever Graft comes to the front with the switch on and macOS not yet asked. A grant made in System Settings tells nobody, so the monitors are put in place again once trust appears, and Settings asks again each time it is looked at.

Graft ships ad-hoc signed, so macOS knows each version by its code hash, as the keychain does. Expect both grants to need giving again after an update until releases are signed with a Developer ID; a release signed with the hardened runtime would also need the audio-input entitlement, which nothing passes to `codesign` today.

## Verification state

Built on 26 and 27 November 2026; the unit suite and the layout checks pass with it. It has not yet been run with a voice. Unproven until a run on a real Mac with a microphone says otherwise: that the key is seen in other apps once Accessibility is granted, that `CGEventSource.keyState` reports the right-hand modifiers and Fn, that the socket carries the `Authorization` header, how soon the microphone delivers its first sound after a press, how typing behaves in a terminal and in Claude's own message box, and whether a recording longer than a few seconds keeps its connection. `Tests/manual/dictation.md` is the case.

One cost is known and not yet weighed on a device. The microphone opens with every press of the key, shortcuts included, so the microphone light flickers at each right Command shortcut and a Bluetooth headset may drop to its call sound for that moment. Opening it only once the press has outlasted a tap would lose the first third of a second of speech.
