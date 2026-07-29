# Coordinator review — EV-aware chase budget (2026-07-28)

**Verdict: APPROVED. All five open asks RULED — none changes code.** Implementation
`03bafe8 · 2ffbe1a · fef4371 · e9f5bfc` (+ spec-back `ac860d9`) against
`spec-ev-chase-budget.md`; review request `spec-back-ev-chase-budget.md`. Ships OFF
(`min_net_move_pct = 0`); the owner's §6.3 runtime pass remains and can run before or after this
review's fold-backs land.

## 1. What this reviewer executed and verified (not taken on trust)

- **Gate EXECUTED at HEAD: GATE PASSED, OrderCheck 124/124** — including the 7 D3 persistence
  fixtures and their final "left the settings path as it found it" assertion.
- **`git show 2ffbe1a` read in full.** The four gates are the one mechanical substitution the
  spec-back describes — `Dim abortReason = If(maxSlippageATRchecked, ChaseAbortReason(<own-side>,
  "<dir>"), Nothing)` — and nothing else inside those blocks moved. The VB ternary `If(...)`
  short-circuits, so the unchecked-arm behaviour (no guard calls at all) is preserved exactly.
- **Tripwire censuses re-grepped independently, all at spec-back values:**
  `ResetCommandedSLPrices()` 8 · `cancelPending = False` 5 · `emergencyBaselineSettled` 10 ·
  `RecordCommandedSLPrice` 3 · `SendReduceMarketOrderAsync` 5 · `ResetOrderAttempt()` 5 ·
  `CancelWorkingEntryCoreAsync` 6 · `IsATRSlippageExcessive` 11 → 8 (delta = the 4-site collapse
  into `ChaseAbortReason`, fully accounted) · reason literals `"ATR slippage"`/`"EV floor"` on
  exactly one code line each.
- **`emergencyFired` raw-count note for future greps:** the census is 1 decl + 2 sets + 3 clears +
  3 reads = **9 code sites**, but a raw grep returns **10** — the 10th is a pre-existing N1-era
  *comment* at :2253 naming the latch. 10 at `cd567e1`, 10 at HEAD: unchanged by this diff, and the
  emergency block (:4245–:4260 fire path, all three clears) is untouched.
- **Knob-0 parity verified structurally** (acceptance 2): ATR is called first with the same
  argument in the same position (the `Decimal?`→`Decimal` conversion moved from one call's
  argument to another's — same site, same throw semantics); on ATR-False the predicate's first
  guard returns False at knob ≤ 0; both literals byte-identical. The two pure argument
  evaluations (a field read, a multiply) are the only delta — accepted.
- **Commit 1 and commit 3 diffs read**: spec §1 predicate verbatim; fee round-trip derives as
  2 × maker (no hardcoded 3.0); absent keys ⇒ 0/1.5/3.5; invariant parse in BOTH the commit and
  the warning path; seed-before-commit positioned with the other four applications;
  `SetMinNetMovePct` follows the breaker's contract (any parsed value, parse-failure keeps last
  good) and the breaker's userSettings arrangement.

## 2. Rulings

### D1 — RATIFIED: the EV check reads the own-side quote

The implementer's algebra was re-derived and holds in **both** directions, not just LONG:
LONG (target above, bid ≤ chaseTarget): remaining term larger AND fee term smaller AND floor
term smaller ⇒ binds strictly later. SHORT (target below, ask ≥ chaseTarget): the LHS grows by
(ask − ct)(1 − fee) while the RHS grows by only floor × (ask − ct) ⇒ binds strictly later.
So own-side can only ever chase LONGER than the economically-exact `chaseTarget` input — it
defers to today's ATR-cap behaviour, never aborts a chase the exact input would keep. Magnitude
≈ spread + tick, under 2% of a 5 bps threshold — inside knob-tuning noise. Against that, the
own-side choice buys invariant-8 uniformity (all four gates, one guard input) and the mechanical
diff. **The input is now PINNED own-side** (folded into spec §2); revisiting it is a spec change.

### D2 — RATIFIED: `ChaseAbortReason` replaces the literal `OrElse`

The spec's own §2 demands two distinct cancel reasons, which a bare `OrElse` cannot name. The
helper preserves every §2 property: ATR evaluated first (keeps owning `originalSignalPrice`
seeding + the attempt-reset side effect), both arms under the single `maxSlippageATRchecked`
switch (housekeeping 8b), each literal in exactly one place. The `ByRef`/module-field
alternatives are correctly rejected. Shape folded into spec §2.

### D3 — RATIFIED: acceptance 4 stays automated (the 7 persistence fixtures)

Filesystem I/O is not "a UI layer on the gate" (the charter's actual exclusion), the item-12
SQLite fixture is precedent, and the resolved path is OrderCheck's own bin — structurally never
the app's. The backup/restore + leave-as-found assertion executed and passed under this review's
own gate run. One recorded assumption: gate runs are serial (a concurrent gate could race the
`.ordercheck-bak`) — true of every current usage. **Acceptance 4 is hereby OFF the owner's
runtime list** (§6.3 remains).

### D4 — no action

Trap-application numbering is a historical label; the comment stating both counts is the right
form. Specs are not renumbered when the queue reorders.

### §3 residual — ACCEPTED, with the DERIVATION CORRECTED (the review's one substantive delta)

The spec-back flagged this claim for a second pair of eyes, and it needed one. **Gates 3/4 are
not a post-fill/post-close context.** The block at :2378–:2381 requires a *working entry order*
(`CurrentOpenOrderId` + `CurrentSLOrderId` + `isTrailingStopLossPlaced`), and the only site
setting `isTrailingStopLossPlaced = True` (:4650) is the **BuyTrail/SellTrail placement** — the
manual Trail flow, whose bracket has no TP leg. So gates 3/4 are the **pre-fill chase of a manual
Trail entry**, running while `manualTPval` is whatever the owner had in the box at placement
(the :3193 clear has NOT run — no position exists yet).

The corrected rule, uniform across all four gates: **the EV floor is live wherever
`manualTPval > 0` at evaluation.** Bridge acts (bracket flow, gates 1/2) always have it; manual
brackets with a typed TP have it; offset-only flows — at either block — skip to ATR-only per §3.
A Trail entry WITH a typed manual TP therefore has the EV floor live at gates 3/4, correctly:
`manualTPval` is the honest in-force target there too (the Trail flow itself reads it —
:4623 displays it as the placed TP, and :2484 seeds `TPTrailprice` from it post-fill).

The implementer's practical conclusion survives for a different reason: gates 3/4 will usually
run ATR-only because the Trail flow is offset-centric, not because the target was cleared. The
consequences all still hold: fail-safe, §6.3's entry-chase acceptance is the right scope, and
**no follow-up micro-spec is needed** — the trailing chase already has the correct target source
whenever one exists. Corrected derivation folded into spec §3.

### §4 finding — RULED: queued micro-spec, and YES the engine seat should be told

Verified live: `TakerFeeRate = 0.0005D` (:354, 2024 schedule) sets the default comms every index
tick (:1871 `comms = TakerFeeRate × indexPrice`), and comms feeds the derived TP and the item-E
break-even trigger — ~43% overstatement against the 2026-08-01 taker of 3.5 bps. The implementer
was right not to touch it (a TP-moving behaviour change, adjacent to the Rec-2-deferred
arithmetic). Disposition:

- **Micro-spec queued: `spec-fee-comms-repoint.md`** (written alongside this review) — repoint
  the default-comms computation to the persisted `taker_fee_bps` key; owner acceptance on the
  comms-default shift (≈ $10 on a derived TP at 64k). Owner schedules it — it is Aug-1-adjacent
  correctness; recommended slot: with the owner's Aug-1 settings touch, or immediately after N1b.
- **Engine-seat relay: one line** — their §0 "fee constants deliberately duplicated per-repo"
  note should record that the order app had a third, older copy (2024 taker) driving its comms
  default, now known and queued for repoint. No contract impact.

## 3. Endorsements (adversarial table checked, two items called out)

The §5 table's cases were spot-checked rather than re-run in full; two deserve the record:

- **Arm-switch coupling, consciously ticked:** unticking Max Slippage ATR disables the EV floor
  too. This is what spec §2 mandates (housekeeping 8b's single arm) and is harmless for bridge
  trades (the checkbox is a bridge START precondition), but the owner should know the manual-
  trade consequence. It rides the review summary to the owner rather than a code change.
- **Gate 3's missing `bestBid` null check** — pre-existing, position-identical before/after the
  diff, not reached in practice (quote messages carry both sides). Stays on the hygiene backlog.

## 4. Fold-backs landed with this review (the N1 precedent — no stale greps)

1. Spec §2: D1's own-side input PINNED; D2's `ChaseAbortReason` shape recorded; the standing
   "four gates call the ATR guard" grep replaced — the reposition gates now show up under
   `ChaseAbortReason` (census 1 decl + 1 call + 6 pre-placement = 8).
2. Spec §3: the corrected residual derivation (Trail flow, uniform `manualTPval > 0` rule).
3. Spec §6: acceptance 4 marked automated (D3); the §6.5 grep note gains the raw-vs-census
   `emergencyFired` count (10 raw = 9 sites + 1 N1-era comment).
4. `spec-fee-comms-repoint.md` created (owner-tick pending; not scheduled by this review).
5. Roadmap: EV row marked implemented+reviewed; the micro-spec added to the backlog.

## 5. What remains

1. **Owner §6.3 runtime pass** (recipe: impl report §5) — knob 0.5% + near-target bridge act ⇒
   `Working entry cancelled (EV floor)` on the first reposition evaluation; knob 0 ⇒ chases to
   the ATR cap as today. **x64 rebuild first** (the runtime bin is pre-EV) and **re-check the
   window title for `— TESTNET`** — the rebuild clobbers the bin's `secrets.json`. Back up
   `orderapp-settings.json` (the run tightens geometry; FormClosing persists all 11 fields).
   Trade-placing steps: OWNER mouse clicks only (triple-placement WATCH).
2. ~~**Tooling row visual check** rides the same session (`grpTooling` +48px, ClientSize 856→904).~~
   **PASSED — owner screenshot, TESTNET, 2026-07-30.** The row renders as specified: caption
   `Min Net Move:`, unit `%`, default `0`, textbox on the same x as the four boxes above it, unit
   label in the same column, no overlap with the `SIZE` button (which ends at y=216; the row starts
   at y=226), `lblAtrNow` intact and fully legible on its new line
   (`ATR now: 30.51 (indicator) -> slip limit $18.31`), nothing clipped at the group or form edge,
   window fully on screen. Corroboration that the screenshot is a current build: `Comms.: 22` at
   index 63746.51 = the post-repoint 3.5 bps derivation (the retired constant would read 32).
   **Two findings the check produced — see §5a.**
3. Owner push (this review + fold-backs land on top of the five EV commits).
4. Owner tick on `spec-fee-comms-repoint.md` scheduling; relay the one-line fee note to the
   engine seat with the standing ack header.

## 5a. Findings from the Tooling visual check (2026-07-30)

The row itself is correct; both findings are about what the +48px did to its surroundings, and
both are implementer misses from the EV pass, not defects in the shipped behaviour.

1. **Stale comment CORRECTED in this commit.** `AutoTradeSettings.Designer.vb` carried, from the
   risk-sizing pass, "ClientSize stays 512x856 … the matching heights are deliberate". The EV pass
   changed `ClientSize` to 904 and did not update it, leaving a designer comment that stated a
   false number — exactly the class of stale fact a future seat greps and trusts. Reworded to
   record the actual history and the property below.

2. **The deliberate height match is BROKEN — owner decision, deliberately not fixed here.**
   `frmMainPageV2` is `1080x856`; `AutoTradeSettings` was `512x856`, an exact match the designer
   comment called deliberate, with `StickToHost` top-aligning the two. At 904 the settings window
   now overhangs the main form's bottom edge by **48px**, visible in the owner's screenshot. It is
   cosmetic — nothing clips, both windows fit the display, and the overhang is dead space below
   `lblAtrNow` — so it does not block anything. But the EV spec did not licence breaking a
   documented deliberate property, and the choice belongs to the owner:
   - **accept** the asymmetry (zero work, one comment already records it); or
   - **reclaim the 48px** inside the existing 856 — the Tooling group has ~30px of slack under
     `lblAtrNow` plus ~14px inside the group, so tightening the five row pitches from 48 to ~44
     would fit the new row without growing the form. That is a designer-only change, no logic.

   Recommend **accept** unless the owner wants the matched bottom edges back; raising it rather
   than silently keeping the growth.

   **OWNER RULED 2026-07-30: RECLAIM.** Done — the form is back to `512x856` and the host's bottom
   edge lines up again. **The recommendation above ("tighten the five row pitches 48 → ~44") was
   WRONG and is corrected here**: the knob rows are single-line `TextBox`es, whose height WinForms
   clamps to the font, so 42px at Calibri 14pt is not adjustable. Fitting five of them plus the
   26px readout inside 270 needs `4×pitch + gap + margin ≤ 172`, which fails even at pitch 42
   (boxes touching). Pitch can reclaim ~8px, never 48.

   What actually worked: pitch 48 → **46** (the value `grpTradeGates` already uses, so it is a
   proven spacing on this form, not a guess) **plus lifting `lblAtrNow` out of `grpTooling` onto
   the form**, into the strip below it. Final geometry: rows at 30/76/122/168/214 (last box ends
   256), `grpTooling` 482x**266** (10px margin), `btnRiskSize` 122..210 still spanning rows 3–4
   exactly, `lblAtrNow` at form-relative (29, 826), 460x26, ending 4px above the form edge,
   `ClientSize` **512x856**. `lblAtrNow` is a status readout rather than a knob, so a status line
   under the group is a defensible home for it — but it IS a change to the grouping and wants an
   eyeball on the next screenshot.

   **CONFIRMED by owner screenshot, TESTNET, 2026-07-30 — the eyeball above is closed.** The two
   windows' bottom edges line up again (both `856`, both top-aligned by `StickToHost`), which was
   the whole point of the reclaim. Five rows render at the tighter pitch with the value column and
   the unit column both still aligned; `SIZE` still spans the Risk/Trade and Max Size rows and
   clears the fifth row beneath it; `ATR now: 27.39 (indicator) -> slip limit $16.43` is fully
   legible on its new form-level line, nothing clipped at either the group or the window edge.
   (Readout self-consistent: 27.39 × the 0.6 ATRSlip multiplier = 16.43.) The relocated readout
   does now read as a status line detached from the group rather than as the group's last row —
   intended, and it looks deliberate rather than orphaned.

3. **Caption renamed `Min Net Move:` → `Min Net Profit:`** (owner, 2026-07-30) — **confirmed on the
   same screenshot**: the row reads `Min Net Profit:` / `0` / `%`, caption not clipped and not
   colliding with `SIZE`. **Display text only**, by instruction: `lblMinNetMoveCap`, `txtMinNetMove`, `minNetMovePctVal`, `MinNetMovePct`,
   `SetMinNetMovePct`, `SeedMinNetMoveFromHost`, the persisted `min_net_move_pct` key and the
   harness `AccessibleName` are all unchanged. The gate-config warning line now reads
   `min net profit '<text>'`, and the caption tooltip's one "net move" became "net profit"; the
   example-json comment and spec §4 record the split. **Standing note for future seats: the label
   and the identifiers deliberately disagree — grepping one spelling will miss the other.**
