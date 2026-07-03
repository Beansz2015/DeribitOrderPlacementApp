# Integration Contract — DeribitVerdictEngine ⇄ DeribitOrderPlacementApp

**Schema v1 — FROZEN 2026-07-03** (trader-ratified: order-app rulings 2026-07-03 + engine-side D1–D10 ticks 2026-07-03).
**Canonicality:** THIS document is canonical for **consumer behavior** (the order app). `DeribitVerdictEngine/docs/signal-bridge-v1-proposal.md` §3 is the verbatim schema mirror, canonical for **emission + display parity** (fixture-guarded on the engine side). **Drift guard:** `schema_version` gates the consumer; any schema change bumps it and updates both documents in one coordinated pass. Neither side edits the other's repo.
**Reconciliation trail:** engine brief (`signal-bridge-orchestrator-brief.md`) → order-app reply (`signal-bridge-reply-orderapp.md`) → engine ack (`signal-bridge-ack-to-orderapp.md`). This document supersedes all three for implementation purposes.

---

## 1. Roles and rulings (trader-fixed)

- **R1 — replacement, not gating.** The engine's verdict is the **sole signal source** for the autotrade path; the order app's internal EMA/VWAP/DMI score retires with FrmIndicators. The order app never re-gates signal *logic*. It always enforces **operational gates**: the dual-arm interlock (§6), connection state, rate-limit readiness, flat/no-working-entry, cooloff, circuit breaker, session time window. Operational gates may refuse an entry; they never overrule the verdict.
- **R2 — engine levels are authoritative.** The order app places the engine's final effective stop/target levels as-is. The app's ATR distance math is a **default-OFF checkbox override**; when ON it may recompute distances only, never resurrect a suppressed verdict; both level sets are logged per trade.
- The engine never places orders. The order app never generates signals.

## 2. Transport

- **One JSON file, overwritten after EVERY engine run** (including skips — silence therefore means the engine is dead, unambiguously). Atomic write: temp file + `File.Replace`; a reader never sees a torn file.
- **Path default:** `C:\Dev\DeribitBridge\verdict_signal.json`. Engine config key `signal_bridge.output_path` (emitter creates the directory); order-app path in `bridge.json` beside the exe. Both configurable; the default is the contract's shared assumption.
- **Consumer watcher:** `FileSystemWatcher` + ~150 ms debounce + one retry on share-violation, **plus an independent ~10 s staleness timer** (FSW cannot detect a dead engine). Stand-down alert after **3 consecutive stale checks**.
- Engine ships the emitter behind `signal_bridge.enabled: false` until rollout.

## 3. Schema v1 (frozen)

```json
{
  "schema_version": 1,
  "signal_id": 1234,
  "generated_at_utc": "2026-07-03T14:31:02Z",
  "engine": { "instance_id": "9f0c…-guid", "autotrade_armed": false,
              "settings_version": 48, "app": "DeribitVerdictEngine" },
  "instrument": "BTC-PERPETUAL",
  "signal_state": "OK",
  "skip_reason": null,

  "verdict": "STRONG SHORT",
  "confidence": "HIGH",
  "direction": "SHORT",
  "verdict_context": "CONFIRMED",
  "mtf_blocked": false,
  "scores": { "long": 4, "short": 13, "eff_long": 4, "eff_short": 13, "max": 20 },

  "price": 59012.5,
  "exec_resolution_min": 1,
  "trigger_mode": "on_close",
  "atr": 41.3,

  "levels": {
    "long":  { "entry": 59012.5, "stop": 58962.9, "target": 59095.1,
               "target_capped": true, "cap_reason": "SWING_HIGH_5M", "raw_target": 59095.1 },
    "short": { "entry": 59012.5, "stop": 59062.1, "target": 58929.9,
               "target_capped": false, "cap_reason": null, "raw_target": 58929.9 }
  },
  "structural": { "swing_target_long": 59095.0, "swing_stop_long": 58860.0,
                  "swing_target_short": 58860.0, "swing_stop_short": 59095.0 },

  "hold_status": null,
  "kelly": { "contracts": 2, "risk_usd": 32.5, "lev_capped": false },

  "health": { "ws": "OK", "degraded_this_run": false, "ledger_mismatch": false }
}
```

**Pinned enums (exact strings):** `signal_state` `"OK" | "SKIPPED"` · `confidence` `"HIGH" | "MEDIUM" | "LOW" | "N/A"` · `direction` `"LONG" | "SHORT" | "NONE"` · `trigger_mode` `"interval" | "on_close"` · `health.ws` `"OK" | "DEGRADED" | "DOWN" | "REST"` (REST = engine deliberately on its REST transport; **treat as OK — only DOWN blocks**).

**Field semantics (binding):**
- `signal_id` — monotonic **per engine process**. De-dupe identity = **(`engine.instance_id`, `signal_id`)**; `instance_id` is a GUID minted at engine process start.
- `direction` — `"NONE"` on **every** `NO TRADE*` verdict; leans are logging context. **WEAK verdicts carry their direction** (`WEAK LONG` ⇒ `direction:"LONG"`, `confidence:"LOW"`) — the *confidence-tier gate* refuses them, not `direction`. Never infer non-actionability from `direction` alone.
- `levels.<dir>.stop` — the **exit-trigger level** (structural/ATR invalidation). The consumer derives its stop-limit's limit leg a small execution offset beyond it (execution mechanics, per R1).
- `levels.<dir>.target` — the take-profit **limit price**, placed as-is.
- `levels.<dir>.entry` / `price` — **signal reference price**, not a limit-price command. The consumer enters at top-of-book with its own repositioning, slippage-capped relative to `entry` (cap = configurable ATR-multiple of the payload `atr`, default **0.6×**); beyond the cap it abandons the entry via scoped cancel and stands down until the next signal.
- `atr` — execution-resolution ATR. **Guaranteed present and non-zero whenever `direction ≠ NONE`** (engine-side construction: min-tradeable-move gate ⇒ `atr ≥ 0.0004 × price`). Post-FrmIndicators this is the order app's only ATR source (slippage cap + R2 override math).
- `verdict_context = "BELOW_MIN_MOVE"` — contract-pinned **no-action** value. All other `verdict_context` values, and `verdict`, `skip_reason`, `cap_reason`, `hold_status`: informational free strings — **never gate on them**.
- `scores` — logging/analytics only; never threshold on raw scores (`max` varies by regime). `kelly` — advisory display only in v1; never size from it. `structural` — informational in v1 (0 = unset). `engine.settings_version` — informational; **`schema_version` is the only version gates read.**
- Serialization: JSON numbers (never strings), invariant culture, ISO-8601 UTC with `Z`.

## 4. Consumer gate chain (v1)

An entry may be attempted **only when every clause holds**, evaluated per fresh payload:

1. `schema_version = 1` (mismatch ⇒ no action + alert).
2. Freshness: `now − generated_at_utc ≤ 2.5 × max(exec_resolution_min, 1)` minutes. Stale ⇒ stand down (+ alert per §2). `signal_state = "SKIPPED"` ⇒ stand down (never "hold the last signal").
3. Not a duplicate: (`instance_id`, `signal_id`) not already acted on — **persisted across order-app restarts**.
4. `signal_state = "OK"` ∧ `direction ≠ "NONE"` ∧ `confidence ∈ configured tiers` (default **HIGH + MEDIUM**) ∧ `mtf_blocked = false` ∧ `verdict_context ≠ "BELOW_MIN_MOVE"` ∧ `health.ledger_mismatch = false` ∧ `health.ws ≠ "DOWN"`.
5. Dual-arm interlock satisfied (§6) — live mode only.
6. Order-app operational gates: connected ∧ rate-limit OK ∧ flat ∧ no working entry ∧ no cancel pending ∧ cooloff elapsed ∧ circuit breaker not tripped ∧ inside session window (if configured).

Every consumed payload gets a **disposition log entry**: `acted` / `refused: <first failing gate>` / `stale` / `skipped` / `duplicate`. (v1: local log; v2: also written to the feedback file.)

## 5. Placement mapping (v1)

On a passing payload: `SetTradeTargets(manualTP := levels.<dir>.target, manualSL := <stop mapped per §3 semantics>, sizeUSD := <consumer sizing>)` then `PlaceAutomatedOrder(direction, "limit")` on the order app's public API (see `spec-decouple-v2.md`) — strict v1 API policy applies (flat + no working entry), acks/rejections surface through the placement-ack machinery, slippage cap per §3 `entry` semantics. Sizing is consumer-side config in v1 (fixed size; `kelly` ignored).

## 6. Dual-arm interlock (trader-fixed)

Live auto-trading requires, in order: **engine ARM toggle** (default OFF every start, runtime-only, not persisted; emitted as `engine.autotrade_armed` in every payload — emission itself is unconditional) → **order-app ARM toggle** (default OFF, not persisted) → **START button on the order app**. Enforcement is consumer-side:

- Live entries only while: latest payload fresh ∧ `engine.autotrade_armed = true` ∧ local toggle ON ∧ STARTed ∧ §4 gates.
- **START is not sticky:** any disarm — either toggle, a stale/`SKIPPED` stand-down, the circuit breaker — drops to STOPPED; resuming requires the full arm+arm+START sequence.
- **Restart = disarmed**, both apps, by construction.
- The interlock gates **live mode only**; log-only mode places no orders and may run un-armed.

## 7. Rollout ladder

`off` → **log-only** (a few sessions: would-be orders logged against live payloads, no placement) → **live at minimum size** → normal. The mode switch is order-app-side and independent of engine emissions. v1 soak: **1–2 supervised weeks** before v2 work begins.

## 8. v2 (agreed direction, not yet specified)

One **feedback file** (order app → engine, same atomic-write pattern): position state (size, avg entry, flat/holding), last-processed signal disposition, and executor armed/started state (for engine-side interlock display). Unlocks the engine's `hold_status`/exit-guard as actionable exit signals and slippage-aware signal pricing. Gated on the v1 soak.

## 9. Version history

- **v1 — 2026-07-03 — FROZEN.** Initial contract: brief → reply (adds `instance_id`, `autotrade_armed`, enum pins, semantics clarifies, interlock) → ack (accepts all; `health.ws` gains `"REST"`; WEAK-carries-direction clarification; `atr` guarantee proof). Implementation unlocked both lanes: engine `Core/SignalEmitter.vb` + ARM toggle; order-app consumer inside the re-coded AutoTradeSettings (tie-in spec).
