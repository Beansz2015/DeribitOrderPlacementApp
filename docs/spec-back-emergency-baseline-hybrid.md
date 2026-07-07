# Spec-back — M.SL emergency baseline: anchor on the ACTUAL top-of-book SL, not the trigger-flip limit

**For:** the orchestrator (to turn into a forward fix spec). **What this is:** a refinement of the just-approved emergency-baseline fix (`spec-emergency-baseline-fix.md`, impl `326cbaf`), driven by the owner's runtime trade #67 and a detailed model the owner stated and confirmed. The approved fix correctly made `emergencyBaseline` a fixed loss-cap anchor (not chase-following) — but it anchors on the **wrong post-trigger price**. **No code changed for this** (M.SL-emergency invariant change; routing to the orchestrator like the last one). Anchors at `326cbaf`; locate by symbol.

**Origin:** owner trade #67 (SHORT, M.SL 5). The cap fired (fix works) but at 64019.5; owner wants **64023**. The 4.5 gap is exactly the SL's trail-to-top-of-book step.

> **IMPLEMENTED 2026-07-08 — Option 2 (owner authorised direct implementation), impl `25cac5f`, build 0/0.** As-built: `docs/impl-report-emergency-baseline-hybrid.md`. New `emergencyBaselineSettled` latch; adopt seeds the flip price + re-arms (False); the first post-trigger chase reposition captures the top-of-book SL + freezes (True); subsequent chases skip; manual edit re-anchors + freezes; restore freezes; latch cleared at the 3 trade-over resets (the 4 pre-trigger placements rely on the adopt re-arm). `spec-emergency-baseline-fix.md` §2 invariant refined; owner runtime (§6) + consolidated stack review pending. The orchestrator's review of this as-built is still welcome.

---

## 1. The owner's model (stated + confirmed — authoritative)

Owner's worked LONG example (entry 64000, SL trigger 63950, intended SL limit 63900, M.SL 70), confirmed as "(a) hard cap":

1. **Pre-trigger:** the M.SL cap is based on the **SL trigger price** → 63950 − 70 = **63880**.
2. **At trigger:** the SL is placed at **63920 because that's the top of the orderbook** — NOT the intended limit 63900. The cap **rebases** to that actual SL → 63920 − 70 = **63850**.
3. **Post-trigger:** the anchor is **held** (hard cap, "(a)") — it does NOT follow further chasing. If the market keeps running past the cap while the maker SL is still chasing, the emergency fires. Pre-trigger it is always trigger-based; it switches to actual-SL-based only once triggered.

Manual edits still move the anchor (unchanged from the approved fix / the rejected-clamp decision).

## 2. Why this reconciles #55 and #67 (it looked contradictory; it isn't)

The anchor is the **actual SL where it first rests at top-of-book post-trigger**, then frozen. Cap = that anchor ∓ M.SL.

| | Anchor (top-of-book SL) | Cap | Owner wanted |
|---|---|---|---|
| **#55** (M.SL 20, triggered ~63398, chased to 63457) | ~63398–63403 | ~63418–63423 | **63418** ✓ (~) |
| **#67** (M.SL 5, flip 64013.5 → settled 64018) | **64018** | **64023** | **64023** ✓ |
| **example** (M.SL 70, settled 63920) | 63920 | 63850 | 63850 ✓ |

All three line up. The ~5 wobble on #55 is the trigger-flip-vs-first-settle question in §4/§5.

## 3. What the approved fix got right, and the one gap

**Right (keep):** `emergencyBaseline` is a fixed loss-cap anchor, moved only by the adopt + manual edit + restore seed, never by the chase (`:1753` deletion). Pre-trigger it falls back to `StopLossTriggerOriginal` (the trigger price) via `emgBaseline = If(emergencyBaseline > 0D, emergencyBaseline, StopLossTriggerOriginal)` (`:1688`). That is exactly the owner's rules 1 and 3.

**Gap:** the adopt (`:2279`) captures `emergencyBaseline = price.Value` from the **trigger-flip** `StopLossOrder` echo — which is the SL's **live/trailed limit** (64013.5 in #67), i.e. where the stop order posts the instant it triggers. But the OTOCO SL goes live at that limit and only **then** does the app's chase reposition it to top-of-book (64018). The owner's rule 2 wants the anchor on the **top-of-book placement (64018)**, not the trigger-flip limit (64013.5). So the fix freezes 4.5 too low → cap 64018.5 instead of 64023.

## 4. The capture-point question (the one real decision)

"The actual SL at top-of-book" = the trigger-flip price **only if** the SL went live already at top-of-book; when it trailed **below** top-of-book (the #67 case), it's the **first post-trigger reposition** that brings it there. No clean signal separates "the settling reposition" from "a genuine first chase step," which is the #55 wobble. Options, cheapest → most faithful:

- **Option 1 — capture at adopt (trigger-flip).** = current behavior. #55 ✓ (63418), #67 ✗ (64018.5). Rejected by #67.
- **Option 2 — capture at the FIRST post-trigger chase reposition, then freeze.** Latch: adopt sets anchor = flip price + `settled=False`; the first chase reposition sets anchor = `newStopPrice` + `settled=True`; later chases don't touch it. #67 ✓ (64018→64023); example ✓ (63920); #55 ≈ (63403→63423, ~5 high vs 63418). Simple, one bool.
- **Option 3 — capture the first tick the SL sits AT top-of-book (best-non-crossing), then freeze.** Compare the SL price to the chase target (`bestAsk − 0.5` short / `bestBid + 0.5` long) at/after the flip; anchor when they match, freeze. Most faithful to "actual SL at top-of-book" (handles #55 SL-live-at-top-of-book → captures 63398 immediately; #67 trailed-below → captures 64018 at the reposition). More logic in the hot block.

**Recommendation: Option 2** — it nails #67 and the example, the #55 wobble is ~5 on a 20-point threshold (inside the owner's own approximation), and it's a single latch bool with no new quote comparison in the emergency path. Option 3 if the orchestrator wants #55 exact.

## 5. Mechanism + interactions (for the forward spec)

- **New field** `emergencyBaselineSettled As Boolean` (or fold into the existing state), reset at the SL-context reset sites alongside `emergencyBaseline = 0` (so a new trade re-arms). Adopt sets it False; the capture event (per the chosen Option) sets it True.
- **Manual edit** (`:2288`): sets `emergencyBaseline = price.Value` AND `settled=True` (the owner's manual re-set defines the new cap; a subsequent chase must not override it). This keeps the trade-#44 / rejected-clamp behavior (manual moves arm the emergency).
- **No-reposition fallback:** if the SL fills or the emergency fires before any reposition, `settled` stays False and the anchor is the flip price — the best available. Acceptable.
- **Hard-cap semantic (unchanged, restate):** the cap fires at anchor ∓ M.SL even while the maker chase is keeping up — more taker closes on fast runs, bounded loss. This is the owner's confirmed intent ("(a)").
- **Readers unaffected:** both emergency computations (`:1688` quote handler, and the copy inside the send function) read `emgBaseline` the same way; only the write timing changes. The discriminator does not read `emergencyBaseline` — invisible to it.
- **Doc churn:** the `:1685-1687` comment ("pinned at the trigger moment") and the adopt-block comment need the "top-of-book settle, then frozen" wording; the `spec-emergency-baseline-fix.md` invariant gains "anchor = actual top-of-book SL, captured at first settle."

## 6. Owner runtime tests

1. **#67 re-run:** SHORT, small M.SL; SL triggers below top-of-book, settles up one step; cap fires at (settled SL) + M.SL, not (flip price) + M.SL.
2. **Grind (the #55 shape):** SHORT, M.SL 20, SL chases far; the cap fires at ~(settled SL) + 20 even as the SL chases past it (hard cap), rather than riding the maker to the bottom.
3. **SL live at top-of-book:** trigger with no trail gap; anchor = flip price (Option 2: first reposition may nudge it a tick — confirm acceptable, or Option 3 pins it).
4. **Manual re-set post-trigger:** move the SL wide → cap re-anchors to (manual) ∓ M.SL, holds through the subsequent chase pullback.
5. **Pre-trigger unchanged:** cap based on the trigger price; M.SL disarmed = pure chase.

Related: `spec-emergency-baseline-fix.md` (the fix this refines), `spec-back-emergency-baseline.md` (the original diagnosis), `spec-back-session-2026-07-07.md` §2/§4 (the discriminator + the rejected clamp the manual path preserves).
