# C1 proposal — v2 feedback file (order app → engine)

**Status: DRAFT for owner relay to the engine seat** (contract §8 direction; owner arbitrates).
Written 2026-07-28, coordinator seat. Process = the v1 reconciliation-trail pattern in reverse:
this proposal → engine-seat reply → order-app ack → ONE coordinated documentation pass amending
the frozen contract (both repos' docs, neither side editing the other's repo). Implementation
starts only after the ack + owner tick. Roadmap slot: `ROADMAP-2026-08.md` §4 C1 — after the EV
chase budget, N1b, and N2 in the implementation queue; the *proposal* exchange can run in parallel
with those implementation passes since it is docs-only.

## 1. Why now, and what it unlocks

The v1 gate is met: the soak was called clean 2026-07-22 (`review-soak-join-2026-07-22.md`),
live-at-min-size is executed and CSV-verified, and contract §8 already records the agreed v2
direction. On the engine side the machinery is *waiting for this file*:

- The engine's exit guard and `hold_status` are keyed on a `PositionState` that today comes from
  **manual radio buttons** on the engine UI (`MainForm_ExitGuard.vb` `rbLong`/`rbShort`) — the
  engine literally does not know whether the executor holds a position.
- The emitter's own source comments say it: `hold_status` is *"informational until v2 makes
  posState truthful"* (`Core/SignalEmitter.vb`), and the engine's v1 proposal records the agreed
  v2 shape verbatim: position state **plus last-processed signal disposition plus the executor's
  armed/started state**, same atomic-write pattern, closing the ack loop.

What a truthful feedback file unlocks, in order of value:

1. **Engine-managed exits (the largest remaining P/L lever)** — with `posState` truthful, the
   exit guard and `CalcHoldStatus` finally evaluate against the *real* position (including the
   owner's manual trades, deliberately — see §4). Actionability itself is **phase 2, explicitly
   out of scope here** (§7).
2. **Slippage-aware signal pricing** — the engine can join achieved entry to its own signal:
   after `last_signal.disposition = "acted"` for (`instance_id`, `signal_id`) = (its own GUID, N),
   the next non-flat `position.avg_entry` IS the achieved fill for signal N. No new per-signal
   field, no cardinality change — the derivation rides the state snapshot.
3. **Full interlock display engine-side** — the engine can show executor armed/started/mode/alive
   next to its own ARM toggle, making §6's dual-arm state visible from one screen (today the
   engine emits `autotrade_armed` blind and sees nothing back).
4. **Dead-executor detection** — same silence-is-unambiguous property as v1, in the reverse
   direction (§5 freshness).

## 2. Design principles (mirroring frozen v1; binding on the draft)

1. **Telemetry, never commands.** The feedback file carries executor *state*. Orders and signals
   flow engine→app via `verdict_signal.json` ONLY. Nothing in the feedback file may instruct the
   engine, and nothing in it re-gates signal logic (R1 unchanged, both directions).
2. **One JSON file, overwritten, atomic** (temp + `File.Replace`), same directory as the signal
   file. Single writer (the order app); the engine never writes it. Silence = dead executor.
3. **Phase 1 leaves frozen v1 UNTOUCHED.** `verdict_signal.json` does not change; its
   `schema_version` stays 1; no consumer-gate change on either side. The amendment *adds* a
   section; it does not reopen the freeze. (The signal-schema bump to 2 happens only at phase 2,
   through the same process, when exits become actionable.)
4. **v1 conventions carry over verbatim:** JSON numbers never strings, invariant culture,
   ISO-8601 UTC `Z`, pinned enums only where gating/display needs them, free text never gated,
   zeros-never-null for suppressed numeric blocks (kelly precedent), null for absent objects
   (`hold_status` precedent), monotonic ids per process instance, gaps legal.
5. **The disposition-cardinality freeze holds.** `last_signal` reflects the most recent
   *consumed payload* and is written once per payload at consumption. Post-acted outcomes (chase
   aborts, EV-budget abandonments) NEVER update it — the host-log cancel REASON remains the
   counterfactual instrument. The feedback file gives that freeze a second reader, not a side door.

## 3. Proposed schema (feedback v1 — the file's own counter, distinct from the signal schema)

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

**Pinned enums (exact strings):** `executor.mode` `"OFF" | "LOG_ONLY" | "LIVE"` ·
`executor.ws` `"OK" | "DOWN"` · `position.direction` `"LONG" | "SHORT" | "FLAT"`.

## 4. Field semantics (binding)

- `feedback_id` — monotonic per executor process. Identity = (`executor.instance_id`,
  `feedback_id`); `instance_id` is a GUID minted at order-app process start. Restart ⇒ new GUID,
  counter restarts, `armed`/`started` false **by construction** — §6's restart-= -disarmed
  becomes *visible* engine-side instead of assumed.
- `executor.armed` / `started` — the order-app local toggle and START state (the engine's own
  ARM is NOT echoed back; the engine knows its own state, and echoing invites drift confusion).
  `mode` distinguishes the log-only soak from live for engine display and analytics.
- `executor.breaker_tripped` — the circuit breaker's current state (informational; it gates the
  bridge path only, but "executor cannot act" is exactly what interlock display wants).
- `executor.ws` — the executor's Deribit WS connection, two values only; informational.
- `position` — **the account's BTC-PERPETUAL position as the order app's position model sees it,
  regardless of origin** (bridge-placed AND owner-manual alike). Deliberate: the exit guard
  should guard the owner's manual positions too, and the position model is already the app's
  single source of truth. Flat ⇒ `direction:"FLAT"`, `size_usd:0`, `avg_entry:0`, `working`
  zeros (zeros-never-null). `size_usd` is signed; the emitter guarantees sign/direction
  consistency; the consumer may key on either.
- `position.working` — the currently-resting stop/target levels, 0 = unset (structural-block
  precedent: informational, never gated; the engine may ignore).
- `last_signal` — `null` until this executor process consumes its first payload. `instance_id` /
  `signal_id` are the ENGINE's identity pair for that payload (the join key the soak already
  proved). `disposition` = the exact soak-stable token verbatim from the §4 disposition machinery
  (same strings as `bridge-dispositions.log`); `at_utc` = consumption time. Written once per
  consumed payload (§2.5). Note one deliberate gap semantics: after `acted`, `position` stays
  FLAT until the entry chase fills — the engine must not infer failure from that window, and an
  abandoned chase simply never opens the position (abandonment telemetry is host-log/EV-budget
  territory, not this file's — a v2.1 discussion if the engine ever wants it).
- The file never carries credentials or secrets (the ntfy URL rule generalizes).

## 5. Emission triggers and freshness

Write on **every state change + a heartbeat**: (a) each disposition (at the existing
`EmitDisposition` site — the "v2 feedback-file precursor" comment already marks it); (b) position
model open/close/size change; (c) ARM/START/mode/breaker transitions; (d) a ~10 s heartbeat
(implementation note: the bridge's existing 10 s staleness timer can drive it — zero new timers);
(e) a final write on graceful close. Crash ⇒ stale file, which is the point:

- **Proposed engine-side freshness rule:** `now − generated_at_utc > 35 s` (3 heartbeats +
  margin) ⇒ "EXECUTOR STALE" display and `posState` falls back to today's manual radio state.
  **File absent ⇒ feature OFF, never an alarm** (rollout ships OFF, §7). Numbers are proposal
  defaults — the engine seat tunes its own consumption.

## 6. What the order app commits to (implementation constraints, spec-bound later)

1. Emission is unconditional once enabled — every disposition including refusals, every state
   change, heartbeat always; no "quiet mode".
2. **Never on the receive path.** The emitter snapshots state and hands off to a single-writer
   worker (RemoteNotifier discipline: fire-and-forget, fail-silent, self-coalescing last-wins;
   a write failure logs once to the host log and never throws into a caller).
3. Atomic write, same pattern as the engine's `TryWrite` (temp + `File.Replace` in the target
   directory).
4. Token and format stability once the engine starts reading (soak discipline carries over).
5. Ships **OFF** behind config; path configurable; default
   `C:\Dev\DeribitBridge\executor_feedback.json` beside the signal file. Order-app key in
   `bridge.json` (`feedback_output_path`); engine key is the engine seat's naming call.
6. Opus-HIGH implementer, own pass, coordinator review + gate, per the standing methodology.

## 7. Rollout ladder and phase 2 (out of scope here, fenced deliberately)

**Phase 1 ladder (this proposal):** OFF → **emit-only** (engine not yet reading; file inspected
by hand a few sessions) → **engine display consumption** (truthful `posState` drives the
HOLD\EXIT row + exit-guard strip + interlock display — still display/alert only) → soak in that
state through normal trading.

**Phase 2 — actionable exits — is a SEPARATE future amendment**, gated on the phase-1 display
soak proving `posState` truthful in practice. Design stance proposed now so nobody builds toward
the wrong shape: actionability arrives as a **new pinned field on the signal schema (bump to
`schema_version: 2`)** with contract-pinned semantics — it does NOT arrive by the consumer
starting to parse `hold_status`, which stays informational free text forever (v1's never-gate-
on-free-strings principle). App-side execution of an engine exit signal remains execution
mechanics under R1: the engine decides the exit, the app places it. Nothing in phase 1 commits
either side to a phase-2 schedule.

**AWS §9 co-location: zero impact** — same-machine file transport, both directions.

## 8. Decision points for the owner + engine seat

| # | Question | Recommendation |
|---|---|---|
| D1 | File name/path + config keys | `executor_feedback.json` beside the signal file; keys per §6.5 |
| D2 | Cadence + staleness numbers | 10 s heartbeat / 35 s engine staleness (§5); engine tunes |
| D3 | `position.working` stop/target block | INCLUDE (informational, 0=unset; engine may ignore) |
| D4 | `breaker_tripped` + `ws` in executor block | INCLUDE (interlock display wants "can the executor act") |
| D5 | Phase-2 fence | Ratify §7: separate amendment, signal-schema bump, pinned field, never parse `hold_status` |
| D6 | Engine-side `posState` consumption shape (do the radio buttons become override? retire?) | Engine seat proposes in its reply; owner ticks |

**Open question for the engine seat:** is the §1.2 `avg_entry` join sufficient for slippage-aware
pricing v1, or does the engine want per-acted-signal achieved-entry surfaced explicitly? (We
prefer the join — no new cardinality — but the reply should say.)

## 9. Contract mechanics

On ack: one coordinated pass replaces contract §8's direction paragraph with the agreed feedback
spec (this doc reconciled), records the exchange in §9 version history, and mirrors the schema in
the engine's bridge doc — the signal schema and its `schema_version: 1` gate UNTOUCHED. The
engine's fixture-guard pattern (A22-style pins) is recommended for the feedback emitter on our
side; OrderCheck grows the corresponding fixtures at implementation time.
