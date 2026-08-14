# Spec — make the `executor.ws` DOWN/OK edge AUDITABLE

**Origin:** the LONDON log-only session, 2026-08-13. A real disconnect occurred
(`Server closed connection - scheduling reconnect` → `Successfully reconnected`), so E6a's `ws`
DOWN edge almost certainly fired — **and nothing can prove it.** Filed at `ROADMAP-2026-08.md` §5.
**Status at writing:** open. Facts below verified in code on 2026-08-13 at HEAD `9f041fe`.
**Scope:** `DeribitOrderPlacementApp` — two sites in `frmMainPageV2.vb` plus one new writer.
**Ships:** ON. This is observability for an already-shipped field, not a new feature.

**One-line statement of the job:** `executor.ws` is written **only** into
`executor_feedback.json`, which is overwritten on every publish, so a DOWN→OK transition leaves no
trace once it has passed. Make each transition durable, without disturbing the receive path or the
frozen disposition contract.

---

## 🚫 Do-not-touch

| Thing | Where | Why |
|---|---|---|
| The two `PublishExecutorFeedback()` calls | `frmMainPageV2.vb:1426` (OK edge) · `:1523` (DOWN edge) | These are E6a as ruled and runtime-accepted. **Add beside them; never move, wrap or condition them.** |
| `bridge-dispositions.log` | `SignalBridge.EmitDisposition` | 🚨 **Cardinality is FROZEN: one row per payload** (`HANDOVER-6.md` §6.3). A `ws` row in that file breaks a soak-frozen contract. See §2.3. |
| `ExecutorFeedback.Publish` / `WriteAtomic` / `ShouldWrite` | `ExecutorFeedback.vb` | C1's emitter, reviewed and D1-fixed. The `ws` value itself is correct — this spec adds an audit trail beside it, not a change to it. |
| The reconnect branch | `frmMainPageV2.vb:1527-1533` | Live recovery machinery. |

**Standing rules that apply** (`HANDOVER-6.md` §7): a seat never places a trade and never arms the
bridge · **escalate a spec defect to the owner BEFORE implementing** · finding-ID convention `E` /
`D` / `SB` (§7bb) · the owner is the only pusher.

---

## §1 — The defect, precisely

### 1.1 What exists today

- `executor.ws` is serialised from the snapshot: `{"ws", If(snap.WsConnected, WsOk, WsDown)}`
  (`ExecutorFeedback.vb:322`).
- It is published at exactly two edges, per E6a: the **connect path** (`frmMainPageV2.vb:1426`) and
  the **receive-loop exit** (`:1523`, above the reconnect branch so it covers every way out).
- The emitter writes **one file, overwritten** — `executor_feedback.json`.

### 1.2 Why that is unauditable

- The heartbeat republishes every 10 s, so a DOWN state is overwritten by the next OK within
  seconds of reconnecting.
- **No host-log line and no file records the transition.** Grep confirms it: the only mentions of
  the `ws` value outside the emitter are the publish sites themselves.
- Result: on 2026-08-13 a genuine disconnect happened during a monitored session and **left no
  evidence at all**. The single hardest-to-provoke behaviour in C1's "still unobserved" list
  occurred and could not be recorded.

### 1.3 🚨 The trap that decides the design

**The host log does NOT persist.** `txtLogs` is a `RichTextBox` — screen only, gone when the app
closes. The only file writes from the log path are:

- `AutoTradeLog.txt` — a **narrow trade-event log**, one `AppendAllText` site
  (`frmMainPageV2.vb:5963`), not a general log.
- `crash.log` — the `ApplicationEvents.vb` backstop.

**So a gray log line alone does NOT satisfy this spec.** It gives live visibility and no audit. The
fix must write a file. *(Getting this wrong is the obvious way to build something that looks
finished and does not answer the question that prompted it.)*

### 1.4 Two different things called `ws` — do not conflate them

| Field | Meaning | Where |
|---|---|---|
| `executor.ws` | **OUR** socket to Deribit. **This spec's subject.** | `ExecutorFeedback.vb:322` |
| `p.WsHealth` | **THE ENGINE's** health, read from the payload; drives `refused: ws_down` | `SignalBridge.vb:701` |

---

## §2 — The fix

### 2.1 Log the TRANSITION, not the state

- Keep the last **logged** `ws` state in a backing field on the form.
- Write a record **only when the new state differs** from it.
- **Why:** the heartbeat republishes every 10 s. Logging state rather than transition produces
  ~8,600 identical lines a day and buries the one line that matters.
- ⚠ The two publish sites are edges *by construction*, but do not rely on that — a failed reconnect
  can re-enter the loop-exit path. **The field is what makes "transition" true, not the call site.**

### 2.2 Write BOTH, for two different readers

1. **An append-only file — `ws-edges.log`, beside the exe.** This is the audit trail and the point
   of the spec. House precedent for append-only, deliberately non-atomic logs:
   `bridge-dispositions.log`, `crash.log`, `AutoTradeLog.txt` (`HANDOVER-6.md` §6.10).
   Suggested row, one line, sortable, greppable:
   `2026-08-13T13:22:41Z | DOWN | <reason-if-known>`
2. **A gray host-log line**, via the existing `AppendColoredText`. Live visibility while watching a
   session.

### 2.3 Why NOT the disposition log

`bridge-dispositions.log` is the obvious-looking home and it is **forbidden**. Its cardinality is
**soak-frozen at one row per payload** (`HANDOVER-6.md` §6.3), the engine's join reads it, and a
`ws` row has no payload. Adding rows there breaks a frozen contract to save creating one file.

### 2.4 Threading — the part to get right, and the good news

- The DOWN site (`frmMainPageV2.vb:1523`) runs on a **threadpool thread**, not the UI thread. The
  house rule is absolute: **never touch a WinForms control from there** — it caused an edit-flood
  storm once already.
- ✅ **`AppendColoredText` is already safe to call from that thread.** Verified: it marshals via
  `Me.BeginInvoke` and guards the handle-not-created window. **Use it; do not hand-roll an Invoke.**
- The file append is direct IO and is thread-safe enough for an append-only log, but **it must never
  throw into the receive loop**. Wrap it in its own `Try` and swallow, exactly as `ExecutorFeedback`
  and `RemoteNotifier` do. **Telemetry must never be the reason the receive loop dies.**

### 2.5 Make the decision a pure seam

Put the transition rule in a function over plain data — `ShouldLogWsEdge(lastLogged, current)` or
equivalent — so it is provable with **no app, no socket and no disconnect**. This is the house
pattern (`ShouldSend`, `EffectiveSizeUsd`, `Select-BestMatchIndex`, `Merge-ProcessWindows`) and it
is what makes §Acceptance item 1 possible at all.

---

## §Acceptance

**Test the defect, not the theory** (`HANDOVER-6.md` §7 lesson 2), and **verify the instrument can
exist** before relying on it (lesson 1).

**1 — The pure seam, with no app running.** OrderCheck fixtures or a check script:
first-ever call (no prior state) · OK→OK suppressed · OK→DOWN logged · DOWN→DOWN suppressed ·
DOWN→OK logged. **This is the only acceptance that does not need a disconnect, so it carries the
logic.**

**2 — 🚨 A REAL disconnect, end to end.** Launch on TESTNET via `tools/launch-app.ps1`. Once
connected, **disable the network adapter for about 10 seconds, then re-enable it.** No trade, no
ARM, no START — this is a connectivity test only. Required afterwards:
- `ws-edges.log` contains **exactly two** new rows: `DOWN` then `OK`.
- The host log shows both gray lines.
- The app reconnected on its own — the existing recovery path is untouched.
- ⚠ **If you cannot provoke a disconnect, say so and stop.** Do not claim the acceptance on the pure
  seam alone. An unprovoked edge is exactly the state this spec exists to end.

**3 — No flooding.** Leave it connected for **five minutes** with the 10 s heartbeat running.
`ws-edges.log` must gain **zero** rows. This is the test that the transition rule works rather than
the state being logged.

**4 — Graceful close is not a DOWN.** Close the app normally. Per `frmMainPageV2.vb:1520-1522` the
final write latches the emitter disposed *before* the socket closes, so shutdown must **not** append
a spurious `DOWN` row. Assert the row count is unchanged.

**5 — Nothing else regressed.** `tools/checks/verify-gate.ps1` → `GATE PASSED`, OrderCheck
**268/268** (**+ your new fixtures if you add them there**). The nine `frmMainPageV2.vb` censuses →
**68 occurrences across 64 lines**. ⚠ They are **occurrence** counts; a count-mode grep returns 64
and reads as four missing. **You are editing `frmMainPageV2.vb`, so these can genuinely move** —
if they do, say which and why.

**6 — `.gitignore`.** `ws-edges.log` is operational output beside the exe. Confirm it is ignored,
like the other logs, and that `verify-gate.ps1`'s untracked-file guards still pass.

---

## §Commits

1. The pure seam + its fixtures. No behaviour change.
2. The writer and the two call sites.
3. The impl report.

Docs tracked and committed with the work. **The owner is the only pusher.**

**The impl report must carry:** the acceptance results including the real-disconnect evidence (paste
the rows), a numbered § answering the tiering question below, and an explicit *what was NOT
established* section.

---

## §Model and effort (`HANDOVER-6.md` §7b)

> **Recommendation: Opus, HIGH effort, fresh conversation.**

**Why — tied to what it touches, not to its size:**

- The DOWN site is **inside the receive path**, at the loop exit. `HANDOVER-6.md` §7a reserves
  Opus-HIGH for the order / SL / receive / bridge / act paths and calls it non-negotiable, because
  the failure modes there are silent.
- The edit runs on a **threadpool thread** in a form whose one absolute rule is *never touch a
  control from there*. That rule has already been broken once here, and it produced an edit-flood
  storm.
- It sits beside E6a, which is runtime-accepted machinery. Disturbing the publish ordering would
  falsify a field the engine displays as *"can the executor act"*.
- **The diff will look trivial. That is exactly why the tier is not.**

**Where the thinking should go:**

1. **§1.3 — the host log does not persist.** A seat that skips this ships a gray line and declares
   victory, having answered nothing.
2. **§2.4 — the failure must be swallowed.** A telemetry write that throws into the receive loop
   converts an observability gap into an outage.
3. **§2.1 — transition, not state**, and the field is what makes it true, not the call site.

**What would change the answer:** if it turns out the whole change can live outside
`frmMainPageV2.vb` — for example entirely inside `ExecutorFeedback`, where the value is already
derived and the class already owns its own fail-silent `Try` — then nothing touches the receive path
and **Sonnet, medium** would be right. **Investigate that first and say which you chose and why.**
It is the better design if it works.

**Answer in the impl report:** was the tier right? Name where the depth was needed, or say plainly
that it was not.

---

# ✅ §Coordinator review — 2026-08-14. APPROVED. No code defect.

Opus/HIGH per `HANDOVER-6.md` §7a. Commits `090d88c` · `f9a9b2d` · `8eb9390` · `e0587af`.
**All three spec-back findings are UPHELD, and all three are defects in THIS SPEC, not in the code.**

## R1 — Executed at the coordinator seat, not taken on report

| Check | Result |
|---|---|
| `tools/checks/verify-gate.ps1` | **GATE PASSED**, OrderCheck **289/289** (was 268 — the 21 new fixtures are real) |
| Nine `frmMainPageV2.vb` censuses | **68 across 64** — unchanged, **and they edited that file**, so this is a genuine pass |
| `frmMainPageV2.vb` deletions | **0**, against 64 insertions |
| Both `PublishExecutorFeedback()` calls | **untouched**; the new calls sit beside them |
| Receive-loop control flow | unchanged — `exitReason` is a local string, assigned in the exit arms, read once |
| **Negative test** | **5 fixtures fail** under `Return True` — 3, 5, **7**, 9, 10 |
| Runtime artefact | `ws-edges.log` exists with **exactly** the three quoted rows |
| `.gitignore` | line 376; no untracked leak |

⚠ **The negative test caught one more than predicted.** The review request named 3, 5, 9, 10;
fixture **7** ("an unknown current state is never logged") also fails, because replacing the whole
body removes the empty-state guard too. **Not a defect — the suite is *more* failable than claimed,
which is the safe direction** — but the prediction was reasoned rather than run.

## R2 — The design decision they asked to have challenged: they are right, and this spec was wrong

§Model-and-effort offered "entirely inside `ExecutorFeedback`" as the route that would drop the tier.
**That route is unreachable.** `Publish` returns on a single Boolean when `feedback_output_path` is
absent, so an emitter-hosted trail **would not exist on an unconfigured bin — absent exactly when
nobody is checking.** Verified independently at `ExecutorFeedback.vb:419`, `:442`, `:475`, `:578`,
and **demonstrated at runtime**: the test bin logged `Executor feedback: disabled` and the ws-edge
file wrote anyway. Rejecting the hatch was correct.

## R3 — Spec-back rulings

- **`SB1` UPHELD.** §Acceptance item 4 cited the emitter's disposed latch as the mechanism that stops
  a graceful close writing a DOWN. That latch lives inside `ExecutorFeedback` and cannot cover an
  external writer — and it is only ever set when the emitter is **configured**, so in the very
  configuration they tested it would never have been set at all. `isClosing` is the correct guard.
- **`SB2` UPHELD.** See R2.
- **`SB3` UPHELD, and this spec was two-thirds wrong.** §2.2 offered `bridge-dispositions.log`,
  `crash.log` and `AutoTradeLog.txt` as one "beside the exe" precedent. Verified: `crash.log`
  (`ApplicationEvents.vb:41`) and `AutoTradeLog.txt` (`frmMainPageV2.vb:6031`) use **bare relative
  names** and resolve against the **working directory**; only `bridge-dispositions.log`
  (`SignalBridge.vb:361`) uses `AppContext.BaseDirectory`. They followed the only correct one.

## R4 — Their five self-declared weak points, ruled

1. **`Append` inside the `SyncLock` on the receive thread — ACCEPTED, and the risk is smaller than
   they think.** The DOWN call site is at the receive-loop **exit**, after `Exit While` — the loop
   has already ended, so a stalled disk delays the **reconnect**, not message processing. The OK site
   runs before the receive task starts. Worst case is a bounded delay to connect, never a stall in
   message handling. With ~2 rows a session and no lock nesting, row ordering is the right trade.
2. **`isClosing` read unsynchronised — ACCEPTED.** It matches the pattern the reconnect branch two
   lines below already uses. Cost of a stale read is one spurious DOWN row; synchronising it would be
   novel machinery for a cosmetic gain.
3. **No rotation — ACCEPTED.** ~2 rows per session against `bridge-dispositions.log`'s 225 KB of
   per-payload rows. Decades from mattering.
4. **OK reason always `connect` — ACCEPTED.** The preceding DOWN row disambiguates. A synthetic
   first-vs-re-connect distinction could itself be wrong.
5. **Editing inside the receive loop for `exitReason` — WARRANTED, and it earned its place.** Four
   string assignments, no control flow touched, zero deletions. The captured DOWN row carries the
   real `WebSocketException` text; without it that row would read `socket no longer open` and say
   nothing.

## R5 — The best thing in the submission

**§6's first bullet, which they volunteered.** The ws DOWN edge **stays** on C1's unobserved list:
the *transition* is now evidenceable, the *emitter publishing* `"ws": "DOWN"` is not. They wrote that
distinction into `HANDOVER-6.md` §2 item 7 themselves — *"Two claims, one word: do not let 'the DOWN
edge was observed' collapse them."* **That is the discipline this repo runs on, applied against their
own result.** Verified present and correct.

**Nothing is owed. The three SB findings are folded in above.**
