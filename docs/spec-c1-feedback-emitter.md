# Spec — C1: executor feedback emitter (ships OFF; contract §8 is the binding schema)

**Origin:** `ROADMAP-2026-08.md` §4 / `HANDOVER-6.md` §3 — the order app writes
`executor_feedback.json` so the engine can see the real executor state. Documentation phase closed
2026-07-29 (proposal → engine ACCEPT + 3 refinements → ack → trader tick T1–T8); **contract §8 is
canonical for emitter behaviour and this spec never restates it — it implements it.** The engine's
bridge-doc §10 is canonical for consumption/display and is NOT our concern.

**Recommended implementer:** **Opus HIGH, fresh conversation.** New file, but it snapshots the
position model and sits beside the receive path.
**Target:** new `ExecutorFeedback.vb`; `SignalBridge.vb` (config key, disposition hook, mode/START
transitions, a breaker seam); `frmMainPageV2.vb` (position/working-level change hooks, process
GUID, graceful-close write); `tools/OrderCheck` (fixtures).
**Ground rules:** standing (gate per commit, never push, one commit per section, impl report).
**Ships OFF:** absent `feedback_output_path` ⇒ the emitter is inert and **no file is created**.
Contract §8.5: *file absent = feature OFF, never an alarm.*

**Do-not-touch:** the §4 gate chain order and disposition tokens · `bridge-dispositions.log` line
format (soak-stable) · the disposition-cardinality freeze · `positionAvgEntry`'s retention through
the flat echo (§3.2 — read around it, never "fix" it) · the v1 signal schema and its
`schema_version: 1` gate · `RemoteNotifier` (it is the *model* here, not a dependency).

---

## 🚨 §0 — Escalations: RULE ON THESE BEFORE WRITING CODE

Per `memory: spec-defect-escalation` / H-6 §7 — **E1** changes the frozen contract's wording,
**E2/E3** change the code, **E4/E5** are owner policy calls the implementer must not make alone.

**E1 — `File.Replace` vs the house atomic-write pattern.** Contract §8.1 says *"atomic (temp +
`File.Replace`)"*. The house pattern in both existing writers is
`File.WriteAllText(tmp)` → `File.Move(tmp, path, overwrite:=True)`
(`AppUserSettings.vb:207`, `SignalBridge.vb:377`). **`File.Replace` throws when the destination does
not exist**, which is precisely the first-write case and the ships-OFF→ON transition.
**Recommendation: implement the house pattern and amend §8.1's parenthetical to
"temp + atomic replace".** It is a wording defect, not a design one — but the contract is frozen, so
it needs the owner, and the engine seat gets a one-line relay.

**E2 — `executor.mode` must be an explicit MAP, not `.ToString()`.** `BridgeMode` is
`Off | LogOnly | Live` (`SignalBridge.vb:36`); the pinned wire strings are `"OFF" | "LOG_ONLY" |
"LIVE"` (§8.3). `.ToString()` emits `"LogOnly"` and silently violates the pin — and §8.3's T8
tolerance means the **engine takes the conservative arm without erroring**, so this fails
*invisibly*. Confirm: explicit `Select Case`, fixture-pinned.

**E3 — `position.size_usd` sign is NOT ESTABLISHED.** §8.4 requires it signed and
sign/direction-consistent. `positionSizeUSD` (`frmMainPageV2.vb:2796`) is only ever compared
`<> 0D` / `= 0D` at the sites read for this spec, so whether it carries a short's sign is unproven.
**The implementer must establish it from a real short position before writing the mapping** — and
say so in the impl report §3 either way. Do not assume.

**E4 — the accepted torn-read class was accepted for a DIFFERENT consumer.** The declaration
comment at `frmMainPageV2.vb:2795` is explicit: *"Written on the receive thread; UI buttons read
them (accepted `Decimal` torn-read class)."* A `Decimal` is 16 bytes and its read is not atomic, so
a reader on another thread can observe a half-updated value. **That was accepted when the reader was
a UI button — a human sees a briefly wrong number and it self-corrects on the next tick.** This
emitter publishes the same fields into a machine-consumed file that the engine treats as
**authoritative for position state** (§8.6: feedback-authoritative when governing), where a torn
`size_usd` or `avg_entry` is a plausible, well-formed, wrong number with no self-correction until
the next trigger. **Not necessarily a blocker** — the window is tiny and the heartbeat re-publishes
— but the risk calculus changed when the consumer changed, so it is the owner's call, not the
implementer's. Cheapest mitigation if wanted: take the snapshot of all four position fields under
the same lock the writer uses, or re-read and discard on mismatch.

**E5 — is `LOG_ONLY` allowed to emit at all?** §8.5 says emission is unconditional once enabled and
§8.4 says `mode` exists to distinguish the log-only soak from live — which implies yes. Confirm
explicitly, because a log-only executor emitting a real `position` block is exactly the case where
the engine could act on a position the bridge did not create. *(It is the owner's own manual
trading — T6 ticked that knowingly — but ticking it for LIVE is not the same as ticking it for a
soak.)*

---

## §1 — The writer: one worker, snapshot-then-write, never on the receive path

`RemoteNotifier.vb` is the **model to copy** (its header comment states the discipline and every
rule there applies here): fire-and-forget, fail-silent, inert without config, off the caller's
thread, one shared resource for the app's lifetime.

Binding differences from the notifier:

1. **Self-coalescing, last-wins** (§8.5) — *not* rate-limit-and-drop. Bursts collapse to the newest
   snapshot; the newest state must always win. A dropped notification is a missed alert; a dropped
   feedback write leaves the engine on a stale executor picture until the next trigger.
2. **Single writer.** One serialized worker; two concurrent writes to one path is the failure this
   design exists to prevent.
3. **🚨 Snapshot on the CALLER's thread, serialize and write on the worker.** The snapshot is a flat
   immutable value type — capture it, hand it over, never let the worker reach back into live state.

**🚨 The receive-path constraint is absolute.** The receive loop runs on a threadpool thread and
must never touch WinForms controls ([[receive-loop-threading]] — this caused an edit-flood storm).
Every field the snapshot reads must be a **backing field**, never a `TextBox.Text`:
`positionSizeUSD`, `positionAvgEntry`, `placedStopLossPrice` (`:350`), `manualTPval` (`:364`) all
qualify. **A single control read in the snapshot path is a review-blocking defect.**

## §2 — Config

`bridge.json` gains `feedback_output_path` (§8.2), alongside the existing payload path — the file
that "carries only what has no natural home on a form" (`SignalBridge.vb:58`). Default when the key
is present but blank: `C:\Dev\DeribitBridge\executor_feedback.json`.

**Absent key ⇒ fully inert:** no timer, no worker, no file. Mirror `RemoteNotifier.IsConfigured`
(`:45`) — one property, checked at every entry point. Log one startup line, `configured` or
`disabled`, matching the notifier's precedent. **The path is not a credential** — unlike `ntfy_url`
it may be logged.

## §3 — The snapshot → schema mapping (§8.3 is the shape; this is where it comes from)

### 3.1 Identity and process state

- **`executor.instance_id` — NEW STATE, does not exist today.** A GUID minted **once at order-app
  process start**. Do not confuse it with the disposition log's column 2, which is the **engine's**
  `instance_id` from the payload. §8.4's guarantee — *restart ⇒ new GUID with `armed`/`started`
  false by construction* — is what makes §6's restart-disarmed visible engine-side, so mint it at
  process start and never regenerate.
- **`feedback_id`** — monotonic per process, gaps legal. Start at 1.
- **`generated_at_utc`** — ISO-8601 UTC `Z`, invariant culture (v1 serialization pins carry over).
- **`executor.mode`** — see **E2**. `armed` = the local toggle, `started` = `_started`
  (`SignalBridge.vb:145`); the engine's own ARM is never echoed back.
- **`executor.breaker_tripped`** — no field exists. It is computed inline at `SignalBridge.vb:696`
  as `breaker > 0D AndAlso _host.SessionPnLUSD <= -breaker`. **Extract that expression to a pure
  shared seam and have BOTH the gate and the emitter call it** — the house pattern
  (`ShouldSend`, `EffectiveSizeUsd`), and it makes the value fixture-pinnable. Do not duplicate the
  expression.
- **`executor.ws`** — `IsWebSocketConnected` (`frmMainPageV2.vb:672`) → `"OK"` / `"DOWN"`.
  ⚠ **Do NOT import `"REST"` from v1's `health.ws`.** That is the engine→app direction; §8.3 pins
  this field to two values only.

### 3.2 🚨 `position` — and the flat trap, which is the defect this spec exists to prevent

§8.4: flat ⇒ `direction:"FLAT"` **+ zeros**.

**`positionAvgEntry` is deliberately RETAINED through the flat echo.** The declaration comment says
so outright (`frmMainPageV2.vb:2794`): *"position exists and RETAINS the just-closed basis on the
flat echo (close-P/L reads it)"* — corroborated at `:5214` and used in the close P/L computation at
`:5224`. It is *correct* that the field survives; it is the closing calculation's basis.

**So the emitter must NOT read `OpenPositionAvgEntry` (`:694`) unconditionally.** Gate on `IsFlat`
(`:712`, `positionSizeUSD = 0D`) and emit explicit zeros:

```
If IsFlat  ⇒  direction "FLAT", size_usd 0, avg_entry 0, working {0,0}
Else       ⇒  direction from the sign (E3), size_usd, avg_entry = positionAvgEntry, working as below
```

**Why this is the highest-stakes line in the spec:** §8.4 makes the `avg_entry` join *the slippage
record* — after `acted` for pair (X, N), the next non-flat `avg_entry` is signal N's achieved fill.
A retained `avg_entry` published on a FLAT executor gives the engine a **stale fill attributed to
the next signal**, and it is silent: the number is plausible, well-formed, and wrong. It would
survive a soak. *(This is the era's standing trap in a new place — a value that is correct for one
consumer and poison for another; cf. the placement log line that is not evidence of position size,
H-6 §5.8.)*

`working` = the resting stop/target, `0` = unset, informational: `placedStopLossPrice` and
`manualTPval`. Zeros-never-null for the suppressed numerics (§8.1).

### 3.3 `last_signal`

`null` until this executor process consumes its first payload. Identity is the **ENGINE's**
(`instance_id`, `signal_id`) — the soak-proven join key. `disposition` is the exact soak-stable
token, unmodified. `at_utc` = consumption time.

**🚨 The disposition-cardinality freeze extends here (§8.1):** `last_signal` is written **once, at
consumption**. A post-`acted` chase abort updates the host log's cancel REASON and **must not touch
`last_signal`** — the same rule that forbids a second disposition row. Hook the existing
consumption site, not the act site.

**Fill-window gap is legal and must not be papered over:** after `acted`, `position` stays FLAT
until the entry chase fills, and an abandoned chase leaves it FLAT. The engine never infers failure
from that window — so the emitter must not synthesize anything to close the gap.

## §4 — Emission triggers (§8.5)

(a) each disposition · (b) position open/close/size change · (b2) working-level stop/target change
*(engine refinement 3.1)* · (c) ARM/START/mode/breaker transition · (d) **~10 s heartbeat** ·
(e) a final write on graceful close.

- The heartbeat exists so the engine's staleness rule (`now − generated_at_utc > 35 s` ⇒
  `EXECUTOR STALE`) has something to measure. **Only when configured**, and it must not resurrect a
  disposed emitter.
- (e) is what makes **silence = dead executor** honest. It rides the existing shutdown path —
  ⚠ that path already persists all 11 geometry fields unconditionally (H-6 §5.4); **add to it, do
  not restructure it.**
- Coalescing must never starve the heartbeat: a burst collapses, but the clock still ticks.

## §5 — OrderCheck fixtures

Fixtures are the review's evidence, so pin the things that fail *silently*:

1. **Mode mapping** — all three enum values → exact pinned strings (**E2**).
2. **🚨 Flat snapshot** — a flat position with a **non-zero retained `positionAvgEntry`** ⇒
   `direction "FLAT"` and `avg_entry 0`. **This is the fixture that would have caught §3.2.**
3. **Non-flat snapshot** — direction/sign consistency both ways (**E3**).
4. **`last_signal` null** before first consumption; populated after; **unchanged across a simulated
   post-`acted` chase abort** (the cardinality freeze).
5. **`breaker_tripped`** seam: below / at / above threshold, and `breaker = 0` ⇒ never tripped.
6. **Serialization pins** — invariant culture on a decimal, ISO-8601 `Z`, zeros-never-null for a
   suppressed block, `null` for an absent object.
7. **Coalescing seam** — pure, last-wins: three snapshots in, newest out.

Keep the seams pure and side-effect-free, as `ShouldSend` (`RemoteNotifier.vb:103`) and
`EffectiveSizeUsd` are — that is what makes them fixture-pinnable at all.

## §Acceptance

1. **Gate green at each commit**; censuses re-run (H-6 §1).
2. **OFF-parity:** with no `feedback_output_path`, **no file is created**, and a diff of the app's
   observable behaviour against the pre-change build is empty. The disabled path must be
   byte-identical in effect.
3. **Emit-only rollout step (§8.7):** enable the path, drive one bridge act on the harness, then
   **inspect the file by hand** — `schema_version`, `feedback_id` monotonic, the engine's pair in
   `last_signal`, `position` matching what the app shows.
4. **🚨 The flat-trap acceptance, and it is the one that matters:** open a position, close it, then
   read the file **while flat**. `direction` must be `"FLAT"` and `avg_entry` must be `0` — with a
   *non-zero* `positionAvgEntry` still live in the app. **Log-only inspection cannot prove this**;
   it needs a real open→close cycle.
5. **Restart:** new `instance_id`, `feedback_id` restarts, `armed`/`started` false (§8.4).
6. **Heartbeat:** file `generated_at_utc` advances ~10 s with the app idle and flat.
7. **Graceful close** leaves a final write; **kill the process** and confirm the file simply goes
   stale — no partial/corrupt JSON (the atomic-write proof, **E1**).

**Not in scope, fenced:** phase-2 actionable exits (a future signal-schema-v2 amendment with a
pinned field — **never parse `hold_status`**), and anything engine-side.

## §Commits

1. §2 config + the inert-when-absent property (+ startup log line).
2. §3 snapshot + mapping + the `breaker_tripped` seam extraction.
3. §1 writer/worker + §4 triggers.
4. §5 fixtures.
5. Impl report — **§3 lists what was NOT established**, E3's finding stated either way.
