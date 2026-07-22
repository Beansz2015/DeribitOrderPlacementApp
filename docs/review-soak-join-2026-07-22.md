# Soak join review — full column-level pass (2026-07-22)

**Reviewer:** coordinator seat (Fable) · **Scope:** the contract-§7 log-only soak's exit evidence —
`bridge-dispositions.log` (x64 bin, snapshotted at 1404 rows) vs the engine's `analysis_log.csv`
(snapshotted at 6196 identity-bearing rows), joined on **(InstanceId, SignalId)**, dispositions
checked gate-for-gate against the engine's own columns. Method + the join script:
scratchpad `join-review.ps1` (parse both, per-instance coverage, semantic + numeric column checks).

## VERDICT: **CLEAN — the join passes in full.**

## The numbers

- **1404 disposition rows, 15 engine instances** (soak window 2026-07-16 → 07-22, ~6.5 days, plus a
  handful of pre-soak stale rows). **1398 rows matched an engine CSV row; ZERO column-level
  mismatches** across all of: verdict string parity, confidence parity, direction derivation
  (NO-TRADE→NONE, WEAK carries direction), and disposition semantics vs the CSV-derivable gates
  (direction / tier / MTF pass flags / BELOW_MIN_MOVE).
- **Token distribution (all 8 classes expected, nothing unexplained):** 1139 `refused: direction` ·
  173 `refused: tier` · **69 `would-act`** · 11 `stale` · 5 `refused: cooloff` · 3 `skipped` ·
  3 `refused: not_flat` · 1 `refused: not_connected`. **No `refused: policy` (the gate ships
  disabled — soak-safety confirmed in the wild), no `rejected:`, no parse errors, no
  `refused: levels/schema/size/window`.**
- **would-act: 69/69 numerically verified** — entry = CSV `Price`, stop/target = the direction's
  `PlacedStop*/PlacedTarget*`, every level > 0, tolerance 0.011 (F2 display). Size = 10 on every
  row (the standing min-size Amount). R2 (engine levels as-is) holds end to end.
- Every operational refusal (`cooloff`/`not_flat`/`not_connected`) sits on a row whose §4.4 prefix
  is clean on the CSV columns — i.e. they refused signals the contract chain had genuinely passed,
  exactly where those gates live.

## The six unmatched rows — all explained, all one mechanism

All six log rows without a CSV counterpart are **SKIPPED-class payloads, which by contract carry no
verdict and therefore no analysis row** (§3: "no verdict exists on a skip"): the 3 live `skipped`
dispositions (cc2e5e5a #72, 37f850e4 #48/#51 — engine stand-downs mid-burst/stall) — **exactly the
log's total skip count, 1:1** — plus 465f9ac1 #11 ×3, a final SKIPPED payload left on disk when the
engine stopped pre-soak (Jul 14–15), re-read as `stale` by later app starts. Emission ⊃ analysis
for skips is design, not divergence. No action for either seat.

## Identity coverage — every gap accounted for

- **Duplicates (6 pairs):** all are app-restart re-reads of the still-current payload — the
  in-memory same-(inst,id) suppression is per-session by design; every dup pair carries the
  identical disposition (2 pre/early `stale` pairs; 4 same-minute restart pairs, incl. this
  morning's 380bf062 #80 — the owner's post-push restart). Zero contradictory dups.
- **In-range gaps:** every one matches an app-off window (our-log time jump ≈ gap, e.g. 6f6f0ba9
  #48→#116 = 67 ids over ~68 min), an **engine catch-up burst coalesced by the payload mailbox**
  (f90f59c4 #31→#37: CSV shows 10-second cadence there — the file is last-writer-wins by design,
  the app legitimately consumes the newest), or an **engine-side emission stall mirrored in its own
  CSV** (37f850e4 #47→#50: the CSV itself jumps 23 min there). No unexplained miss, no phantom row,
  no row the app invented.

## What this proves (the soak's exit claims)

1. The consumer's gate chain matched the engine's book **gate-for-gate for ~6.5 days across 15
   engine restarts and multiple app restarts** — first-failing-gate discipline, display parity, and
   R2 level fidelity all hold at column level.
2. The D1-culture / identity / de-dupe machinery held: no parse errors, no watermark violations
   (would-act ids strictly advance), restart semantics exactly as designed.
3. The session-policy gate's disabled-parity claim is now proven **on the live stream**, not just
   in acceptance: zero policy tokens across the whole soak.

## Standing

Contract §7 prescribes 1–2 supervised weeks; the lower bound lands ~2026-07-23 with this evidence
already clean. **Extending vs calling it is the owner's decision**; the remaining gate for
live-at-min-size is unchanged either way — the engine-side placed-geometry pass (cross-app, owner
confirms), after which: §6 interlock ladder + §6.2/6.3 live tests at min size, then the owner
enables the session policy per P5.
