# Coordinator review — ergonomics Phase A (2026-07-18) — ALL 9 APPROVED

**Scope:** `d2bbf64..0749a58` (items A–J, one commit each) at HEAD `3b4810a`, base `a176d5f`. **Method:** the verify gate **executed by this reviewer** at HEAD — `GATE PASSED`, exit 0 (both builds; OrderCheck **48/48** incl. item C's 4 migration fixtures and item H's 8 predicate fixtures); full diffs read for the two invariant-sensitive items (E, C-tracking); HEAD-state reads for H's emission condition and G's guard; commanded-set grep **10 occurrences = pre-Phase-A identical** (zero new `RecordCommandedSLPrice`/`ResetCommandedSLPrices` touches anywhere).

## Verdicts

- **E APPROVED — the sensitive one, clean:** `EditStopLossTo` is a genuine verbatim extraction (payload byte-equal, id 223346, audit-2 fallbacks, same logs; the diff is re-indentation). **No `RecordCommandedSLPrice`** — the binding honored with the rationale in the header. `btnBreakEven` improves on spec: direction from the **position sign** (model truth), not `TradeMode`; `RoundToTick` reused; flat-gated; static placement respecting the `SetTradeMode` shuffle. The TradeMode-vs-position limit-side note is a pre-existing class, correctly flagged not changed.
- **C APPROVED:** resets live INSIDE the existing flat→nonzero branch (the `avg`-hoist is behavior-identical — nothing reads `pendingCloseValid` in the reordered window, and the seed correctly uses the freshly-written average); the hot-path addition is exactly two guarded compares (zero-safe both sides, `maePrice=0` reseed arm for restores); fees summed after the positions pass / before `CompletePositionClose`. Close math + storage verified via the gate-executed migration fixtures (old-schema DB opens, legacy defaults, enriched round-trip, idempotent re-open) + report; journal-only arithmetic, not order-path. The fixture addition beyond spec is exactly right — migration evidence is now reproducible on every gate run.
- **H APPROVED:** emission condition verbatim per the amended item; file append + `_lastDisposition` + `StatusChanged` structurally above it (comment documents the join-integrity reason).
- **G APPROVED:** the three-clause provably-flat guard with the invariant-3 rationale; not an 8th reset site.
- **A/B/D/I/J APPROVED:** mechanical, gate-verified. Accepted deviations, all correctly flagged: A's gate-list edit (the gate's purpose); D's close alerts at `CompletePositionClose` (structurally once-per-close — better than per-fill); I's selection save/restore (the pre-existing append moves the caret; without the restore the item's own acceptance is unachievable; stays inside the one marshalled action, display-only); B's step-floor refusal.
- The post-retirement reconciliation was real work done well (the `txtTriggerOffset`-not-a-mirror finding is exactly what the note existed for).

## Owner decisions queued (from the report, coordinator recommendations added)

1. **Persist `txtComms`?** Recommend **YES** — item E's BE button now derives from `commsVal`, so a stale default silently mis-places break-evens after every restart. One-line addition to item A's key list.
2. **Alert on the untracked external/liquidation close?** Recommend **YES** — it is the highest-surprise close there is (something closed your position outside the app). One `Alert("close_fill")` line in that branch.

## Remaining

Owner runtime acceptance pass (the spec's per-item acceptance lines: SIZE math, alerts fire once, B.E. on a live position, quiet-log-when-busy, sticky scroll + selection survival, averaged entry line, persistence round-trip) → push. The two decisions above can ride the same or the next commit.
