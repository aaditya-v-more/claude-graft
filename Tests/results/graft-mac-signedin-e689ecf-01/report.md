# TEST RESULT — checkpoint

Product: Claude Graft  
Job: graft-mac-signedin-e689ecf-01  
Device: mac2 only  
Build: e689ecf; local and transferred ZIP SHA-256 `02edc702fbf896d8a0724ab09dbdfcb4ba06c34a3fffd3eeb1b13c23e8813e66`  
Overall: BLOCKED — authentication handoff, no test case completed.

## Preflight evidence
- Manual files at 6911bdc match 73edeed for the seven requested cases.
- Exclusive mac2 lock held by `graft-mac-signedin-e689ecf-01`; renewed 2026-11-18 21:15:36 UTC, lapses about 21:45:36 UTC unless renewed.
- mac2 free disk: 5.3 GiB. No Graft or relay process was running before launch. Installed Graft untouched.
- ZIP copied, checksum verified and extracted into `/Users/admin/graft-mac-signedin-e689ecf-01.1gFtQa` on mac2. Graft not yet launched.
- Main Claude Desktop launched; sign-in landing page displayed with Get started and cookie choices. No account identity or credentials collected. No interaction with the sign-in screen.

## Step status
- Before test-profiles case 1: BLOCKED at main Claude Desktop account 1 sign-in, pending orchestrator-controlled official email-code UI. No code requested by tester.
- test-profiles steps 1–5: NOT RUN; step 6 Windows-only.
- smoke steps 1–6: NOT RUN.
- usage-and-start-session steps 1–5: NOT RUN; coordinate any Start Session with Windows run before choosing an idle account.
- shared-usage steps 1–7: NOT RUN.
- chat-sharing steps 1–5: NOT RUN.
- artifacts steps 1–8: NOT RUN; steps 4–6 require owner-confirmed private non-sensitive scratch artifact with no invitees; otherwise mark not run and create nothing.
- projects-and-search steps 1–8: NOT RUN; obtain owner-approved common non-sensitive search word.

## Handoff and cleanup outstanding
Orchestrator must take sole control of mac2 sign-in UI, arrange email-code flow before requesting the code, and renew the exclusive lock before expiry. Resume ordered cases after account 1 authentication, including additional handoffs for Test A/account 2, Test B/account 3, Artifacts/account 1 and Projects/Search account 1/2 as applicable. Answer only Allow once on a genuine Claude Safe Storage prompt. After tests, undo only test sharing, quit Graft through its own Quit, verify no ClaudeGraft or graft-relay process, clean only the unique test folder if appropriate, and release lock only after cleanup/handoffs. No screenshots or raw logs collected.
