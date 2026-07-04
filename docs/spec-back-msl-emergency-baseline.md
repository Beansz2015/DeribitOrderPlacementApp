# Spec-back — M.SL emergency baseline (`emergencyBaseline`) + trigger auto-sync (as-built)

**What this is:** a specification reconstructed **from the committed code** (`57dd0fe` = fields/wiring/btnMark removal; `HEAD` = the follow-live-SL refinement), not from a forward spec. It states the behavior contract the code now enforces, with anchors, so a reviewer can diff it against those commits. Written 2026-07-04, updated same day after the owner's follow-up test. Files: `DeribitOrderPlacementApp/frmMainPageV2.vb` (+ `frmMainPageV2.Designer.vb` for the btnMark removal).

**Origin (owner design, runtime testing):**
- **Item 1** — the M.SL emergency market-reduce should measure from the **actual stop-loss price** once triggered (unknown until then), staying offset from the **trigger** price before that. Market reduce is the last resort (taker fees). *Refined after a follow-up test:* it should track the **live** SL (following manual post-trigger adjustments), not stay pinned at the trigger moment — see Region 2.
- **Item 2** — the app should track exchange-side OTOCO moves. It already syncs the displays / `placedStopLossPrice` / ids from untriggered echoes, but did **not** sync the M.SL baseline — which is what the manual **btnMark** button re-synced. Auto-syncing it makes btnMark redundant.

---

## The two fields and their split of responsibility

| Field | Meaning | Read by |
|---|---|---|
| `StopLossTriggerOriginal` (`:42`) | The **trigger price of record**. Seeded at SL placement, kept current with exchange-side trigger moves (item 2), restored/healed from `trigger_price`. Also the **pre-trigger** emergency baseline (fallback). | The emergency, as the fallback term of `emgBaseline` |
| `emergencyBaseline` (`:50`) — **new** | The price the emergency **actually** measures from once the SL has triggered: the **actual SL price pinned at the trigger moment**, held fixed as the SL trails. `0` = not yet triggered ⇒ emergency falls back to `StopLossTriggerOriginal`. | The emergency (primary term of `emgBaseline`) |

Both are written on the receive/UI thread and read on the receive thread by the emergency — the accepted Decimal torn-read class (invariant #6), same as `StopLossTriggerOriginal` before.

---

## Region 1 — the emergency reads (both sites now use a combined baseline)

**Contract:** `emgBaseline = If(emergencyBaseline > 0D, emergencyBaseline, StopLossTriggerOriginal)` — the actual SL price once triggered, else the trigger price. The zero-baseline guard (restore-hardening) now keys off `emgBaseline > 0D`.

- `HandleQuoteUpdates` (`:1577`): `emgBaseline` local; `baselineKnown = emgBaseline > 0D`; `priceMovement = emgBaseline − bestAsk` (long) / `bestBid − emgBaseline` (short). The full-M.SL emergency gate and the ½-M.SL force-reposition chooser both consume these locals, so both tiers move to the new baseline automatically.
- `UpdateStopLossForTriggeredStopLossOrder` (`:3200`): same `emgBaseline` local; both emergency-market-reduce branches use it (`emgBaseline > 0D` guard + `emgBaseline − newPrice ≥ threshold` / `newPrice − emgBaseline ≥ threshold`).

**Gating unchanged:** the emergency block still only *evaluates* once `SLTriggered = True` (`HandleQuoteUpdates:1545`), so in practice `emgBaseline` is the pinned SL price; the `StopLossTriggerOriginal` fallback covers the edge where the trigger echo carried no `price`.

---

## Region 2 — `emergencyBaseline` follows the live SL (item 1, refined 2026-07-04)

- Open `StopLossOrder` echo (`:2059`): `emergencyBaseline = If(price, emergencyBaseline)` on **every** triggered (open) echo — the trigger flip and every one after. `price` is the current SL limit price. So the baseline is set to the actual SL at the trigger moment and then **tracks the live SL** as it moves — following both the app's own trailing edits (which return as open echoes) and **manual exchange-side SL adjustments**.
- **Consequence:** the emergency stays `M.SL` below the **live** stop (not the trigger-moment stop). Manual post-trigger SL moves are followed (owner runtime test 2026-07-04: adjusting the SL on the exchange re-anchors the emergency).
- **Single source = the exchange's SL state** (the echo), so manual and app-trailing moves converge without a second writer to fight. A rare out-of-order echo self-corrects on the next one; a fast adverse move — exactly when the emergency fires — is unaffected in practice.
- **Design history:** initially *pinned* at the trigger moment ("at the point it is triggered"); the owner's follow-up test showed the intent is to track the live SL, so the trigger-flip gate (`If Not SLTriggered`) was removed.

---

## Region 3 — sync the trigger price of record (item 2, makes btnMark redundant)

- Untriggered `StopLossOrder` echo (`:2183`): `StopLossTriggerOriginal = If(triggerPrice, StopLossTriggerOriginal)` — added alongside the existing unconditional `placedStopLossPrice = price` mirror. Untriggered state isn't trailing, so an unconditional mirror is safe (no single-writer conflict). This keeps the trigger price of record (and the pre-trigger emergency fallback) current with exchange-side trigger drags.
- **btnMark removed:** `btnMark_Click` (which did `StopLossTriggerOriginal = Decimal.Parse(txtPlacedTrigStopPrice.Text)`) and all four designer references deleted. `btnMarket` (the market-order button) is untouched. The `CustomLabel7` tooltip describing the mark function is left in place (harmless residual; removing the label risks layout).

---

## Region 4 — restore into an already-triggered SL

- id-778 snapshot, triggered (`state = "open"`) SL case (`:4102`): `If emergencyBaseline = 0D Then emergencyBaseline = If(price, 0D)` — a restart into a triggered SL pins the emergency baseline to the actual SL price (seed-if-zero). The untriggered snapshot case leaves `emergencyBaseline = 0` (pre-trigger; emergency falls back to the restored `StopLossTriggerOriginal`).

---

## Region 5 — every `emergencyBaseline` write site (the "catch all fields" audit)

| Site | Line | Value | Why |
|---|---|---|---|
| SL placement (limit OTOCO ×2) | `:2742`, `:3505` | `0` | new SL is pre-trigger — clear any pinned baseline |
| SL placement (trailing/edit ×2) | `:3107`, `:3342` | `0` | same |
| Trigger flip | `:2059` | actual SL `price` | item 1 — pin at trigger |
| Restore triggered SL | `:4102` | actual SL `price` (seed-if-0) | item 1 on reconnect |
| Nuclear `CancelOrderAsync` reset | `:2903` | `0` | position/context torn down |
| Market-reduce path (`isMarketOrder`) | `:2993` | `0` | position closing by market |
| `CompletePositionClose` | `:3825` | `0` | position flat |

`emergencyBaseline` is `0` **except** between the SL trigger and the position end — mirroring exactly where `StopLossTriggerOriginal` is reset, plus the 4 placement sites for a clean pre-trigger start. Scoped cancel (`CancelWorkingEntryCoreAsync`) deliberately touches **neither** field (invariant #3).

---

## Behavior contract / invariants (post-change)

1. **Baseline selection:** emergency measures from `emergencyBaseline` if set (actual SL @ trigger), else `StopLossTriggerOriginal` (trigger). One value, chosen per-quote.
2. **Follows the live SL:** `emergencyBaseline` is set at the trigger flip and updated on every subsequent triggered echo, so it tracks the live SL (manual moves + the app's trailing). The emergency stays M.SL below the current stop.
3. **Pre-trigger tracks the exchange:** `StopLossTriggerOriginal` mirrors exchange-side trigger moves while untriggered → btnMark is unnecessary.
4. **Clean per-trade:** both fields are 0 at every new SL placement and every position end; a new trade cannot inherit a stale baseline.
5. **Zero disables:** an unknown combined baseline (`emgBaseline = 0`) disables the emergency market-stop (restore-hardening rule, unchanged) — e.g. immediately after a restart before the snapshot lands.
6. **Gating unchanged:** emergency still evaluates only post-trigger; the three action tiers (rate-limited reposition / ½-M.SL force reposition / full-M.SL market reduce) are unchanged except for the baseline they measure from.

---

## Residuals / known-theoretical

1. **App trailing vs manual SL divergence:** `emergencyBaseline` follows the live SL via the echo, but `placedStopLossPrice` (which the app's own trailing logic reads) is **not** updated by post-trigger manual echoes — its seed-if-zero single-writer guard blocks that. So after a manual post-trigger SL move, the emergency baseline reflects it while the app's trailing still runs off its own `placedStopLossPrice`. Pre-existing gap (the trailing design assumed the app is the only mover post-trigger); out of scope here, flagged for a future "reconcile app trailing with manual SL edits" pass.
2. **Null `price` at the trigger flip:** `emergencyBaseline` stays 0 and the emergency falls back to `StopLossTriggerOriginal` (the synced trigger). Safe (measures from the trigger), not a crash.
3. **`StopLossTriggerOriginal` now read only via the `emgBaseline` fallback** — retained deliberately as the trigger price of record and the pre-trigger baseline (owner request), and still seeded by placement / untriggered echo / restore / heal.
4. **Stale `CustomLabel7` tooltip** ("To mark the current stop loss price…") remains after btnMark's removal — cosmetic only.

---

## Owner runtime tests to run

1. **Emergency actually fires:** with M.SL checked + a tight threshold, drive price past `liveSL − M.SL` faster than the trailing limit fills (a sharp move) → `Emergency Sell/Buy Market Order Executed.` (execution price is the orderbook fill, not the arming price.) — **passed 2026-07-04.**
2. **Baseline tracks the live SL (manual):** once triggered, adjust the SL on the exchange chart → the emergency arming point moves to `newSL − M.SL` (no btnMark). — **the case that drove the follow-live-SL refinement.**
3. **Exchange-side trigger move, no btnMark:** drag the SL trigger on Deribit while **untriggered** → the pre-trigger baseline (`StopLossTriggerOriginal`) follows. — **passed 2026-07-04.**
4. **Restart into a triggered SL:** emergency baseline restored to the actual SL price, then follows subsequent echoes; emergency still armed. — **passed 2026-07-04.**
5. **Regression:** normal trailing + close cycle; flat/new-trade start (baseline clean).
