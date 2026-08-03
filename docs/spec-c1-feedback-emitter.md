# Spec — C1: executor feedback emitter (ships OFF; contract §8 is the binding schema)

**Origin:** `ROADMAP-2026-08.md` §4 / `HANDOVER-6.md` §3 — the order app writes
`executor_feedback.json` so the engine can see the real executor state. Documentation phase closed
2026-07-29 (proposal → engine ACCEPT + 3 refinements → ack → trader tick T1–T8); **contract §8 is
canonical for emitter behaviour and this spec never restates it — it implements it.** The engine's
bridge-doc §10 is canonical for consumption/display and is NOT our concern.

**Recommended implementer — per H-6 §7b (advisory; the owner picks): Opus 5, HIGH effort, fresh
conversation.**

*Why, tied to what it touches rather than its size:* the new file is the easy half and Sonnet could
write it from §3 alone — the schema is fully pinned, `RemoteNotifier.vb` is a named model, and the
atomic write is a two-line house idiom. **The hard half is that §4's triggers must be hooked into
`frmMainPageV2.vb`'s receive-thread echo handling** — the 6,881-line god-form on the LIVE trading
path, whose own history is a cross-thread fix and an edit-flood storm. That is the standing
receive-path tier, and it decides this.

*Where the thinking should go — three places a competent implementer moving fast gets wrong:*

1. **§3.2's flat trap.** The retention of `positionAvgEntry` is *deliberate* and close-P/L depends
   on it. The pattern-matched move — "flat should zero the field" — edits the wrong thing and breaks
   the close calculation. The emitter must read **around** it, never fix it.
2. **§4's heartbeat.** The obvious implementation reads live state; the spec forbids exactly that.
   It is counter-intuitive by design (E4).
3. **Fixture 8 / trigger completeness.** Requires finding *every* write site across **two** writer
   threads (~14 assignments). Under §4's heartbeat rule a missed hook is permanent, not
   self-correcting — this is the one place where "mostly wired" is a real defect.

*What would change the answer:* if the triggers turn out to be reachable through an existing
position-change seam — so that no edit lands inside the receive-thread echo handlers — this drops to
**Sonnet, medium**. The implementer should say so in the impl report if they find such a seam; it
would also be worth having for its own sake. Commit 4 (fixtures) is mechanical in isolation, but
"one implementer at a time" means it is not worth splitting.

**Review is Opus regardless** (H-6 §7a) — it is the adversarial pass.
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

## ✅ §0 — Escalations: ALL RULED 2026-08-02. No open questions; implement as ruled.

**E1/E2/E3/E5 owner-ticked 2026-08-02; E4 resolved against the engine's canonical mirror.** The
spec is READY FOR AN IMPLEMENTER SEAT — the owner launches it. Kept in full rather than collapsed to
"approved", because three of the five carry obligations that outlive the ruling: **E1**'s engine
relay, **E3**'s verify-before-you-map, and **E4**'s revisit trigger. Raising all five before any
code was written is the standing rule working (`memory: spec-defect-escalation` / H-6 §7).

**E1 — ✅ RULED 2026-08-02: amend the wording, implement the house pattern.** The contract is
**already amended** — §8.1 now reads *"atomic (temp + atomic replace)"*, logged in its §9 version
history. ⚠ **Owner action still owed: the one-line relay to the engine seat**, whose §10.2 repeats
the old wording; it is a coordinated docs note, not a schema bump. *(Original finding below.)*

Contract §8.1 said *"atomic (temp + `File.Replace`)"*. The house pattern in both existing writers is
`File.WriteAllText(tmp)` → `File.Move(tmp, path, overwrite:=True)`
(`AppUserSettings.vb:207`, `SignalBridge.vb:377`). **`File.Replace` throws when the destination does
not exist**, which is precisely the first-write case and the ships-OFF→ON transition.
**The engine's mirror repeats it** — `signal-bridge-v1-proposal.md` §10.2 also says
*"atomic (tmp + `File.Replace`)"* — so the correction is a **coordinated docs note on both sides**,
the established pattern for this.

**Recommendation: implement the house pattern and amend §8.1's parenthetical to "temp + atomic
replace".** Scale it honestly: `File.Replace` is *usable* with a first-write fallback, so this is a
wording tidy rather than a broken contract — but the contract is frozen, so it is still the owner's
ruling plus a one-line relay, not something the implementer quietly works around.

**E2 — ✅ RULED 2026-08-02: explicit map, fixture-pinned. Confirmed as specced.** `BridgeMode` is
`Off | LogOnly | Live` (`SignalBridge.vb:36`); the pinned wire strings are `"OFF" | "LOG_ONLY" |
"LIVE"` (§8.3). `.ToString()` emits `"LogOnly"` and silently violates the pin — and §8.3's T8
tolerance means the **engine takes the conservative arm without erroring**, so this fails
*invisibly*. Confirm: explicit `Select Case`, fixture-pinned.

**E3 — ✅ RULED 2026-08-02: the obligation stands — establish it, do not assume it.** The tick
approves the *approach*; it does not supply the fact. The sign is still **NOT ESTABLISHED** and the
implementer must prove it from a real short before writing the mapping, and record the result in
impl report §3 either way. §8.4 requires it signed and
sign/direction-consistent. `positionSizeUSD` (`frmMainPageV2.vb:2796`) is only ever compared
`<> 0D` / `= 0D` at the sites read for this spec, so whether it carries a short's sign is unproven.
**The implementer must establish it from a real short position before writing the mapping** — and
say so in the impl report §3 either way. Do not assume.

**E4 — position-snapshot coherence. RESOLVED for phase 1; the REVISIT TRIGGER is binding.**

*The issue.* The four snapshot fields are written from **two threads**. `SyncTradeInputsFromUi`
(`frmMainPageV2.vb:515`) sets `manualTPval` from `txtManualTP.Text` — UI thread by construction —
while `:3389` resets the same field on the **receive** thread (*"Cross-thread fix: reset engine
fields synchronously, mirror the controls via UiInvoke"*); `positionSizeUSD` / `positionAvgEntry`
are receive-thread only (`:2909`, `:5657`, `:5659`). The declaration comment (`:2795`) already names
the accepted `Decimal` torn-read class — accepted because *"UI buttons read them"*, i.e. a human
sees a briefly wrong number that self-corrects on the next tick.

**Cross-field incoherence is the bigger half, not the 16-byte tear.** Even with atomic `Decimal`s,
reading four fields one at a time can mix pre- and post-update values — `size_usd` from before a
close with `avg_entry` from after. That is ordinary interleaving, far likelier than tearing, and it
lands exactly where §3.2 warns.

*The exposure is narrower than it first looks.* §1 snapshots on the **caller's** thread, and trigger
(b) fires from the receive thread that just wrote those fields — so the position-change path is
sequential and already coherent. The residual race is only the ~10 s heartbeat, UI-originated (b2)
triggers, and the graceful-close write.

*Why it is resolved.* The engine's canonical mirror settles the consequence.
`C:\Dev\DeribitVerdictEngine\docs\signal-bridge-v1-proposal.md` **§10.3**: *"Per-run **fresh read**
at `RunAnalysisAsync` start (no watcher)"* — the engine holds no stored copy. **§10.4**: phase 1 is
the live-status tier ONLY — *"**NO snapshot line, NO card binding, NO CSV column, NO payload field
changes**"*. **So nothing durably records `avg_entry` in phase 1**, and the only value that governs
anything is `direction` → `PositionState`. A mixed snapshot is a briefly wrong number on a status
strip, gone on the next run. *(This also settles what §8.4's "the `avg_entry` join is the slippage
record" means — a reconstruction method for a human, not an automated write. T7 agrees: a per-signal
achieved-entry field is a **v2.1 amendment**, not a phase-1 field.)*

**Ruling taken: ACCEPT the class for phase 1, and take the free half — §4's heartbeat rule.** No
lock and no second representation of position state: the alternative would put new code in the live
receive path of a LIVE trading app to serve a feature that **ships OFF**, and buys nothing while no
consumer records anything.

🚨 **BINDING REVISIT TRIGGER — this ruling expires on its own terms.** The exposure returns the
moment ANY consumer **records** the join instead of re-deriving it: a CSV column, a card binding, a
stored achieved-entry, or the T7 / v2.1 per-signal field. **If any of those land, re-open E4 BEFORE
implementing them.** The mitigation then is an **immutable snapshot object published by reference**
(reference assignment is atomic, so it is coherent by construction) — not a lock.

*(Withdrawn: the earlier suggestion here to "snapshot under the same lock the writer uses" was
wrong. There is no single writer, so it would mean taking a lock at ~14 assignment sites, several
in the hot receive path.)*

**E5 — ✅ RULED 2026-08-02: YES, `LOG_ONLY` emits.** Emission is unconditional once enabled (§8.5);
`mode` exists precisely to tell the soak from live (§8.4), and the emitter never gates on it.

The concern raised was that a log-only executor publishes a real `position` block — the case where
the engine could act on a position the bridge did not create. **What actually contains that is the
rollout ladder, not mode-gating** (§8.7): OFF → emit-only with the file inspected by hand → engine
display consumption → soak. Engine consumption ships `enabled: false`, so nothing consumes during
emit-only; by the time it does, the ladder has cleared the step deliberately. Mode-gating would have
bought nothing and would have made the soak unable to exercise the emitter — the exact "acceptance
runs in a mode that cannot exercise the failure surface" trap the N2 §1 find is named for.

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

- **🚨 (d) the heartbeat REPUBLISHES the last snapshot with a fresh `generated_at_utc` and
  `feedback_id`. It does NOT read live state** (E4). Its job is the engine's staleness rule
  (`now − generated_at_utc > 35 s` ⇒ `EXECUTOR STALE`), which is a **liveness proof, not a data
  refresh** — every actual change to the four fields already fires (b) or (b2) *from the thread that
  wrote it*. This removes the main coherence race at zero cost and adds no live-path code.
  **Only when configured**, and it must not resurrect a disposed emitter.
  ⚠ **The trade this makes:** a MISSING trigger stops being self-correcting. Today a forgotten hook
  would be papered over by the next heartbeat within 10 s; under this rule it republishes stale data
  indefinitely. **Fixture 8 is what makes that trade safe** — do not adopt this rule without it.
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
8. **🚨 Trigger completeness — the fixture that makes §4's heartbeat rule safe.** For EACH of the
   four snapshot fields, mutating it must produce a new published snapshot. Assert per field, not in
   aggregate: aggregate passes while three of four are wired. Under §4 (d) the heartbeat no longer
   papers over a missing hook, so this fixture is the only thing standing between a forgotten
   trigger and the engine reading one stale value forever.

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
