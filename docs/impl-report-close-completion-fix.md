# Implementation report — spec-close-completion-fix.md (+ review amendments)

**Date:** 2026-07-04
**Implementer:** Opus 4.8 (high)
**Base:** `4e1d751` on `master` (restore-hardening + TradeMode fix landed).
**Commit:** `63bb149` (1 commit, code). Build `dotnet build DeribitOrderPlacementApp.sln -c Debug` = **0 errors / 0 warnings** (baseline before, and after the commit). Committed locally, not pushed.
**Reviews honored:** `docs/spec-close-completion-fix.md` + `docs/review-close-completion-fix.md` (APPROVED with 5 amendments — all applied).

All code in `DeribitOrderPlacementApp/frmMainPageV2.vb`. Diff: 123 insertions, 104 deletions.

---

## What shipped

### Persistent close capture (amendment 2)

- **New fields** after `positionRestoreAnnounced`: `pendingCloseValid`, `pendingClosePorLAmt`, `pendingClosePorL`, `pendingCloseLabel`, `pendingCloseAmountUSD`, `pendingCloseWasLong`, `pendingCloseExecPrice`.
- **`ApplyCloseFill`** rewritten to a fields-only writer: signature is now `(order, execPrice, label)` — the **five ByRef outputs dropped**. Same P/L math (`signedPL` from the fill's own `direction` + `positionAvgEntry`), now stored to the fields, `pendingCloseValid = True`.
- **Five per-echo locals removed** (`PorLAmt`, `PorL`, `label4DB`, `closedAmountUSD`, `closedWasLong`); `OpenPositions` and `ExecPrice` kept (still used). The `closedWasLong = TradeMode` default is gone — the closed side is the fill's direction, fully TradeMode-independent.
- **Five call sites** updated to `ApplyCloseFill(order, ExecPrice, label)` (StopLoss/TakeProfit/Trailing/ReduceLimit/ReduceMarket filled cases).

### Transition-triggered completion (amendment 1)

- **Model loop** (`HandleOrderPositionUpdates`): captures `wasOpen = (positionSizeUSD <> 0D)` before overwriting `positionSizeUSD`; sets a per-echo `positionJustClosed = True` on `!=0 -> 0`, and clears `pendingCloseValid` on `0 -> !=0` (stale-capture guard). Avg-entry retention on the flat echo is unchanged (only updated while size ≠ 0).
- **Post-orders trigger:** `If positionJustClosed Then Await CompletePositionClose()` placed **after** the `If orders.Count > 0` gate closes but inside `If orderData IsNot Nothing` — so it fires whether or not the flat echo carried orders, and (for a co-echo close) after `ApplyCloseFill` has written the fields.
- **Old inline `size = 0` close block removed** from the positions loop (its `size <> 0` live-data-refresh branch and the `Else` reset stay). The block's `OpenPositions = False` was **dropped, not migrated** — `OpenPositions` is a per-echo local with no reader after the positions loop (amendment 1).

### `CompletePositionClose()` helper (amendments 1, 3)

Async, receive-thread. Snapshots `entryPriceAtClose` before `CancelOrderAsync` (F1 basis rule), runs the flag/display cleanup, then:
- `If pendingCloseValid`: the fix-7 three-way — profit / loss on `pendingClosePorLAmt > 0`, scratch otherwise — each "Position executed at …" line as before, followed by `RecordCompletedTrade(...)`; then `pendingCloseValid = False`.
- `Else`: bare `"Position closed."` (no record) — the external/liquidation fallback (amendment 3).

Threading: engine fields written directly, UI via `Me.Invoke`, `AppendColoredText` self-marshals, `Await CancelOrderAsync()` — same surface as the old inline block.

---

## Ordering correctness (the four cases the review called out)

| Case | Trace | Result |
|---|---|---|
| **Co-echo** (fill + flat, one message) | model loop: `wasOpen=T`, size→0 ⇒ `positionJustClosed=T`. Orders loop: `ApplyCloseFill` sets fields. Post-orders: completion consumes fresh fields. | one close, correct P/L |
| **Split** (fill echo A, flat echo B) | A: fields set, `positionJustClosed=F`, no completion, capture retained. B (no orders): `wasOpen=T`, size→0 ⇒ completion consumes A's capture. | one close, correct P/L |
| **Duplicate flat** (B') | `wasOpen=(positionSizeUSD<>0)=F` ⇒ `positionJustClosed=F`. | no re-fire |
| **Reduce-then-later-flatten** | partial fill sets fields but size ≠ 0 (no completion); final fill overwrites; flat ⇒ completion uses the last fill. | one close (last-fill P/L — see deviation 1) |

---

## Deviations / residuals (flagged)

1. **Partial-close undercount (pre-existing, out of scope).** If a close arrives as multiple partial fills, `pendingClose*` holds only the **last** fill's amount/P/L. This matches the prior per-echo behavior — no regression. This app's SL/TP close the full position in one fill in practice. A full multi-fill accumulator is a separate item.
2. **Reverse-split (flat echo before its fill echo)** — causally implausible on the raw channel (the fill causes the flat) and its residue is neutralized by the `0 -> !=0` stale-capture clear. No code for it (amendment 4); recorded here as known-theoretical.
3. **`isRequestingLiveData` reset** on a flat position still lives in the positions-loop `Else` (unchanged); the completion helper does not touch it. Harmless overlap.

---

## Acceptance summary (implementer-verifiable)

| Assertion | Result |
|---|---|
| Build 0/0 (baseline + after commit) | ✅ |
| No orphan refs to the 5 removed locals | ✅ grep clean (only `pendingClose*`) |
| `ApplyCloseFill` fields-only, 5 ByRef outs gone | ✅ |
| Completion fires outside the `orders.Count > 0` gate | ✅ trigger at the `orderData` body level |
| Fires once per transition (latch = `positionSizeUSD` sign) | ✅ by construction |
| Message/record parity on the co-echo path | ✅ same wording/colors/args, now from fields |

Owner runtime tests (spec §4 tests 1–6 + review amendment 5: re-run restore-hardening test 1 end-to-end) remain to be run on the test sub-account.
