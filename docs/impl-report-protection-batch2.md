# Implementation report — position protection, batch 2

**Spec:** `docs/spec-protection-batch2.md` (owner rulings R1–R4, 2026-10-10).
**Built:** 2026-10-12 (GMT+8), on top of `96371d4`. One commit. **Not pushed** (the owner is the only pusher).
**Implementer:** Opus 5.5, high. **Review owed:** coordinator, Opus, high.
**Line numbers:** after this change, in `DeribitOrderPlacementApp/frmMainPageV2.vb` unless named.

## 1. What changed, by item

### Item 1 — the rejected stop leg (`docs/spec-protection-batch2.md` §1, owner ruling R1)

| Change | Where |
|---|---|
| New branch in the order-state chain: `orderState` **starting with** `rejected`, any label. Pure seam `IsRejectedOrderState` (prefix, ordinal). | `:3914`, `:6557` |
| Stop labels (`StopLossOrder`, `TrailingStopLoss`): red log, `Alert("no_stop")`, urgent page "Stop REJECTED by the exchange (<reason>) — <side> <size> position has NO STOP". Side from the leg's direction (sell = LONG). Size from the position when known, else the leg's amount. Other labels: yellow log, normal-priority page. | `NoteRejectedOrder`, `:6612` |
| Audit G2: the dead leg leaves `working.stop`. `placedStopLossPrice` (what the feedback reads) goes to 0 when no triggered stop is live. `PositionSLOrderId` / `CurrentSLOrderId` are dropped only if they hold the dead leg's id. The handler's tail publish carries it. | `NoteRejectedOrder` |
| The R1 action waits for the position. The push lands before the fill (record Appendix A.1 carries `positions: []`), so the context is parked and `TryActOnRejectedStopAsync` runs it once the position is known: at the tail of every `user.changes` message and on each watchdog tick. Single-fire by a claim under a lock. It expires after 30 s and is dropped at each fresh placement and at the close. | `:6655` |
| R1 decision, pure seam `DecideRejectedStopAction`: the M.SL test of `UpdateStopLossForTriggeredStopLossOrder` with its pre-trigger baseline (the leg's trigger) against the own-side quote (`BestAskPrice` long, `BestBidPrice` short). M.SL unticked, threshold 0, or an unknown trigger/quote → the limit. | `:6578` |
| **Flatten:** the M.SL emergency's own sequence — latch `emergencyFired`, `CancelOrderAsync`, `ArmEmergencyClose`, `SendReduceMarketOrderAsync`, `ReportEmergencyCloseSend` (batch 1's SENT/CONFIRMED/FAILED). Refuses if an emergency is already in flight. Cancel-then-reduce order unchanged. | `TryActOnRejectedStopAsync` |
| **Stop limit:** reduce-only, post-only, `reject_post_only: false` limit, label `StopLossOrder`, position size, at the leg's `price` (`RoundToTick`), id 40. Its `open` echo goes through the existing `StopLossOrder` case, the same "Trigger FLIP" adoption a real trigger gets, so the chase and the M.SL cap take over. An id-40 error pages urgent "Stop limit REJECTED … position has NO STOP". | `SendReplacementStopLimitAsync`, `:6719`; `:2011` |
| Page of what was done: "… flattening" or "… stop limit placed at <price>". | as above |

### Item 2 — the live no-stop watchdog (`docs/spec-protection-batch2.md` §2, owner ruling R2: alarm only)

| Change | Where |
|---|---|
| An order map: seeded from the id-778 snapshot at each connect (ahead of its `cancelPending` gate), then kept current from every `user.changes` order. Open/untriggered = live; any other state removes the id. | `ApplyOrderToLiveMap` `:6439`; `:3487`; `:6836` |
| "A live stop" = batch 1's `IsClosingStop` over the map (`HasClosingStop`). The rule is reused, not forked. | `:6454` |
| The trigger link (experiment (e)): a **live** order's `trigger_order_id` removes the id it names. A triggered echo removes only itself. See section 3, finding 1. | `ApplyOrderToLiveMap` |
| Once a second (`RunProtectionWatchdogAsync`, started from Load): position ≠ 0, no live stop, for > 5 s → red log, `Alert("no_stop")`, urgent page "Position <side> <size> USD has had NO STOP for 5 s". Once per episode; the episode ends when a stop reappears or the position goes flat. Pure seam `NoStopWatchdogFires`. | `:6465`, `:6507`, `:6537` |
| Stands down while disconnected and until this connection's snapshot lands. Batch 1's restore NO STOP page marks the episode as paged, so a restart with no stop pages once, not twice. | `EvaluateNoStopWatchdog`; `EvaluateRestoreStopCheck` |

### Item 3 — cancel-on-disconnect check at connect (`docs/spec-protection-batch2.md` §3)

- After the id-777/778 requests, `private/get_cancel_on_disconnect` with `scope: "account"`, id 41 (`:1685`).
- ON: red log, `Alert`, urgent page "Deribit cancel-on-disconnect is ON — every disconnect deletes your stops". OFF: one gray line. Error or an unexpected shape: yellow, carry on. Never changes the setting (`:6784`). Pure seam `CancelOnDisconnectEnabled` (`:6764`): only a JSON boolean counts.
- **Method and shape confirmed on testnet 2026-10-12** by a read-only HTTPS call with the testnet key: `{"result":{"enabled":false,"scope":"account"}}`. The probe printed no key or token, and was a scratch script, not committed.

### Item 4 — say what is true (`docs/spec-protection-batch2.md` §4)

- The three comments that said the exchange trails the SL are corrected (the fill-reanchor comment, the trailing-entry comment, the `ReanchorTPToFillAsync` header).
- `frmMainPageV2.Designer.vb`: the `Trig.O.:` label (`CustomLabel11`) had **no** tooltip. It now has one: "Trigger offset. No effect on OTOCO stops: Deribit drops it inside the bracket, so the stop does not trail." The payload is untouched.

### Item 5 — Reduce while a triggered stop is closing (`docs/spec-protection-batch2.md` §5, owner ruling R4)

- `btnReduceLimit_Click` (`:7499`): when `SLTriggered` and `PositionSLOrderId` is set, log "Reduce not sent: the triggered stop is already closing the position at the top of the book" and send nothing. Pure seam `ReduceLimitRefused` (`:6818`). `btnReduceMarket_Click` unchanged.
- An `11030` on id 1 (reduce orders) now logs "reduce-only rejected — a reduce-only order may already cover the position, or the position is already flat (code 11030 - <message>)" in place of the generic API ERROR line (`:2030`).

### Item 6 — the market entry's fill line (`docs/spec-protection-batch2.md` §6)

- New `EntryMarketOrder` case in the `filled` branch (`:3816`): status label, "Position entered: <side> <size> @ $<avg fill>", `Alert("entry_fill")`. It does not set `OpenPositions`; the legs' open echoes still make the in-position transition, as before.

## 2. Verification — what I checked, and how

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 426/426** (376 before; 50 new) | `tools/checks/verify-gate.ps1`, executed |
| Build warnings | 0 | `dotnet build … -c Debug --no-incremental` |
| New fixtures fail against the old code | Yes — every new fixture calls a seam that does not exist at `96371d4`, so OrderCheck does not compile there | by construction; not executed against `96371d4` |
| Map fixture 9 catches my own first-draft defect | Yes — it replays Appendix A.5 and fails on the first draft (section 3, finding 1) | reasoning from the draft's code; not executed against the draft |
| The nine censuses | **70 occurrences across 66 lines.** `emergencyFired` 10 → **12**; the other eight unchanged (8 · 2 · 3 · 1 · 0 · 13 · 13 · 18) | `Select-String -AllMatches -CaseSensitive`, summed |
| The `emergencyFired` +2 | The R1 flatten path: `If emergencyFired Then` (refuse a second emergency) and `emergencyFired = True` (the latch, set before the first Await, like the M.SL branches) | read the two lines |
| COD method and response | Confirmed on testnet | see item 3 |
| Line endings | CRLF kept in the working copy | byte check after one script edit |

**Not verified:**
- **No runtime test.** Nothing here ran in the app. Acceptance `docs/spec-protection-batch2.md` §7 item 2 (a–e) is owed: harness or owner, testnet.
- **The x64 bin does not carry this code.** The gate builds AnyCPU only. Rebuild x64 before any runtime check.
- The R1 limit's adoption through the open echo is by code reading of the existing `StopLossOrder` case, not observed.
- The buy-side rejection reason (`price_too_low`) is still unobserved; the prefix match covers it.

## 3. Findings for the coordinator

1. **D1** (a batch-2 implementation defect, found and fixed before commit — the watchdog would have paged on every normal entry). My first draft removed the id named by `trigger_order_id` on **every** echo. In the record, a triggered OTO template names its **child** there (Appendix A.5: `OTO-11031638` names the live `SLTS-11031639`). The draft deleted the live SL leg at each fill. Fixed: only a live order's link is followed. Map fixture 9 replays A.5.
2. **SB1** (a batch-2 spec-back finding — the spec assumes the price is always past the trigger). `docs/spec-protection-batch2.md` §1 says "Otherwise (the price is past the trigger, which is why it was rejected)". The exchange tests `last_price`; the code tests the own-side quote, which can sit on the other side of the trigger by the spread. In that case the stop limit is still placed, repriced to the top of the book, and the chase runs — so the position closes about where the stop would have. I built the two-way rule as written.
3. **SB2** (a batch-2 spec-back finding — a rejected `TrailingStopLoss` has no limit price). §1 says "at the leg's `price`". A trailing stop has none. The code falls back to the leg's `trigger_price`; with neither, it places nothing and logs red "Place a stop by hand". The page and the watchdog still fire.
4. **SB3** (a batch-2 spec-back finding — the M.SL cap after an R1 limit). The limit's open echo seeds `emergencyBaseline` at its price, which settles at the top of the book at the first chase — exactly as for a real trigger in a gap. So after an R1 limit, the M.SL distance is measured from the market at that moment, not from the dead leg's trigger. This is spec-literal ("the same triggered-stop context a real trigger sets"). Flagged because total loss can reach trigger distance plus M.SL.
5. **SB4** (a batch-2 spec-back finding — `Trig.O.` had no tooltip to change). §4 says "Change the `Trig.O.` tooltip"; there was none. I added one to the label.
6. **Watchdog blind spot, by design.** If the id-778 snapshot fails, the map stays unseeded and the watchdog stands down for that connection. The snapshot handler already logs the error yellow. Seeding from an empty list instead would false-page.

## 4. Model and effort for the review (`docs/HANDOVER-6.md` §7b)

- **Model:** Opus. **Effort:** high. Receive, order and emergency paths.
