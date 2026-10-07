# Coordinator review — reset the chase anchor when a placement dies; a refused bridge act says why

**Spec:** `docs/spec-chase-anchor-reset.md`. **Report:** `docs/impl-report-chase-anchor-reset.md`.
**Commit reviewed:** `14db8c0`. **Reviewer:** coordinator seat, Opus 5.5, high. **Date:** 2026-10-08.

## Verdict: ✅ APPROVED. One owner ruling owed (`SB1`, §3). Owner testnet check owed (report §6).

## 1. Verified at the artefact

| Claim | Result | How |
|---|---|---|
| Gate | ✅ `GATE PASSED`, OrderCheck **327/327** | Re-run at `14db8c0` |
| Nine `frmMainPageV2.vb` censuses | ✅ 10 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18 = **68 across 64 lines** | `Select-String -AllMatches -CaseSensitive`, summed |
| Reset only on a timely rejection | ✅ `ResetOrderAttempt` added in the error arm; the `TimedOut` branch untouched (spec A1) | Diff read |
| Reset at every close | ✅ unconditional in `CompletePositionClose`; `CancelOrderAsync` untouched (spec A2) | Diff read |
| Every early return records a reason | ✅ 13 `NotePlacementNotSent` calls plus the `Catch` | Diff read |
| A not-sent act returns at once, clears the staged tag, removes its registry entry | ✅ | Diff read of `PlaceAutomatedOrder` |
| A manual placement is unaffected | ✅ `NotePlacementNotSent` is a no-op without a pre-registered entry | Diff read |

**Accepted from the report, not re-run:** the red step (the seam stubbed to today's behaviour → 3 of 4 new
fixtures fail).

## 2. The implementer's five decisions — all upheld

Decision 1 is worth naming: the reason names the quote the code **checks**, where four existing log
lines name the wrong side. Those log lines are already on the audit's list (row A22 / G18 of
`docs/triage-adversarial-audit-2026-10.md`, misleading log lines).

## 3. `SB1` (chase-anchor-reset spec-back) — confirmed; owner ruling owed

In the four limit arms, the slippage check seeds the anchor, then `Decimal.Parse(txtTriggerOffset.Text)`
runs, and `RegisterPendingPlacement` comes later (`frmMainPageV2.vb:4384`). A throw in between (a blank
Trigger Offset box) sends nothing and leaves the anchor seeded. Confirmed by reading the `BuyLimit` arm.

✅ **Owner ruling 2026-10-08: fold in. Built in this review** (both placement routines, including the trailing
placement, which has the same check-then-parse order). Pure seam `ShouldUndoAnchorSeed` with 4 fixtures; red
step run (the seam stubbed to never undo → fixture 1 fails, 1/331). Gate 331/331; censuses 68 across 64
lines; x64 rebuilt. Residual: if a working order with **no** anchor (a market entry) exists and a second
placement throws, the reset also zeroes that order's requote count. The coordinator judges it negligible; the owner may rule otherwise.

**Recommendation (as written before the ruling): fold in now.** Reset at the routine's exit **only if this call seeded the anchor and did
not reach the send**. A local flag, one reset in a `Finally`. It can never wipe the anchor of an earlier
working order, because that call did not seed it. Decision-bias tripwire:
`docs/harness-runs/decision-bias-20261008T0400Z-*`, baseline first, no `gives_up_for_economy` flag,
stable 5/5.
