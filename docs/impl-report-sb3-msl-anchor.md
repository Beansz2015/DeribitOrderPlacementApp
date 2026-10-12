# Implementation report — batch-2 `SB3` follow-up: pin the M.SL anchor at the dead stop's trigger

**Written 2026-10-12 by the implementer seat (Opus 5.5, high).**
**Spec:** `docs/review-protection-batch2.md` §3 (owner ruling 2026-10-12), also folded into
`docs/spec-protection-batch2.md` §8.
**Base:** `1f0b830` (app code identical to `389a87a`).

`SB3` here is batch 2's spec-back finding (`docs/impl-report-protection-batch2.md` §3): after an R1
replacement limit, the M.SL anchor settled at the top of the book, so the flatten fired only a further M.SL
distance beyond the market. Finding IDs in this report's §4 are scoped to **this follow-up** (`SB3-fix`).
They are not batch 2's `D1`/`SB1`–`SB4` and not the partial-fill spec's `PF-*`.

---

## 1. What changed

All line numbers are `DeribitOrderPlacementApp/frmMainPageV2.vb` after the change, unless named otherwise.

| Change | Where |
|---|---|
| New field `r1MslAnchor` (Decimal, 0 = none pending), under `rejectedStopGate` | `:6633` |
| **Record.** `TryActOnRejectedStopAsync`, limit branch: under `rejectedStopGate`, `r1MslAnchor` = the dead leg's trigger (0 when the trigger is 0). Set **before** `SendReplacementStopLimitAsync`, so the open echo cannot beat it | `:6732`–`:6736` |
| **Clear on a failed send.** `placed = False` → `ClearR1MslAnchor()`. No message went out, so no echo will consume it | `:6738` |
| **Adopt.** The `StopLossOrder` open-echo trigger-flip block (`If Not wasTriggered OrElse emergencyBaseline = 0D`), inside `If price.HasValue`: `TakeR1MslAnchor()` reads and consumes the anchor; `FlipEmergencyBaseline` sets `emergencyBaseline` and `emergencyBaselineSettled`. `placedStopLossPrice` (the chase reference) still takes the echo's price. When pinned, one orange log line: "Stop limit adopted at $X; M.SL anchor pinned at the dead stop's trigger $T" | `:3599`–`:3611` |
| **Pure seam** `FlipEmergencyBaseline(echoPrice, r1Anchor) As (Baseline, Settled)`: anchor > 0 → (anchor, True); else (echo price, False), which is the `389a87a` behaviour | `:6604` |
| `TakeR1MslAnchor()` — read-and-zero under the gate, so one anchor pins exactly one flip | `:6796` |
| `ClearR1MslAnchor()` — zero under the gate | `:6788` |
| **Clear sites.** `DropRejectedStopContext` (runs at both fresh placements, `:4639` and `:5674`, and in `CompletePositionClose`) now zeroes the anchor too | `:6783` |
| Clear at the id-40 error hook (the limit was refused; no limit exists) | `:2012` |
| Clear at the two other triggered-SL context resets: `CancelOrderAsync` (nuclear cancel) and `SendReduceOrderAsync` (market reduce). See finding `SB3-fix/SB1` | `:4762`, `:4884` |
| OrderCheck: 5 fixtures, `SB3 fixture 1`–`5` | `tools/OrderCheck/Program.vb:1645`–`:1663` |

**Not changed:** the emergency send order (cancel, then reduce); the OTOCO payload; `IsClosingStop`;
`DecideRejectedStopAction`; the chase; the manual-edit branch; the id-778 restore seed.

## 2. Verification — what I checked, and how

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 431/431** (426 at `389a87a` + 5 new) | `tools/checks/verify-gate.ps1`, executed by this seat on the working tree |
| Nine `frmMainPageV2.vb` censuses | **70 occurrences across 66 lines, unchanged**: `emergencyFired` 12 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 · `RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1 · `TakerFeeRate` 0 · `isPlacingOrder` 13 · `lastPlacementAdmittedUtc` 13 · `placedOrderSizeUsd` 18 | `Select-String -AllMatches -CaseSensitive`, per-symbol match counts summed; lines de-duplicated by line number. Executed |
| The fixtures fail against `389a87a`'s behaviour | **Yes. FAILED 3/431.** Mutation: `FlipEmergencyBaseline`'s pin line replaced, so it always returns (echo price, False) — the `389a87a` adoption. Lines seen: `FAIL  SB3 fixture 1 (experiment (a) prices): R1 anchor 82790 pending, long limit echoes at 82785 -> baseline 82790, SETTLED - got 82785 settled=False`; `FAIL  SB3 fixture 2 … - got 82795 settled=False`; `FAIL  SB3 fixture 5: pinned at 82790, M.SL 50 -> the cap is at 82740, the R1 flatten point` | Executed: mutate, `dotnet build tools\OrderCheck\OrderCheck.vbproj -c Debug`, run `OrderCheck.exe`. **Restore was by a byte copy, not `git checkout`** (the fix was still uncommitted, so `git checkout` would have discarded it). SHA-256 of the restored file matched the pre-mutation hash; no `MUTATION` text left; OrderCheck rebuilt → `OK 431/431` |
| The pinned, settled baseline survives the first chase reposition | **Yes.** The chase's latch writes `emergencyBaseline` only `If Not emergencyBaselineSettled` (`:2936`–`:2939`). The pin sets settled = True, so the latch skips | Read the code. Then listed **every** write to `emergencyBaseline` / `emergencyBaselineSettled` with `grep` (14 sites) and classed each, below |
| The emergency path reads the pinned baseline | **Yes.** Both readers use `If(emergencyBaseline > 0D, emergencyBaseline, StopLossTriggerOriginal)`: the hoisted tick check (`:2815`) and `UpdateStopLossForTriggeredStopLossOrder` (`:5262`). Neither writes it. With baseline = trigger, the long fires at `trigger − ask ≥ M.SL` and the short at `bid − trigger ≥ M.SL` | Read the code |
| The pin equals R1's own flatten point | **Yes**, same baseline, same `>=`, same own-side quote in the hoisted check. `SB3 fixture 5` pins it at experiment (a)'s prices | Read `DecideRejectedStopAction` (`:6589`) against `:2815`–`:2860`; fixture |
| No other writer moves the pin during the trade | The 14 writes: `:2937`/`:2938` chase latch — skipped (settled). `:3608`/`:3609` the flip — this change. `:3623`/`:3624` manual-edit branch — runs only on an echo price the app did not command; it moves the pin by design (the owner's manual cap). `:4554`, `:5005`, `:5427`, `:5605` new-SL placement resets; `:4759`/`:4760`, `:4881`/`:4882`, `:6078`/`:6079` context resets — all zero it when the R1 context is over. `:6992`–`:6994` the id-778 restore seed — writes only when the baseline is 0, so it cannot overwrite a pin | `grep` of every assignment, each read in place |
| No deadlock from the new `SyncLock` on the UI thread | The other holders of `rejectedStopGate` do no blocking UI call inside the lock. `AppendColoredText` and `UiInvoke` both use `BeginInvoke` (`:5959`, `:584`) | Read the code |
| CRLF kept | `frmMainPageV2.vb`: 8303 CRLF of 8303 line ends. `tools/OrderCheck/Program.vb` was LF-only in the working tree before the change and stays LF (git normalises with `* text=auto`) | Byte count, executed |

## 3. Not verified

- **No runtime test.** Nothing here has run in the app. The gate builds AnyCPU only; I did not build x64 and did
  not launch the app. The runtime check is `docs/review-protection-batch2.md` §5 item a ("the M.SL anchor is
  the dead trigger"); the orange "M.SL anchor pinned" line is there to make it visible in the log.
- **That the R1 limit's open echo reaches the flip block.** I carried this over from the coordinator's read in
  `docs/review-protection-batch2.md` §1 (row "The R1 limit is adopted as a triggered stop"). I did not re-trace
  the outer conditions of the `open` case.
- **That `SLTriggered` is False when the stop leg is rejected.** If it were True, the R1 limit's echo would take
  the manual-edit branch (baseline = echo price, settled, a false "Manual SL edit" line) and the pin would wait
  unconsumed until the next context reset. I reasoned it unreachable: the rejection comes at the entry fill, and
  each fresh placement resets the triggered context. Not tested.
- **The exchange's echo for a repriced post-only limit.** If Deribit sent the open echo first at the requested
  price and then a second echo at the repriced price, the second would hit the manual-edit branch and move the
  pin. Experiment (c) observed the repricing; I did not check how many `open` echoes it produced.
- **The 30 s R1 wait does not apply to the anchor.** Its lifetime is send → echo. A lost echo leaves it pending
  until the next clear site (placement, close, cancel, market reduce, id-40 error). Bounded, not tested.
- The doc re-ranker was not fired: every document this seat read was named in the brief. The decision-bias
  tripwire was not fired: this report makes no recommendation between options (`SB3-fix/E1` is left open).

## 4. Findings (scoped to this follow-up: `SB3-fix/…`)

| ID | Kind | Meaning | State |
|---|---|---|---|
| `SB3-fix/E1` | Escalation (raised before code; **no code written**) | **An app restart loses the pin.** The anchor lives in memory only. After a restart with the R1 limit still resting, the id-778 restore seed (`:6992`–`:6994`) adopts the **resting limit's price** as the settled baseline. After any chase that price sits near the top of the book, so the cap is again a further M.SL distance beyond the market — the `SB3` gap, in the restart case only. A reconnect without a restart keeps the pin (no reconnect path zeroes the baseline). `docs/review-protection-batch2.md` §3 does not cover a restart. Fixing it needs a persisted anchor or a restore-time rule — new design | **Open, for the coordinator**, then the owner if the coordinator wants it closed. Owner away 2026-10-14 to 2026-11-25 |
| `SB3-fix/SB1` | Spec-back (against `docs/review-protection-batch2.md` §3) | §3 lists two clear sites: `DropRejectedStopContext` and the id-40 error. I added three more. (a) A failed send of the R1 limit — no message went out. (b) `CancelOrderAsync` and (c) `SendReduceOrderAsync`'s market branch — the other two triggered-SL context resets. (b) also covers the case where the echo lands while `cancelPending` is True: the flip block is skipped and the anchor would otherwise stay pending. Without these, a stale anchor could pin a **later** stop's flip at the dead trigger until the next placement or close. Each extra clear only falls back to the `389a87a` behaviour | Built; for the coordinator to fold into `docs/spec-protection-batch2.md` §8 or reject |
| `SB3-fix/SB2` | Spec-back (consequence, no code) | With the pin, the chase's 50 % path (`:2915`, `priceMovement >= emergencyThreshold * 0.5` → `ForceStopLossUpdate`) also measures from the dead trigger, so it engages earlier than after a real trigger. `ForceStopLossUpdate` only clears the throttle stamp (`:5796`) before the same `UpdateStopLossForTriggeredStopLossOrder`. Behaviour: the chase edits without the 333 ms throttle sooner. No action proposed | For the record |
| `SB3-fix/SB3` | Spec-back (by design, stated) | An owner's manual edit of the R1 limit still moves and settles the anchor (manual-edit branch, `:3623`/`:3624`), as for any triggered stop. The pin is the app's default, not a lock against the owner | For the record |

## 5. Model and effort

- **Review of this commit:** coordinator, **Opus 5.5, high** — the emergency baseline is on the stop path.
- **`SB3-fix/E1`, if taken up:** **Opus 5.5, high** — restore path and the emergency baseline.
