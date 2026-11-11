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
looks like, to open a session window when you press the button for it, and,
when you set a Claude up to share usage, to answer that Claude's Claude Code
requests from another of your accounts.

These things are true of that, and a report that one of them has stopped being
true is the kind worth sending:

Only access tokens are ever decrypted, never the refresh token. Anthropic
rotates refresh tokens, and using one would sign Claude Desktop out. An account
signed in from Graft for shared usage is given a refresh token too, and Graft
never reads it.

Nothing is written back to Claude's login. Not to its `config.json`, not to its
cookies, not to the keychain. Claude's safe-storage key seals two things of
Graft's own: the Claude Code setting it writes into a closed profile for shared
usage, and the logins signed in from Graft, kept in Graft's own folder.

A token goes to `api.anthropic.com` and nowhere else, over HTTPS, as a bearer
token. Signing in an account for shared usage sends only the browser's one-time
code to `platform.claude.com`. Graft has no server, no telemetry and no account
of its own.

The claude.ai sign-ins behind the Artifacts, Projects and Search Chats panes are
made on claude.ai's own page, in a window of Graft's, and each stays in a web
store of its own that only claude.ai's pages are loaded into. Graft never reads
the cookie out of it and never sends it anywhere; requests are made from a
claude.ai page, so the browser engine sends it to claude.ai alone. Claude
Desktop's own cookies are never decrypted.

The relay that carries shared usage listens on `127.0.0.1` only, takes its
instructions over a socket only your user can open, and holds the logins it is
lent in memory. It unwraps connections to `api.anthropic.com` alone, with a
certificate from an authority Graft makes on your Mac whose name constraint
permits that one name; the authority's private key is deleted as soon as the
certificate is signed, and its identity is never written to the keychain.
Everything else passes through it as bytes.

The keychain is asked once, and only when asking is the way through. Every
read is tried silently first, so a build already trusted by the keychain item
raises no dialog at all.

## Known and not a vulnerability

The app is ad-hoc signed rather than notarised, because notarisation needs a
paid Apple developer account. The Homebrew cask clears the quarantine flag at
install time and the command it runs is in the cask file. This is documented in
the README, and it is a deliberate trade rather than an oversight.

Updates are fetched over HTTPS and verified against an EdDSA public key that
ships inside the app, so an update Sparkle will install has been signed by the
key that made the release. A report that this verification can be skipped is
very much a vulnerability; the ad-hoc signature by itself is not.
