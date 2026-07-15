# Spec-back — AutoTrade retirement pulled forward (2026-07-15)

**Why this doc:** the owner ruled 2026-07-15 to retire FrmIndicators and consolidate the settings surface **now**, before the log-only soak, rather than in the post-soak retirement spec the plan assumed (`spec-autotrade-tiein.md` §1 "transition scaffolding", ROADMAP §2). No spec existed; this reconstructs what changed for the coordinator's re-review. **It supersedes parts of the tie-in that were reviewed and APPROVED at `bbebaa7`** — see §6.

**Commits (local, on top of `5b629b3`):** `df615b7` (A) · `cdc7ce4` (B) · `1e314aa` (C+D) · `c622212` (C fixup). Build **0/0 in BOTH Debug and Release** after each (see §7 — Debug now verified explicitly, the sln default is Release).

---

## 1. Owner decisions this implements

1. **Cooloff anchors on the position CLOSE**, not on placement. (Asked directly; see §3.)
2. **Bigger cut than first proposed:** retire the whole FrmIndicators form including the backtest module — archive/hide, don't delete. Add a main-form button to open the settings form. Add a textbox for the ATR length.
3. **Gate config lives in the old autotrader controls** (`txtAmount` on the main form for size; `txtCooloff`, `txtCircuitBreaker`, `txtStartTime`/`txtEndTime` on the settings form), not in the bridge's own duplicate boxes. Rename "Exclusion Time Range" → "Inclusion Time Range".
4. **Delete** ATR Settings + Signal Score groups and EMA Diff Trend Strength; relayout in the same form size.
5. **Live-read the textboxes, no SAVE**; clicking a textbox selects all its text.
6. **Expose the hard-coded `$70`** in a new bottom "Tooling" section, intended as the home for future settings.

## 2. Shape after the change

| Concern | Before | After |
|---|---|---|
| Signal source | FrmIndicators score trigger (neutralised at `5e5acdf`) | SignalBridge only (contract R1) |
| FrmIndicators | visible form, autotrade + backtest + indicators | **never shown**; headless indicator/ATR engine only |
| Settings form owner | FrmIndicators | frmMainPageV2 (revived the dead `_autotradesettings` field) |
| Settings opener | FrmIndicators' `btnAutoTradeSettings` | `btnAutoSettings` on the main form |
| Order size | bridge `size_usd` (bridge.json) | main form `txtAmount` → `OrderSizeUSD` |
| Cooloff / max loss / window | bridge.json + duplicate panel boxes | `txtCooloff` / `txtCircuitBreaker` / `txtStartTime`+`txtEndTime` |
| Cooloff anchor | placement | **position close** |
| ATR length | `txtATR` (ATR Settings) | `txtAtrLength` (Tooling) → host `AtrLength` |
| ATR fallback | hard-coded `Return 70` | `txtAtrFallback` (Tooling) → host `atrFallbackVal` |
| bridge.json | path + tiers + all gates | **path only** (+ inert `slippage_atr_mult`) |

## 3. The cooloff change (owner question, and why it mattered)

`RecordActed` stamped `_lastActionUtc` at placement. But the **flat gate blocks entries for the entire life of the position**, so a placement-anchored cooloff burns off *during* the trade: a 12-minute position with a 5-minute cooloff gives **zero** pause after the exit. The old autotrader's own tooltip says "after a position is exited", and `CompletePositionClose` already stamped `_indicators.lastAutoTradeTime` — the original design agreed. That line is now `signalBridge?.NotifyPositionClosed()`, the single once-per-close completion path (`spec-back-session-2026-07-04` §3), so cooloff means "N minutes after going flat".

Consequence: log-only no longer advances the cooloff anchor (it opens no positions), while the **de-dupe watermark still does** — the soak disposition stream stays gate-for-gate identical to live, minus `refused: cooloff` rows, which log-only cannot generate by construction. **Flagged for the soak reviewers.**

## 4. Half-typed-value safety (owner-requested select-all, and the gap it left)

Select-all on click does **not** stop a signal landing mid-edit from reading `1` of an intended `15` — it only prevents concatenation (`5` + `15` → `515`). So the gate boxes **commit on focus-loss / Enter, not per keystroke**: while you type, the bridge keeps using the last committed value. Unparseable input is **ignored** (last good value stays) and named in the panel's status line.

`txtAmount` on the main form deliberately keeps its existing per-keystroke `SyncTradeInputsFromUi` sync — manual trading depends on that path and it is well-tested; and prefixes of a positive number are always *smaller*, so a mid-typing signal can only under-size, never over-size.

**Related fix:** `IsInsideSessionWindow` returned `True` (unrestricted) on unparseable text, so the window gate silently vanished while the text was malformed. Now **fail-closed**: both blank = unrestricted (contract §4.6), non-blank-and-unparseable = refuse.

## 5. New/changed disposition tokens (pre-soak — the set is still unfrozen)

- **`refused: size`** — NEW. Size now comes from `txtAmount`; an empty/zero box would otherwise reach the exchange as a zero-amount order and bounce as a rejection. Appended **after** the contract-ordered §4.6 gates, so contract gate order is untouched.
- Everything else in the `9a51b5f` / `5b629b3` set is unchanged (`refused: levels` from F-1 included).

## 6. What this supersedes from the APPROVED tie-in review (`bbebaa7`)

The coordinator approved `2dcb84e`'s panel and accepted six judgment calls. Three no longer describe the code:

1. **Panel gate config + SAVE** (approved) — **gone**. `SaveConfig` and the bridge-owned gate fields are deleted; config is live-read from the old controls. Owner-awareness item 1 from the review (window semantics inverted) now also shows in the UI: the group is literally renamed "Inclusion Time Range", and the `21:30`/`22:00` defaults are **blanked** — kept as-is under inclusion semantics they would have permitted trading *only* in that half hour.
2. **Judgment call 5** (log-only advances de-dupe **and cooloff**) — now de-dupe only, per §3.
3. **Config persistence** — `bridge.json` no longer stores the gates, so cooloff/max-loss/window/tiers **reset to designer defaults at every app start** until ergonomics Phase A. This matches the old autotrader's behaviour. `txtCircuitBreaker`'s default is `-1` = **breaker disabled** (bridge.json defaulted it to 50 = enabled) — deliberate, it is the pre-existing default, but the owner should set it before live.

Unchanged and still as approved: the contract §4 gate chain order and clauses, the `manualSL` sign math, the interlock, the hot regions (untouched), F-1/F-2.

## 7. Risks / what to look hard at

1. **Headless FrmIndicators is the big one.** The form is never `Show()`n, so `Form.Load` never fires — its body moved to `StartHeadless()`, called by the host after construction. The handle realised in the constructor (2026-07-04 handle-race fix) is now the **only** thing making the receive loop's `Me.Invoke` marshals legal. **Runtime-verify the ATR display/value still updates** (it feeds the slippage guard's fallback); if `CurrentATR` stays 0 the whole headless premise is wrong.
2. **Debug vs Release.** The sln default is **Release**, which is what every "0/0" in the tie-in reports was. The owner debugs in **Debug** (and HANDOVER-2 §2 records a past chain that didn't Debug-build). Both configs are now explicitly verified 0/0.
3. **`_indicators.lastAutoTradeTime` / `LogFailedEntry` removed** — `LogFailedEntry` never ran (its only call site is commented out) and read the never-assigned `_autotradesettings`, so it was a latent NullReferenceException. It also stamped a cooloff on an *abandoned entry*; that behaviour is gone. If wanted, it belongs on the bridge.
4. **First-click bug fixed:** `AutoTradeSettings_Load` called `Me.Hide()`, so the first click of the opener was always a no-op. Removed.
5. **Deleted from the dying file:** ProcessAutomatedSignal, IsTrendAligned, CanPlaceAutomatedOrder, ExecuteAutomatedTrade, IsWithinRestrictedTimeRange, GetCurrentUTC8TimeString, the 4-arg LogTradeDecision, the backtest module, btnATR paste, btnAutoTrade/btnAutoTradeSettings handlers, StickToHost. **The backtest and the ATR-paste button are gone as working tools** — that was the owner's explicit call ("retire the entire backtesting module"). FrmIndicators' own controls stay in its Designer, unhandled (archived, not deleted).

## 8. Layout verification (how, not just "looks fine")

The form was rendered off-screen from the built assembly by a throwaway harness (scratchpad, not committed) that also scanned every control for (a) bounds escaping the parent and (b) text not fitting its label. Two lessons worth recording:

- `DrawToBitmap` paints the **whole window incl. title bar**; sizing the bitmap to `ClientSize` shifts everything down by the title-bar height and crops the bottom, which reads as a layout bug that isn't there. Size the bitmap to `f.Width/f.Height`.
- The harness must set `Application.SetHighDpiMode(PerMonitorV2)` to match `My Project/Application.Designer.vb`; without it the form's `AutoScaleMode.Font` rescaled 512×856 → 358×514 and every measurement was meaningless.

Result: **no bounds overflow, no text overflow, client size unchanged at 512×856**, and the form confirmed not to autoscale (`AutoScaleDimensions == CurrentAutoScaleDimensions == {10,25}`). Real defects the render caught that the arithmetic missed: both grey notes truncated mid-sentence, two labels 1px short of their own text, the "candles" unit label overflowing its group.

## 8a. Owner-run findings, 2026-07-16 (fixed in `895b73a`)

1. **`btnAutoSettings` shipped as "Auto".** At 110 wide the caption (122px on one line) wrapped, and at height 50 there was only room for one line (two need 51px), so the second line clipped. Now 140 wide, ends at 1048 inside the 1080 client. **The harness's text-fit scan only covered Labels** — it now covers Buttons and CheckBoxes, which is the check that would have caught this. (It also flags the pre-existing multi-line trade buttons — deliberate, they have the height — and three labels clipped 2–3px in AccountInfo/PlacedOrders: **pre-existing, left alone**, report-only.)
2. **No way to see the ATR at runtime.** Retiring FrmIndicators took its ATR display with it, and that display is exactly what proves the headless engine is running — so the §9.1 test below had no instrument. New **live ATR readout** in Tooling (`lblAtrNow`): value, source and resulting limit, colour-coded (green = signal payload, cyan = headless indicator, orange = none/fallback), ticking 1 s while the window is open. `GetEffectiveAtr()` is now the single source of truth for both the guard and the readout, so the display cannot drift from what is enforced; the no-ATR case still returns the fallback **unmultiplied** (the original `Return 70` semantics).

## 9. Owner test additions (on top of `spec-autotrade-tiein.md` §6)

1. **ATR still lives:** open Auto Settings and read the Tooling **ATR now:** line — it should show a real value in **cyan (indicator)** with FrmIndicators hidden and no fresh payload. Orange (`NONE → fallback`) means the headless engine is NOT computing and the retirement premise is broken.
2. **Tooling knobs bite:** change ATR Length → the computed ATR changes; blank the payload + indicator ATR path → the slippage limit uses the ATR Fallback value, not 70 hard-coded.
3. **Commit-on-blur:** type a cooloff digit and leave it un-blurred while dropping a fresh payload → the bridge must use the OLD value; click away → new value applies.
4. **Fail-closed window:** put garbage in Start Time → entries refuse (`refused: window`), not "unrestricted". Blank both → unrestricted.
5. **Size gate:** clear the main form's Amount → `refused: size`.
6. **Opener:** the Auto Settings button opens the settings form on the FIRST click, aligned with Results/Clear.
7. **Regression:** manual trading unaffected; no FrmIndicators window appears anywhere.
