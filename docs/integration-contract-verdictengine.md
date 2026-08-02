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

**Emitter implementation notes (2026-07-06 — informational, from the engine coordinator's A2 go-ahead; schema v1 unchanged):**
- Output is indented JSON — whitespace is not contractual.
- `engine.settings_version` drifts without notice (48→50 within days of the freeze) — reaffirming: never pin it; `schema_version` is the only version gates read.
- `kelly` on a no-edge run emits `{"contracts":0,"risk_usd":0.0,"lev_capped":false}` — zeros, never null (advisory-only in v1 regardless).
- `SKIPPED` payloads always carry `health.ledger_mismatch: false` — no verdict exists on a skip; the consumer stands down at gate §4.2 and must not read health semantics from skips.
- `health.ws` precedence as implemented: engine on REST transport → `"REST"`; per-run degradation → `"DEGRADED"`; feed missing/disconnected → `"DOWN"`; else `"OK"`. Only `DOWN` blocks (unchanged).
- `signal_id` gaps are legal (`SKIPPED` runs consume ids; a rare mid-run abort can burn one). Monotonicity within an `instance_id` is the only guarantee — never infer missed signals from gaps.
- Soak-review join: as of engine CSV v0.8, every logged engine run carries `InstanceId`/`SignalId` columns equal to the payload identity — the consumer's §4 disposition log (which records the same pair) joins row-for-row. Keep the disposition log faithful and its tokens stable.

## 4. Consumer gate chain (v1)

An entry may be attempted **only when every clause holds**, evaluated per fresh payload:

1. `schema_version = 1` (mismatch ⇒ no action + alert).
2. Freshness: `now − generated_at_utc ≤ 2.5 × max(exec_resolution_min, 1)` minutes. Stale ⇒ stand down (+ alert per §2). `signal_state = "SKIPPED"` ⇒ stand down (never "hold the last signal").
3. Not a duplicate: (`instance_id`, `signal_id`) not already acted on — **persisted across order-app restarts**.
4. `signal_state = "OK"` ∧ `direction ≠ "NONE"` ∧ `confidence ∈ configured tiers` (default **HIGH + MEDIUM**) ∧ `mtf_blocked = false` ∧ `verdict_context ≠ "BELOW_MIN_MOVE"` ∧ `health.ledger_mismatch = false` ∧ `health.ws ≠ "DOWN"`.
5. Dual-arm interlock satisfied (§6) — live mode only.
6. Order-app operational gates: connected ∧ rate-limit OK ∧ flat ∧ no working entry ∧ no cancel pending ∧ cooloff elapsed ∧ circuit breaker not tripped ∧ inside session window (if configured).

Every consumed payload gets a **disposition log entry**: `acted` / `refused: <first failing gate>` / `stale` / `skipped` / `duplicate`. (v1: local log; v2: also written to the feedback file.)

**Addendum 2026-07-21 (session policy — consumer-side subsets; decision-of-record for the engine proposal's P1, trader-ticked both sides):** the order app adds a **trader-policy gate** — per-session tier/context subsets + a size multiplier (`session_policy`, engine doc `session-policy-gate-proposal.md`, order-app spec `spec-session-policy-gate.md`). Contract consequences, and nothing else changes:

- **Placement in the chain:** evaluated AFTER the whole §4 gate 4 (so every contract refusal keeps its exact token) and before the interlock. New disposition class: `refused: policy(<session>/<tier|context>)`. This is R1's "tier selection" grown up — execution policy, never verdict re-gating; engine-side per-session thresholds were considered and rejected for the subset use-case (set membership, not a bar), so the engine's book stays whole and the refused cells keep measuring.
- **Session buckets pinned, engine-identical, derived from `generated_at_utc` (UTC):** ASIA 00:00–07:59 · LONDON 08:00–12:59 · NY 13:00–23:59.
- **`verdict_context` values become STABLE IDENTIFIERS:** the policy compares them as opaque case-folded strings (no app-side vocabulary), so renaming/removing a context value is now a **coordinated change** (docs note both sides), not free drift. Their *semantics* stay informational — `BELOW_MIN_MOVE` remains the only value with pinned no-action meaning; the policy gate is trader configuration, not payload semantics. Tier subsets gate on the already-pinned `confidence` enum only (`verdict` stays free text, never parsed).
- **`size_mult` is consumer-side sizing config** (§5's "sizing is consumer-side" unchanged), applied exactly once at the act/would-act site, floored to the contract step with a logged clamp at the 10-USD minimum.
- **Rollout:** ships **disabled by default** — the token cannot appear in the v1 soak's disposition stream until the trader enables it at the live-ladder step (post-soak-review, per the engine doc's P5 conservative starting policy).

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

**Addendum 2026-07-06 (cross-project rollout gate):** the step to **live at minimum size** is additionally gated on the engine-side "placed-geometry structural-first" pass being live (it changes the origin of `levels.*` from ATR-derived to structural-first; schema and placement semantics untouched — prices stay prices; `cap_reason` may gain new label values, informational as ever). **The trader confirms that pass is live before stepping up. Log-only does NOT wait for it.**

**2026-07-22 — log-only soak COMPLETED and CALLED** at the §7 one-week lower bound (soak live 2026-07-16 → 07-22). Exit evidence: the full column-level join, CLEAN — 1398/1398 matched rows zero mismatches, 69/69 would-act ≡ engine `Placed*` (the fourth parity check), all unmatched rows SKIPPED-class per the by-design partial payload→CSV join (`review-soak-join-2026-07-22.md`; engine-seat §5.7 mapping in `soak-review-reply-orderapp.md`). The 2026-07-06 geometry gate is satisfied (engine seat: v51 live since 07-07, v56 defaults byte-identical; trader-relayed = the trader confirmation this addendum requires). **The ladder may proceed: §6 interlock + §6.2/6.3 live tests at minimum size, trader-supervised.**

## 8. v2 feedback file (SPECIFIED 2026-07-28 — trader-ticked T1–T8; implementation pending both sides)

**Exchange of record:** order-app `proposal-c1-v2-feedback-file.md` → engine
`feedback-file-engine-reply-2026-07-28.md` → order-app `ack-c1-v2-feedback-file.md` → trader tick
(T1–T8, 2026-07-28) → this coordinated pass. **Canonicality mirrors v1, reversed:** THIS section is
canonical for **emitter behavior** (the order app writes the file); the engine's bridge-doc mirror
is canonical for **consumption + display**. **The v1 signal schema and its `schema_version: 1`
gate are UNTOUCHED by this addendum.**

### 8.1 Principles (binding)

Telemetry, never commands — orders/signals flow engine→app via `verdict_signal.json` ONLY (R1
holds in both directions). One JSON file, overwritten, **atomic (temp + atomic replace)**, single
writer = the order app. *(Amended 2026-08-02 — was "temp + `File.Replace`". Naming that specific API
was a wording defect: `File.Replace` throws when the destination does not exist, which is exactly
the first-write and OFF→ON case. The requirement is atomicity, not the API; the order app's house
pattern is `WriteAllText(tmp)` + `File.Move(overwrite:=True)`. Engine mirror §10.2 repeats the old
wording and takes the same one-line correction.)* Silence = dead executor. v1 serialization pins carry over (JSON numbers,
invariant culture, ISO-8601 UTC `Z`). Zeros-never-null for suppressed numeric blocks; null for
absent objects; monotonic ids per process instance, gaps legal, never inferred from. The §4
**disposition-cardinality freeze holds**: `last_signal` reflects the most recent consumed payload,
written once at consumption; post-acted outcomes (chase aborts) never update it. No credentials
ever appear in the file.

### 8.2 Transport + config (T1/T2)

Default path `C:\Dev\DeribitBridge\executor_feedback.json` (beside the signal file). Order-app key:
`bridge.json` `feedback_output_path`; engine key: `signal_bridge.feedback` block
`{ enabled: false, path, stale_after_sec: 35 }`. **Ships OFF both sides.** Engine consumes per-run
(fresh read at `RunAnalysisAsync` start; the staleness check at the same site).

### 8.3 Schema — feedback v1 (the file's own counter)

```json
{
  "schema_version": 1,
  "feedback_id": 587,
  "generated_at_utc": "2026-08-05T09:14:02Z",
  "executor": { "instance_id": "3c1a…-guid", "app": "DeribitOrderPlacementApp",
                "mode": "LIVE", "armed": true, "started": true,
                "breaker_tripped": false, "ws": "OK" },
  "instrument": "BTC-PERPETUAL",
  "position": { "direction": "LONG", "size_usd": 250.0, "avg_entry": 59012.5,
                "working": { "stop": 58962.5, "target": 59095.0 } },
  "last_signal": { "instance_id": "9f0c…-guid", "signal_id": 1234,
                   "disposition": "acted", "at_utc": "2026-08-05T09:13:41Z" }
}
```

**Pinned enums (exact strings):** `executor.mode` `"OFF" | "LOG_ONLY" | "LIVE"` · `executor.ws`
`"OK" | "DOWN"` · `position.direction` `"LONG" | "SHORT" | "FLAT"`.
**Enum tolerance (T8):** the consumer gates/branches only on pinned strings; an unrecognised value
renders verbatim and takes the conservative arm (unknown `mode` ≠ LIVE; unknown `direction` ⇒
manual fallback). An *additive* enum value is a coordinated docs note in both documents — not a
schema bump, not a lockstep deploy; never free drift.

### 8.4 Field semantics (binding)

- `feedback_id` — monotonic per executor process; identity = (`executor.instance_id`,
  `feedback_id`); `instance_id` = GUID minted at order-app process start. **Restart ⇒ new GUID +
  `armed`/`started` false by construction** — §6's restart-disarmed becomes visible engine-side.
- `executor.armed`/`started` — the order-app local toggle and START state. The engine's own ARM is
  NOT echoed back. `mode` distinguishes the log-only soak from live. `breaker_tripped` and `ws`
  are informational ("can the executor act" display).
- `position` — **the account's BTC-PERPETUAL position as the order app's position model sees it,
  regardless of origin (bridge-placed AND owner-manual alike, T6 — deliberate).** Flat ⇒
  `direction:"FLAT"` + zeros. `size_usd` signed; emitter guarantees sign/direction consistency.
  `working` = resting stop/target, 0 = unset, informational (structural-block precedent).
- `last_signal` — `null` until this executor process consumes its first payload. Identity = the
  ENGINE's (`instance_id`, `signal_id`) pair (the soak-proven join key); `disposition` = the exact
  soak-stable token; `at_utc` = consumption time. **Fill-window gap semantics:** after `acted`,
  `position` stays FLAT until the entry chase fills; an abandoned chase leaves it FLAT — the
  engine never infers failure from that window.
- **The `avg_entry` join is the slippage record:** after `acted` for the engine's pair (X, N), the
  next non-flat `position.avg_entry` is signal N's achieved fill. **Standing policy note (T7):**
  the day stacking/pyramiding enters executor policy, a per-acted-signal achieved-entry field
  becomes a v2.1 amendment FIRST — until then the join is the record (stacking is currently
  structurally excluded by the strict v1 API policy + gate 4.6).

### 8.5 Emission (order-app commitments) + freshness

Write on: (a) each disposition · (b) position open/close/size change · **(b2) working-level
stop/target change** (engine refinement 3.1) · (c) ARM/START/mode/breaker transitions · (d) ~10 s
heartbeat · (e) a final write on graceful close. Emission is unconditional once enabled; **never
on the receive path** (snapshot → single-writer worker, fire-and-forget, fail-silent,
self-coalescing last-wins — the notifier discipline). **Engine staleness rule:**
`now − generated_at_utc > stale_after_sec (35)` ⇒ `EXECUTOR STALE` + manual fallback; **file
absent = feature OFF, never an alarm.**

### 8.6 Engine-side consumption (D6/T6 — mirror doc is canonical for detail)

Feedback-authoritative when governing (`enabled` ∧ present ∧ fresh ∧ `direction` parses):
`LONG`/`SHORT`/`FLAT` → `PositionState` Long/Short/None; manual radios grey out with a source
tooltip (`POS:EXEC`/`POS:MANUAL` tags); stale/absent/disabled/unparseable ⇒ manual behaviour
returns unchanged. **Phase 1 surfaces on the live-status display tier ONLY** — no snapshot line,
no card binding, no CSV column, no payload field. While governed, HOLD\EXIT + the exit guard track
the executor's real position including owner-manual trades (T6, ticked knowingly).

### 8.7 Phase-2 fence (T5) + rollout

**Actionable exits are a SEPARATE future amendment**: a new pinned field on the signal schema with
a bump to `schema_version: 2`, gated on the phase-1 display soak — **never by parsing
`hold_status`**, which stays informational free text forever. Rollout: OFF → emit-only (file
inspected by hand) → engine display consumption → soak through normal trading. Implementation
slots into each side's queue (order-app emitter = own Opus-HIGH pass, ships OFF, after N2 unless
the trader reorders; engine consumption behind its §6.1 net-EV rider). Nothing is Aug-1-critical.
AWS §9 co-location impact: none (same-machine file transport, both directions).

## 9. Version history

- **v1 — 2026-07-03 — FROZEN.** Initial contract: brief → reply (adds `instance_id`, `autotrade_armed`, enum pins, semantics clarifies, interlock) → ack (accepts all; `health.ws` gains `"REST"`; WEAK-carries-direction clarification; `atr` guarantee proof). Implementation unlocked both lanes: engine `Core/SignalEmitter.vb` + ARM toggle; order-app consumer inside the re-coded AutoTradeSettings (tie-in spec).
- **2026-07-06 — v1 unchanged; engine emitter LIVE-READY.** Emitter implemented, fixture-pinned to v1 (engine A22a–g: enums, target-cap cases, NO-TRADE→`direction:"NONE"`, invariant culture), live-smoke-tested, pushed (engine repo `23fd8b9`); emission ships OFF (`signal_bridge.enabled: false`) until the trader flips it for the log-only soak. Emitter implementation notes recorded in §3 (informational); rollout addendum in §7 (geometry-pass gate on the live step). Consumer lane (A2) cleared to implement.
- **2026-07-21 — v1 unchanged (schema untouched); session-policy addendum in §4.** Consumer-side per-session tier/context subsets + size multiplier (engine proposal P1–P5 trader-ticked both sides): new `refused: policy(...)` disposition class, session buckets pinned, `verdict_context` values pinned as stable identifiers (renames = coordinated change). Ships disabled; enable = trader action at the live-ladder step. The engine seat should read the §4 addendum — the stable-identifier pin is the one obligation it adds engine-side.
- **2026-08-02 — signal schema v1 UNTOUCHED; §8.1 wording amended (trader-ticked).** `atomic (temp + File.Replace)` → `atomic (temp + atomic replace)`. Raised as escalation **E1** of `spec-c1-feedback-emitter.md` before any emitter code was written: `File.Replace` throws when the destination is absent — the first-write and OFF→ON case — so naming it pinned an API that cannot serve the requirement unaided. Atomicity was always the requirement; the API never was. **No schema, field, enum or behaviour change; no engine-side work.** ⚠ **Owner relay owed:** the engine's `signal-bridge-v1-proposal.md` §10.2 repeats the old wording verbatim and takes the same one-line correction — a coordinated docs note, per the §8.3 T8 precedent, not a schema bump. Ruled in the same pass: E2 (`executor.mode` must be an explicit map — `BridgeMode.LogOnly.ToString()` = `"LogOnly"` ≠ pinned `"LOG_ONLY"`, and T8 tolerance makes that fail *silently*), E3 (`position.size_usd` sign to be established from a real short before the mapping is written), E5 (**`LOG_ONLY` DOES emit** — `mode` exists to tell the soak from live, and the rollout ladder, not mode-gating, is what keeps the engine from consuming early). E4 was resolved against the engine's §10.3/§10.4 and carries a binding revisit trigger.
- **2026-07-28 — signal schema v1 UNTOUCHED; §8 replaced: the v2 feedback file is SPECIFIED (trader-ticked T1–T8).** Order app → engine, `executor_feedback.json`, feedback `schema_version: 1` (its own counter). Exchange: `proposal-c1-v2-feedback-file.md` → `feedback-file-engine-reply-2026-07-28.md` (ACCEPTED + 3 refinements, all accepted in the ack) → `ack-c1-v2-feedback-file.md` → tick. Engine mirrors this section in its bridge doc in the same coordinated pass (canonical for consumption/display; this section canonical for emission) — **mirror LANDED same day: engine `signal-bridge-v1-proposal.md` §10, engine commit `52c8633`; the v2 documentation phase is CLOSED both sides.** Ships OFF both sides; implementation queued per §8.7; phase-2 actionable exits fenced as a future signal-schema-v2 amendment.
