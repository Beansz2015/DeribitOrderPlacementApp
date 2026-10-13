# Impl report — refused edits: the app's record follows the exchange; a stuck stop pages

**Written 2026-10-14 (GMT+8) by the refused-edits implementer seat (Opus 5.5, high).**
**Spec:** `docs/spec-refused-edits.md` (owner rulings `RE-R1` to `RE-R4`, all (a), 2026-10-13).
**Code commit:** `ddb2984` (on top of `f198d01`). Local only; nothing pushed.
**Scope held:** code and fixtures only. No app launch, no testnet, no trade, no bridge arm.

IDs in this report carry the prefix `RE-` (refused edit), as in the spec. `RE-1`…`RE-4` are the spec's build
items (`docs/spec-refused-edits.md` §3). `RE-R1`…`RE-R4` are the owner's rulings (the same doc, §0). `RE-SB1`…
are this seat's spec-back findings (section 5 below). None of them is batch 2's `R1` or a `PF-…` ID.

---

## 1. Summary

| Item | Built | Where (`frmMainPageV2.vb`, lines at `ddb2984`) |
|---|---|---|
| `RE-1` — a refused stop edit | The reply to 223346 / 223348 / 223350 that is an error (10028 included, the benign `already_closed` race excluded) sets `placedStopLossPrice` to the stop's price in batch 2's live-order map. Yellow log `SL edit REFUSED (<code>) — stop still at $X; chase resumes from there` | handler `:6786`; seam `ReferenceAfterEditReply` `:6536` |
| `RE-2` — a stuck triggered stop | Refused 223350 replies count up; a confirmed echo resets the count. At 3, or with the confirmed price behind the chase target for more than 10 s (batch 2's one-second watchdog tick), one urgent page per triggered stop, with the spec's text | state machine `:6671`–`:6720`; tick `:6746`, called at `:6963`; confirmed reset `:3686` |
| `RE-3` — a refused TP resize (id 42) | The leg-size check (F3) acts again on the SL-edit backoff's schedule (0.67 s, doubling, 5 s cap) until the TP matches. One page per episode. The id-42 single-flight gate is unchanged | seams `:7326`–`:7346`; reply `:7274`; tick `:7403`, `:7428` |
| `RE-4` — a refused entry edit (id 223344) | Each 223344 send keeps its order id. A refused **entry** edit sets `placedPrice` to the entry's echoed price; yellow log `Entry edit REFUSED (<code>) — order still at $X`. A refused TP edit (same id) does not touch it | keep `:5198`; match `TakeEditSend` `:6570`; decide `EntryRefusalResets` `:6586` |
| Do-not-touch (spec §2) | The M.SL anchor latch at the first post-trigger reposition, the `SB3` pin and its restart re-pin, the emergency send order: **no line changed** | `:2949`–`:2952` (latch) unchanged |

## 2. What changed, by site

`frmMainPageV2.vb`, line numbers at `ddb2984`.

| Site | Change |
|---|---|
| `:1648` connect | `ResetEditSends()` beside `ResetLiveOrderMap()`: replies to the old socket never arrive |
| `:1797` receive loop | `HandleRefusedEditResponse(response)` runs **after** `HandleRateLimitError` and `HandleUnhandledJsonRpcError`. Their lines print first; their behaviour is unchanged |
| `:2049` `HandleUnhandledJsonRpcError` | The benign-race test now calls the shared seam `IsBenignEditRace` (same logic: the message contains `already_closed`, case-insensitive; `Nothing` is not benign). No other line in that handler changed. `HandleRateLimitError` is not touched |
| `:2676`, `:2942` | Comments only: the optimistic advances stay; the reply now corrects them |
| `:3686` confirmed-success site | `NoteTriggeredStopConfirmed()` beside `slUpdateFailures = 0` (the "confirmed success clears the backoff" site, reused, not forked) |
| `:5194`–`:5198` `SendRateLimitedUpdate` | A 223344 send (no trigger price) keeps `(order id, is entry = orderType "main")` **before** the send; a failed send drops it |
| `:6494`–`:6835` new section "Refused edits" | The seams, the state, `LiveOrderPrice`, the RE-2 state machine, `HandleRefusedEditResponse` |
| `:6963` watchdog | `EvaluateStuckStop()` after `EvaluateLegSizeCheckAsync()` in the same one-second tick |
| `:7273`–`:7281` `HandleTpGuardResponse` | A refused id-42 (not a gone TP) calls `NoteTpResizeRefused`: schedules the retry; the `TP resize REFUSED` page goes only on the episode's first refusal |
| `:7315`–`:7365` | RE-3 state (`legResize*`, under `liveOrderGate`) and seams |
| `:7389`–`:7413`, `:7428` `EvaluateLegSizeCheckAsync` | Episode start/end resets the retry state; a due retry re-arms the episode's action; a retry logs `… (retry after a refused resize)` and does not page |

**Pure `Friend Shared` seams (every decision):** `IsBenignEditRace`, `ClassifyEditReply`, `EditWasRefused`,
`ReferenceAfterEditReply`, `SlEditRefusedText`, `EntryEditRefusedText`, `TakeEditSend`, `EntryRefusalResets`,
`StuckStopPages`, `StopTrailsChaseTarget`, `MslCapPrice`, `MslCapArmed`, `StuckStopPageText`, `StuckStopForStop`,
`StuckStopOnRefusal`, `StuckStopOnConfirmed`, `StuckStopOnTick`, `TpResizeRetryAfterRefusal`, `LegSizeCheckRearms`,
`LegSizeActionPages`, `TpResizeRefusalPages`.

**How each piece decides:**
- **Which stop (RE-1).** The stop the chase tracks: `PositionSLOrderId`, else `CurrentSLOrderId`. Its price comes from
  its latest echo in the live-order map. Pre-trigger, `placedStopLossPrice` already mirrors every untriggered echo
  (`:3793`), so a refused pre-trigger edit resets it to the value it already has.
- **10028 (RE-1, RE-2).** Classified `RateLimited`: it resets the reference and counts for `RE-2`. It is **not**
  passed to `BackoffStopLossRetry`: the N1b coupling still returns early for 10028, unchanged.
- **The benign race.** `already_closed` on any edit id: no reset, no count, no log from the new handler. This is
  exactly what `HandleUnhandledJsonRpcError` returns early on, through the same seam.
- **A torn-down reference.** `ReferenceAfterEditReply` never turns a 0 reference into a price, and the handler stands
  down while a cancel is pending. A late refusal cannot undo a close or cancel teardown.
- **The 223344 match (RE-4).** A success reply names its order (`result.order.order_id`) and takes that send. An
  error reply takes the oldest outstanding send. A success that names no kept send takes nothing. Sends older than
  10 s are dropped. Only a refusal of the **current** entry (`CurrentOpenOrderId`) resets `placedPrice`.
- **The stuck-stop clock (RE-2).** "Behind" is the chase's own one-tick test applied to the confirmed price
  (long: bid + 0.5 < confirmed; short: ask − 0.5 > confirmed). The clock restarts when the stop is not behind, and on
  a confirmed echo. A disconnected socket or an unseeded map counts as "not behind".
- **The page text (RE-2).** `Stop cannot be moved: <n> edits refused (<last code>) — stop at $X, market $Y. M.SL cap
  at $Z still armed`. Market = the bid for a long, the ask for a short. Cap = anchor − M.SL (long) or + M.SL (short),
  the anchor the hoisted M.SL check uses. When the M.SL is not armed, the tail reads `M.SL cap NOT armed` (`RE-SB3`).
- **The retry schedule (RE-3).** `TpResizeRetryAfterRefusal` calls `NextSlBackoff` and adds back the one throttle
  interval its stamp subtracts: 666 ms, 1332 ms, 2664 ms, then 5000 ms. The watchdog reads it at its one-second tick.

## 3. Verified, and how

| Claim | Result | How |
|---|---|---|
| Gate | **GATE PASSED**, OrderCheck **489/489** (466 + 23) | `tools/checks/verify-gate.ps1`, executed on the final tree before the commit |
| Censuses | Before (`2dead65`): **71 across 67 lines** (12 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 19). After (`ddb2984`): **73 across 69 lines** (**13** · 8 · **3** · 3 · 1 · 0 · 13 · 13 · 19) | `Select-String -AllMatches -CaseSensitive` per symbol, match counts summed, lines de-duplicated. Executed before and after |
| Census change 1 | `emergencyFired` 12 → 13: one **read** in `BuildStuckStopPage` (`:6776`), so the page does not call a fired M.SL "still armed". No write; the latch's set and clear sites are unchanged | read the line; grep |
| Census change 2 | `NextSlBackoff` 2 → 3: one call in `TpResizeRetryAfterRefusal` (`:7327`) — the spec's "on `NextSlBackoff`'s schedule". New comments name it "the SL-edit backoff" so they add no count | read the line; grep |
| Census `slUpdateFailures = 0` | Still 1: the RE-2 reset is a separate call beside it | grep |
| The new fixtures fail against the pre-change behaviour | **Yes — five mutations, each restoring today's behaviour in one seam:** | Executed: copy, mutate by script, build OrderCheck, run; restore by byte copy **and touch**; SHA-256 equal before and after (`e7ac3302d8ba7938…`); 0 `MUTATION` strings left; rebuilt → `OK 489/489` |
| ↳ `M-RE1` (`ReferenceAfterEditReply` returns the reference: today's optimistic record) | **FAILED 3/489** — RE-1 fixtures 2, 3; RE-4 fixture 18 | as above |
| ↳ `M-RE1b` (the benign race classed as a refusal) | **FAILED 3/489** — RE fixture 1; RE-1 fixture 4; RE-4 fixture 22 | as above |
| ↳ `M-RE2` (`StuckStopPages` never pages: today's log-only) | **FAILED 4/489** — RE-2 fixtures 6, 7, 8, 10 | as above |
| ↳ `M-RE3` (`LegSizeCheckRearms` never re-arms: today's one try per episode) | **FAILED 1/489** — RE-3 fixture 15 | as above |
| ↳ `M-RE4` (an error or a success always takes the oldest send) | **FAILED 2/489** — RE-4 fixtures 20, 21 | as above |
| Fixtures that pass under every mutation | RE-2 fixtures 9, 11–13; RE-3 fixtures 14, 16, 17; RE-4 fixtures 19, 23. They pin texts, arithmetic and the once-per-episode rules, which today's code also obeys or did not have | read |
| Line endings | `frmMainPageV2.vb` CRLF 9321 of 9321, BOM kept; `Program.vb` CRLF 2097 of 2097, no BOM (as before) | Python byte count |
| Edit success reply shape | Compact JSON with `"id":<n>` and `result.order.order_id` — what `TakeEditSend` and the fast path rely on | the recorded raw echoes in `docs/impl-report-pf-phase2.md` lines 215 and 231 (ids 42 and 223345) |
| Do-not-touch | The latch block, the `SB3` pin code, `ArmEmergencyClose` / `SendReduceMarketOrderAsync` order: no line in the diff | `git diff 2dead65 ddb2984` read in full |
| Decision-bias tripwire | `RE-2` episode scope and `RE-4` correlation: both `richer_option_wrong`, agreement 1.0 (5/5), matching my baseline written first. No `gives_up_for_economy` | `docs/harness-runs/decision-bias-20261013T1800Z-*` |
| Doc re-ranker | Fired once, for the census list. Top hit `docs/impl-report-ws-edge-audit.md`; the list itself was in `docs/HANDOVER-6.md` lines 47–54 | `docs` not changed by it |

## 4. Not verified

- **Runtime — `docs/spec-refused-edits.md` §4 item 3. OWED, needs the owner** (auto-mode refuses a seat's testnet
  order placement; the owner is away until 2026-11-25). The candidate is a temporary testnet-only switch that makes
  Deribit refuse the chase edit; its spec is written with the runtime seat.
- **Reply order on the socket.** `RE-4` assumes an error reply for a later 223344 request does not overtake the
  reply to an earlier one. Not checked. The one case it can mis-match is bounded (`RE-SB4`).
- **Reply versus echo order** (spec §5): that the live-order map holds the stop's or entry's latest echo when the
  refusal lands. Not checked; the code reads whatever the map holds.
- **The post-send race.** The chase writes its optimistic reference after its send returns. If a refusal reply were
  handled before that write, the write would win for that one edit (`RE-SB6`). Not observed; reasoned.
- **How often refusals happen.** Not measured. The page thresholds are the owner's ruling.
- **The x64 bin.** Not rebuilt: no runtime test in this seat. The next runtime seat rebuilds it.

## 5. Findings (scoped `RE-…`)

| ID | Kind | Meaning | State |
|---|---|---|---|
| `RE-SB1` | Spec-back against `docs/spec-refused-edits.md` §3 `RE-1` ("not the benign `already_closed` / `order_not_found` races that already `Return` early") | For **edit** ids only `already_closed` returns early. `order_not_found` / `not_open_order` returns early only for id 31 (the scoped entry cancel). Built as the code stands: on an edit id those are refusals. A stop cancelled outside the app therefore logs `SL edit REFUSED (11044) — the stop is not in the live-order map; chase reference unchanged` and counts toward the `RE-2` page; the no-stop watchdog pages too | Built; for the coordinator to accept or narrow |
| `RE-SB2` | Implementer choice inside `RE-R2` | **Episode = one triggered stop.** A confirmed echo resets the count and the 10-s clock, not the paged flag, so the same stop pages once. A new triggered stop id is a new episode. The clock reset on a confirmed echo stops a false page in a fast one-way market where every edit succeeds but the confirmed price is always one move behind. Tripwire `richer_option_wrong` 5/5 | Built; for the coordinator to accept |
| `RE-SB3` | Spec-back on the `RE-2` page text | The spec's text ends `M.SL cap at $Z still armed`. When the M.SL box is unticked, its threshold is 0, its anchor is unknown, or it already fired, that is false. Built: the tail reads `M.SL cap NOT armed` in those cases. The armed case is the spec's text, byte for byte (RE-2 fixture 12) | Built; for the coordinator to accept |
| `RE-SB4` | Implementer choice inside `RE-R4` ("decide by the order id in the request, kept with the send") | A FIFO of 223344 sends, matched as section 2 says. The one mis-match: a TP-template error that overtakes the entry's success reply in the bracket path. Effect: `placedPrice` is reset to the entry's own echoed price (at worst one duplicate edit to the same target) and the log names the entry once. The alternative, a new request id for the entry, changes wire ids and the benign-race range without a ruling. Tripwire `richer_option_wrong` 5/5 | Built; for the coordinator to accept |
| `RE-SB5` | Gap outside the rulings | The trailing-entry chase's **two-edit** arm (223347 trailing main + 223348 trailing SL, `UpdateStopLossForTrailingOrder`) also advances `placedPrice` at the send. `RE-R4` names 223344 only, so a refused 223347 still leaves `placedPrice` optimistic (same class as `TN-F3`). Its entry-only arm uses 223344 and is covered. A refused 223348 is covered by `RE-1` | Not built; a follow-up for the coordinator |
| `RE-SB6` | Residual | The post-send race in section 4. Bounded to one edit; the next refusal corrects it. Moving the optimistic write before the send would change the chase code the N1c spec ruled stays as it is | Accepted by reasoning; for review |
| `RE-SB7` | Observation | `RE-3` re-arms at the watchdog's one-second tick, so the first retries land at about 1, 2 and 3 s, then every 5 s, not at 0.67 / 1.33 / 2.66 s exactly. A tighter timer would fork the watchdog, which batch 2's rule forbids | Built; noted |

## 6. Files

- `DeribitOrderPlacementApp/frmMainPageV2.vb` — the change.
- `tools/OrderCheck/Program.vb` — 23 fixtures, block "Refused edits" (lines 1937–2085).
- `docs/harness-runs/decision-bias-20261013T1800Z-{population,baseline,jev}.json` — the tripwire run (stamp is the
  file name's; the run itself was 2026-10-13T17:29Z).
- `docs/outstanding.json` — this seat's row removed; the review row added.

## Model and effort (`docs/HANDOVER-6.md` §7b)

- **Review of `ddb2984`:** coordinator, **Opus 5.5, high** — the triggered-SL chase, the entry chase and the edit
  replies.
- **Runtime (`docs/spec-refused-edits.md` §4 item 3), after the owner returns:** **Opus 5.5, high**.
- **`RE-SB5` follow-up (the 223347 trailing-main refusal), if the owner rules it in:** **Opus 5.5, high**.
