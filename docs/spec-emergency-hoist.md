# Spec — N1: emergency-check hoist above the SL-edit throttle (single-fire latch)

**Origin:** ROADMAP-2026-08 §3 N1; the documented trade-off (housekeeping item 16's comment at the
throttle gate): the M.SL emergency-threshold check lives inside the `MinStopLossUpdateInterval` /
`BackoffStopLossRetry`-throttled triggered-SL block, so a persistently failing SL edit also delays
emergency market-stop detection by up to ~5 s. Noise at size 10; the one latent safety-latency
item at normal size. **This spec must land BEFORE the size ladder tops out.**

**Recommended implementer:** **Opus HIGH, fresh conversation, this spec alone** — it sits on the
hottest safety path in the app. NOT bundled with anything.
**Target:** `frmMainPageV2.vb` (`HandleQuoteUpdates` triggered-SL region + `CompletePositionClose`
+ the placement seeds). **Anchors by symbol — re-verify every site against HEAD before editing**
(line numbers in older docs have drifted by thousands).
**Ground rules:** standing — gate per commit (GATE PASSED), local commits, never push,
receive-thread rules, impl report. **Read first:** `HANDOVER-3.md` §4 invariants 1–2 (the
emergency-baseline latch + single-send-point), `spec-emergency-baseline-fix.md` (the frozen
anchor), `review-consolidated-stack-2026-07-08.md` (the hybrid latch review).

## The change, precisely

**Today:** inside the triggered-SL branch of `HandleQuoteUpdates`, the emergency check
(price beyond `emergencyBaseline` ∓ `M.SL` value ⇒ market-close via `SendReduceMarketOrderAsync`)
is evaluated only when the throttled SL-edit block runs. **After:** the THRESHOLD CHECK is
evaluated on every qualifying quote tick, before/outside the throttle; the SL-edit machinery stays
throttled exactly as today.

## The single-fire latch (the load-bearing piece — get this exactly right)

New engine field `emergencyFired As Boolean = False` (quote-thread owned, same thread as every
reader — plain field, no marshalling).

- **Set:** immediately BEFORE dispatching the emergency market-stop (both fire paths if two still
  exist — re-verify; the #68/#71 era had quote + send variants). Set-then-send, so a re-entrant
  quote tick during the await can never double-fire.
- **Cleared at exactly THREE places** (was written "TWO"; **corrected 2026-07-27 by ruling D1** —
  (2) below is plural and resolves to two sites): (1) `CompletePositionClose` (a new position may need its own
  emergency); (2) the placement seeds where a fresh order re-establishes a clean context (the
  `cancelPending = False` "fresh order" sites — put the latch reset BESIDE them, same commit
  discipline as every paired reset).
- **NOT cleared** anywhere else — in particular NOT in the SL-edit paths, NOT in the cancel
  teardowns (a mid-cancel emergency latch must survive the cancel window), and it is **NOT an 8th
  SL-context reset site** (it zeroes no price, no commanded set, no baseline).

## Gating conditions the hoisted check MUST preserve (each existed for a reason)

1. **Measure from the FROZEN `emergencyBaseline`** (the settled loss-cap anchor), never
   `placedStopLossPrice` (the chase reference). `emergencyBaseline = 0` ⇒ no check (unarmed).
2. **`SLTriggered` context only** — the emergency is the triggered-SL loss cap, not a general stop.
3. **M.SL checkbox + value** gating exactly as today (`chkMSL`/value mirror — re-verify symbols).
4. **The `IsCancelPending()` interaction:** the 2026-07-08 owner decision LEFT the emergency gated
   during the ≤4 s cancel window (arguably-correct: don't market-close while a cancel is settling).
   **Preserve that behaviour** in the hoisted check — hoisting changes WHEN we look, not WHAT we
   respect. If the implementer believes the hoist makes ungating now correct, that is an OWNER
   question raised before commit, not a deviation.
5. **Rate-limiter guard** on the fire path as today (`CanMakeRequest`) — but note in the report:
   if the limiter blocks an emergency fire, the latch must NOT be set (the fire didn't happen;
   next tick retries). Set-the-latch only when the send is actually dispatched.
   > **CORRECTED 2026-07-27 by coordinator ruling D2** (`docs/review-emergency-hoist.md`): the
   > premise is false — **no limiter has ever guarded this fire path**. The emergency branch of
   > `UpdateStopLossForTriggeredStopLossOrder` returns before that function's limiter section, and
   > neither `CancelOrderAsync` nor `SendReduceMarketOrderAsync` consults `rateLimiter`. **None is
   > wanted:** rate-limiting the loss cap is the wrong direction. Only the derived requirement
   > stands — set the latch only where a send is genuinely dispatched — and it is met. Do not
   > re-raise this as a missing guard.

## What must be byte-identical

The SL-edit/chase machinery and its throttle; `UpdateStopLossForTriggeredStopLossOrder` (the
single send point) and the commanded-set recording; all 7 paired reset sites; the emergency
DOLLAR semantics (anchor ∓ M.SL, same comparison direction per side — copy the existing
comparison verbatim into the hoisted site, do not re-derive it).

## Acceptance

1. Gate per commit; greps: `emergencyFired` = the declaration + set-at-fire + **exactly 3 clears**;
   `ResetCommandedSLPrices()` count unchanged (8); no new `emergencyBaseline = 0` sites.
   > **AMENDED 2026-07-27 by coordinator ruling D1** (`docs/review-emergency-hoist.md`): this line
   > originally said "exactly 2 clears", which was a miscount of this spec's own §latch body — that
   > says `CompletePositionClose` plus "the placement **seeds**", plural, and there are two
   > (`ExecuteOrderAsync`, `StopLossForTrailingOrderAsync`). Clearing only one would leave a
   > trailing-bracket placement running cap-less after an emergency that never reached
   > `CompletePositionClose`. The implemented census is **1 declaration + 2 sets + 3 clears +
   > 3 reads** (the third read at the hoisted gate is ruling D3).
2. **Testnet (isolated-harness protocol, engine untouched):** enter a position, trigger the SL,
   force a persistent SL-edit failure (the historical repro: an edit that keeps rejecting), then
   push price beyond the cap ⇒ the emergency market-stop fires **without waiting for the throttle
   window**, exactly once (log shows one fire, one reduce). Re-arm on a fresh position works.
3. **Normal sessions byte-identical:** with edits succeeding, behaviour is unchanged (the check
   passing is invisible; the throttled block still does all edits).
4. Impl report: before→after of the check's location, the latch's sites quoted (six, per D1/D3), the gating
   conditions table above ticked one by one against code.
