# Security

## Reporting something

Use the **Security** tab on this repository and choose *Report a vulnerability*,
which opens a private advisory only the maintainer can see. If that is not
available to you, open an ordinary issue saying you have something to report and
nothing else, and you will be asked for a private channel.

Please do not post a working exploit in a public issue. There is nothing here
worth an embargo of any length, so expect a fix and a release rather than a
process.

## What Graft touches

Graft reads the access token Claude Desktop already holds for each profile,
decrypting it out of `config.json` with the key in the login keychain — the same
key Claude itself uses. It does that to ask Anthropic what an account's usage
looks like, and to open a session window when you press the button for it.

Four things are true of that, and a report that one of them has stopped being
true is the kind worth sending:

Only the access token is ever decrypted, never the refresh token. Anthropic
rotates refresh tokens, and using one would sign Claude Desktop out.

Nothing is written back. Not to Claude's `config.json`, not to its cookies, not
to the keychain.

The token goes to `api.anthropic.com` and nowhere else, over HTTPS, as a bearer
token. Graft has no server, no telemetry and no account of its own.

The keychain is asked once, and only when asking is the way through. Every read
is tried silently first, so a build already trusted by the keychain item raises
no dialog at all.

## Known and not a vulnerability

The app is ad-hoc signed rather than notarised, because notarisation needs a
paid Apple developer account. The Homebrew cask clears the quarantine flag at
install time and the command it runs is in the cask file. This is documented in
the README, and it is a deliberate trade rather than an oversight.

Updates are fetched over HTTPS and verified against an EdDSA public key that
ships inside the app, so an update Sparkle will install has been signed by the
key that made the release. A report that this verification can be skipped is
very much a vulnerability; the ad-hoc signature by itself is not.
