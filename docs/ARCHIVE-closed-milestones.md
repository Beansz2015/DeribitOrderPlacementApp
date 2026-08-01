# ARCHIVE — closed milestones (index only; NOT part of the read path)

**Nothing here is open.** This file exists so `HANDOVER-6.md` and `ROADMAP-2026-08.md` can stay
short: it holds the one-line verdict per closed milestone plus the doc chain that carries the real
detail. **Do not read this file to get up to speed** — read `HANDOVER-6.md`. Come here only when
you need the reasoning behind a specific closed item.

Rule of the house: **per-milestone detail lives in the spec → spec-back → impl-report → review →
runtime-record chain**, never re-narrated in a handover or roadmap row. These entries are pointers.

## Superseded handovers — HISTORY ONLY, not binding

`HANDOVER-3.md` · `HANDOVER-4.md` · `HANDOVER-5.md` are **superseded and carry stale instructions**.
`HANDOVER-6.md` is self-contained; it does not require reading any of them.

⚠ **Two H-4 §5 "binding" runtime facts were later REVERSED — do not act on that section:**
- *"Trade-placing steps: OWNER mouse clicks only"* — superseded 2026-08-01. Harness-driven placement
  IS permitted on a TESTNET-titled, harness-launched session, through `tools/place-and-verify.ps1`.
- *"Consumer-only payload tests: the isolated harness `bridge.json` protocol — **never stop the
  engine**"* — **inverted** by owner ruling 2026-08-01: **STOP the engine → run →
  `restore-payload.ps1` → RESTART the engine.**

H-4 §4's invariants were folded (corrected) into `HANDOVER-6.md` §7. H-3 §12 is still the ntfy
server-side poll recipe — the one section of the old handovers with live reference value.

## Closed milestones — 2026-06 → 2026-08

| # | Milestone | Verdict | Chain |
|---|---|---|---|
| Q1 | Remote ntfy notifier | **DONE** 2026-07-25, owner-accepted incl. A-B-A disabled-parity proven server-side | `spec-quickwins-notifier-signalcols.md` → `impl-report-quickwins.md` → `review-quickwins.md` → `handover-quickwins-runtime-acceptance.md` |
| Q2 | Signal columns in the trade DB | **DONE** 2026-07-25 (`SignalId`/`SignalConfidence`, TEXT per ruling a; View Trades grid shows them) | same chain |
| Q3 | Ops tooling (`backup-orderapp.ps1`, `policy-report.ps1`) | **DONE** 2026-07-25 | same chain |
| — | Session policy gate (Phase 3) | **DONE + ENABLED** — LONDON/ASIA configured; NY deliberately unconfigured ⇒ defaults (see H-6 §4) | `spec-session-policy-gate.md` → `impl-report-…` → `review-…` |
| — | Soak + column-level join review | **DONE + CLEAN** 2026-07-22 — 1404 rows / 15 instances, ZERO column mismatches. **Do not re-raise.** | `review-soak-join-2026-07-22.md` |
| — | Live-at-min-size interlock ladder | **DONE** 2026-07-23, CSV-verified | `runtime-record-live-ladder-2026-07-23.md` |
| N1 | Emergency-hoist | **DONE** 2026-07-28, runtime-accepted (#96–#98). *Framing corrected after:* the "~5 s delay" was never reachable (the backoff was dead code); the real pre-N1 exposure was ≤ 333 ms **plus a latent double-fire race, which N1 closed** — the more valuable half. | `spec-emergency-hoist.md` chain + `spec-back-emergency-hoist-acceptance-2026-07-28.md` + `runtime-record-emergency-hoist-2026-07-27.md` |
| N1b | SL-backoff coupling | **DONE** 2026-07-30 — red ids **223346/223348/223350 only**; the backoff's first reachable trigger ever. Its acceptance-2 defect spawned N1c. | `spec-sl-backoff-coupling.md` → `review-…` + `spec-back-sl-backoff-escalation-defect.md` |
| N1c | SL-backoff confirmed reset | **DONE + CLOSED** 2026-07-31, runtime-accepted on trade #99 (backoff climbed 0.7 → 1.3 → 2.7 s; `SL update rate limited` printed with no pump). The optimistic reset had pinned the backoff at 666 ms. | `spec-sl-backoff-confirmed-reset.md` → `impl-report-…` → `runtime-record-sl-backoff-2026-07-31.md` |
| EV | Chase budget (EV floor) | **CLOSED END-TO-END** 2026-08-01 — manual arm AND bridge-act leg. Disposition cardinality held: exactly one row per payload, the abort adding none. Ships OFF (`min_net_move_pct = 0`). | `spec-ev-chase-budget.md` → `spec-back-…` → `review-ev-chase-budget.md` → `runtime-record-ev-chase-budget-2026-08-01.md` |
| — | Fee-comms repoint | **CLOSED** 2026-07-30 — 2024 `TakerFeeRate` gone; comms derives from `taker_fee_bps` (32 → 22 at 64k). ⚠ Its review was appended to the **spec**, not a standalone `review-….md` — which is why it read as owed for three days. | `spec-fee-comms-repoint.md` (review is inside it, `fd2604e`) + `impl-report-fee-comms-repoint.md` |
| WATCH | Triple-placement investigation | **CLOSED** 2026-08-01 — harness EXONERATED (53/53 single invokes → one order); the defect was app-side. Old "leans harness-side" premise was wrong. | `investigation-triple-placement-2026-08-01.md` |
| SF | Placement single-flight v1 | **SUPERSEDED by SF2, code KEPT.** Failed acceptance 2 with the defect intact — the latch proves its own irrelevance (had any two overlapped, one would have been blocked). **A RATE problem, not an OVERLAP problem.** | `spec-placement-single-flight.md` → `review-placement-single-flight.md` |
| SF2 | Placement debounce | **DONE + CLOSED** 2026-08-01 — 500 ms debounce ahead of the v1 latch. The burst that gave 3 entries + reduce 30 now gives 1 + reduce 10. | `spec-placement-single-flight-v2.md` → `review-placement-debounce.md` |
| N2b | Chase preserve placed size | **DONE + CLOSED** 2026-08-01, all five runtime acceptances PASSED. Pre-existing defect, not an N2 regression: both chase edits re-derived `amount` from the Amount box, so session-policy `size_mult` had been silently reverted since it shipped — invisible only because live-at-min-size clamped every reduction back to 10. | `spec-chase-preserve-placed-size.md` → `spec-back-…` → `review-…` → `runtime-record-chase-preserve-placed-size-2026-08-01.md` |
| N2 | Risk-sized bridge trades | **CODE APPROVED + UNBLOCKED** 2026-08-01, **ships DISABLED** — the only milestone with an owner action left (tick `Risk-size`); see H-6 §4. Acceptances 1/3/4/5 + the §2 screenshot passed; 3b failed on the N2b defect above and was re-run green as N2b's acceptance 2. | `spec-risk-sized-bridge-trades.md` + `spec-back-risk-sized-bridge-defects.md` (five defects, all upheld) → `review-risk-sized-bridge-trades.md` |
| C1 | v2 feedback file — **documentation phase** | **CLOSED both sides** 2026-07-29 — proposal → engine ACCEPT (+3 refinements) → ack → trader tick T1–T8. **Contract §8 is the binding spec**; engine mirror is their §10. The two implementation builds remain (ours = the emitter, still the live queue item). | `proposal-c1-v2-feedback-file.md` → `ack-c1-v2-feedback-file.md` → `integration-contract-verdictengine.md` §8 |

## Retired reasoning worth one line each

- **Divergence tests need the Amount box ABOVE the min-10 clamp.** At box 10 a `0.5` mult clamps
  back to 10 and there is nothing to observe — the blind spot that hid the N2b defect for weeks.
- **A placement log line is NOT evidence of position size.** N2's 3b logged a correct
  `Buy limit order placed For 310` against an actual position of 10. The **reduce** is
  exchange-derived and is the authority.
- **The N2b D1 trap:** a four-conjunct gate was quoted, **one** conjunct's reachability proved, and
  the whole gate generalised. Prove every conjunct or claim nothing.
- **Harness payload timing:** with the engine stopped, freshness is `2.5 × exec_resolution_min` and
  `write-payload.ps1` emits `1` — a 2.5-minute window, shorter than a human round-trip to START.
  Patch `exec_resolution_min` to 15 in the payload before START (~37 min) and the race disappears.
