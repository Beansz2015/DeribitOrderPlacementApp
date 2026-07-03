# Spec-back — M.SL emergency baseline (`emergencyBaseline`) + trigger auto-sync (as-built)

**What this is:** a specification reconstructed **from the committed code** (commit `57dd0fe` on `master`), not from a forward spec. It states the behavior contract the code now enforces, with anchors, so a reviewer can diff it against `git show 57dd0fe`. Written 2026-07-04. File: `DeribitOrderPlacementApp/frmMainPageV2.vb` (+ `frmMainPageV2.Designer.vb` for the btnMark removal). Diff: 42 insertions, 29 deletions.

**Origin (owner design, runtime testing):**
- **Item 1** — the M.SL emergency market-reduce should measure from the **actual stop-loss price at the moment it triggers** (unknown until then), staying offset from the **trigger** price before that. Market reduce is the last resort (taker fees).
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

## Region 2 — pinning `emergencyBaseline` at the trigger moment (item 1)

- Open `StopLossOrder` echo (`:2059`), on the `SLTriggered` False→True flip: `If Not SLTriggered Then emergencyBaseline = If(price, emergencyBaseline)`, immediately before `SLTriggered = True`. `price` is the triggered SL limit price (the `Triggered SL placed @ $price` value). Captured **only on the flip**, so it stays fixed while the SL trails afterward (subsequent open echoes have `SLTriggered = True` and skip it).
- **Consequence:** the market-reduce point moves from `trigger − M.SL` to `actualSL@trigger − M.SL`. Since the SL limit usually sits a little below the trigger, it fires slightly further out — measuring from where the stop actually is.
- **Post-trigger exchange moves do not change it** (it's pinned), matching "at the point it is triggered."

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
2. **Pinned, not trailing:** `emergencyBaseline` is captured once at the trigger flip and held; the SL trailing afterward does not move it.
3. **Pre-trigger tracks the exchange:** `StopLossTriggerOriginal` mirrors exchange-side trigger moves while untriggered → btnMark is unnecessary.
4. **Clean per-trade:** both fields are 0 at every new SL placement and every position end; a new trade cannot inherit a stale baseline.
5. **Zero disables:** an unknown combined baseline (`emgBaseline = 0`) disables the emergency market-stop (restore-hardening rule, unchanged) — e.g. immediately after a restart before the snapshot lands.
6. **Gating unchanged:** emergency still evaluates only post-trigger; the three action tiers (rate-limited reposition / ½-M.SL force reposition / full-M.SL market reduce) are unchanged except for the baseline they measure from.

---

## Residuals / known-theoretical

1. **Post-trigger manual SL move on the exchange:** `emergencyBaseline` stays pinned at the trigger-moment SL price and does not follow a post-trigger drag. This matches the stated "at the point it is triggered" design; flagged in case the owner later wants it to follow.
2. **Null `price` at the trigger flip:** `emergencyBaseline` stays 0 and the emergency falls back to `StopLossTriggerOriginal` (the synced trigger). Safe (measures from the trigger), not a crash.
3. **`StopLossTriggerOriginal` now read only via the `emgBaseline` fallback** — retained deliberately as the trigger price of record and the pre-trigger baseline (owner request), and still seeded by placement / untriggered echo / restore / heal.
4. **Stale `CustomLabel7` tooltip** ("To mark the current stop loss price…") remains after btnMark's removal — cosmetic only.

---

## Owner runtime tests to run

1. **Emergency actually fires:** with M.SL checked + a tight threshold, drive price past `actualSL@trigger − M.SL` faster than the trailing limit fills (a sharp move) → `Emergency Sell/Buy Market Order Executed.`
2. **Baseline is the actual SL, not the trigger:** confirm the fire point is `actualSL − M.SL` (a bit further out than `trigger − M.SL`).
3. **Exchange-side trigger move, no btnMark:** drag the SL trigger on Deribit while untriggered → the M.SL baseline follows (verify the fire point shifts) with no button press.
4. **Restart into a triggered SL:** emergency baseline restored to the actual SL price; emergency still armed.
5. **Regression:** normal trailing + close cycle; flat/new-trade start (baseline clean).
