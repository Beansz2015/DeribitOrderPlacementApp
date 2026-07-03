# Spec — Restart restore hardening (trade-context restore; SL trailing must survive a restart)

**Date:** 2026-07-03. **Origin:** owner runtime test of the resilience pass — after a restart with an open SHORT and a later SL trigger, the triggered SL was **never repositioned** and the emergency stop was dead.
**Root causes (all pre-existing, exposed by the restore display):** `TradeMode` defaults to LONG at startup (a restored SHORT runs the wrong trailing branch, silently); `StopLossTriggerOriginal` is 0 after restart (kills the emergency math — and once TradeMode is fixed, a zero baseline would fire an **instant** emergency close for shorts: `bestBid − 0 ≥ threshold`); the OTOCO children (order ids, prices) are never fetched at connect, so the leg displays are empty and order context is missing until an echo happens to arrive.
**Priority: implement BEFORE the tie-in** (a restored position that displays as managed but isn't SL-trailed is a live trap). Opus/Fable **high**. Base: resilience HEAD (`a42aa76`+). Standing ground rules + `HANDOVER-2.md` §4 invariants apply (receive thread = engine fields; seed-only-when-zero; `UiInvoke` for display).

---

## Commit 1 — zero-baseline guard (safety prerequisite, ships first)

In `UpdateStopLossForTriggeredStopLossOrder`, add `StopLossTriggerOriginal > 0D AndAlso` to **both** emergency conditions (long + short branches). In `HandleQuoteUpdates`' triggered-SL emergency pre-check, the same: the `priceMovement`-based emergency (`emergencyThresholdValid AndAlso priceMovement >= …` and the `* 0.5` force-path chooser) additionally requires `StopLossTriggerOriginal > 0D`. Semantics: **an unknown baseline disables the emergency market-stop** (same philosophy as threshold-0-disables); normal SL trailing is unaffected. This must land before commit 2 makes `TradeMode` correct for restored shorts.

## Commit 2 — trade-context restore at connect

### 2a. TradeMode + placedPrice from the position (in the existing restore-announce block, `ProcessPositionData`)

Inside `If positionSize.HasValue AndAlso positionSize.Value <> 0D AndAlso Not positionRestoreAnnounced`:

```vb
                ' Restore hardening: trade context must match the REAL position, or the SL-trailing
                ' and emergency branches run the wrong side (TradeMode defaults to LONG at startup).
                ' SetTradeMode touches controls -> inside the existing UiInvoke.
                ' placedPrice: restart-restore exception to placement-only seeding (it IS 0 here;
                ' no working entry exists, so no reposition can act on it) - gives the PnL/display
                ' path its basis back and stops the 5s "Placed price = 0" warning loop.
```

- In the existing `UiInvoke` block add `SetTradeMode(positionSize.Value > 0D)` as its first line.
- After the `UiInvoke`, add: `If placedPrice = 0D Then placedPrice = If(averagePrice, 0D)` (+ mirror `txtPlacedPrice` is already set there).

### 2b. Open-orders snapshot (new request id **778**)

- `ConnectToWebSocketDirectly`: after `Await GetLivePositionData("BTC-PERPETUAL")`, add `Await RequestOpenOrdersSnapshot()` — a send-only helper posting `private/get_open_orders_by_instrument` (`instrument_name` BTC-PERPETUAL, id 778). Responses drain once the receive loop starts (same pattern as the id-777 seed).
- New handler `HandleOpenOrdersSnapshot(response)` wired into the dispatch (after `HandleMarginEstimationResponse`). Null-safe id-gate (`HasValue AndAlso = 778`); iterate `result` array; for each order map by `label` + `order_state`, **mirroring the echo handler's semantics** (seed-only-when-zero everywhere a price is seeded; displays via `UiInvoke`):

| label | state | engine | display |
|---|---|---|---|
| `EntryLimitOrder` / `EntryTrailingOrder` | `open` | `CurrentOpenOrderId = id`; `placedPrice` seed-if-zero from `price` | `txtPlacedPrice` |
| `TakeLimitProfit` | `untriggered` or `open` | `PositionTPOrderId = id`; `CurrentTPOrderId = id` only when a working entry was also found | `txtPlacedTakeProfitPrice` from `price` |
| `StopLossOrder` | `untriggered` | `PositionSLOrderId = id`; `CurrentSLOrderId = id` only when a working entry was also found; `placedStopLossPrice` seed-if-zero from `price`; `StopLossTriggerOriginal` seed-if-zero from `trigger_price` | `txtPlacedTrigStopPrice` from `trigger_price`, `txtPlacedStopLossPrice` from `price` |
| `StopLossOrder` | `open` (already triggered) | `SLTriggered = True`; `PositionSLOrderId = id`; `placedStopLossPrice` seed-if-zero from `price`; `StopLossTriggerOriginal` seed-if-zero from `trigger_price` | both SL boxes |
| `TrailingStopLoss` | any | out of scope v1 — log "trailing order found; manual re-attach" (restored trailing context is a rarer, hairier state; don't guess) | — |

  Gate the whole handler body on `Not cancelPending` (echo-handler convention). One log line summarizing what was restored, e.g. `Restored order context: entry=…, TP=…, SL=… (untriggered)`.
- Add id 778 to the `HANDOVER-2.md` §4.5 id map line (docs commit alongside).

### 2c. Defensive mid-session heal (one line)

In the **open `StopLossOrder` echo case** (where `SLTriggered = True` is set), `triggerPrice` is already extracted — add: `If StopLossTriggerOriginal = 0D Then StopLossTriggerOriginal = If(triggerPrice, 0D)`. Covers any path where the baseline was lost but the SL triggers later.

## Commit 3 — triggered-SL edits sized from the position

`UpdateStopLossForTriggeredStopLossOrder` sends `amount = orderAmountVal` (the txtAmount mirror) — after a restart, or whenever txtAmount ≠ position size, an SL edit would **resize the stop away from the position**. Change:

```vb
            ' Restore hardening: a triggered SL covers the POSITION - size edits from the position
            ' model, falling back to the input mirror only when the model is unseeded.
            Dim amount As Decimal = If(positionSizeUSD <> 0D, Math.Abs(positionSizeUSD), orderAmountVal)
```

Entry-context edits (`UpdateLimitOrderWithOTOCOAsync`, trailing) stay on `orderAmountVal` — they size a *working order*, not the position.

---

## Test plan (owner, test sub-account)

1. **The failing scenario, re-run:** open a SHORT, restart, connect → TP/trigger/SL boxes populated, Buy/Sell buttons show SELL mode, no "Placed price = 0" warning spam. Let the SL trigger, then let price move further adverse → **`SL repositioned:` lines appear** (trailing works); no spurious emergency close (baseline restored, guard in place). Mirror once with a LONG.
2. **Restart with a working (unfilled) entry** → order context restored; repositioning resumes; scoped cancel still works on it.
3. **Emergency stop regression:** normal session (no restart), M.SL checked, small threshold → emergency close still fires as before (the new guard must not break the normal path — baseline is set at placement).
4. **Flat restart** → snapshot returns empty; nothing seeded; no announce.
5. Full trade-cycle regression.

## Implementation report

`docs/impl-report-restore-hardening.md`, standard format; commit docs alongside code (docs are tracked now).
