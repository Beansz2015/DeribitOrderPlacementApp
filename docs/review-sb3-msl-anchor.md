# Coordinator review — batch-2 `SB3` follow-up: pin the M.SL anchor at the dead stop's trigger

**Reviewed 2026-10-12 by the coordinator seat (Opus 5.5, high).**
**Under review:** commit `02260d8`, against `docs/review-protection-batch2.md` §3 (owner ruling 2026-10-12) and
`docs/impl-report-sb3-msl-anchor.md`.
**Verdict: ACCEPTED at code level.** One more follow-up is ruled (`SB3-fix/E1` (b), §3), and it is built before
the testnet session. Nothing here has run in the app.

IDs: `SB3` is batch 2's spec-back finding (the M.SL anchor after an R1 limit). `SB3-fix/…` IDs are the fix
seat's own (`docs/impl-report-sb3-msl-anchor.md` §4). `R1` is batch 2's owner ruling on a rejected stop leg.

---

## 1. Verified at the artefact

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 431/431** | `tools/checks/verify-gate.ps1`, executed by this seat at `02260d8` |
| Nine `frmMainPageV2.vb` censuses | **70 occurrences across 66 lines, unchanged** (12 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18) | `Select-String -AllMatches -CaseSensitive`, summed; lines de-duplicated |
| The change matches `docs/review-protection-batch2.md` §3 | Yes. Record before the send; consume at the flip, inside `If price.HasValue`; `placedStopLossPrice` still takes the echo; trigger 0 pins nothing | read the diff |
| The pin survives the chase | Yes. The settle flag is True, so the chase latch skips (the seat's grep of all 14 writers; I re-read the restore seed at `:6991`–`:6994`, which writes only when the baseline is 0) | read |
| The fixtures fail against `389a87a` | Carried from the seat: mutation → FAILED 3/431. **Not re-run by me** | report §2 |
| No deadlock | `rejectedStopGate` holders do no blocking UI call; `AppendColoredText` and `UiInvoke` use `BeginInvoke` | carried from the seat's read; consistent with the batch-2 diff I read |

## 2. The fix seat's findings — dispositions

| ID | Meaning | Disposition |
|---|---|---|
| `SB3-fix/E1` | An app restart loses the pin. The id-778 restore seed then anchors M.SL at the resting limit's price — the existing rule for every restored triggered stop. The extra distance before the market flatten is under one M.SL | **Owner ruling 2026-10-12: (b) persist and re-pin.** The owner's words: "A M.SL is already an emergency by nature. An additional amount up to 1x the M.SL is already past the amount that I'm willing to risk." Spec in §3 |
| `SB3-fix/SB1` | Three extra clear sites (a failed send; `CancelOrderAsync`; the market-reduce reset) | ✅ Accepted. Each falls back to the `389a87a` behaviour; without them a stale pin could catch a later stop. Folded into `docs/spec-protection-batch2.md` §8 |
| `SB3-fix/SB2` | With the pin, the chase's 50 % force path engages earlier | ✅ Accepted for the record. It only drops the 333 ms throttle sooner |
| `SB3-fix/SB3` | The owner's manual edit still moves and settles the anchor | ✅ Accepted, by design. §3 must keep it true across a restart |

**Decision-bias tripwire on `SB3-fix/E1`** (`docs/harness-runs/decision-bias-20261012T1005Z-*`, baseline written
first): the coordinator recommended (a) accept; verdict **`gives_up_for_economy`, stable 5/5**, matching the
seat's own baseline. Sent to the owner as `CLAUDE.md` requires. The owner chose (b).

## 3. Required follow-up — `SB3-fix/E1` (b): persist the pin and re-pin at restore

**Rule:** a pinned R1 anchor survives an app restart. After a restart, while the same R1 stop limit rests, the
M.SL anchor is the dead stop's trigger, settled.

**Shape (the implementer chooses the details):**
- **Write.** When the flip consumes a pin (`TakeR1MslAnchor` > 0), write a small state file beside the exe,
  `r1-anchor.json`: the anchor, the R1 limit's `order_id` (from the echo), the position side, UTC time. Follow
  `bridge-state.json`'s pattern in `SignalBridge.vb` (path, load/write, failures logged yellow, never thrown).
  Write atomically (temp file, then replace). The write runs on the UI thread inside the echo lambda: keep it
  small; a failure must never break the echo handler.
- **Re-pin.** In the id-778 restore seed's triggered-SL branch: when the file exists and its `order_id` equals
  the restored `StopLossOrder`'s id, set `emergencyBaseline` to the anchor and `emergencyBaselineSettled = True`,
  in place of the resting price. Log orange: "M.SL anchor re-pinned at the dead stop's trigger $T (restored)".
  A Deribit edit keeps the order id, so the chased limit still matches. No match, or no resting stop → delete
  the file (stale).
- **Delete** the file wherever the pin's context ends: every site that calls `ClearR1MslAnchor` or
  `DropRejectedStopContext`, `CompletePositionClose`, and the triggered-SL context resets that zero
  `emergencyBaseline`.
- **The owner's manual edit wins** (`SB3-fix/SB3`): the manual-edit branch, which moves and settles the anchor,
  deletes the file. A restart must not undo the owner's manual cap.
- **Repo hygiene:** add `r1-anchor.json` to `.gitignore` and to the gate's untracked-file guard
  (`tools/checks/verify-gate.ps1`, the `foreach ($name in @(…))` list).
- **Pure seams → OrderCheck:** the restore decision (record, restored id) → re-pin or not, including a
  mismatched id, a missing record and an anchor of 0; the record's serialise/parse round trip. At least one
  fixture must fail against `02260d8`, proven by a mutation run.
- **Not in scope:** any other persisted state; changing the restore rule for real triggered stops.

## 4. Runtime acceptance — added to the testnet session (`docs/review-protection-batch2.md` §5)

To item a (a crossed stop → the R1 action): check that the log shows "M.SL anchor pinned …"; then restart the
app while the R1 limit rests, and check that the log shows "re-pinned … (restored)". End flat.

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **`SB3-fix/E1` seat:** Opus 5.5, high — the restore path and the emergency baseline.
- **Its review:** coordinator, Opus 5.5, high.
