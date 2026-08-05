# Runtime record — C1 executor feedback emitter, acceptances 4–7, 2026-08-06

**Harness-driven, TESTNET, harness-launched session.** Owner authorised the position for acceptance
4; everything else needed no trade. Run against the **Debug/AnyCPU harness bin**, never the owner's
x64 bin — separate `secrets.json` (`Environment: testnet`, verified), settings, DB and bridge state.
Window title verified `— TESTNET` at every launch by the harness's own gate.

## VERDICT: **PASS on every observable attempted. Acceptances 4, 5, 6 and 7 are CLOSED.**
**Acceptance 3 was NOT run** — it needs ARM/START (owner-only; `START` is on the harness deny list)
and an engine stop. It remains the only open acceptance.

**Two things observed here for the first time ever:** E6a's `ws` transition and E6b's trigger (f).
Both were implemented and gate-green but had never been seen fire — impl report §8.4 said so
explicitly, and this closes that gap.

## Setup

| | |
|---|---|
| Bin | `DeribitOrderPlacementApp\bin\Debug\net9.0-windows8.0` (harness), built from HEAD |
| Emitter | `feedback_output_path` → `…\bin\Debug\…\executor_feedback.json` (scratch, **not** `C:\Dev\DeribitBridge\`, which the owner's §7.1 emit-only step still needs clean) |
| Bridge | mode **Off** throughout — no payload consumed, `last_signal` stays `null` in every reading |
| Amount | **10** (contract minimum) · `M. SL` 200 checked · ATRSlip unchecked |
| Engine | left RUNNING and untouched; its `verdict_signal.json` was never written to |

`orderapp-settings.json` was backed up before and restored after (H-6 §5.4 — `FormClosing` persists
all 11 geometry fields unconditionally, and the run changed `Trig.O.` 30 → 300).

## 1. Trigger (f) — E6b, observed. **The file exists on an idle, flat, unconnected app.**

First read after launch, before any click:

```json
{ "schema_version": 1, "feedback_id": 1, "generated_at_utc": "2026-08-05T19:59:19Z",
  "executor": { "instance_id": "3a009b79-…", "app": "DeribitOrderPlacementApp", "mode": "OFF",
                "armed": false, "started": false, "breaker_tripped": false, "ws": "DOWN" },
  "instrument": "BTC-PERPETUAL",
  "position": { "direction": "FLAT", "size_usd": 0.0, "avg_entry": 0.0,
                "working": { "stop": 0.0, "target": 0.0 } },
  "last_signal": null }
```

`feedback_id: 1`, `armed`/`started` false, `ws: "DOWN"` (correct — not connected yet). **This is the
state that did not exist before E6b**, and it is what makes §8.1's *"silence = dead executor"* true
rather than aspirational: a live idle executor is now distinguishable from an absent one.

Startup line, matching the notifier's precedent:
`Executor feedback: configured - …\executor_feedback.json`

## 2. Acceptance 6 — heartbeat. **PASS, and it republishes rather than re-reads.**

Five samples, app idle and flat:

| wall clock | `feedback_id` | `generated_at_utc` | ws | direction | avg_entry | last_signal |
|---|---|---|---|---|---|---|
| 03:59:40 | 3 | 19:59:39Z | DOWN | FLAT | 0.0 | null |
| 03:59:50 | 4 | 19:59:49Z | DOWN | FLAT | 0.0 | null |
| 04:00:00 | 5 | 19:59:59Z | DOWN | FLAT | 0.0 | null |
| 04:00:10 | 6 | 20:00:09Z | DOWN | FLAT | 0.0 | null |
| 04:00:20 | 7 | 20:00:19Z | DOWN | FLAT | 0.0 | null |

**Exact 10 s steps, `feedback_id` +1 each, and every other field byte-identical.** That last column
is the point: §4(d)'s rule is that the heartbeat republishes the *last snapshot*, and a heartbeat
that re-read live state would be free to show drift here. It shows none.

## 3. E6a — the `ws` OK edge, observed. **First runtime evidence for E6a.**

`btnConnect` clicked at ~20:00:40.

| `feedback_id` | `generated_at_utc` | ws |
|---|---|---|
| 7 | 20:00:19Z | DOWN |
| **10** | **20:00:41Z** | **OK** |
| 11 | 20:00:49Z | OK |
| 12 | 20:00:59Z | OK |

**The discriminating detail is the timestamp, not the value.** The heartbeat cadence in this session
was `…:19, :29, :39, :49, :59`. The flip landed at **`:41`** — off-cadence, therefore a *transition*
publish from the connect hook, not a heartbeat. Under the republish rule a heartbeat could not have
changed this field at all; before commit `5c2d6ed` it would have read `"OK"` forever after a drop.

⚠ **The DOWN edge is still NOT observed.** It needs a real disconnect (adapter down / firewall), and
manufacturing one on the owner's machine was out of scope for this run. The graceful-close path
below exercises the *disposal* half of that hook but not the transition itself.

## 4. 🚨 Acceptance 4 — THE FLAT TRAP. **PASS, both conjuncts proven.**

Placement and close both through `tools/place-and-verify.ps1` (TESTNET-gated, effect-asserted):

```
Market buy order placed For 10 starting at 64806.
Reduce-only MARKET sell 10  order sent.
Position reduced at 64825.62 (market order).
Position executed at 64825.62.
Scratch close: P/L ≈ $0.00.
Trade #75 recorded in database
```

**In position** — triggers (b) and (b2) both fired:
`direction "LONG"` · `size_usd 10.0` · `avg_entry 64806.5` · `working.stop 64800.0` ·
`working.target 0.0` (no manual TP set — 0 = unset, informational, as specced).

**While flat**, `feedback_id 28`:
`direction "FLAT"` · `size_usd 0.0` · `avg_entry 0.0` · `working.stop 0.0` · `working.target 0.0`.

### The second conjunct, which is the whole test

A flat file showing zeros proves nothing on its own — it must be flat *while the app still holds a
non-zero `positionAvgEntry`*. **`Scratch close: P/L ≈ $0.00` does NOT establish that**: the same line
prints when the model is unseeded and the P/L computes to 0, which is exactly the fix-7 scratch path.
Claiming the trap on that line alone would have been the "prove every conjunct or claim nothing"
error (H-6 §7b lesson 4).

**The discriminator is the recorded trade row.** `CompletePositionClose` stores
`entryPriceAtClose = If(positionAvgEntry > 0D, positionAvgEntry, placedPrice)`, and the two candidates
are distinguishable here:

- `positionAvgEntry` = **64806.50** (exchange `average_price`)
- `placedPrice` = **64806.00** (`BestPrice` at placement — the log's *"starting at 64806"*)

Trade **#75** records **`Entry Price = 64806.50`**, read back from the app's own Trade History grid
(ID 75 · Long · entry 64806.50 · exit 64825.62 · size 10.00 · fees 0.01).

**Therefore `positionAvgEntry` was still non-zero at the flat echo, while the feedback file published
`avg_entry: 0`.** The retention is intact and the emitter reads around it. That is §3.2's defect
prevented, demonstrated on a real open→close cycle — which log-only inspection could not have done.

## 5. Acceptance 7a — graceful close. **PASS.**

Last heartbeat before close `feedback_id 45`; after `stop-app.ps1` (CloseMainWindow → `FormClosing`):
**`feedback_id 47`, then frozen** — identical 25 s later. Silence = dead executor, honestly.

No `.tmp` residue.

**Bonus observation, and it is the D1-adjacent design point:** the final snapshot carries
**`ws: "OK"`**, not `"DOWN"`. The receive loop exits during shutdown and its DOWN hook fires — but
`ShutdownWithFinalWrite` latches `_disposed` before the socket closes, so that publish is a no-op and
cannot overwrite the final write. Impl report §8.1 claimed this by construction; here it is observed.

## 6. Acceptance 5 — restart. **PASS.**

| | before | after |
|---|---|---|
| `instance_id` | `3a009b79-eac6-4678-aba7-e29df4717e48` | `8c46361f-19a2-4814-adde-259f5a07c745` |
| `feedback_id` | 47 | **1** |
| `armed` / `started` | false / false | false / false |

New GUID, counter restarted, disarmed by construction — contract §8.4's guarantee, and what makes
§6's restart-disarmed visible engine-side.

## 7. Acceptance 7b — hard kill. **PASS. E1's atomic-write proof.**

`Stop-Process -Force` (no `FormClosing`). Last write `feedback_id 5` @ `20:07:54Z`. 25 s later:
**unchanged, 549 bytes, parses as complete JSON, terminates with `}`, no `.tmp` left behind.**
The file goes *stale*, never partial — which is the property `File.Replace` could not have given on
a first write and the reason contract §8.1 was amended (E1).

## 8. OFF-parity, re-proved at runtime

Impl report §2 argued this from code. Observed here: with `bridge.json` removed, the startup line
reads `Executor feedback: disabled (no feedback_output_path in bridge.json)` and **no
`executor_feedback.json` is created** — contract §8.5's *file absent = feature OFF*.

## 9. Account left clean

Relaunched and reconnected after the trade: **no `Open position detected`** line (would print at the
id-777 seed for any non-zero size) and **no `Restored order context`** line (would print for any
surviving OTOCO child). Flat, no working orders — `CompletePositionClose`'s cancel-all did its job.

## 10. What this run does NOT prove

- **Acceptance 3 is unrun.** No payload was consumed, so `last_signal` was `null` in every reading
  and trigger (a) was never exercised at runtime. The `mode` field was only ever observed as
  `"OFF"` — **`"LOG_ONLY"` and `"LIVE"` remain fixture-evidence only**, and E2's silent-failure mode
  is precisely a wrong `mode` string, so this is the most valuable thing left to observe.
- **The `ws` DOWN edge is unobserved** (§3).
- **`breaker_tripped` was never seen flip** — session P/L stayed at $0.00; fixture 5 only.
- **A `SHORT` position was never emitted.** E3's sign is established from trades #99/#68 and pinned
  by fixture 3, but this run went long, so `size_usd` was only ever observed positive.
- **D1's race is not reproduced.** It needs two threads and a pre-emption; the graceful-close path
  ran cleanly, which is consistent with the fix but does not exercise the window.
- Single session, single 10-USD position, testnet.

## 11. Housekeeping — one loss, recorded

Cleanup ran `Remove-Item verify\out\*.png`, which deleted **`n2-settings.png`** (204 KB) alongside
this run's own two screenshots. That file pre-dated this seat and belonged to the closed N2 work;
`verify/` is git-ignored, so it is not recoverable. The standing "delete after use" rule covers
screenshots the harness run creates — a wildcard was the wrong instrument. Recorded rather than left
to be discovered.
