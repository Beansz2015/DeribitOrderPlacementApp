# Coordinator review — `SB3-fix/E1` (b): persist the R1 M.SL pin and re-pin at restore

**Reviewed 2026-10-12 by the coordinator seat (Opus 5.5, high).**
**Under review:** commit `5a815cc`, against `docs/review-sb3-msl-anchor.md` §3 (owner ruling 2026-10-12) and
`docs/impl-report-e1-pin-restore.md`.
**Verdict: ACCEPTED at code level.** No follow-up code. Runtime check owed in the testnet session
(`docs/review-sb3-msl-anchor.md` §4). Nothing here has run in the app.

IDs: `SB3-fix/E1` is the SB3 fix seat's escalation (a restart lost the pin). `E1-fix/…` are this seat's own
findings (`docs/impl-report-e1-pin-restore.md` §4). `R1` is batch 2's owner ruling on a rejected stop leg.

---

## 1. Verified at the artefact

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 441/441**; repo guard `OK r1-anchor.json untracked` | `tools/checks/verify-gate.ps1`, executed by this seat at `5a815cc` |
| Nine `frmMainPageV2.vb` censuses | **70 occurrences across 66 lines, unchanged** (12 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18) | `Select-String -AllMatches -CaseSensitive`, summed; lines de-duplicated |
| The write | Only where a pin is consumed (`TakeR1MslAnchor` > 0), with the echo's own `order_id`; temp file then move; failures log yellow | read the diff |
| The re-pin | id-778 restore, triggered-SL branch, **inside** the existing `If emergencyBaseline = 0D` guard; needs an ordinal `order_id` match; settled either way, as before | read the diff |
| The guard placement is right | After a restart the baseline is 0, so the re-pin applies. On a reconnect the live pin already holds, and the snapshot never overwrites a live baseline. Agreed with the seat's reasoning | read |
| "No echo before the snapshot" after a restart | Supported: `SLTriggered = True` is set in only two places, the live flip (`:3570`) and the restore seed (`:7101`). So no chase edit — the only source of a fresh echo for a resting limit — can run before the snapshot | grep |
| The file cannot outlive the pin | Deleted at: a real flip; the owner's manual edit; the four new-SL placement resets; inside `ClearR1MslAnchor` and `DropRejectedStopContext` (all their callers, incl. `CompletePositionClose`); a restore with no match, an empty order list, or no triggered stop | read every added call |
| No nested locks | `DeleteR1AnchorFile` runs after `End SyncLock rejectedStopGate`; `r1AnchorFileGate` holders take no other lock | read |
| The owner's manual cap wins | The manual-edit branch deletes the file, so a restart cannot re-pin over it (`SB3-fix/SB3`) | read `:3630` |
| Fixtures fail against `02260d8` | Carried from the seat: mutation → FAILED 3/441. **Not re-run by me** | report §2 |

## 2. The seat's findings — dispositions

| ID | Meaning | Disposition |
|---|---|---|
| `E1-fix/SB1` | Extra deletes at the four new-SL placement resets and at a real trigger flip | ✅ Accepted. They keep the invariant "the file exists only while `emergencyBaseline` holds the pin" |
| `E1-fix/SB2` | A connect's snapshot older than the pin can delete the file | ✅ Accepted as residual. It needs an R1 action inside the snapshot's round trip just after a connect; the in-memory pin is unaffected |
| `E1-fix/SB3` | A close between the R1 send and its echo leaves no file | ✅ Accepted as residual: a one-round-trip window |
| `E1-fix/SB4` | The record's `side` is not used by the restore decision | ✅ Accepted: the order id identifies the limit |
| Method note | A byte-copy restore keeps the old timestamp, so MSBuild can reuse the mutated assembly; touch the file before rebuilding | Recorded in the seat memory for future mutation runs |

**On the residuals and the owner's tolerance** (memory: never accept a path beyond one M.SL past the anchor):
`E1-fix/SB2` and `E1-fix/SB3` each need a restart inside a window of about one network round trip after a
rare event. Neither is a design gap the ruling left open. Both are recorded here, not queued.

## 3. Runtime check owed

`docs/review-sb3-msl-anchor.md` §4, inside the single testnet session (`docs/review-protection-batch2.md` §5):
after the R1 action, "M.SL anchor pinned …" and `r1-anchor.json` beside the **x64** exe; restart while the limit
rests → "re-pinned … (restored)"; after the trade ends flat, the file is gone.

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Testnet session** (batch-2 acceptance, the pin checks, X-2): Opus 5.5, high.
