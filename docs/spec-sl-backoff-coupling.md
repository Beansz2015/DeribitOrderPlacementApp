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

**AMENDED 2026-07-28 (coordinator ruling on `spec-back-sl-backoff-coupling-id-scope.md`: OPTION
(a)).** The original text said "SL-edit error arm (ids 223344–223350)"; the implementer proved
that range is the WHOLE order-edit id space (verified independently by the coordinator at the
senders: 223344 = entry main/pre-fill TP/post-fill TP re-anchor, 223345 = manual TP button,
223347 = trailing main, 223349 = reduce reposition — none an SL edit). The range was shorthand
carried from the benign-race arm (where all-edit-ids is CORRECT — any edit can lose the chase
race), not a deliberate widening; taken literally it breaks acceptance 4 and re-couples non-SL
failures onto the SL hot path. Therefore:

In `HandleUnhandledJsonRpcError`, couple **the SL-edit ids ONLY: `223346` (pre-fill secondary SL
+ manual SL button), `223348` (trailing SL), `223350` (the triggered-SL chase — the one path the
throttle it feeds actually governs)** — call `BackoffStopLossRetry(DateTime.UtcNow)` on the
**red emission path** for those ids:

- **Hook placement RATIFIED as the implementer derived it:** hanging the coupling on the red
  emission automatically excludes the benign `already_closed` chase-race downgrade and the id-31
  abort race (both `Return` before the red emission) — no extra condition needed.
- The success-reset (`slUpdateFailures = 0` at the edit-success site) already exists — verify,
  don't duplicate.
- **Accepted residual (ruled with the amendment):** the counter persists across positions (the
  only reset is the successful chase edit; the mechanism goes live for the first time with this
  commit). Benign by construction: a pre-armed counter changes nothing unless the chase is ALSO
  failing — its first success resets to 0, and only on a failure does the pre-arm shorten the
  escalation to the 5 s cap, which is exactly when escalation is wanted. No placement-seed reset
  in this pass; revisit only if runtime shows noise.
- **Re-verification items: CLOSED by the spec-back** — both existing `BackoffStopLossRetry` call
  sites confirmed dead (send-site swallow-catches; the wrapped edit body), so this coupling is
  the backoff's first reachable trigger. Thread discipline confirmed with the spec's wording
  corrected: the writer set is the receive loop AND its post-await threadpool continuations (the
  accepted lock-free torn-write class, `emergencyBaseline` precedent) — the new receive-thread
  writer adds no class not already present; **no new synchronisation**.

## Acceptance

1. Gate per commit. Greps: `BackoffStopLossRetry` call sites = the existing 2 + exactly 1 new (the
   error arm); **the new site's id condition names exactly `223346`, `223348`, `223350`** (2026-07-28
   amendment); `emergencyFired` census untouched (1+2+3+3 — raw grep 10, incl. the N1-era comment);
   tripwires unchanged.
2. **Emergency independence (the point of the ordering):** with a persistent SL-edit failure now
   genuinely driving the backoff to multi-second throttles, the M.SL cap still fires pre-throttle
   — spec-emergency-hoist §Acceptance 2's scenario becomes REACHABLE for the first time; run it
   (isolated harness, testnet): backoff engages (the `SL update rate limited` line appears — its
   first-ever genuine appearance), emergency fires immediately regardless, exactly once.
3. Benign-race exclusion: force the chase race (the standing recipe) — gray line, **no backoff
   increment** (log the counter in the report's evidence).
4. Normal sessions byte-identical (no red SL-edit errors ⇒ counter stays 0 ⇒ flat 333 ms as today).
