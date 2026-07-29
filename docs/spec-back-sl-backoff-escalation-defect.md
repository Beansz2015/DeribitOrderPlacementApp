# Spec-back — the SL backoff cannot escalate past one step, and acceptance 2 is not reproducible

**For:** the coordinator. **From:** the N1b implementer seat, 2026-07-29 (after the approval `c5f539a`).
**Status:** **no code changed.** Found while writing the owner's acceptance-2/3 runtime recipe — the
recipe could not be made to produce the observable the spec asks for, and the reason is a defect in
the mechanism N1b just made reachable. **File:** `DeribitOrderPlacementApp/frmMainPageV2.vb`.

> ⚠️ Anchors are at `e58766c` (code unchanged since `6d23bf0`) and will drift. Locate by symbol.

**⚠️ This contradicts a recorded state.** `review-sl-backoff-coupling.md` (`c5f539a`) records N1b as
closing on "the owner's acceptance-2/3 runtime RECORD (runs already PASSED per the handoff)". Per §2
below, **acceptance 2's stated observable cannot have appeared** — the `SL update rate limited` line
is unreachable at the only backoff level the chase path can produce. Whatever passed, it was not
acceptance 2 as written. The runtime record should not be written up until this is ruled.

---

## 1. The defect — the success-reset is optimistic, and it fires on rejected edits too

`SendWebSocketMessageAsync` swallows on all three catch arms (the same fact that made the backoff
unreachable before N1b). So `UpdateStopLossForTriggeredStopLossOrder` **returns normally when the
exchange rejects the edit**, the chase's `Try` block completes, and `slUpdateFailures = 0` at
**`:2375`** ("success clears the backoff") executes. The rejection arrives milliseconds later, on the
receive thread, and N1b's new coupling at `:1647` increments it back to 1.

Per failing chase attempt, in order:

| # | Thread | Event | `slUpdateFailures` |
|---|---|---|---|
| 1 | quote/continuation | send returns (swallowed), `Try` completes, `:2374`–`:2375` run, log prints `SL repositioned` | **0** |
| 2 | receive | red `API ERROR (id 223350)`, coupling at `:1647` fires | **1** |
| 3 | quote/continuation | next attempt clears the throttle, `:2375` runs again | **0** |

**The counter oscillates 0↔1 and can never reach 2.** For it to reach 2, two error responses would
have to land with no intervening chase attempt — but there is exactly one response per attempt, and
every attempt runs the reset. The `isSLRepositioning` single-flight (`:2345`) serialises attempts, so
there is no interleaving that produces two consecutive increments.

**Consequences:**

- The backoff is pinned at `Math.Min(333 × 2¹, 5000)` = **666 ms**. It can never reach the 5 s cap,
  or any multi-second value, from the chase path.
- The #4 retry-amplifier fix is therefore still only half-delivered: a persistent failure halves the
  retry rate (333 → 666 ms) instead of escalating away from it. N1b made the mechanism *reachable*
  but not *effective*.
- `SL repositioned: $X → $Y` prints on every rejected edit — the log actively asserts a reposition
  that the exchange refused. `placedStopLossPrice` also advances (`:2364`), so the chase reference
  tracks a stop that isn't there. (Pre-existing, not introduced by N1b; in scope here because it is
  the same optimistic-completion root cause.)

## 2. Why acceptance 2 is not reproducible as written

Acceptance 2 asks for "a persistent SL-edit failure now genuinely driving the backoff to
multi-second throttles" and names the observable: the `SL update rate limited` line's "first-ever
genuine appearance". That line is at `:2397`, gated `remainingMs > 1000` at `:2396`. With
`lastStopLossUpdate = failedAt + (backoffMs − 333)` (`:1969`):

```
remainingMs(now) = 333 − (now − lastStopLossUpdate) = backoffMs − (now − failedAt)
max remainingMs ≈ backoffMs
⇒ the line prints iff backoffMs > 1000 ⇒ 333 × 2ⁿ > 1000 ⇒ n ≥ 2
```

At the pinned **n = 1 (666 ms) the line can never print.** The acceptance's own instrument is
unreachable. This is the second time an acceptance on this mechanism has turned out unobservable —
`runtime-record-emergency-hoist-2026-07-27.md` §6 raised the first, and that is what produced N1b.

**How I verified the failure mode is reachable at all** (so the defect is about escalation, not about
the coupling): the `cancelled` echo for `StopLossOrder` (`:3194`–`:3211`) sets only `OpenPositions`
and clears `cancelPending` — it does **not** clear `SLTriggered` or `PositionSLOrderId`. So an SL
order cancelled outside the app leaves the chase gate at `:2231` open, and every chase tick edits a
dead order → `10004 order_not_found` → red on 223350 → the coupling fires. That part works.

## 3. Options

### 3a. The escalation defect

| | Option | Assessment |
|---|---|---|
| **(a)** | **Move the reset to a genuine success signal** — reset only when the exchange confirms the edit (our own `open` echo / the commanded-price set), not on send-completion. | Correct fix, and it makes the log honest too. Touches the SL hot path and the reconciliation discriminator — a real spec, not a patch. |
| **(b)** | **Don't reset on the attempt; decay instead** — leave `:2375`, but only reset after a confirmed-clean interval. | Cheaper, still leaves `SL repositioned` lying on rejects. |
| **(c)** | **Accept and amend acceptance 2** — document that the backoff is a one-step 333→666 ms damper by construction, and rewrite the acceptance to that. | Honest and zero-risk, but concedes that the escalating backoff never escalates. |
| **(d)** | Do nothing, close N1b on acceptance 1 + 4. | Not recommended — leaves a recorded "PASSED" against an unobservable test. |

I lean **(a)**, sequenced as its own spec after the current queue; **(c)** as the immediate
bookkeeping regardless, since acceptance 2 must be corrected either way.

### 3b. Observability — and the part that needs no trades

| | Option | Assessment |
|---|---|---|
| **(i)** | **Gate fixtures.** Extract the backoff arithmetic to a `Friend Shared` helper and cover it in OrderCheck. | **Recommended.** `InternalsVisibleTo("OrderCheck")` already exists (`DeribitOrderPlacementApp.vbproj:21`); this is exactly how `EffectiveSizeUsd`, `SessionBucketFor` and the notifier limiter are tested. Proves the oscillation **deterministically, with zero trades**, and locks the fix against regression — which a one-off testnet observation never does. The extraction is pure arithmetic (counter + timestamp in, stamp out); no behaviour change. |
| **(ii)** | **Gray diagnostic at the coupling site** — log the failure count and resulting backoff. | Useful in production the first time SL edits fail for real, and makes acceptance 3's "no increment" directly observable. Adds a log line on the SL error path; small. |
| **(iii)** | Leave both invisible. | Then acceptance 2/3 can only ever be argued, not observed — the position we are already in. |

**(i) and (ii) are complementary**: (i) proves the *logic* offline, (ii) makes the *live* path
legible. (i) is the one that removes the need to place trades to close this out.

## 4. Consequence for acceptance 3

Acceptance 3 (benign-race exclusion) is **structurally sound and unaffected** — the gray branch
`Return`s at `:1568`, ahead of the coupling at `:1647`, which the coordinator already walked and
ratified. But its runtime evidence is weak for the same observability reason: `slUpdateFailures` is
private and never displayed, and the log carries no timestamps, so "no increment" cannot be observed
directly. Under option (i) it becomes a fixture assertion instead of an eyeball test.

## 5. What I recommend

1. Rule on §3a — at minimum **(c)**, so acceptance 2 stops asserting an unreachable observable.
2. Approve **§3b(i)** as a small standalone item I can execute immediately: extraction + OrderCheck
   fixtures proving 0↔1 oscillation today, and (after a §3a fix) proving real escalation. No trades,
   no testnet, gate-only.
3. Hold the acceptance-2/3 runtime record until 1–2 land; re-scope the owner's run to what is
   actually observable.

Related: `spec-sl-backoff-coupling.md` (amended `a973c3f`), `review-sl-backoff-coupling.md`
(`c5f539a`), `impl-report-sl-backoff-coupling.md` (`65ef548`),
`runtime-record-emergency-hoist-2026-07-27.md` §6 (the first unobservable-acceptance finding),
`spec-cross-thread-fix.md` (#4, the retry-amplifier fix this defect half-neuters).
