# Implementation report — `SB3-fix/E1` (b): persist the R1 M.SL pin and re-pin at restore

**Written 2026-10-12 by the implementer seat (Opus 5.5, high).**
**Spec:** `docs/review-sb3-msl-anchor.md` §3 (owner ruling 2026-10-12: "(b) persist and re-pin").
**Base:** `d2fcf71` (app code identical to `02260d8`).

IDs used here:
- `SB3` — batch 2's spec-back finding: after an R1 replacement limit, the M.SL anchor settled at the top of book.
- `R1` — batch 2's owner ruling on a rejected OTOCO stop leg: place a reduce-only replacement limit.
- `SB3-fix/E1` — the SB3 fix seat's escalation (`docs/impl-report-sb3-msl-anchor.md` §4): an app restart loses
  the pin. `SB3-fix/SB3` — the same seat's note that the owner's manual edit moves and settles the anchor.
- `E1-fix/…` — **this seat's own findings**, in this report's §4. They are not batch 2's `D1`/`SB1`–`SB4`
  and not `SB3-fix/SB1`–`SB3`.

---

## 1. What changed

Line numbers are `DeribitOrderPlacementApp/frmMainPageV2.vb` after the change, unless named otherwise.

| Change | Where |
|---|---|
| **Record type + pure seams.** `R1AnchorRecord` (anchor, order id, position side, UTC). `SerializeR1AnchorRecord` → JSON `{anchor, order_id, side, utc}`. `ParseR1AnchorRecord` → `Nothing` for malformed text, anchor ≤ 0, no order id or an unknown side; `DateParseHandling.None`, invariant culture; a bad `utc` does not void the record. `RestoredR1Pin(record, restoredOrderId)` → the anchor when the ids match (ordinal), else 0 | `:6824`–`:6874` |
| **File helpers.** `R1AnchorFilePath` = `AppContext.BaseDirectory\r1-anchor.json`. `WriteR1AnchorFile`: temp file `r1-anchor.json.tmp`, then `File.Move(…, overwrite:=True)` — `bridge-state.json`'s pattern in `SignalBridge.vb` (`PersistState`). `DeleteR1AnchorFile`: `File.Delete` (no-op when absent). `LoadR1AnchorRecord`. All three under a new `r1AnchorFileGate`; every failure logs yellow and returns, never throws | `:6878`–`:6919` |
| **Write.** The trigger-flip adoption, inside `If price.HasValue`: when `TakeR1MslAnchor()` returned > 0, after the existing orange "pinned" line, `WriteR1AnchorFile(r1Anchor, orderId, direction = "sell")`. `orderId` is that echo's own `order_id`. A sell limit closes a long | `:3614` |
| **Delete at a real trigger flip.** Same block, the no-pin arm: the baseline is now the echo price, so no pin may be restored | `:3616` |
| **Delete at the owner's manual edit** (`SB3-fix/SB3`). The manual-edit branch moves and settles the anchor; a restart must not re-pin over the owner's cap | `:3630` |
| **Delete at every `ClearR1MslAnchor` call.** Added once, inside `ClearR1MslAnchor` (after its `SyncLock`, so the two gates never nest). Covers its four callers: the id-40 error hook `:2012`, `CancelOrderAsync` `:4769`, the market-reduce reset `:4891`, the failed R1 send `:6748` | `:6803` |
| **Delete at every `DropRejectedStopContext` call.** Same way. Covers both fresh placements (`:4646`, `:5684`) and `CompletePositionClose` (`:6091`) | `:6795` |
| **Delete at the four new-SL-placement resets** that zero `emergencyBaseline` (OTOCO placement, the two pre-fill chase edits, the trailing placement). See `E1-fix/SB1` | `:4561`, `:5013`, `:5436`, `:5615` |
| **Re-pin.** id-778 restore seed, triggered-SL branch (`state = "open"`): `RestoredR1Pin(LoadR1AnchorRecord(), id)`. Inside the existing `If emergencyBaseline = 0D` guard: a pin > 0 sets `emergencyBaseline` = the anchor and logs orange "M.SL anchor re-pinned at the dead stop's trigger $T (restored)"; otherwise the resting price, as before. Both set `emergencyBaselineSettled = True`, as before. A pin of 0 deletes the file (stale) | `:7109`–`:7130` |
| **Delete when no stop rests.** An empty open-orders list (`:7017`), and a snapshot with no triggered `StopLossOrder` (`:7145`, flag `triggeredSlRestored`) | `:7017`, `:7044`, `:7145` |
| `.gitignore`: `r1-anchor.json` and `r1-anchor.json.tmp` | `.gitignore:373`–`:375` |
| Gate untracked-file guard: `'r1-anchor.json'` added to the `foreach ($name in @(…))` list | `tools/checks/verify-gate.ps1:53` |
| OrderCheck: 10 fixtures — `E1 restore fixture 1`–`5`, `E1 record fixture 1`–`5` | `tools/OrderCheck/Program.vb:1665`–`:1708` |

**The restore seed's `If emergencyBaseline = 0D` guard — what I did and why.** The re-pin sits **inside** the
guard. The guard is the snapshot's single-writer rule: the id-778 seed never overwrites a live in-process
baseline.
- After a restart the baseline is 0, so the re-pin applies. That is the case the owner's ruling is for.
- On a reconnect without a restart the baseline is already non-zero. It is either the live pin (and the file
  matches it) or the owner's manual cap (and the manual-edit branch already deleted the file). Re-pinning
  outside the guard would at best repeat the live value; it could never be more correct.
- Outside the guard, only the stale-file delete runs: a non-matching or unreadable file goes on every
  snapshot that restores a triggered stop.

**Not changed:** the emergency send order; `FlipEmergencyBaseline`; `DecideRejectedStopAction`; the chase and
its latch; the restore rule for a real triggered stop (resting price, settled); the R1 send. No other state is
persisted.

## 2. Verification — what I checked, and how

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 441/441** (431 before + 10 new). `OK r1-anchor.json untracked` in the repo guards | `tools/checks/verify-gate.ps1`, executed by this seat on the working tree, output to a scratch file and read |
| Nine `frmMainPageV2.vb` censuses | **70 occurrences across 66 lines, unchanged**: `emergencyFired` 12 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 · `RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1 · `TakerFeeRate` 0 · `isPlacingOrder` 13 · `lastPlacementAdmittedUtc` 13 · `placedOrderSizeUsd` 18 | `Select-String -AllMatches -CaseSensitive`, per-symbol match counts summed; lines de-duplicated by line number. Executed before and after the change |
| The fixtures fail against `02260d8`'s behaviour | **Yes. FAILED 3/441.** Mutation: `RestoredR1Pin`'s last line `Return record.Anchor` → `Return 0D` — never re-pin, so the seed always takes the resting price, which is `02260d8`. Lines seen: `FAIL  E1 restore fixture 1: record names the restored triggered stop's id -> re-pin at the dead trigger 82790 - got 0`; `FAIL  E1 record fixture 3: a parsed record feeds the restore decision -> re-pin at 82790`; `FAIL  E1 record fixture 5: a bad utc does not void the record (the utc is for the reader only)` | Executed: byte-copy backup, mutate, `dotnet build tools\OrderCheck\OrderCheck.vbproj -c Debug`, run `OrderCheck.exe`. **Restore by a byte copy, not `git checkout`** (the work was uncommitted). SHA-256 after restore `DD21A3AC…D14629` = before the mutation. No case-sensitive `MUTATION` text left. Rebuilt → `OK 441/441`. See the method note in §3 |
| Every writer of `emergencyBaseline` is classed | 19 assignment lines in 11 sites after the change. **Pin write:** the flip (`:3608`–`:3609`) writes the file. **Delete:** real flip `:3616`; manual edit `:3630`; new-SL placements `:4561`, `:5013`, `:5436`, `:5615`; context resets `CancelOrderAsync`, market reduce, `CompletePositionClose` (via `ClearR1MslAnchor` / `DropRejectedStopContext`); the restore seed's no-match path `:7130`. **No action:** the chase latch (`:2937`–`:2938`) runs only `If Not emergencyBaselineSettled`; the pin and the re-pin both set settled = True, and settled goes False only at sites that delete the file | `grep` of every assignment, each read in place |
| CRLF kept | `frmMainPageV2.vb`: 8440 CRLF of 8440 line ends. `tools/OrderCheck/Program.vb` and `tools/checks/verify-gate.ps1` were LF in the working tree before and stay LF (git normalises) | Byte count, executed; `file` on the others |
| Echo-handler safety | The write runs on the UI thread inside the existing `Me.Invoke`: one small file write and a rename, inside `Try`; a failure logs yellow (`AppendColoredText` uses `BeginInvoke`) and returns | Read the code |
| No nested locks | `DeleteR1AnchorFile` is called after `End SyncLock rejectedStopGate` in both helpers. No holder of `r1AnchorFileGate` takes another lock | Read the code |
| The culture trap | `ParseR1AnchorRecord` uses `DateParseHandling.None` and invariant formats. `E1 record fixture 2` round-trips under `de-DE` (comma decimal, day-first date) | Fixture, executed |

## 3. Not verified

- **No runtime test.** Nothing here has run in the app. The gate builds AnyCPU only; I did not build x64 and did
  not launch the app. The runtime check is `docs/review-sb3-msl-anchor.md` §4: after the R1 action, restart the
  app while the limit rests, and look for "re-pinned … (restored)". Also check that `r1-anchor.json` appears
  beside the **x64** exe after the "pinned" line and is gone after the trade ends flat.
- **That a Deribit edit keeps the order id.** Carried from `docs/review-sb3-msl-anchor.md` §3 and
  `docs/impl-report-sb3-msl-anchor.md`. If a chase edit changed the id, the restore would find no match,
  delete the file and anchor at the resting price — the `02260d8` behaviour, never worse.
- **That the R1 limit comes back in the id-778 snapshot as `label = "StopLossOrder"`, `order_state = "open"`.**
  It is sent with that label (`SendReplacementStopLimitAsync`); I did not see a snapshot of one.
- **Restart timing.** The id-778 snapshot is sent at connect. I did not trace whether a restart can process the
  R1 limit's own open echo before the snapshot. If it did, the flip block would adopt it first (baseline 0 →
  flip arm) with no pending anchor, take the echo price unsettled and delete the file. Read: on a restart the
  order is already open, so no `user.changes` echo comes for it until an edit, and edits need `SLTriggered`,
  which only the snapshot sets. Not tested.
- **Method note for the next seat (not a code finding).** After the byte-copy restore, the first OrderCheck
  rebuild **reused the mutated assembly** and still printed FAILED 3/441: `Copy-Item` keeps the backup's
  older timestamp, so MSBuild saw the app as up to date. Touching the file and rebuilding gave `OK 441/441`.
  Any mutation run restored by a byte copy must touch the file before the rebuild.
- The doc re-ranker was not fired: every document this seat read was named in the brief. The decision-bias
  tripwire was not fired: this report recommends nothing between options. The guard placement (§1) and the
  extra delete sites (`E1-fix/SB1`) are implementation choices inside the owner's ruling, stated for review.

## 4. Findings (scoped to this seat: `E1-fix/…`)

| ID | Kind | Meaning | State |
|---|---|---|---|
| `E1-fix/SB1` | Spec-back (against `docs/review-sb3-msl-anchor.md` §3) | §3 lists the delete sites as the `ClearR1MslAnchor` / `DropRejectedStopContext` callers, `CompletePositionClose`, the triggered-SL resets and the manual edit. I also delete at the **four new-SL-placement resets** that zero `emergencyBaseline` and at a **real trigger flip**. Rule kept: the file exists only while `emergencyBaseline` holds the pin. Without them, a fresh placement whose send fails before `DropRejectedStopContext` could leave a file that a later restart re-pins while the old R1 limit still rests. Each extra delete only falls back to the `02260d8` behaviour on a restart | Built; for the coordinator to accept or reject |
| `E1-fix/SB2` | Spec-back (residual, no code) | **A snapshot older than the pin deletes it.** The stale-file delete trusts the id-778 snapshot. If a connect's snapshot was taken before the R1 limit existed but is processed after that limit's echo wrote the file, the snapshot shows no triggered stop and deletes the file. The in-memory pin is unaffected; only a later restart falls back to the resting price. Window: an R1 action inside the snapshot round trip just after a connect | For the record. A fix would compare the record's `utc` with the snapshot's send time |
| `E1-fix/SB3` | Spec-back (residual, no code) | **No file before the echo.** If the app closes between the R1 send and the adoption of its echo, there is no file and the restart anchors at the resting price. Window: one round trip | For the record |
| `E1-fix/SB4` | Spec-back (by design, stated) | The record's `side` is written for the reader and checked by the parser, but the restore decision does not use it. The order id alone identifies the limit | For the record |

## Model and effort

- **Review of this commit:** coordinator, **Opus 5.5, high** — the restore path and the emergency baseline.
- **Runtime check** (`docs/review-sb3-msl-anchor.md` §4, inside the batch-2 testnet session): **Opus 5.5, high**.
