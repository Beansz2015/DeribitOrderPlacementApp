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

## Acceptance — AMENDED 2026-07-30 (escalation-defect ruling on `spec-back-sl-backoff-escalation-defect.md`)

**Defect CONFIRMED by the coordinator at the sites:** the chase's success-reset is OPTIMISTIC —
the dead chase `Catch` (established in the N1b review) means the `Try` ALWAYS completes, so
`slUpdateFailures = 0` runs on rejected edits too; the rejection then increments 0→1 via the
coupling. **From the chase path alone the counter oscillates 0↔1 and the backoff pins at 666 ms**
(one response per attempt, every attempt resets, single-flight serializes). The original
acceptance 2's observable (`SL update rate limited`, gated `remainingMs > 1000` ⇒ needs the
counter ≥ 2 at increment) is therefore UNREACHABLE from the chase path — it cannot have been
observed, and the fix is `spec-sl-backoff-confirmed-reset.md` (own spec). **The counter CAN
exceed 1 today via the no-reset pump paths:** the manual SL button (223346) and the trailing-SL
loop (223348) increment with no intervening reset — which is what the corrected acceptance 2
uses. N1b's coupling itself is correct as ruled; nothing in this amendment changes commit 2.

1. Gate per commit. Greps: `BackoffStopLossRetry` call sites = the existing 2 + exactly 1 new (the
   error arm); **the new site's id condition names exactly `223346`, `223348`, `223350`** (2026-07-28
   amendment); `emergencyFired` census untouched (1+2+3+3 — raw grep 10, incl. the N1-era comment);
   tripwires unchanged. **PASSED (review `c5f539a` + re-verified at the defect ruling).**
2. **CORRECTED — emergency independence, via the reachable pump recipe** (isolated harness,
   testnet, owner-mouse): dead-order chase live (SL cancelled outside the app; chase red-fails on
   223350 each tick) + **two or more manual SL button clicks** (each a red 223346, no reset
   between them) ⇒ counter ≥ 2 ⇒ stamp goes multi-second ⇒ the `SL update rate limited` line
   PRINTS (now reachable); then force the M.SL trip ⇒ **emergency fires immediately, exactly
   once, despite the multi-second throttle** (the N1 hoist's guarantee under a genuinely engaged
   backoff — the point of the ordering, preserved).
3. Benign-race exclusion: **structurally ratified in the review** (the gray branch `Return`s
   ahead of the coupling); the runtime eyeball test is superseded by the N1b-closeout fixtures
   (the 3b(i) extraction below) — "no increment" becomes a fixture assertion.
4. Normal sessions byte-identical (no red SL-edit errors ⇒ counter stays 0 ⇒ flat 333 ms as
   today). **Unaffected by the defect; stands as argued.**
5. **ADDED (3b(i), approved for immediate execution as N1b's closing commit — gate-only, no
   behaviour change):** extract the backoff arithmetic to a `Friend Shared` helper
   ((failures, failedAt) → (newFailures, stamp)) + OrderCheck fixtures pinning the arithmetic AND
   proving today's 0↔1 chase-path oscillation deterministically (`InternalsVisibleTo("OrderCheck")`
   exists — the `EffectiveSizeUsd`/`SessionBucketFor` precedent). When the confirmed-reset spec
   lands, these fixtures extend to prove real escalation.
