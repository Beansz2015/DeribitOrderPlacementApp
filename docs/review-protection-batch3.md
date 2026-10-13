# Coordinator review — protection batch 3

**Reviewed 2026-10-14 (GMT+8) by the coordinator seat (Opus 5.5, high).**
**Under review:** commit `4ad3035` (report `f5a76a1`), against `docs/spec-protection-batch3.md` and
`docs/impl-report-protection-batch3.md`.
**Verdict: ACCEPTED at code level** for `B3-R1`–`B3-R4` and `RE-SB5`. `B3-R5` (the freshness cap) was not built,
correctly: escalation `B3-E1` was ruled by the owner on 2026-10-14 (default N = 3) and the build is queued. Runtime
(`docs/spec-protection-batch3.md` §5 item 3) is owed after the owner's return.

IDs: `B3-R*` are the owner's rulings; `B3-E1` the seat's escalation; `B3-SB*` its spec-back findings; `RE-SB5` the
refused-edits finding folded in. `SB3` is batch 2's M.SL pin.

---

## 1. Verified at the artefact

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 510/510** | `tools/checks/verify-gate.ps1`, executed by this seat at `f5a76a1` (code = `4ad3035`) |
| Nine `frmMainPageV2.vb` censuses | **75 occurrences across 71 lines** (15 · 8 · 3 · 3 · 1 · 0 · 13 · 13 · 19). `emergencyFired` +2 = the cap's latch read and set | executed; `CLAUDE.md` census line updated |
| Do-not-touch | `git diff 74f8399 4ad3035`: **0** removed lines touch the M.SL latch, the `SB3` pin or its re-pin, `ArmEmergencyClose` or the emergency latch's set/clear sites. The hoisted M.SL gate is still in place, once | diff filtered; grep |
| The cap never pre-empts the triggered path | `TriggeredMslPathArmed` = connected ∧ ¬cancel-pending ∧ `SLTriggered` ∧ stop id known ∧ chase reference > 0 — the hoisted M.SL check's own gate (fixture 5 pins it). `PositionCapFires` returns False first when it holds | read `:7043`, `:7049` |
| The cap's arithmetic | long: `trigger − ask ≥ M.SL`; short: `bid − trigger ≥ M.SL`; no cap with M.SL off, threshold 0, no trigger or no quote | read |
| One emergency per position | `TryFirePositionCapAsync` claims `emergencyFired` under `positionCapGate`, then runs the batch-2 R1 sequence: page, cancel, `ArmEmergencyClose` before the reduce, `ReportEmergencyCloseSend` | read `:7106` |
| Dead feed | `FeedCheck`: probe after 3 s of silence; stall 2 s after an unanswered probe; any frame answers | read `:7188` |
| Stand-down scope | `IsBridgeEntry` = the current entry id equals the id from the bridge's own placement ack; `StandDownCancelsEntry` needs a reason, a bridge entry and no cancel pending — a manual entry can never match | read `:7278`, `:7282` |
| Mutation proofs (6), the engine values, the heartbeat interval | Carried from the report, **not re-run by me** | report §1, §4 |

## 2. Findings — dispositions

| ID | Meaning | Disposition |
|---|---|---|
| `B3-E1` | The ruled default N = 1 would stand the bridge down for most of ASIA and LONDON (the engine emits 3 there) | ✅ **Owner ruling 2026-10-14: (a) default N = 3.** NY 2.5 min, ASIA/LONDON 7.5 min, larger capped. Build queued (`docs/spec-protection-batch3.md` §4) |
| `B3-SB1` | The bridge-entry handle is the order id from the bridge's own placement ack | ✅ Accepted: every residual falls on the safe side (an unmarked bridge entry is not cancelled; a manual one never is) |
| `B3-SB2` | The cap stands down while disconnected or cancel-pending; the notices wait for the snapshot and the no-stop grace; priority `high` | ✅ Accepted |
| `B3-SB3` | Any frame answers the probe; a stall `ForceStop`s the bridge (owner START needed); abort rather than close; a stall is not a stand-down cancel | ✅ Accepted |
| `B3-SB4` | Which stand-downs cancel (stale, SKIPPED, engine ARM off, local ARM off) and which do not | ✅ Accepted |
| `B3-SB5` | A stand-down cancel writes no `AbortedEntries` row | **Folded into the freshness-cap build seat** as a small logging item (a row with reason `stand-down: <reason>`). Logging only; no order behaviour |
| `B3-SB6` | A double latch claim across the cap and the older paths needs a microsecond race; the second order is reduce-only | ✅ Accepted residual |
| `B3-SB7` | In a gap, the cap can fire before the trigger echo arrives, or when the ask passes one M.SL beyond a last-price trigger that has not fired | ✅ Accepted: it always flattens at exactly one M.SL past the trigger, inside the owner's standing tolerance. **Shown to the owner** |
| `B3-SB8` | The probe is one more socket sender | Covered by the queued hygiene item (serialise socket sends) |

## 3. Still owed

- The freshness-cap build (N = 3) with the `B3-SB5` row — next seat.
- Runtime (`docs/spec-protection-batch3.md` §5 item 3) after the owner's return, with the refused-edits runtime.
- The SHORT side; the `TrailingStopLoss` anchor (later, never earlier).

## §Model and effort

- Freshness-cap build seat (with `B3-SB5`): Opus 5.5, high — the bridge freshness gate.
