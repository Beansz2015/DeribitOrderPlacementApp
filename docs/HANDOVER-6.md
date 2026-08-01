# HANDOVER-6 — DeribitOrderPlacementApp coordinator seat (written 2026-08-01)

**Supersedes `HANDOVER-5.md` as the standing checkpoint.** H-4 §4 (invariants), §5 (runtime
bite-list) and §6 (methodology + seat rules) **remain binding and are NOT restated here** — read
them. H-5 is superseded except as history; its **§5 Fable-reserve list is DISSOLVED** (Fable is off
the table at 98% usage — nothing routes to a second model seat; items touching settled rulings
escalate to the OWNER, who is the arbiter). Memory's `era-state-checkpoint` is the live ledger.

## 1. State (verify: `git rev-parse HEAD origin/master`; never trust this text)

- **origin/master `53a2375` · HEAD `2308122`, 19 ahead, tree clean.** Owner is the only pusher.
- **Gate at HEAD: GATE PASSED, OrderCheck 173/173** (153 before N2). Execute it yourself.
- **Censuses, `frmMainPageV2.vb`-scoped** (a repo-wide grep inflates several and reads as drift):
  `emergencyFired` 10 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 · `RecordCommandedSLPrice` 3
  · `slUpdateFailures = 0` 1 · `TakerFeeRate` 0 · `isPlacingOrder` 13 ·
  `lastPlacementAdmittedUtc` 13 · `placedOrderSizeUsd` 18.

## 2. Closed this era (do not re-open; the reasoning is in the named docs)

- **N1b/N1c SL-backoff arc** — runtime-accepted, trade #99. `runtime-record-sl-backoff-2026-07-31.md`.
- **EV chase budget — CLOSED END-TO-END**, both the manual arm and the bridge-act leg
  (`runtime-record-ev-chase-budget-2026-08-01.md`). Disposition cardinality held: exactly one row
  per payload, the abort adding none.
- **Triple-placement WATCH — INVESTIGATED and CLOSED**
  (`investigation-triple-placement-2026-08-01.md`). The harness was exonerated (53/53 single
  invokes → one order); the defect was app-side. **Harness-driven placement is now permitted** on a
  TESTNET-titled, harness-launched session, through `tools/place-and-verify.ps1`.
- **SF/SF2 placement multi-order — CLOSED.** A 500 ms debounce (the Windows default double-click
  time, owner-ruled — do not re-tune without a ruling) ahead of the v1 single-flight latch.
  ⚠ **The emergency exclusion is what to protect in any future change there:** the debounce lives in
  the six handlers ONLY; `FlattenPositionAsync` and the two emergency sites reach
  `SendReduceMarketOrderAsync` directly and must stay that way.

## 2b. 🚨 THE OWNER'S x64 BIN IS STALE — nothing from 2026-08-01 is in it

Checked 2026-08-01 at the end of the session: `bin\x64\Debug\…\DeribitOrderPlacementApp.dll` is
dated **2026-07-31 23:12** and contains **none** of `RiskSizedBase` (N2), `placedOrderSizeUsd` (N2b),
`isPlacingOrder` (SF) or `lastPlacementAdmittedUtc` (SF2). Every runtime pass this era ran on the
**harness AnyCPU Debug** bin.

**So the owner is trading on pre-SF code — the placement debounce is NOT in their app** — and
ticking `Risk-size` there would do nothing, because the checkbox does not exist in that build.

The owner must `dotnet build … -p:Platform=x64`, **then re-read the window title**: the rebuild
copies `secrets.json` from project source, which is **testnet**, so a live app comes up
`— TESTNET`. (The recorded trap is the reverse direction — it once came up `— LIVE` unexpectedly —
but the mechanism is the same and it bites both ways.)

**Do not assume a runtime observation on the harness bin says anything about the owner's bin.**

## 3. N2 + N2b — BOTH CLOSED 2026-08-01. Nothing is in flight.

**N2b: all five runtime acceptances PASSED**
(`runtime-record-chase-preserve-placed-size-2026-08-01.md`). The instrument that failed N2's 3b now
gives **reduce 310, not 10**. Also passed: manual regression (10); the owner-vetoed mid-chase box
edit restored (place 10, retype 20, get 20); no stale size leaking to a later manual order; and the
**pre-existing half** — box 40 with a `0.5` mult held 20 through a reposition, the first observation
of the session-policy `size_mult` surviving a chase.

**N2 is code-APPROVED and UNBLOCKED, and ships DISABLED.** The owner enables it by ticking
`Risk-size` — *after* the x64 rebuild in §2b, without which the checkbox does not exist in their app.

Chains, if the reasoning is ever needed: `spec-risk-sized-bridge-trades.md` +
`spec-back-risk-sized-bridge-defects.md` (five defects, all upheld) + `review-…`; and
`spec-chase-preserve-placed-size.md` + its spec-back (D1 REJECTED / D2 UPHELD / D3 accepted) +
`review-…` + the runtime record.

**Three runtime traps this era paid for — reuse them:**
- **A placement log line is NOT evidence of position size.** N2's 3b failed with a correct
  `Buy limit order placed For 310` and an actual position of 10. The **reduce** is exchange-derived
  and is the authority.
- **Divergence tests need the Amount box ABOVE the min-10 clamp.** At box 10 a `0.5` mult clamps
  back to 10 and there is nothing to observe — that clamp is what hid the N2b defect for weeks.
- **ATRSlip must be CHECKED** or the bridge refuses to START in Live mode and neither chase-abort
  arm evaluates.

**Harness payload timing (cost two runs today):** with the engine stopped, freshness is
`2.5 × exec_resolution_min` and `write-payload.ps1` emits `1` — a 2.5-minute window, shorter than a
round-trip through a human pressing START, after which `[BRIDGE] auto-STOP: stale payload` fires.
Patch `exec_resolution_min` to 15 in the payload before START (~37 min) and the race disappears.
Worth a `write-payload.ps1` parameter if it recurs.

## 4. Owner-side open

**x64 rebuild + title re-check (§2b) — this gates everything else** · push (22+ ahead) · circuit
breaker is `$10` in the owner's bin, which gates the BRIDGE path and could stop a session quickly at
risk-sized notionals — set deliberately before enabling N2 · `risk_per_trade_usd` / `max_size_usd`
must be set **together**: at 25/500 a realistic $200 stop computes ~7875 and caps to 500, so every
signal gets a flat 500 rather than risk-based sizing · testnet journal rows #90–#93 / #96–#98 / #99
to delete · AWS §9 migration · the size ladder · one optional non-blocking check: a physical
owner-mouse double-click on `Mkt. BUY` (the SF2 burst instrument is UIA-driven, so a human
double-click is still unobserved).

## 5. Queue

**N2 enable (owner) → C1 emitter build** (contract §8 is the binding spec; phase-2 actionable exits
stay fenced) → backlog `ROADMAP-2026-08.md` §5. **No implementer seat is in flight.**

## 6. Methodology notes this era earned (the expensive ones)

1. **An acceptance must test the DEFECT, not the fix's theory of it.** SF v1 was a correct
   implementation of a wrong mechanism and read as convincing until the runtime pass contradicted it.
2. **Verify the change is in force before believing a runtime result** — check the running assembly
   for the new symbol. Both SF reviews did; it is what made the v1 failure trustworthy.
3. **Statement ORDER cannot be checked from a unified diff.** Added lines either side of unchanged
   context read in the wrong order. Open the file.
4. **Escalate spec defects before implementing.** Three seats did it this era and were right five,
   two and two times respectively. The one rejected item (N2b D1) was rejected because *one
   conjunct's reachability was proved and the rest of the gate assumed* — a reusable trap.
5. **Prose moves censuses.** Run them even for comment-only commits.
