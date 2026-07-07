# Spec-back — the M.SL emergency baseline follows the chase, so the loss-cap never fires

**For:** the orchestrator (to turn into a forward fix spec, or to reject with reasons). **What this is:** a bug + design clarification reconstructed from the committed code (`3abee26`) and owner runtime trades #53/#55/#56 (2026-07-08). The owner has stated the intended behavior explicitly (§3) — this doc records it, the mechanism, the proposed one-line fix, and how it reconciles with two prior owner decisions (the rejected "clamp" and review finding F1). **No code changed for this** (owner asked for the spec-back). Also folds in three minor observations from the same test session (§6).

> ⚠️ Anchors at `3abee26`; locate by symbol.

**Origin:** owner set **M.SL = 20**, entered short, expected an emergency **market** reduce at trigger + 20, got none — the SL chased as a maker limit to the exit. Owner: *"There should have been an emergency market reduce at 63418… the app should offset the M.SL amount to the actual placed SL when it happened, but it didn't."* Correct diagnosis.

---

## 1. The bug — the emergency reference tracks the market

The triggered-SL block computes `emgBaseline = If(emergencyBaseline > 0D, emergencyBaseline, StopLossTriggerOriginal)` (`~:1673`) and, for a short, fires when `priceMovement = bestBid − emgBaseline ≥ marketStopThreshold` (`~:1690`/`~:1696`). But the chase execute block advances the baseline on **every** reposition:

```vb
placedStopLossPrice = newStopPrice
emergencyBaseline   = newStopPrice   ' <-- :1753  the bug
```

The chase keeps the SL at `bestAsk − 0.5`, so after each tick `emergencyBaseline ≈ bestAsk − 0.5 ≈ bestBid + 0.5`, and next tick `priceMovement = bestBid − emergencyBaseline ≈ −0.5`. It can only reach `marketStopThreshold` if the market **gaps** ≥ threshold within one 333 ms tick (before the chase re-advances the baseline). So the M.SL emergency is a **gap detector**, not a **loss cap**.

**Arithmetic, trade #55 (M.SL 20):**
```
Triggered SL placed @ 63398   → adopt sets emergencyBaseline = 63398
SL 63398→63403  (check first: bestBid≈63403, 63403−63398 = 5  < 20)  then baseline := 63403
SL 63403→63417.5 (bestBid≈63417, 63417−63403 = 14 < 20)            then baseline := 63417.5
SL 63417.5→63427.5 (bestBid≈63427, 63427−63417.5 = 9.5 < 20)       then baseline := 63427.5
... never ≥ 20 ... executed 63457 as a limit (no emergency)
```
With a **fixed** baseline of 63398, `priceMovement` crosses 20 at bestBid 63418 → the emergency fires there, exactly as the owner expected.

**Pre-existing, not from SL-chase v2.** The `= newStopPrice` write came from the reconcile fix (`968b26d`, spec `4a`). Before that the baseline was echo-driven, which *also* tracked the SL. So the loss-cap has **never** been the live behavior; SL-chase v2's perfect 1-tick tracking just made the gap window airtight and exposed it.

## 2. This is review finding F1 — understated

`spec-back-session-2026-07-07.md` §4 **F1** already flagged `emergencyBaseline = newStopPrice` and accepted it, but characterized the effect as *"the emergency measures from one chase-step closer to the market → fires one step later."* That undersells it: because the baseline re-advances **every** tick to sit at the book, it doesn't fire "one step later" — on a smooth move it **never** fires. F1 was reasoning about a single failed edit; the structural effect across a whole chase is a disabled loss-cap. The owner's test is the empirical proof F1's "accepted as-is" was premature.

## 3. The owner's intended model (authoritative — owner stated it)

> *"I expect the emergency market offset to be baselined against the trigger (even after I manually moved it — this was fixed earlier) and when it is triggered, the emergency market offset should then be based off the actual stop loss that was placed by the trigger (this was also implemented earlier)."*

Decoded into three rules — the first two are already implemented and correct; only the third is violated:

1. **Pre-trigger:** reference = the SL **trigger** price (`StopLossTriggerOriginal`), and it follows manual moves of the untriggered SL (synced from the untriggered echo's `trigger_price`, `~:2359`). ✓ works.
2. **At trigger:** reference switches to the **actual triggered SL price** (the adopt: `emergencyBaseline = price.Value`, `~:2242`). ✓ works.
3. **Post-trigger:** reference **stays** at that triggered SL price (the loss-cap anchor) and follows only **manual** SL edits (`~:2252`), **not** the app's own chase. ✗ — the chase advances it (`:1753`). **This is the whole bug.**

So the emergency = "market ran `M.SL` beyond the triggered SL (or the owner's last manual re-set)" ⇒ cap the loss with a taker close. The maker chase positions the exit; it must not move the loss-cap.

## 4. Reconciles with the two prior decisions — no conflict

- **The rejected "clamp" (trade #44, `spec-back-session-2026-07-07.md` §2/§5):** that was a clamp to **suppress** the emergency when the user manually moves the SL to the wrong side (arming the gap). The owner rejected it — manual moves **should** still move the baseline and can arm the emergency. This request **keeps** rule 3's manual-follow intact, so trade-#44 behavior is preserved. The rejected clamp was about manual edits; this fix is about the **app chase**. Different write, different concern.
- **F1's acceptance:** superseded by the owner's runtime finding. The forward spec should note F1 is being re-opened with evidence.

## 5. Proposed fix + interactions (for the forward spec to weigh)

**The change is one line:** delete `emergencyBaseline = newStopPrice` (`:1753`); keep `placedStopLossPrice = newStopPrice`. Then the baseline is set once at the adopt, held through app chases, and moved only by the manual-edit path (`~:2252`) — exactly rules 1–3.

Interactions to verify in the spec:
- **Discriminator safety:** the manual-edit discriminator keys off `placedStopLossPrice` + the commanded set, **not** `emergencyBaseline` (`~:2246`). `placedStopLossPrice` still advances with the chase, so detection is unaffected. Removing the baseline advance is invisible to the discriminator.
- **Reconcile-fix rationale subsumed:** `4a` advanced the baseline "so the emergency follows the app's own chase without relying on the discriminated echo." That rationale is exactly the behavior the owner is rejecting; there is no other reader of `emergencyBaseline` that needs the chase value.
- **F1 resolved as a bonus:** with no chase-advance, the failed-edit phantom-advance of the baseline (F1) can't happen either. (F1's `placedStopLossPrice` half remains — the accepted runaway-fix trade-off, separate.)
- **Fallback intact:** if the adopt is ever missed (baseline 0), `emgBaseline` falls back to `StopLossTriggerOriginal` (the trigger) — still a valid loss-cap anchor.
- **Fire-vs-fill race:** once fixed, the emergency fires at `trigger + M.SL` even while the maker chase is keeping up — i.e. it will **take** (market close) at the cap rather than let the maker exit continue. That is the owner's explicit intent (guaranteed exit / bounded loss), but the spec should state it plainly so it's a conscious behavior, not a surprise: enabling the loss-cap means more taker closes in fast moves than today (which never caps).
- **`marketStopLossChecked` + threshold 0/blank** still gate the whole thing (unchanged): the emergency only exists when M.SL is armed with a positive value.

## 6. Minor observations from the same session (orchestrator to triage)

- **(a) Intermittent `Manual SL edit` cyan.** Post-trigger manual moves logged cyan in #58 but not #52. Under the 1-tick/333 ms chase the commanded set is dense near the book, so a manual move that lands within `CommandedSLMatchTol` of a just-commanded price is classified as ours and ignored (no cyan, not followed). **Interaction with §5:** rule 3's manual-follow relies on detection — but the manual moves that matter for the loss-cap are the *wider* re-sets (well off the book), which don't match a commanded price and **are** detected; near-book manual moves are transient (the chase overwrites them) so missing them is harmless. Likely acceptable, but the orchestrator should confirm whether the detection reliability is good enough or wants tightening.
- **(b) Manual-TP input resets to 0 on placement.** Entering a Manual TP and placing (trade in test #10: $63800 → Limit Buy) **cleared `txtManualTP` to 0** and used the offset TP instead. Separate, likely pre-existing bug in the manual-TP input handling; it also blocks the fill-reanchor's manual-TP path from ever being exercised. Needs its own look.
- **(c) Cancel-while-in-position warning spam.** Nuclear cancel on a triggered-SL-in-position (trade #58) cancelled the orders (correct) but produced the transition-race `Placed price = 0 while an order context is active…` warning ×5 over the window the position sat open (no SL) before the owner's manual reduce closed it. Cosmetic; the cancel is cleanly gated (no wasted edit, no `already_closed`). Question for the orchestrator: suppress that warning longer post-cancel, or leave it.

## 7. Runtime test plan for the eventual fix

1. **Loss-cap fires:** short, M.SL 20, let the SL trigger at T; market rises past T+20 while the maker chase keeps up → `Emergency … Market Order Executed` fires at ~T+20 (not a limit exit at the run's end).
2. **Maker exit still wins when it should:** market rises only to ~T+10 then the maker SL fills → no emergency (position closed maker).
3. **Manual re-set moves the cap (rule 3 / trade #44 preserved):** post-trigger, manually widen the SL by X → `Manual SL edit` → the cap is now at (new SL)+M.SL, and a subsequent run fires there.
4. **App chase does NOT move the cap:** a normal chase with no manual edit → the emergency reference stays at the triggered SL (verify the fire point is measured from T, not from the chased SL).
5. **M.SL disarmed / 0:** no emergency ever; pure maker chase (regression).

Related: `spec-back-session-2026-07-07.md` §4 (F1), §2/§5 (the rejected clamp, trade #44), `spec-reconcile-manual-sl-edits.md` (`4a`, the origin of the baseline advance), `impl-report-sl-chase-v2.md` (the chase that exposed it).
