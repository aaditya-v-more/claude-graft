# Dictation types what is said into any app

Platforms: macOS
Needs: Claude signed in to one account, a working microphone
Setup: Graft running, Claude closed. TextEdit open on an empty document.
Do not touch: any chat. Nothing here sends a message.
Put back: turn **Dictate into any app** off, and in System Settings › Privacy &
Security remove Claude Graft from Accessibility and from Microphone if it was
not there before.

1. Open Graft's window, choose **Dictation** in the sidebar and turn on
   **Dictate into any app**.
   Expect: macOS asks for Accessibility and for the microphone. Allow both.
   The line under the key reads "Hold to speak, tap twice to keep recording".
   Until both are allowed it says which is missing, in orange, with a button
   that opens the right page of System Settings.
2. Click into TextEdit. Hold right Command, say "the quick brown fox", let go.
   Expect: a small pill at the bottom of the screen says "Listening…" and then
   shows the words as they are said, changing as they are corrected, and
   grows upward as they run to more lines rather than cutting them short. On
   letting go the sentence is typed in TextEdit within about two seconds, as
   the pill last showed it. Focus never left TextEdit.
3. Hold right Command and say "jumps over the lazy dog". Let go.
   Expect: the words follow the first sentence with one space between.
4. Tap right Command twice quickly, take your hands off the keyboard, and speak
   two sentences with a pause between them. Press right Command once.
   Expect: the pill says to press the key to finish, stays up with no key
   held, and both sentences are typed after the last press.
5. Tap right Command twice, say a few words, press Escape.
   Expect: the pill goes and nothing is typed.
6. Press right Command+A (hold right Command, press A). Then hold right
   Command for a second and click in the document before letting go.
   Expect: TextEdit selects everything, as Command+A always does. No pill
   after the A; the pill goes at the click. Nothing typed either time.
7. Tap right Command once and wait.
   Expect: nothing is typed and no pill stays.
8. Tap right Command twice and stay silent.
   Expect: after about 45 seconds the recording ends by itself and the pill
   says nothing was heard.
9. Open System Settings › Passwords (or any password field) and click into the
   password field. Hold right Command and speak, then let go.
   Expect: nothing is typed into the field. The pill either says a password
   field has the keyboard, or says the words are on the clipboard.
10. Turn Wi-Fi off. In TextEdit, hold right Command, speak, let go. Turn Wi-Fi
    back on.
    Expect: within about ten seconds the pill says Anthropic could not be
    reached. Nothing is typed, and the next recording works once Wi-Fi is back.
11. Change the key to Right Option on the Dictation page and repeat step 2
    with it.
    Expect: Right Option dictates and right Command no longer does.
12. Tap the key twice, speak for a few seconds, then lock the screen. Unlock.
    Expect: the recording is gone, nothing is typed, and the key works again.
13. Hold the key, say a sentence, let go, and press Escape while the pill
    says "Writing it down…". Then do it again and click into another app
    instead of pressing Escape.
    Expect: after Escape nothing is typed. After the click nothing is typed in
    either app, and the pill says the words are on the clipboard.
14. Copy any word in TextEdit. Dictate a sentence and paste.
    Expect: the copied word is pasted, not the sentence. Turn on **Also copy
    what was said to the clipboard**, dictate another sentence and paste.
    Expect: that sentence is typed once and pasted once. Turn the switch off.
15. Optional, ten minutes: tap the key twice and read aloud until it stops.
    Expect: a countdown over the last 30 seconds, then everything said is typed.

Pass when: held and hands-free recordings both type what was said where the
cursor is, a shortcut, a stray tap and Escape type nothing, a password field
is never typed into, and every failure says what went wrong and leaves the key
working. Save `diagnostics.log` with the result: it has a `dictation.` line for
each recording and must contain none of the words spoken.
