# Spec — Position model: reduce the actual position, P/L from average entry

**Date:** 2026-07-02
**Origin:** owner decision 2026-07-02 — "Reduce Buy/Sell should reduce the current position instead of creating a separately-sized order." Full-close default per the owner's always-flat workflow.
**Recommended implementer:** Opus at **high**, or Fable 5 at **high**. This touches the close-P/L path, the emergency market-stop, and adds code to the receive-loop echo handler.
**Sequencing:** implement **only after** the owner's pending runtime tests for `spec-audit2-quickfixes.md` fix 7 and `spec-reduce-reposition.md` have passed (this spec edits the same close path — don't stack untested work).
**Target:** `DeribitOrderPlacementApp\frmMainPageV2.vb` only. Anchors reflect the file at **`1a79135`**.

---

## 1. Context (self-contained)

- Repo: `DeribitOrderPlacementApp` — .NET 9 WinForms VB.NET Deribit BTC-PERPETUAL front-end. Branch **`housekeeping-now`**; gate: `dotnet build DeribitOrderPlacementApp.sln` → 0 errors / 0 warnings after every commit; commit locally, **never push**. `Option Strict Off` — be precise with conversions.
- **The problem.** The app has no position model. Reducing and close-P/L are anchored to *locally remembered order state*:
  - Reduce buttons size from `txtAmount` and direction from the `TradeMode` toggle → an added-to position needs multiple clicks to flatten; a wrong toggle produces a silently-rejected reduce-only order.
  - **The emergency market-stop** (`SendReduceMarketOrderAsync`, called from the receive thread) sizes from `orderAmountVal` → with a stale `txtAmount` it only *partially* closes the real position. Safety gap.
  - Close P/L and the DB record use `placedPrice` (the entry *order* price) → garbage after `Cancel all open` zeroes it (bogus "profit ≈ order amount"), wrong after position adds, and direction depends on `TradeMode` at close time.
- **The fix.** Two engine fields — signed `positionSizeUSD` and `positionAvgEntry` — fed from the exchange (`user.changes` position echoes + `private/get_position` id 777, both already received), then: reduce buttons close the **full actual position** with direction from the position **sign**; close P/L / DB records / PnL display compute from **average entry** and the closing fill's own **direction and amount**.
- On Deribit there is no "decrement position" primitive — closing is always an opposite-direction `reduce_only` order. This spec changes what the orders and bookkeeping are **anchored to**, not the mechanics.

### Invariants (must survive)

1. **No WinForms control access on the receive thread.** The position pass, `ApplyCloseFill`, and `SendReduceMarketOrderAsync` (emergency path!) read/write engine fields only; `AppendColoredText` self-marshals. The reduce **buttons** are UI-thread `Click` handlers — control access is legal there, but the spec uses the engine quote fields (`BestBidPrice`/`BestAskPrice`) anyway.
2. **Do not touch** the reposition gates (`isRepositioning`, `IsCancelPending` ordering), the `cancelPending` lifecycle, the reduce-reposition context (`ReduceOrderId`/`reduceOrderPrice`/`reduceOrderAmount`/`reduceOrderIsBuy` — it keys off the **order**, stays correct, and needs zero changes), or `CancelOrderAsync`.
3. **Position fields update rule (single source of truth with a retention twist):** `positionSizeUSD` always tracks the echo; `positionAvgEntry` updates **only while size ≠ 0** — on a flat echo it *retains the basis of the just-closed position* so the close-P/L code in the same handler invocation can still read it. It is overwritten when the next position opens. Never zero it on flat.
4. Nullable guards stay null-safe (`HasValue AndAlso …`); Decimal cross-thread reads of the new fields by UI buttons are the same accepted micro-risk class as `USDPublicSession` — do not add locking.
5. Scope discipline: anything suspicious nearby goes in the report, not the diff.

---

## 2. Change 1 — the model (commit 1)

### 2a. Fields

Add directly under the reduce-reposition context block (anchor: `Private reduceOrderIsBuy As Boolean = False`, ~`:1648`):

```vb
    ' Position model (docs/spec-position-model.md): the engine's view of the ACTUAL position.
    ' size is signed USD (+long / -short) from the exchange; avg entry updates only while a
    ' position exists and RETAINS the just-closed basis on the flat echo (close-P/L reads it).
    ' Written on the receive thread; UI buttons read them (accepted Decimal torn-read class).
    Private positionSizeUSD As Decimal = 0D
    Private positionAvgEntry As Decimal = 0D
```

### 2b. Unconditional echo pass

In `HandleOrderPositionUpdates`, directly after the `Dim orderData = json.SelectToken("params.data")` / `If orderData IsNot Nothing Then` gate and **before** the orders parsing (anchor: `Dim orders = orderData.SelectToken("orders")?.ToObject(Of List(Of JObject))()`), insert:

```vb
                    ' Position model: update from EVERY positions echo, independent of the
                    ' order-context gates below (fills from other sources / liquidations included).
                    Dim posTokens = orderData.SelectToken("positions")?.ToObject(Of List(Of JObject))()
                    If posTokens IsNot Nothing Then
                        For Each p In posTokens
                            Dim sz = p.SelectToken("size")?.ToObject(Of Decimal?)()
                            If sz.HasValue Then
                                positionSizeUSD = sz.Value
                                If sz.Value <> 0D Then
                                    Dim avg = p.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                    If avg.HasValue AndAlso avg.Value > 0D Then positionAvgEntry = avg.Value
                                End If
                            End If
                        Next
                    End If
```

Ordering matters: this pass runs before the orders loop, so a same-message closing fill computes P/L against the retained basis (Deribit does not change avg entry on reduces). Do **not** modify the existing `positions` loop further down (the `size = 0` cleanup) — it keeps its own parse and gates.

### 2c. id-777 snapshots

In `ProcessPositionData`, directly after the six existing `SelectToken` extractions (anchor: `Dim averagePrice = positionData.SelectToken("average_price")?.ToObject(Of Decimal?)()`), insert:

```vb
            ' Position model: keep the engine fields current from id-777 snapshots too.
            If positionSize.HasValue Then
                positionSizeUSD = positionSize.Value
                If positionSize.Value <> 0D AndAlso averagePrice.HasValue AndAlso averagePrice.Value > 0D Then
                    positionAvgEntry = averagePrice.Value
                End If
            End If
```

### 2d. Seed at connect

In `ConnectToWebSocketDirectly`, after `Await SubscribeToUserOrders()` and before the `Me.BeginInvoke` UI update, insert:

```vb
        ' Position model: seed size/avg-entry for a restart with an already-open position
        ' (id-777 response lands in ProcessPositionData via HandleMarginEstimationResponse).
        Await GetLivePositionData("BTC-PERPETUAL")
```

---

## 3. Change 2 — reduce buttons close the actual position (commit 2)

### 3a. `btnReduceLimit_Click` — replace the body's direction/amount/price logic

Current body (anchor, ~`:3900`): the `TradeMode`-based `direction`, the `txtAmount` TryParse, and the `txtTopBid/txtTopAsk` TryParse, ending in `Await SendReduceOrderAsync(price, amount, direction, isMarketOrder:=False)`.

Replace everything between `Try` and the `SendReduceOrderAsync` call with:

```vb
            ' Position model: reduce the ACTUAL position. Direction from the position sign (a
            ' wrong TradeMode used to produce a silently-rejected reduce-only order); amount =
            ' full position size (owner's full-close workflow; reduce_only caps there anyway).
            Dim posSize As Decimal = positionSizeUSD
            If posSize = 0D Then
                AppendColoredText(txtLogs, "No open position to reduce.", Color.Yellow)
                Return
            End If
            Dim direction As String = If(posSize > 0D, "sell", "buy")
            Dim amount As Decimal = Math.Abs(posSize)

            ' Passive side for the chosen direction (engine quote fields, not textbox parses)
            Dim price As Decimal = If(direction = "buy", BestBidPrice, BestAskPrice)
            If price <= 0 Then
                AppendColoredText(txtLogs, "Invalid price.", Color.Red)
                Return
            End If

            Await SendReduceOrderAsync(price, amount, direction, isMarketOrder:=False)
```

The `Catch` stays. Note this feeds the reduce-reposition context via `SendReduceOrderAsync`'s existing seeding — `reduceOrderIsBuy` now derives from the position, so the chase direction is always correct.

### 3b. `SendReduceMarketOrderAsync` — position-sized, with a safety fallback

This function is the **emergency market-stop path** (called from the receive thread) as well as the manual button. Replace the body between the comment header and the final `SendReduceOrderAsync` call:

```vb
        ' Position model: flatten the ACTUAL position - the emergency stop must close what is
        ' really open, not what txtAmount says (a stale amount used to under-close after adds).
        Dim posSize As Decimal = positionSizeUSD
        Dim direction As String
        Dim amount As Decimal
        If posSize <> 0D Then
            direction = If(posSize > 0D, "sell", "buy")
            amount = Math.Abs(posSize)
        Else
            ' Safety fallback: model unseeded (shouldn't happen after the connect seed) - behave
            ' exactly like the old path so the emergency stop is never WEAKER than before.
            AppendColoredText(txtLogs, "Position model empty - using TradeMode/txtAmount fallback for market reduce", Color.Orange)
            direction = If(TradeMode, "sell", "buy")
            amount = orderAmountVal
            If amount <= 0 Then
                AppendColoredText(txtLogs, "Invalid amount.", Color.Red)
                Return
            End If
        End If

        ' Call the function to send the reduce-only market order
        Await SendReduceOrderAsync(Nothing, amount, direction, isMarketOrder:=True)
```

No control reads anywhere in this function (invariant 1). `btnReduceMarket_Click` is untouched.

---

## 4. Change 3 — close P/L, DB record, and PnL display from the model (commit 3)

### 4a. Handler locals

Anchor (~`:1666`): `Dim label4DB As String = Nothing`. Add directly after:

```vb
                        Dim closedAmountUSD As Decimal = 0D   ' position model: actual closed size from the fill echo
                        Dim closedWasLong As Boolean = TradeMode ' position model: closed side (from the fill's direction)
```

### 4b. The shared close-fill helper

Add as a new method near `RecordCompletedTrade`:

```vb
    ' Position-model close P/L (docs/spec-position-model.md). Basis = exchange average entry
    ' (positionAvgEntry, retained through the flat echo); side = the closing fill's OWN direction
    ' (a closing SELL means the position was long); size = the fill's own amount. Replaces the
    ' old placedPrice/TradeMode/orderAmountVal math, which was wrong after Cancel-All (basis
    ' zeroed), after adds (order price <> avg entry), and after mode flips. Receive-thread safe:
    ' reads engine fields only. If the model is unseeded (avg = 0) the P/L is 0 -> the close
    ' lands in the fix-7 scratch path instead of recording garbage.
    Private Sub ApplyCloseFill(order As JObject, execPrice As Decimal, label As String,
                               ByRef porLAmt As Decimal, ByRef porL As Boolean, ByRef label4DB As String,
                               ByRef closedAmountUSD As Decimal, ByRef closedWasLong As Boolean)
        Dim fillDir As String = order.SelectToken("direction")?.ToString()
        Dim fillAmt As Decimal = If(order.SelectToken("amount")?.ToObject(Of Decimal?)(), 0D)
        Dim signedPL As Decimal = 0D
        If execPrice > 0D AndAlso positionAvgEntry > 0D AndAlso fillAmt > 0D Then
            signedPL = If(fillDir = "sell", execPrice - positionAvgEntry, positionAvgEntry - execPrice) * (fillAmt / execPrice)
        End If
        porL = (signedPL >= 0D)
        porLAmt = Math.Abs(Math.Round(signedPL, 2, MidpointRounding.AwayFromZero))
        closedAmountUSD = fillAmt
        closedWasLong = (fillDir = "sell")
        label4DB = label
    End Sub
```

### 4c. The five closing-fill cases (`orderState = "filled"` branch)

Replace each case's P/L block with an `ApplyCloseFill` call, preserving everything else in the case:

**`Case "TakeLimitProfit"`** — currently sets `ExecPrice` from `price`, then four lines of `PorLAmt`/`PorL`/`label4DB` math. New body:

```vb
                                    Case "TakeLimitProfit"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                        ApplyCloseFill(order, ExecPrice, label, PorLAmt, PorL, label4DB, closedAmountUSD, closedWasLong)
```

**`Case "StopLossOrder"`** — same shape; **keep** `SLTriggered = False`:

```vb
                                    Case "StopLossOrder"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                        SLTriggered = False
                                        ApplyCloseFill(order, ExecPrice, label, PorLAmt, PorL, label4DB, closedAmountUSD, closedWasLong)
```

(The old `'Need to calculate PorL when trailing stop loss is triggered` comment may be dropped with the replaced lines.)

**`Case "TrailingStopLoss"`** — `ExecPrice` stays sourced from `average_price`:

```vb
                                    Case "TrailingStopLoss"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                        ApplyCloseFill(order, ExecPrice, label, PorLAmt, PorL, label4DB, closedAmountUSD, closedWasLong)
```

**`Case "ReduceLimitOrder"`** — the entire nested `If TradeMode … Else …` block (both outer branches, four inner assignments) collapses to one call; **keep** the reduce-reposition context clears at the end:

```vb
                                    Case "ReduceLimitOrder"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                        ApplyCloseFill(order, ExecPrice, label, PorLAmt, PorL, label4DB, closedAmountUSD, closedWasLong)

                                        ' Reduce order gone from the book - drop the reposition context.
                                        ReduceOrderId = Nothing
                                        reduceOrderPrice = 0D
                                        reduceOrderAmount = 0D
```

**`Case "ReduceMarketOrder"`** — market reduces become tracked closes (the model makes it trivial and puts emergency closes in the stats). Keep `OpenPositions`/`LogTradeDecision`/`ResetOrderAttempt` and the fix-6 price line; **drop** the `"P/L not tracked for market reduces - check order history."` line:

```vb
                                    Case "ReduceMarketOrder"
                                        OpenPositions = True 'Actually no positions but flagged true to use code in openpositions segment for cleanup
                                        If _indicators.IsAutoTradingEnabled Then
                                            LogTradeDecision("Exit Position - Market Order Loss", 0, 0) 'For autotrade log for when trade exit position
                                        End If
                                        ResetOrderAttempt() ' Reset ATR slippage tracking
                                        ' Audit2 fix 6 + position model: log the echo's actual fill price and track the
                                        ' close like every other fill (P/L vs avg entry; emergency closes hit the stats).
                                        Dim reduceFill = order.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                        ExecPrice = If(reduceFill, 0D)
                                        AppendColoredText(txtLogs, $"Position reduced at {If(reduceFill?.ToString("F2"), If(newPricePublic > 0D, newPricePublic.ToString("F2"), "?"))} (market order).", Color.Crimson)
                                        ApplyCloseFill(order, ExecPrice, label, PorLAmt, PorL, label4DB, closedAmountUSD, closedWasLong)
```

### 4d. The `size = 0` branch — basis snapshot, record args, comment truth

1. The fix-1 snapshot line becomes model-first with the old behavior as fallback:

```vb
                                        ' Audit2 F1 + position model: snapshot the closing basis BEFORE CancelOrderAsync
                                        ' zeroes placedPrice. Avg entry is the true basis (survives adds/Cancel-All);
                                        ' placedPrice remains the fallback for an unseeded model.
                                        Dim entryPriceAtClose As Decimal = If(positionAvgEntry > 0D, positionAvgEntry, placedPrice)
```

2. The `RecordCompletedTrade` call: `orderAmountVal` → `If(closedAmountUSD > 0D, closedAmountUSD, orderAmountVal)`, and `TradeMode` → `closedWasLong`. (`entryPriceAtClose`, `ExecPrice`, `PorLAmt`, `PorL`, `label4DB` stay.)

3. Comment truth updates — market reduces are no longer untracked:
   - Fix-7 scratch-branch comment: replace its four lines with:

```vb
                                            ' Audit2 fix 7 + position model: a tracked close whose P/L rounds to $0.00
                                            ' (scratch) still logs and records. No LogTradeDecision call here: it has no
                                            ' scratch branch and would write an empty file line.
```

   - DB-gate comment: replace its three lines with:

```vb
                                        ' Audit2 fix 7 + position model: record every computed close (label4DB set) -
                                        ' $0.00 scratches and market reduces included; they are real trades and their
                                        ' absence biased the stats.
```

### 4e. Live PnL display (in `HandleQuoteUpdates`) — position-first, minimal diff

Anchor: `If placedPriceValid AndAlso placedPrice > 0 AndAlso amountValid Then` (the `lblPnL` block). Prepend effective-input resolution and swap the block's inputs — do **not** restructure the `Invoke` calls or fix the known mark-to-ask quirk (separately tracked):

```vb
                ' Position model: when a position exists, display P/L against the avg-entry basis
                ' and the real size; otherwise keep the resting-order hypothetical (old behavior).
                Dim dispLong As Boolean = TradeMode
                Dim dispBasis As Decimal = placedPrice
                Dim dispAmt As Decimal = orderAmountVal
                If positionSizeUSD <> 0D AndAlso positionAvgEntry > 0D Then
                    dispLong = (positionSizeUSD > 0D)
                    dispBasis = positionAvgEntry
                    dispAmt = Math.Abs(positionSizeUSD)
                End If
                If dispBasis > 0D AndAlso dispAmt > 0D Then
```

Then, **inside this block only**, replace: `If TradeMode = True Then` → `If dispLong Then`, both `placedPrice` occurrences in the two PnL formulas → `dispBasis`, both `orderAmountVal` occurrences → `dispAmt`, and the two `BestAskPrice < placedPrice` / `BestAskPrice > placedPrice` color conditions → `dispBasis`. The `Else` (zero display) branch is unchanged. The old outer condition (`placedPriceValid AndAlso placedPrice > 0 AndAlso amountValid`) is fully replaced by `dispBasis > 0D AndAlso dispAmt > 0D`.

---

## 5. Known limitations (owner-accepted; restate in the report)

- **Multi-fill closes record the last fill only** — the DB write happens once, on the `size = 0` echo, with that message's fill values. Pre-existing shape (it previously recorded `orderAmountVal` with the last fill's P/L); per-close aggregation is out of scope.
- `positionAvgEntry` retention on flat means a *stale* basis exists until the next position opens — only the same-message close path reads it, so this is by design (invariant 3).
- Cross-thread Decimal reads of the new fields by UI buttons: accepted torn-read class (house pattern).
- `LogTradeDecision("Exit Position - Market Order Loss", 0, 0)` file-log wording stays (scope).

---

## 6. Commit plan

1. `Position-model (1/3): engine fields + echo/id-777 updates + connect seed`
2. `Position-model (2/3): reduce buttons close the actual position (full-close, sign-derived direction)`
3. `Position-model (3/3): close P/L, DB record, PnL display from avg entry; market reduces tracked`

Build 0/0 before each commit.

---

## 7. Test plan

**Implementer-verifiable:** build green ×3; grep assertions — `ApplyCloseFill(` called from exactly 5 cases; no `.Text` reads in `SendReduceMarketOrderAsync`, `ApplyCloseFill`, or the new position pass; `RecordCompletedTrade` call passes `closedWasLong` and the `closedAmountUSD` fallback expression; the fix-7 comments no longer claim market reduces are untracked; reduce-reposition context code untouched by commits 1 and 3.

**Owner runtime (test sub-account, VS debugger):**
1. **Seed:** with a position already open, restart the app and connect → press Reduce (limit) → the full position closes (proves the connect seed; no "No open position" refusal).
2. **Full-close:** two entries (10 + 10) → one Reduce click closes all 20; DB row shows entry = blended avg, size = 20, correct direction, sensible P/L.
3. **The original bug:** enter → Cancel all open → Reduce → chased order fills → **no bogus "profit ≈ amount"**; P/L is the real few-dollar figure vs. avg entry.
4. **Mode-flip:** open a long, toggle the mode to Sell, press Reduce → still closes (sell) correctly.
5. **Market reduce:** now produces the standard close lines *and* a DB row (entry = avg, exit = market avg fill).
6. **Scratch regression (fix 7):** tiny quick close → `Scratch close: P/L ≈ $0.00.` + recorded.
7. **PnL label:** after an add, the displayed P/L tracks the blended avg entry, not the last order price.

---

## 8. Implementation report (required)

Produce `docs/impl-report-position-model.md`: per change — exact lines (before → after), build results, deviations with justification, suspicious-nearby items deliberately not touched. The owner pastes it back for code-level review; write it to be checkable, not persuasive.
