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

## 3. IN FLIGHT — N2b, and it is the only thing between here and N2 shipping

**N2 (risk-sized bridge trades) is code-APPROVED and ships DISABLED. It stays disabled until N2b's
acceptance 2 passes.** N2's own acceptances 1/3/4/5 + the §2 screenshot all passed; **3b FAILED** —
the risk size was placed correctly (310) and then reverted to the Amount box (10) by the first chase
reposition. That is N2b. See `review-risk-sized-bridge-trades.md`.

**N2b is code-complete at `2308122`, awaiting REVIEW + runtime.** Chain:
`spec-chase-preserve-placed-size.md` (amended twice) → `spec-back-…` (D1/D2/D3, ruled) →
`review-chase-preserve-placed-size.md` (rulings + code-side APPROVED) → commits `82005d0` ·
`9b04972` · `e2c66b9` · `5fc5b16` · report `2308122`.

**What the incoming seat must do:**

1. **Review `e2c66b9` and `5fc5b16`** — the two commits made after the code-side approval. I verified
   both diffs and they are correct (`placedOrderSizeUsd = If(sizeUsdOverride > 0D, amount, 0D)`; the
   restore seed under `placedPrice`'s single-writer rule), but the impl report amendment at
   `2308122` has NOT been reviewed.
2. **Drive runtime acceptances 2, 3, 3b, 4, 5** — spec §4. You can run these yourself on the harness;
   the owner is needed only for bridge Mode/ARM/START on leg 2.
3. Then **re-run N2's acceptance 3b** (the reduce must report 310, not 10) and N2 can be enabled.

**Traps for those runs, learned today:**
- **Acceptance 5 needs the Amount box ABOVE the clamp** (e.g. 40). At box 10 a `0.5` mult clamps back
  to 10 and the divergence vanishes — that clamp is exactly what hid this defect for weeks.
- **Do not accept a placement log line as evidence of position size.** N2's 3b failed precisely
  because `Buy limit order placed For 310` and the actual position of 10 disagreed. The reduce is
  exchange-derived and is the authority.
- **ATRSlip must be CHECKED** or the bridge refuses to START in Live mode, and neither chase-abort
  arm is evaluated.

## 4. Owner-side open

Push (19 ahead) · circuit breaker still at the `$1` test value in the owner's bin · testnet journal
rows #90–#93 / #96–#98 / #99 to delete · AWS §9 migration · the size ladder · one optional
non-blocking check: a physical owner-mouse double-click on `Mkt. BUY` (the SF2 burst instrument is
UIA-driven, so a human double-click is still unobserved).

## 5. Queue after N2b

**N2b → N2 enable → C1 emitter build** (contract §8 is the binding spec; phase-2 actionable exits
stay fenced) → backlog `ROADMAP-2026-08.md` §5.

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
