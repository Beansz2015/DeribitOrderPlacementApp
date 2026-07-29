# Micro-spec — repoint the comms default off the 2024 taker constant

> **COORDINATOR REVIEW 2026-07-30: APPROVED — CLOSED.** Gate EXECUTED by the reviewer: GATE
> PASSED, OrderCheck **146/146**, all six comms fixtures passing under the reviewer's own run.
> Diff verified at `60b95d6`: rounding carried verbatim (only the rate moved), the string→decimal
> conversion stays in the same position (non-numeric index still throws into the same Catch), the
> mirror follows the maker-sibling pattern, and the owner's acceptance-2 numbers corroborate to
> the dollar (63678.54 × 3.5 bps = 22.29 → 22; the retired rate gives 31.84 → 32). Censuses
> re-run independently: `TakerFeeRate` **0 in code**, `DefaultCommsFromTakerBps` 2,
> `takerFeeBpsVal` 3, `emergencyFired` 10, `slUpdateFailures = 0` exactly 1, `NextSlBackoff` 2.
> **The two reviewer observations, ruled:** (a) comms stays ONE taker leg — that is the box's
> historical semantic; any round-trip/break-even redefinition is the deferred Rec-2
> crossing-delta micro-spec's scope, not drift to absorb here; (b) the persisted `comms` key
> being overwritten by the first index tick is pre-existing and accepted — the persisted value
> covers only the start→first-tick window; if the owner ever wants a tick-surviving manual comms
> override, that is a new ask, not a defect. **Nothing remains on this spec.**

**Status: IMPLEMENTED 2026-07-30 at `60b95d6`** (approved 2026-07-28, owner tick, post-N1b — the
recommended slot). Gate PASSED, OrderCheck 140 → 146; as-built in
`docs/impl-report-fee-comms-repoint.md`. **ALL THREE ACCEPTANCES PASSED** — 1 and 3 in-seat;
**acceptance 2 CLOSED by the owner's runtime pass 2026-07-30** (TESTNET, index 63678.54 → comms
box **22**, i.e. 3.5 bps to the dollar; the retired constant would have written 32). The
derived-TP / break-even shift was accepted as-is. Awaiting coordinator review only.
Origin: EV-chase-budget spec-back §4 finding, ruled in `review-ev-chase-budget.md` §2.
**Contract impact: NONE. Engine-seat relay: CLOSED** (their relay record amended; the engine
carries no analogous constant — fees entered engine code at v62, `scoring.trade_costs`).
**Recommended implementer: Opus HIGH, fresh conversation** — the change is one site but it lives
in `HandleIndexUpdates` on the receive path (threading rules apply; the comment discipline near
tripwire tokens applies — this file's censuses include `TakerFeeRate → 0`). Single commit +
gate + short impl report; standing ground rules (never push, anchors by symbol at HEAD).

## The defect

`frmMainPageV2.vb` `TakerFeeRate = 0.0005D` (2024 schedule, ~:354) sets the **default comms**
on every index tick (`HandleIndexUpdates`: `comms = TakerFeeRate × indexPrice`, rounded), and
comms feeds the derived TP and the item-E break-even trigger. Against the 2026-08-01 taker of
3.5 bps that overstates the fee ~43% (≈ $32 vs ≈ $22 at a 64k index) — moving every derived TP
by ≈ $10. The EV pass deliberately did not touch it (behaviour change, adjacent to the
Rec-2-deferred crossing-delta arithmetic).

## The change (one site)

Replace the constant read with the persisted schedule: `comms = (TakerFeeBps / 10000) × index`
using the hot mirror of `taker_fee_bps` (the key already round-trips; commit 1 of the EV pass
carried it precisely for this). Add the mirror field (`takerFeeBpsVal`, seeded in
`ApplyEvChaseBudgetFromSettings`) — the EV pass seeded only the maker leg. Delete `TakerFeeRate`
or leave it commented-out dead? **Delete** — a second live schedule is exactly the defect.

## Do-not-touch

The EV predicate and its maker-derived round trip (2 × maker is CORRECT for the maker-first
flow — this spec is about the comms/taker path only); the emergency block; the M.SL comparison
(Rec 2 stays guidance); the four gates.

## Acceptance

1. Gate per commit; one fixture: the comms derivation from a hand-edited `taker_fee_bps`.
2. Owner runtime: the comms box lands ≈ 3.5 bps of index after the first index tick (≈ $22 at
   64k, was ≈ $32) and the owner accepts the derived-TP/break-even shift, or re-tunes offsets.
3. Grep: `TakerFeeRate` count 0.
