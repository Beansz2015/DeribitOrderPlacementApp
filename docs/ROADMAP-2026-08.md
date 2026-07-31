# ROADMAP — August 2026 era (written 2026-07-24, coordinator seat, owner-reviewed)

**Context at writing:** the system is COMPLETE and LIVE — contract v1 frozen, log-only soak passed
(clean column join), interlock ladder + live-at-min-size executed and CSV-verified, session policy
shipped (enable = Phase 3, imminent), cutover checklist written incl. the §9 AWS London
co-location plan, all §8 rulings implemented. Fable coordinator window ends ~Aug 2 → **Opus-high
seat inherits this doc as the plan of record** (with `HANDOVER-3.md`'s successor).

**Standing constraints (unchanged, binding):** owner-rejected list stays rejected (session-end
auto-flattening; app-side re-gating of engine logic; P2 SL policy; P3 hybrid chase; the emergency
clamp; auto-arm-on-boot). R1: the engine decides signal logic — the app never grows a second
brain. One implementer conversation at a time; reviews execute the gate; owner is the only pusher.

## §1 — Owner track (no code): the path already in motion

1. Phase 3: enable the session policy, P5 conservative values (London/Asia session).
2. Size ladder (checklist §3) at FIXED size: 10 → 20–30 → normal.
3. AWS London migration (checklist §9; key choreography kills the two-executor window).
4. Rulings as they arise; matrix/policy revisions on the engine seat's cadence (evidence
   discipline: revise at regenerations, not week-to-week).

## §2 — Pre-ladder quick wins (the low-hanging batch; spec + implement NOW)

| # | Item | Size | Notes |
|---|---|---|---|
| Q1 | **Remote alerting** — fire-and-forget notifier (Telegram webhook or SMTP; owner picks transport) on the significant class: `acted` / `rejected:` / auto-STOP / breaker trip / emergency market-stop. Never on the receive path (Task.Run, fail-silent, self-rate-limited); token/credentials in `secrets.json`. | S–M | The AWS topology makes sound/taskbar alerts worthless off-RDP. **Blocking input: owner chooses transport + provides the token/address.** |
| Q2 | **Phase B item C — signal columns in the trade DB** (`SignalId`, `SignalConfidence` on bridge-placed trades). The DB migration pattern + fixtures exist (ergonomics item C precedent). Makes per-signal P/L attribution a query and joins the book to the engine matrix trivially. | S | Well-templated; acted path touched lightly. |
| Q3 | **Ops tooling (no app code, no gate risk):** (a) scheduled backup task — settings/trade DB/bridge-state/disposition log to S3 or pull-down (checklist §9 note); (b) policy-report script over the disposition log (per-session/tier counts of `refused: policy` vs `would-act`) for the evidence reviews. | S | Coordinator/any seat; scripts in `tools/`. |
| Q4 | *(optional)* **Phase B item F — USE-ENGINE-LEVELS button** (manual trade borrows the latest payload's levels). Modest value; the tie-in shape is now stable so the old "needs a fresh look" caveat is cleared. | S | Take it if Q1/Q2 leave room in the same implementer pass. |

## §3 — Pre-normal-size (spec now, implement with care — NOT rushed)

| # | Item | Notes |
|---|---|---|
| N1 | **DONE 2026-07-28** — implemented, reviewed (4 asks ruled), owner runtime-accepted (#96–#98). *Framing corrected post-acceptance:* the "~5 s" delay was never reachable (the backoff was dead code); the real pre-N1 exposure was ≤ 333 ms **plus a latent double-fire race, which N1 closed** — the more valuable half. |
| EV | **IMPLEMENTED + REVIEWED 2026-07-28** (jumped the queue for the Aug-1 fee change; `spec-ev-chase-budget.md`, review `review-ev-chase-budget.md` — all five asks ruled, no code change). Ships OFF (`min_net_move_pct = 0`). Remaining: owner §6.3 runtime pass (x64 rebuild + TESTNET title check first) + the Tooling-row visual check. Spawned `spec-fee-comms-repoint.md` — **IMPLEMENTED 2026-07-30 (`60b95d6`, gate 146/146, `impl-report-fee-comms-repoint.md`)**: the comms default now derives from the persisted `taker_fee_bps` (3.5 bps ≈ $22 at a 64k index, was ≈ $32 on the retired 2024 constant), and the 2024 rate is gone from the code. **Owner runtime acceptance PASSED same day** (TESTNET, index 63678.54 → comms 22; the shift accepted, offsets untouched) — **all three acceptances met, coordinator review is all that remains.** |
| N1b | **DONE 2026-07-30 — implemented, reviewed, CLOSED code-side** (`spec-sl-backoff-coupling.md`, the §2 ruling: option (a); review `review-sl-backoff-coupling.md`). Genuine red SL-edit failures (ids **223346/223348/223350** only, benign races excluded) now reach `BackoffStopLossRetry` via `HandleUnhandledJsonRpcError` — the backoff's first reachable trigger ever. Closing commit = the `NextSlBackoff` extraction + 16 OrderCheck fixtures. **Its acceptance-2 defect spawned N1c below.** |
| N1c | **DONE + CLOSED 2026-07-31 — implemented, reviewed, and runtime-accepted** (`spec-sl-backoff-confirmed-reset.md`, `impl-report-sl-backoff-confirmed-reset.md`, gate **153/153**; acceptance 3 record = `runtime-record-sl-backoff-2026-07-31.md`, trade #99: backoff climbed 0.7→1.3→2.7 s, `SL update rate limited` printed with no pump, M.SL cap fired once and on threshold — one spread of slippage, ~1 s into a 2.7 s backoff window. **The N1b/N1c arc is closed.**) The chase's failure-counter reset ran on send-completion, which the swallowing send site reaches for *rejected* edits too — the counter oscillated 0↔1 and the backoff pinned at 666 ms, so N1b delivered the damper but never the escalation. The reset now runs on the exchange's **confirmation** of one of our own SL edits (the reconcile-4a commanded-price echo; only id 223350 feeds it). Commit 2 = log honesty (`SL reposition sent:`) + a gray `SL-edit failure #N - backoff X.Xs` diagnostic at the coupling site. `lastStopLossUpdate` and `placedStopLossPrice` stay optimistic, as ruled. Nothing remains. |
| **SF2** | **🚨 Placement debounce — `spec-placement-single-flight-v2.md`, OWNER RULING NEEDED on the window (§2; coordinator recommends 500 ms), then a fresh Opus-HIGH seat.** SF v1 shipped a single-flight latch and **failed acceptance 2 with the defect intact** — 3 actuations still gave 3 entries and a reduce of 30, with the latch verified present in the running binary. The latch proves its own irrelevance: had any two invocations overlapped one would have been blocked, so **none overlapped** — the message pump dispatches queued clicks sequentially. It is a RATE problem, not an OVERLAP problem, so the fix is a time-based debounce (keep the latch, add the window). **The LIVE exposure is STILL OPEN.** Review: `review-placement-single-flight.md`. |
| **SF** | **Placement single-flight — v1, SUPERSEDED by SF2 but the code is KEPT** (acceptances 1/3/4 passed, 2 failed). Originally owner-ticked 2026-08-01, ahead of N2** (`spec-placement-single-flight.md`; origin `investigation-triple-placement-2026-08-01.md`). The six `Async Sub` placement handlers leave their button enabled across the await with no re-entrancy guard — `ButtonDisabler`/`ButtonEnabler` were written for it and are never called — so N actuations give N concurrent orders at one cached price. Reproduced: 3 invokes in 33 ms ⇒ 3 entries ⇒ reduce of 30. **This is live exposure on LIVE (a double-click places two orders), which is why it jumps N2.** One shared `Interlocked` latch across all six, released in a `Finally`; the reviewer's real question is leak-freedom, not the latch. Opus HIGH, fresh seat, ships ON (defect fix, no knob). |
| N2 | **Risk-sized bridge trades** — per-signal size = `risk_per_trade_usd ÷ engine stop distance`, capped `max_size_usd`, × sessionFactor (the session-policy mult folds in — ONE formula, as its spec anticipated), floored to the contract step. The principled endpoint of the size ladder; spec written (`spec-risk-sized-bridge-trades.md`), ships DISABLED. |

## §4 — The next era's centerpiece

| # | Item | Notes |
|---|---|---|
| C1 | **v2 feedback file — SPECIFIED 2026-07-28, trader-ticked T1–T8; contract §8 amended (coordinated pass).** Exchange closed (proposal → engine reply ACCEPTED → ack → tick). Remaining = the two implementation builds, each on its own queue: order-app emitter (own Opus-HIGH pass, ships OFF, after N2 unless reordered) and engine consumption (behind its net-EV rider). Phase 2 (ACTIONABLE EXITS — the largest remaining P/L lever) stays fenced as a future signal-schema-v2 amendment, gated on the phase-1 display soak. Right-sized as the Opus era's flagship. |

## §5 — Hygiene backlog (anytime, unscheduled, all on record)

**Triple-placement WATCH — INVESTIGATED + CLOSED 2026-08-01** (`investigation-triple-placement-2026-08-01.md`). The old protocol (trade-placing steps = OWNER mouse only, harness drives everything else) rested on a 5-vs-1 tally that "leans harness-side, unproven". **The lean was wrong.** 53/53 single harness `Invoke`s placed exactly one order (95% upper bound on a spurious multi-invoke ~5.7%); the real defect is app-side — `ButtonDisabler`/`ButtonEnabler` are never called, so the six `Async Sub` placement handlers leave their button enabled across the await with no single-flight guard, and N actuations from ANY source give N concurrent orders. Reproduced exactly: 3 `Invoke`s in 33 ms ⇒ 3 entries at one price ⇒ reduce of 30. **⚠ This exposure is live on LIVE — a double-click places two orders — until `spec-placement-single-flight.md` lands (Opus HIGH, owner tick pending).** AMENDED PROTOCOL: harness-driven placement is ALLOWED on a TESTNET-titled, harness-launched session, but MUST go through `tools/place-and-verify.ps1` (asserts the log effect of every placement; dumps log + UIA tree on mismatch) rather than calling `click-PLACES-ORDER.ps1` directly. Owner-mouse-only no longer applies · `set-textbox -Exact` switch (the substring trap silently set the wrong box and reported success) · FrmIndicators full retirement (more attractive now — with the engine 24/7 on AWS the indicator
fallback is nearly never exercised; retiring it = payload ATR + $70 constant only) · Edit T.S.
post-trigger rework (4-trap catalog; `EditStopLossTo` is the home) · item G stale-SL-box cosmetic
(mechanism documented in `runtime-record-live-ladder-2026-07-23.md`) · housekeeping smoke
leftovers (8b checkbox-OFF half, 14g long-PnL observation) · A2 manual-freeze observation · B2
regression · F1 rejected-edit re-sync (accepted LOW) · TP post-fill manual-move gap · harness
txtTrigger tree-order nit · fractional-offset loud rejection.

## §6 — Deliberately NOT building

App-side exit logic (partial TP / trailing TP / scale-outs) — v2 (C1) is the correct home with
the engine deciding; building it app-side forks the brain R1 forbids. Multi-instrument — the
engine's scope call, not this app's. Anything on the owner-rejected list.

## §7 — Sequence of record

Phase 3 → **Q1+Q2(+Q4) one implementer pass + Q3 tooling** → size ladder at fixed size →
N1 (hoist, own pass) → AWS migration (§9 choreography) → normal size → **N2 risk-sized bridge
trades** → **C1 v2 feedback file**. Handover: Aug 1–2 — HANDOVER-3 successor + memory
consolidation; this doc rides along as the plan of record.

**Amended 2026-08-01:** N1b/N1c closed; the queue is now **SF placement single-flight (owner-ticked,
jumps the queue as live LIVE exposure) → EV §6.3 owner runtime pass → N2 → C1 emitter.**
