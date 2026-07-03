# Signal Bridge — order-app reply to the VerdictEngine brief

**From:** the DeribitOrderPlacementApp side (coordinator seat). **Re:** `DeribitVerdictEngine/docs/signal-bridge-orchestrator-brief.md` (2026-07-03). Self-contained; field-level diff as requested. After you ack this, we freeze **schema v1** into `DeribitOrderPlacementApp/docs/integration-contract-verdictengine.md` (canonical on the order-app side, since it's the executing party; your repo references it by path) and only then does either side implement.

## Accepted as-is

- **Transport.** Atomic single-file overwrite + FSW/debounce/retry. We concede our named-pipe preference: at this cadence, for one-way flow, your design is better (inspectable, replayable, restart-proof), and the v2 feedback file gives the ack loop a pipe would have provided. One mechanism, both directions, is the right end state.
- **All five fixed semantic rules:** `direction: NONE` on NO TRADE leans; effective-levels-as-placed-values; SKIPPED-means-stand-down; de-dupe on signal identity; tier-based action mapping (never raw scores).
- **R1, with one distinction made explicit (trader-confirmed 2026-07-03):** we retire our internal EMA/VWAP/DMI score with FrmIndicators — no *signal* re-gating of your verdicts, ever. What we retain and will always enforce are **operational gates**: arming interlock (below), connection state, rate-limit readiness, flat/no-working-entry, cooloff, circuit breaker, session time window. These can refuse an entry your verdict proposed; they never overrule your signal *logic*.
- **R2.** Your final effective stop/target are what we place. Our ATR distance math becomes a default-OFF checkbox override that may recompute *distances only*; both level sets logged per trade when it's ON.
- Keeping `scores`, `kelly`, `structural`, `hold_status` as informational fields — good for our trade log; we will not act on them in v1 (`kelly` explicitly not used for sizing).

## Field-level diff (adds / clarifications; no removals, no renames)

1. **ADD `engine.instance_id`** (string; stable per engine process — GUID or process-start ticks). Your `signal_id` is "monotonic **per engine process**": an engine restart resets it, which breaks any monotonic de-dupe and can make fresh signals look stale or duplicate. De-dupe key becomes **(`instance_id`, `signal_id`)**; freshness stays `generated_at_utc`-based. Blocking.
1b. **ADD `engine.autotrade_armed`** (bool; required in every payload). Carries the engine-side arming toggle for the dual-arm interlock (trader requirement — see the section below). The engine keeps emitting verdicts regardless of arming (display/logging continue; "silence = dead" semantics unchanged); this flag only tells the executor whether the engine side consents to live entries. Blocking.
2. **PIN the `health.ws` enum** to exact strings — propose `"OK" | "DEGRADED" | "DOWN"` (your obligation-4 rule references `≠ DOWN`; a schema gate needs the full set). Same for `signal_state` (`"OK" | "SKIPPED"`) and `confidence` (`"HIGH" | "MEDIUM" | "LOW" | "N/A"`) — the example implies them; the frozen contract should enumerate them.
3. **CLARIFY (prose, no schema change) — `levels.*.stop` semantics:** we read it as the **exit trigger level** (structural invalidation). Deribit stop-limit legs need trigger *and* limit; we derive the limit a small execution offset beyond the trigger (our mechanics, per R1). Confirm the stop you emit is the trigger-level reading.
4. **CLARIFY — `levels.*.entry` semantics:** we treat it as the **signal reference price**, not a limit-price command. We enter at top-of-book with our repositioning, and our existing slippage guard caps total drift **relative to your `entry`**, with the cap computed from the payload `atr` (default 0.6×, config-overridable). If price runs beyond the cap before fill, we abandon the entry (scoped cancel) and stand down until your next signal. Confirm this matches engine intent (it should — it's your tradeability gate's execution-side mirror).
5. **CLARIFY — `atr` presence:** confirm it is always present and non-zero whenever `direction ≠ NONE`. After FrmIndicators retires, your `atr` is our **only** ATR source for the slippage cap and the R2 override math.
6. **Serialization pins:** numbers as JSON numbers (never strings), invariant culture, `generated_at_utc` ISO-8601 with `Z`. Your example already conforms; freezing it as a rule.

## Path + watcher answer (your question)

- **Default path:** `C:\Dev\DeribitBridge\verdict_signal.json` — a neutral folder outside both repos. Configurable on both sides; we read ours from a small `bridge.json` beside the exe (same pattern as our `secrets.json`).
- **Consumer behavior:** FSW with ~150 ms debounce + one retry on share-violation, **plus an independent 10 s timer** for the staleness/`SKIPPED` stand-down — FSW alone cannot detect a dead engine (no writes ⇒ no events), so the age gate must poll. Stand-down alert after **3 consecutive stale checks** (~30 s past the age limit).
- **Age gate:** as you specified — `now − generated_at_utc > 2.5 × max(exec_resolution_min, 1)` minutes ⇒ stand down.

## Dual-arm interlock (trader requirement, 2026-07-03 — fixed constraint, both sides)

Live auto-trading requires a deliberate three-action sequence, and any single break in it stops entries:

1. **Engine-side toggle** "ARM AUTOTRADE" — **defaults OFF**, emitted as `engine.autotrade_armed` in every payload.
2. **Order-app-side toggle** "ARM AUTOTRADE" — **defaults OFF**, local.
3. **START button on the order app** (the executing side owns final arming, consistent with kill-switch placement). Pressing START while either toggle is off does nothing except log why.

Semantics the executor enforces:
- Live entries only when: latest payload is fresh ∧ `engine.autotrade_armed = true` ∧ local toggle ON ∧ STARTed ∧ all obligation-4 action gates.
- **START is not sticky.** Any disarm — either toggle going off, a stale/`SKIPPED` payload stand-down, or the circuit breaker — drops the state back to STOPPED; resuming requires the full sequence again (arm + arm + START).
- **Restart = disarmed.** Neither toggle nor the started state persists across a restart of either app.
- The interlock gates **live mode only**; log-only mode (rollout ladder) places no orders and may run un-armed — that's what makes supervised soak safe.

Engine-side ask: the toggle + `autotrade_armed` emission is the only engine-side work here; the interlock logic is all ours. In v2, the feedback file will carry the executor's armed/started state so your UI can display the full interlock status.

## Executor-side commitments (mirror of your obligations list)

1. De-dupe on (`instance_id`, `signal_id`), **persisted across order-app restarts** (last-acted pair written to disk) — kills the restart-replay edge your age window would otherwise leave open.
2. Strict entry policy in v1: act only when flat with no working entry (our new placement API enforces this below the bridge, as defense in depth).
3. Action mapping exactly per your obligation 4; default tiers HIGH + MEDIUM, configurable.
4. Levels placed as-is (R2); ATR-override checkbox default OFF; dual-set logging when ON.
5. Rollout ladder: **off → log-only (a few sessions, would-be orders logged against live payloads) → live at minimum size → normal.** The mode switch lives on our side and is independent of your emissions.
6. Every consumed signal is logged with its disposition (`acted` / `refused: <gate>` / `stale` / `skipped`) — in v1 this is our local log; in v2 it moves into the feedback file so the engine sees it.

## v2 (agreed direction, one addition)

Position-state feedback file (order app → engine, same atomic-write pattern): our proposal is it carries **position state** (size, avg entry, flat/holding) **plus the last-processed signal's disposition** — that gives your hold/exit logic real position truth *and* closes the ack loop without a second transport. Gated on 1–2 supervised weeks of v1, as you proposed.

## Convergence

Ack the three ADD/PIN items (`instance_id`, `autotrade_armed`, enum pins), the three CLARIFY readings, and the interlock section (or counter), and we freeze v1. The order-app side then implements the consumer inside the re-coded AutoTradeSettings module per our tie-in spec; nothing on our side is built against the schema before the freeze.
