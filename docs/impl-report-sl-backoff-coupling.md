# Impl report — `spec-sl-backoff-coupling.md` (N1b): couple genuine SL-edit failures into the backoff

**Implementer:** Opus HIGH, fresh conversation, 2026-07-28. **File:** `DeribitOrderPlacementApp/frmMainPageV2.vb`
(only). **Commits:** `afb4bbc` (commit 1) · `f7c458e` (spec-back, id scope) · `6d23bf0` (commit 2).
**Gate:** GATE PASSED / OrderCheck **124/124** at each. **Not pushed** — owner is the only pusher.

> ⚠️ Anchors are at `6d23bf0` and will drift. Locate by symbol.

---

## ADDENDUM 2026-07-30 — N1b's CLOSING COMMIT: the 3b(i) extraction + fixtures (`d3c9a04`)

Approved in the escalation-defect ruling (`513a2c8`, `spec-sl-backoff-coupling.md` §Acceptance 5).
**Gate-only, no behaviour change, no trades.** Files: `frmMainPageV2.vb` + `tools/OrderCheck/Program.vb`.

**1. The extraction.** `NextSlBackoff(failures, failedAt) → (Failures, Stamp)`, `Friend Shared` on
`frmMainPageV2` immediately above its caller — the `RoundTripFeePctFromMakerBps` /
`IsChaseEvExhausted` precedent, so **no new file and no constants moved** (a `Shared` member reads
the type's own `Private Const`s, so `MinStopLossUpdateInterval` and `SLUpdateMaxBackoffMs` stay
exactly where they are, with all five of their other references untouched). `BackoffStopLossRetry` is
now a three-line caller owning the two field writes and nothing else.

*Behaviour identity, the one subtlety worth stating:* the old code assigned `slUpdateFailures` **first**
and then used the **new** value in `2 ^ slUpdateFailures`. `n` is that new value and feeds the same
exponent, so the arithmetic is identical rather than off-by-one — and the caller writes the same two
fields in the same order (counter, then stamp). Cross-checked against the fixtures: 0 → 1 failure →
666 ms, which is what the pre-extraction code produced.

**2. Fixtures: 124 → 140 (+16).** Four groups:

| Group | What it pins |
|---|---|
| Arithmetic | 666 ms at one failure · doubling · the 5 s cap (5328 would overshoot) · counter clamp at 8 · the stamp formula `failedAt + backoffMs − 333`, recovered *from the stamp* so the formula is pinned rather than restated |
| Acceptance-2 threshold | the rate-limited line needs `remainingMs > 1000` ⇒ needs 2+ failures — the reason acceptance 2 was corrected, now an assertion instead of prose |
| Today's chase path | reset-per-attempt + one increment per response never exceeds **1 failure / 666 ms** ⇒ never escalates. Documents the defect N1c fixes |
| No-reset run | escalates 666 → 1332 → 2664 → 5000 and saturates. True **today** on the pump paths (manual SL button 223346, trailing-SL loop 223348); becomes the chase path once N1c moves the reset |

The last two are deliberately the same shape, so N1c **extends** them (point the chase model at the
confirmed-reset state machine) rather than rewriting: the oscillation fixture then flips from
documenting the defect to guarding against its return, exactly as N1c §Fixtures anticipates.

**3. Censuses — RUN, not assumed** (at `d3c9a04`):

- `BackoffStopLossRetry` **raw 7 = 1 decl + 3 comments + 3 call sites** (`:1647`, `:2398`, `:4394`) —
  unchanged. The new comment block avoids the token deliberately (prose-near-tripwire rule; this era
  has hit that trap twice).
- `slUpdateFailures = 0` reset sites: **exactly 1**, still at the chase `Try` (`:2389`) — the site
  N1c will move.
- `emergencyFired` **raw 10 / 9 code sites** (1+2+3+3) — untouched.
- `IsATRSlippageExcessive` 8 · `RecordCommandedSLPrice` 3 · `isSLRepositioning` 3 — unchanged.
- New symbol `NextSlBackoff` = 2 in `frmMainPageV2.vb` (declaration + the caller).

**Gate: GATE PASSED, OrderCheck 140/140.** Nothing found worth a spec-back.

**Out of scope and untouched, per the ruling:** the N1c reset move, the log-honesty reword, the gray
diagnostic — all of `spec-sl-backoff-confirmed-reset.md`. The acceptance-2/3 runtime record stays
HELD; the owner runs the corrected pump recipe, or waits for N1c.

---

## 1. What shipped

**Commit 1 `afb4bbc` — the "~5 s" framing correction (comment-only, zero IL).** Both comments the
spec named — the item-16 trade-off comment at the throttle gate and the N1-era hoist comment on the
threshold check — now state the truth: the backoff has never been reachable, so `slUpdateFailures`
has always been 0, the gate has always been a flat 333 ms, and the real pre-hoist exposure was
≤ one throttle interval plus the latent double-fire race. `git diff -w` showed **no non-comment
changed line**.

**Commit 2 `6d23bf0` — the coupling.** In `HandleUnhandledJsonRpcError` (`:1538`), on the **red
emission path**, for the SL-edit ids only:

```vb
If messageId.HasValue AndAlso
   (messageId.Value = 223346 OrElse messageId.Value = 223348 OrElse messageId.Value = 223350) Then
    BackoffStopLossRetry(DateTime.UtcNow)
End If
```

`:1646`–`:1647`. **The IL delta is exactly those four lines**; every other changed line in the commit
is a comment. Null-safe positive `HasValue AndAlso` gate, per the standing receive-loop id-guard rule.

## 2. The spec defect that was escalated and ruled

Raised **before** implementing, per the standing rule; the owner directed escalation rather than an
implementer judgment call. Full evidence chain in `spec-back-sl-backoff-coupling-id-scope.md`
(`f7c458e`); **ruled `a973c3f` = option (a)** and the spec amended in place.

The spec's original "SL-edit error arm (ids 223344–223350)" named the **whole** order-edit id space.
Four of the seven are not SL edits — 223344 (entry main / pre-fill TP / post-fill TP re-anchor),
223345 (manual TP), 223347 (trailing main), 223349 (reduce reposition). The sharp case:
`ReanchorTPToFillAsync` sends **223344 post-fill**, off the `open` TP echo — so under the literal
range a red TP error would push `lastStopLossUpdate` forward and throttle a **live SL chase** by up
to 5 s, the exact harm N1 was hoisted to remove. It also made the spec's own acceptance 4
unsatisfiable. Implemented as ruled: **223346 / 223348 / 223350**.

## 3. Why the hook sits on the red emission (ratified, and load-bearing)

`HandleUnhandledJsonRpcError` returns early, above the red emission, for every class that must NOT
count:

| Class | Anchor | Why it must not couple |
|---|---|---|
| `already_closed` chase race (edit ids) | `:1567` → `Return` | the fill won a race; the edit didn't fail |
| id-31 benign abort race | `:1583` → `Return` | same — the fill won |
| code 10028 | `:1555` → `Return` | owned by `HandleRateLimitError` |
| placements (≥ `PlacementIdBase`), ids 3/999/777 | `:1552`, `:1549` | dedicated handlers |

So placing the call **after** the red `AppendColoredText` gets the spec's "only errors that survive
to a RED emission couple in" for free — **no extra condition**, and the benign exclusions cannot
drift apart from the coupling later.

**Observation worth recording (no action taken):** the 10028 early-return means the **rate-limit
class does not couple**. That is faithful to the ruling, and is mostly moot — the `:4380` arm that
was *meant* to catch that class is itself dead (§4). But it means the one failure mode a backoff is
most classically for is currently uncoupled. Flagging for the reviewer; I did not act on it.

## 4. Re-verification items — both closed

**The `too_many_requests` arm (spec `:4246`, now `:4380`) is UNREACHABLE.** Derived independently
rather than inherited from the spec-back:

1. `SendWebSocketMessageAsync` (`:1123`) catches `WebSocketException`, `OperationCanceledException`
   and `Exception`, and swallows all three — no send propagates.
2. No local `Throw` in the project carries `too_many_requests` or `10028`; the only occurrences of
   those tokens are response-side (`:1517`, `:1555`).
3. ⇒ nothing can satisfy that `ex.Message.Contains` gate.

The chase `Catch` (`:2380`-area, call at `:2384`) is dead for the same reason — its awaited body is
fully wrapped in its own `Try/Catch`, and `UiInvoke` is non-blocking `BeginInvoke`. **Both
pre-existing call sites are dead, so `:1647` is the backoff's first reachable trigger in the app's
history.** No design change either way, as the spec anticipated.

**Thread discipline — confirmed, with the spec's wording corrected (and now amended into it).** The
writer set is the receive loop **and its post-await threadpool continuations**, not one thread:
`HandleUnhandledJsonRpcError` is called synchronously at `:1357` (raw receive thread), whereas
`HandleQuoteUpdates` is `Private Async Sub` (`:2016`) so its writes at `:2374`–`:2375`/`:2384` run on
continuations, as does `:4380`. The pair is already lock-free across that set — the accepted
torn-write class with the `emergencyBaseline` precedent. The new writer is the only one *not* on a
continuation and **adds no class not already present**; no new synchronisation.

**Success reset: verified, not duplicated.** Exactly one site, `:2375` (`slUpdateFailures = 0`,
"success clears the backoff"), on the successful triggered-SL chase.

## 5. Acceptance

| # | Item | Status |
|---|---|---|
| 1 | Gate per commit + greps | **PASS** — see below |
| 2 | Emergency independence (persistent SL failure drives the backoff; M.SL still fires pre-throttle, exactly once) | **OWNER-RUN** (runtime, testnet) |
| 3 | Benign-race exclusion (gray line, no increment) | **OWNER-RUN** (runtime, testnet) |
| 4 | Normal sessions byte-identical | **PASS by construction** — no red SL-edit error ⇒ the guard never fires ⇒ counter stays 0 ⇒ flat 333 ms. This is now true *as written*, which it would not have been under the original id range. |

**Acceptance 1 censuses at `6d23bf0`:**

- `BackoffStopLossRetry` **call sites = 3** — the existing two (`:2384` chase `Catch`, `:4380`
  rate-limit arm) **+ exactly 1 new** (`:1647`). Raw grep 6 → 7; **the delta is purely the call**,
  because the new comment deliberately avoids the token (HANDOVER-4 §5 tripwire-pollution rule).
- New site's id condition names **exactly `223346`, `223348`, `223350`** (`:1646`) — the amended
  acceptance-1 clause.
- `emergencyFired` **1 decl + 2 sets + 3 clears + 3 reads = 9 code sites, raw 10** — untouched.
- Tripwires unchanged: `IsATRSlippageExcessive` 8, `RecordCommandedSLPrice` 3, `isSLRepositioning` 3.

**One self-correction folded into commit 2:** commit 1's prose had introduced the token
`emergencyFired` into a comment, moving that standing census from raw 10 → 11 — precisely the
tripwire pollution HANDOVER-4 §5 warns about, caught by running the acceptance-1 grep rather than
assuming. Reworded to "the N1 single-fire latch below"; raw is back to **10** and matches both the
amended acceptance and the recorded grep trap.

## 6. Runtime items for the owner (acceptance 2 + 3)

Standing preconditions first: **x64 rebuild** (the gate builds AnyCPU only) → **re-check the window
title reads `— TESTNET`** (the rebuild clobbers the bin's `secrets.json` from project source) →
**back up `orderapp-settings.json`** (FormClosing persists all 11 geometry fields) → **owner mouse
clicks only** for every trade-placing step, per the triple-placement WATCH protocol.

- **Acceptance 2 — emergency independence.** `spec-emergency-hoist.md` §Acceptance 2's scenario is
  reachable for the first time. Drive a persistent SL-edit failure on the isolated harness; expect
  the `SL update rate limited: N.Ns remaining` line at `:2397` to appear — **its first-ever genuine
  appearance** (it is gated on `remainingMs > 1000`, which requires `lastStopLossUpdate` in the
  future, which only the backoff ever does) — and the M.SL cap to fire immediately regardless,
  exactly once.
- **Acceptance 3 — benign-race exclusion.** Force the chase race by the standing recipe: gray
  `Order edit skipped … benign chase race` line, and **no** backoff increment. Evidence to log: the
  absence of the rate-limited line on subsequent ticks (the counter is private, so the observable
  proxy is that the throttle stays flat 333 ms).

## 7. Residual carried (ruled with the amendment, restated so it is not re-litigated)

The failure counter **persists across positions** — the only reset is the successful chase edit at
`:2375`. Accepted as benign by construction: a pre-armed counter changes nothing unless the chase is
*also* failing, its first success resets it to 0, and on a failure the pre-arm only shortens
escalation toward the 5 s cap — which is exactly when escalation is wanted. No placement-seed reset
this pass; revisit only if runtime shows noise.

Related: `spec-sl-backoff-coupling.md` (amended `a973c3f`),
`spec-back-sl-backoff-coupling-id-scope.md`, `spec-back-emergency-hoist-acceptance-2026-07-28.md` §2
(the originating ruling), `HANDOVER-4.md` §4.4/§5.
