# Coordinator review — position protection, batch 2

**Reviewed 2026-10-12 by the coordinator seat (Opus 5.5, high).**
**Under review:** commit `389a87a`, against `docs/spec-protection-batch2.md` (owner rulings R1–R4, 2026-10-10)
and `docs/impl-report-protection-batch2.md`.
**Verdict: ACCEPTED at code level, with one required follow-up (`SB3` fix, §3) before the runtime acceptance.**
Runtime acceptance (`docs/spec-protection-batch2.md` §7 item 2) is still owed. Nothing here has run in the app.

Finding IDs below are batch 2's own (`docs/impl-report-protection-batch2.md` §3). They are not the
partial-fill spec's `PF-*` IDs, and not the older `D1`s of other features.

---

## 1. Verified at the artefact

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 426/426** | `tools/checks/verify-gate.ps1`, executed by this seat at `6d7798d` (code identical to `389a87a`) |
| Nine `frmMainPageV2.vb` censuses | **70 occurrences across 66 lines**: `emergencyFired` 12 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 · `RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1 · `TakerFeeRate` 0 · `isPlacingOrder` 13 · `lastPlacementAdmittedUtc` 13 · `placedOrderSizeUsd` 18 | `Select-String -AllMatches -CaseSensitive`, summed; lines de-duplicated |
| The `emergencyFired` +2 | The R1 flatten's refuse-if-in-flight test and its latch, set before the first `Await` | read `TryActOnRejectedStopAsync` |
| Map fixture 9 guards the implementer's own first-draft defect | **Yes.** Mutation: `ApplyOrderToLiveMap` follows `trigger_order_id` on every echo → **FAILED 1/426**, map fixture 9 only. File restored with `git checkout`; OrderCheck rebuilt clean | executed |
| Every spec item has code and fixtures | Item 1 (rejected 12, R1 9), item 2 (watchdog 8, map 9), item 3 (COD 6), item 5 (R4 6); items 4 and 6 are text | read the diff and the fixture list |
| "A live stop" reuses batch 1's rule | `HasClosingStop` loops `IsClosingStop`; no fork. `IsClosingStop` counts an `open` `StopLossOrder`, so the R1 limit and a triggered limit both count | read `:6350` |
| R1 decision = the M.SL test before a trigger | `UpdateStopLossForTriggeredStopLossOrder` uses `emergencyBaseline` if set, else `StopLossTriggerOriginal` (the trigger). `DecideRejectedStopAction` measures from the leg's trigger, `>=`, same quote sides | read `:5251`–`:5267` and `:6578` |
| The R1 limit is adopted as a triggered stop | Its `open` `StopLossOrder` echo takes the flip branch (`wasTriggered` False): `SLTriggered`, `placedStopLossPrice`, `emergencyBaseline`, settle latch re-armed | read `:3556`–`:3605` |
| An orphan TP after an R1 limit closes the position | Cleared: `CompletePositionClose` calls `CancelOrderAsync` (socket permitting) | read `:6054` onward |
| Do-not-touch list | Emergency send order unchanged (cancel, then reduce); OTOCO payload unchanged; `IsClosingStop` reused | diff read |

## 2. The implementer's findings — dispositions

| ID (batch 2) | Meaning | Disposition |
|---|---|---|
| `D1` | Implementation defect, fixed before commit: the map dropped the live SL leg at every fill | ✅ Fixed and guarded; mutation-proven above |
| `SB1` | The spec assumed the price is always past the trigger when a stop is rejected; the own-side quote can sit inside the spread | ✅ Accepted as built. The limit is repriced to the top of the book and the chase runs. Folded into the spec |
| `SB2` | A rejected `TrailingStopLoss` has no limit price | ✅ Accepted: fall back to its trigger; with neither, place nothing and say so. Folded into the spec |
| `SB3` | After an R1 limit, the M.SL anchor settles at the top of the book, so the flatten fires only a further M.SL distance beyond the market. Worst-case loss is about the trigger distance plus up to 2× M.SL | 🔧 **Owner ruling 2026-10-12: pin the anchor at the dead stop's trigger.** Follow-up fix, §3 |
| `SB4` | `Trig.O.` had no tooltip to change | ✅ Accepted: one added to the label. Folded into the spec |
| Watchdog blind spot | If the id-778 snapshot fails, the watchdog stands down for that connection | ✅ Accepted by design: seeding from nothing would false-page. The snapshot error already logs yellow |

**Decision-bias tripwire on `SB3`** (`docs/harness-runs/decision-bias-20261012T0944Z-*`, baseline written first):
`richer_option_wrong`, stable 5/5. The seat's baseline was `no_richer_option`. Not flagged. The owner ruled on it
directly.

## 3. Required follow-up — the `SB3` fix (owner ruling 2026-10-12)

**Rule:** an R1 replacement limit's M.SL baseline is the **dead stop's trigger**, and it is settled (frozen).
The M.SL flatten then fires when the own-side quote is the M.SL distance past that trigger: the same test R1
used to choose the limit over an immediate flatten.

**Shape (the implementer chooses the details):**
- `TryActOnRejectedStopAsync`, limit branch: before the send, record the dead leg's trigger as a pending R1
  anchor (under `rejectedStopGate`).
- The `StopLossOrder` open-echo flip adoption: when an R1 anchor is pending, set `emergencyBaseline` to it and
  `emergencyBaselineSettled = True`, instead of the echo's price. `placedStopLossPrice` (the chase reference)
  still takes the echo's price. Consume the anchor.
- Clear the pending anchor wherever `DropRejectedStopContext` runs, and at the id-40 error (no limit exists).
- A trigger of 0 (`SB2`'s trailing case with no trigger) → no pin; today's behaviour.
- Pure seam for the baseline choice → OrderCheck: pinned when an anchor is pending; the echo price otherwise;
  no pin from 0. Include a fixture that fails against `389a87a`.
- One census symbol may move (`emergencyBaselineSettled` is not a census symbol; `emergencyFired` must not).

## 4. Observations — no action in batch 2

1. **Concurrent socket sends.** `SendWebSocketMessageAsync` (`:1416`) has no send lock. Many existing
   `Task.Run` paths already send. Batch 2 adds one more sender: the watchdog thread can run the R1 action. The
   .NET `ClientWebSocket` contract allows one send at a time. A collision would surface as "Error sending
   message" and a forced reconnect. **Not verified** whether .NET 9 serialises internally. The risk class predates
   batch 2. Queued as a hygiene row: serialise sends with one `SemaphoreSlim`.
2. **Item 6's side** comes from `ManagedSideIsLong()`, which can fall back to `TradeMode` before the position
   echo lands. The order's own `direction` is the truthful source. Log text only.
3. The watchdog reads `positionSizeUSD` across threads without a lock. A torn read lasts one tick; the 5 s grace
   absorbs it.

## 5. Runtime acceptance — what is owed (`docs/spec-protection-batch2.md` §7 item 2)

Run after the `SB3` fix is reviewed. Rebuild **x64** first; check `secrets.json` reads `testnet` and the title
shows `— TESTNET`. The procedure is `docs/spec-x1-testnet-experiments.md` §0. It can share one testnet session
with the X-2 experiment (`docs/spec-partial-fill-fix.md` §3).

| Item | Who can do it |
|---|---|
| a. A crossed stop → REJECTED page, then the R1 action; add: the M.SL anchor is the dead trigger (`SB3`) | Seat, via the harness |
| b. Cancel the SL by hand with a position open → one watchdog page after 5 s | Seat, by a direct **testnet** API call (`private/cancel` on the SL id) — **owner ruling 2026-10-12** |
| c. A normal trigger → no watchdog page | Seat, via the harness |
| d. COD on, then off, on testnet → the page on connect, then none | Seat, by direct **testnet** API calls (`private/enable_cancel_on_disconnect`, then `private/disable_cancel_on_disconnect`, scope `account`) — **owner ruling 2026-10-12**. The session must **end with COD OFF**, confirmed by `private/get_cancel_on_disconnect` |

**Owner ruling 2026-10-12 on b and d:** the seat may use the testnet key for these calls only, on testnet only,
and records each request and reply verbatim (no key or token). No order placement outside
`tools/place-and-verify.ps1`.
| e. Reduce Sell while a triggered SL is closing → the refusal, no `11030` | Seat, via the harness |

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **`SB3` fix seat:** Opus 5.5, high — the emergency baseline is on the stop path.
- **Testnet session (batch-2 acceptance + X-2):** Opus 5.5, high.
- **Review of the `SB3` fix:** coordinator, Opus 5.5, high.
