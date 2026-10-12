# Coordinator review — partial fills follow-up: the wrong-side TP cancel, and PF-R4 at runtime

**Reviewed 2026-10-13 (GMT+8) by the coordinator seat (Opus 5.5, high).**
**Under review:** commits `89ebf8a`, `4f29398`, `cdd3c2d`, `c52bf2b`, against `docs/review-pf-phase2.md` §3 and
`docs/impl-report-pf-followup.md`.
**Verdict: ACCEPTED.** PF-R4 is **runtime-accepted** on testnet (LONG). The wrong-side cancel is accepted on fixtures
and code reading; it shares the cancel path seen working. No follow-up code.

IDs: `PF-R4` is the owner's ruling that a TP resting while flat is cancelled, then alerted. `PF-SB2` and `PF-SB3` are
the Phase 2 seat's spec-back findings (an API flatten never reaches PF-R4; a TP on the position's own side).
`PF-FU-…` are this seat's findings. `R2` is batch 2's ruling that the no-stop watchdog alarms only.

---

## 1. Verified at the artefact

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 466/466** | `tools/checks/verify-gate.ps1`, executed by this seat at `c52bf2b` |
| Nine `frmMainPageV2.vb` censuses | **71 occurrences across 67 lines**, unchanged | executed |
| The temporary switches are gone | `git diff --stat 89ebf8a HEAD -- DeribitOrderPlacementApp` is **empty**; 0 references to `PfFuTestnet` / `PFFU_` | git; grep |
| The ruling is what was built | `LegBreachCancelsTp` = RestingFlat **or** WrongSide → cancel by id (request id 43) first, then red log and urgent page; the old alarm-only branch is gone | read the `89ebf8a` diff |
| PF-R4 at runtime | Raw echo (`%LOCALAPPDATA%\DeribitOrderApp\pffu-backup-20261013\`): **1** id-43 `private/cancel`, **0** id-30 cancel-all (the switch worked), the TP `122923006765` cancelled `user_request`, the SL `SLTS-11032708` cancelled `oco_other_closed`. The page `TP resting with no position: cancelled` at p5 is in the report's Appendix C | regex counts by code; page carried from the report |
| End state | flat, no orders, cancel-on-disconnect OFF at 20:03:18–20:03:20 UTC | the report's Appendix B (API replies, verbatim); not re-read by me |
| The permission refusal | The seat stopped at the refusal, sent nothing, read the account back and committed the pane as blocked (`cdd3c2d`); it resumed only after the owner's go | `cdd3c2d` message; report §3.1 |

## 2. Findings — dispositions

| ID | Meaning | Disposition |
|---|---|---|
| `PF-FU-SB1` | Both cancels now send first and page "cancelled" only when the send went out; a failed send pages "NOT SENT". The wrong-side case lost its local `no_stop` sound | ✅ Accepted: it matches the ruling's text ("cancel …, then … page"; "the same action") and never claims a cancel that did not go out |
| `PF-FU-SB2` | Cancelling the TP also cancels its OCO-linked stop (`oco_other_closed`, observed) | ✅ Accepted; **no ruling needed.** In the wrong-side case the app's stop has the TP's direction, so it is on the position's side too and, being reduce-only, could never close the position. Batch 2's no-stop watchdog already treats that state as "no stop" (`IsClosingStop` needs the closing direction) and pages under `R2`. Nothing protective is lost by the cancel |
| `PF-FU-SB3` | Git Bash line-ending trap (`sed -i` → LF; `grep -c $'\r$'` false) | Closed by the seat. Recorded in the seat memory |

## 3. Still open on partial fills

`docs/spec-partial-fill-fix.md` §5 item 5 (a real partial fill with the fix in force) — after the owner's return; it
gates any Amount above 10 USD and `N2 LIVE`. Also unobserved: the SHORT side, refusal paths of ids 42 and 43.

## §Model and effort

- Next coordinator item: the `TN-F3` protection spec (a refused stop edit or TP resize is never re-checked) —
  Opus 5.5, high.
