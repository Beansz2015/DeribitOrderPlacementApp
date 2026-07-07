# Impl report — M.SL emergency baseline hybrid (top-of-book settle latch)

**Spec:** `spec-back-emergency-baseline-hybrid.md` Option 2 (owner confirmed model "(a)" and authorised direct implementation). **Implementer:** Fable. **Commit:** `25cac5f`. **Build:** 0/0.
**Sequence:** on top of the approved emergency-baseline fix (`326cbaf`). Local; feeds the consolidated stack review + combined runtime, then push.

## The problem it fixes

`326cbaf` correctly made `emergencyBaseline` a fixed loss-cap anchor, but froze it on the **trigger-flip echo price** — the SL's trailed/live limit, which can sit *below* top-of-book. Owner #67 (SHORT, M.SL 5): flip 64013.5 → SL settled at 64018 → cap fired at 64018.5 (owner wants 64023 = 64018 + 5). The 4.5 gap is the trail-to-top-of-book step.

## What shipped — the `emergencyBaselineSettled` latch

New `Private emergencyBaselineSettled As Boolean`. Anchor lifecycle:

| Event | `emergencyBaseline` | latch | where |
|---|---|---|---|
| Pre-trigger | 0 → emergency falls back to `StopLossTriggerOriginal` (trigger price) | — | `:1688` reader |
| Trigger flip (adopt) | = flip price (seed) | **False** (re-arm) | `:2298` |
| **First** post-trigger chase reposition | = `newStopPrice` (actual top-of-book) | **True** (freeze) | `:1768` |
| Subsequent chases | unchanged (frozen) | True → skip | `:1768` |
| Manual SL edit (detected) | = manual price (re-anchor) | **True** (freeze) | `:2310` |
| Restore (id-778, already-triggered) | = restored SL price | **True** | `:4543` |
| Trade over (nuclear cancel / market reduce / close) | 0 | **False** | `:3178`/`:3272`/`:4259` |

Result: post-trigger the cap = **actual top-of-book SL ∓ M.SL**, a fixed hard cap that fires even while the maker chase keeps going (owner rule "(a)"). Between the flip and the first reposition the anchor is the flip price — the best available until the SL settles.

`emergencyBaseline = newStopPrice` now appears **only** inside the `If Not emergencyBaselineSettled` latch (grep-verified) — the old unconditional chase-advance (removed in `326cbaf`) does not return.

## Reset handling (the one judgment call)

The latch is cleared at the **3 trade-over** `emergencyBaseline = 0` sites (nuclear `CancelOrderAsync`, market reduce, `CompletePositionClose`) so nothing stale carries into the next trade. The **4 pre-trigger SL-placement** reset sites deliberately do **not** clear it: `SLTriggered = False` there (the chase isn't running), and the trigger adopt re-arms the latch (`settled = False`) at the next trigger. A null-price flip (adopt can't seed) self-heals — the emergency uses the `StopLossTriggerOriginal` fallback until the next priced echo re-fires the adopt. So the latch is fully managed by adopt-re-arm + first-chase-capture; the trade-over resets are belt-and-suspenders.

## Interactions preserved

- **Manual re-set defines the cap:** a detected manual SL edit re-anchors `emergencyBaseline` and freezes (`settled = True`) so the subsequent P1 chase pullback can't override the owner's manual cap. Keeps the trade-#44 / rejected-clamp behavior (manual moves arm the emergency).
- **Discriminator untouched:** it keys off `placedStopLossPrice` + the commanded set, never `emergencyBaseline`/`settled`. The chase still advances `placedStopLossPrice` every tick. Invisible to the discriminator.
- **Both emergency readers** (`:1688` quote handler + the copy inside the send function) read `emgBaseline` identically — only the write timing changed.

## Arithmetic (reconciles all three owner data points)

| | anchor (top-of-book SL) | cap | owner wanted |
|---|---|---|---|
| #55 (M.SL 20) | ~63403 (first settle) | ~63423 | 63418 (~, ±5 flip-vs-settle) |
| #67 (M.SL 5) | 64018 | **64023** | 64023 ✓ |
| example (M.SL 70) | 63920 | 63850 | 63850 ✓ |

The ~5 wobble on #55 is the trigger-flip-vs-first-settle rounding on a 20-pt threshold (spec-back §4; Option 3 would pin it exactly at the cost of a quote comparison in the hot block — not taken).

## Docs updated (this commit + the code comments)

In-code: the `emergencyBaseline` field comment (`:44`), the adopt-block comment, and the chase-execute invariant comment now state "latch the top-of-book settle, then freeze." `spec-emergency-baseline-fix.md` §2 invariant refined with the latch + the "why (owner #67)". `HANDOVER-2.md` 07-08 delta invariant sentence updated. The session spec-backs (`-07-04` §6/§8, `-07-07` §1) still read "frozen loss-cap anchor" — correct as-is; the "captured at first settle" nuance lives in the two emergency docs.

## Owner runtime tests (spec-back §6)

1. **#67 re-run:** SHORT, small M.SL, SL triggers below top-of-book and settles up one step → cap fires at (settled SL) + M.SL, not (flip) + M.SL.
2. **Grind (#55 shape):** SHORT, M.SL 20, SL chases far → cap fires at ~(settled SL) + 20 even as the SL chases past it (hard cap), not riding the maker to the bottom.
3. **SL live at top-of-book:** trigger with no trail gap → the first reposition may nudge the anchor a tick; confirm acceptable.
4. **Manual re-set post-trigger:** move the SL wide → cap re-anchors to (manual) ∓ M.SL and holds through the chase pullback.
5. **Pre-trigger:** cap on the trigger price; M.SL disarmed = pure chase.

Related: `spec-emergency-baseline-fix.md` (the fix this refines), `spec-back-emergency-baseline-hybrid.md` (the proposal), `spec-back-emergency-baseline.md` (original diagnosis).
