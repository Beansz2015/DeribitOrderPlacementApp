# Spec — retire FrmIndicators; engine ATR in every mode, with a switchable Flat ATR

**Origin:** `docs/ROADMAP-2026-08.md` §5 hygiene row "FrmIndicators full retirement". Owner rulings
2026-10-06 (below). **Status at writing:** open, no code written. Every fact below verified in code
on 2026-10-06 at HEAD `4a3e29d`.
**Scope:** `frmMainPageV2.vb`, `SignalBridge.vb`, `AutoTradeSettings.vb` (+ Designer),
`AppUserSettings.vb`, `orderapp-settings.example.json`; delete `FrmIndicators.vb`,
`FrmIndicators.Designer.vb`, `FrmIndicators.resx`. **Touches the bridge path → Opus, high.**
**Ships:** ON. ⚠ It changes behaviour for manual trades (see §3).

**One-line job:** remove the headless FrmIndicators form and its second websocket. The chase-slippage
guard then takes its ATR from the engine payload in **every** bridge mode, or from a **Flat ATR** the
owner sets and can switch to. Flat ATR is also the fallback.

---

## §0 — Owner rulings, 2026-10-06. Do not re-litigate.

| # | Ruling |
|---|---|
| R1 | **Full retirement.** Accept payload ATR or the Flat ATR, nothing else. |
| R2 | **Engine ATR in mode Off too.** The bridge reads the payload ATR read-only in Off. It never acts in Off. |
| R3 | **Flat ATR is switchable and is the fallback.** A "Use flat ATR" checkbox: ticked = always Flat ATR; unticked = engine ATR, Flat ATR only when no fresh engine ATR exists. |
| R4 | **"ATR Length" box is removed.** It is a candle count that only FrmIndicators uses. |
| R5 | **"ATR Fallback: 70 USD" becomes "Flat ATR (USD)".** |
| R6 | **Flat ATR is an ATR, so ATRSlip multiplies it.** Limit = ATR × ATRSlip, where ATR is the engine ATR or the Flat ATR. Today the 70 is the limit itself, unmultiplied — that changes. |

**R2 supersedes the owner ruling of 2026-07-16** ("bridge Off must mean back on our own 14-period
ATR", quoted in `SignalBridge.vb` `StopWatching` and at `:615-622`). Those comments must be rewritten,
not left beside code that contradicts them.

## 🚫 Do-not-touch

| Thing | Why |
|---|---|
| The triggered-SL chase (`frmMainPageV2.vb:2669-2760`, `isSLRepositioning`) | It has **no** ATRSlip cap and must keep none: an exit chase that gives up leaves the position open. See §5. |
| The gate chain, dispositions, de-dupe watermark, act path in `SignalBridge.vb` | R2 is a read-only ATR tap. Mode Off must still never write a disposition row or act. |
| `ChaseAbortReason` order (ATR cap first, then EV floor) | Load-bearing; see its comment at `frmMainPageV2.vb:3826`. |
| `maxSlippageATRchecked` as the single arm for chase-abort guards | Housekeeping 8b ruling. |

## §1 — The dependency today (verified)

- Constructed and started at `frmMainPageV2.vb:1064-1065`. Runs its own `ClientWebSocket` with an
  unguarded reconnect (`FrmIndicators.vb:20`, `:69`, `:161`).
- Read in exactly two places: `GetEffectiveAtr` (`:3751`) and the ATR-multiple **log text** inside
  `IsATRSlippageExcessive` (`:3788`).
- `GetEffectiveAtr` order today: payload ATR → `_indicators.CurrentATR` → none. With none,
  `CalculateATRSlippageLimit` returns `atrFallbackVal` (70) **unmultiplied** (`:3765-3770`, where the `eff.Atr <= 0D` branch returns it).
- 🚨 **In mode Off the payload ATR is always 0.** `StopWatching` zeroes `_lastSignalAtr` and stops the
  watcher and the staleness timer (`SignalBridge.vb:450-470`). So **every manual trade in Off uses
  FrmIndicators' ATR today.** This is why R2 exists.
- `atrLengthVal` / `AtrLength` (`:35`, `:38-41`) and `SetToolingValues(atrLength, …)` (`:59-62`) exist
  only to feed FrmIndicators (`FrmIndicators.vb:1253`).
- **ATR Length and ATR Fallback are NOT persisted.** They reset to 7 and 70 on every launch.
- **Latent defect `D1` (FrmIndicators retirement — the slippage log line reads the wrong ATR):**
  `:3788` divides by `_indicators.CurrentATR`, not by the ATR the limit used. With a payload ATR in
  force, the logged "x ATR" figure is against a different ATR (7-period engine vs the indicator's
  own). Display-only. Fixed by §2.3.

## §2 — The change

### 2.1 Bridge: an ATR-only tap in mode Off (R2)

- `Mode` set to Off (`SignalBridge.vb:203`) keeps the watcher and the staleness timer **running**.
- In `EvaluateLatestPayloadAsync`, after the status-snapshot block (the `SyncLock` that sets
  `_lastSignalAtr`, ending `:636`), **if mode is Off: `RaiseEvent StatusChanged()` and `Return`.**
  Nothing below that point runs in Off: no `ForceStop`, no de-dupe marking, no gate chain, no
  disposition row, no act, no feedback publish.
- Do **not** mark `_lastSeenInstanceId/_lastSeenSignalId` in Off. Switching Off → Log-only must still
  give the current payload its one disposition, exactly as today's `EvaluateNow()` on mode change does.
- The staleness timer in Off **zeroes `_lastSignalAtr` only**. It must not raise the stale alert in
  Off — the owner trading manually must not be paged about the engine.
- The freshness and `SignalState = "OK"` rules for taking an ATR are **unchanged** (`:623-634`).
- `StopWatching` is still called on dispose (`:340`). Decide whether Off now calls it at all; the
  simplest correct shape is: Off no longer calls `StopWatching`, dispose still does.
- Log line on entering Off changes from "watcher and staleness checks stopped" to say the watcher
  stays on for ATR only, read-only.

### 2.2 The ATR source and the limit (R3, R5, R6)

```
GetEffectiveAtr():
  If useFlatAtr                → (flatAtrVal, "flat ATR (switched)")
  ElseIf bridge ATR > 0        → (bridgeAtr,  "signal payload")
  Else                         → (flatAtrVal, "flat ATR (fallback)")

CalculateATRSlippageLimit():  eff.Atr × mult    (mult = maxSlippageATRmult, or 0.6 when blank/0)
```

- There is no "none" branch any more: Flat ATR is always > 0 (validated).
- ⚠ **Default behaviour change from R6:** with Flat ATR left at 70, the fallback limit becomes
  70 × 0.6 = **$42**, down from $70. Owner was told this when ruling. Do not change the default 70
  without asking.
- Rename `atrFallbackVal` → `flatAtrVal`; add `useFlatAtr As Boolean`. Both plain fields, both read on
  the receive thread — same torn-read class as today's fields.
- The Tooling readout (`lblAtrNow`, "Current ATR: …") shows the three new source strings.

### 2.3 The log line (`D1`)

`IsATRSlippageExcessive` computes `slippageInATR` from `GetEffectiveAtr().Atr`, the same ATR the limit
used. The census count for `IsATRSlippageExcessive` (8) must not change: edit the body, add no call.

### 2.4 Auto Settings form (R4, R5, R3)

- Remove `txtAtrLength`, `lblAtrLenCap`, `lblAtrLenUnit`, its validation (`AutoTradeSettings.vb:416`,
  `:470`) and its entry in the commit-box list (`:164`).
- `lblAtrFallbackCap` → "Flat ATR:" (unit stays "USD"). New tooltip: the ATR used when "Use flat ATR"
  is ticked, and the fallback when no fresh engine ATR exists; ATRSlip multiplies it.
- Add `chkUseFlatAtr` ("Use flat ATR") beside it. It commits on change.
- `SetToolingValues` becomes `(flatAtr As Decimal, useFlatAtr As Boolean)`.
- Update the `lblAtrNow` tooltip's priority text (`AutoTradeSettings.Designer.vb:681`).

### 2.5 Persistence (coordinator default — owner may override)

Both values are **session-only today**. A switch the owner must re-tick on every launch is a trap.
Persist `flat_atr_usd` and `use_flat_atr` in `orderapp-settings.json` through item A's save path, the
same way the circuit breaker persists (`spec-breaker-persist-atr7-item8.md` R1). Add both keys to
`orderapp-settings.example.json`. An old file without them loads with 70 and unticked.
`atr_length` was never a key, so there is nothing to remove from JSON.

### 2.6 Retire the form

- Delete the field (`:23`), construction and `StartHeadless` (`:1064-1065`), `atrLengthVal` and
  `AtrLength` (`:35`, `:38-41`).
- Delete `FrmIndicators.vb`, `.Designer.vb`, `.resx`. The project is SDK-style with no explicit
  `Compile` entries for it — confirm the build after deletion.
- Rewrite the comments that name FrmIndicators or the 14-period ATR: `SignalBridge.vb:149`, `:507`,
  `:615-622`, `StopWatching`'s comment; `frmMainPageV2.vb:3743-3746`.

## §3 — What the owner will see change

| Situation | Today | After |
|---|---|---|
| Manual trade, bridge Off, engine publishing | indicator ATR × 0.6 | **engine ATR × 0.6** |
| Manual trade, engine stale/down | indicator ATR × 0.6, else $70 flat | **Flat ATR × 0.6** ($42 at default) |
| "Use flat ATR" ticked | n/a | Flat ATR × 0.6, always |
| Bridge Log-only / Live, fresh payload | engine ATR × 0.6 | unchanged |
| Second websocket to Deribit | yes | **gone** |

## §4 — Acceptance

1. Build and gate pass; OrderCheck all pass; the nine `frmMainPageV2.vb` censuses re-run and any
   change explained (expected: unchanged).
2. **OrderCheck fixtures (new), pure functions — extract the decision so it is testable:**
   a. switched → Flat ATR whatever the payload says;
   b. unswitched + payload ATR > 0 → payload ATR;
   c. unswitched + payload ATR = 0 → Flat ATR;
   d. limit = ATR × mult; mult 0/blank → 0.6;
   e. a fixture that **fails against today's code**: no payload ATR → limit is 70 × 0.6, not 70.
3. **Mode-Off tap, runtime, testnet (owner-driven session or harness on a TESTNET title):** bridge Off,
   engine publishing → Tooling readout shows source "signal payload" with the payload's ATR; **zero
   new rows in `bridge-dispositions.log`**; no stale alert after stopping the engine for > 3 checks;
   readout falls to "flat ATR (fallback)".
4. Off → Log-only gives the current payload exactly one disposition row (no regression of the
   de-dupe).
5. `grep -rn FrmIndicators DeribitOrderPlacementApp/*.vb` returns nothing. The app makes one
   websocket connection to Deribit (verify by the log: no indicator connect lines).
6. Persistence: tick "Use flat ATR", set 55, close, relaunch → both restored.

## §5 — Not in scope, recorded

- **The owner's stated understanding of ATRSlip (2026-10-06) is wider than the code.** The owner said
  ATRSlip also cancels "triggered SL repositioning". **It does not.** The cap guards the entry side
  only: entry chase (`:2441`, `:2494`), trailing-entry chase (`:2797`, `:2836`), `ExecuteOrderAsync`
  (`:3909-4027`) and SL placement for a trailing entry (`StopLossForTrailingOrderAsync`, `:5003`,
  `:5044`). The triggered-SL chase has no cap, and `docs/spec-sl-chase-v2.md` never mentions one.
  Recommendation: **keep it that way** — an exit chase must not give up. If the owner wants otherwise,
  that is a separate spec.

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Model:** Opus. **Effort:** high. Bridge path and the slippage guard.
- Run this **before** `docs/spec-trade-slippage-fields.md`: that spec reads `GetEffectiveAtr`.
- Coordinator review: Opus, regardless of implementer.
