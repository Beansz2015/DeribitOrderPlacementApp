# Spec — Execution ergonomics bundle (owner-selected, 2026-07-03)

**Origin:** owner-approved improvement list 2026-07-03. **Rejected and out of scope forever unless the owner reopens it:** automatic session-end flattening (shutoff time is variable; discretionary late opportunities must stay possible). Do not re-propose.
**Recommended implementer:** Opus at **high** (items C/F touch the receive hot path and the SL-edit path; the rest is mechanical). Post-window work: implement **after** `spec-resilience.md` lands (item E depends on its `FormClosing` handler) — Phase B additionally after the signal-bridge tie-in.
**Target:** `DeribitOrderPlacementApp` (mostly `frmMainPageV2.vb`; `TradeRecord.vb`/`TradeDatabase.vb` for item C). Anchors by symbol; verify against HEAD at implementation time — several specs land before this one.
**Ground rules:** the standing ones — build 0/0 per commit, local commits, never push, §5 invariants (receive thread = engine fields only; `UiInvoke`/`AppendColoredText` for display; don't touch reposition gates/cancel lifecycle/position-model retention rule), scope discipline, impl report at the end.

**Phasing:** items A–E are standalone (Phase A). Item F + the bridge-fed parts of C and D are **Phase B — implement only after the signal-bridge tie-in ships** (they consume the tie-in's `BridgeReader` and the frozen contract fields).

---

## Item A — persist the standing trade inputs (config file)

**Problem:** every restart resets the inputs to Designer defaults ("60/60/30…"); the owner re-types session values. Also violates the owner's config philosophy (externalized, not baked in).

**Design:** `orderapp-settings.json` beside the exe (same discovery pattern as `secrets.json`; add to `.gitignore`; ship a committed `orderapp-settings.example.json`). New `Option Strict On` class `AppUserSettings.vb` (load/save via Newtonsoft, tolerant of missing keys → defaults). Persisted keys: the seven standing inputs (`amount`, `take_profit`, `trigger`, `stop_loss`, `trigger_offset`, `tp_offset`, `market_stop_loss`), the two checkboxes (`max_slippage_atr_checked`, `market_stop_checked`) + `max_slippage_atr_mult`, plus this bundle's new keys (`risk_per_trade_usd`, `max_size_usd`, `alerts` block). **Never persist** `txtManualTP`/`txtManualSL` (per-trade values) or anything credential-like.

**Wiring:** load in `frmMainPageV2_Load` *before* `SyncTradeInputsFromUi()` — write the textbox/checkbox values on the UI thread; the existing `TextChanged`/`CheckedChanged` handlers sync the mirrors (do not set mirror fields directly). Save in the resilience-spec `FormClosing` handler (before socket teardown) and from a small "Save Defaults" option (a right-click context item on the MARGINS group or a tiny button — implementer's choice, note it). Failures log yellow and never block startup/shutdown.

**Acceptance:** change inputs → close → reopen: values restored. Delete the file → app starts on Designer defaults with a yellow log line. `secrets.json` untouched.

## Item B — risk-based sizing button

**Problem:** the owner sizes dynamically (risk-defined-by-stop) but computes it mentally.

**Design:** a small `SIZE` button near `txtAmount`. On click (UI thread):
- `ref` = current best price for the active side (`BestBidPrice` long / `BestAskPrice` short); refuse if ≤ 0.
- `dist` = `|ref − manualSL|` when `txtManualSL > 0`, else `txtTrigger` (the trigger distance *is* the planned stop distance in offset mode). Refuse if ≤ 0.
- `size = riskPerTradeUSD × ref ÷ dist` (inverse-contract linearization), **rounded DOWN to a 10-USD multiple** (Deribit contract step — a non-multiple rejects with -32602), clamped to `max_size_usd`.
- Write `txtAmount.Text` (mirror syncs via `TextChanged`); log `Size: $N (risk $R over $D stop distance)`.
`risk_per_trade_usd` / `max_size_usd` come from item A's config (defaults: 25 / 500 — owner tunes).

**Acceptance:** with R=$25, ref≈60000, dist=$60 → size 25×60000/60 = 25000 → clamped to `max_size_usd`; with dist=$3000 → $500 → 500 ✓ 10-multiple. Zero/blank dist → refusal log, amount untouched.

## Item C — journal enrichment: MAE/MFE, planned R, fees (+ signal columns)

**Problem:** the DB records outcomes, not trade quality. For structural-stop calibration the owner needs: how far price went against/for the position (MAE/MFE), the R-multiple vs the *planned* stop, and net-of-fees P/L.

**Design — tracking (receive thread, engine fields only):**
- New fields: `mfePrice`, `maePrice` (extremes since entry), `plannedStopAtEntry`, `cumFeesBTC`.
- **Reset on flat→nonzero transition** — detect in the §2b position pass (`positionSizeUSD` was 0, incoming `sz ≠ 0`): set both extremes to the incoming `average_price`, `plannedStopAtEntry = StopLossTriggerOriginal`, `cumFeesBTC = 0`.
- **Update per quote tick** in `HandleQuoteUpdates`, guarded `positionSizeUSD <> 0D`: `maePrice`/`mfePrice` min/max against `BestBidPrice`/`BestAskPrice` (two compares — hot-path budget is fine; no controls, no allocation).
- **Fees:** `user.changes` messages carry a `trades` array the handler currently ignores. In `HandleOrderPositionUpdates`, alongside the §2b pass, sum `trades[*].fee` (BTC) into `cumFeesBTC` (null-safe; fee_currency is BTC on this instrument).
- **At close** (the `size = 0` record block): compute direction-aware `MAE_USD`/`MFE_USD` vs `entryPriceAtClose` and the closed size, `PlannedRiskUSD = |entryPriceAtClose − plannedStopAtEntry| × size/entryPriceAtClose`, `RMultiple = signedPL ÷ PlannedRiskUSD` (0 when risk unknown), `FeesUSD = cumFeesBTC × indexPriceVal`.

**Design — storage:** new `TradeRecord` properties (`MaeUSD`, `MfeUSD`, `PlannedStop`, `RMultiple`, `FeesUSD`, `SignalId`, `SignalConfidence`) + SQLite migration: on `InitializeDatabase`, `ALTER TABLE Trades ADD COLUMN …` for each, individually try/caught (SQLite throws on existing columns — swallow those; anything else surfaces via `DatabaseError`). Extend the insert, `CreateTradeFromReader`, and the trade-history grid (MAE/MFE/R/Fees columns; keep it readable — drop the old unused-width columns if space demands, note what moved). `SignalId`/`SignalConfidence` are written empty in Phase A and populated by the bridge consumer in **Phase B**.

**Acceptance:** a test trade shows plausible MAE/MFE (MAE ≤ 0 ≤ MFE relative to entry, sign-adjusted), an R-multiple consistent with planned stop distance, and non-zero fees on taker fills. Existing DBs open cleanly (migration idempotent). The multi-fill last-fill-only limitation stays (known, accepted).

## Item D — alerts

**Design:** `Private Sub Alert(kind As String)` — config-gated (item A `alerts` block, all default **on** except reposition noise which is not alertable at all), plays a `SystemSounds` tone (Exclamation for adverse, Asterisk for benign) and flashes the taskbar via `FlashWindowEx` P/Invoke (marshalled; handle-safe). Call sites (Phase A): entry fill (the `EntryLimitOrder/EntryTrailingOrder` filled cases), TP/SL/trailing close fills, the emergency market-stop, `ORDER REJECTED`, disconnect + reconnect-failure. **Phase B adds:** engine-stale stand-down, circuit-breaker trip. Every call site is one line; no logic changes around them.

**Acceptance:** each event audibly fires once (no per-tick spam); toggling the config key silences it.

## Item E — one-click break-even stop

**Design:** extract the core of `btnEditSLPrice_Click` into `Private Async Function EditStopLossTo(newTrigger As Decimal) As Task` (id resolution incl. the audit-2 fixed fallbacks, limit = trigger ∓ `txtStopLoss` offset, payload, send, logs); the button calls it with its textbox value. New `BE` button: gates `positionSizeUSD <> 0D` and `positionAvgEntry > 0D`, computes `trigger = avgEntry ± commsVal` (+ for long, − for short — covers round-trip cost), **rounds to the 0.5 tick**, calls `EditStopLossTo`. UI thread only; refuse with a yellow log when flat or no SL order id resolves.

**Acceptance:** in a long position, one click moves the SL trigger to entry+comms (verify on the exchange UI); flat → refusal log; the existing edit button behaves identically to before (shared core, byte-equal payload).

## Item F — engine levels for manual trades (Phase B)

**Design:** button `USE ENGINE LEVELS` (near the manual TP/SL boxes). Reads the tie-in's `BridgeReader.LatestPayload` (the same consumer the autotrade path uses — do **not** build a second file reader): refuse (yellow log) if payload is stale per the contract age gate, `signal_state ≠ OK`, or `direction = NONE`. Otherwise: `txtManualTP = levels.<direction>.target`; **stop mapping per the contract semantics** — `levels.<direction>.stop` is the exit *trigger* level, and the manual-SL path derives trigger = `manualSL ± txtStopLoss`, so write `txtManualSL = stop ∓ txtStopLoss` (long: `stop − txtStopLoss`; short: `stop + txtStopLoss`) so the resulting trigger lands exactly on the engine's stop. Log both levels + `signal_id`. Works regardless of arming (it's a manual-trading aid; the interlock is untouched). If the payload direction disagrees with the current Buy/Sell mode, log the mismatch and still populate (the owner decides — they may be fading; do not block).

**Acceptance:** with a fresh actionable payload, one click populates manual TP/SL such that the placed OCO's TP price and SL *trigger* equal the engine's target/stop exactly; stale/NONE payloads refuse cleanly.

---

## Commits

1. `Ergonomics (1/5): orderapp-settings.json - persist standing inputs` (A)
2. `Ergonomics (2/5): risk-based SIZE button` (B)
3. `Ergonomics (3/5): journal MAE/MFE, planned R, fees + schema migration` (C, signal columns empty)
4. `Ergonomics (4/5): alerts` (D)
5. `Ergonomics (5/5): break-even button via shared EditStopLossTo` (E)
Phase B (post-tie-in, separate mini-handoff): F + C's signal columns + D's two bridge alerts.

## Implementation report

`docs/impl-report-execution-ergonomics.md`, standard format: per item — exact changes, build results, deviations with justification, migration evidence (old DB opened + new columns present), suspicious-nearby not touched.
