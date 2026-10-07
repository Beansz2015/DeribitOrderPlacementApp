# Implementation report — reset the chase anchor when a placement dies; a refused bridge act says why

**Spec:** `docs/spec-chase-anchor-reset.md` (owner go-ahead 2026-10-06; §2.4 added 2026-10-07).
**Built:** 2026-10-08 (GMT+8), on top of `82b1376`. One commit. **Not pushed** (the owner is the only pusher).
**Implementer:** Opus 5.5, high. **Review owed:** coordinator, Opus, high.
**Line numbers:** the spec cites HEAD `5f69134`. The cited lines still matched at `82b1376`
(`PlaceAutomatedOrder` 953, `HandlePlacementResponse` 1990, `RegisterPendingPlacement` 3155). The
lines below are after this change.

## 1. What changed, by spec section

All in `DeribitOrderPlacementApp/frmMainPageV2.vb`, plus four OrderCheck fixtures.

| Spec section | Change | Where |
|---|---|---|
| §2.1 reset on a timely rejection | `ResetOrderAttempt()` in `HandlePlacementResponse`'s error arm, after the `placedPrice` / `placedStopLossPrice` rollback, before the ack completes. The `entry.TimedOut` branch is untouched (spec decision A1). | `:2036` |
| §2.2 reset at position close | `ResetOrderAttempt()` in `CompletePositionClose`, unconditional, right after the `currentTradeSignalPrice` / `currentTradeRequoteCount` / `currentTradeSlippageAtr` clears. `CancelOrderAsync` is untouched (spec decision A2). | `:5865` |
| §2.3 `Sent` flag | `PendingPlacement` gains `Sent As Boolean` and `NotSentReason As String`. `RegisterPendingPlacement` sets `Sent = True`. | `:3165`, `:3208` |
| §2.3 record the reason | New `NotePlacementNotSent(requestId, reason)`. Called before **every** early `Return` in `ExecuteOrderAsync` (13 sites, spec decision A3) and in its `Catch`. A no-op for a manual placement (`requestId` 0, no pre-registered entry). | `:3194`, `ExecuteOrderAsync` |
| §2.3 return at once | `PlaceAutomatedOrder`, after the marshalled `ExecuteOrderAsync` completes: if the entry exists and is not `Sent`, remove it and return the refusal. No 5-s wait. The decision is the pure seam `UnsentPlacementResult(sent, reason)`. | `:996`, `:3186` |
| §2.4 clear the staged signal tag | In the same not-sent branch, `ClearPendingSignalTag()` — the same two writes `CancelWorkingEntryCoreAsync` makes (`pendingSignalId = -1`, `pendingSignalConfidence = ""`). | `:996` block |

### The reasons, per early return

| Early return | Reason text after `rejected: ` |
|---|---|
| Socket down | `not sent: WebSocket is not connected` |
| Amount not a positive number | `not sent: invalid amount` |
| Own-side quote ≤ 0 (6 sites) | `not sent: best bid price is not valid` or `not sent: best ask price is not valid` |
| ATR guard at placement (4 sites) | `ATR slippage at placement` — the literal its `AbortedEntries` row uses |
| Unsupported order type | `not sent: unsupported order type` |
| Exception before the send | `not sent: error placing order: <exception message>` |

The disposition format and its token set are unchanged. Only the text after `rejected: ` changes.

## 2. Decisions the spec did not spell out — review these

| # | Decision | Why |
|---|---|---|
| 1 | **The quote-check reasons name the quote the code actually checks**, not the log line's wording. Four existing log lines name the wrong side (for example `BuyNoSpread` checks `BestAskPrice` and logs "Best bid price Is Not valid"). | A disposition reason that names the wrong quote misleads. The log lines are not touched — out of scope. |
| 2 | **The `Catch` records a reason too.** The reason is read only when the entry is not `Sent`. A throw after `RegisterPendingPlacement` (for example a send failure) still waits for the ack and returns `timeout`. | Spec §2.3: "every one must produce a reason". After the send point the order may exist, so `timeout` (not definitive) is the honest answer. |
| 3 | **`UnsentPlacementResult` falls back to `not sent`** when the reason is empty. | Defence in depth for spec §2.3 "never an empty string". It also guarantees the reason is never `timeout`, which `SignalBridge` reads as not definitive. |
| 4 | **§2.4 clears the tag inside `PlaceAutomatedOrder`.** `SignalBridge` already calls `ClearPendingSignalTag()` for any reason other than `timeout`. | Spec §2.4 asks for it in the not-sent branch. It also covers any future caller of `PlaceAutomatedOrder`. The double clear is harmless. |
| 5 | **The not-sent branch removes the registry entry.** | Spec §2.3. Before this change the entry stayed until the 60-s sweep. |

## 3. Findings — raised after the work, against the spec

Prefix rule from `docs/HANDOVER-6.md` §7bb. Scoped to this feature ("chase-anchor-reset").

| ID | Kind | Finding | Disposition |
|---|---|---|---|
| `SB1` (chase-anchor-reset) | spec-back | **A throw between the ATR guard and the send leaves the anchor seeded.** In the four limit arms, `IsATRSlippageExcessive` seeds `originalSignalPrice` first. Then `Decimal.Parse(txtTriggerOffset.Text)` and the payload build run, before `RegisterPendingPlacement`. A throw there (for example a blank Trigger Offset box) means nothing was sent, but the anchor survives into the next placement. The spec's one-line job ("a placement never becomes a working order") covers it in spirit; spec §2 does not list it. | **Not fixed** — outside the listed changes. The bridge half is fixed (the act reports `not sent: error placing order: …` at once). An anchor fix needs a local "sent" flag in `ExecuteOrderAsync`, because a manual placement has no registry entry. Owner ruling: fold into this spec, or accept. |

## 4. Verified, and how

| Claim | How |
|---|---|
| Gate passes | `tools/checks/verify-gate.ps1` → `GATE PASSED`, run after the last code edit |
| OrderCheck 327/327 | Same gate run. 323 before + 4 new `not-sent fixture` checks (spec acceptance item 2) |
| The fixtures fail against today's behaviour | Red step run first: the seam stubbed to `Return Nothing` (= wait for the ack, today's behaviour). `dotnet run --project tools/OrderCheck` → `FAILED 3/327`. Fixtures 1–3 failed; fixture 4 (sent → wait) passed, as a guard |
| Nine censuses unchanged | Occurrence counts (case-sensitive `grep -o -F` per symbol, summed; distinct line numbers collected): 10 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18 = **68 across 64 lines**, before and after. `ResetOrderAttempt` is not a census symbol. `IsATRSlippageExcessive` stays at 8: no call added, no new comment names it |
| All 13 early returns record a reason | Printed the line before each bare `Return` in `ExecuteOrderAsync`: 13 of 13 are `NotePlacementNotSent` calls |
| A timely rejection is the only new reset site in `HandlePlacementResponse` | Diff read: the call sits in the `errorField` arm only. The `TimedOut` branch is byte-identical |
| The trade row is unaffected by the close reset | `RecordCompletedTrade` reads the `currentTrade*` snapshot. Grep of `originalSignalPrice` / `anchorAtr` / `currentRequoteCount`: no reader between the reset and the end of `CompletePositionClose` |
| Testnet rejection recipe exists | `docs/impl-report-decouple-v2.md` (runtime item 2, line 114): a 1-USD placement on testnet logged `ORDER REJECTED (id 600002): code -32602 - Invalid params \| {"reason":"must be a multiple of contract size","param":"amount"}`. `ExecuteOrderAsync` sends the Amount box as typed; its only amount check is `> 0`. Not re-run by me |

## 5. NOT verified

- **Runtime.** Spec acceptance item 3 is owner-driven; a seat never places a trade. Not run.
- **The bridge half at runtime** (spec acceptance item 4, optional). Verified by code reading and the
  seam fixtures only.
- **Mutation runs** beyond the red step above.
- **Cross-thread visibility of `Sent` / `NotSentReason`.** Reasoned: written on the UI thread inside
  the marshalled call; read on the caller's thread after awaiting that call's `Task`. The await
  completion is a full fence. Not tested under load.

## 6. Runtime recipe for the owner (spec acceptance item 3)

Testnet only. Rebuild the **x64** bin first (the gate builds AnyCPU only). Check the `Environment`
key and the `— TESTNET` window title before placing anything.

1. Tick ATRSlip and set it to **0.05**.
2. Set Amount to **1**. Press Buy Limit.
   - Expect: `ORDER REJECTED (id 6000xx): code -32602 - Invalid params | {"reason":"must be a multiple of contract size",…} - engine state rolled back`.
3. Set Amount back to **10**. Note the rejected price (the log line before the rejection).
4. Wait until the bid has moved more than ATR × 0.05 from that price. The Tooling readout shows the
   current limit.
5. Press Buy Limit again.
   - **Pass:** `Buy limit order placed For 10 at …`. No `slippage exceeds limit` line. No new
     `AbortedEntries` row.
   - **Today's behaviour (fail):** `LONG slippage $… exceeds limit $…` and an `AbortedEntries` row
     with reason `ATR slippage at placement`.
6. Cancel the entry. ⚠ With ATRSlip at 0.05 the chase guard may abort the working entry on its own
   after one small move. That is expected and is not a failure of this test.

**Optional bridge check (spec acceptance item 4):** only if you choose to arm Live on testnet. Force
an ATR refusal at placement. Expect the disposition `rejected: ATR slippage at placement` within
about 1 s, not after 5 s.

## Model and effort for the review

- **Model:** Opus. **Effort:** high. Order path, receive thread, bridge act.
