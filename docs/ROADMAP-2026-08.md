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
| EV | **IMPLEMENTED + REVIEWED 2026-07-28** (jumped the queue for the Aug-1 fee change; `spec-ev-chase-budget.md`, review `review-ev-chase-budget.md` — all five asks ruled, no code change). Ships OFF (`min_net_move_pct = 0`). Remaining: owner §6.3 runtime pass (x64 rebuild + TESTNET title check first) + the Tooling-row visual check. Spawned `spec-fee-comms-repoint.md` (**APPROVED for implementation 2026-07-28**, post-N1b: the 2024 `TakerFeeRate` constant still drives the comms default — ~43% overstated after Aug-1). |
| N1b | **SL-backoff coupling** (`spec-sl-backoff-coupling.md`, the §2 ruling: option (a)) — couple genuine SL-edit failures (223344–223350 red class ONLY, benign races excluded) into `BackoffStopLossRetry` via `HandleUnhandledJsonRpcError`. Before normal size, after the EV chase budget. Opus HIGH, own pass. |
| N2 | **Risk-sized bridge trades** — per-signal size = `risk_per_trade_usd ÷ engine stop distance`, capped `max_size_usd`, × sessionFactor (the session-policy mult folds in — ONE formula, as its spec anticipated), floored to the contract step. The principled endpoint of the size ladder; spec written (`spec-risk-sized-bridge-trades.md`), ships DISABLED. |

## §4 — The next era's centerpiece

| # | Item | Notes |
|---|---|---|
| C1 | **v2 feedback file — SPECIFIED 2026-07-28, trader-ticked T1–T8; contract §8 amended (coordinated pass).** Exchange closed (proposal → engine reply ACCEPTED → ack → tick). Remaining = the two implementation builds, each on its own queue: order-app emitter (own Opus-HIGH pass, ships OFF, after N2 unless reordered) and engine consumption (behind its net-EV rider). Phase 2 (ACTIONABLE EXITS — the largest remaining P/L lever) stays fenced as a future signal-schema-v2 amendment, gated on the phase-1 display soak. Right-sized as the Opus era's flagship. |

## §5 — Hygiene backlog (anytime, unscheduled, all on record)

**Triple-placement WATCH** (N1 spec-back §3: one harness UIA `Invoke` of Mkt. BUY produced 3 entries; 5-vs-1 tally leans harness-side, unproven — protocol until investigated: trade-placing steps in runtime tests use OWNER mouse clicks, harness drives everything else; if it reproduces, capture logs+tree immediately — that session becomes the investigation) · `set-textbox -Exact` switch (the substring trap silently set the wrong box and reported success) · FrmIndicators full retirement (more attractive now — with the engine 24/7 on AWS the indicator
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
