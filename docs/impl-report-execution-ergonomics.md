# Implementation report — Execution ergonomics Phase A (items A–E, G, H, I, J)

**Spec:** `docs/spec-execution-ergonomics.md` (Phase A only — item F and the Phase-B parts of C/D
excluded, per the phasing section). **Base:** pushed master + `a176d5f` (the item-J spec amendment).
**Commits:** 9, one per item, in spec order:

| # | Commit | Item |
|---|--------|------|
| 1/9 | `d2bbf64` | A — orderapp-settings.json, persist standing inputs |
| 2/9 | `a39e321` | B — risk-based SIZE button |
| 3/9 | `2686404` | C — journal MAE/MFE, planned R, fees + schema migration |
| 4/9 | `249b875` | D — alerts |
| 5/9 | `4c62bfc` | E — break-even button via shared EditStopLossTo |
| 6/9 | `57d8ed7` | G — guarded placed-SL display clear on entry-abort |
| 7/9 | `abf73c3` | H — Live-mode disposition filter + predicate fixtures |
| 8/9 | `104de4d` | I — sticky-bottom log follow |
| 9/9 | `0749a58` | J — Position-entered line shows the true average fill |

**Build results:** `tools/checks/verify-gate.ps1` run after EVERY commit — Release (sln) 0/0,
Debug (app vbproj direct) 0/0, OrderCheck Debug 0/0, fixtures green, **GATE PASSED ×9**.
OrderCheck grew 36 → 40 (item C migration fixtures) → 48 (item H predicate fixtures).

**Post-retirement reconciliation (binding note in the spec):** every control reference was
re-anchored against the CURRENT forms by symbol before implementation. Key finding: the spec's
seventh standing input `trigger_offset` maps to `txtTriggerOffset` — it exists in MARGINS
(412,135, "Trig.O.", default 30) but is NOT one of the ten engine mirrors (it is parsed directly
at the 8 placement sites), which is why a mirror-list-based reading would have missed it.
The bridge gate-config boxes (Auto Settings) are excluded from persistence, as the note requires.

---

## Item A — persist the standing trade inputs (`d2bbf64`)

**New file `DeribitOrderPlacementApp/AppUserSettings.vb`** (`Option Strict On`): nullable fields
for the seven standing inputs + both checkboxes + `max_slippage_atr_mult` (Nothing = key absent =
leave the Designer default untouched), non-nullable `RiskPerTradeUsd`/`MaxSizeUsd` (defaults
25/500) and the five alert booleans (all default True). `Load(ByRef message)` never throws: file
discovery is the secrets.json pattern (`AppContext.BaseDirectory` then CWD); a malformed file
resets to a clean defaults instance (no half-apply) and reports. `Save()` writes the full
document, round-tripping the no-UI keys (risk/alerts) so hand-edits survive.

**`frmMainPageV2.vb`:**
- `Private userSettings As AppUserSettings` field (beside the DB fields).
- `frmMainPageV2_Load`: load + `ApplyUserSettingsToControls()` **before** `SyncTradeInputsFromUi()`
  (spec-required ordering — the textboxes are written on the UI thread and the existing
  TextChanged/CheckedChanged handlers sync the mirrors; no mirror field is set directly).
  Missing/broken file logs yellow; success logs green.
- Save affordance (implementer's choice, noted per spec): a `ContextMenuStrip` with
  **"Save Trade Defaults"** on BOTH the MARGINS and AMOUNT($) group boxes (the standing inputs
  span both; there is no free space for a button near either).
- `frmMainPageV2_FormClosing`: `SaveUserSettings(announce:=False)` at the very top, in its own
  Try, before `signalBridge?.Dispose()` and socket teardown (the spec's placement note in the
  handler comment was honored).
- Helpers `ApplyUserSettingsToControls` / `CaptureUserSettingsFromControls` / `SaveUserSettings`.

**Repo:** `orderapp-settings.json` added to `.gitignore` and to the verify-gate's never-tracked
list; `orderapp-settings.example.json` committed (and required present by the gate); vbproj copies
the local file beside the exe when present (`Condition="Exists(...)"`, same as secrets/bridge).

**Deviations / notes:**
- `txtComms` (Comms., default 30) is ALSO a standing input by any functional reading, but the
  spec's persisted-keys list is closed and does not include it — followed the spec; flagging for
  an owner decision (one-line addition if wanted).
- The verify-gate edit (two list entries) is outside the item's file list but is exactly the
  gate's purpose for local-only config; noted here rather than silently.

**Acceptance mapping:** change inputs → close → reopen restores (Load-before-sync + FormClosing
save); delete the file → Designer defaults + yellow line (`FindSettingsFile` returns Nothing);
`secrets.json` untouched (no writes anywhere near it).

## Item B — risk-based SIZE button (`a39e321`)

**Designer:** `txtAmount` width 200 → 148; new `btnRiskSize` ("SIZE", 62×47 at (176,49)) inside
the AMOUNT($) group — the only free space near `txtAmount` (group is 246 wide; the caption is
measured single-line at 10pt bold, per the btnAutoSettings wrap-clip lesson). Tooltip documents
the formula and config keys.

**`btnRiskSize_Click`** (UI thread): `ref` = `BestBidPrice` (long) / `BestAskPrice` (short) by
`TradeMode`, refuse ≤ 0; `dist` = `|ref − manualSLval|` when `manualSLval > 0` else
`triggerDistance` (mirrors — same values the engine uses), refuse ≤ 0; refuse
`risk_per_trade_usd ≤ 0`; `size = Floor(risk × ref ÷ dist ÷ 10) × 10`, clamped to
`Floor(max_size_usd/10)×10`; refuse if the result lands below the 10-USD step. Writes
`txtAmount.Text` only (TextChanged mirrors it); logs `Size: $N (risk $R over $D stop distance)`.
All refusals log yellow and leave the amount untouched.

**Spec-example check:** R=25, ref=60000, dist=60 → 25000 → clamp 500 ✓; dist=3000 → 500 ✓
(already a 10-multiple).

## Item C — journal MAE/MFE, planned R, fees + signal columns (`2686404`)

**Tracking (receive thread, engine fields only):** new fields `maePrice`/`mfePrice` (raw low/high
extremes — direction applied at close), `plannedStopAtEntry`, `cumFeesBTC`, declared beside the
position model.
- **Reset:** inside the EXISTING flat→nonzero branch in `HandleOrderPositionUpdates` (the
  `wasOpen` local + `sz.Value <> 0D` / `Not wasOpen` arm — the detection was NOT duplicated).
  The `Dim avg` read was hoisted above the `Not wasOpen` grouping (behavior-identical: the
  average-entry mirror write is unconditional in the nonzero branch, exactly as before) so the
  extremes seed at the incoming average entry. `plannedStopAtEntry = StopLossTriggerOriginal`
  (recorded at placement, so it is the bracket's planned trigger by this echo), `cumFeesBTC = 0`.
- **Per quote tick** (`HandleQuoteUpdates`, right after the bid/ask field writes, guarded
  `positionSizeUSD <> 0D`): two guarded compares — min-bid into `maePrice` (with a `maePrice = 0`
  reseed arm for restart-restored positions that never showed a flat→nonzero transition),
  max-ask into `mfePrice`. No controls, no allocation.
- **Fees:** `user.changes` `trades[*].fee` summed (null-safe) — placed AFTER the positions pass
  (an entry echo's own fee lands after the reset) and BEFORE the `CompletePositionClose` call at
  the bottom of the handler (the closing fill's fee is counted).

**At close (`CompletePositionClose`, beside the `RecordCompletedTrade` call):** direction-aware
from `pendingCloseWasLong`, linearized at `closeAmt/entryPriceAtClose`:
`MAE = Min(0, …)`, `MFE = Max(0, …)` (long: vs `maePrice`/`mfePrice`; short: mirrored), 0/0 when
the extremes are unseeded; `PlannedRiskUSD = |entry − plannedStopAtEntry| × btcSize`;
`RMultiple = signedPL / plannedRisk` (rounded 2dp; **0 when risk unknown** per spec — signedPL
reconstructed from `pendingClosePorL`/`pendingClosePorLAmt`); `FeesUSD = cumFeesBTC ×
indexPriceVal` (2dp). All four trackers cleared after BOTH branches (tracked and
external/liquidation closes). The untracked-close branch records nothing, as before.

**Storage:** `TradeRecord` + 7 properties (`MaeUSD`, `MfeUSD`, `PlannedStop`, `RMultiple`,
`FeesUSD`, `SignalId`, `SignalConfidence` — the last two written empty in Phase A).
`TradeDatabase.MigrateSchema`: 7 individually try/caught `ALTER TABLE ADD COLUMN` with
`NOT NULL DEFAULT` (SQLite backfills existing rows); ONLY `duplicate column name` is swallowed
(a `Catch … When` filter) — anything else surfaces via `DatabaseError` exactly like before.
Insert + `CreateTradeFromReader` extended (null-safe converters for belt-and-braces).
`RecordCompletedTrade` (form) gained five Optional metric parameters (single caller updated).

**Grid:** MAE / MFE / R / Fees columns added (80/80/60/80, F2, right-aligned) after P/L;
Entry/Exit/Size/PL shrank 100 → 90 each; the view form widened 1000 → 1180. **Nothing was
dropped or moved otherwise.** `TradeAnalytics` verified unaffected (named aggregates only).

**Migration evidence (reproducible, runs on every gate):** OrderCheck fixture set 12 builds a
verbatim PRE-item-C 8-column DB with a legacy row in temp, opens it through `TradeDatabase`
(ctor runs the migration), and asserts: both rows readable; legacy row reads metric defaults
(0/'' — old DB opened cleanly); an enriched row round-trips all five metrics; a SECOND open is
clean with rows intact (idempotency = the duplicate-column swallowing). All four PASS
(OrderCheck 40/40 at commit 3; 48/48 from commit 7). Adding fixtures for C was an implementer
addition beyond the spec's "fixtures for H" — it makes the report's required migration evidence
reproducible instead of a one-off manual claim.

**Known/accepted:** the multi-fill last-fill-only limitation stays (spec). Extremes are
bid-for-low/ask-for-high, so MAE/MFE include half a spread of noise — inherent to quote-based
tracking. If the avg token is absent on the transition echo (rare), the seed uses the retained
`positionAvgEntry`; the guarded compares correct it within a tick.

## Item D — alerts (`249b875`)

**`Private Sub Alert(kind As String)`** near `AppendColoredText`: gates on item A's alerts block
(null-settings ⇒ enabled — alerts work even before Load), plays
`SystemSounds.Exclamation` (adverse) / `.Asterisk` (benign) — safe from any thread — and flashes
the taskbar via `FlashWindowEx` (`FLASHW_ALL Or FLASHW_TIMERNOFG`, `UiInvoke`-marshalled,
handle-guarded). Whole body in a swallow-all Try: best-effort, never breaks an engine path.
`Imports System.Runtime.InteropServices` added (shared with item I).

**Nine one-line call sites, no logic changes around any:** both filled-entry branches
(`EntryLimitOrder`/`EntryTrailingOrder` → `entry_fill`, benign); the three
`CompletePositionClose` branches (profit/scratch benign, loss adverse — all under the single
`close_fill` config key); both emergency market-stop branches in
`UpdateStopLossForTriggeredStopLossOrder` (`emergency_stop`); `ORDER REJECTED` in
`HandlePlacementResponse` (`order_rejected`); "Connection lost" + "All reconnection attempts
failed" in `HandleWebSocketDisconnect` (`connection`).

**Deviations / notes:** close alerts fire in `CompletePositionClose` (the single once-per-close
completion point) rather than per fill-label — same events, structurally spam-proof (the spec's
"fires once" acceptance is why). The untracked external/liquidation "Position closed." branch has
NO alert (the spec's call-site list names close *fills*; that branch has none) — arguably the one
close an owner most wants to hear about; flagged for an owner decision. Reposition noise has no
alert kind at all, per spec.

## Item E — one-click break-even stop (`4c62bfc`)

**`EditStopLossTo(newTrigger)`** extracted verbatim from `btnEditSLPrice_Click`: connection
guard, id resolution incl. the audit-2 fallbacks (`CurrentSLOrderId` → `PositionSLOrderId` →
yellow refusal, no null-id send), limit = trigger −/+ `txtStopLoss` by `TradeMode`, the id-223346
`private/edit` payload (`post_only:=True`, `reject_post_only:=False`), send, the two "Updated"
log lines — byte-equal payload, same logs. The button now parses its textbox and delegates
(same outer Try/Catch and "S.L. textbox is 0" refusal as before).

**BINDING honored:** `EditStopLossTo` does **NOT** call `RecordCommandedSLPrice`. It is a USER
edit path — the commanded-price discriminator (4a) deliberately treats its echo as a manual edit
and follows it (re-anchoring `placedStopLossPrice`/`emergencyBaseline`); recording here would
make the discriminator ignore the user's own move (spec item E + spec-back-session-2026-07-04
§9/§10).

**`btnBreakEven`** ("B.E.", Gold, 107×42 at PlacedOrders (379,299) — STATIC: `SetTradeMode`
shuffles the three edit buttons between rows 93/146/194/247 in both layouts, so the band below
294 is the one spot safe in both modes; the button is deliberately not added to the shuffle).
Handler: gates `positionSizeUSD <> 0D AndAlso positionAvgEntry > 0D` (flat → yellow refusal),
`trigger = RoundToTick(avgEntry ± commsVal)` with the sign from the POSITION (+ long / − short),
logs the derivation, calls the shared core (which owns the no-SL-id refusal).

**Note:** the shared core derives the LIMIT side from `TradeMode` (inherited, pre-existing
behavior of the edit button). In the pathological "mode flipped while in a position" state the
BE trigger (position-signed) and the limit derivation (TradeMode-signed) could disagree — exactly
as a manual edit-button click would today. Not changed; flagged.

**Follow-up `f28f811` (owner testnet 2026-07-18):** the first B.E. click on a long (entry 63962 +
comms 32 = trigger 63994) with the market at ~63959 fired the edit and the exchange rejected it
`10034 trigger_price_too_high` — a long's break-even sell-stop must rest BELOW the market (a
short's buy-stop ABOVE it), so break-even is unplaceable until price has moved past it. The math
was correct; only the missing pre-check was wrong. `btnBreakEven_Click` now refuses cleanly
(yellow line) when `beTrigger >= BestBidPrice` (long) / `beTrigger <= BestAskPrice` (short), 0-price
arms skipping the check, the exchange staying the final arbiter — no more doomed edit + red API
ERROR. `Edit T.S.` path unchanged. **Runtime-confirmed working otherwise:** `Updated T.S.`/`Updated
S.L.` lines emit with the correct byte-equal payload; the rejection was the exchange's, not the
app's.

## Item G — guarded placed-SL display clear on entry-abort (`57d8ed7`)

At the end of `CancelWorkingEntryCoreAsync`, after its existing context resets:
`If positionSizeUSD = 0D AndAlso Not SLTriggered AndAlso PositionSLOrderId Is Nothing Then`
→ `placedStopLossPrice = 0D` + `UiInvoke` clearing `txtPlacedStopLossPrice` and
`txtPlacedTrigStopPrice` (the latter is already cleared unconditionally by this path's existing
UiInvoke — kept per the spec's explicit display list; idempotent). The guard is the load-bearing
part: any live position / triggered-SL context leaves everything untouched (invariant 3).
`emergencyBaseline` / the commanded set are NOT touched (already 0 on this path from the
placement reset — this is a display-hygiene clear, not an 8th SL-context reset site).

## Item H — Live-mode disposition filter (`abf73c3`)

**`SignalBridge`:** new pure `Friend Shared IsSignificantDisposition(disposition)` — True iff
ordinal-StartsWith "acted" or "rejected" (Nothing → False; "would-act" → False, defensive).
In `EmitDisposition`, ONLY the host-log `_log(...)` line (with its color chain, moved inside
unchanged) is now conditional on
`IsSignificantDisposition(disposition) OrElse (_mode <> BridgeMode.Live AndAlso _host.IsFlat
AndAlso Not _host.HasWorkingEntryOrder)` — Live always quiet; Log-only mid-position or mid-chase
quiet (the 2026-07-17 amendment); Log-only + flat = the full soak stream, unchanged.
`IsFlat`/`HasWorkingEntryOrder` are plain field-backed host properties (the same reads the 4.6
gate chain already does from this thread).

**Untouched, verified by position in the method:** the `bridge-dispositions.log` file append and
`_lastDisposition` write sit ABOVE the condition (file line count identical across all
modes/states — join integrity); `StatusChanged` is raised by the caller; the panel label reads
`_lastDisposition`; mode changes / START refusals / auto-STOP / stand-down / schema / malformed-
payload lines never pass through `EmitDisposition` and are never filtered.

**OrderCheck:** 8 fixtures pin the predicate (acted + rejected True; would-act, `refused:
not_flat`, `refused: levels`, stale, skipped, duplicate False). The busy-state condition is a
live read — exercised at the owner's runtime check (enter a position in Log-only → per-run lines
stop; flatten → they resume), per the spec's acceptance.

## Item I — sticky-bottom log follow (`104de4d`)

**P/Invoke local to the form** (beside item D's): `SCROLLINFO`, `GetScrollInfo`, `SendMessage`,
constants (`SB_VERT`, `WM_VSCROLL`, `SB_BOTTOM`, `SIF_*`).

**`AppendColoredText`** — check + append + scroll all inside the ONE marshalled action (atomic
per append, UI thread, display-only). BEFORE the append: `GetScrollInfo(SIF_RANGE Or SIF_PAGE Or
SIF_POS)`, `atBottom = nPos + nPage >= nMax − rtb.Font.Height` (slack ≈ one line-height in scroll
units; no scrollbar yet ⇒ at-bottom). Append as before. AFTER: if `atBottom`,
`SendMessage(WM_VSCROLL, SB_BOTTOM)` — scrolls without touching caret or selection;
`ScrollToCaret`/selection-based scrolling deliberately NOT used, per the owner ruling.

**Deviation (flagged):** the spec's design says "append as today", but the pre-existing append
itself moves the caret (`SelectionStart = TextLength` for coloring) — that alone would destroy a
user's in-progress selection and fail the item's own acceptance ("selecting text mid-stream
survives an append"). The action now saves a non-empty selection and restores it after the append
(`rtb.Select(start, len)`). Display-only, same marshalled action; without it the acceptance is
unachievable.

**Correction after the owner's runtime test (2026-07-18):** the original note here claimed
`rtb.Select` "does not scroll" — it does. Restoring the selection scrolls it back into view, so
with text selected and lines appending the box nudges down a little (NOT to the bottom — the
`atBottom` follow is correctly suppressed while scrolled up). The selection itself survives, which
is the acceptance criterion; the owner reviewed and accepted the nudge as cosmetic. Suppressing it
would need the scroll position saved and restored around the `Select` call — not done, low value
against the added P/Invoke.

## Item J — Position-entered line shows the true average fill (`0749a58`)

**Lines touched (the spec asks which):** the two `Position entered:` emissions in
`HandleOrderPositionUpdates`' filled branch — `Case "EntryLimitOrder"` and
`Case "EntryTrailingOrder"`. Each now reads `order.SelectToken("average_price")?.ToObject(Of
Decimal?)()` (the fill-reanchor pattern) into a local, prints it when > 0, else falls back to
`placedPrice`; format `F2` as before. **Display/log-line only:** `placedPrice`/`txtPlacedPrice`
(the chase/order reference), the fill-reanchor staging (`entryFillPrice` in the limit branch is
computed exactly as before), and the DB record are byte-identical.

---

## Suspicious-nearby NOT touched

- **Hot paths:** the entry/reduce/SL chase blocks, gate ordering (`IsCancelPending` before
  work), single-flight flags, and the reposition/cancel lifecycle — untouched. Item C's quote-tick
  addition is the spec'd two guarded compares, placed before the chase blocks, engine fields only.
- **`Decimal.Parse` (culture + throw-on-garbage) in `EditStopLossTo`** — inherited verbatim from
  the button (the byte-equal requirement); the known Edit-T.S. post-trigger rework backlog item
  (4 traps) is untouched and now shared by BE — still backlog, not worsened.
- **`ElseIf (orderState = "untriggered")` mirrors** writing `placedStopLossPrice`
  unconditionally — pre-existing single-writer design, untouched by item G (whose clear runs only
  in the provably-flat state where no such echo context exists).
- **`EmitDisposition`'s file-append failure handling** and the `_log` sink signature — untouched;
  item H only wrapped the final host-log emission.
- **`AppendColoredText`'s rate-limiter dedupe (`skipNext`)** and handle-race guard — untouched;
  item I's logic lives inside the existing `Me.Invoke` action.
- **`SetTradeMode`'s button shuffle** — deliberately NOT extended to `btnBreakEven` (static spot).
- **`positionRestoreAnnounced` / restore seeding (id 778)** — not extended to seed
  `plannedStopAtEntry`; a restored position records `RMultiple = 0` (risk unknown) by design.

## Owner decisions — BOTH RULED YES 2026-07-18, implemented in `e79026d`

1. **Persist `txtComms`? → YES.** `comms` is now the eighth persisted standing input
   (`AppUserSettings.Comms` + load/save, written in `ApplyUserSettingsToControls` so TextChanged
   mirrors `commsVal` like the other seven, snapshotted in `CaptureUserSettingsFromControls`,
   example json updated). Rationale recorded in the spec amendment: item E's break-even trigger
   derives from `commsVal`, so a silent reset to the Designer default moves where B.E. puts the stop.
2. **Alert on the untracked external/liquidation close? → YES.** New alert kind
   `external_close` with its **own** config key (default on) and an **adverse** tone —
   deliberately not folded into `close_fill` so it stays audible when routine close chimes are
   off. Call site = the existing "Position closed." branch in `CompletePositionClose`, one line,
   no logic change. Spec item D amended to match.

Both changes gate-green (build ×3, OrderCheck 48/48). Spec `docs/spec-execution-ergonomics.md`
items A and D carry the amendments so the decisions survive this conversation.

## Still open

1. Item C acceptance's live half (plausible MAE/MFE/R/fees on a real test trade) and the
   owner-eyeball acceptances of B/D/E/G/I/J belong to the next runtime session — the
   deterministic halves (build, fixtures, migration) are green here.

## Runtime acceptance results (owner testnet, 2026-07-18)

- **A, D, J — PASS.** **I — PASS** with a cosmetic note (a selected-then-appended box nudges down
  a little as `rtb.Select` scrolls the restored selection into view; selection survives; owner
  accepted).
- **B — PASS** (both the size line and the zero-distance refusal). Surfaced a config insight: at
  the shipped defaults (risk 25 / cap 500) the raw risk-size exceeds the cap for any stop tighter
  than ~$3,200, so the button returns `max_size_usd` for every normal structural stop (effective
  risk ≪ $25). Working as designed; the owner tunes `max_size_usd` to make risk-sizing bind. No
  code change.
- **C — PASS** (values checked): min-size (10 USD) makes MAE/MFE/Fees land at ±$0.01 (correct
  magnitude); signs correct (long ID 84 MAE −0.01); fees discriminate maker (0.00) vs taker close
  (0.01); old rows read 0 (clean migration). Caveat noted: R is derived from the 2dp-rounded P/L,
  so min-size scratch trades show R = 0.00 — meaningful only at real size.
- **E — FIXED** (`f28f811`, above): the reported `10034 trigger_price_too_high` was the exchange
  correctly rejecting a break-even stop placed before price reached break-even; the pre-flight
  guard now refuses cleanly. Re-test pending.
- **G — HALF PROVEN (2026-07-18, second attempt):** forcing the abort with ATRSlip 0.05 produced a
  **raced** abort — the entry filled before the cancel landed. That accidentally exercised the
  safety-critical half of item G's acceptance: with a live position the guard **correctly refused to
  clear** (`Stop Loss` kept 64872 = trigger 64892 − S.Loss 20; item G's block clears both SL boxes, so
  its survival proves the block did not run — `PositionSLOrderId` was non-Nothing). The
  *clears-when-provably-flat* half remains unobserved and is hard to reach on testnet (thin book fills
  entries near-instantly). Two PRE-EXISTING defects surfaced by that run are specced in
  `docs/spec-back-execution-ergonomics-runtime.md` — item 1 (benign id-31 `not_open_order` logged red)
  **fixed here**; item 2 (TP / Entry / Trig Stop all read 0 while a live position exists) handed off.
- **G — first attempt, INVALID TEST:** the owner tested via **Cancel All Open** (the nuclear
  `CancelOrderAsync`), not the scoped `CancelWorkingEntryCoreAsync` that item G touches; the
  observed box-clear was pre-existing nuclear-cancel display behavior, and item G's guard would
  have blocked its own clear anyway (position open + SL triggered). Correct test = force an
  ATR-slippage-guard abort while FLAT (drop ATRSlip to ~0.05, place a Limit, let the chase trip
  the guard) → the SL/Trig-Stop boxes should read 0. Still owner-eyeball-pending.
