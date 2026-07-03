# Spec-back — Restart restore hardening (as-built)

**What this is:** a specification reconstructed **from the committed code**, not from the forward spec. It states the behavior contract the code now enforces, with anchors, so a reviewer can diff it against `spec-restore-hardening.md` (intent) and against the code (`git show`) to confirm three-way agreement. Written 2026-07-03 against commits `0d078eb` / `310de5f` / `c6a893c` (+ docs `fdb1160`) on `master`. All code in `DeribitOrderPlacementApp/frmMainPageV2.vb`.

**Reconstruction method:** each section below was written by reading the merged code region and describing what it *does* for every input path, independent of what the forward spec asked for. The "Fidelity" section at the end records where as-built matches intent and the two known deviations.

---

## Invariants this change relies on (unchanged, restated for the reader)

- **Receive-thread ownership.** `HandleQuoteUpdates`, `HandleOrderPositionUpdates`, `HandleOpenOrdersSnapshot` all run on the single receive loop thread. Engine fields (`placedPrice`, `placedStopLossPrice`, `StopLossTriggerOriginal`, `positionSizeUSD`, `SLTriggered`, `Current*OrderId`, `Position*OrderId`, `TradeMode`) are read/written there without locks; there is no cross-handler race because the loop dispatches them sequentially per message.
- **Seed-only-when-zero.** A price engine field is written from an exchange echo/snapshot **only if it is currently 0** — so a lagging message can never reset an actively-trailing value backward. The display mirror follows the seed, not the raw value.
- **Control writes marshal.** Controls are touched only inside `UiInvoke`/`Me.Invoke`/`AppendColoredText` (self-marshalling).
- **Unknown baseline disables emergency.** `StopLossTriggerOriginal = 0` means "baseline unknown"; the emergency market-stop must treat that as *off*, mirroring the existing "threshold 0/blank disables" rule.

---

## Region 1 — Emergency market-stop gating (`baselineKnown`)

**Contract:** the emergency market-close path fires only when the trigger baseline is known (`StopLossTriggerOriginal > 0`), in addition to the pre-existing enables.

- `HandleQuoteUpdates`, triggered-SL pre-check (`frmMainPageV2.vb:1566`): `Dim baselineKnown As Boolean = StopLossTriggerOriginal > 0D`. Both emergency decisions require it:
  - full-emergency force (`:1578`): `emergencyThresholdValid AndAlso baselineKnown AndAlso priceMovement >= emergencyThreshold`.
  - half-threshold force-path chooser (`:1607`): `emergencyThresholdValid AndAlso baselineKnown AndAlso priceMovement >= emergencyThreshold * 0.5`.
- `UpdateStopLossForTriggeredStopLossOrder`, both emergency branches (`:3236` long, `:3242` short): each condition now carries `StopLossTriggerOriginal > 0D AndAlso`, inserted between the `marketStopThreshold > 0D` term and the `TradeMode` term.

**Why it matters (failure it prevents):** `priceMovement` is computed as `StopLossTriggerOriginal − bestAsk` (long) or `bestBid − StopLossTriggerOriginal` (short). With a 0 baseline and a corrected `TradeMode` (Region 3), a short yields `priceMovement = bestBid`, which clears any threshold instantly → an unwanted market close on the first quote after restart. The guard makes that path inert until a baseline exists.

**Postcondition:** normal (rate-limited) SL trailing below the guard is unchanged; only the two emergency force-paths and the two market-close branches are affected. With `StopLossTriggerOriginal > 0` (normal placement-set baseline), behavior is byte-identical to before.

---

## Region 2 — Trade-context restore in `ProcessPositionData` (id-777 path)

Entered once per connection, inside `If positionSize.HasValue AndAlso positionSize.Value <> 0D AndAlso Not positionRestoreAnnounced` (`positionRestoreAnnounced` reset at connect, `:724`).

**Contract:**
- Inside the existing `UiInvoke` (`:4081`), **first line:** `SetTradeMode(positionSize.Value > 0D)`. Buy/Sell button state, leg-textbox layout, and `TradeMode` are set to match the real position side. This must run on the UI thread (touches controls) — hence its placement inside `UiInvoke`.
- After the `UiInvoke` (`:4090`): `If placedPrice = 0D Then placedPrice = If(averagePrice, 0D)`. Seeds the PnL/display basis. `txtPlacedPrice` was already mirrored from `averagePrice` inside the same `UiInvoke`.

**Preconditions / scope:** this is the **only** place `placedPrice` is seeded outside order placement. Justified because at restore `placedPrice` is provably 0 and no working entry exists, so no reposition path can act on the seeded value; it exists to restore PnL basis and silence the 5-s "Placed price = 0" warning loop.

**Postcondition:** for a restored SHORT, subsequent `HandleQuoteUpdates` runs the short trailing branch and the correct-side emergency branch. Idempotent per connection via `positionRestoreAnnounced`.

---

## Region 3 — id-778 open-orders snapshot protocol

### 3a. Request (send-only)

- `RequestOpenOrdersSnapshot()` (`:1846`): posts `private/get_open_orders_by_instrument`, `params = { instrument_name: "BTC-PERPETUAL" }`, `id: 778`. `type` omitted ⇒ Deribit default `all` (untriggered stop/trigger orders included). Consumes one rate-limit credit iff `rateLimiter IsNot Nothing`. Swallow-all `Catch` → one red log line; never throws into the connect path.
- Call site (`:752`): in `ConnectToWebSocketDirectly`, immediately after `Await GetLivePositionData("BTC-PERPETUAL")`, before the receive loop and UI-success block. The response therefore sits in the socket buffer and is drained at the very start of the receive loop, ordinarily before any `user.changes` echo.

### 3b. Handler (`HandleOpenOrdersSnapshot`, `:3947`) — dispatch wired at `:827`, after `HandleMarginEstimationResponse`

**Entry gates (in order):**
1. `messageId.HasValue AndAlso messageId.Value = 778` — null-safe id gate; else `Return`.
2. `If cancelPending Then Return` — whole body suppressed while a cancel is in flight.
3. `error` field present ⇒ yellow log + `Return`.
4. `result` not a `JArray`, or `Count = 0` ⇒ `Return` (**flat restart: nothing seeded, no announce**).

**Pass 1 — working-entry detection:** `workingEntryFound = True` iff any order has `label ∈ {EntryLimitOrder, EntryTrailingOrder}` and `order_state = "open"`. This decides whether the TP/SL legs are treated as belonging to a *live working OTOCO* (adopt the `Current*` ids) vs a filled position (leave `Current*` alone, set only `Position*`).

**Pass 2 — per-order mapping** (`label` × `order_state`):

| label | state | engine writes | display (via `UiInvoke`) |
|---|---|---|---|
| `EntryLimitOrder` / `EntryTrailingOrder` | `open` | `CurrentOpenOrderId = id`; `placedPrice` seed-if-0 from `price` | `txtPlacedPrice` **only when seeded** |
| `TakeLimitProfit` | `untriggered` or `open` | `PositionTPOrderId = id`; `CurrentTPOrderId = id` **iff `workingEntryFound`** | `txtPlacedTakeProfitPrice` from `price` |
| `StopLossOrder` | `untriggered` | `PositionSLOrderId = id`; `CurrentSLOrderId = id` **iff `workingEntryFound`**; `placedStopLossPrice` seed-if-0 from `price`; `StopLossTriggerOriginal` seed-if-0 from `trigger_price` | `txtPlacedTrigStopPrice` from `trigger_price` (always if present); `txtPlacedStopLossPrice` from `price` **only when seeded** |
| `StopLossOrder` | `open` (already triggered) | `SLTriggered = True`; `PositionSLOrderId = id`; `placedStopLossPrice` seed-if-0 from `price`; `StopLossTriggerOriginal` seed-if-0 from `trigger_price` | same as SL row above |
| `TrailingStopLoss` | any | none | yellow "trailing order found; manual re-attach required (out of scope v1)" |
| any other label | any | none (silent fall-through of `Select Case`) | none |

**Threading detail (as-built, stricter than the echo handler):** engine fields are written directly on the receive thread; only control writes enter `UiInvoke`. The two *moving* values (`placedPrice`, `placedStopLossPrice`) mirror to their display **only in the branch that actually seeded them** (`seeded` local), so a mid-session reconnect during active trailing cannot clobber the live display. Static values (TP price, trigger price) are written to their displays unconditionally when present.

**Summary line:** after Pass 2, iff any of `entryDesc/tpDesc/slDesc` ≠ `"none"`, emit cyan `Restored order context: entry=…, TP=…, SL=…`. (Empty result already returned at gate 4, so a pure-`TrailingStopLoss` snapshot emits only the yellow warning, no summary.)

**Parse safety:** outer `Try/Catch` swallows JSON parse errors (every socket message flows through this handler; non-778 messages fail the id gate cheaply).

### 3c. Mid-session heal (echo path, `HandleOrderPositionUpdates`)

- Open `StopLossOrder` echo case (`:2031`), immediately after `SLTriggered = True`: `If StopLossTriggerOriginal = 0D Then StopLossTriggerOriginal = If(triggerPrice, 0D)` (`triggerPrice` already extracted in the enclosing `order_state = "open"` scope). Recovers the baseline for any path where it was lost but the SL triggers live — independent of whether the id-778 snapshot populated it.

---

## Region 4 — Triggered-SL edits sized from the position

- `UpdateStopLossForTriggeredStopLossOrder` (`:3275`): `Dim amount As Decimal = If(positionSizeUSD <> 0D, Math.Abs(positionSizeUSD), orderAmountVal)` (was `= orderAmountVal`). The subsequent `amount <= 0` guard, order-id/socket validation, and the `private/edit` payload (`id 223350`, `order_id = PositionSLOrderId`, `price = newPrice`, `amount`) are unchanged.

**Contract:** a triggered-SL reposition sizes to the **position** (`|positionSizeUSD|`), falling back to the input mirror `orderAmountVal` only when the position model is unseeded (0). Prevents a restart / `txtAmount ≠ position` from resizing the stop off the position.

**Explicit non-scope:** entry-context edits (`UpdateLimitOrderWithOTOCOAsync`, trailing-stop edit) remain on `orderAmountVal` — they size a working order, not the position. Not touched.

---

## Cross-cutting behavior notes (as-built consequences worth knowing at review)

1. **Restore ordering per connection:** `GetLivePositionData` (id 777) and `RequestOpenOrdersSnapshot` (id 778) are both sent before the receive loop starts (`:747`, `:752`, loop at `:761`). Draining order in the loop is 777-then-778 by send order but not guaranteed by protocol; each handler is independent and idempotent, so order doesn't change the fixed point.
2. **`TradeMode` set by 777, baseline/legs set by 778:** if the 778 response is lost/errored, `TradeMode` and `placedPrice` are still correct (from 777), the emergency stays *disabled* (baseline 0, Region 1), and the 2c echo heal repopulates the baseline the moment the SL is next seen open. No path is left in the "wrong-side + armed-emergency" trap.
3. **Reconnect (not process restart)** re-runs both requests (`positionRestoreAnnounced` reset at `:724`). Seed-only-when-zero + seeded-gated displays make this safe during active trailing; the only visible effect is the announce + "Restored order context" line reappearing.

---

## Fidelity vs the forward spec (`spec-restore-hardening.md`)

**Matches intent, verbatim or equivalent:** Commit-1 guards (both files, both sites); 2a `SetTradeMode` placement + `placedPrice` seed; 2b request id/method/placement, handler dispatch position, id gate, `Not cancelPending` gate, the full label×state table incl. `workingEntryFound` gating of `Current*` and the `TrailingStopLoss` out-of-scope log; 2c heal line; Commit-3 sizing expression and the entry-edit non-scope.

**Deviations (both already in the impl report):**
1. **Threading:** as-built writes engine fields on the receive thread with only displays in `UiInvoke`, rather than wrapping engine writes in `Me.Invoke` like the legacy echo handler. Behaviorally equivalent (single receive thread), invariant-stricter. Display mirrors for moving values are seeded-gated to avoid clobber.
2. **Docs packaging:** the id-778 §4.5 map entry rode in the docs commit alongside the owner's pre-existing "Resilience pass SHIPPED" HANDOVER-2 note (additive, not overwritten).

**As-built additions not spelled out in the forward spec (defensive, no intent conflict):** empty-`result` early return (satisfies "flat restart seeds nothing/no announce"); summary line suppressed when nothing restored; `type`-omitted request documented as relying on Deribit's `all` default.
