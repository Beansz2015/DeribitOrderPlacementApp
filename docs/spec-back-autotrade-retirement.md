# Spec-back — AutoTrade retirement pulled forward (2026-07-15)

**Why this doc:** the owner ruled 2026-07-15 to retire FrmIndicators and consolidate the settings surface **now**, before the log-only soak, rather than in the post-soak retirement spec the plan assumed (`spec-autotrade-tiein.md` §1 "transition scaffolding", ROADMAP §2). No spec existed; this reconstructs what changed for the coordinator's re-review. **It supersedes parts of the tie-in that were reviewed and APPROVED at `bbebaa7`** — see §6.

**Commits (local, on top of `5b629b3`):** `df615b7` (A — retire the UI, keep headless ATR) · `cdc7ce4` (B — main-form opener + re-parent) · `1e314aa` (C+D — fold gate config into the old controls, add Tooling, bridge rewiring) · `c622212` (C layout fixup, from rendering the form) · `895b73a` (fixups from the owner's run: button caption + live ATR readout) · docs `d597561`, `c28073d`. Build **0/0 in BOTH Debug and Release** after each (see §7.2 — Debug is now verified explicitly; the sln default is Release).

**⚠ 2026-07-16 — a CULTURE BUG in the coordinator-APPROVED `ebde3aa` made the bridge read EVERY payload as stale on this machine (en-MY); fixed in `8956baa`.** Newtonsoft date auto-parsing + `JToken.ToString()` re-rendered the contract's ISO timestamp in the current culture, and the InvariantCulture parse then rejected it. Culture-dependent (en-US works, en-GB/de-DE/en-MY do not), which is why review and the mock tests missed it. **Full write-up at the top of `impl-report-autotrade-tiein.md`** — including the recommendation that the consumer needs a day-first-culture parse fixture to mirror the engine's A22.

**STATUS 2026-07-16 — owner rebuilt and ran; UI CONFIRMED and §9.1 PASSED — the headless premise HOLDS.** Both forms render as intended (the `Auto Settings` caption is no longer truncated; the regrouped layout and Tooling readout look correct; the settings window opens from the main form). **Critically, the owner confirms the Tooling `ATR now:` line reads a live value** — which retires the single biggest risk in this change (§7.1). **§9.2, §9.2a and §9.3 also PASSED**, which between them runtime-confirm **both** §8b bugs, the retirement's ATR-length repoint, the bridge-first ATR rule, and the commit-on-blur safety mechanism (§4). **Only §9.4, §9.5 and §9.7 remain OPEN** (§9.6 half-observed) — and §9.4/§9.5 need a hand-crafted **actionable** payload with the engine **stopped**. Nothing is push-ready until they run.

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
| ATR **visibility** | FrmIndicators' `lblATR` | Tooling **`ATR now:`** readout — value + source + resulting limit (§8a.2) |
| ATR source selection | inline in `CalculateATRSlippageLimit` | `GetEffectiveAtr()` — one source of truth for the guard **and** the readout |
| bridge.json | path + tiers + all gates | **path only** (+ inert `slippage_atr_mult`) |

## 3. The cooloff change (owner question, and why it mattered)

`RecordActed` stamped `_lastActionUtc` at placement. But the **flat gate blocks entries for the entire life of the position**, so a placement-anchored cooloff burns off *during* the trade: a 12-minute position with a 5-minute cooloff gives **zero** pause after the exit. The old autotrader's own tooltip says "after a position is exited", and `CompletePositionClose` already stamped `_indicators.lastAutoTradeTime` — the original design agreed. That line is now `signalBridge?.NotifyPositionClosed()`, the single once-per-close completion path (`spec-back-session-2026-07-04` §3), so cooloff means "N minutes after going flat".

Consequence: log-only no longer advances the cooloff anchor (it opens no positions), while the **de-dupe watermark still does** — the soak disposition stream stays gate-for-gate identical to live, minus `refused: cooloff` rows, which log-only cannot generate by construction. **Flagged for the soak reviewers.**

## 4. Half-typed-value safety (owner-requested select-all, and the gap it left)

Select-all on click does **not** stop a signal landing mid-edit from reading `1` of an intended `15` — it only prevents concatenation (`5` + `15` → `515`). So the gate boxes **commit on focus-loss / Enter, not per keystroke**: while you type, the bridge keeps using the last committed value. Unparseable input is **ignored** (last good value stays) and named in the panel's status line.

**✅ RUNTIME-CONFIRMED 2026-07-16 (§9.3):** typing into ATR Length left the value untouched until focus left the field. The concern this section was written to answer is closed.

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

1. **Headless FrmIndicators was the big one — ✅ RESOLVED 2026-07-16 (§9.1 passed).** The form is never `Show()`n, so `Form.Load` never fires — its body moved to `StartHeadless()`, called by the host after construction. The handle realised in the constructor (2026-07-04 handle-race fix) is now the **only** thing making the receive loop's `Me.Invoke` marshals legal. The risk was that the WS stream, the poll timer or the `Me.Invoke → UpdateSignals` marshal silently fails on a never-shown form, leaving `CurrentATR` at 0 and the slippage guard quietly on the constant. **The owner's run shows the Tooling `ATR now:` line carrying a live value, which proves the whole chain end-to-end** — see §9.1 for why that reading is dispositive. The headless-component pattern (construct → realize handle in the ctor → host calls an explicit start method, never `Show`) is therefore **validated** and safe to reuse.
2. **Debug vs Release.** The sln default is **Release**, which is what every "0/0" in the tie-in reports was. The owner debugs in **Debug** (and HANDOVER-2 §2 records a past chain that didn't Debug-build). Both configs are now explicitly verified 0/0.
3. **`_indicators.lastAutoTradeTime` / `LogFailedEntry` removed** — `LogFailedEntry` never ran (its only call site is commented out) and read the never-assigned `_autotradesettings`, so it was a latent NullReferenceException. It also stamped a cooloff on an *abandoned entry*; that behaviour is gone. If wanted, it belongs on the bridge.
4. **First-click bug fixed:** `AutoTradeSettings_Load` called `Me.Hide()`, so the first click of the opener was always a no-op. Removed.
5. **Deleted from the dying file:** ProcessAutomatedSignal, IsTrendAligned, CanPlaceAutomatedOrder, ExecuteAutomatedTrade, IsWithinRestrictedTimeRange, GetCurrentUTC8TimeString, the 4-arg LogTradeDecision, the backtest module, btnATR paste, btnAutoTrade/btnAutoTradeSettings handlers, StickToHost. **The backtest and the ATR-paste button are gone as working tools** — that was the owner's explicit call ("retire the entire backtesting module"). FrmIndicators' own controls stay in its Designer, unhandled (archived, not deleted).

## 8. Layout verification (how, not just "looks fine")

Both forms are rendered off-screen from the built assembly by a throwaway harness (scratchpad, **not committed** — recreate from this section if needed) which scans every control for (a) bounds escaping the parent and (b) text that does not fit. It constructs the forms without `Show()`ing them, so no WebSocket opens and no credentials are touched. Lessons worth keeping, each of which cost a wrong conclusion first:

- **Scan Buttons and CheckBoxes, not just Labels.** A `Button` silently *wraps and clips* its caption — that is exactly how `btnAutoSettings` shipped as "Auto" and the Label-only scan saw nothing (§8a.1). Check the single-line caption width against the control width, not only wrapped height.
- **`DrawToBitmap` paints the whole window including the title bar.** Sizing the bitmap to `ClientSize` shifts everything down by the title-bar height and crops the bottom, which reads as a layout bug that is not there — it sent me chasing a phantom clip. Size the bitmap to `f.Width`/`f.Height`.
- **Match the app's DPI mode** (`Application.SetHighDpiMode(PerMonitorV2)`, per `My Project/Application.Designer.vb`). Without it the form's `AutoScaleMode.Font` rescaled 512×856 → 358×514 and every measurement was meaningless.
- **Check which configuration you are actually building.** The harness first loaded `bin\Debug`, which was months stale, and faithfully rendered the *old* form. The sln default is Release (§7.2).

Result: **no bounds overflow, no text overflow, client size unchanged at 512×856**, and the form confirmed not to autoscale (`AutoScaleDimensions == CurrentAutoScaleDimensions == {10,25}`). Defects the render caught that arithmetic missed: the truncated `Auto Settings` caption, both grey notes truncated mid-sentence, two labels 1px short of their own text, the "candles" unit label overflowing its group. **Known pre-existing and deliberately left alone** (report-only, scope discipline): the multi-line trade buttons (`Cancel All Open`, `Mkt. Rdc. Sell`, `No Sprd. Buy`, `Reduce Sell`, `Limit BUY`) wrap by design and have the height for it; `lblPnL`, `lblUSDSession`, `lblBTCSession` are clipped 2–3px by their parents.

## 8a. Owner-run findings, 2026-07-16 (fixed in `895b73a`, owner re-ran and confirmed the UI)

1. **`btnAutoSettings` shipped as "Auto".** At 110 wide the caption (122px on one line) wrapped, and at height 50 there was only room for one line (two need 51px), so the second line clipped. Now **140 wide**, ends at 1048 inside the 1080 client, one line, aligned with `Clear`/`Results` (Y=6, height 50). Root cause of the *miss*, not just the bug: the harness's text-fit scan only covered Labels — it now covers Buttons and CheckBoxes (§8).
2. **No way to see the ATR at runtime — a gap the retirement itself introduced.** Retiring FrmIndicators took its `lblATR` with it, and that display is exactly what proves the headless engine is running, so §9.1 had **no instrument**. New live readout in Tooling (`lblAtrNow`): value, source and resulting limit, colour-coded — **green** = signal payload, **cyan** = headless indicator, **orange** = no ATR / using fallback — ticking 1 s while the window is open (UI-thread `Timer`, enabled on `VisibleChanged`, stopped on `FormClosed`).
   - `GetEffectiveAtr()` became the **single source of truth** for both the guard and the readout, so the displayed number cannot drift from the enforced one. It keeps the receive-thread contract (plain field reads, literal sources, `ValueTuple` is a struct).
   - **Semantics preserved deliberately:** with no ATR at all the fallback **is** the limit and is **not** multiplied by the ATR multiplier — the original `Return 70` behaviour. Refactoring it into the shared helper would otherwise have silently changed the cap to 70 × 0.6 = 42.

## 8b. ATR-source ruling, 2026-07-16 (`bbec1e2`) — owner-found via the new readout

The Tooling readout paid for itself within a day: the owner saw it green (`signal payload`) with no actionable signal, which surfaced **two bugs and one cross-app hazard**.

1. **The guard held an ageing ATR.** `_lastSignalAtr` only refreshed on `direction <> NONE`, so a lone WEAK signal's atr was held **indefinitely** through the NO TRADE stretches that are most of a session, while fresher values streamed past every run. Observed live: **34.91 held from WEAK SHORT #33 while payload #41 said 24.78** → slippage limit $20.95 against a volatility-correct $14.87, **~41% too loose** — and that guard governs **manual** entries too.
   **Ruling (owner):** refresh from **every fresh `signal_state=OK` payload with `atr > 0`, NO TRADE included.** `atr` is an execution-resolution *market measurement*, not a trade decision — the verdict is about conviction, the ATR about volatility. The contract's *"guaranteed non-zero when `direction <> NONE`"* is a **guarantee**, not a claim that other payloads' atr is junk (a live NO TRADE payload carries a real one). **Deviation:** the spec §1 wording is *"last actionable payload's atr"* — owner-ruled, flagged here.
2. **Mode Off froze the ATR forever.** `StopWatching` stops the staleness timer that would otherwise zero `_lastSignalAtr`, but never zeroed it itself — so once the bridge went Off the guard stayed pinned to the last engine ATR with **nothing able to clear it**. Now zeroed in `StopWatching` (mode Off + Dispose).
3. **⚠ The two ATRs are NOT the same measurement — engine period 7, this app 14.** So the slippage limit *steps* whenever the source flips, and "bridge off / stale / skipped" must mean **back on our own 14**, not "keep quoting a frozen 7". This is the owner's stated requirement and the reason bug 2 mattered. **Cross-app item for the coordinator:** if the two periods should agree, that is an engine-side settings decision (`indicators.ATR` length) and goes through the owner — this repo must not write there.

Also zeroed on a fresh **SKIPPED** payload: contract §4.2 is *"never hold the last signal"* on a skip, and its atr is no more holdable than its verdict.

Net: the guard reverts to the host's own indicator ATR on **mode Off, stale, SKIPPED, or `atr <= 0`**. Verified against the **shipped assembly** by reflection — all five transitions pass (OK/direction → adopt; OK/NO TRADE → adopts newest; SKIPPED → 0; stale → 0; `StopWatching` → 0).

**BOTH bugs are additionally RUNTIME-CONFIRMED by the owner:**
- **Bug 1** — §9.2: in Log-only the readout tracks the **engine's live ATR regardless of the local ATR Length**. Decisive, because the engine is emitting only `NO TRADE`: under the pre-`bbec1e2` code those payloads never refreshed `_lastSignalAtr`, so it could not have been tracking the engine at all.
- **Bug 2** — §9.2a: green → cyan on mode Off, at the `txtAtrLength` period.

The **stale** and **SKIPPED** hand-backs remain probe-verified only. They are the same `_lastSignalAtr = 0D` release on the same field that mode-Off now exercises live, so residual risk is low — but they have not been watched in the app.

## 9. Owner test additions (on top of `spec-autotrade-tiein.md` §6)

**Status 2026-07-16:** UI confirmed; **§9.1, §9.2, §9.2a and §9.3 PASSED** by the owner's runs. **§9.4, §9.5 and §9.7 still open**; §9.6 partially observed (the settings window was open in the owner's screenshot, so the opener works — the *first-click* half is not explicitly confirmed). **§9.4 and §9.5 require an ACTIONABLE payload** — gate 4.4 refuses `NO TRADE` before the 4.6 operational gates run, so with the engine emitting NO TRADE they are unreachable and must be hand-crafted (engine stopped, or it overwrites within a run interval).

1. ✅ **PASSED 2026-07-16 — ATR still lives; THE load-bearing test (§7.1).** Owner confirms the Tooling **`ATR now:`** line reads a **live value**.
   **Why a live value is dispositive, not merely encouraging:** with the bridge at mode Off and no engine emitting, `LastSignalAtr` is 0, so `GetEffectiveAtr()` cannot be on the payload branch; and the no-ATR branch prints the literal word `NONE`, never a number. A numeric reading therefore can only come from `_indicators.CurrentATR` — and that is only non-zero if **every** link of the headless chain worked on a form that is never shown: `StartHeadless()` ran in place of `Form.Load`, `ConnectAndStream` opened the WS and filled `ohlcList`, the receive loop's `Me.Invoke → UpdateSignals` marshal succeeded against the constructor-realized handle, and `UpdateATR` read `AtrLength` back from the host. That is the entire premise of retirement commit A, confirmed end-to-end by one reading.
2. ✅ **PASSED 2026-07-16 — Tooling knobs bite, both halves.** Owner: changing ATR Length **with Mode Off** moves the `ATR now:` value; with **Mode Log-only** it shows the engine's ATR **regardless of the ATR Length set**.
   The second half is correct **by design, and is itself the §8b bug-1 confirmation**: ATR Length governs only the *indicator fallback*, so a fresh payload ATR must win (bridge-first, spec §1). Note what that observation proves — the engine is emitting only `NO TRADE`, and under the **pre-`bbec1e2`** code NO TRADE payloads never refreshed `_lastSignalAtr`, so the readout could not have been tracking the engine's live ATR at all. It doing so is bug 1 fixed, observed live.
   *(Still unobserved, minor: with **no** ATR available from either source the limit must equal the ATR Fallback box **unmultiplied** — it is the `eff.Atr <= 0` branch, exercised only when the indicator is also dry.)*
2a. ✅ **PASSED 2026-07-16 — ATR hands back (§8b).** Owner: with the bridge in Log-only and the readout **green (signal payload)**, Mode → **Off** flipped it to **cyan (indicator)**, at the period configured in `txtAtrLength`.
   **What this proves beyond its own line:** (a) the §8b **bug-2 fix is real at runtime**, not just in the reflection probe — `StopWatching` genuinely releases the ATR instead of leaving the guard pinned to the engine's frozen 7-period value with nothing able to clear it; (b) the readout's source discrimination is honest (it changes *source*, not just colour); and (c) **retirement commit A's ATR-length repoint is live** — `UpdateATR` really is reading `AtrLength` back from the host in place of the deleted `txtATR` control. §9.1 proved the headless engine computes *something*; this proves it computes it at the *configured* period.
3. ✅ **PASSED 2026-07-16 — commit-on-blur.** Owner, with Mode Off: typed a new **ATR Length** without leaving the field → `ATR now:` **did not move**; tabbed away → it moved within ~1 s (the readout's 1 s timer repainting after the commit — which incidentally confirms that timer too).
   **This closes the owner's original safety concern** (§4): a signal landing mid-edit can no longer read the `1` of an intended `15`. Select-all alone would **not** have achieved this — it only prevents concatenation; commit-on-blur is what closes the transient.
   **Generalises to the other six boxes by construction:** `InitialiseSettings` wires `Enter`/`Click` → `SelectAllOnEnter` and `Leave`/`KeyDown` → `CommitOnLeave`/`CommitOnEnterKey` over one array — `txtCooloff`, `txtCircuitBreaker`, `txtStartTime`, `txtEndTime`, `txtBridgeTiers`, `txtAtrLength`, `txtAtrFallback` — and `CommitOnLeave` calls the same `CommitGateConfig` + `CommitToolingConfig`. One wiring loop, one handler, one code path: proving the mechanism on any box proves it for all. (Their individual *effects* are still covered separately by §9.4/§9.5.)
   *(Instrument choice, for the record: cooloff — the original idea — cannot be observed in log-only at all, since cooloff anchors on position close and log-only opens none. The window can be, but only with an actionable payload, because gate 4.4 refuses `NO TRADE` before 4.6 ever runs. ATR Length needs neither payload nor engine.)*
4. ⬜ **Fail-closed window:** garbage in Start Time → entries refuse (`refused: window`), not "unrestricted". **Both** blank → unrestricted. (Exactly one blank also refuses, with an orange warning naming it — owner-confirmed semantics 2026-07-16.)
5. ⬜ **Size gate:** clear the main form's Amount → `refused: size`.
6. 🟨 **Opener:** opens the settings form on the **FIRST** click (the `Me.Hide()`-in-`Load` bug, §7.4), aligned with Results/Clear. *Alignment + opening observed; first-click not explicitly confirmed.*
7. ⬜ **Regression:** manual trading unaffected in every mode; no FrmIndicators window appears anywhere.
