# Coordinator review — trade-record slippage fields, and a record of every chase abort

**Spec:** `docs/spec-trade-slippage-fields.md`. **Report:** `docs/impl-report-trade-slippage-fields.md`.
**Commit reviewed:** `bcf9681`. **Reviewer:** coordinator seat, Opus 5.5, high. **Date:** 2026-10-06.

## Verdict: ✅ APPROVED. Owner testnet check owed. One owner ruling owed (the anchor reset, §4).

## 1. Verified at the artefact

| Claim | Result | How |
|---|---|---|
| Gate | ✅ `GATE PASSED`, OrderCheck **323/323** | Re-run at `bcf9681` |
| Nine `frmMainPageV2.vb` censuses | ✅ 10 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18 = **68 across 64 lines** | `Select-String -AllMatches -CaseSensitive`, summed |
| No abort decision changed | ✅ every `frmMainPageV2.vb` edit is a field write or a record call beside an existing branch; `ChaseAbortReason` untouched | Read the diff, code lines only |
| Every guard call records its abort | ✅ the 8 census occurrences of `IsATRSlippageExcessive` = 1 definition + `ChaseAbortReason` + 4 `ExecuteOrderAsync` + 2 `StopLossForTrailingOrderAsync`; each caller now records | grep + read |
| The trip snapshot cannot go stale | ✅ the guard parks it, calls `ResetOrderAttempt` (which drops any older one), then publishes it; `RecordAbortedEntry` consumes it | Read `IsATRSlippageExcessive` |
| Direction mapping | ✅ every placement guard runs with `direction = "LONG"`/`"SHORT"` | grep of the 6 placement sites |
| Normal close resets the anchor | ✅ `CompletePositionClose` → `CancelOrderAsync` → `ResetOrderAttempt` | grep |
| Threadpool abort write is safe | ✅ `RecordAbortedEntry` never throws; its `DatabaseError` handler only calls `AppendColoredText`, which marshals | Read `OnDatabaseError` |
| Schema + P/L on the real 89-row db | Accepted from the report (a scratch-copy probe); **not re-run** | — |

**Reasoned, not verified:** a threadpool abort write and a trade write cannot collide in practice — an
abort happens while flat (entry chase), a trade write at close. Even if they did, System.Data.SQLite's
default command timeout retries a busy lock. I did not test it.

**Not done, by me or the implementer:** mutation runs on the new fixtures.

## 2. The implementer's six decisions — all upheld

The snapshot before the reset (decision 1) is the only correct shape: the guard resets the anchor itself,
so no call site could record it afterwards. Rows written at the call sites, not inside
`CancelWorkingEntryCoreAsync` (decision 3), keep "API request" cancels out of the abort table.

## 3. Spec-back findings — upheld and folded into `docs/spec-trade-slippage-fields.md` §0b

- `SB1` (slippage-fields spec-back — the anchor is the PLACEMENT price, not "the first reposition check").
- `SB2` (slippage-fields spec-back — the ATR arm resets the anchor before any call site can record it).
- `SB3` (slippage-fields spec-back — the re-quote count counts edits issued, not edits confirmed sent).
  Accepted residual.

## 4. `D1` (slippage-fields, a pre-existing defect in code) — confirmed; owner ruling owed

`HandlePlacementResponse` (`frmMainPageV2.vb:1990`) restores `placedPrice` on a rejection and never calls
`ResetOrderAttempt`. Confirmed by grep: the only reset sites are the reduce-market echo, the guard's own
trip, `CancelOrderAsync` and `CancelWorkingEntryCoreAsync`. Effect: the next placement measures drift
from the rejected order's anchor. It can be **refused** as "ATR slippage at placement" after the market
moved, and those refusals now land in `AbortedEntries` as if they were real slippage.

**Recommendation: a small separate spec** — call `ResetOrderAttempt` on a placement rejection and on the
socket-down close path. Model: Opus. Effort: high (order path). Decision-bias tripwire:
`docs/harness-runs/decision-bias-20261006T1440Z-*`, baseline written first, no `gives_up_for_economy`
flag, stable 5/5.

### 4a. What `D1` does in practice — traced 2026-10-06 for the owner

- The rejected request is the **new entry itself** (ids >= `PlacementIdBase`). Nothing else is
  cancelled. Reposition edits use a different path and are not affected.
- The anchor never **triggers** a placement; it can only **refuse** one. The next placement
  (`BuyLimit`, `SellLimit`, `BuyNoSpread`, `SellNoSpread`) is refused if the price is more than
  ATR × ATRSlip from the rejected attempt's price, **in either direction** (absolute distance). That
  refusal resets the anchor, so the attempt after it goes through. One refusal, not a lockout.
- Within the limit, the placement goes through, but its chase budget is measured from the old price.
- 🚨 **Bridge act (Live):** `ExecuteOrderAsync` returns before sending, so the ack never completes.
  `PlaceAutomatedOrder` waits its 5 s and returns `timeout`. The disposition reads
  **`rejected: timeout`**, a misleading reason, and the signal is lost. Read from code; not run.
- Applies only with ATRSlip ticked.

## 5. Owed — owner, testnet, after an x64 rebuild

1. A chased limit entry that fills → its `Trades` row has `RequoteCount > 0`, `SignalPrice` = the
   placement price, a plausible `SlippageATR`.
2. A forced abort (ATRSlip 0.05) → one `AbortedEntries` row, reason `ATR slippage`, no new `Trades` row.
3. Read both on a **copy** of `trades.db`. A seat can read the copy for you.
