# HANDOVER-6 — DeribitOrderPlacementApp coordinator seat (written 2026-08-01, audited + corrected 2026-08-02)

**Supersedes `HANDOVER-5.md` as the standing checkpoint.** H-4 §4 (invariants), §5 (runtime
bite-list) and §6 (methodology + seat rules) **remain binding and are NOT restated here** — read
them. H-5 is superseded except as history; its **§5 Fable-reserve list is DISSOLVED** (Fable is off
the table at 98% usage — nothing routes to a second model seat; items touching settled rulings
escalate to the OWNER, who is the arbiter). Memory's `era-state-checkpoint` is the live ledger.

## 1. State (verify: `git rev-parse HEAD origin/master`; never trust this text)

- **origin/master `234131a` — the owner PUSHED 2026-08-02 01:38 +0800** (reflog `update by push`),
  which took the tree from 19-ahead to level. HEAD is now this docs commit, i.e. 1+ ahead: a docs
  commit can never state its own sha, so `git rev-parse` rather than trusting the number.
- **Gate at HEAD: GATE PASSED, OrderCheck 173/173** (153 before N2). Execute it yourself.
  Every commit above the last gated one (`2308122`) is **docs-only** — verified with
  `git log --name-only 2308122..HEAD`, no `.vb`/`.vbproj` touched — so the 173/173 carries.
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

## 2b. ✅ RESOLVED 2026-08-02 — the owner's x64 bin is CURRENT (was stale; the rule survives)

**The owner rebuilt it.** Verified 2026-08-02: `bin\x64\Debug\…\DeribitOrderPlacementApp.dll` is
dated **2026-08-02 01:33** and contains **all four** era symbols — `RiskSizedBase` (N2),
`placedOrderSizeUsd` (N2b), `isPlacingOrder` (SF), `lastPlacementAdmittedUtc` (SF2). The placement
debounce **is** in the owner's app and the `Risk-size` checkbox **exists**. That bin's
`orderapp-settings.json` was rewritten at 01:35, so the rebuilt app was launched and closed
normally (`FormClosing` persistence) — the build was exercised, not merely produced.

**Environment verified from config, which is stronger than reading the title:** that bin's
`secrets.json` reads `Environment = testnet` and is byte-identical (SHA-256) to project source, so
the bin is on **testnet**. ⚠ Note the mechanism this section used to assert — "the rebuild copies
`secrets.json`" — is **not decidable from timestamps**: MSBuild `PreserveNewest` stamps the copy
with the *source's* mtime, so a copy and a skipped copy look identical afterwards. **Check the
`Environment` key, not the file date.**

**The standing rule survives its instance:** after ANY x64 rebuild, re-read `Environment` **and**
the window title before clicking Connect — it has bitten in both directions (`— LIVE` unexpectedly,
2026-07-27). And: **do not assume a runtime observation on the harness bin says anything about the
owner's bin** — separate settings file, DB and journal.

## 3. N2 + N2b — BOTH CLOSED 2026-08-01. Nothing is in flight.

**N2b: all five runtime acceptances PASSED**
(`runtime-record-chase-preserve-placed-size-2026-08-01.md`). The instrument that failed N2's 3b now
gives **reduce 310, not 10**. Also passed: manual regression (10); the owner-vetoed mid-chase box
edit restored (place 10, retype 20, get 20); no stale size leaking to a later manual order; and the
**pre-existing half** — box 40 with a `0.5` mult held 20 through a reposition, the first observation
of the session-policy `size_mult` surviving a chase.

**N2 is code-APPROVED and UNBLOCKED, and ships DISABLED.** The owner enables it by ticking
`Risk-size`, which **exists in their bin as of the 2026-08-02 rebuild** (§2b) — the rebuild
precondition this line used to carry is DONE. What is *not* done is §4's knob pairing, which is the
real precondition now.

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

## 4. Owner-side open — AUDITED 2026-08-02 against the bin, the settings file and the journal

**Four items this list carried were already DONE when it was written.** Cleared, with the evidence:

| Was listed open | Verdict | Evidence |
|---|---|---|
| x64 rebuild + title re-check | **DONE** | dll 2026-08-02 01:33, four symbols present, `Environment = testnet` (§2b) |
| push (22+ ahead) | **DONE** | reflog `update by push` 2026-08-02 01:38 +0800; 0 ahead at audit |
| testnet journal rows #90–#93 / #96–#98 / #99 | **DONE** | queried the x64 `trades.db`: **none** survive |
| EV bridge-act leg *(carried in memory, not here)* | **DONE** | closed end-to-end 2026-08-01; §2 already had this right |

**Still genuinely open:**

- **`risk_per_trade_usd` / `max_size_usd` = 25 / 500 — unchanged, and this is the live footgun.**
  They must be set **together**: at 25/500 a realistic $200 stop computes ~7875 and caps to 500, so
  every signal gets a flat 500 rather than risk-based sizing. **N2 would be "on" and doing nothing
  risk-shaped.** Fix this before ticking `Risk-size`, not after.
- **Circuit breaker `$10`** (verified in the bin) — gates the BRIDGE path and could stop a session
  quickly at risk-sized notionals. Set deliberately before enabling N2.
- **Session policy is ENABLED in the owner's bin** (`session_policy.enabled = True`;
  `LONDON = MEDIUM | CONFIRMED | 0.5`, `ASIA = HIGH,MEDIUM | any | 0.75`). Not an open item — but
  material to N2, because that multiplier applies **on top of** the risk size, exactly once. With
  N2 on, a LONDON signal is half the computed size and an ASIA signal three-quarters. Factor it in
  when setting the knobs above, or the delivered size will surprise.
- **Two journal rows the deletion list never named: `#95` and `#100`.** The x64 journal holds **91
  live rows**; after `#89` only these two remain. `#95` = 2026-07-27 19:33 UTC (TakeLimitProfit
  long, the 07-27/28 emergency-hoist session, adjacent to the deleted #96–#98); `#100` =
  2026-07-31 15:24 UTC (StopLossOrder long 62490 → 62475.5) — **later than** N1c's trade #99 and
  recorded in no doc. Both are testnet-era. Whether they go is the owner's call, not a defect.
- AWS §9 migration · the size ladder.
- Optional, non-blocking: a physical owner-mouse double-click on `Mkt. BUY` (the SF2 burst
  instrument is UIA-driven, so a human double-click is still unobserved).

## 5. Queue — re-verified 2026-08-02

**N2 enable (owner) → C1 emitter build** (contract §8 is the binding spec; phase-2 actionable exits
stay fenced) → backlog `ROADMAP-2026-08.md` §5. **No implementer seat is in flight.**

Verified rather than assumed: `risk_size_bridge_trades = False` in the owner's bin, so **N2 is still
off** and the queue head is real — but it is now *actionable*, which it was not while §2b stood.
C1 has **not** started: only `proposal-c1-v2-feedback-file.md` + `ack-…` exist, no spec or impl
report. `ROADMAP-2026-08.md` §5's backlog is intact and unscheduled — spot-checked
`set-textbox -Exact`, still absent from `tools/set-textbox.ps1`, so that row is correctly open.

**Three stale-open claims elsewhere were corrected in the same pass** (they read as work owed and
were not): ROADMAP §3's EV row said the fee-comms repoint's *"coordinator review is all that
remains"* — it closed 2026-07-30 in `fd2604e`, appended to `spec-fee-comms-repoint.md` rather than
a standalone `review-…md`, which is exactly why it kept reading as open; ROADMAP §5's WATCH
paragraph still warned the LIVE multi-order exposure was open "until `spec-placement-single-flight.md`
lands"; and §7's amended sequence still listed SF and EV as pending.

⚠ **Method note for the next audit — a doc is not evidence about a doc.** Every clear above came
from the artefact (reflog, dll symbols, `trades.db`, the settings JSON, the tools script), never
from another `.md` asserting it. Two of the four had *already* been closed and re-copied forward as
open across a handover boundary, which is how they survived.

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
