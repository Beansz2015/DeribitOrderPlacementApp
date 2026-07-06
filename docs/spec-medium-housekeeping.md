# Implementation Spec — MEDIUM gated housekeeping (frmMainPageV2 plumbing cleanups)

**Severity:** ⚪ Low-risk cleanups (behaviour-preserving). Independent of each other; do anytime after the current runtime testing.
**Source:** `docs/CODE_AUDIT.md`. This file used to also carry #9/#10/#11 — those are now:
- **#9 (parse-once dispatcher)** → `spec-medium-dispatcher.md` (high-risk; consider deferring — see that spec).
- **#10 (decouple via clean API)** → `spec-medium-decouple.md` (highest-value; the contract for the new analysis module).
- **#11 (indicator-stream reconnect guard)** → **dropped.** It lives in `FrmIndicators`, which is being replaced; the new analysis module must own a single guarded connect/reconnect loop from the start (carry-forward requirement for that module's spec, not worth fixing in throwaway code).

**Project:** DeribitOrderPlacementApp — .NET 9 WinForms, VB.NET. `Option Strict Off` on legacy files.
**Start point:** `master` tip (`housekeeping-now` was merged and deleted 2026-07-03; single-branch discipline). Build green.
**2026-07-06:** Addendum appended below (audit F14–F18 + post-audit items, all re-verified against `master` @ `fc7bb6c`). This spec is now the complete housekeeping-v2 bundle of ROADMAP §2 item 5.

> ⚠️ Locate code by symbol; line numbers drift. All in `frmMainPageV2.vb` unless noted.

---

## Items (each an independent commit; all behaviour-preserving)

1. **`throw` followed by dead code** — `AuthorizeWebSocketConnection` has `Throw New Exception("Authorization failed…")` / `"Refresh token not found…"` immediately followed by an unreachable `AppendColoredText`. Keep the throw **or** the log (pick what fits), delete the dead line. Audit the rest of the auth path too (the HIGH #3 rewrite of `RefreshWebSocketAuthentication` likely already removed its versions — verify).

2. **Empty stub `ProcessEstimationData` + the id-890 branch** in `HandleMarginEstimationResponse` — remove if confirmed unused (nothing sends id 890; verify by reference search). **Preserve the id-777 live-position path** (that one is live).

3. **Duplicate functions** `GetAccountSummaryLimits` vs `GetAccountSummaryLimitsWithTimeout` — consolidate to one (keep the timeout version); update both callers (`InitializeRateLimits`, `InitializeRateLimitsAfterAuth`). Verify behaviour parity first.

4. **Heartbeat enabled twice** per connect — `EnableDeribitHeartbeatEnhanced` is called in the connect sequence (`ConnectToWebSocketDirectly`) **and** inside `AuthorizeWebSocketConnection`. Keep one (the connect-sequence call is the clearer home). Confirm heartbeat still starts on connect + reconnect.

5. **Magic numbers → named constants** (`frmMainPageV2` only; same values, no behaviour change): taker fee `0.0005` (`HandleIndexUpdates`), reposition leeways `±3` / `±5` / `0.5` (`HandleQuoteUpdates`). Promote to named `Const`. **Skip the `FrmIndicators` magic numbers** (`4320` bar cap, score `/21`) — that module is being replaced.

> Not here: converting the receive-loop `Async Sub` handlers to `Async Function … As Task` — that belongs with the #9 dispatcher refactor (`spec-medium-dispatcher.md`), not this cleanup.

## Acceptance

- Build green (0 errors) after each item. Items 1/5 are pure no-ops (build-check only). Items 2/3/4 touch the connect/receive path — a quick connect + data-flowing smoke test confirms no regression (no missing heartbeat, rate-limit init still runs, margin/position display for id-777 still updates).
- No cross-thread access reintroduced (keep the field/`UiInvoke` discipline).

## Ground rules

Implement directly; one local commit per item; **do NOT push**; 0-error build per commit; behaviour-preserving. Produce a short **implementation report**: per-item before→after + reference-search evidence for the "confirm unused" items (2, 3), build/commits, and the smoke-test result.

**Suggested model/effort: Sonnet is fine, medium effort** — these are mechanical, low-risk, and independent. (Use Opus only if bundling them with #9/#10 in one conversation.)

---

# Addendum 2026-07-06 — audit F14–F18 + post-audit items (housekeeping v2)

**Source:** `CODE_AUDIT_FABLE5.md` §C/§D + ROADMAP §2 item 5 + owner runtime findings. **Every item below was re-verified against the actual code at `master` @ `fc7bb6c` on 2026-07-06** — call sites enumerated, already-fixed items removed. Line numbers are `fc7bb6c` hints; **symbols are canonical, re-grep before editing**. All in `frmMainPageV2.vb` unless noted.

## Already resolved elsewhere — do NOT re-implement, do not go looking

- `MonitorConnectionHealth` — already deleted (resilience pass). It is not in the code; F14's mention is obsolete.
- F12 crash backstops — done (resilience pass, `ApplicationEvents.vb`).
- F15 id-space — resolved where it mattered: entry placements now use unique ids ≥ 600000 + the `pendingPlacements` registry; edits use 223344–223350. The remaining shared ids (1, 4, 30) are **accepted** — do not renumber anything.
- F11 quote-path parses — fixed previously; the one surviving receive-path parse cluster is item 12 below.

## Items (numbering continues from the base spec; each an independent commit; behaviour-preserving unless flagged **[owner-visible]**)

6. **Dead code removal (F14, re-verified zero references 2026-07-06):** delete `HandleOrderUpdates` (`:2440`), `ExportTradesToCSV` (`:5190` — also broken on .NET 9: `Process.Start(fileName)` without `UseShellExecute`), `LogFailedEntry` (`:2498`) **plus** its commented-out call in `IsATRSlippageExcessive` (`:2489`) **plus** the never-assigned `Private _autotradesettings As AutoTradeSettings` field (`:21` — `LogFailedEntry` dereferences it ⇒ NRE landmine; the only two references are the field and that dead method), and `TradeAnalytics.GetTradeStatistics` (`TradeAnalytics.vb:10`). Re-run the reference search immediately before each delete. **Skip** `FrmIndicators.UpdateEmaVwapLabels` (dying module, standing skip).

7. **Unused packages (F17):** remove `EntityFramework 6.5.1` and `System.IO.FileSystem.DriveInfo 4.3.1` from `DeribitOrderPlacementApp.vbproj` (`:20`, `:24`). SQLite is used directly — after removal, build + a DB smoke (view trades grid) confirms nothing pulled them transitively.

8. **[owner-visible] Slippage-guard coherence** (owner runtime-confirmed 2026-07-03; ROADMAP §2.5). **Coordination 2026-07-06:** `spec-entry-chase-v2.md` rewrites the four reposition-gate lines and applies the `And→AndAlso` there — if it has landed first (it precedes this bundle in the roadmap), part (a) below is already done; only part (b) (the six pre-placement gates) remains here. Verify with a grep before implementing. One commit, two parts — the checkbox becomes the single arm switch for the whole feature:
   - `And` → `AndAlso` on the **four** reposition gates in `HandleQuoteUpdates`: `maxSlippageATRchecked And IsATRSlippageExcessive(...)` at `:1469` (entry LONG, `bestBid`), `:1508` (entry SHORT, `bestAsk`), `:1695` (trailing LONG), `:1721` (trailing SHORT). Today the predicate runs with the checkbox OFF — logging "exceeds limit" lines and resetting the slippage baseline via `ResetOrderAttempt()`.
   - Gate the **six** pre-placement checks that currently run unconditionally: `If IsATRSlippageExcessive(BestPrice, direction)` at `:2590/:2630/:2669/:2708` (`ExecuteOrderAsync` branches) and `:3510/:3551` (`StopLossForTrailingOrderAsync`) → prepend `maxSlippageATRchecked AndAlso`.
   - ⚠️ **Flag, don't fix:** the trailing-LONG gate (`:1695`) passes `bestAsk` where entry-LONG (`:1469`) uses `bestBid` — audit F18 calls it copy/paste drift. Changing it alters guard behaviour when armed; put it in the impl report for the owner's decision, leave the code as-is.

9. **[owner-visible] 500 ms entry latency (F16, owner-approved removal):** delete `Await Task.Delay(500)` in the four entry-button handlers — `btnLimit_Click :4617`, `btnNoSpread_Click :4639`, `btnMarket_Click :4673`, `btnTrail_Click :4895` (each sits after the `btnEstimateMargins_Click` call; keep the estimate call itself). **Do NOT touch** the `Task.Delay(500)` at `:1268` — that one is in `HandleHeartbeat` (LED blink duration, not latency).

10. **Stale id-2 hint in `RequestNameForId` (`:1078`):** `" auth/entry order"` → `" auth"` — entry orders moved to ids ≥ 600000 (handled by `HandlePlacementResponse`); the hint predates that.

11. **Stale `SendReduceMarketOrderAsync` header comment (`:4716-4718`):** it says "Reads orderAmountVal, not txtAmount" — superseded by the position-model flatten (body reads `positionSizeUSD`, falls back to `TradeMode`/`orderAmountVal` only when the model is unseeded) — and omits a caller. Rewrite to match reality; actual callers: `FlattenPositionAsync` (`:428`), the two emergency sites in `UpdateStopLossForTriggeredStopLossOrder` (`:3260/:3266`), `btnReduceMarket_Click` (`:4754`).

12. **Raw `Decimal.Parse` cleanup — surviving clusters only** (blank field ⇒ exception ⇒ red error line today; use the established patterns):
   - **Receive path (highest value, F11 residue):** the filled-`EntryLimitOrder` TP-display block inside the `HandleOrderPositionUpdates` open-orders `Invoke` lambda (`:2149-2155`) parses `txtManualTP`/`txtTPOffset`/`txtComms` → read the maintained mirror fields `manualTPval`/`tpOffsetVal`/`commsVal` (`:124/:129/:130`, kept current by `SyncTradeInputsFromUi`). **Parse-swap only — do not restructure the lambda** (see item 15 exclusions).
   - **UI-thread buttons:** `btnEditTPPrice_Click` (`:4763-4787`), `btnEditSLPrice_Click` (`:4834-4859`), `btnRefreshLiveData_Click` (`:5299`), `btnEstimateMargins_Click` (`:5234`) → `TryParse` with the "no edit on a bad amount" pattern already used in `UpdateLimitOrderWithOTOCOAsync` (`:3125`) / `UpdateStopLossForTrailingOrder` (`:3378`). ⚠️ `btnEditSLPrice_Click` is the **Edit T.S. path (id 223346)** — parse handling only, do not touch its edit semantics; it deliberately does NOT record to the commanded-SL set (`spec-back-session-2026-07-04.md` §10).

13. **`positionSizeUSD` local shadowing:** `btnEstimateMargins_Click` declares `Dim positionSizeUSD As Decimal = Decimal.Parse(txtAmount.Text)` (`:5234`), shadowing the engine's position-model field — rename the local (e.g. `orderValueUSD`) throughout the method. (Combines naturally with its item-12 TryParse.)

14. **F18 small fixes** (independent commits):
   a. `DeleteMultipleTrades` raises its **success** through the `DatabaseError` event (`TradeDatabase.vb:208`) ⇒ red "Database Error: Successfully deleted N trades". Add a `DatabaseInfo(message)` event for the success path, subscribe in `frmMainPageV2` (next to the `:472` handler wiring), log green.
   b. `AutoTradeSettings.vb:20` calls `Me.Hide()` inside its own `Load` ⇒ first settings-button click does nothing (Load fires on first `Show()` and immediately hides). Delete the `Me.Hide()`; verify the show/hide toggle in `FrmIndicators` (`:1915-1920`) still behaves. **Skip this item entirely if the tie-in re-code has already replaced `AutoTradeSettings`.**
   c. `HandleAccountSummaryResponse` uses `SetResult` (`:942`, `:960`) — a response arriving after the timeout throws `InvalidOperationException` into the swallow-catch → `TrySetResult` both.
   d. `txtLogs` grows unbounded (`AppendColoredText`, `:3836`) — long unattended sessions degrade the UI thread the receive loop marshals through. Cap inside the marshaled lambda: e.g. when `rtb.TextLength > 400000`, remove the first ~100000 chars before appending.
   e. Duplicate TP computation in `ExecuteOrderAsync` BuyLimit branch: If/Else at `:2570-2575` immediately followed by the identical ternary at `:2576` overwriting the same variable — delete the ternary, verify equivalence.
   f. `TrailingStopLossOrderAsync` sends an inline `cancel_all_by_instrument` (id 30, `:3686-3695`) **without setting `cancelPending`** — echoes from the cancel can be misread while the trailing placement is in flight. **Minimal fix:** set the `cancelPending` flag + timestamp exactly as `CancelOrderAsync` does, right before the send (the 4-s self-clear handles reset). **Do NOT reroute through `CancelOrderAsync`** — it now resets the entire SL context (`emergencyBaseline`, commanded-SL set, trigger baseline) which would break the trailing transition.
   g. **[owner-visible, cosmetic]** PnL display marks longs to `BestAskPrice` (value `:1806` + colour threshold `:1807`); closing a long hits the **bid** → use `BestBidPrice` in the long branch only. The short branch (`:1817-1818`, ask) is correct — leave it.

15. **F10 scoped pass — blocking `Me.Invoke` → `UiInvoke`, display-only sites ONLY.** `UiInvoke` (`:148`) is the guarded non-blocking `BeginInvoke` marshal. Convert **exactly** these sites (each verified display-only 2026-07-06); recommend doing this item as its own final commit:
   - `HandleHeartbeat` `:1261`, `:1270` — `radHeartBeat.BackColor` blink.
   - `HandleQuoteUpdates` `:1430`, `:1437` — `txtTopBid`/`txtTopAsk` (2 blocking round-trips per quote tick — the hot path).
   - `HandleQuoteUpdates` PnL block `:1808-1835` — 6 sites writing `lblPnL` colour/text; optionally consolidate each tick's colour+text into ONE `UiInvoke`.
   - `CancelOrderAsync` `:2941`, `:2966` — placed-price textbox resets + status label; margin-display clears.
   - `CompletePositionClose` `:3945` — margin-display clears.
   - `ProcessPositionData` `:4302` — margin/leverage labels.
   - `AppendColoredText` `:3858` — `Invoke` → `BeginInvoke`, **keeping** the existing handle guard and try/catch-drop. Queue ordering keeps log lines in order; UI-thread callers now append after the current handler returns (cosmetic).

   **Excluded — DO NOT TOUCH (this list is the whole point of the scoping):**
   - `SetTradeTargets` `:368` and `PlaceAutomatedOrder` `:412` — the decouple-v2 public-API thread contract; blocking is deliberate.
   - The two `HandleOrderPositionUpdates` order-echo lambdas (`:2049` open-orders, `:2215` untriggered-legs) — they **mutate engine state** (`placedPrice`, `CurrentOpenOrderId`, `CurrentTPOrderId`, `placedStopLossPrice`, `StopLossTriggerOriginal`, `OpenOrderNo`/`unTrigOrder`…). Switching them to `BeginInvoke` changes read-after-write ordering for the rest of that message's processing. Needs a state-hoisting design first — out of this bundle's scope.
   - `LogTradeDecision` `:4359` — synchronous snapshot-read of controls; blocking is the point.
   - `btnEstimateMargins_Click` `:5258` — UI-thread caller; the Invoke is inert.

   Ordering safety note (why the conversion is sound): converted writes and any subsequent blocking `Invoke` reads (e.g. `CancelOrderAsync`'s resets inside `CompletePositionClose` before `LogTradeDecision`'s snapshot) marshal through the same UI message queue, so posted-before-waited ordering is preserved.

16. **Document-only:** the emergency-threshold check sits inside the `BackoffStopLossRetry`-throttled block, so a persistently failing SL edit also delays emergency market-stop detection by up to 5 s. Accepted trade-off vs. the edit-storm fix — add a short comment at the throttle gate stating the property. No behaviour change.

## Sequencing, acceptance, ground rules (addendum)

- Suggested order: 6, 7 (dead weight) → 10, 11, 16 (comments) → 13 + 12 (parses) → 14a–g → 8, 9 (owner-visible) → **15 last, own commit**.
- Base-spec ground rules apply: one local commit per item, **never push**, 0-error/0-warning build per commit, impl report with per-item before→after + reference-search evidence (items 6, 7) + smoke results.
- Smoke tests: after 7 — DB ops (view trades); after 8/9 — place+cancel a test order with the slippage checkbox OFF (expect **zero** slippage log lines) then ON; after 15 — connect, quotes flowing, place/cancel, log lines still in order.
- **[owner-visible] items for the runtime pass:** 8, 9, 14g, and the item-8 bid/ask-drift flag (decision, not code).
- **Do NOT touch** (hot invariants, `spec-back-session-2026-07-04.md` §10): the 7 SL-context reset sites, the `HandleQuoteUpdates` chase/emergency block, the `StopLossOrder`-echo commanded-price discriminator, `pendingPlacements`/`HandlePlacementResponse` (the tie-in spec owns its `TimedOut` hardening).
- Model/effort: Sonnet medium for 6–14 and 16 (mechanical, sites enumerated). Item 15 is mechanical **given the exclusion list**, but its diff sits on the receive hot path — keep it a separate commit and have the coordinator review that diff specifically.
