# Implementation Report — MEDIUM Gated Housekeeping (v2 bundle)

**Spec:** `docs/spec-medium-housekeeping.md` (base items 1–5; 2026-07-06 addendum items 6–17; 2026-07-22 addendum items 18–19)
**Branch:** `master` (single-branch discipline; started from `b8d4308`)
**Build:** Release + Debug builds green at every commit; gate ran after each commit — 96/96 OrderCheck fixtures PASS, repo guards all OK, **GATE PASSED** throughout.
**Push status:** NOT pushed. 24 local commits on master.
**Runtime scope:** NONE exercised by this seat. Smoke tests are a listed owner-runtime checklist below.

---

## Per-item summary

### Item 1 — Dead code after `Throw` in `AuthorizeWebSocketConnection` ✅ `141f81d`

**File:** `frmMainPageV2.vb`

Two `Throw New Exception(...)` / `Return` sites in `AuthorizeWebSocketConnection` each had an unreachable `AppendColoredText` call immediately after — dead because the `Throw` or `Return` exits the path before it. Deleted the two dead log calls (deletion-only). `RefreshWebSocketAuthentication` (the HIGH #3 rewrite) was also confirmed clean — no dead-after-throw lines there.

---

### Item 2 — Delete `ProcessEstimationData` stub + id-890 branch ✅ `b316f64`

**File:** `frmMainPageV2.vb`

Reference search confirmed zero callers of `ProcessEstimationData` and zero senders of id 890 in the codebase. Deleted:
- `Private Sub ProcessEstimationData(...)` stub body
- `Case 890` branch in `HandleMarginEstimationResponse` that called it

The id-777 live-position path (`HandlePositionSize`) is untouched and confirmed present.

---

### Item 3 — Consolidate duplicate `GetAccountSummaryLimits` ✅ `eb9bb79`

**File:** `frmMainPageV2.vb`

`GetAccountSummaryLimits` (no timeout) and `GetAccountSummaryLimitsWithTimeout` (30 s CancellationTokenSource) were identical in purpose. Verified behaviour parity: both sent id-9, read `result.maintenance_margin` and `result.initial_margin`. Deleted `GetAccountSummaryLimits`; updated both callers (`InitializeRateLimits`, `InitializeRateLimitsAfterAuth`) to call `GetAccountSummaryLimitsWithTimeout`. Reference search post-delete: zero remaining references to `GetAccountSummaryLimits`.

---

### Item 4 — Remove duplicate heartbeat enable call ✅ `d1dc5ef`

**File:** `frmMainPageV2.vb`

`EnableDeribitHeartbeatEnhanced` was called in both `ConnectToWebSocketDirectly` (the connect-sequence home) **and** again inside `AuthorizeWebSocketConnection` — two enable sends per connect. Deleted the call from `AuthorizeWebSocketConnection`. `ConnectToWebSocketDirectly` retains the call; reconnect path confirmed to pass through `ConnectToWebSocketDirectly`, so heartbeat still arms on both initial connect and reconnect.

---

### Item 5 — Magic number 0.0005 taker fee → named constant ✅ `77ae8b3`

**File:** `frmMainPageV2.vb`

Promoted the inline `0.0005` taker-fee literal in `HandleIndexUpdates` to:
```vb
Private Const TakerFeeRate As Decimal = 0.0005D ' Deribit perpetual taker fee (2026)
```
All three reposition-leeway magic numbers (`±3`, `±5`, `0.5` in `HandleQuoteUpdates`) were already named constants from a prior pass — no action needed there. Same value, no behaviour change.

---

### Item 6 — Delete dead methods and orphan field ✅ `9784d2c`

**Files:** `frmMainPageV2.vb`, `TradeAnalytics.vb`

Reference searches immediately before each delete confirmed zero live callers:

| Symbol | Location | Verdict |
|---|---|---|
| `HandleOrderUpdates` | `frmMainPageV2.vb` | 0 callers — deleted |
| `ExportTradesToCSV` | `frmMainPageV2.vb` | 0 callers — deleted (also broken on .NET 9: `Process.Start(fileName)` without `UseShellExecute`) |
| `LogFailedEntry` | `frmMainPageV2.vb` | 0 callers — deleted |
| Commented-out `LogFailedEntry(...)` call in `IsATRSlippageExcessive` | `frmMainPageV2.vb` | dead comment — deleted |
| `Private _autotradesettings As AutoTradeSettings` | `frmMainPageV2.vb` | only ref was `LogFailedEntry` (now gone) — deleted (NRE landmine) |
| `GetTradeStatistics` | `TradeAnalytics.vb` | 0 callers — deleted |

`FrmIndicators.UpdateEmaVwapLabels` skipped per spec (dying module).

---

### Item 7 — Remove unused NuGet packages ✅ `d9bff07`

**File:** `DeribitOrderPlacementApp/DeribitOrderPlacementApp.vbproj`

Removed `<PackageReference Include="EntityFramework" Version="6.5.1" />` and `<PackageReference Include="System.IO.FileSystem.DriveInfo" Version="4.3.1" />`. SQLite is used directly via `System.Data.SQLite`; neither removed package was transitively required. Build confirmed clean after removal.

---

### Item 10 — Stale id-2 hint in `RequestNameForId` ✅ `134d26a`

**File:** `frmMainPageV2.vb`

```vb
' Before:
Case 2 : Return " auth/entry order"
' After:
Case 2 : Return " auth"
```

Entry orders moved to ids ≥ 600000 (handled by `HandlePlacementResponse`) in a prior pass; the `" auth/entry order"` hint predated that change.

---

### Item 11 — Rewrite `SendReduceMarketOrderAsync` header comment ✅ `8040383`

**File:** `frmMainPageV2.vb`

Old comment said "Reads orderAmountVal, not txtAmount" — superseded by the position-model flatten (body now reads `positionSizeUSD`, falls back to `TradeMode`/`orderAmountVal` only when the model is unseeded) and omitted one caller. Rewrote to enumerate all four actual callers (`FlattenPositionAsync`, two emergency sites in `UpdateStopLossForTriggeredStopLossOrder`, `btnReduceMarket_Click`) and describe the actual read logic.

---

### Item 16 — Document emergency-delay trade-off at throttle gate ✅ `4bc322f`

**File:** `frmMainPageV2.vb`

Added an 8-line comment at the `MinStopLossUpdateInterval` throttle gate inside the triggered-SL block explaining: (a) a persistently failing SL edit delays emergency detection by up to 5 s (accepted trade-off vs. edit-storm), and (b) the emergency now measures from the frozen `emergencyBaseline` anchor per `spec-emergency-baseline-fix.md`. No code change.

---

### Item 17 — Delete duplicate "Capture the pre-echo triggered state" comment ✅ `c293678`

**File:** `frmMainPageV2.vb`

The open-`StopLossOrder` echo branch had the sentence "Capture the pre-echo triggered state BEFORE flipping it" written twice — once as a plain comment and once as part of the newer block ending with the hybrid-fix text. Deleted the earlier duplicate copy; kept the newer block verbatim. Comment-only deletion, zero IL change.

---

### Items 13 + 12 — `positionSizeUSD` local rename + `Decimal.Parse` cleanup ✅ `f941422`

**File:** `frmMainPageV2.vb`

**Item 13 (`positionSizeUSD` shadow in `btnEstimateMargins_Click`):**
Renamed `Dim positionSizeUSD As Decimal` local to `orderValueUSD` throughout the method. The engine's position-model field `positionSizeUSD` is now unambiguously the field, not a local shadow. Combined with the item-12 TryParse in the same method.

**Item 12 (`Decimal.Parse` → `TryParse`):**

| Site | Before | After |
|---|---|---|
| Receive path: filled-`EntryLimitOrder` TP-display block (inside `HandleOrderPositionUpdates` lambda) | `Decimal.Parse(txtManualTP.Text)`, `Decimal.Parse(txtTPOffset.Text)`, `Decimal.Parse(txtComms.Text)` | Read mirror fields `manualTPval`, `tpOffsetVal`, `commsVal` (kept current by `SyncTradeInputsFromUi`) — no parse at all |
| `btnEstimateMargins_Click` | `Decimal.Parse(txtAmount.Text)` | `Decimal.TryParse(..., orderValueUSD)` with early-return on failure |
| `btnEditTPPrice_Click` | `Decimal.Parse(txt.Text)` at multiple points | `Decimal.TryParse(txt.Text, d)` with `If(...)` pattern |
| `btnEditSLPrice_Click` | `Decimal.Parse(txt.Text)` | `Decimal.TryParse(txt.Text, d)` with guard; edit semantics (id 223346, no commanded-SL recording) untouched |
| `btnRefreshLiveData_Click` | `Decimal.Parse(txtPlacedPrice.Text)` | Uses engine field `placedPrice > 0D` directly — no parse, no shadow |

Lambda structure in `HandleOrderPositionUpdates` not restructured (exclusion per spec).

---

### Item 14a — `DatabaseInfo` event for `DeleteMultipleTrades` success ✅ `718aefa`

**Files:** `TradeDatabase.vb`, `frmMainPageV2.vb`

**Before:** `DeleteMultipleTrades` success path raised `DatabaseError($"Successfully deleted {n} trades")` → red "Database Error:" prefix in the log.

**After:**
- Added `Public Event DatabaseInfo(message As String)` in `TradeDatabase.vb` (after `DatabaseError`).
- Changed the success `RaiseEvent` to `RaiseEvent DatabaseInfo(...)`.
- Added `AddHandler tradeDatabase.DatabaseInfo, AddressOf OnDatabaseInfo` in `frmMainPageV2.vb` (beside the other handler wirings).
- Added `Private Sub OnDatabaseInfo(message As String)` that logs green via `AppendColoredText(..., Color.LimeGreen)`.

---

### Item 14b — `AutoTradeSettings.Me.Hide()` SKIPPED — tie-in re-code already removed it

**Evidence:** `AutoTradeSettings.vb` line 116 carries the comment `' no Me.Hide() here any more` — the tie-in re-code replaced the form and the skip condition stated in the 2026-07-22 addendum is TRUE. No action needed; no commit created.

---

### Item 14c — `TrySetResult` in `HandleAccountSummaryResponse` ✅ `3321b8b`

**File:** `frmMainPageV2.vb`

Two `tcs.SetResult(True)` calls (one in the normal-data branch, one in the error branch inside `HandleAccountSummaryResponse`) replaced with `tcs.TrySetResult(True)`. A late response arriving after the timeout no longer throws `InvalidOperationException` into the swallow-catch.

---

### Item 14d — Log cap in `AppendColoredText` ✅ `820ff38`

**File:** `frmMainPageV2.vb`

Added inside the marshalled lambda, before the append:
```vb
' item 14d: log cap — trim the front when the log grows large to prevent unbounded
' UI-thread work on long unattended sessions.
If rtb.TextLength > 400000 Then rtb.Select(0, 100000) : rtb.SelectedText = ""
```
Trim threshold: 400 000 chars; front-remove: 100 000 chars. The cap fires rarely (only after hours of dense logging) and keeps the RichTextBox in a usable range.

---

### Item 14e — Delete duplicate TP ternary in `ExecuteOrderAsync` BuyLimit branch ✅ `64256e6`

**File:** `frmMainPageV2.vb`

The BuyLimit branch had an `If/Else` block computing `takeProfitPrice`, immediately overwritten by an identical ternary on the next line. Deleted the redundant ternary; the `If/Else` block remains. Verified that both expressions produced the same result (`manualTPval > 0 → manualTPval`, else `entryPrice + tpOffsetVal`).

---

### Item 14f — Set `cancelPending` in `TrailingStopLossOrderAsync` ✅ `7d95efd`

**File:** `frmMainPageV2.vb`

Before the inline `cancel_all_by_instrument` send in `TrailingStopLossOrderAsync`, added:
```vb
cancelPending = True                              ' item 14f: guard receive echoes from this cancel
cancelPendingSince = DateTime.UtcNow             ' 4-s self-clear (same as CancelOrderAsync)
```
The 4-second self-clear in the receive loop handles reset. `CancelOrderAsync` was deliberately NOT called — it resets the entire SL context (`emergencyBaseline`, commanded-SL set, trigger baseline), which would break the trailing transition.

---

### Item 14g — PnL long branch: `BestAskPrice` → `BestBidPrice` ✅ `7000a69` **[owner-visible]**

**File:** `frmMainPageV2.vb`

The long-position PnL block used `BestAskPrice` for both the computed value and colour threshold. Closing a long position hits the bid; corrected to `BestBidPrice` in the long branch only. The short branch (`BestAskPrice`) is correct and untouched.

```vb
' Before (long branch):
lblPnL.Text = $"PnL: ${(BestAskPrice - placedPrice) * orderAmountVal / BestAskPrice:F2}"
If BestAskPrice >= placedPrice Then ...

' After (long branch):
lblPnL.Text = $"PnL: ${(BestBidPrice - placedPrice) * orderAmountVal / BestBidPrice:F2}"
If BestBidPrice >= placedPrice Then ...
```

---

### Item 18 — Atomic write in `AppUserSettings.Save()` ✅ `06176dd`

**File:** `DeribitOrderPlacementApp/AppUserSettings.vb`

Applied the same temp-then-rename pattern that `SignalBridge.PersistState` uses:
```vb
' Before:
File.WriteAllText(SavePath, json.ToString())

' After:
Dim tmp As String = SavePath & ".tmp"
File.WriteAllText(tmp, json.ToString())
File.Move(tmp, SavePath, overwrite:=True)
```
A crash mid-save can no longer truncate `orderapp-settings.json`. No format change, no new key. The `.tmp` file is transient and will not survive a completed save.

---

### Item 19 — `AutoEllipsis` on `lblBridgeStatus` ✅ `b668af3`

**File:** `DeribitOrderPlacementApp/AutoTradeSettings.Designer.vb`

Added `lblBridgeStatus.AutoEllipsis = True` after the label's text initialisation in `InitializeComponent`. The session-policy warning (`Ignored (keeping last good): session policy '<line>'`) can exceed the 460 px fixed width; with `AutoEllipsis` the text clips with `…` rather than silently mid-string. Label size/position untouched; UIA `Name` property unaffected (harness reads the full text via `Name`, not the visual truncation).

---

### Item 8b — Gate 6 pre-placement slippage checks on checkbox ✅ `8908c6e` **[owner-visible]**

**File:** `frmMainPageV2.vb`

Part (a) was already done (entry-chase v2 at `9c3c351` converted all 4 reposition gates to `AndAlso`). Only part (b) remained: 6 bare `IsATRSlippageExcessive(BestPrice, direction)` calls in `ExecuteOrderAsync` (4 sites) and `StopLossForTrailingOrderAsync` (2 sites) that ran unconditionally regardless of the checkbox.

Used `replace_all: True` to prepend `maxSlippageATRchecked AndAlso` to all 6 occurrences simultaneously:
```vb
' Before:
If IsATRSlippageExcessive(BestPrice, direction) Then

' After:
If maxSlippageATRchecked AndAlso IsATRSlippageExcessive(BestPrice, direction) Then
```

Post-edit grep confirms 10 total `maxSlippageATRchecked AndAlso IsATRSlippageExcessive` occurrences (4 pre-existing reposition gates + 6 new pre-placement gates). The checkbox is now the single arm switch for the entire feature.

---

### Item 9 — Remove 500 ms entry latency from 4 buttons ✅ `24084f9` **[owner-visible]**

**File:** `frmMainPageV2.vb`

Deleted `Await Task.Delay(500)` and its "Wait a moment for UI update" comment from:
- `btnLimit_Click`
- `btnNoSpread_Click`
- `btnMarket_Click`
- `btnTrail_Click`

The `btnEstimateMargins_Click` call before each delay is retained. The `Task.Delay(500)` in `HandleHeartbeat` (LED blink duration) is untouched.

---

### Item 15 — Convert display-only `Me.Invoke` → `UiInvoke` / `BeginInvoke` ✅ `bd34fbf`

**File:** `frmMainPageV2.vb`

`UiInvoke` (non-blocking `BeginInvoke` with handle-race guard) applied at all 13 display-only marshalling sites. `AppendColoredText` converted directly to `Me.BeginInvoke` (keeping the existing handle guard and try/catch-drop, per spec).

| Location | Sites | Change |
|---|---|---|
| `HandleHeartbeat` | 2 | `Me.Invoke` → `UiInvoke` |
| `HandleQuoteUpdates` top-bid/ask | 2 | `Me.Invoke` → `UiInvoke` (2 blocking round-trips per tick eliminated) |
| `HandleQuoteUpdates` PnL block | 6 → 3 | Consolidated: `pnlColorLong`/`pnlColorShort` computed before lambda; 6 sites → 3 `UiInvoke` calls |
| `CancelOrderAsync` | 2 | `Me.Invoke` → `UiInvoke` |
| `CompletePositionClose` | 1 | `Me.Invoke` → `UiInvoke` |
| `ProcessPositionData` | 1 | `Me.Invoke` → `UiInvoke` |
| `AppendColoredText` | 1 | `Me.Invoke` → `Me.BeginInvoke` |

**Exclusions applied (untouched):**
- `HandleOrderPositionUpdates` open-orders lambda and untriggered-legs lambda: mutate engine state (`placedPrice`, `CurrentOpenOrderId`, `CurrentTPOrderId`, etc.) — blocking ordering required.
- `LogTradeDecision`: synchronous snapshot-read of controls; blocking is the point.
- `btnEstimateMargins_Click`: UI-thread caller; the `Invoke` is inert there.

Ordering safety: posted-before-waited ordering is preserved through the UI message queue; any subsequent blocking `Invoke` reads (e.g. `LogTradeDecision` snapshot after `CancelOrderAsync` resets) see the queued writes already committed.

---

## Flag — Item 8 bid/ask drift (report only; no code change)

**Location:** `HandleQuoteUpdates` trailing-LONG reposition gate.

The trailing-LONG gate (grep: `IsATRSlippageExcessive` in the LONG trailing block, the third of the four reposition gates) passes `bestAsk` as the price argument:
```vb
If maxSlippageATRchecked AndAlso IsATRSlippageExcessive(bestAsk, direction) Then
```
The entry-LONG gate (immediately above it) correctly uses `bestBid`. Audit F18 identifies this as copy/paste drift. Changing it alters guard behaviour when the feature is armed (trailing LONG would compare against the bid, not the ask, which is the correct closing-side price for a long). **This is a pre-existing defect; spec ground rule 7 explicitly says "FLAG in report, do NOT fix."** Owner decision required before any code change.

---

## Owner runtime checklist

Spec smoke tests are deferred to owner runtime (no runtime scope for this seat). Suggested order:

1. **After item 7 (packages removed):** open the trade history grid — DB reads still work (SQLite not pulled transitively from removed packages).
2. **After item 8b + 9:** place a test order with slippage checkbox OFF → expect zero "slippage limit" log lines; cancel. Repeat with checkbox ON → slippage guard fires as expected. Verify the 500 ms latency reduction is subjectively noticeable on the four entry buttons.
3. **After item 14g:** open a long position (or simulate via quote updates) — confirm PnL display sign and colour are correct (green when bid > placed price, red otherwise).
4. **After item 15:** connect, let quotes flow, place and cancel an order — log lines still arrive in order; no duplicate or missing status lines around the cancel.
5. **Item-8 drift flag:** decide whether the trailing-LONG gate should use `bestBid` instead of `bestAsk`. If yes, raise as a separate targeted fix.

---

## Deviations

None. All items implemented exactly as specified. Item 14b correctly skipped (tie-in re-code condition TRUE). Item 8 part (a) correctly confirmed already done and not re-committed. The `replace_all` approach for item 8b was a mechanical choice to avoid any site count error; verified post-edit with grep.
