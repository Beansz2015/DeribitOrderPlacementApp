# Spec — Reconcile the app's triggered-SL state with manual exchange-side edits

**Status:** DRAFT for the spec writer. **Date:** 2026-07-04. **Priority:** high (owner manages the SL/TP manually on the exchange; the app's state and chase logic silently diverge). **Base:** `f42a6a7` on `master`. **File:** `DeribitOrderPlacementApp/frmMainPageV2.vb`. Model/effort: Opus/Fable **high** (order/SL path).

**One-line:** after the SL triggers, a manual SL move on the Deribit chart is **not** reflected in `placedStopLossPrice` or the "Stop Loss" display, and the app's chase-to-fill logic keeps running off the stale value — it can override the manual placement. Make the app track the true live SL from the exchange while still rejecting stale echoes of its own repositions, and decide whether the app should defer to a manual edit.

---

## 1. Background — what the triggered-SL "chase" is

Once the SL **triggers** (`SLTriggered = True`), its stop-limit becomes a resting **limit** order. If price runs past that limit, a resting limit won't fill and the close is stranded. So the app **chases the triggered SL limit toward the market** so it fills. In `HandleQuoteUpdates`, rate-limited to `MinStopLossUpdateInterval` (333 ms, `:1361`):

- **Long** (`:1604`): `If bestAsk < placedStopLossPrice − MinPriceMovementThreshold` → reposition the SL to `bestAsk` (chase **down**).
- **Short** (`:1610`): `If bestBid > placedStopLossPrice + threshold` → reposition to `bestBid` (chase **up**).
- On reposition (`:1626-1632`): edit the live SL order, then **synchronously** `placedStopLossPrice = newStopPrice` (the "runaway fix" — so the next quote doesn't re-chase the same move before the echo arrives), mirror the display, log `SL repositioned`.

Every chase decision reads **`placedStopLossPrice`** (the `currentStopPrice` local at `:1553`).

## 2. The defect

`placedStopLossPrice` (and `txtPlacedStopLossPrice`) are updated by the app's own repositions (`:1627`) and otherwise **seed-if-zero** from the open echo:

```
' :2083 — open StopLossOrder echo
If placedStopLossPrice = 0D AndAlso Not cancelPending Then
    placedStopLossPrice = If(price, 0D)
    txtPlacedStopLossPrice.Text = If(price?.ToString("F2"), "0")
End If
```

Post-trigger `placedStopLossPrice` is non-zero, so a **manual SL move on the exchange** — which arrives as an open `StopLossOrder` echo with the new `price` — is **dropped**. Consequences:

1. **Stale engine state:** `placedStopLossPrice` keeps the app's last value, not the user's.
2. **Stale display:** the "Stop Loss" box doesn't show the manual move.
3. **Wrong chase reference:** the chase compares the market against the stale price, so its threshold is off and, when it fires, it edits the live SL order (by `PositionSLOrderId`) to `bestAsk/bestBid` — **overriding the user's manual placement**.

**Why the seed-if-zero exists (must be preserved):** it stops a **stale / out-of-order echo of the app's own rapid reposition** from resetting `placedStopLossPrice` backward mid-chase (the "runaway"/transition-race fix). The app treats itself as the single writer post-trigger. Naively removing the guard reintroduces that race.

**Already handled (context):** `emergencyBaseline` (the M.SL emergency baseline, commit `f42a6a7`) *does* now follow the live SL by updating on every open echo (`:2062`) — but it's currently **ungated**, so it inherits the same stale/out-of-order exposure. This spec should bring it under the same discriminator as `placedStopLossPrice`.

**Parallel gap — Take Profit:** post-fill the TP is a live `open` limit; its open echo (`:2046-2052`) sets only `PositionTPOrderId`, **no price update**, so a manual TP move post-fill isn't reflected either. Pre-fill (untriggered) TP moves *are* reflected. Same class of defect; see §7.

## 3. Requirements

1. Post-trigger, a **genuine manual SL edit** updates `placedStopLossPrice`, `txtPlacedStopLossPrice`, and `emergencyBaseline` to the true exchange value.
2. A **stale / out-of-order echo of the app's own reposition** must NOT move `placedStopLossPrice` backward (preserve the current protection).
3. The app's chase decisions must then run off the corrected reference.
4. Decide the **policy** in §6 (chase-over-manual vs defer).
5. No change to pre-trigger behavior (untriggered SL echo already mirrors + syncs the trigger, item 2).

## 4. The crux — distinguishing a manual edit from a stale app-echo

Both arrive as open `StopLossOrder` echoes with a `price`. The discriminator is the whole problem. Two viable approaches:

### 4a. Recommended — "commanded-price" set

The app knows every SL price it commanded. Track a short history of recently-commanded SL prices (a small ring buffer or a ~1–2 s time-window; note repositions are ≥333 ms apart and echoes normally return in well under that, so the window is small).

- On each app reposition (`:1626`), record `newStopPrice` in the set.
- Open echo: if `price` **is in** the set ⇒ it's the app's own reposition (or a lagging echo of one) → **ignore** (current behavior, no backward reset). If `price` is **not in** the set ⇒ **manual edit** → accept (update the three fields).

Pros: keeps the app's synchronous advance authoritative (no walk-through/blip); only *adds* manual detection. Cons: a manual move that coincidentally equals a recently-commanded price is missed (falls back to today's behavior — acceptable); needs sizing/eviction.

### 4b. Alternative — `last_update_timestamp` monotonicity

If Deribit's raw-channel order object carries `last_update_timestamp` (**implementer to verify**), track the max accepted; accept only strictly-newer echoes, reject older (out-of-order). Pros: no price-history state. Cons: interacts awkwardly with the quote handler's synchronous advance (the watermark is echo-driven but `placedStopLossPrice` is advanced ahead of echoes) → a lagging in-order app-echo can momentarily walk `placedStopLossPrice` backward before converging. Workable but messier than 4a.

**Recommendation:** 4a. Tie `emergencyBaseline` to `placedStopLossPrice` (set both together wherever `placedStopLossPrice` changes — the app's reposition at `:1627` and the accepted-manual-echo path), so the emergency always equals the live SL with no separate race.

## 5. What an accepted (manual) echo does

Replace the seed-if-zero block (`:2083`) with: *if the discriminator says manual* (and `Not cancelPending`), set `placedStopLossPrice = price`, `txtPlacedStopLossPrice.Text = price`, `emergencyBaseline = price`. Keep the `Not cancelPending` gate. Keep seeding when `placedStopLossPrice = 0D` (the trigger-moment seed) unchanged.

## 6. POLICY DECISION (owner call — the important one)

Once the app's reference is corrected, **should the chase keep running?** The user has stated a preference for **limit (maker) fills over the market (taker) M.SL emergency**, and actively manages the SL by hand.

- **P1 — Keep chasing from the corrected reference (accuracy only).** The app respects the manual value as the new starting point but still chases toward the market to guarantee a maker fill. *Downside:* if the user moved the SL away from price to "give the trade room," the chase pulls it back — overriding that intent.
- **P2 — Defer: suspend the chase after a manual edit.** The user has taken manual control; the app stops auto-chasing, relying on the M.SL emergency (which now follows the live SL) as the hard backstop. *Downside:* a stranded manual SL is closed by the **market** M.SL (taker fees) — against the user's stated fee preference.
- **P3 — Hybrid.** Defer while the manual SL is near/would-fill; re-engage the chase only if price gaps far past it (about to strand), before the M.SL fires. Best matches "manual control + maker-fill + market only as last resort," but the most logic.

**Suggested default:** P1 for the first cut (it is the minimal behavioral change on top of the accuracy fix and cannot strand the SL), with P3 as the target if the owner wants true manual control. **Spec writer: please pick.**

## 7. Take-Profit parallel (decide scope)

The same open-echo gap affects a **post-fill** manual TP move (`:2046` updates no price). If in scope, mirror the fix for `TakeLimitProfit` (update `txtPlacedTakeProfitPrice` from the accepted echo). The TP has no chase-to-fill and no emergency baseline, so it's display-only — simpler, and no policy decision. Recommend including it for consistency, or explicitly deferring.

## 8. Edge cases the implementer must cover

- **Cancel in flight** (`cancelPending`): keep ignoring echoes (scoped/nuclear cancel semantics, invariant #3).
- **Reconnect / id-778 restore into a triggered SL:** the snapshot seeds `placedStopLossPrice`/`emergencyBaseline`; the discriminator state (commanded-set / ts watermark) must initialize so the first post-restore echo isn't misread as manual (or is — harmless if it equals the restored value).
- **Reset sites:** clear the discriminator state (commanded-set / watermark) wherever the SL context resets — placement, close (`CompletePositionClose`), nuclear cancel, market reduce (the same sites that already reset `StopLossTriggerOriginal`/`emergencyBaseline`).
- **Rapid app repositions vs a simultaneous manual move:** acceptable if the last write wins and converges; document the chosen behavior.
- **Threading:** the open echo runs inside `Me.Invoke` (UI thread); the chase reads `placedStopLossPrice` on the receive thread — the existing accepted Decimal torn-read class, unchanged.

## 9. Test plan (owner, test sub-account)

1. **Manual SL move is reflected:** long, SL triggers, drag the SL on the chart → the "Stop Loss" box and the engine value update; the M.SL emergency arms at `newSL − M.SL`.
2. **App doesn't fight its own echoes:** let the app chase normally (no manual touch) → no spurious "manual" detections, `SL repositioned` behaves as today, no backward blips.
3. **Chosen policy:** verify P1/P2/P3 behavior after a manual move (does the app re-chase, defer, or hybrid?).
4. **Stale/out-of-order:** (hard to force) confirm under a brief reconnect that a late echo doesn't reset the SL backward.
5. **TP (if in scope):** post-fill manual TP move updates the TP box.
6. **Regression:** full trigger→chase→fill cycle; restart-into-triggered restore; flat/new-trade start (state clean).

## 10. Open questions for the spec writer

1. **Policy §6:** P1, P2, or P3?
2. **Discriminator §4:** commanded-set (4a) or timestamp (4b)? If timestamp, confirm `last_update_timestamp` is on the raw-channel order object.
3. **TP §7:** in scope or separate?
4. Should a manual edit be **logged** (e.g. `Manual SL edit detected: $X`) so it's visible in the trade log?
