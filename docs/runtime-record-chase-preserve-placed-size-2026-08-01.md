# Runtime record — N2b acceptances 2/3/3b/4/5, 2026-08-01

**All five PASS. N2b is CLOSED, and with it the defect that failed N2's acceptance 3b.**

Driven by the coordinator on the harness (TESTNET, AnyCPU Debug bin, trades #70–#74 in the harness
DB — the owner's x64 journal untouched). The owner drove bridge Mode/ARM/START; everything else was
harness-driven, permitted by the amended WATCH protocol. Isolated scratch payload path throughout,
so the engine's real `verdict_signal.json` was never touched — verified present afterwards.

**In force check first**, against the standing stale-binary trap: the running assembly contains
`placedOrderSizeUsd`. Gate at HEAD: **GATE PASSED, OrderCheck 173/173.**

## Results

| # | What it tests | Setup | Result |
|---|---|---|---|
| **2** | **THE acceptance** — the risk size survives the chase | bridge Live, N2 ON, policy OFF, box **10**, risk 1, payload `entry 63000 / stop 62800` ⇒ computed **310** | placed 310 → `Order repositioned` → position 310 → **reduce 310** ✅ |
| **3** | manual regression | N2 OFF, manual Limit BUY, box **10** | placed 10 → chased → **reduce 10** ✅ |
| **3b** | the owner-vetoed control, restored | manual Limit BUY at box 10, **retype 20 while it rests** | `placed For 10` → `Order repositioned` → **position 20** → **reduce 20** ✅ |
| **4** | no stale size leaks to a manual order | manual placement immediately after the 310 act | placed **10** → position 10 → **reduce 10** ✅ |
| **5** | the PRE-EXISTING half — session-policy `size_mult` | N2 **OFF**, policy `0.5`, box **40** (above the clamp) | `placed For 20` → `Order repositioned` → position 20 → **reduce 20** ✅ |

Every verdict is taken from the **reduce**, which is exchange-derived (`positionSizeUSD`, the
position model). The N2 review established why: its 3b failed with
`Buy limit order placed For 310` and an actual position of 10 — the placement log line was correct
and still not evidence.

## The before/after that closes the arc

Same instrument, same payload, same box:

| | N2 alone | N2 + N2b |
|---|---|---|
| placed | 310 | 310 |
| chased | yes | yes |
| **reduce** | **10** ✗ | **310** ✅ |

## Acceptance 5 is the one worth reading twice

It is not about N2 at all — the N2 checkbox was **off**. Box 40 with a `0.5` session mult held **20**
through a reposition. Before N2b the chase would have restored 40, which means the session-policy
`size_mult` has been silently reverted on every chased bridge entry since that feature shipped.
It was invisible only because everything ran at live-at-min-size, where the box is 10 and
`EffectiveSizeUsd` clamps every reduction back to 10, so no divergence existed to revert. **This run
is the first observation of that half of the defect being fixed** — and the box had to be set above
the clamp to see it at all.

## Two things confirmed in passing

- **The D1 ruling is empirically right.** Acceptance 3 logged `TP re-anchored to fill …` — the
  re-anchor path *does* run on a manual entry with no manual TP, and it is harmless there because
  the position size IS the Amount box. It never fired on any of the three bridge acts, exactly as
  the `manualTPval <= 0D` gate requires.
- **§3's `Position entered:` line now reports the fill.** It read `LONG 20` in 3b (where the box was
  10 at placement and 20 at fill) and `LONG 310` in acceptance 2 — in both cases the size that
  actually entered, which the old `orderAmountVal` mirror could not have produced.

## Harness lesson — fold into the runtime facts

**The documented "write → START → write" protocol has a timing hole with the engine stopped.**
Payload freshness is `2.5 × exec_resolution_min`, and `write-payload.ps1` emits
`exec_resolution_min = 1` — a **2.5-minute** window, shorter than a round-trip through a human
pressing START. Two runs died to `START refused: latest payload is stale` and then
`[BRIDGE] auto-STOP: stale payload`, which is the documented "the staleness tick can only stop"
behaviour arriving between the click and the payload.

**Fix used, and the one to reuse:** write the payload, then patch `exec_resolution_min` to 15 before
START — freshness becomes ~37 minutes and the race disappears. Worth adding as a
`write-payload.ps1` parameter if this recurs.

## Teardown

Harness bin restored to baseline (box 10, cooloff 5, risk 25, N2 flag off, policy off, ATRSlip off),
scratch `bridge.json` and payload directory deleted, app stopped. Engine payload untouched; the
engine stayed stopped throughout per the blunt-gate ruling.
