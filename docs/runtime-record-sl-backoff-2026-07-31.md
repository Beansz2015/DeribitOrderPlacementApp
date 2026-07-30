# Runtime record — SL-backoff confirmed reset (N1c acceptance 3), 2026-07-31

**Owner-driven, TESTNET, owner-mouse-only.** Trade #99, SHORT 10 @ $64792.02. Coordinator
(Opus seat) verified the log against the code at HEAD before writing this record.

## VERDICT: **PASS on every named observable. N1c acceptance 3 is CLOSED, and with it the whole
N1b/N1c SL-backoff arc.**

## Setup (screenshot-confirmed, window title `— TESTNET`)

Amount 10 · Trig. P 5 · S. Loss 1 · **M. SL 30, checked** · ATRSlip 0.6 **unchecked** (so no
ATR-abort interference) · T.Prof 400 · Comms 23. Recipe per HANDOVER-5 §2.1: let the SL trigger and
the chase run, then **cancel the SL on the Deribit web UI** so the chase edits a dead order.

## The log

```
Position entered: SHORT 10 @ $64792.02
Triggered SL placed @ $64798
SL reposition sent: $64798.00 → $64809.00
SL reposition sent: $64809.00 → $64812.50
SL reposition sent: $64812.50 → $64814.50
API ERROR (id 223350 order edit): code 11044 - not_open_order
SL-edit failure #1 - backoff 0.7s
SL reposition sent: $64814.50 → $64826.50
API ERROR (id 223350 order edit): code 11044 - not_open_order
SL-edit failure #2 - backoff 1.3s
SL reposition sent: $64826.50 → $64832.00
API ERROR (id 223350 order edit): code 11044 - not_open_order
SL-edit failure #3 - backoff 2.7s
SL update rate limited: 2.4s remaining
SL update rate limited: 1.8s remaining
SL update rate limited: 1.8s remaining
Cancelled all open orders
Reduce-only MARKET buy 10  order sent.
Emergency Buy Market Order Executed.
Position reduced at 64839.49 (market order).
Loss of: $0.01.
```

## 1. The backoff ESCALATES — the observable N1c exists to produce

**0.7 → 1.3 → 2.7 s** = 666 → 1332 → 2664 ms, the fixture-pinned `333 × 2^n` ladder printed to 1 dp.

This is the discriminating evidence, and it is unambiguous. **Pre-N1c this sequence was
impossible.** The old reset sat in the chase's `Try`, which the swallowing send site made
unconditional, so a *rejected* edit reset the counter too: it oscillated 0↔1 and every one of these
lines would have read `#1 - backoff 0.7s`, forever. Three consecutive failures with a doubling
delay prove the reset no longer fires on rejection — exactly the move N1c commit 1 made.

Note also that the `SL reposition sent:` line precedes each error rather than claiming success —
commit 2's log-honesty reword doing its job on real rejections.

## 2. `SL update rate limited` printed — the instrument that was unreachable for two specs

That line is gated `remainingMs > 1000`, which needs `backoffMs > 1000`, which needs the failure
counter at **2**. It had never printed in the app's history. This is the observable that
N1b acceptance 2 named and could not reach — the defect that produced
`spec-back-sl-backoff-escalation-defect.md` and then N1c.

Worth recording precisely: **it printed with no pump.** The corrected N1b acceptance 2 needed
manual SL clicks (223346) or the trailing loop (223348) to push the counter above 1. Here the
**triggered-SL chase reached counter 2 on its own**, which was the entire point of moving the reset
to a confirmed echo. The corrected pump recipe is now moot — superseded by this run rather than
left outstanding.

## 3. The M.SL cap fired EXACTLY once, and fired on threshold — not after the backoff

One `Emergency Buy Market Order Executed.` line. The N1 single-fire latch held.

**The arithmetic, derived from code rather than assumed** ([frmMainPageV2.vb:2411](../DeribitOrderPlacementApp/frmMainPageV2.vb:2411)):
`emergencyBaseline` is seeded at the trigger flip but deliberately left **un-settled**
([:3005](../DeribitOrderPlacementApp/frmMainPageV2.vb:3005)) so it re-latches onto the actual
top-of-book SL at the **first post-trigger reposition**, then freezes (the owner-#67 hybrid fix —
anchoring on the pre-settle flip price fired the cap too early).

- First post-trigger reposition ⇒ **anchor = 64809.00** (not the 64798 flip price)
- SHORT ⇒ fires when `bestBid - anchor >= M.SL` ⇒ **bestBid >= 64809 + 30 = 64839.00**
- Fill **64839.49** on a reduce-only market BUY, which lifts the **ask**; the screenshot spread is
  0.50 (64853.00 / 64853.50)

**Gap = 0.49 ≈ exactly one spread.** The cap fired on the very tick the threshold was crossed.

That is the strongest single result in this run. The cap fired roughly **1 s into a 2.7 s backoff
window** (the three rate-limited lines count down 2.4 → 1.8 → 1.8 of that window, then the emergency
block runs). Had the cap been throttle-coupled it would have waited ~1.7 s longer, and at the price
velocity this run actually showed, the close would have landed materially worse. The N1 hoist is
confirmed load-bearing under a *reachable* backoff for the first time — which is precisely what
N1b/N1c made possible and what the old "~5 s" framing wrongly claimed was already happening.

Structurally this is guaranteed, not lucky: the emergency check at
[:2331](../DeribitOrderPlacementApp/frmMainPageV2.vb:2331) sits above the throttle gate at
[:2352](../DeribitOrderPlacementApp/frmMainPageV2.vb:2352), and the rate-limited print is in that
gate's else-arm at [:2452](../DeribitOrderPlacementApp/frmMainPageV2.vb:2452).

## 4. What this run does NOT prove (recorded, not hand-waved)

- **The 5.0 s cap step was never reached.** The ladder stops at #3 because the M.SL cap intervened
  before a fourth failure. Steps 4+ and the saturation-at-cap behaviour remain **gate-fixture
  evidence only** (`SL backoff: n=3 -> 4 failures, capped at 5000 ms` and the two lines after it).
  This is not a gap worth chasing with another trade — the cap firing first is the *correct* system
  behaviour, and arranging a 4th failure would mean disabling the loss cap to observe a delay.
- **Neither accepted residual was exercised**: no lost/unrecognised echo (un-reset), no lagging
  older echo over-resetting a newer rejection. Both remain accepted-by-argument, bounded and
  self-healing on the next confirmed edit.
- The confirmed-reset *healing* path (a successful edit clearing the counter back to 666 ms) was not
  observed here — the order stayed dead for the rest of the position, so no edit was ever confirmed.
  Fixture-covered (`a confirmed echo clears the counter`).

## 5. Open after this record

- **Owner: delete testnet row #99** from the journal, along with the standing #90–#93 / #96–#98.
- Cosmetic, not filed as a defect: `Reduce-only MARKET buy 10  order sent.` has a double space.
- **The N1b/N1c arc is closed.** Remaining owner-runtime item across the whole queue is the
  **EV §6.3 pass**. Implementation queue: N2 → C1 emitter.
