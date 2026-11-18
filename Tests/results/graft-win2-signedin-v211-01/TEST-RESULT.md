# TEST RESULT — checkpoint pending auth handoff

Product: Claude Graft  Job: graft-win2-signedin-v211-01  Build: installed Windows 2.1.1
Overall: BLOCKED (handoff pending; not a final test verdict)

| Case | Device | Verdict | Evidence |
| --- | --- | --- | --- |
| test-profiles | wintest2 | BLOCKED at step 2 | Test A was already open at official email sign-in UI; `test-profiles-step2-ready.png`. Step 1 intentionally skipped. No code requested. |
| smoke | wintest2 | NOT RUN | Ordered after test-profiles. Graft already running on arrival. |
| usage-and-start-session | wintest2 | NOT RUN | Ordered after smoke; Start Session requires separate orchestrator go-ahead and idle account. |

Manual files match commit 73edeed (git diff empty). Installed executable reports 2.1.1. Existing Graft and Test A windows were already open. No configuration or sign-in changed. No tray, Quick Settings or network interaction. No test-created device windows or files to clean up. 

Handoff: Test A, account 2, test-profiles step 2: enter account and request/enter email code in existing Test A window. After owner completes, verify account differs from main without recording identifying email. Then step 3 Test B/account 3 needs a separate AUTH READY before any code request, and verify all three distinct. Continue test-profiles steps 4–5 and conditional 6 only if provider flow occurs. Then smoke, then usage case, with explicit go-ahead before Start Session; only once on a truly idle account. Official UI comparison required. Flyout steps require a supported non-tray route; source Program.cs exposes show-window and quit, not a flyout command, so do not use tray or assume route.

Exclusive win2 lock held by graft-win2-signedin-v211-01, renewed 2026-11-18 21:15:37 UTC; expires 30 minutes after renewal. Orchestrator must renew well before expiry and retain through auth/cleanup, then free. UI intentionally left untouched for handoff.
