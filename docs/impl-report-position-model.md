# Implementation report — spec-position-model.md

**Date:** 2026-07-02
**Implementer:** Fable 5 (high)
**Base:** `1a79135` on `housekeeping-now`. Anchors matched the spec exactly; no drift.
**Commits:** `d314fff` (1/3), `3041ad4` (2/3), `5da2e5b` (3/3). Build `dotnet build DeribitOrderPlacementApp.sln` = **0 errors / 0 warnings before each commit** (verified three times, once per commit). Committed locally, not pushed.

All line numbers below refer to the file state **after** all three commits unless marked "was".

---

## Commit 1 — `d314fff` — fields + echo/id-777 updates + connect seed

### 2a. Fields (`frmMainPageV2.vb:1681-1687`)

Inserted verbatim from the spec directly under `Private reduceOrderIsBuy As Boolean = False` (was :1679), separated by blank lines from the reduce-reposition block above and `isTrailingStop` below. **Cosmetic deviation:** spec showed no surrounding blank lines; I added one before and one after for readability. No code difference.

### 2b. Unconditional echo pass (`frmMainPageV2.vb:1706-1720`)

Inserted verbatim between `If orderData IsNot Nothing Then` and the `Dim orders = ...` parse in `HandleOrderPositionUpdates`. Runs before the orders loop as required (same-message closing fill reads the retained basis). The pre-existing `positions` loop further down (`size = 0` cleanup, now ~:2082) was **not** modified — it keeps its own parse and gates.

### 2c. id-777 snapshots (`frmMainPageV2.vb:3676-3682`)

Inserted verbatim in `ProcessPositionData` directly after the six `SelectToken` extractions (anchor `Dim averagePrice = ...`, before the `' CORRECTED:` comment).

### 2d. Connect seed (`frmMainPageV2.vb:618-620`)

Inserted verbatim in `ConnectToWebSocketDirectly` between `Await SubscribeToUserOrders()` and the `Me.BeginInvoke` UI update. Verified `GetLivePositionData` is safe here: it has its own `isRequestingLiveData` guard and rate-limiter check, and only *sends* the id-777 request — the response is consumed by the receive loop, which starts two lines later (`ReceiveWebSocketMessagesAsync`), so nothing races.

Before → after summary: 36 insertions, 0 deletions.

---

## Commit 2 — `3041ad4` — reduce buttons close the actual position

### 3a. `btnReduceLimit_Click` (`frmMainPageV2.vb:4055-4086`)

Everything between `Try` and the `SendReduceOrderAsync` call replaced verbatim with the spec block (position-sign direction, `Math.Abs(posSize)` amount, `BestBidPrice`/`BestAskPrice` passive price, "No open position to reduce." refusal). Removed: the `TradeMode` direction block, the `txtAmount.Text` TryParse, the `txtTopBid.Text`/`txtTopAsk.Text` TryParse. The `Catch` is untouched. Verified `BestBidPrice`/`BestAskPrice` are engine fields (`Public BestBidPrice, BestAskPrice, TPTrailprice As Decimal`, :41), written by `HandleQuoteUpdates` — no control reads.

### 3b. `SendReduceMarketOrderAsync` (`frmMainPageV2.vb:4093-4116`)

Body between the (unchanged) comment header and the final `SendReduceOrderAsync` call replaced verbatim with the spec block: position-sized close when `posSize <> 0D`, else the exact old `TradeMode`/`orderAmountVal` path behind an orange fallback log. No `.Text` reads anywhere in the function (`txtLogs` is only passed as a control reference to the self-marshalling `AppendColoredText`, same as before). `btnReduceMarket_Click` untouched.

Before → after summary: 27 insertions, 29 deletions.

---

## Commit 3 — `5da2e5b` — close P/L, DB record, PnL display

### 4a. Handler locals (`frmMainPageV2.vb:1725-1726`)

`closedAmountUSD` / `closedWasLong` inserted verbatim directly after `Dim label4DB As String = Nothing`.

### 4b. `ApplyCloseFill` (`frmMainPageV2.vb:3506-3527`)

Added verbatim directly above `RecordCompletedTrade`. Reads engine fields only (`positionAvgEntry`); no control access.

### 4c. The five closing-fill cases (`orderState = "filled"` branch)

Each replaced exactly as specified; grep confirms `ApplyCloseFill(` appears at exactly 5 call sites (:1943, :1949, :1964, :1969, :1987) plus the declaration (:3506).

- **`TakeLimitProfit`** (:1940-1943) — old four-line `PorLAmt`/`PorL`/`label4DB` math → `ApplyCloseFill` call. `ExecPrice` still from `price`.
- **`StopLossOrder`** (:1945-1949) — same; `SLTriggered = False` kept, moved above the call per the spec's layout. The old `'Need to calculate PorL when trailing stop loss is triggered` comment dropped (spec-sanctioned).
- **`TrailingStopLoss`** (:1961-1964) — `ExecPrice` stays sourced from `average_price`.
- **`ReduceLimitOrder`** (:1966-1974) — the entire nested `If TradeMode … Else …` block (both outer branches, four inner assignment groups) collapsed to one call; the three reduce-reposition context clears kept verbatim at the end.
- **`ReduceMarketOrder`** (:1976-1987) — kept `OpenPositions`/`LogTradeDecision`/`ResetOrderAttempt` and the fix-6 price log line; added `ExecPrice = If(reduceFill, 0D)` and the `ApplyCloseFill` call; **dropped** the `"P/L not tracked for market reduces - check order history."` line; comment updated to the spec's "Audit2 fix 6 + position model" wording.

### 4d. `size = 0` branch

1. Basis snapshot (:2110-2113): `Dim entryPriceAtClose As Decimal = If(positionAvgEntry > 0D, positionAvgEntry, placedPrice)` with the spec's three-line comment (was: bare `= placedPrice` with the fix-1 two-line comment).
2. `RecordCompletedTrade` call (:2170-2179): `orderAmountVal` → `If(closedAmountUSD > 0D, closedAmountUSD, orderAmountVal)`; `TradeMode` → `closedWasLong`. Other five args unchanged.
3. Comment truth: fix-7 scratch-branch comment (:2156-2158) and DB-gate comment (:2166-2168) replaced with the spec's wording; neither claims market reduces are untracked any more (grep for `untracked|not tracked` over the file: 0 hits).

### 4e. Live PnL display (`HandleQuoteUpdates`, :1584-1644)

Prepended the `dispLong`/`dispBasis`/`dispAmt` resolution verbatim; outer condition `placedPriceValid AndAlso placedPrice > 0 AndAlso amountValid` → `dispBasis > 0D AndAlso dispAmt > 0D`. Inside the block only: `If TradeMode = True Then` → `If dispLong Then`; both PnL formulas now use `dispBasis`/`dispAmt`; both color conditions compare `BestAskPrice` to `dispBasis`. The `Else` zero-display branch, the `Me.Invoke` structure, and the mark-to-ask quirk are untouched as instructed.

Before → after summary: 61 insertions, 64 deletions.

---

## Implementer-verifiable test results (spec §7)

| Assertion | Result |
|---|---|
| Build green ×3 (once per commit) | ✅ 0 errors / 0 warnings each time |
| `ApplyCloseFill(` called from exactly 5 cases | ✅ :1943, :1949, :1964, :1969, :1987 (+ decl :3506) |
| No `.Text` reads in `SendReduceMarketOrderAsync`, `ApplyCloseFill`, or the new position pass | ✅ verified by inspection; only `txtLogs` passed as a reference to self-marshalling `AppendColoredText` |
| `RecordCompletedTrade` passes `closedWasLong` and the `closedAmountUSD` fallback expression | ✅ :2173, :2176 |
| Fix-7 comments no longer claim market reduces are untracked | ✅ grep `untracked\|not tracked` → 0 hits |
| Reduce-reposition context untouched by commits 1 and 3 | ✅ `git diff` over both commits: no `+`/`-` line touches `ReduceOrderId`/`reduceOrderPrice`/`reduceOrderAmount`/`reduceOrderIsBuy` (commit 3's `ReduceLimitOrder` rewrite preserves the clears byte-identically; they appear only as diff context) |

Owner runtime tests 1–7 (spec §7) remain to be run on the test sub-account.

---

## Deviations

1. **2a blank lines** — one blank line before and after the new field block (readability; spec showed the block flush under the anchor). No semantic difference.
2. **4c `StopLossOrder` comment** — the old `'Need to calculate PorL…'` comment was dropped along with the replaced lines, which the spec explicitly permits.
3. **4e leaves `amountValid` unused** — `Dim amountValid As Boolean = orderAmountVal > 0D` (:1256) now has no readers (the lblPnL block was its only consumer). Left in place: the spec's replacement text covers only the `If` line, VB emits no unused-local warning (build stays 0/0), and removing it would widen the diff beyond the spec. Flagged for a future housekeeping pass.

No other deviations; all inserted blocks are byte-for-byte the spec's code.

---

## Known limitations (owner-accepted, restated from spec §5)

- **Multi-fill closes record the last fill only** — the DB write happens once, on the `size = 0` echo, with that message's fill values. Pre-existing shape; per-close aggregation is out of scope.
- `positionAvgEntry` retention on flat means a *stale* basis exists until the next position opens — only the same-message close path reads it (invariant 3, by design).
- Cross-thread Decimal reads of the new fields by UI buttons: accepted torn-read class (house pattern, same as `USDPublicSession`).
- `LogTradeDecision("Exit Position - Market Order Loss", 0, 0)` file-log wording stays (scope).

---

## Suspicious nearby — deliberately not touched

1. **Name shadowing:** `CalculateDeribitInverseLiquidationPrice(positionSizeUSD As Decimal, …)` (:3565) has a *parameter* named identically to the new field. Inside that function the parameter shadows the field (VB rule), so behavior is unchanged — but the collision is confusable for future readers. Rename candidate for a housekeeping pass.
2. **Nullable-to-Decimal `ExecPrice` assignments:** `ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()` (TakeLimitProfit/StopLossOrder/ReduceLimitOrder/TrailingStopLoss) assigns a `Decimal?` to a `Decimal` under `Option Strict Off`; a fill echo missing the field would throw `InvalidOperationException` (swallowed by the handler's outer `Try`, logged red). Pre-existing in all four cases; the new `ReduceMarketOrder` case avoids it via `ExecPrice = If(reduceFill, 0D)`. Not normalized (scope).
3. **Duplicate positions parse:** the new 2b pass and the pre-existing `size = 0` loop (:2082) each call `orderData.SelectToken("positions")?.ToObject(...)` on the same message. Spec explicitly forbids merging them; minor redundant work only.
4. **`SendReduceMarketOrderAsync` header comment** (:4090-4092) still says "Reads orderAmountVal, not txtAmount" — now only true of the fallback branch. Left because the spec's replacement region starts *below* the header comment.
5. **lblPnL marks both long and short to `BestAskPrice`** (mark-to-ask quirk) — explicitly out of scope per spec §4e ("separately tracked").
6. **Full-close amount vs. exchange lag:** `btnReduceLimit_Click` sizes from `positionSizeUSD`, which can momentarily lag the exchange after a partial fill; `reduce_only` caps the order at the actual position, so any overage is clipped server-side (spec §3a notes this).
