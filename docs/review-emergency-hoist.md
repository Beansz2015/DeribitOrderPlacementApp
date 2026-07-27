# Coordinator review — N1 emergency hoist (`b4496b9` + `1a97054`)

**Date:** 2026-07-27 · **Reviewer:** coordinator seat (Fable) · **Verdict: APPROVED — all four
asks RULED below.** Owner runtime acceptance (spec §Acceptance 2/3) is now unblocked.

**Method:** spec-back §7 followed exactly — both diffs read; `git diff -w` confirms the eleven
non-comment lines and that the emergency locals/comparisons are verbatim-moved, not re-derived;
`emergencyFired` census re-grepped independently (1 decl :169 · 2 sets :4148/:4158, each on the
synchronous line ahead of the dispatch · 3 clears :3642/:4539/:4942 · 3 reads :2190/:4143/:4156);
all six tripwires identical (`ResetCommandedSLPrices` 8 · `cancelPending = False` 5 ·
`RecordCommandedSLPrice` 3 · `emergencyBaselineSettled` 10 · `SendReduceMarketOrderAsync` 5 ·
baseline-zero sites unchanged); **gate EXECUTED at HEAD: GATE PASSED, 104/104.**

## The four rulings

- **D1 — RATIFIED: 3 clears.** The acceptance line's "exactly 2" was this coordinator's miscount of
  the spec's own body ("the placement seeds", plural — there are two). The implementer's hole
  argument is exactly why the plural was the intent: a `StopLossForTrailingOrderAsync` bracket
  after an emergency that never reached `CompletePositionClose` must not run cap-less. The
  acceptance grep is hereby amended to **exactly 3**.
- **D2 — CONFIRMED STALE SPEC PREMISE, no guard wanted.** Spec §5's "as today (`CanMakeRequest`)"
  misdescribed HEAD — no limiter ever guarded this fire path, and none should: rate-limiting the
  loss cap is the wrong direction, agreed in full. The derived requirement (latch set only where
  the send is genuinely dispatched) is what §5 was for, and it is met.
- **D3 — RATIFIED: the hoisted-gate latch read.** Load-bearing, correctly derived: without it a
  post-fire tick in the send-await window re-enters, finds the fire branches latched out, and
  falls through to editing the resting SL to the own-side touch — a defect the hoist would have
  introduced. The `1a97054` window-scoping correction is accurate and welcome.
- **§3 residual — ACCEPTED + documented.** Latch-set-but-send-not-dispatched (socket drop across
  the cancel, or an empty position model in a triggered context) forfeits the pre-N1 retry for
  that position. Both escape paths are near-unreachable, a down socket could not have sent the
  retry either, and both alternatives are correctly rejected — set-after-send reopens the §4 race
  and trades a rare missed retry for a rare **duplicate taker close**, the more expensive error;
  clearing inside `SendReduceMarketOrderAsync`'s guards pollutes a seam shared with
  `FlattenPositionAsync` and the manual button. Right call.

## The §4 finding — endorsed, and it upgrades N1's characterisation

Premises verified in code: `HandleQuoteUpdates` is fire-and-forget `Async Sub`; the force path
sets `lastStopLossUpdate = MinValue` so the throttle passes unconditionally; every
`CancelOrderAsync` state reset executes after its own send-await. So for the width of that await,
every pre-N1 gate stayed open to a re-entrant tick — **the double-fire was reachable before the
hoist**. N1 is therefore honestly "hoist + close a latent double-fire race on the taker-close
path", and the set-being-ahead-of-the-first-yield argument makes the latch airtight for exactly
that window. This supersedes the item-16 trade-off comment's framing.

## Conscious ticks (adversarial table rows the reviewer confirms deliberately)

- A manual SL re-anchor after a fire does NOT re-arm the cap within the same position — correct:
  single-fire is per-position by design; the cap fired and did its job. On record.
- The latch is not an SL-context reset site (zeroes no price/set/baseline) — confirmed.
- The tripwire-prose lesson (comments containing literal tripwire tokens pollute the standing
  greps) is worth keeping — noted for the handover.

## Remaining before "done"

Owner runtime acceptance per spec §2/§3 with D1 semantics (re-arm provable at ANY of the three
clear sites): **rebuild the x64 bin first** (the gate builds AnyCPU only), re-check the window
title — the rebuild-to-live trap is closed (`5ef58c4`) but the ritual stands — then the isolated-
harness persistent-edit-failure recipe: emergency fires without waiting for the throttle, exactly
once; fresh position re-arms. Normal-session parity is structural (throttle-passing ticks see
identical inputs) — spot-check only.
