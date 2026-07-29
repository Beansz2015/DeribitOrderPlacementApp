# Micro-spec — move the SL-backoff success-reset to a CONFIRMED success (N1c)

**Status: IMPLEMENTED 2026-07-30** — commits `a960f07` (commit 1) + `9d3b9f5` (commit 2), gate
executed at each and at final HEAD: **GATE PASSED, OrderCheck 146 → 153**. Report:
`docs/impl-report-sl-backoff-confirmed-reset.md`. Acceptances 1, 2 and 4 CLOSED in the report
(4 with one flagged exception: commit 2 deliberately changes a log line every normal session
prints); **acceptance 3 is OWNER-RUN runtime** — recipe in the report §5. Nothing pushed.
*(Was: WRITTEN 2026-07-30, owner-tick pending on scheduling — recommended slot after the
fee-comms repoint, before normal size, same before-normal-size class as N1b.)* Origin:
`spec-back-sl-backoff-escalation-defect.md`, defect CONFIRMED by the coordinator (ruling in
`spec-sl-backoff-coupling.md` §Acceptance amendment 2026-07-30). **Recommended implementer: Opus
HIGH, fresh conversation** — SL hot path + the reconciliation discriminator. Standing ground
rules (gate per commit, never push, anchors by symbol at HEAD, impl report).

## The defect being fixed

The chase's `slUpdateFailures = 0` ("success clears the backoff") runs on **send-completion**,
and the send never throws — so it runs on rejected edits too. The rejection then increments 0→1
via the N1b coupling: the counter oscillates 0↔1 and the backoff pins at 666 ms. The escalation
half of the #4 retry-amplifier fix is undelivered; a persistently failing chase retries at
666 ms forever instead of backing off toward the 5 s cap.

## Commit 1 — the reset moves to the confirmed-success signal

Remove the reset from the chase `Try`'s completion path and re-home it on **the exchange's
confirmation of our own SL edit**: the echo-recognition branch that consumes
`RecordCommandedSLPrice`'s record (reconcile 4a — the single send point for every triggered-SL
edit routes through `RecordCommandedSLPrice`, so a recognized commanded-price `open` echo IS the
confirmation). Implementer verifies at HEAD which edit paths feed the discriminator and states in
the impl report which of the three coupled ids gain a confirmed reset (the chase must; others as
the discriminator already covers them — do not widen the discriminator itself).

**Do-not-touch, load-bearing (ruled with the defect confirmation):**
- `lastStopLossUpdate = currentTime` stays on the attempt — it is the anti-duplicate throttle
  stamp (SL-chase v2: without it a second tick passes the gate mid-flight and dispatches a
  duplicate edit). Only the COUNTER's reset moves.
- `placedStopLossPrice = newStopPrice` stays optimistic — it is the chase reference, advanced
  deliberately (runaway-fix invariant). Its divergence-on-reject is accepted and now documented.
- The emergency block, `emergencyFired`, the N1b coupling condition, the benign-race `Return`s.

**Accepted residual:** a lost/unrecognized echo leaves the counter un-reset until the next
confirmed success, so a later transient failure starts one step escalated — bounded by the 5 s
cap and self-healing on any confirmed edit; same class as the cross-position persistence already
ruled in `a973c3f`.

## Commit 2 — log honesty + the coupling diagnostic (3b(ii))

- The chase's completion log `SL repositioned: $X → $Y` becomes **`SL reposition sent: $X → $Y`**
  — accurate for both outcomes, no mechanism change. (The confirmed echo already produces the
  reconcile machinery's own recognition; do not add a second "confirmed" line.)
- At the N1b coupling site, after `BackoffStopLossRetry`, one gray diagnostic line:
  failure count + resulting backoff (e.g. `SL-edit failure #3 - backoff 2.7s`). Makes the live
  path legible the first time SL edits fail for real; receive-thread-safe (log delegate only).

## Fixtures + acceptance

1. Gate per commit. The 3b(i) helper's fixtures (landed as N1b's closing commit) EXTEND: the
   confirmed-reset state machine now proves real escalation (1 → 2 → … → cap) under repeated
   failures and reset-on-confirmation — the oscillation fixture flips from documenting the
   defect to guarding against its return.
2. Censuses: `slUpdateFailures = 0` moves site (old site count 0 at the chase Try; exactly one
   new reset site at the recognition branch); `BackoffStopLossRetry` call sites unchanged at 3;
   `emergencyFired` 1+2+3+3 (raw 10); tripwires unchanged.
3. Owner runtime (isolated harness, testnet, owner-mouse): the corrected N1b acceptance-2 pump
   recipe now works WITHOUT the manual-click pump — the dead-order chase alone escalates
   666 → 1332 → … and the `SL update rate limited` line prints from the chase path; emergency
   still fires pre-throttle exactly once. The gray diagnostic lines show the climb.
4. Normal sessions byte-identical (no red SL-edit failure ⇒ counter 0 ⇒ flat 333 ms; the only
   moved code runs on the recognition branch and the failure path).
