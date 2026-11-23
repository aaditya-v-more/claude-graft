# graft-win2-signedin-v211-01 — AUTH READY, baseline incomplete

Device: wintest2; exclusive win2 reacquired and renewed at 17:48 UTC, 2026-11-19 (30-minute lease; renew before expiry). Live installed executable reports ProductVersion and FileVersion 2.1.1. Manuals: 73edeed.

| Case | Result | Observations |
| --- | --- | --- |
| test-profiles | BLOCKED at step 2 | Existing Test A and Test B visible (no profile creation or modification). Graft reported Test A not signed in. Opened Test A from Graft and confirmed native Claude login, then advanced Get started to the empty Email field and Continue with email. No code requested. Distinct accounts and reopen checks pending. |
| smoke | PARTIAL | Reopened the running manager without tray interaction. Verified Claude, Test A, Test B, Artifacts, Projects, Search Chats and Settings in the native UI. Claude showed usage; Test A showed a signed-out reason. Settings opened, maximized and returned to original size. Minimum-size layout, flyout and clean quit not verified. |
| usage-and-start-session | BLOCKED | Main Claude showed usage bars and reset times; Test A signed out. No official usage comparison, refresh, Start Session or idle observation. Coordinate with Mac release QA before Start Session. |

Secure handoff: Enter authorized account 2 address in the native Test A Email field, choose Continue with email only when coordinated, then enter the one-time code securely. Verify account distinct from main without recording identifiers. Test B requires a separate coordinated handoff if not already signed in. Test A prompt intentionally left open. No tray, Quick Settings, network controls, process kill, settings changes, or mail sent. Request 4 not started.
