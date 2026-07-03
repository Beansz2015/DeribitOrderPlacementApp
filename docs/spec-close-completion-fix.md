# Spec — Reliable position-close completion (message + DB record + cleanup must not depend on echo batching)

**Date:** 2026-07-03. **Origin:** owner runtime test of the restore-hardening pass — when a triggered SL finally executed and closed the position, **no close message appeared** and (worse) the position-closed cleanup did not run. Surfaced during restore testing but **pre-existing and independent** of restore hardening. **Model/effort:** Opus/Fable **high** (touches the P/L + trade-DB accounting path). **Base:** `4e1d751` on `master`.

**Scope note:** this is NOT part of the restore-hardening pass. It is filed separately because it changes the close-accounting path and needs its own Test-5 regression. The restore-hardening TradeMode fix (`4e1d751`) is unrelated to this and already landed.

**Status:** APPROVED with amendments (`docs/review-close-completion-fix.md`) and **IMPLEMENTED** at `63bb149`. The five review amendments are folded into the design below (see `docs/impl-report-close-completion-fix.md` for the as-built delta): (1) `OpenPositions = False` dropped, not promoted to a field — it is a per-echo local; (2) `ApplyCloseFill` is fields-only, its five ByRef outs and the five dead per-echo locals removed; (3) gating is `If pendingCloseValid` with the fix-7 three-way + `pendingCloseValid = False` fallback; (4) reverse-split left as known-theoretical (neutralized by the stale-capture clear); (5) re-run restore-hardening test 1 end-to-end after this lands.

---

## 1. The trace — why the close message/record/cleanup is lost

`HandleOrderPositionUpdates` (`frmMainPageV2.vb:1902`, receive thread) processes a `user.changes.BTC-PERPETUAL.raw` echo. Structure:

```
If orderData IsNot Nothing Then
    posTokens = orderData.positions                 ' :1917  MODEL loop - runs every echo
    For Each p In posTokens: positionSizeUSD/avg update

    If orders IsNot Nothing AndAlso orders.Count > 0 Then          ' :1932  ORDERS GATE
        Dim ExecPrice, PorLAmt, PorL, label4DB, closedAmountUSD, closedWasLong   ' :1971  PER-ECHO LOCALS
        For Each order In orders:
            ... ElseIf orderState = "filled":
                Case "StopLossOrder": ApplyCloseFill(...) -> sets the per-echo locals   ' :2183
        Next
        If OpenPositions = True Then                               ' :2255
            positions = orderData.positions                        ' :2291
            If positions.Count > 0 Then
                For Each position:
                    If size = 0 Then                               ' :2323  CLOSE branch
                        [flag resets, CancelOrderAsync, margin reset]
                        [close message from PorLAmt/label4DB]      ' :2357-2379
                        [RecordCompletedTrade if label4DB]         ' :2386-2396
```

Two structural facts combine into the bug:

1. **The close message/record read per-echo locals** (`PorLAmt`, `label4DB`, `ExecPrice`, …, declared at `:1971`) that `ApplyCloseFill` sets **only in the echo that carried the closing fill**.
2. **The entire close branch is nested under `If orders.Count > 0`** (`:1932`) → `If OpenPositions` (`:2255`) → `If positions.Count > 0` (`:2292`) → `If size = 0` (`:2323`).

### Failure mode (what the owner hit)

Deribit delivered the closing fill and the flat-position update in **separate** echoes (allowed — the raw channel emits changes as they occur):

- **Echo A** = `orders:[StopLossOrder filled]`, position still `size ≠ 0` (or no `positions` token). `ApplyCloseFill` sets the locals, but the `size = 0` branch isn't reached, so the locals are **discarded** at echo end.
- **Echo B** = `positions:[size = 0]`, **no `orders`**. `orders.Count > 0` is false → the whole block (`:1932`) is skipped. The model loop zeroes `positionSizeUSD`, but there is **no close message, no DB record, and none of the `:2330-2354` cleanup** (`OpenPositions=False`, `SLTriggered=False`, `StopLossTriggerOriginal=0`, `isTrailing*`, `CancelOrderAsync`, margin-display reset).

Result: the trade is silently unrecorded, and the app can be left believing it is still in a position (stale flags/displays, lingering OCO sibling context). Co-echoed closes (the common case) work, which is why this is intermittent.

**Not caused by TradeMode:** `ApplyCloseFill` (`:3817`) derives P/L and side from the fill's own `direction` + `positionAvgEntry`, and always sets `label4DB`. The wrong TradeMode from the earlier bug did not contribute.

---

## 2. Fix design

Two changes: **(A)** persist the close-fill P/L so it survives across echoes, and **(B)** trigger close-completion off the position transition, outside the orders gate.

### 2a. Persist close-fill state in fields (replaces the per-echo locals for close purposes)

New engine fields (receive-thread owned):

```vb
Private pendingCloseValid As Boolean = False
Private pendingClosePorLAmt As Decimal = 0D
Private pendingClosePorL As Boolean = True
Private pendingCloseLabel As String = Nothing
Private pendingCloseAmountUSD As Decimal = 0D
Private pendingCloseWasLong As Boolean = False
Private pendingCloseExecPrice As Decimal = 0D
```

`ApplyCloseFill` writes these fields (instead of / in addition to the ByRef locals) and sets `pendingCloseValid = True`. Side stays `fillDir = "sell"` ⇒ was-long (restore-safe; independent of `TradeMode`).

### 2b. Detect the close transition in the model loop; complete after the orders block

- In the model loop (`:1917`), capture the transition **before** overwriting `positionSizeUSD`:
  - `Dim wasOpen = (positionSizeUSD <> 0D)` then assign `positionSizeUSD = sz.Value`.
  - If `sz.Value = 0D AndAlso wasOpen` ⇒ set a per-echo `positionJustClosed = True`.
  - If `sz.Value <> 0D AndAlso Not wasOpen` ⇒ **new position opened**: set `pendingCloseValid = False` (drop any stale capture).
- **After** the `If orders.Count > 0` block (so `ApplyCloseFill` has run when the fill was co-echoed), add — still inside `If orderData IsNot Nothing`, outside the orders gate:
  ```vb
  If positionJustClosed Then Await CompletePositionClose()
  ```

Because `positionSizeUSD` is set both by the model loop and by id-777 `ProcessPositionData`, a position restored after a restart correctly has `wasOpen = True` when its close echo later arrives.

### 2c. Extract `CompletePositionClose()` (async, receive-thread)

Move the existing close body (`:2323-2399`) into one helper, driven by the pending fields:

```vb
Private Async Function CompletePositionClose() As Task
    Dim entryPriceAtClose As Decimal = If(positionAvgEntry > 0D, positionAvgEntry, placedPrice)

    ' flag/display cleanup (verbatim from the current size=0 branch)
    OpenPositions = False : isTrailingStop = False : isTrailingPosition = False
    isTrailingStopLossPlaced = False : SLTriggered = False : StopLossTriggerOriginal = 0
    PositionEmpty = True : PositionLog = False : OrderLog = False
    Await CancelOrderAsync()
    Me.Invoke(Sub() ... reset lblEstimatedLiquidation/IM/MM/Lev ...)

    If pendingCloseValid Then
        ' message: profit / loss / scratch - same wording as today, from pendingClose* fields
        ' RecordCompletedTrade(entryPriceAtClose, pendingCloseExecPrice, pendingCloseAmountUSD,
        '                      pendingClosePorLAmt, pendingClosePorL, pendingCloseWasLong, pendingCloseLabel)
        pendingCloseValid = False
    Else
        AppendColoredText(txtLogs, "Position closed.", Color.Yellow)   ' fallback: external/liquidation close, no P/L basis
    End If
    _indicators.lastAutoTradeTime = DateTime.Now
End Function
```

The existing `size = 0` branch inside the positions loop is **removed** (its cleanup+message now lives in the helper, invoked by 2b); the `size <> 0` branch (re-request live data, `PositionLog`) stays. This removes the duplication and the double-trigger risk (the helper runs exactly once per non-zero→zero transition).

---

## 3. Edge cases / risks (review checklist)

1. **No double-fire:** completion fires only on the `≠0 → 0` transition (`wasOpen` guard); the latch is `positionSizeUSD` itself. Co-echo → fires once after the orders loop. Split → fires once on the flat echo. A duplicate flat echo has `wasOpen = False` ⇒ no re-fire.
2. **Stale capture:** `pendingCloseValid` is cleared both on consumption and on a new-position open (`0 → ≠0`), so a reduce that set it but didn't flatten can't mis-attribute to a later trade.
3. **Partial closes:** if a close arrives as multiple partial fills, `pendingClose*` holds only the **last** fill's amount/P/L — an undercount. This matches the current per-echo behavior (no regression) and is explicitly **out of scope**; note in the impl report. This app's SL/TP close the full position in one fill in practice.
4. **Threading:** `CompletePositionClose` runs on the receive thread; UI via `Me.Invoke`, `AppendColoredText` self-marshals, `Await CancelOrderAsync()` as today. No new cross-thread surface.
5. **Parity on the common path:** for a co-echoed close the message text, colors, and `RecordCompletedTrade` args are identical to today (same values, now via fields). Verify by diffing the emitted lines in a normal cycle.
6. **`CancelOrderAsync` ordering:** now fires from the post-orders completion rather than mid-positions-loop. For a split close it fires on the flat echo (slightly later) — strictly better than never. For a co-echo it fires at the same logical point (after the orders loop). Confirm no reliance on it running before the orders loop (there is none today).

---

## 4. Test plan (owner, test sub-account)

1. **Split-echo close (the failing case):** reproduce a close where the SL fill and the flat position arrive separately (the restore scenario reproduced it). Expect: `Position executed at … / Loss of $…`, a DB row, and full cleanup (status clears, margins → N/A, no lingering "in position"). 
2. **Normal co-echoed close regression:** ordinary TP hit and SL hit, no restart. Message + DB record + cleanup identical to before.
3. **Scratch close** (P/L ≈ $0): still logs "Scratch close" and records.
4. **Market/emergency reduce close:** `ReduceMarketOrder` path still logs and records.
5. **External/liquidation close** (no tracked fill): fallback "Position closed." + cleanup, no DB row.
6. **Restart + close:** restore into a position, close it → completion fires (transition seen because id-777 seeded `positionSizeUSD`).

---

## 5. Implementation report

`docs/impl-report-close-completion-fix.md`, standard format; docs committed alongside.
