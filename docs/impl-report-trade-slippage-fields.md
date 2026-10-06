# Implementation report — trade-record slippage fields, and a record of every chase abort

**Spec:** `docs/spec-trade-slippage-fields.md` (owner rulings R1 populate, R2 separate `AbortedEntries`
table, 2026-10-06).
**Built:** 2026-10-06, on top of `9a8961c`. One commit. **Not pushed** (the owner is the only pusher).
**Implementer:** Opus 5.5, high. **Review owed:** coordinator, Opus, high.
**Line numbers:** the spec cites HEAD `4a3e29d`. The retirement commits moved `frmMainPageV2.vb` by
about +30 to +45 lines. Every site below was re-found by symbol, not by the spec's line number.

## 1. What changed, by spec section

| Spec section | Change | Where |
|---|---|---|
| §2.1 count re-quotes | `currentRequoteCount += 1` at the four chase-edit sites, after the edit's `Await`, beside the `lastEntryChaseUtc` stamp. Reset stays in `ResetOrderAttempt`. | `frmMainPageV2.vb` |
| §2.2 completed trades | New `currentTradeSignalPrice` / `currentTradeRequoteCount` / `currentTradeSlippageAtr`, snapshotted at the flat→nonzero transition beside the signal-tag promote, cleared at close beside the tag clear, copied onto the `TradeRecord` in `RecordCompletedTrade`. | `frmMainPageV2.vb` |
| §2.2 the spec-back recommendation | **Taken.** New `anchorAtr` = `GetEffectiveAtr().Atr` captured in the guard's seed branch, reset in `ResetOrderAttempt`. A trade's `SlippageATR` = `|positionAvgEntry − anchor| / anchorAtr`. | `frmMainPageV2.vb` |
| §2.2 schema | Three `ALTER`s in `MigrateSchema` (`SignalPrice`, `RequoteCount`, `SlippageATR`, all `NOT NULL DEFAULT 0`), the `INSERT`, `CreateTradeFromReader`, new `ReadIntegerOrZero`. | `TradeDatabase.vb` |
| §2.2 delete two fields | `AttemptType` and `MaxSlippageExceeded` deleted. The `TradeRecord` comment now states that `SignalPrice` is the chase anchor, not the engine's signal price. | `TradeRecord.vb` |
| §2.3 `AbortedEntries` | Table created `IF NOT EXISTS` at init. `RecordAbortedEntry` (never throws; reports via `DatabaseError`, returns 0) and `GetAbortedEntries`. New `AbortedEntryRecord` class. No UI. | `TradeDatabase.vb`, `TradeRecord.vb` |
| §2.3 the recorder | `RecordAbortedEntry(reason, direction, ownSideQuote)` builds the row from plain fields and hands it to `QueueAbortedEntryWrite`, which writes on the threadpool inside a swallow-all `Try`. | `frmMainPageV2.vb` |
| §2.3 abort sites | Four chase sites: one line before `CancelWorkingEntryCoreAsync(abortReason)`; reason = `"ATR slippage"` or `"EV floor"`, as `ChaseAbortReason` returns it. Four `ExecuteOrderAsync` guards: `"ATR slippage at placement"`. Two `StopLossForTrailingOrderAsync` guards: `"ATR slippage at trailing placement"`. | `frmMainPageV2.vb` |
| §3 item 3 | Pure `Friend Shared SlippageAtrRatio(price, anchor, atr)`: 0 when anchor, price or ATR ≤ 0; else the ratio rounded to 4 dp. Used by both the trade snapshot and the abort row. | `frmMainPageV2.vb` |

## 2. Decisions the spec did not spell out — review these

| # | Decision | Why |
|---|---|---|
| 1 | **The ATR guard parks a snapshot (`slipTrip`) before its own reset.** Three lines in the trip branch, one in the seed branch. No decision changed; no new call to the guard. | See finding `SB2` below. Without it, an ATR-slippage abort row would read anchor 0, re-quotes 0. |
| 2 | **`ResetOrderAttempt` drops the snapshot (`slipTrip.Valid = False`).** The guard re-publishes it right after its own reset. | Any later reset (a cancel, a close) kills a stale snapshot, so an `"EV floor"` row can never pick up an old ATR trip's values. |
| 3 | **The chase rows are written at the four call sites, not inside `CancelWorkingEntryCoreAsync`.** | The call site has the quote and the direction. `CancelWorkingEntryCoreAsync` also serves `"API request"` cancels, which are not chase aborts. The call precedes the cancel, so the tag and the anchor are still staged. |
| 4 | **An abort row uses the ATR in force at the abort, not `anchorAtr`.** | Spec §2.3 says "ATR in force" and `AtrSource = GetEffectiveAtr().Source`. For an ATR trip it is the exact ATR the decision used. |
| 5 | **`SlippageATR` is rounded to 4 dp**, away from zero. | Tidy storage. The OrderCheck fixtures pin it. |
| 6 | **`Direction` is written `Long` / `Short`**, mapped from the guard's `LONG` / `SHORT`. | Matches the `Trades.Direction` values. |

## 3. Findings — raised after the work, against the spec

Prefix rule from `docs/HANDOVER-6.md` §7bb. All are scoped to this feature ("slippage-fields").

| ID | Kind | Finding | Disposition |
|---|---|---|---|
| `SB1` | spec-back | `docs/spec-trade-slippage-fields.md` §1 says the anchor is "the own-side quote at the first reposition check". In code the guard first runs at the **placement**: `ExecuteOrderAsync` and `StopLossForTrailingOrderAsync` call it before sending, when ATRSlip is ticked. So the anchor is the placement price. For a NoSpread entry that is ask − 0.5 (bid + 0.5), not a raw quote. | Comments written to match the code. Fold into spec §1. |
| `SB2` | spec-back | Spec §2.3 says "write one row per abort, before `ResetOrderAttempt` clears the anchor". For the ATR arm that order cannot hold at any call site: the guard calls `ResetOrderAttempt` itself before it returns `True`. | Fixed by decision 1 above (a record-only snapshot). Fold into spec §2.3. |
| `SB3` | spec-back | Spec §2.1 says count "only after the edit was sent". The three edit functions return a bare `Task`. They can skip without sending (credit shortfall, blank amount) or swallow a send exception. The count sits after the `Await`, so it counts the same events that advance `placedPrice` and log "Order repositioned". A skipped edit still counts. | Accepted residual. An exact count needs the edit functions to return a sent flag — a change on the order path, outside this spec. |
| `D1` | defect in code, pre-existing | **The chase anchor is not reset on a placement rejection.** `HandlePlacementResponse` restores `placedPrice` but never calls `ResetOrderAttempt`. The next placement's guard then measures drift from the rejected order's anchor. It can abort the new placement ("ATR slippage at placement"), or seed the new trade's `SignalPrice` with the old anchor. The same holds after a close while the socket is down (`CancelOrderAsync` returns before its reset). | **Not fixed** — the spec's do-not-touch list covers every abort decision. Queued for an owner ruling: a separate spec, or accept. Until then, read placement-abort rows with care. |

## 4. Verified, and how

| Claim | How |
|---|---|
| Gate passes | `tools/checks/verify-gate.ps1` → `GATE PASSED` (Release sln, Debug app, Debug OrderCheck) |
| OrderCheck 323/323 | Same gate run. 307 before + 16 new: 8 ratio fixtures (spec §3 item 3: anchor 0, exact ratio, ATR 0, negative ATR, price 0, rounding, both directions, unmoved) and 8 in the migration fixture (new columns 0 on a legacy row, round-trip, abort table created empty, abort round-trips every field, an abort adds no `Trades` row, a failing abort write returns 0 without a throw, re-open keeps the abort row) |
| Nine censuses unchanged | `Select-String -AllMatches -CaseSensitive` per symbol, summing `Matches.Count`: 10 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18 = **68 across 64 lines**, before and after. `IsATRSlippageExcessive` stays at 8: no call added, and no new comment names it |
| Spec §3 item 2 (schema) on the real 89-row file | Copied `bin/x64/Debug/net9.0-windows8.0/trades.db` (89 rows, the item-C schema) to a scratch path. A scratch console probe opened it through the built `TradeDatabase`: 89 rows; all three new columns read 0 on every row; `AbortedEntries` created, 0 rows; a second open is a no-op (89 rows, 0 aborts, 19 columns). The original file is untouched (mtime still 2026-08-14 22:12) |
| Spec §3 item 5 (P/L statistics unchanged) | Same probe: the Results summary line ("Total Trades … Total P/L") computed from a raw read of the unmigrated copy, then from `GetAllTrades` after migration. Byte-identical: `Total Trades: 89 \| Wins: 22 \| Losses: 67 \| Win Rate: 24.7% \| Total P/L: $40.65`. Caveat: the "before" side is the same formula over raw rows, not the pre-change binary |
| `Trades` P/L queries untouched | Grep of every tracked `.vb` and `tools/` script: `GetAllTrades` (the trade grid and its Results summary) is the only `Trades` reader. `tools/backup-orderapp.ps1` copies the file and never queries it. Nothing filters on the new columns |

## 5. NOT verified

- **Mutation runs.** I did not mutate the code to prove each new fixture fails against a wrong implementation.
- **Thread behaviour at runtime.** The threadpool write and the fail-silent path are reasoned from code and the one fixture (a `Nothing` record returns 0). No runtime fault injection.
- **Spec §3 item 4, runtime, testnet, owner-driven** (a seat never places a trade). ⚠ **Rebuild the x64 bin first** — the gate builds AnyCPU only. Then:
  - A chased limit entry that fills → its `Trades` row has `RequoteCount > 0`, `SignalPrice` = the placement price, a plausible `SlippageATR`.
  - A forced abort (tight ATRSlip, e.g. 0.05) → one `AbortedEntries` row with reason `ATR slippage`, zero new `Trades` rows.
  - Read both with `GetAbortedEntries` or any SQLite browser on a **copy** of `trades.db`.

## 6. Do-not-touch list — confirmed

| Thing | State |
|---|---|
| Any chase or abort decision | No condition changed. Each edit is a field write or a record call beside an existing branch |
| `ChaseAbortReason` order and the `maxSlippageATRchecked` arm | `ChaseAbortReason` is byte-identical |
| `Trades` rows from aborts | None. Fixture "an abort adds NO Trades row" |
| The census symbol `IsATRSlippageExcessive` | 8, unchanged |

## Model and effort for the review

- **Model:** Opus. **Effort:** high. Entry-chase and position-model paths (`docs/HANDOVER-6.md` §7a).
