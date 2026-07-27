# Spec — couple real SL-edit failures into the retry backoff (§2 ruling of the N1 closing spec-back)

**Coordinator ruling 2026-07-28 on `spec-back-emergency-hoist-acceptance-2026-07-28.md` §2:
OPTION (a) — RATIFIED**, as its own small spec (this doc), sequenced **before normal size** but
after the EV chase budget (no date pressure; the exposure it closes is only latent until this very
coupling makes the backoff reachable — the spec-back's ordering argument is endorsed: N1 already
removed the emergency-latency cost pre-emptively, so landing this is now safe). Option (b) —
REJECTED as the spec-back recommends (the retry-amplifier concern was real; keep the mechanism).
The **"~5 s" framing correction is mandatory regardless** and is split: docs corrected by the
coordinator (`ROADMAP-2026-08` N1 line + a dated addendum on `review-emergency-hoist.md`, same
commit as this spec); the **item-16 comment at the throttle gate is corrected as THIS spec's
commit 1** (comment-only, zero IL — the housekeeping-17 precedent).

**Recommended implementer:** **Opus HIGH, fresh conversation** (SL hot path — a new coupling onto
the throttle deserves its own review, exactly as the spec-back argued).
**Target:** `frmMainPageV2.vb` only. **Ground rules:** standing (gate per commit, never push,
anchors by symbol re-verified at HEAD, impl report).

## Commit 1 — correct the throttle-gate comment (no IL)

The item-16 comment (and the `:2172`/`:2199` N1-era comments) say the backoff "delayed emergency
detection by up to ~5 s". Correct to the truth established by the spec-back's evidence chain: the
backoff has been **unreachable** (send-site swallow-catches + the wrapped body), `slUpdateFailures`
has always been 0, the pre-N1 exposure was ≤ 333 ms plus the latent double-fire race — and this
spec is what makes the backoff reachable for the first time, with N1's hoist already immunising
the emergency path against it.

## Commit 2 — the coupling

In `HandleUnhandledJsonRpcError`'s **SL-edit error arm (ids 223344–223350)** — the seam that
already parses the error and knows the ids — call `BackoffStopLossRetry(DateTime.UtcNow)` for
**genuine edit failures only**:

- **EXCLUDED, load-bearing:** the benign `already_closed` chase-race downgrade (the gray-line
  class) must NOT count — it is the fill winning a race, not an edit failing. Same for the id-31
  benign abort race. Only errors that survive to a RED emission on those ids couple in.
- The success-reset (`slUpdateFailures = 0` at the edit-success site) already exists — verify,
  don't duplicate.
- **Implementer re-verification item:** the spec-back asserts the `:4246` `too_many_requests`
  arm is also currently unreachable — re-derive that as part of the anchor pass; if it turns out
  reachable, this coupling simply joins it (no design change), but the impl report must say which.
- Thread discipline: `HandleUnhandledJsonRpcError` is receive-thread; `BackoffStopLossRetry`
  writes `slUpdateFailures`/`lastStopLossUpdate` — engine fields, same-thread class as their
  existing writers. Confirm, don't assume.

## Acceptance

1. Gate per commit. Greps: `BackoffStopLossRetry` call sites = the existing 2 + exactly 1 new (the
   error arm); `emergencyFired` census untouched (1+2+3+3); tripwires unchanged.
2. **Emergency independence (the point of the ordering):** with a persistent SL-edit failure now
   genuinely driving the backoff to multi-second throttles, the M.SL cap still fires pre-throttle
   — spec-emergency-hoist §Acceptance 2's scenario becomes REACHABLE for the first time; run it
   (isolated harness, testnet): backoff engages (the `SL update rate limited` line appears — its
   first-ever genuine appearance), emergency fires immediately regardless, exactly once.
3. Benign-race exclusion: force the chase race (the standing recipe) — gray line, **no backoff
   increment** (log the counter in the report's evidence).
4. Normal sessions byte-identical (no red SL-edit errors ⇒ counter stays 0 ⇒ flat 333 ms as today).
