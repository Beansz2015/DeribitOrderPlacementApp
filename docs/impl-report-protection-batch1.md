# Implementation report — position protection, batch 1

**Spec:** `docs/spec-protection-batch1.md` (owner go 2026-10-08). The spec's run order is met: the
chase-anchor reset (`docs/spec-chase-anchor-reset.md`) was already built, reviewed and approved
(`14db8c0`, `1ba9f9d`).
**Built:** 2026-10-08 (GMT+8), on top of `1ba9f9d`. One commit. **Not pushed** (the owner is the only pusher).
**Implementer:** Opus 5.5, high. **Review owed:** coordinator, Opus, high.
**Line numbers:** the spec cites HEAD `0bda3ad`. `14db8c0` shifted the region by about 17 lines. All
lines below are after this change.

## 1. What changed, by item

All code is in `DeribitOrderPlacementApp/frmMainPageV2.vb`. `RemoteNotifier.vb` is unchanged: its
`Post` already takes `priority:="urgent"`.

### Item 1 — reconnect forever, page the owner, bound the auth wait (`docs/spec-protection-batch1.md` §1)

| Change | Where |
|---|---|
| The reconnect loop retries until connected or `isClosing`. `maxReconnectAttempts` and the "manual intervention required" give-up are gone. The single-flight guard is kept. | `HandleWebSocketDisconnect`, `:1511` |
| Delay after failed attempt n: the pure seam `ReconnectDelayMs`. Attempts 1–9 keep the old 2/4/6/8/10/10/10/10/10 s. From attempt 10 on, the 30 s cap (`ReconnectCapMs`). The 2 s wait before attempt 1 is unchanged. | `:1459` |
| Paging: a watcher task (`WatchWsDownPagesAsync`) starts with each recovery sequence. Once a second it checks the pure seam `WsDownPagesDue`: 0 pages under 60 s, 1 at 60 s, one more every 10 min. Each page is urgent and names the position (`WsDownPageText`: "position OPEN: LONG 120 USD" or "no position open"). | `:1467`, `:1487` |
| Recovery: if a page went out (`ShouldPostReconnectLine`), one "WebSocket reconnected after N s" post, priority `high`. A drop that recovers inside 60 s posts nothing. | `HandleWebSocketDisconnect` success arm |
| The auth reply wait uses a linked token with a 10 s timeout (`AuthReplyTimeoutSeconds`). A timeout throws `TimeoutException("auth reply not received within 10 s")`, which fails the attempt; the loop retries. | `AuthorizeWebSocketConnection`, `:1285` |
| Both local `Alert("connection")` calls are kept. The first still fires at the drop. The second, which fired at the give-up, now fires with each page. | as above |

⚠ **`docs/HANDOVER-7.md` §6's trap "Recovery budget is ~72 s of backoff plus per-attempt connect time,
10 attempts" is now FALSE.** The loop never gives up. The first 72 s of delays are unchanged, so the
advice "hold a provoked outage to 20–30 s" still avoids a page. The coordinator updates the handover
(spec `docs/spec-protection-batch1.md` §1).

### Item 2 — side from the position (`docs/spec-protection-batch1.md` §2)

- New pure seam `ManagedSideIsLongFor(positionSize, tradeModeIsLong)` at `:3214`: long if size > 0,
  short if size < 0, else the toggle. The instance wrapper `ManagedSideIsLong()` reads `positionSizeUSD`
  and `TradeMode`, both plain fields.
- `SetTradeMode` logs one yellow line when the new toggle side disagrees with an open position's sign
  (`:6777`). Informational only, never a block.

**All 17 live `If TradeMode` sites.** The spec counts 18 by grep; the 18th is a commented-out line
(`'If TradeMode Then` in `LogTradeDecision`, old `:6315`). The table also covers the
other `TradeMode` reads that position paths had (`If(TradeMode, …)`, `(TradeMode = True)` in a condition).

| # | Method | Line now | Entry or position | Changed | Why |
|---|---|---|---|---|---|
| 1 | `HandleQuoteUpdates` — entry chase | `:2584` | entry | no | Chases a resting entry; the toggle is the entry's side |
| 2 | `HandleQuoteUpdates` — M.SL emergency `priceMovement` | `:2768` | position | **yes** | Triggered stop protects the position |
| 3 | `HandleQuoteUpdates` — `ForceStopLossUpdate(If(…))` (an `If(TradeMode,…)`, not in the 17) | `:2805` | position | **yes** | Same emergency |
| 4 | `HandleQuoteUpdates` — triggered-SL chase | `:2835` | position | **yes** | Chases the exit of the position |
| 5 | `HandleQuoteUpdates` — trailing-entry chase | `:2947` | entry | no | Chases a resting trailing entry |
| 6 | `HandleQuoteUpdates` — trailing TP trigger, `TPTrailprice` (an `If(TradeMode,…)`) | `:3056` | position | **yes** | Runs only when `isTrailingPosition` (in position) |
| 7 | `HandleQuoteUpdates` — trailing TP trigger compare | `:3062` | position | **yes** | Same block |
| 8 | `HandleQuoteUpdates` — `dispLong` P/L display (`Dim … = TradeMode`) | `:3083` | display | no | Already overridden by the position sign when a position exists |
| 9 | `HandleOrderPositionUpdates` — TP re-anchor at fill (an `If(TradeMode,…)`) | `:3489` | position | **yes** | Sets the live post-fill TP leg |
| 10 | `HandleOrderPositionUpdates` — `EntryTrailingOrder` open echo, TP display | `:3607` | entry | no | A resting trailing entry, not yet a position |
| 11 | `HandleOrderPositionUpdates` — "Position entered: LONG/SHORT" (two `If(TradeMode,…)` log lines) | `:3737`, `:3784` | position logging | **yes** | Position-side logging (spec). If the position echo has not landed yet, the helper falls back to the toggle, as before |
| 12 | `UpdateLimitOrderWithOTOCOAsync` — bracket geometry | `:4879` | entry | no | Re-anchors a pre-fill OTOCO bracket for a resting entry |
| 13 | `UpdateStopLossForTriggeredStopLossOrder` — M.SL fire branches (`(TradeMode = True/False)`) | `:5156`, `:5167` | position | **yes** | The emergency close. `emergencyFired` tokens untouched |
| 14 | `UpdateStopLossForTrailingOrder` — SL geometry | `:5294` | position (spec-listed) | **yes** | Spec names it. Flat (the normal case), the helper returns the toggle: no change |
| 15 | `StopLossForTrailingOrderAsync` — TP display after placing | `:5546` | entry | no | Placing a NEW trailing entry; `TypeOfOrder` already fixes the side |
| 16 | `TrailingStopLossOrderAsync` — trailing stop side | `:5624` | position | **yes** | The reduce-only trailing stop closes the position |
| 17 | `btnLimit_Click` | `:6893` | entry | no | New entry |
| 18 | `btnNoSpread_Click` | `:6917` | entry | no | New entry |
| 19 | `btnMarket_Click` | `:6953` | entry | no | New entry |
| 20 | `SendReduceMarketOrderAsync` — fallback `If(TradeMode,…)` | `:7029` | position, flat arm | no | Runs only when `positionSizeUSD = 0`, where the helper returns `TradeMode` anyway. The position arm above it already uses the sign |
| 21 | `btnEditTPPrice_Click` — trailing branch (two sites) | `:7066`, `:7077` | position | **yes** | Inside `isTrailingPosition` |
| 22 | `EditStopLossTo` — limit offset side (B.E. and T.S. buttons) | `:7163` | position | **yes** | Edits the position's stop. `btnBreakEven_Click` already took its trigger from the sign |
| 23 | `btnTrail_Click` | `:7250` | entry | no | Places a NEW trailing entry |
| — | `ApplyRiskBasedSize` `:369`, `btnEstimateMargins_Click` `:7625`, `:7640` (not `If TradeMode` sites) | — | entry | no | Size and margin of the next entry |

The 17 `If TradeMode` sites are rows 1, 2, 4, 5, 7, 10, 12, 14, 15, 16, 17, 18, 19, 21 (×2), 22, 23.

### Item 3 — restore checks for a stop (`docs/spec-protection-batch1.md` §3)

| Change | Where |
|---|---|
| Per-connection state, reset in `ConnectToWebSocketDirectly` beside `positionRestoreAnnounced = False`. | `:1619` |
| `ProcessPositionData` hands the first id-777 size since connect to `NoteRestorePosition`. | `:6473` |
| `HandleOpenOrdersSnapshot` hands the order list to `NoteRestoreOrders` **before** the empty-list `Return`, whose comment no longer says "flat restart" only. | `:6334` |
| `EvaluateRestoreStopCheck` runs once, when the second half lands. Pure decision: `RestoreNoStopAlarm` and `IsClosingStop` (the owner's ruling below). On alarm: red log, `Alert("no_stop")`, urgent page "Open position with NO STOP: LONG 10 USD - place a stop now". | `:6247`, `:6262`, `:6296` |
| `Alert` gains `Case "no_stop"`: always audible, no settings key. (Unknown kinds were already audible; the case documents it.) | `:5784` |

Nothing the restore re-adopts changed.

### Item 4 — the emergency close's messages (`docs/spec-protection-batch1.md` §4)

**The spec's question, answered:** the `HandleQuoteUpdates` M.SL gate (old `:2678`, now `:2768`–`:2805`)
is **not** a second send site. It calls `ForceStopLossUpdate`, which calls
`UpdateStopLossForTriggeredStopLossOrder`, where the two fire branches are. One send site, covered.

| Change | Where |
|---|---|
| `SendWebSocketMessageAsync` returns `Task(Of Boolean)`: True when the send completed. Its catch arms are unchanged. Every other caller discards the value. | `:1412` |
| `SendReduceOrderAsync` returns `Task(Of String)`: `Nothing` when sent, else the reason. Its flow is unchanged: a failed send still runs the code after it, as before. | `:4770`, `:4824` |
| `SendReduceMarketOrderAsync` returns `Task(Of String)`: "not connected - reduce skipped", "no position size and no Amount to reduce", or the reason from `SendReduceOrderAsync`. | `:7008` |
| Both fire branches: the send order (`emergencyFired = True`, `CancelOrderAsync`, reduce) is unchanged. The three "Executed" lines become one `ReportEmergencyCloseSend(side, reduceFailure)` call. | `:5156`–`:5175` |
| `ReportEmergencyCloseSend`: red log, `Alert("emergency_stop")`, urgent page. Text from the pure seam `EmergencyCloseSendText`: **"Emergency close SENT — awaiting fill (market sell)"** or **"Emergency close FAILED — position may have NO orders (market sell: <reason>)"**. On SENT it arms a 10 s watchdog. | `:5101` |
| Watchdog: still pending after 10 s → urgent page **"Emergency close UNCONFIRMED after 10 s — check the position"**. The state stays pending, so a later flat still pages CONFIRMED. | `:5101` |
| `CompletePositionClose` → `NoteEmergencyCloseFlat`: if pending → **"Emergency close CONFIRMED — flat"**, urgent. | `:5957`, `:5121` |
| `HandleUnhandledJsonRpcError`: an error on JSON-RPC id 1 (reduce orders) while pending → **"Emergency close FAILED — position may have NO orders (reduce rejected: <error>)"**, urgent. The hook sits **before** the handler's skips, so a 10028 rejection (the audit's `emergency-rejected` case) counts. | `:1973`, `:5134` |

## 2. Decisions the spec did not spell out — review these

| # | Decision | Why |
|---|---|---|
| 1 | **Every emergency page is urgent, including CONFIRMED and UNCONFIRMED.** | `RemoteNotifier.ShouldSend` drops a non-urgent post within 5 s of the last send. CONFIRMED usually lands within 5 s of the SENT page, so a `high` CONFIRMED would be dropped. |
| 2 | **The recovery line is `high`, not urgent.** | It is an all-clear. Risk: it is dropped if another non-urgent post went out in the 5 s before it. Nothing else posts during an outage, so this is unlikely. |
| 3 | **"Confirmed flat" = `CompletePositionClose`** (the `≠0 → 0` transition), not the `ReduceMarketOrder` filled echo. | The spec says "confirmed flat". A filled reduce echo is not proof of flat; the flat transition is. The reduce-fill echo path feeds `CompletePositionClose`. |
| 4 | **A rejection is matched by JSON-RPC id 1 while an emergency is pending.** The reduce payload's id is unchanged. | Changing the id changes the payload; the spec allows messages only. Residual: see section 3. |
| 5 | **The page watcher polls once a second** instead of hanging off the reconnect loop. | A connect attempt can block for 30 s (connect timeout) plus 10 s (auth). A page tied to the loop could be ~40 s late. |
| 6 | **`SendReduceOrderAsync` keeps running after a failed send** (it seeds the reduce-limit context and logs "order sent", as before). | Spec: messages only. Only the return value is new. |
| 7 | **The restore check uses the first id-777 since connect**, and ignores later id-777s and a second id-778. | Spec: "evaluate when the second of the two lands". id-777 is also requested on position changes and by a button; those are not the restore seed. |
| 8 | **The side-mismatch log fires in `SetTradeMode`**, at the moment the toggle changes. | One line per disagreement. Logging from the hot quote path would print every tick. |

## 3. Findings

Prefix rule from `docs/HANDOVER-6.md` §7bb. Scoped to this feature ("protection-batch1").

| ID | Kind | Finding | Disposition |
|---|---|---|---|
| `E1` (protection-batch1) | escalation, raised before the code | **What counts as a stop in the restore check.** The spec's literal parenthetical (the app's `StopLossOrder` only) pages a false urgent NO STOP on every restart in trailing mode, and when a stop was placed by hand. | ✅ **Owner ruled 2026-10-08: any closing stop.** Folded into `docs/spec-protection-batch1.md` §3. Decision-bias tripwire: `no_richer_option`, agreement 1.0, matching the implementer's baseline. |
| `SB1` (protection-batch1) | spec-back | `docs/spec-protection-batch1.md` §2 says "18 `If TradeMode` sites (grep)". 17 are live; the 18th is a commented-out line. | Note only; the table above covers all 17 plus the other position reads. |

**Residuals — seen, not fixed, outside this batch:**

- **JSON-RPC id 1 is shared.** `SubscribeToIndexPrice` also uses id 1, at connect. An error on that
  subscribe while an emergency close is pending would page a false FAILED. It needs a reconnect inside
  the pending window and a failing subscribe. The FAILED text quotes the error, so it is readable.
- **A manual Connect click during an outage.** The loop does not see it. Its next attempt reconnects
  again (≤30 s later) and only then stops the pages. This was already the case before this batch, for
  10 attempts; now it holds until that next attempt. Using `IsWebSocketConnected` as "recovered" is
  wrong: a socket that connected but failed auth also reads Open.
- **The single-flight release races a very fast re-drop.** `isReconnecting` is released in `Finally`
  after `ConnectToWebSocketDirectly` has started the new receive loop. If that loop dies within those
  microseconds, its `HandleWebSocketDisconnect` returns at the guard and nothing reconnects. This is
  older than this batch; "reconnect forever" makes it the only way the loop can still stop.
- **`order_type` field name.** `IsClosingStop` reads Deribit's `order_type` (`stop_limit`, `stop_market`,
  `trailing_stop`) only for stops the app did not label. I did not verify the field name against a real
  payload: no captured order JSON exists in the repo or the bins. The app's own labels do not depend on it.

## 4. Verified, and how

| Claim | How |
|---|---|
| Gate passes | `tools/checks/verify-gate.ps1` → `GATE PASSED`, run after the last code edit |
| OrderCheck 371/371 | Same gate run. 331 before (327 + the 4 `anchor-undo` fixtures from `1ba9f9d`) + 40 new: side 5, reconnect 5, page 11, restore 14, emergency 5 |
| The new fixtures fail against the old behaviour | Mutation runs, then the file restored. (a) `ManagedSideIsLongFor` → `Return tradeModeIsLong` and `RestoreNoStopAlarm` → `Return False`: `FAILED 8/371` (side 4, 5; restore 1, 2, 10, 12, 13, 14). (b) `ReconnectDelayMs` → 0 from attempt 10 (the give-up): `FAILED 2/371` (reconnect 2, 3). The page and emergency-text seams are new functions with no old counterpart; their fixtures pin the spec's texts and thresholds |
| Nine censuses unchanged: **68 occurrences across 64 lines**, each symbol exact (`emergencyFired` 10, `IsATRSlippageExcessive` 8, `NextSlBackoff` 2, `RecordCommandedSLPrice` 3, `slUpdateFailures = 0` 1, `TakerFeeRate` 0, `isPlacingOrder` 13, `lastPlacementAdmittedUtc` 13, `placedOrderSizeUsd` 18) | Case-sensitive occurrence count per symbol, before and after the change |
| No new compiler warnings | `dotnet build … --no-incremental`: 0 `frmMainPageV2.vb` warnings before and after |
| The M.SL gate in `HandleQuoteUpdates` is not a second send site | Code reading: it calls `ForceStopLossUpdate` → `UpdateStopLossForTriggeredStopLossOrder` |
| The audit scenarios map onto fixtures | Read `HEAD` of branch `claude/zen-cray-wy33a6`, `docs/audits/proofs/order-app-gap/src/Program.vb.txt`: `mode-flip` → side fixture 4; `restore` cases 1 and 2 → restore fixtures 1 and 2; `emergency-rejected` (id-1 error 10028) → emergency fixture 3; `trace-emergency-send-fails` → emergency fixture 2 |

**Not verified:**

- **No runtime run.** Nothing here was run against the exchange. The x64 bin was not rebuilt.
- **The wiring is verified by code reading only**, not by a test: that the watcher pages on time, that the
  reconnect success arm stops it, that `CompletePositionClose` and the id-1 hook clear the pending state,
  and that the restore halves meet. OrderCheck covers the pure seams only.
- **The auth timeout aborts the socket.** Cancelling `ReceiveAsync` aborts a `ClientWebSocket`; the next
  attempt disposes it and builds a new one. This is .NET behaviour as I know it, not observed here.
- The `order_type` field name (section 3).

## 5. Runtime checks owed — owner-driven (`docs/spec-protection-batch1.md` §5 item 4)

Rebuild x64 first, then confirm `Environment: testnet` and the `— TESTNET` title.

1. **Outage page.** No position. Pull the network for more than 60 s. Expect, in the log and on ntfy:
   one "WebSocket DOWN for 6x s, still reconnecting - no position open" page, attempts continuing past
   10 with 30 s gaps, then "WebSocket reconnected after N s" once the network is back.
2. **Short drop stays quiet.** Pull the network for 20–30 s. Expect no page and no recovery line.
3. **NO STOP alarm.** Open a testnet position. Cancel its stop by hand in the Deribit UI. Restart the app.
   Expect the red line, the alert sound and an urgent "Open position with NO STOP" page.
4. **No false alarm.** Restart with a position and its stop resting. Expect no NO STOP line.
5. **Optional: a stop placed by hand.** Replace the app's stop with a hand-placed sell stop-market and
   restart. Expect no alarm. This also confirms the `order_type` field name.

## Model and effort

- **Review model:** Opus. **Effort:** high. Receive path, order path, emergency path.
