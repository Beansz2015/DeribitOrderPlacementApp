# Spec — populate the trade-record slippage fields, and record every chase abort

**Origin:** `docs/ROADMAP-2026-08.md` §5 hygiene row "`TradeRecord.SlippageATR` / `MaxSlippageExceeded`
are declared and never written". Owner rulings 2026-10-06 (below). **Status at writing:** open, no
code written. **Status 2026-10-06: implemented**, coordinator review owed — see
`docs/impl-report-trade-slippage-fields.md`; its §3 carries three spec-back findings for §1, §2.1 and
§2.3 of this spec. Every fact below verified in code on 2026-10-06 at HEAD `4a3e29d`.
**Scope:** `TradeRecord.vb`, `TradeDatabase.vb`, `frmMainPageV2.vb`; OrderCheck fixtures.
**Touches the entry-chase and position-model paths → Opus, high.**
**Ships:** ON. Additive: new columns default to 0, a new table, no change to any trading decision.
**Depends on:** `docs/spec-frmindicators-retirement.md` landing first — this spec reads
`GetEffectiveAtr()` in its post-retirement form.

**One-line job:** record, on every completed trade, how far the entry slipped from its chase anchor
in ATR units and how many times it was re-quoted; and record every chase that aborted, which today
leaves no trace at all.

---

## §0 — Owner rulings, 2026-10-06. Do not re-litigate.

| # | Ruling |
|---|---|
| R1 | **Populate, not delete.** |
| R2 | **Completed trades + a separate `AbortedEntries` table.** The `Trades` table and every P/L, win-rate and R query stay untouched by aborts. |

Decision-bias tripwire run for R2's recommendation: `docs/harness-runs/decision-bias-20261005T1940Z-*`
— no `gives_up_for_economy` flag, stable 5/5.

## §0b — Spec-back findings, upheld at coordinator review 2026-10-06 (`docs/review-trade-slippage-fields.md`)

| ID (scope: slippage fields) | Amends | Correction |
|---|---|---|
| `SB1` | §1 | The anchor is the **placement** price, not "the own-side quote at the first reposition check": `ExecuteOrderAsync` and `StopLossForTrailingOrderAsync` run the guard before sending. For a NoSpread entry it is ask − 0.5 / bid + 0.5. |
| `SB2` | §2.3 | "Write the row before `ResetOrderAttempt`" cannot hold for the ATR arm: the guard resets the anchor itself before returning `True`. Built instead: the guard parks a record-only snapshot that the abort row reads. |
| `SB3` | §2.1 | The count follows the edit's `Await`, so it counts edits **issued**; an edit the send path skips (credit shortfall, blank amount) still counts. Accepted residual: an exact count needs the edit functions to return a sent flag, an order-path change. |

## 🚫 Do-not-touch

| Thing | Why |
|---|---|
| Any chase or abort **decision** | This spec records; it never changes when a chase moves or aborts. |
| `ChaseAbortReason` order and the `maxSlippageATRchecked` arm | See its comment, `frmMainPageV2.vb:3826`. |
| The `Trades` rows aborts would add | R2. An abort is never a `Trades` row. |
| The census symbol `IsATRSlippageExcessive` (8 occurrences) | Record from the callers or from fields; do not add calls. If a count changes, explain it. |

## §1 — The gap (verified)

- **Five** `TradeRecord` fields are never persisted, not two: `RequoteCount`, `AttemptType`,
  `SignalPrice`, `SlippageATR`, `MaxSlippageExceeded` (`TradeRecord.vb:15-19`). The `INSERT` in
  `TradeDatabase.vb:108-130` names none of them.
- **`currentRequoteCount` is never incremented.** Declared `frmMainPageV2.vb:3741`, only ever reset
  (`:3810`). The four chase-edit sites stamp `lastEntryChaseUtc` (`:2464`, `:2513`, `:2811`, `:2849`)
  but count nothing.
- **The anchor is not the signal price.** `originalSignalPrice` is seeded by the **first** call to
  `IsATRSlippageExcessive` (`:3779-3782`) — the own-side quote at the first reposition check. The
  field name `SignalPrice` is therefore misleading. Keep the name (R1 says populate the fields) and
  document the meaning in the column comment and the `TradeRecord` comment.
- A trade that never chased (a market entry, an unrepositioned limit) has anchor 0.
- **Aborts leave no record.** Every chase abort goes through `CancelWorkingEntryCoreAsync(reason)`
  (`:4351`) or returns early from `ExecuteOrderAsync` / `StopLossForTrailingOrderAsync`. Only a red
  log line survives.
- Precedent for the plumbing: the signal tag is staged as `pendingSignalId`, promoted at the
  flat→nonzero transition (`:3160-3175`), cleared on cancel (`:4375`) and on close (`:5641`). Use
  the **same stage → promote → clear shape** for the slippage snapshot.

## §2 — The change

### 2.1 Count re-quotes

Increment `currentRequoteCount` at each of the four chase-edit sites, beside the
`lastEntryChaseUtc` stamp, **only after the edit was sent**. Reset stays in `ResetOrderAttempt`.

### 2.2 Completed trades

- At the flat→nonzero transition (`:3160-3175`), snapshot into `currentTrade*` fields:
  `SignalPrice` = `originalSignalPrice`; `RequoteCount` = `currentRequoteCount`;
  `SlippageATR` = `|positionAvgEntry − anchor| / GetEffectiveAtr().Atr`, or **0 when anchor = 0**.
  Clear them with the other position fields at close (`:5636-5642`).
- `RecordCompletedTrade` (`:5654`) copies them onto the `TradeRecord`.
- `TradeDatabase`: add `SignalPrice`, `RequoteCount`, `SlippageATR` via `MigrateSchema` (one `ALTER`
  each, `NOT NULL DEFAULT 0`, same idempotent pattern as `:74-80`), add them to the `INSERT` and to
  `CreateTradeFromReader` (use the existing `ReadDecimalOrZero` helper; add an integer equivalent).
- **Delete `AttemptType` and `MaxSlippageExceeded` from `TradeRecord`.** On a completed trade
  `MaxSlippageExceeded` can never be true (an exceeded chase aborts), and `AttemptType` is always
  "Entry". Their meaning moves to `AbortedEntries` (R2).
- ⚠ **Spec-back point for the implementer:** the snapshot reads `GetEffectiveAtr()` at the fill, not
  at the anchor. If the ATR source changed between the two (payload went stale mid-chase), the ratio
  mixes two ATRs. Recommendation: snapshot the effective ATR **when the anchor is seeded**, in a new
  `anchorAtr` field reset with the anchor. Raise it as an escalation if you disagree.

### 2.3 `AbortedEntries` table (R2)

```
AbortedEntries (
  AbortId INTEGER PRIMARY KEY AUTOINCREMENT,
  Timestamp DATETIME NOT NULL,        -- UTC
  Direction TEXT NOT NULL,            -- Long / Short
  Reason TEXT NOT NULL,               -- 'ATR slippage' | 'EV floor' | the early-return path name
  Anchor DECIMAL(18,8) NOT NULL,      -- originalSignalPrice at abort
  LastQuote DECIMAL(18,8) NOT NULL,   -- the own-side quote that tripped it
  SlippageATR DECIMAL(18,8) NOT NULL, -- |LastQuote - Anchor| / ATR in force
  AtrUsed DECIMAL(18,8) NOT NULL,
  AtrSource TEXT NOT NULL,            -- GetEffectiveAtr().Source
  RequoteCount INTEGER NOT NULL,
  SignalId TEXT NOT NULL DEFAULT ''   -- the staged pendingSignalId, '' for a manual entry
)
```

- Write one row per abort, **before** `ResetOrderAttempt` clears the anchor and before
  `pendingSignalId` is cleared (`:4375`).
- Cover all three abort shapes: the `CancelWorkingEntryCoreAsync(reason)` path, and the two
  early-return guards in `ExecuteOrderAsync` (`:3909-4027`) and `StopLossForTrailingOrderAsync`
  (`:5003`, `:5044`). Name the early-return reasons distinctly so the table separates them.
- 🚨 **Thread and failure rules.** The abort sites run on the receive thread. The write must not
  block it and must never throw into it: queue the write off-thread (or reuse whatever
  `RecordCompletedTrade`'s caller does — check its thread first) and swallow and log failures, the
  same fail-silent rule as `WsEdgeLog` and the disposition log. Telemetry must never hurt the
  trading path.
- Add `GetAbortedEntries()` for later reporting. **No UI in this spec.**

## §3 — Acceptance

1. Build and gate pass; OrderCheck all pass; nine censuses re-run and any change explained.
2. **Schema:** an existing `trades.db` (copy the x64 bin's 89-row file to a scratch path) migrates
   in place — new columns read 0 on old rows; `AbortedEntries` created; a second launch is a no-op.
3. **OrderCheck (new):** the slippage-ratio function — anchor 0 → 0; anchor and ATR > 0 → exact
   ratio; ATR 0 → 0, never a divide-by-zero.
4. **Runtime, testnet, owner-driven** (seats never place trades): a chased limit entry that fills →
   its `Trades` row has `RequoteCount > 0`, `SignalPrice` = the anchor, a plausible `SlippageATR`.
   A forced abort (tight ATRSlip) → one `AbortedEntries` row, zero new `Trades` rows.
5. The P/L statistics in the Results form are byte-identical before and after on the same db.

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Model:** Opus. **Effort:** high. Entry-chase and position-model paths.
- Run **after** `docs/spec-frmindicators-retirement.md` is merged and reviewed.
- Coordinator review: Opus, regardless of implementer.
