# Session spec-back — 2026-07-04 (restore-hardening → M.SL emergency work)

**For:** the orchestrator and any implementer of a pipeline spec that touches the same files. This reconstructs **everything changed this session** so parallel work doesn't collide. Session range: **`0d078eb..968b26d`** on `master` (18 commits), base `7fb075a` (resilience impl-report). Build **0/0** throughout. **Nothing pushed** (owner is the only pusher). Owner has runtime-tested and PASSED everything except the SL reconciliation (§9), which is now IMPLEMENTED (`968b26d`) and code-reviewed (agree), pending the owner's runtime test.

**Update 2026-07-04 (post-handover):** §9 was open at handover (`2381766`); a follow-on Opus seat implemented it (`968b26d`) and it was reviewed/agreed. This doc now reflects that — sections updated: header, §0, §1, §6, §8, §9, §10.

Line numbers drift — anchors below are **function/symbol names**; grep for them.

## 0. Files touched (cumulative `7fb075a..HEAD`)

| File | Δ | What |
|---|---|---|
| `frmMainPageV2.vb` | +479/−138 (~617 lines changed) | the bulk — restore, close, emergency, UX, SL reconciliation. **Most collision-prone.** |
| `frmMainPageV2.Designer.vb` | ±34 | removed `btnClose` and `btnMark` controls |
| `FrmIndicators.vb` | ±25 | handle-race guard (`UiInvokeSafe` + constructor handle realize) |
| `docs/*` | new | specs, spec-backs, impl-reports, 1 review (all listed below) |

## 1. New engine fields / methods / removals in `frmMainPageV2.vb` (the coordination-critical list)

**New public/private fields:**
- `emergencyBaseline As Decimal` — M.SL emergency baseline; the live SL once triggered (see §6, §9). Read by the emergency; reset at 7 sites (grep `emergencyBaseline = 0`). Was briefly 8 with entry-chase v2's fill re-anchor; reverted to 7 by the TP-only fill-reanchor fix (`spec-fill-reanchor-fix.md`).
- `pendingCloseValid/PorLAmt/PorL/Label/AmountUSD/WasLong/ExecPrice` (7 fields) — persisted close-fill capture (see §3).
- `positionRestoreAnnounced As Boolean` — one restore announce per connection (see §2).
- **`commandedSLPrices : List(Of CommandedSLEntry)` + `commandedSLLock` + consts `CommandedSLWindowMs`(2000ms)/`CommandedSLMatchTol`(0.25) + `Structure CommandedSLEntry {Price, Stamp}`** — the SL-reconciliation commanded-price set (§9). Lock-guarded; cleared at the same 7 SL-context reset sites as `emergencyBaseline` (was briefly 8 with entry-chase v2's `ReanchorLegsAsync`; reverted to 7 by the TP-only fill-reanchor fix).

**New methods:**
- `RequestOpenOrdersSnapshot()` — send-only `get_open_orders_by_instrument`, id **778** (new JSON-RPC id; added to HANDOVER-2 §4.5 map).
- `HandleOpenOrdersSnapshot(response)` — drains id-778; restores OTOCO order context at connect. Wired in the receive-loop dispatch after `HandleMarginEstimationResponse`.
- `CompletePositionClose()` — async; the single position-close completion path (see §3).
- `RecordCommandedSLPrice` / `IsRecentlyCommandedSLPrice` / `ResetCommandedSLPrices` / `PurgeCommandedSLPrices` — the commanded-price-set helpers (§9). All `commandedSLLock`-guarded; **no `Await` inside the lock**.

**Changed signature:** `ApplyCloseFill(order, execPrice, label)` — **dropped its 5 ByRef out-params**; now writes the `pendingClose*` fields (see §3). All 5 call sites updated.

**Removed:** `btnClose` + `btnClose_Click` (in-app "-X-" button — title-bar X now does the shutdown); `btnMark` + `btnMark_Click` (manual SL-baseline re-sync — made vestigial by §6). Both removed from the Designer too. `btnMarket`/`btnClearLog` are DIFFERENT buttons — untouched.

## 2. Restore hardening (`0d078eb`, `310de5f`, `c6a893c`, `4e1d751`; docs `fdb1160`)

Spec `spec-restore-hardening.md`; spec-back `spec-back-restore-hardening.md`. Restores trade context after a restart with an open/working position.
- **`0d078eb` zero-baseline guard:** `StopLossTriggerOriginal > 0D` added to both emergency branches in `UpdateStopLossForTriggeredStopLossOrder` and to the `HandleQuoteUpdates` triggered-SL pre-check (via a `baselineKnown`/now-`emgBaseline` local). Unknown baseline disables the emergency (prevents an instant short close after restart).
- **`310de5f` trade-context restore at connect:** `ProcessPositionData` announce block calls `SetTradeMode(size>0)` + seeds `placedPrice` from avg entry. New `RequestOpenOrdersSnapshot` (id 778) + `HandleOpenOrdersSnapshot` map entry/TP/SL legs by label+state (seed-only-when-zero; `UiInvoke`; `Not cancelPending`). Mid-session heal of `StopLossTriggerOriginal` in the open-SL echo.
- **`c6a893c` triggered-SL sized from position:** `amount = If(positionSizeUSD<>0, Math.Abs(positionSizeUSD), orderAmountVal)` in `UpdateStopLossForTriggeredStopLossOrder`.
- **`4e1d751` (owner-test fix):** restarting with an UNFILLED entry left `TradeMode` LONG (position flat at connect, so id-777 announce didn't run). Now `HandleOpenOrdersSnapshot` pass-1 captures the working entry's `direction` and calls `SetTradeMode(entryIsLong)`.

## 3. Reliable position-close completion (`63bb149`; docs `65fd66f`)

Spec `spec-close-completion-fix.md` + coordinator review `review-close-completion-fix.md` (APPROVED, 5 amendments applied) + `impl-report-close-completion-fix.md`.
- **Bug:** close message + DB record + cleanup were nested under `If orders.Count>0` and read per-echo locals set by `ApplyCloseFill` only in the fill's echo. A split echo (fill and flat-position in separate messages) lost the whole close.
- **Fix:** `ApplyCloseFill` persists to `pendingClose*` fields. `HandleOrderPositionUpdates` detects the `positionSizeUSD` !=0→0 transition in the model loop and calls `CompletePositionClose()` **after** the orders block (outside the orders gate), so a flat echo with no orders still completes. `pendingClose*` cleared on a new-position open. Fires once per transition. `OpenPositions=False` was dropped (per-echo local, no reader).

## 4. UI-marshal window-handle race (`8bcf770`; docs `c38f511`)

Spec-back `spec-back-handle-race-hardening.md`. Owner restart crashed at `FrmIndicators.ProcessMessage → Me.Invoke` (handle not created).
- **FrmIndicators:** `New` realizes the handle (`Dim forceHandle = Me.Handle`); new `UiInvokeSafe` (IsHandleCreated/IsDisposed + Try/Catch); the two receive-loop `Me.Invoke(UpdateSignals)` calls route through it.
- **frmMainPageV2:** `AppendColoredText` was a raw `Me.Invoke` — now guarded (drop the line if no live handle). `UiInvoke` and the other marshals were already guarded.
- **AutoTradeSettings:** clean (no background marshaling).

## 5. UX (`5c37ce9`)

`Position entered: <side> <amount> @ $<entry>` (green) logged in the filled `EntryLimitOrder`/`EntryTrailingOrder` echo cases. `btnClose` removed (see §1).

## 6. M.SL emergency baseline + btnMark removal (`57dd0fe`, `f42a6a7`; docs `6a4d062`)

Spec-back `spec-back-msl-emergency-baseline.md`.
- **`57dd0fe`:** new `emergencyBaseline` field. The emergency (both `HandleQuoteUpdates` and `UpdateStopLossForTriggeredStopLossOrder`) reads `emgBaseline = If(emergencyBaseline>0, emergencyBaseline, StopLossTriggerOriginal)`. `StopLossTriggerOriginal` kept as the trigger price of record + pre-trigger fallback, and now **synced from exchange-side trigger moves** in the untriggered SL echo (this made `btnMark` vestigial → removed). `emergencyBaseline` reset at 7 sites (4 placement + close + nuclear cancel + market-reduce).
- **`f42a6a7` follow-live-SL:** removed the trigger-flip gate so `emergencyBaseline` updates on EVERY triggered (open) echo — follows manual + the app's own trailing. Owner-confirmed working. **SUPERSEDED by `968b26d` (§9):** that ungated per-echo write was removed (it also swallowed lagging/out-of-order echoes of the app's own reposition and could walk the baseline backward). `emergencyBaseline` now moves under the commanded-price discriminator — seeded at trigger, advanced with the chase (`= newStopPrice`), and set on a detected manual edit. Same net "follows the live SL", no race.

## 7. DIAG cycle (`04d708a` added, `2381766` reverted) — net-zero on code

Temporary `[DIAG]` tracing in the `HandleQuoteUpdates` chase block to diagnose §9. Proved the bug, then reverted. `2381766` also elevated the reconcile spec with the runtime evidence.

## 8. Invariants established/reaffirmed this session

- Receive thread → engine fields; controls via `UiInvoke`/`Me.Invoke`/`AppendColoredText`, **all now IsHandleCreated-guarded**.
- Emergency baseline selection = `emergencyBaseline` (live SL post-trigger) else `StopLossTriggerOriginal` (trigger). Unknown (0) disables the emergency.
- Close completion fires once per `positionSizeUSD` !=0→0 transition via `CompletePositionClose`, echo-batching-independent.
- id **778** = `get_open_orders_by_instrument` restore snapshot.
- Seed-only-when-zero still governs `placedPrice`. **For `placedStopLossPrice` it is now the trigger-moment seed only** — post-trigger it follows the live SL via the commanded-price discriminator (§9): a triggered-echo price that differs from the reference AND was not app-commanded ⇒ manual edit ⇒ followed; an app-commanded/unchanged price ⇒ ignored (runaway protection). `emergencyBaseline` moves with it. The untriggered-echo `StopLossTriggerOriginal` sync remains a deliberate exception.

## 9. Triggered-SL reconciliation — IMPLEMENTED (`968b26d`) + reviewed (agree)

Spec `spec-reconcile-manual-sl-edits.md` (4a+P1); impl-report `impl-report-reconcile-manual-sl-edits.md`; spec-back `spec-back-reconcile-manual-sl-edits.md`. Implemented by a follow-on Opus seat, code-reviewed here (**agree**; I built it 0/0 and traced the SL-edit paths).

- **Bug (was runtime-confirmed via the §7 DIAG):** `placedStopLossPrice` was seed-if-zero, so after trigger it froze at the placement value and never followed manual SL edits; the chase (`bestBid/ask` vs `placedStopLossPrice ± $5`) then never fired and the taker emergency fired instead (`emergencyBaseline` followed correctly, so the two references diverged).
- **Fix:** commanded-price-set discriminator. `RecordCommandedSLPrice(newPrice)` is called at the single **auto**-chase send point (inside `UpdateStopLossForTriggeredStopLossOrder`, right after the `private/edit` send — before the echo can return). The open `StopLossOrder` echo: a `price` that differs from `placedStopLossPrice` AND is not in the commanded set ⇒ **manual edit** ⇒ set `placedStopLossPrice`+`emergencyBaseline`+display, log `Manual SL edit: $X` (cyan); a commanded/unchanged price ⇒ ignore (runaway/transition-race protection preserved). `emergencyBaseline` is advanced with the chase (`= newStopPrice`) instead of the removed ungated echo write. `ResetCommandedSLPrices()` at all 7 SL-context reset sites.
- **Review note (recorded for coordination):** the impl-report's "single send point for every triggered-SL edit" is about the **auto-chase** paths only. **`btnEditSLPrice_Click` (the "Edit T.S." button, id 223346) is a second path that edits the triggered SL** and does NOT record — but that is **correct by construction**: it's a *user*-initiated edit, so the discriminator rightly treats its echo as a manual edit and follows it (visible effect: clicking Edit T.S. on a triggered SL now logs `Manual SL edit`). The design correctly splits **auto** (recorded ⇒ ignored) from **user/manual** (not recorded ⇒ followed).
- **Owner runtime test pending** (spec §9 + review additions: the original failing scenario now chases before the emergency; Edit T.S. is followed; a normal app-only chase shows no spurious `Manual SL edit` and no backward `placedStopLossPrice` blip). **Not pushed.**

## 10. Coordination warnings for parallel specs touching these files

- **`HandleQuoteUpdates`** (the triggered-SL chase + emergency block) and **`HandleOrderPositionUpdates`** (the open `StopLossOrder` echo) are the hottest, most-changed regions — restore hardening, the emergency baseline, AND the §9 reconciliation all live here. Coordinate any further SL-path work.
- **SL edits go through two paths:** the auto-chase (`UpdateStopLossForTriggeredStopLossOrder`, which records to the commanded set) and the user "Edit T.S." button (`btnEditSLPrice_Click`, id 223346, which deliberately does NOT). Any **new auto/programmatic SL-edit path MUST call `RecordCommandedSLPrice`**, or its echo will be misread as a manual edit (spurious `Manual SL edit` and, if aged past the ~2s window, the backward-reset the discriminator exists to prevent).
- Don't re-introduce a raw `Me.Invoke`/`AppendColoredText` without the handle guard.
- The 7 `emergencyBaseline = 0` reset sites (4 SL-placement + nuclear cancel `CancelOrderAsync` + market-reduce `isMarketOrder` + `CompletePositionClose`) are the canonical "SL context reset" anchors — `ResetCommandedSLPrices()` already mirrors them; any new SL-context field must too. (Was briefly 8 when `spec-entry-chase-v2.md` §4 added `ReanchorLegsAsync`; the TP-only `spec-fill-reanchor-fix.md` removed that SL edit, reverting to 7.)
- `ApplyCloseFill` is fields-only now — don't pass ByRef.
- Deleted controls `btnClose`/`btnMark`: don't reference them.
