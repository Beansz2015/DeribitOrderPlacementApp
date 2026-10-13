# Coordinator review — refused edits (RE-1 to RE-4)

**Reviewed 2026-10-14 (GMT+8) by the coordinator seat (Opus 5.5, high).**
**Under review:** commit `ddb2984` (report `b9ffd21`), against `docs/spec-refused-edits.md` and
`docs/impl-report-refused-edits.md`.
**Verdict: ACCEPTED at code level.** Runtime (`docs/spec-refused-edits.md` §4 item 3) is owed after the owner's return.
One small follow-up (`RE-SB5`, below), folded into the batch-3 implementer's brief.

IDs: `RE-1`…`RE-4` are the spec's build items; `RE-R1`…`RE-R4` the owner's rulings; `RE-SB1`… the seat's spec-back
findings. `TN-F3` is the 2026-10-12 testnet finding this work closes in code.

---

## 1. Verified at the artefact

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 489/489** | `tools/checks/verify-gate.ps1`, executed by this seat at `b9ffd21` (code = `ddb2984`) |
| Nine `frmMainPageV2.vb` censuses | **73 occurrences across 69 lines** (13 · 8 · 3 · 3 · 1 · 0 · 13 · 13 · 19). `emergencyFired` +1 is a **read** in the stuck-stop page (so a fired M.SL is not called "still armed"); `NextSlBackoff` +1 is the RE-3 schedule the spec names | executed; `CLAUDE.md` census line updated |
| Do-not-touch (`docs/spec-refused-edits.md` §2) | `git diff 2dead65 ddb2984`: **0** removed lines touch the M.SL latch, the `SB3` pin, `ArmEmergencyClose`, the reduce send or the `emergencyFired` latch. 6 code lines removed in all; each read | diff, filtered and read |
| The edit send still goes out | `SendRateLimitedUpdate`: the one replaced line is the send itself, now preceded by recording the 223344 order id and followed by forgetting it if the send failed | read the hunk |
| `ClassifyEditReply` | no error → Accepted; `10028` → RateLimited (counts as refused for RE-1/RE-2, **not** coupled into the backoff); `already_closed` → BenignRace (no action); else Refused | read |
| `ReferenceAfterEditReply` | resets only a live reference (> 0) to a known echoed price (> 0) on a refusal; never revives a torn-down 0 | read |
| `HandleRefusedEditResponse` | positive id gate after `HasValue` (memory `ws-handler-id-guards` satisfied); stands down while a cancel is pending; 223344 resets `placedPrice` only for a refused **entry** edit of the **current** entry; SL ids reset `placedStopLossPrice` from the tracked stop's live echo; only 223350 feeds RE-2 | read |
| No conflict with the manual-SL-edit discriminator | After RE-1, `placedStopLossPrice` equals the stop's live echo price, so the next echo is not mistaken for a manual edit | reasoned from the reconcile branch (`price.Value <> placedStopLossPrice …`) |
| RE-3 re-arm | `LegSizeCheckRearms`: only Oversize/Undersize, only after an act, only when the retry time is due; PF-R4/PF-SB3 cancels unaffected | read |
| Mutation proofs (5 mutations) | Carried from the report, **not re-run by me** | report §3 |

## 2. Findings — dispositions

| ID | Meaning | Disposition |
|---|---|---|
| `RE-SB1` | On edit ids, only `already_closed` is benign; `order_not_found` / `not_open_order` count as refusals | ✅ Accepted as built. A stop cancelled outside the app during a triggered chase is a real loss of protection; paging it is right. A filled stop returns `already_closed` (observed), and the chase stops at the close, so a false RE-2 page needs three such replies — not expected |
| `RE-SB2` | Episode = one triggered stop; a confirmed echo resets the count and clock, not the paged flag | ✅ Accepted |
| `RE-SB3` | Page tail says `M.SL cap NOT armed` when it is not | ✅ Accepted: the spec's text would have been false there |
| `RE-SB4` | FIFO matching of 223344 replies; one bounded mis-match | ✅ Accepted; a new request id would change wire ids without a ruling |
| `RE-SB5` | The trailing-entry chase's main edit (223347) still advances `placedPrice` optimistically | **Follow-up, within `RE-R4`.** The owner's question was about a refused **entry-chase** edit in general; 223347 is the trailing entry's chase. Folded into the batch-3 implementer's brief as a small extra item |
| `RE-SB6` | Post-send race: a refusal handled before the chase's own optimistic write loses to it, for one edit | ✅ Accepted as residual; the next refusal corrects it |
| `RE-SB7` | RE-3 retries land on the watchdog's 1-s tick, not the exact backoff times | ✅ Accepted; forking the watchdog is forbidden |

## 3. Still owed

- Runtime (`docs/spec-refused-edits.md` §4 item 3), after the owner's return: a temporary testnet-only switch that
  makes Deribit refuse the chase edit; check the reference reset, the stuck-stop page, the TP retry and the entry reset.
- The SHORT side; reply-order assumptions (report §4).

## §Model and effort

- Batch-3 implementer (with `RE-SB5` folded in): Opus 5.5, high.
