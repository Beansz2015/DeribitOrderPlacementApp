# Coordinator review — position protection, batch 1

**Spec:** `docs/spec-protection-batch1.md`. **Report:** `docs/impl-report-protection-batch1.md`.
**Commit reviewed:** `2aa0016`. **Reviewer:** coordinator seat, Opus 5.5, high. **Date:** 2026-10-08.

## Verdict: ✅ APPROVED, with one race fixed and one owner ruling built in this review. Owner testnet checks owed.

## 1. Verified at the artefact

| Claim | Result | How |
|---|---|---|
| Gate | ✅ `GATE PASSED`, OrderCheck **371/371** at `2aa0016`; **376/376** after this review's changes | Re-run |
| Nine `frmMainPageV2.vb` censuses | ✅ 10 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18 = **68 across 64 lines**, before and after | `Select-String -AllMatches -CaseSensitive`, summed |
| Reconnect never gives up; delays 2–10 s for attempts 1–9, then 30 s | ✅ | Read `ReconnectDelayMs` and the loop |
| Page watcher: 0 pages under 60 s, then every 10 min; stops on recovery and on close | ✅ | Read `WatchWsDownPagesAsync`, the success arm and the `Finally` |
| Auth reply wait bounded at 10 s | Accepted from the report (the linked-token change); not re-read line by line | — |
| The emergency send order is unchanged; only messages changed | ✅ | Read both fire branches |
| A rejected reduce (id 1) during a pending emergency pages FAILED, ahead of the 10028 skip | ✅ | Read `HandleUnhandledJsonRpcError` |
| The `If TradeMode` classification table | Accepted from the report's 23-row table; spot-read rows 2, 13 and 22 | — |

**Accepted from the report, not re-run:** its mutation runs (8/371 and 2/371 failing against old behaviour).

## 2. The stop rule — `E1` (protection-batch1 escalation), owner ruled "any closing stop": the right call

`IsClosingStop` counts a stop only if its direction **closes** the position, then: the app's own
`StopLossOrder` / `TrailingStopLoss` untriggered or triggered (`open`), or any untriggered
`stop_limit` / `stop_market` / `trailing_stop` of any label. That avoids a false urgent NO STOP on every
restart in trailing mode, or after a hand-placed stop, and it still alarms on a stop on the adding side.

Two limits, both recorded, neither a reason to change it now:
- **It does not check that the stop covers the whole position.** At 10 USD (one contract) the amounts
  cannot differ. Add an amount check with the partial-fill fix, which is queued.
- **`order_type` is unverified against a real payload.** It matches my reading of Deribit's order object,
  which I have not checked against a capture. The experiment seat captures one (plan item (k)). The app's
  own labelled legs do not depend on it.

## 3. `D1` (protection-batch1 review) — a race in the emergency messages; FIXED in this review

The fire branches armed the "pending" state only **after** the reduce's send returned. The receive loop
processes the fill echo (`CompletePositionClose` → `NoteEmergencyCloseFlat`) and a rejection
(`NoteEmergencyReduceRejected`) concurrently with that send's `Await`. A fast outcome could land first: no
CONFIRMED page, then a false urgent "UNCONFIRMED after 10 s".

**Fix (messages only; the send order is untouched):** `ArmEmergencyClose()` arms the pending state and
its sequence number **before** `SendReduceMarketOrderAsync`. `ReportEmergencyCloseSend` takes that
sequence: a failed send clears the state and pages FAILED; an outcome already posted suppresses a stale
"SENT"; otherwise it pages SENT and starts the 10 s watchdog for that sequence.

## 4. Maintenance windows — owner ruling 2026-10-08, built in this review

The owner asked whether the 60 s page can tell a Deribit maintenance window from a fault. **It cannot.**
Nothing in the repo records what the app sees in maintenance, beyond an old note that Deribit closes the
socket gracefully and the owner's memory of HTTP 502 on reconnect (matching the 2026-08-13 outage).

Owner ruling (recommended; decision-bias tripwire `docs/harness-runs/decision-bias-20261008T1000Z-*`,
baseline first, not flagged):
- Every down page carries the **last connect error** (`WsDownPageText`'s `lastError`, cut at 120 chars).
- The page is **urgent only while a position is open**; flat, the same page goes at normal priority
  (`WsDownPagePriority`).
- True maintenance detection waits for evidence: plan item (k) records the connect errors if a window
  falls inside the experiment run.

Five new fixtures (page 12–16). `docs/HANDOVER-7.md` §6's recovery-budget trap is rewritten to match.

## 5. Residuals the report raised — accepted for now

| Residual | Disposition |
|---|---|
| JSON-RPC id 1 is shared with the index subscribe, so a failing subscribe during a pending emergency could page a false FAILED | Accepted: needs a reconnect inside a 10 s emergency window; the page quotes the error. Fix with the post-experiment emergency reorder, which owns the reduce payload (row A22/G20, shared edit ids) |
| A manual Connect click during an outage stops the pages only at the loop's next attempt (≤ 30 s) | Accepted |
| The single-flight release can race a re-drop within microseconds | Pre-existing; now the only way the loop can stop. Queue with the quote-silence watchdog (row A19), which would catch it |

## 6. Owed — owner, testnet (x64 rebuilt in this review, `secrets.json` testnet)

The report's §5 checks 1–5, with one change to check 1: with **no position** the page now arrives at
**normal priority** and names the last connect error. To see an urgent page, hold a testnet position
during the outage.
