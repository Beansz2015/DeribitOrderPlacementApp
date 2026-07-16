# Spec — tick-size rounding at the three fractional-capable price derivations

**Date:** 2026-07-17. **Base:** `987992f` (verify tree clean; anchor by symbol — line refs drift).
**Origin:** the harness testnet pass's `-32602 "must conform to tick size"` finding (`impl-report-ui-test-harness.md` §8), triaged and ESCALATED in `review-ui-test-harness.md`: engine levels are ATR-derived fractionals (contract §3 example: stop `59062.1`, target `59095.1`), so the FIRST live bridge trade would reject at placement — the log-only soak never touches placement, which is why this stayed invisible. **This fix GATES the live-at-min-size step.**
**Owner ruling 2026-07-17:** rounding rule = **NEAREST tick** (max deviation 0.25 — economically nil at ATR-scale distances; conservative-directional rounding rejected as asymmetric complexity).
**Recommended implementer:** Fable high while the window lasts (~Jul 19), else Opus high — one commit + fixtures; it touches the fill-reanchor consume site in the hot file.
**Ground rules:** standing — build 0/0 Debug + Release (via the verify gate), local commit, never push, impl report may be a short addendum to this doc + the review trail.

---

## 1. The helper (one function, fixture-pinned)

In `frmMainPageV2`, beside `Const ChaseTickUSD As Decimal = 0.5D` (`:180` — the tick is an EXCHANGE property and this form already owns it):

```vb
' Exchange tick grid (BTC-PERPETUAL = 0.5). Engine levels and average_price fills are the two
' fractional price sources in this app; every exchange-bound price derived from them must land
' on the grid or Deribit rejects -32602 (harness finding 2026-07-17, testnet fill 64066.83).
' NEAREST tick (owner ruling): midpoints round away from zero for determinism.
Friend Shared Function RoundToTick(price As Decimal) As Decimal
    Return Math.Round(price * 2D, MidpointRounding.AwayFromZero) / 2D
End Function
```

## 2. The three call sites (the complete fractional-capable set)

Closure argument first: every other exchange-bound price derives from bid/ask quotes (always on-tick), user-typed inputs plus integer offsets, or preserves an exchange-echoed price — only **engine levels** (`p.Target`, `p.StopLevel`) and **`average_price` fills** (`pendingReanchorFill`) can be fractional. Three sites consume them:

1. **Fill-reanchor consume site** (`frmMainPageV2` open-`TakeLimitProfit` echo staging block, `:2406-2409`): wrap the whole derivation —
   `Dim newTP As Decimal = RoundToTick(If(manualTPval > 0D, manualTPval, If(TradeMode, pendingReanchorFill + takeProfitOffset, pendingReanchorFill - takeProfitOffset)))`
   (Rounding the full expression is idempotent for on-tick values; the `manualTPval` branch is defensively covered even though manual-TP trades never stage a re-anchor post-`326cbaf`. The skip-if-equal check below it now compares an on-tick target — unchanged logic.)
2. **`SignalBridge.DeriveManualSl`** (`:824-826`): `Return RoundToTick(If(isLong, stopLevel - offset, stopLevel + offset))` — VB resolves the Friend Shared via `frmMainPageV2.RoundToTick`. **Documented consequence:** the derived trigger (`manualSL ± stopLossOffset`, integer offset) stays on-tick and lands within 0.25 of the engine's stop instead of exactly on it — update the function's header comment ("lands exactly on") accordingly.
3. **Bridge act path** (`SignalBridge` `:682`): `_host.SetTradeTargets(manualTP:=frmMainPageV2.RoundToTick(p.Target), manualSL:=manualSl)`.

**Deliberately NOT changed:** the `would-act` log line keeps the engine-RAW levels (it reports the signal; rounding is placement mechanics — and the disposition format is soak-frozen). The manual edit buttons' user-typed inputs (pre-existing, loud exchange rejection, housekeeping candidate). No contract change — R2's "as-is" is satisfied to the exchange grid (execution mechanics, R1's consumer domain); one informational line goes into the contract at its next coordinated touch, never a gate.

## 3. OrderCheck fixtures (same commit, ~8)

- `RoundToTick`: the §8 case `64126.83 → 64127.0`; contract-example fractionals `59062.1 → 59062.0`, `59095.1 → 59095.0`; midpoints `x.25 → x.5` and `x.75 → x+1.0` (away-from-zero); on-tick passthrough `64126.5 → 64126.5`, `64127.0 → 64127.0`.
- Composition: `DeriveManualSl` on the contract-§3 stops is on-tick both directions, and `|result ∓ offset − stop| ≤ 0.25` (the trigger-near-stop identity).

## 4. Verification (the harness earns its keep)

Implementer: verify gate (`GATE PASSED` — both builds + all fixtures). Runtime, testnet via the harness: replay the §8 scenario — `click-PLACES-ORDER` a min-size entry that fills at a fractional average → `read-log` asserts the cyan `TP re-anchored to fill` line and **no** `-32602`; then a `write-payload` actionable with the contract-§3 fractional levels in **Live** mode... **NO — live placement stays interlock-gated; do NOT drive Live mode.** Instead: fixtures pin the bridge derivations; the placement-path proof is the fill-reanchor replay above (same rejection class, same fix). The bridge-live proof happens at the owner's live-at-min-size step per the rollout ladder.

## 5. Sequencing

Lands now (the owner holds the app on testnet until this is in). After it: the owner's live-regression session (§9.7 + the harness review's open regression item + restart the VerdictEngine), then the queue resumes — ergonomics Phase A during the soak; live-at-min-size stays gated on the engine geometry pass AND this fix.

---

## 6. Implementation addendum (2026-07-17, implementer seat — serves as the impl report)

**Implemented exactly as §1–§3, one commit, base `c3f21ad`.** Helper beside `ChaseTickUSD`; all three sites wrapped (fill-reanchor whole-expression, `DeriveManualSl` return, bridge `manualTP`); would-act line untouched. One deliberate extension: the act-path comment block above the `SetTradeTargets` call (`SignalBridge` §"all gates green") ALSO claimed "the TRIGGER lands exactly on the engine's stop" — updated alongside the mandated `DeriveManualSl` header (same falsehood, two homes).

**Fixtures:** 11, not ~8 (the §3 list enumerated to 11: 7 `RoundToTick` + 4 composition — on-tick and trigger-near-stop asserted separately per direction). **Verify gate: GATE PASSED, OrderCheck `OK 36/36`** (was 25/25), Release-sln + Debug-vbproj + OrderCheck builds all 0/0.

**Runtime proof (testnet, harness scripts, §4's replay — Live mode never driven):**

- The §8 scenario needs a fill whose derived TP is off-tick. A resting maker limit always fills at its own on-tick price, so the natural fractional `average_price` only appears on crossed-book taker sweeps (luck). Deterministic instrument used instead: **set a fractional TP offset (`txtTakeProfit` = 200.33) AFTER placement, BEFORE fill** — trade-input mirrors sync on TextChanged, placement had used the on-tick 200, and the consume-site derivation `fill + offset` then goes off-tick regardless of the fill price. Same site, same rejection class, pre-fix a guaranteed −32602.
- **Round 1 (edit-path smoke, LONG 500):** entry chased 64152 → 64165.5, filled at the chased price ⇒ re-anchor staged (fill ≠ anchor) ⇒ **`TP re-anchored to fill $64165.50: $64225.50`** — the cyan line, edit accepted.
- **Round 2 (the proof, min-size 10):** offsets 200/200, placed 64172, offset then set to 200.33; 7 chase repositions to 64181 (drift 9 < the 30 bound — the chase's own full-bracket re-derivation, an out-of-scope path, never fired); the fill came back **fractional `average_price` 64181.14 — the natural §8 class materialized on top of the instrument** ⇒ newTP raw 64381.47 → **`TP re-anchored to fill $64181.14: $64381.50`** — rounded on-tick, edit **accepted**. `read-log` full-session grep for `32602|tick size|Error in Reanchor`: **zero hits**.
- Teardown: scratch closes both rounds (testnet trades #2–#5 in the DB); `stop-app` clean.

**Owner should know:** local `secrets.json` was found at `Environment: "live"` (memory said the app was held on testnet) — flipped to `testnet` for this run only and **restored to `"live"` verbatim after**; the testnet key block is intact. Harness nit for the backlog: `set-textbox` substring matching cannot target `txtTrigger` (`txtTriggerOffset` matches first in tree order); the run used the default trigger instead.
