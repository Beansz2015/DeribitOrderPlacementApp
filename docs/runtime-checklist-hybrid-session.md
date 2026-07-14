# Runtime-session checklist — the hybrid loss-cap gate (compiled 2026-07-13)

**What this is:** the single remaining gate before pushing `44cd51e..de7d87b`, merged from the three specs' test plans into one ordered sheet (`spec-emergency-baseline-fix.md` §5 · `impl-report-emergency-baseline-hybrid.md` §6 · `spec-fill-reanchor-fix.md` §4 · `spec-sl-chase-v2.md` §5). Everything NOT listed in section A/B was already exercised in trades #47–67; only the `25cac5f` latch (post-dates #67) and the previously-unreachable manual-TP path are genuinely untested.

**Preconditions:** test sub-account under the VS debugger; `chkMarketStopLoss` ARMED with `txtMarketStopLoss` > 0 for A1/A2 (blank/0 disables the whole emergency). **Please jot the trade # beside each ticked item** — the docs have no runtime record past #67, and these numbers become the push evidence.

---

## A. MUST-RUN — the gate (never exercised)

### A1. The cap fires from the LATCHED anchor (§5 items 1 + 4 + 6 in one trade)
SHORT entry, small M.SL (5–20). Let the SL trigger, then let the market grind adversely while the chase keeps stepping.

- [x] Red `Triggered SL placed @ $F` (the flip; F is the trailed/live limit and may sit below top-of-book)
- [x] First orange `SL repositioned: $F → $S` — **S is the latched anchor** (in #67 the F→S step was 4.5)
- [x] Emergency fires when bestBid ≈ **S + M.SL**: red `Emergency Buy Market Order Executed.`
- [x] **Fire point is measured from S** — from F (= #67's too-early bug) or never-fires-on-a-grind (= #55's disabled-cap bug) is a **FAIL**
- [x] Cleanup (item 6 rides here): position flat, resting SL/TP cleaned up, **no second market reduce**

Trade #: **68 — PASSED 2026-07-14.** SHORT 10 @ 64007.5, M.SL 20. F = 64049.5, S (latch) = 64054.0 (step 4.5), cap = 64074.0, market fill 64077.5. **Discriminating evidence:** the last chase edit parked the SL at 64069.5 = F + 20 *exactly* — under a flip-price anchor the send-function's internal check would have market-closed instead of placing that edit; it went through, and the emergency fired later via the quote path (log signature: `Cancelled → Reduce-only MARKET → Emergency Buy Market Order Executed`, no further reposition). Anchor was therefore the latched S, per design. Cleanup clean: one reduce, flat, no `already_closed`, trade recorded.

### A2. A manual re-set DEFINES the cap; the chase pullback cannot undo it (§5 item 3)
In a triggered-SL trade, manually move the SL limit **in the RESTING (non-crossing) direction, by LESS than M.SL**:
- SHORT exit (buy limit): move it **DOWN, below the bid** (e.g. bid − 10 with M.SL 20). ⚠️ Direction matters: moving it UP crosses, and `post_only`/`reject_post_only:False` silently reprices a crossing edit back to the book — net effect nil (the #71 lesson). ⚠️ Gap < M.SL: dropping it ≥ M.SL below the bid fires the emergency INSTANTLY (preserved #44 behavior — correct, but it ends the test).
- LONG exit (sell limit): mirror — move it UP, above the ask, by < M.SL.
- Exchange UI or Edit T.S. both work for a non-crossing target (Edit T.S.'s `trigger_price` is moot post-trigger).

- [ ] Cyan `Manual SL edit: $X`
- [ ] Next tick: orange pullback to best-non-crossing — **expected** (P1 accepted 2026-07-08; the chase reference follows)
- [ ] BUT a subsequent adverse run fires at **X + M.SL**, not (pulled-back SL) + M.SL — the freeze held

Trade #: ______  *(Attempt #71, 2026-07-14: NOT exercised — the manual 64360 was a CROSSING buy, repriced by post_only back to bestBid 64299 = where the SL already sat; no exchange change, so no cyan (correct). The trade instead re-proved A1 via the SEND-path: cap fired at 64319.5 = latched S 64299.5 + 20 exactly, with the caller's phantom `SL repositioned → 64319.50` line after the emergency = that path's known signature. Bonus: backlog probe answered — `trigger_price` on an already-triggered SL is NOT hard-rejected (owner confirmed no red API ERROR); the edit is accepted and post_only-repriced.)*

*(Attempts #76 + #80, 2026-07-14: still NOT exercised. **Owner-corrected attribution: both were DERIBIT CHART DRAGS, not Edit T.S.** The drags never executed on the exchange — proven by the fill prices (64858 / 64859.5 = exactly the app-controlled levels; a landed drag would have rested and filled elsewhere, with a cyan echo). #76 is the clean case: the app sent ZERO edits post-flip, so the chase cannot have interfered — the drags died Deribit-side (suspect the chart-edit CONFIRM step; owner to check that UI setting). #80's ~1.5 s triggered phase means the drag likely landed on/after the fill. The duplicate flip line = unchanged-price echo reprinting through the `UpdateFlag=False` gate. No attempt has ever FAILED an assertion — all three were setup misses: #71 button-crossing-repriced, #76/#80 drags-not-executed.)*

**⚠️ STATUS 2026-07-14 — coordinator recommends DOWNGRADE to best-effort (owner to ratify):** the composite left unproven is only "manual echo → freeze survives the next reposition". Its halves are each proven: cyan+follow live in #52/#58 (discriminator unchanged since), anchor-write + fire-from-anchor live in #68/#71 (both fire paths). Worst case if the freeze failed, the cap falls back to A1 semantics (re-latch at top-of-book) — never naked. Best-effort recipe when a TRENDING trigger occurs: drag the limit on the DERIBIT UI (not Edit T.S.) below the bid by < M.SL → expect cyan → pullback → fire at X + M.SL.

### A3. Manual-TP path — reachable for the first time (§5 item 7 + fill-reanchor §4.3)
Enter a Manual TP (absolute), place a limit entry that CHASES before filling.

- [x] Placed TP on the exchange = the manual absolute value
- [x] At fill, `txtManualTP` resets to 0 — **expected one-shot consume, not a bug**
- [x] **NO** `TP re-anchored` line at all (manual trades never stage a re-anchor post-`326cbaf`); the TP still rests at the manual absolute
- [x] Control, no manual TP (ride-along): chased entry → cyan `TP re-anchored to fill $F: $T` **and** the TP display updates (the #49 fix)

Trade #: **80 — PASSED 2026-07-14.** Manual TP 64740; entry chased 64796.5→64780.5 (3 steps — a re-anchor WOULD have fired without the gate); TP placed at 64740 absolute (auto would be 64720.5); no re-anchor line; inputs zeroed at fill (screenshot). Control = #49 (historical). Related intended-semantics note: manual TP/SL inputs consume at FILL only — a slippage-guard abort deliberately leaves them set (retry-friendly); clear-on-abort would be an ergonomics Phase A preference.

---

## B. RIDE-ALONGS — confirm opportunistically, no dedicated trades

- [x] B1 (§5.2): a maker SL fill BELOW the cap → no emergency (position closes maker)   Trade #: **76 + 80** (M.SL 70 armed both; #76 filled at the flip price with zero repositions, #80 filled maker at 64859.5 after two chases — no emergency either time)
- [ ] B2 (§5.5): M.SL blank/0 → pure chase, emergency never fires   Trade #: ______ *(best-effort; the `marketStopLossChecked AndAlso threshold > 0` gate is triple-reviewed)*
- [x] B3 (hybrid §6.3): SL triggers already at top-of-book → the first reposition may nudge the anchor one tick — confirm acceptable   Trade #: **76** (no-reposition variant: anchor stays flip, maker fill, benign) **+ 80** (nudge variant: flip 64856.5 → first-repo latch 64859, a 2.5 nudge — benign, accepted)

---

## C. Documented properties — do NOT read these as failures

- **PRE-trigger manual moves are SILENT by design** (owner hit this in #68): the cyan `Manual SL edit` detection exists only POST-trigger (the P1 discriminator on the triggered SL's limit). Pre-trigger, the untriggered-echo branch silently mirrors `trigger_price` into `StopLossTriggerOriginal` (rule 1) — the display updates, no log line. A2 requires the edit AFTER the `Triggered SL placed` state.
- **Pre-settle window** (flip → first reposition, ≤ ~333 ms): the cap measures from F. A violent gap inside that window fires from F — conservative, bounded, accepted by design.
- **Near-book manual moves** within ±0.25 of a just-commanded price may not log cyan and aren't followed (§6(a) accepted; the wide re-sets that matter to the cap always detect).
- **Post-nuclear-cancel warning repetition** (`Placed price = 0 while an order context is active…`) while a position sits uncovered: deliberate hazard signal (§6(c)).
- **Edit T.S. post-trigger is a four-trap minefield (backlog: make it post-trigger-aware):** (1) you enter a TRIGGER value but the LIMIT is what acts, derived as trigger ± `txtStopLoss` (+30 short / −30 long) — to place the limit at X on a short, you must enter X − 30; (2) `trigger_price` is moot on a triggered SL (accepted, ignored — probe closed #71/#76); (3) a crossing limit is silently repriced to the book by `post_only`/`reject_post_only:False`; (4) `Updated T.S. to:` prints optimistically after the send regardless of outcome. Trust the echo-driven lines (cyan / flip / reposition), never the button's own line. **Post-trigger manual moves: use the Deribit UI and drag the LIMIT.**
- **Duplicate `Triggered SL placed` lines** — the flip-line gate (`UpdateFlag = False`) is weak against Deribit's duplicate/late echoes; two-at-flip (and a re-print on any unchanged-price echo) is cosmetic. Known, pre-existing, hands-off region.
- **After a send-path emergency fire, one `SL repositioned → $X` line prints AFTER the emergency lines** (the caller's bookkeeping is blind to the internal fire; X = the cap price). Accepted F1-class cosmetic; the stale reference write is inert and self-heals at the next placement. Observed live in #71.
- **Cancel-all removes the cap with the SL context** (owner ruling 2026-07-13: left as-spec — nuclear cancel resets `SLTriggered`/baseline/latch). If you cancel-all and keep the position open, the M.SL emergency is gone with it: close manually.

---

## D. Covered in #47–67 — no re-test required

sl-chase-v2 §5.1/.2 (bid+0.5 / ask−0.5 stepping, maker exits), §5.3 pullback (P1 decision CLOSED), §5.5 cancel regression (#58), §5.6 cadence, §5.7 untouched loops · fill-reanchor §4.1/.2/.5 (#47/#49) and §4.6 (7/7 docs grep re-verified 2026-07-13) · post_only `-32602` gone on far chases (#53/#56 fix, clean through #67) · pre-hybrid emergency machinery: fire + cleanup proven by #67/#44.

---

**PASS = A1–A3 ticked → push `44cd51e..HEAD` → fire `handoff-autotrade-tiein.md` at the implementer.**
**Any FAIL: stop, note the trade #, bring me the log lines — do not push.**

**GATE STATE — ✅ SATISFIED, owner ratified 2026-07-14.** A1 ✅✅ (#68 quote-path + #71 send-path) · A3 ✅ (#80) · B1 ✅ (#76/#80) · B3 ✅ (#76/#80) · A2 downgraded to best-effort by owner ratification (three setup misses, zero assertion failures; the chart-drag correction STRENGTHENS the call — staging a mid-chase manual move is rarer live than assumed, and worst-case-if-broken = A1 semantics, double-proven). B2 best-effort. **→ PUSH `44cd51e..HEAD`, then fire `handoff-autotrade-tiein.md`.** Post-push backlog from this session: Edit T.S. post-trigger rework (4-trap catalog above); ergonomics Item G LANDED in `spec-execution-ergonomics.md` (guarded placed-SL display clear on entry-abort, owner-requested); owner to check Deribit's chart-edit confirm setting (the likely #76 drag killer). A2 best-effort recipe stands: trending trigger → Deribit UI drag of the LIMIT below bid by < M.SL → cyan → fire at X + M.SL.
