# Impl report — C1: executor feedback emitter (`executor_feedback.json`)

**Spec:** `spec-c1-feedback-emitter.md`. **Schema:** `integration-contract-verdictengine.md` §8 —
binding, and the spec implements it without restating it, so §8 governed every field decision here.
**Seat:** Opus 5, HIGH effort, fresh conversation (the spec's own recommendation; see §6 for whether
that was right in hindsight).

**Status: code COMPLETE and gate-green; SHIPS OFF. Every line of the spec is implemented —
E6a and E6b were ruled 2026-08-03 and landed in commits 6 and 7 (§8 is the addendum).** Acceptances
1 and 2 are done and evidenced below. Acceptances 3–7 need a running app and in places ARM/START, so
they are written up as a run sheet (§7) rather than driven — the seat places no trades and arms no
bridge. Nothing is owed by this seat; the next step is the coordinator's adversarial review.

⚠ **Read §8 before §3–§7.** The addendum supersedes four statements in them, and each superseded
statement is struck at its own site rather than left to be discovered — a doc is not evidence about
a doc, and the staleness is recursive (H-6 §7b lesson 6).

## 1. What landed

| Commit | Sha | Content |
|---|---|---|
| 1 | `c27951b` | §2 config: `feedback_output_path`, `IsConfigured`, the startup line |
| 2 | `e5156a1` | §3 snapshot + §8.3 mapping + the `breaker_tripped` seam extraction |
| 3 | `2b1b565` | §1 writer/worker + §4 triggers (a)/(b)/(b2)/(c)/(d)/(e) |
| 4 | `3366feb` | §5 fixtures 1–8 — OrderCheck **173/173 → 227/227** |
| 5 | `91c8bc2` | impl report |
| 6 | `5c2d6ed` | **E6a + E6b as ruled** — the `ws` trigger (both edges) and the initial write (f) |
| 7 | `dd43e59` | **fixture 8 rescoped to the whole §8.3 domain** + the 8b domain check — **227 → 264** |
| 8 | this | report addendum (§8), and the §7.0 caveat dropped |

New file `ExecutorFeedback.vb`. Touched: `SignalBridge.vb`, `frmMainPageV2.vb`,
`bridge.example.json`, `tools/OrderCheck/Program.vb`.

**The emitted document, captured from the serializer** (a temporary print in the harness, run once
and removed before commit 4 — it is not in the repo). Field-for-field and in-order against §8.3:

```json
{
  "schema_version": 1,
  "feedback_id": 587,
  "generated_at_utc": "2026-08-05T09:14:02Z",
  "executor": {
    "instance_id": "exec-guid",
    "app": "DeribitOrderPlacementApp",
    "mode": "LOG_ONLY",
    "armed": true,
    "started": false,
    "breaker_tripped": false,
    "ws": "DOWN"
  },
  "instrument": "BTC-PERPETUAL",
  "position": {
    "direction": "SHORT",
    "size_usd": -250.0,
    "avg_entry": 59012.5,
    "working": { "stop": 59062.5, "target": 58930.0 }
  },
  "last_signal": {
    "instance_id": "9f0c-engine-guid",
    "signal_id": 1234,
    "disposition": "acted (id 55)",
    "at_utc": "2026-08-05T09:13:41Z"
  }
}
```

### The three things the spec was built around

**§3.2's flat trap.** `positionAvgEntry`'s retention through the flat echo is untouched — verified
against the declaration comment (`frmMainPageV2.vb:2880`) and its consumer, the close-P/L basis at
`:5336`. The emitter reads *around* it: `BuildSnapshot` gates on `rawSizeUsd = 0D` (the same
predicate as `IsFlat`) and emits explicit zeros for direction, size, avg entry **and both working
levels**. Fixture 2 pins it with a live retained basis of 59012.5.

**§4's heartbeat.** Republishes `_lastPublished` with a fresh `feedback_id`/`generated_at_utc` and
never reads live state. The comment at the site says why, at length, because the obvious
implementation is the wrong one. It also cannot resurrect a disposed emitter, and it bypasses the
content gate deliberately — routing it through would make it a no-op by construction.

**Fixture 8 / trigger completeness.** Two assertions per field (survives the content gate **and**
reaches the wire), plus a survives-coalescing case, plus the gate's other half (an unchanged
snapshot must publish nothing). What it cannot do is in §3.

**The absolute:** the snapshot touches no WinForms control. `CaptureFeedbackSnapshot` reads
`positionSizeUSD`, `positionAvgEntry`, `placedStopLossPrice`, `manualTPval` and
`IsWebSocketConnected` — all backing fields or a socket-state read — plus five plain field reads off
`SignalBridge`. No `.Text`, no control property, anywhere in the publish path.

### Design decisions worth the reviewer's attention

- **A content gate, not per-tick writes.** `SameContent` compares everything the file carries except
  `feedback_id`/`generated_at_utc`; an identical snapshot writes nothing. This is what lets triggers
  sit directly on per-echo handlers — `HandleBalanceUpdates` fires on every portfolio echo but only
  a `breaker_tripped` *flip* reaches disk. It is also the component that could silently swallow a
  field, which is precisely what fixture 8 targets.
- **Method-tail hooks where the method allows it, adjacent hooks where it does not.** For
  `HandleOrderPositionUpdates` the tail is not just convenient, it is the *coherent* choice: the
  whole echo is applied before anything publishes, so the position block can never go out with a new
  size against an old avg entry — E4's cross-field incoherence, the bigger half of that class.
  Two sites could not use a tail and say so in their comments: `HandlePlacementResponse`'s rollback
  arm `Return`s, and `HandleQuoteUpdates` runs per quote tick so only its SL-chase branch is hooked.
- **`last_signal` is an immutable object published by reference.** It is read on threads that did not
  write it, so a struct copy could mix an old `signal_id` with a new disposition. Reference
  assignment is atomic — E4's own named mitigation, cheap here because it costs one allocation per
  consumed payload rather than one per tick.
- **The graceful-close write is synchronous.** A queued write would race process exit and simply be
  lost. It is bounded (one local file write), sits in its own `Try`, and is taken **before** the
  bridge is disposed and the socket closed, so the final snapshot is the executor's last true state
  rather than a torn-down one. It was **added** to `FormClosing`, which was not restructured
  (H-6 §5.4: that handler persists all 11 geometry fields unconditionally).

### Trigger sites — the enumeration, because "mostly wired" is the defect class

Every write site of the four snapshot fields, and the hook that covers it. **17 assignments across
10 methods; all 10 methods carry a hook.**

| Method | Field writes | Hook |
|---|---|---|
| `SyncTradeInputsFromUi` | `manualTPval` (:518) | tail |
| `HandlePlacementResponse` | `placedStopLossPrice` (:1829) | in the rollback arm (it `Return`s) |
| `HandleQuoteUpdates` | `placedStopLossPrice` (:2559) | at the SL-chase branch (per-tick method) |
| `HandleOrderPositionUpdates` | `positionSizeUSD` (:2995), `positionAvgEntry` (:2998), `placedStopLossPrice` (:3158, :3173, :3309), `manualTPval` (:3476) | tail — one publish per echo, coherent |
| `ExecuteOrderAsync` | `placedStopLossPrice` (:4035) | tail |
| `CancelOrderAsync` | `placedStopLossPrice` (:4113) | tail |
| `CancelWorkingEntryCoreAsync` | `placedStopLossPrice` (:4241) | tail |
| `StopLossForTrailingOrderAsync` | `placedStopLossPrice` (:4968) | tail |
| `HandleOpenOrdersSnapshot` | `placedStopLossPrice` (:5707, :5722) | tail |
| `ProcessPositionData` | `positionSizeUSD` (:5773), `positionAvgEntry` (:5775) | tail |

Plus, for state that is not one of the four: `SignalBridge.Mode` / `LocalArmed` / `TryStart` /
`ForceStop` (trigger c), `SignalBridge.EmitDisposition` (trigger a), `HandleBalanceUpdates` tail
(the breaker half of trigger c), the heartbeat timer (d), and `FormClosing` (e).

**Every method using a tail hook was checked for a `Return` between its first snapshot-field write
and the tail; none has one, and each site's comment records that check.** This is the claim most
worth re-deriving in review — it is the one thing standing between a forgotten hook and a
permanently stale field, and a unified diff cannot show it (H-6 §7b lesson 3: open the file).

## 2. Acceptances 1 and 2 — done, with evidence

**1. Gate green at each commit, censuses re-run.** `GATE PASSED` after every one of commits 1–4.
OrderCheck **173/173** at commits 1–3 (no new fixtures yet), **227/227** at commit 4 — +54.

The nine censuses, `frmMainPageV2.vb`-scoped, **at baseline `a7da7e4` and again at every commit**:

| symbol | H-6 §1 | here |
|---|---|---|
| `emergencyFired` | 10 | 10 |
| `IsATRSlippageExcessive` | 8 | 8 |
| `NextSlBackoff` | 2 | 2 |
| `RecordCommandedSLPrice` | 3 | 3 |
| `slUpdateFailures = 0` | 1 | 1 |
| `TakerFeeRate` | 0 | 0 |
| `isPlacingOrder` | 13 | 13 |
| `lastPlacementAdmittedUtc` | 13 | 13 |
| `placedOrderSizeUsd` | 18 | 18 |

**All nine unchanged; 68 occurrences across 64 lines, as H-6 §1 states.** Nothing this pass added
touches any of those symbols, in code or in prose. (Counted with `grep -o | wc -l` — occurrences.
A count-mode `grep -c` returns 64 and reads as four missing; that is the documented trap, not drift.)

**2. OFF-parity.** With no `feedback_output_path`, `ResolveOutputPath` returns `""`, `IsConfigured`
is `False`, and every entry point returns on that one Boolean:

- `PublishExecutorFeedback` returns before capturing anything — no snapshot, no allocation, no queue.
- `StartHeartbeat` returns before creating the timer — **there is no timer at all**, not an idle one.
- `Publish` / `ShutdownWithFinalWrite` return on the same check, so `WriteAtomic` is unreachable —
  **no file and no `.tmp` file is ever created**, which is contract §8.5's *file absent = feature OFF*.

Three residues on the disabled path, stated rather than glossed, none observable in behaviour:

1. One extra read of `bridge.json` at startup (`ExecutorFeedback.LoadConfig`) and one extra grey log
   line, both by design (§2 requires the line).
2. One GUID minted per process by the shared initialiser, on first touch of the class.
3. `SignalBridge.EmitDisposition` allocates one `LastSignalRef` per consumed payload regardless of
   configuration. Deliberately **not** gated on `IsConfigured`: gating it would create a second
   state machine over the same fact for no gain, and one small allocation per payload is not a
   behaviour. Flagged here so the reviewer rules on it rather than discovers it.

The `breaker_tripped` extraction is behaviour-identical: `IsBreakerTripped(breaker, pnl)` is
`breaker > 0D AndAlso pnl <= -breaker`, the gate-4.6 expression moved verbatim, now called from both
sites. Fixture 5 pins all four arms including `breaker <= 0` meaning "off".

## 3. What was NOT established

**This section is the point of the report. Everything below is a limit of the evidence, not a
summary of it.**

### E3 — `position.size_usd` sign: **ESTABLISHED. Signed, NEGATIVE on a short.**

The spec's ruling was explicit that the tick approved the *approach*, not the fact, and that the
sign had to be proven from a real short before the mapping was written. It is now proven, and **not
from the declaration comment** — that comment (`:2879`, "signed USD (+long / -short)") is the claim
under test, not evidence for it.

*The instrument.* `SendReduceMarketOrderAsync` (`frmMainPageV2.vb:6292`) derives the reduce
direction from the position model alone: `direction = If(posSize > 0D, "sell", "buy")`,
`amount = Math.Abs(posSize)`, and it prints a **loud orange `Position model empty — using
TradeMode/txtAmount fallback`** line whenever the model is unseeded. So a reduce line with **no
orange predecessor** is a direct readout of `positionSizeUSD`'s sign and magnitude. The log text
comes from `SendReduceOrderAsync:4320`,
`$"{orderType.ToUpper()} {direction} {amount} {...}"`.

*The evidence — two independent real SHORT positions, both testnet, both owner-driven:*

1. **Trade #99, 2026-07-31** (`runtime-record-sl-backoff-2026-07-31.md`) — **SHORT 10 @ 64792.02**.
   Log: `Reduce-only MARKET buy 10  order sent.`, with no fallback line anywhere in the transcript.
   `direction = "buy"` ⇒ `posSize > 0D` was **False**; the non-fallback branch was taken ⇒
   `posSize <> 0D`; `amount = Math.Abs(posSize) = 10`. Therefore **`positionSizeUSD = −10`**.
   The record independently notes that line's *double space*, which is exactly what that format
   string emits when `isMarketOrder` is true — corroborating that the line came from this site.
2. **Trade #68, 2026-07-14** (`runtime-checklist-hybrid-session.md:20`) — **SHORT 10 @ 64007.5**,
   log signature `Cancelled → Reduce-only MARKET → Emergency Buy Market Order Executed`. Same
   direction on an independent short.

*The long half, for the other direction:* `review-placement-debounce.md:24` and
`review-risk-sized-bridge-trades.md:45` both record `Reduce-only MARKET sell 10` on long positions.

*A second, independent argument from the exchange rather than from a log:* a `reduce_only` order on
the **wrong** side has nothing to reduce and is rejected. In #99 the reduce filled and the position
closed (`Position reduced at 64839.49`, `Loss of: $0.01`). Had the sign been positive on a short,
the app would have sent a reduce-only **sell** into a short and closed nothing.

*The mapping written on that basis:* `BuildSnapshot` derives `direction` and `size_usd` from the one
signed value, so §8.4's sign/direction consistency guarantee is structural. `positionSizeUSD` is the
exchange's `positions[].size` verbatim at both write sites (`:2995`, `:5773`) with no sign transform
anywhere in between.

**The residual, stated honestly:** both proofs are of a **size-10** short on **testnet**, read from
transcribed logs rather than from a live capture taken by this seat. What is unproven is nothing
about the sign; it is that no *third* code path rewrites `positionSizeUSD` — and that is closed by
inspection instead (the field has exactly two assignment sites, both quoted above).

### Not established — the rest

1. **Nothing in §7's run sheet has been executed.** Acceptances 3, 4, 5, 6 and 7 are **unproven**.
   In particular the flat-trap acceptance (4) — the one the spec calls the one that matters — is
   covered by fixture 2 at the seam level and by **nothing at runtime**. A fixture proves the mapping;
   it cannot prove that the mapping is what the running app feeds.
2. **Fixture 8 cannot prove a call site exists.** It proves that a change to each field survives the
   content gate, reaches the wire, and survives coalescing. It cannot execute `frmMainPageV2` and so
   cannot detect a hook deleted from a handler. Call-site completeness rests on the enumeration in
   §1 and on the reviewer re-deriving it — not on the harness. This is the single most important
   limit in this report, because §4(d) makes a missing hook permanent.
3. **No concurrency was exercised.** The single-writer discipline, the drain loop, the coalescing
   under real contention, and the `_gate`/`_writeGate` ordering are argued from the code, not
   measured. No test drove `Publish` from two threads.
4. **The atomic write was never observed mid-crash.** Acceptance 7's kill-the-process half is
   unrun. The claim rests on the house pattern being unchanged from `AppUserSettings:207` /
   `SignalBridge.PersistState`, which are themselves soak-proven — not on a new observation.
5. ~~**`executor.ws` has no trigger** (E6a, unruled).~~ **CLOSED by commit `5c2d6ed` — see §8.**
   `ws` is now published at both edges and is no longer a permanently-stale field.
6. ~~**No file is written on a fresh idle app** (E6b, unruled).~~ **CLOSED by commit `5c2d6ed` —
   see §8.** Trigger (f) writes one snapshot at start when configured.
7. **No engine-side behaviour was verified.** Nothing in this pass ran, read, or touched
   `C:\Dev\DeribitVerdictEngine` (read-only by standing rule). Consumption is the engine's mirror
   doc's business and phase-1 ships `enabled: false`.
8. **The `.tmp` sibling file.** `WriteAtomic` writes `executor_feedback.json.tmp` beside the target
   before moving it. Not a defect and it is the house pattern, but the owner should expect to see
   that name if a write ever fails mid-flight, and the engine must not be pointed at it.

## 4. E1's ruling as implemented

Contract §8.1 as amended (*"atomic (temp + atomic replace)"*) is implemented as
`File.WriteAllText(tmp)` + `File.Move(tmp, path, overwrite:=True)` — the house pattern from
`AppUserSettings.vb:207` and `SignalBridge.PersistState`. `File.Replace` is **not** used and would
have thrown on the first write and on the OFF→ON transition, which is the whole reason E1 was
raised. **The owner's cross-repo relay of that wording to the engine's `signal-bridge-v1-proposal.md`
§10.2 remains owed** (H-6 §2 item 5); nothing in this pass could carry it.

## 5. Escalation E6 — RULED 2026-08-03, IMPLEMENTED in commit `5c2d6ed` (§8)

**Both arms ruled as recommended (A1 and B1) and both are now in the code. This section is kept as
the record of what was raised and why, not as an open item** — the ruling and the implementation are
in §8. The original text follows.

Both are gaps in §4's trigger list, both are the same shape (a schema field no trigger can change),
and both only became defects when §4(d) ruled the heartbeat republishes instead of re-reading.
Full text was handed to the owner for relay; summarised here so this report stands alone.

- **E6a — `executor.ws` has no trigger.** A WS drop publishes nothing, so the emitter heartbeats
  `"OK"` while disconnected, permanently. Contract §8.5's list has the same gap, so it is a
  coordinated docs question. *Implementer's recommendation:* add a ws-transition trigger at the
  connect and receive-loop-exit sites — cold, rare, additive emission, reversible in one line.
- **E6b — nothing publishes at startup.** On a fresh idle flat app no trigger fires and the
  heartbeat has no snapshot to republish, so no file is created — indistinguishable from the feature
  being off. §Acceptance 5 and 6 both presuppose a file that §4 never produces (H-6 §7b lesson 1).
  *Implementer's recommendation:* one initial publish at startup when configured, on the UI thread,
  right after the bridge is constructed — §4 gains an (f) mirroring (e).

~~**Neither is implemented.**~~ **Both are, as of `5c2d6ed`.** The insertion points identified above
turned out to be the ones used, so each ruling did land as a one-line change rather than a rework.

## 6. The question the spec asked: was Opus-HIGH right?

The spec said this would drop to **Sonnet, medium** *"if the triggers turn out to be reachable
through an existing position-change seam — so that no edit lands inside the receive-thread echo
handlers"*, and asked the implementer to say so either way.

**No such seam exists, and the Opus-HIGH tier was correct.** The four fields have **17 assignments
across 10 methods on two threads**, and edits necessarily landed inside `HandleQuoteUpdates` and
`HandleOrderPositionUpdates`. The three near-miss candidates and why each fails:

- `CompletePositionClose` — fires on close only, never on open or a size change.
- `SignalBridge.NotifyPositionClosed` — same, and it is downstream of the model rather than of it.
- The receive-loop dispatcher (`ReceiveWebSocketMessagesAsync:1353`) — one tempting site covering
  every socket-driven write, but `HandleQuoteUpdates` and `HandleOrderPositionUpdates` are
  `Async Sub`, so the dispatcher returns at their first `Await` and a hook there would publish
  *before* their post-await writes. It would look complete and be wrong on exactly the two hottest
  handlers.

**A `PositionModelChanged` seam would be worth having for its own sake** — it would collapse this
table to one hook and make the completeness argument structural instead of enumerated. Recorded as
an observation for the backlog, not proposed as a change here.

## 7. RUN SHEET — acceptances 3–7 (owner-driven)

**The seat drove none of this.** Every step below is the owner's: all trade decisions, ARM and
START. Written to be executable without the implementer.

### 7.0 Preconditions — do these in order, they have each bitten before

1. **Rebuild x64.** The gate is AnyCPU-only and does **not** build the x64 bin the owner runs.
2. **Re-read `Environment` in the x64 bin's `secrets.json`, and confirm the window title ends
   `— TESTNET`.** Not decidable from file timestamps — `PreserveNewest` stamps the copy with the
   source's mtime, so a copy and a skipped copy look identical (H-6 §4).
3. **Back up `orderapp-settings.json` from the x64 bin.** `FormClosing` persists all 11 geometry
   fields unconditionally, so any runtime fiddling overwrites real trading values on exit — in the
   same file that holds the session policy and the breaker (H-6 §5.4). Restore it afterwards and
   **verify by relaunching and reading a box back.**
4. **Enable the emitter** in the **x64 bin's** `bridge.json` (not the AnyCPU one, and not the
   repo template):
   ```json
   "feedback_output_path": "C:\\Dev\\DeribitBridge\\executor_feedback.json"
   ```
   A present-but-blank value resolves to that same default. Confirm at startup: the log must read
   `Executor feedback: configured - C:\Dev\DeribitBridge\executor_feedback.json`.
5. Remember the disposition log you will want to cross-check is the **x64 bin's**
   `bridge-dispositions.log` (H-6 §5.11).

> ✅ **The E6b caveat is GONE** (it said: tick ARM once to force a first write). Trigger (f) now
> writes one snapshot at start, so **the file must exist as soon as the app is up and configured,
> with the app idle and flat and the bridge Off.** That is itself the first observation to make:
> if `executor_feedback.json` is absent after a configured start, that is a defect — there is no
> longer any workaround standing between you and it. Acceptances 5 and 6 now have a real instrument.

### 7.1 Acceptance 3 — emit-only, hand inspection

Engine-side consumption is not involved and must stay off.

1. **STOP the engine.** Then run the payload harness. Then `restore-payload.ps1`. Then **RESTART the
   engine.** Restore is mandatory, not cleanup — with the engine stopped, `write-payload.ps1`
   defaults to the LIVE payload path and clobbers `C:\Dev\DeribitBridge\verdict_signal.json`
   (H-6 §5.2).
2. **Patch `exec_resolution_min` to 15 in the payload before START** (~37 min of freshness).
   At the emitted `1` the window is 2.5 minutes — shorter than a human round-trip to START, and
   `[BRIDGE] auto-STOP: stale payload` fires (H-6 §5.10).
3. **Check ATRSlip** (`chkMaxSlippageATR`). `TryStart` refuses without it, and unchecked also
   silently disables both chase-abort arms (H-6 §5.5).
4. Write → START → write (the payload must LAND while started; START does not re-evaluate what is
   already on disk — H-6 §5.1).
5. Drive **one** bridge act, then open `executor_feedback.json` and check:
   - `schema_version` is `1`;
   - `feedback_id` has increased since the previous read (monotonic; gaps are legal);
   - `last_signal.instance_id` / `signal_id` are the **engine's** pair for that payload, matching
     the x64 `bridge-dispositions.log` row, and `disposition` is that row's token **character for
     character**;
   - `position` matches what the app's own display shows;
   - `executor.mode` is `"LIVE"` or `"LOG_ONLY"` — **never `"LogOnly"`**. That exact string is the
     E2 defect and would be silently tolerated by the engine.

### 7.2 Acceptance 4 — 🚨 the flat trap. This is the one that matters

Fixture 2 proves the mapping; only this proves the app feeds it. **Log-only inspection cannot prove
it** — it needs a real open→close cycle.

1. Open a position (owner's choice of route), let `avg_entry` populate, and **read the file while in
   position** — record `position.avg_entry`; it must be non-zero and match the app.
2. **Close the position.**
3. **While flat, read the file again.** Required:
   - `position.direction` is `"FLAT"`;
   - `position.avg_entry` is **`0`**;
   - `size_usd`, `working.stop`, `working.target` are all `0`.
4. **The discriminating check:** at that same moment the app must still be holding a **non-zero**
   `positionAvgEntry` internally — that is the retained close-P/L basis, and the closing P/L line in
   the app log is the visible proof it was still there. If the file shows the retained number
   instead of `0`, that is the exact defect this spec exists to prevent: a stale fill the engine
   would attribute to the next signal.

### 7.3 Acceptance 5 — restart

Note `executor.instance_id` and the current `feedback_id`, close the app, relaunch, and read **as
soon as it is up** — trigger (f) has already written by then, so nothing needs forcing. Required:
a **different** `instance_id`,
`feedback_id` restarted from `1`, and `executor.armed` / `executor.started` both `false` at the
first write after restart.

### 7.4 Acceptance 6 — heartbeat

App **idle and flat**, emitter configured, nothing forced — trigger (f) has already seeded the
heartbeat, which is why it now has a snapshot to republish from its first tick. Read
`generated_at_utc` twice ~30 s apart: it must advance in ~10 s steps and `feedback_id` must increase
with it, while `position` and `last_signal` stay **byte-identical** — that is the republish rule
doing its job. If `position` changes while genuinely idle, the heartbeat is reading live state and
that is a defect.

**Also check `executor.ws` here now that it has a trigger (E6a).** With the app connected it must
read `"OK"`. Then pull the network (or stop the socket) and watch it go to `"DOWN"` **on the
transition, not on a heartbeat** — the heartbeat republishes, so the value can only change because
the receive loop exited and published. Reconnect and it must return to `"OK"`. Before commit
`5c2d6ed` this field would have read `"OK"` through the entire disconnect.

### 7.5 Acceptance 7 — graceful close, then kill

1. **Graceful:** close the app with the X. The file must carry a **final** write — `feedback_id` one
   higher than the last heartbeat — and then stop advancing. That is what makes "silence = dead
   executor" honest.
2. **Kill:** relaunch, let it write, then kill the process (Task Manager / `Stop-Process`). Required:
   the file simply goes **stale** — valid, parseable, complete JSON, just not advancing. **No
   partial or truncated file, and no `executor_feedback.json.tmp` left behind.** That is E1's
   atomic-write proof.
3. **Restore `orderapp-settings.json`** from 7.0 step 3 and verify by relaunching and reading a box
   back.

### 7.6 Turning it back OFF

Delete the `feedback_output_path` key from the x64 `bridge.json` (blank is **not** off — it resolves
to the default path). Confirm the startup line reads
`Executor feedback: disabled (no feedback_output_path in bridge.json)`, and that no new
`executor_feedback.json` appears.

---

# 8. ADDENDUM — E6a and E6b implemented (2026-08-04, commits `5c2d6ed` and `dd43e59`)

**Both arms ruled 2026-08-03 as recommended (A1 and B1). Contract §8.5 and spec §4 were amended
first — trigger list only, no schema, field, enum or semantic change — and both were re-read before
any code was written.** This addendum supersedes §3 items 5 and 6, §5's "neither is implemented",
and §7.0's E6b caveat; each is struck at its own site as well as listed here.

**Gate after each commit: `GATE PASSED`. OrderCheck 227/227 → 264/264 (+37).** The nine censuses are
**unchanged at both commits** — 10 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18, still 68 occurrences across
64 lines. Commit 6 touches `frmMainPageV2.vb` only; commit 7 touches `tools/OrderCheck/Program.vb`
only.

## 8.1 What the hooks touched — three lines, three sites

| Trigger | Site | Why there |
|---|---|---|
| (c) `ws` — **OK edge** | `ConnectWebSocket`, after the success UI update, before the background tasks start | The connection is established at that point and nothing else has run yet |
| (c) `ws` — **DOWN edge** | `ReceiveWebSocketMessagesAsync`, immediately after `End While`, **above** the `If reconnectNeeded` branch | Covers **every** way out — the server-close `Exit While`, all three exception arms, and the `While` condition itself going non-Open. Above the branch because that branch `Return`s when `isClosing` |
| (f) initial write | `frmMainPageV2_Shown`, after the bridge is constructed, immediately **before** `StartHeartbeat` | UI thread, app idle, nothing racing; and the heartbeat then has a real snapshot to republish from its first tick |

All three are the same one-line `PublishExecutorFeedback()` call that every other trigger uses, so
OFF-parity is unchanged: unconfigured costs one Boolean field read and a return.

**The owner's verification sharpened E6a's diagnosis and the comment records it:**
`IsWebSocketConnected` (`:672`) is a **computed** property over `webSocketClient.State`, not a
stored flag — so there is no assignment site any trigger could hook the way the other four fields
are hooked. The gap was **structural**, not an omission from the list, which is exactly why the
transition has to be published where it *happens* rather than where a field is written.

**One interaction worth stating, because it is the only place the two new hooks meet an old one:**
on a user-driven shutdown the DOWN edge is a no-op by construction. `FormClosing` takes its final
write (trigger e) and latches the emitter disposed **before** it closes the socket, so the
subsequent loop-exit publish cannot overwrite that final snapshot with a `DOWN`. The final write
therefore remains the executor's last true state, which is what makes "silence = dead executor"
honest rather than "last thing observed was a disconnect".

## 8.2 The trigger enumeration, restated with `ws` and (f) folded in

§4(d) makes a missing hook permanent, so this table is the artefact — not the fixtures. **18 call
sites.** The first block is unchanged from §1 and is repeated so this table stands alone.

**(b) / (b2) — the four snapshot fields. 17 assignments across 10 methods; all 10 carry a hook.**

| Method | Field writes | Hook |
|---|---|---|
| `SyncTradeInputsFromUi` | `manualTPval` (:518) | tail |
| `HandlePlacementResponse` | `placedStopLossPrice` (:1829) | in the rollback arm (it `Return`s) |
| `HandleQuoteUpdates` | `placedStopLossPrice` (:2559) | at the SL-chase branch (per-tick method) |
| `HandleOrderPositionUpdates` | `positionSizeUSD` (:2995), `positionAvgEntry` (:2998), `placedStopLossPrice` (:3158, :3173, :3309), `manualTPval` (:3476) | tail — one publish per echo, coherent |
| `ExecuteOrderAsync` | `placedStopLossPrice` (:4035) | tail |
| `CancelOrderAsync` | `placedStopLossPrice` (:4113) | tail |
| `CancelWorkingEntryCoreAsync` | `placedStopLossPrice` (:4241) | tail |
| `StopLossForTrailingOrderAsync` | `placedStopLossPrice` (:4968) | tail |
| `HandleOpenOrdersSnapshot` | `placedStopLossPrice` (:5707, :5722) | tail |
| `ProcessPositionData` | `positionSizeUSD` (:5773), `positionAvgEntry` (:5775) | tail |

*(Line numbers are pre-commit-6; commit 6 inserts above some of them. The methods, not the numbers,
are the claim.)*

**(a) / (c) / (d) / (e) / (f) — everything else in the §8.3 domain.**

| Trigger | Field(s) it exists for | Site |
|---|---|---|
| (a) disposition | `last_signal.*` | `SignalBridge.EmitDisposition` — unconditional, below the host-log filter |
| (c) mode | `executor.mode` | `SignalBridge.Mode` setter |
| (c) ARM | `executor.armed` | `SignalBridge.LocalArmed` setter |
| (c) START | `executor.started` | `SignalBridge.TryStart` |
| (c) STOP | `executor.started` | `SignalBridge.ForceStop` |
| (c) breaker | `executor.breaker_tripped` | `HandleBalanceUpdates` tail — `USDPublicSession` is written there on every portfolio echo |
| **(c) ws — OK** | **`executor.ws`** | **connect path, after the success UI update** |
| **(c) ws — DOWN** | **`executor.ws`** | **receive-loop exit, above the reconnect branch** |
| (d) heartbeat | none — liveness only | the 10 s timer; republishes, never re-reads |
| (e) graceful close | all | `FormClosing`, synchronous, own `Try`, before teardown |
| **(f) initial write** | **all** | **`frmMainPageV2_Shown`, before `StartHeartbeat`** |

**Every §8.3 field now has a trigger.** The three that do not are the three that *cannot change
within a process* — `schema_version`, `executor.app`, `instrument` — which is a different thing from
E6a's "changes with no trigger", and fixture 8b now says so explicitly. `feedback_id` and
`generated_at_utc` are assigned per publish and sit outside the content gate by design.

## 8.3 Fixture 8 — rescoped, and what changed about its shape

The owner filed the defect: fixture 8 was scoped to *"the four snapshot fields"*, so it **could not
have caught E6a** — `executor.ws` is a §8.3 field outside the four. Spec §5 now scopes it to every
§8.3 field.

**The aggregate is gone.** The previous version folded mode, armed, started, breaker_tripped and ws
into **one** `Check` joined by `AndAlso` — the very anti-pattern the fixture exists to forbid, since
it passes while four of five are wired. Fifteen fields now get three assertions each (survives the
content gate, reaches the wire, survives coalescing behind an older snapshot). The four
`last_signal` members vary against the **populated** baseline rather than the null one, or they
would pass merely by being non-null and prove nothing about the member.

**New: fixture 8b, the domain check — this is what catches E6a's *shape* rather than its instance.**
It walks the emitted document's leaf paths and pins them as a set, for both document shapes
(`last_signal` populated and null). A new §8.3 field cannot be added without failing it, which
forces whoever adds it to write a per-field assertion — and therefore to ask whether the field has a
trigger. That is the point a per-field list alone cannot reach: **a fixture can enumerate every
field it knows about and still miss the one nobody listed.** Same "enumerate the domain, not the
keys" lesson as the unconfigured NY session bucket, applied to a fixture.

**It was proved to fail rather than pass vacuously.** Dropping `executor.ws` from the expected set
was run: the check failed and printed the actual field set in its detail line — the message a future
author needs. Reverted before commit; the negative test is not in the repo.

## 8.4 What is still NOT established

§3 stands unchanged except for items 5 and 6, and **nothing in this addendum turns a runtime
acceptance green**:

- **Acceptances 3–7 remain unrun.** The two new hooks are argued from the code, not observed. In
  particular **the ws transition has never been seen fire** — §7.4 now carries its own observation
  step for exactly that, and it is the first runtime evidence anyone will have for E6a.
- **(f) has never been observed writing a file.** That it does is the premise of the amended §7.0,
  §7.3 and §7.4, and it is now the *first* thing the run sheet checks — deliberately, so a failure
  shows up immediately rather than as a confusing absence later.
- **Fixture 8b pins the schema, not the call sites.** It would catch a new field with no assertion;
  it would not catch a hook deleted from a handler. That limit is unchanged and is now written into
  spec §5 as well.
- The **`Return`-between-write-and-tail claim** (§1, and the review's named sharpest target) is
  **unaffected** by these commits: commit 6 added no tail hook — the two `ws` sites are lifecycle
  sites, and (f) is a startup site. The claim still covers the same 10 methods and still rests on
  reading them.

## 8.5 One observation for the owner, not an action

H-6 §2.5 records a reply owed to the engine seat — *"should the mirror carry the
heartbeat-republishes ruling?"* — and points at §3's C1 bullet, which does not answer it. The answer
appears to be **already written, in the owner's own amended contract §8.5**: the heartbeat rule is
*"emitter mechanics — canonical here, deliberately NOT mirrored"*, while its consumption-visible
*consequence* (a fresh `generated_at_utc` proves liveness, not field freshness) **is** in the
contract and so does travel to the mirror. If that is the intended answer, the reply is a relay
rather than a decision. **Flagged, not acted on** — cross-app decisions go through the owner and the
engine repo is read-only to this seat.

