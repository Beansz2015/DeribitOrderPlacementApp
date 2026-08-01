# Spec-back — N2 risk-sized bridge trades: five defects in `spec-risk-sized-bridge-trades.md`

> ## COORDINATOR RULING 2026-08-01: **ALL FIVE UPHELD. Proceed.**
>
> Each claim was re-verified against the code at `677a635` before ruling — none taken on the
> report's word. The spec is **amended in place**, so a future reader is not walked into the same
> traps. Rulings, in the report's order:
>
> | | Ruling |
> |---|---|
> | **§1** | **UPHELD.** `rawSize` stays bound to `SizeUsd`; the risk size enters as a separate `baseSize`; the `sizeUsdOverride` elision keeps comparing against the **box**. Confirmed at the site: `If(effectiveSize <> rawSize, effectiveSize, 0D)` with `If sizeUsdOverride > 0D Then amount = sizeUsdOverride`. **Addition:** the clamp check *and its log text* both take `baseSize` — it is the value actually clamped, and when disabled `baseSize = rawSize`, so the line stays character-for-character today's. |
> | **§2** | **UPHELD.** Mirror the button exactly: `maxUsd > 0` guard + step-floored cap. Confirmed both divergences (`max <= 0` ⇒ collapse to 10; `505` ⇒ off-step order, `-32602`). Parity is the anchor; `Math.Min` is a second, differently-behaved cap. |
> | **§3** | **UPHELD.** Fail-safe covers `dist <= 0` **and** `p.Entry <= 0` **and** `RiskPerTradeUsd <= 0`. Confirmed the levels gate is `StopLevel <= 0 OrElse Target <= 0` with `Entry` absent, and `ParsePayload` defaults it to `0D`. **The alternative is RULED AGAINST:** do not add `p.Entry <= 0D` to `refused: levels` — it changes a frozen disposition-token gate and would alter the soak stream. You were right not to propose it as the default. |
> | **§4** | **UPHELD. "Untouched" = BEHAVIOUR.** Repoint `ApplyRiskBasedSize` at the shared `(riskUsd, maxSizeUsd, refPrice, dist)` seam. Confirmed the button's distance is `If(manualSLval > 0D, Math.Abs(refPrice - manualSLval), triggerDistance)` — no stop price exists in offset mode, so an `(entry, stop)` seam could only be a second copy. Below-10 policy stays per-caller. The button's diff must be exactly two arithmetic lines → one call, guards and all three refusal messages untouched. |
> | **§5** | **UPHELD, and placement RULED: `SIGNAL BRIDGE`, right of Tiers, caption `Risk-size`.** Your measurement is corroborated by the era's own record — the EV reclaim already spent the only spare height in `grpTooling` (that is why `lblAtrNow` was lifted onto the form). Taking your recommendation, and on the merits rather than as a fallback: it is a bridge behaviour and belongs in the bridge group's checkbox column. Owner can veto at the screenshot check. |
> | **§6.1** | Acceptance 3 amended as you suggest, and a **new acceptance 3b added**: a LIVE-mode leg is now REQUIRED. Log-only structurally cannot catch the §1 class of defect, so proving it needs a real placement checked against the exchange, not the log. |
> | **§6.2** | Use the brief's filename, `docs/impl-report-risk-sized-bridge-trades.md`. Spec's Commits line corrected. |
> | **§6.3** | Fixture arithmetic **re-computed independently and adopted verbatim** — all six rows check out. Two regression pins added for defect §2 (`max = 505` ⇒ 500, and `max = 0` ⇒ no cap, NOT 10). |
>
> **Assessment of the escalation itself:** §1 is the find of the era so far. A literal implementation
> would have printed the risk size and placed the Amount box, on the default configuration, with
> Log-only mode structurally unable to reveal it — the soak would have looked perfect. That is the
> third time this era a plausible static derivation has been wrong, and the first time it was caught
> *before* any code was written rather than by a runtime acceptance. Raising it cost one document;
> not raising it would have cost a live mis-sized trade.

**For:** the owner + orchestrator. **From:** the N2 implementer seat (Opus HIGH), 2026-08-01.
**Status:** **no code changed. Nothing implemented.** Raised under the standing rule (H-4 §6 /
H-5 §4): spec defects escalate BEFORE implementing rather than being coded around.
**Baseline verified myself:** `git rev-parse HEAD` = `677a635`, 2 ahead of `origin/master`
(`53a2375`); `tools/checks/verify-gate.ps1` → **GATE PASSED, OrderCheck 153/153**.

> ⚠️ Anchors are at `677a635` and will drift. Locate by symbol.

§1 is the one I would most want ruled: it is a **silent live/log divergence** that a literal
reading of the spec produces, and it is invisible in exactly the mode (Log-only) the owner would
use to gain confidence before going live. §2–§4 are formula-parity and fail-safe defects. §5 is a
geometry fact the spec's fallback did not anticipate.

---

## 1. 🚨 §1's "replace the act-site's `rawSize = SizeUsd` base" silently disarms the size override

Spec §1: *"replace the act-site's `rawSize = SizeUsd` base with: … `effective = EffectiveSizeUsd(base, sessionMult)`"*.

Applied literally — `rawSize` becomes the risk-sized base — the act site at
`SignalBridge.vb:720`–`748` reads:

```vb
Dim rawSize As Decimal = <risk-sized base>          ' was: SizeUsd
Dim effectiveSize As Decimal = EffectiveSizeUsd(rawSize, sizeMult)
...
sizeUsdOverride:=If(effectiveSize <> rawSize, effectiveSize, 0D)     ' :748
```

That last line is the session-policy era's **override elision**, and it is written against the
assumption that `rawSize` **is the Amount box**: `0` means "use the box exactly as before"
(`frmMainPageV2.vb:734`–`736`, `:3567`). Once `rawSize` is the risk-sized base instead:

| session `size_mult` | `effectiveSize` vs `rawSize` | `sizeUsdOverride` | size actually placed |
|---|---|---|---|
| 1.0 (policy off, or a unity session) | **equal** (unity passes through) | **0** | **the Amount box** ❌ |
| < 1.0 | differ | `effectiveSize` | risk-sized ✓ |

**So on the default configuration — session policy OFF — the bridge would compute the risk size,
print it in `would-act … size <computed>`, and then place the Amount-box size.** Log-only mode
never places anything, so the soak stream would look *perfect*; only a live trade would reveal it.
This is precisely the "correct implementation of a wrong theory reads as convincing" class the
brief warns about, and acceptance 3 (a Log-only `would-act` check) **would pass while the live
path was wrong**.

**Fix (recommended):** do not overload `rawSize`. Keep it bound to the Amount box and introduce a
separate base:

```vb
Dim rawSize   As Decimal = SizeUsd                       ' unchanged - still the Amount box
Dim baseSize  As Decimal = <risk-sized base, or rawSize when disabled/fallback>
Dim sizeMult  As Decimal = PolicySizeMultFor(p)
Dim effectiveSize As Decimal = EffectiveSizeUsd(baseSize, sizeMult)
If EffectiveSizeWasClamped(baseSize, sizeMult) Then _log(...)
...
sizeUsdOverride:=If(effectiveSize <> rawSize, effectiveSize, 0D)     ' compare vs the BOX
```

Knob off ⇒ `baseSize = rawSize = SizeUsd` ⇒ every expression above is character-for-character
today's, including the clamp line's `raw size {rawSize}` text. **Please rule that §1 is amended to
this wording**, so the next reader of the spec is not walked into the same trap.

## 2. §1's cap loses two protections that `ApplyRiskBasedSize` has (formula parity)

Spec §1: `base = Math.Max(10D, Math.Min(riskSize, MaxSizeUsd))`.
The SIZE button (`frmMainPageV2.vb:296`): `If maxUsd > 0D AndAlso size > maxUsd Then size = Math.Floor(maxUsd / 10D) * 10D`.

`Math.Min` drops both halves of that line:

| case | SIZE button (today, runtime-verified) | spec §1 as written | consequence |
|---|---|---|---|
| `max_size_usd` = 0 or negative | guard fails ⇒ **no cap** | `Min(riskSize, 0)` = 0 ⇒ `Max(10, 0)` = **10** | every bridge trade silently collapses to contract minimum |
| `max_size_usd` = 505 (non-step) | cap floored ⇒ **500** | **505** | a non-multiple-of-10 order size; Deribit rejects `-32602` |

`AppUserSettings.MaxSizeUsd` defaults to 500 and `SetRiskSizingValues` ignores non-positive input,
so both cases need a hand-edited `orderapp-settings.json` — but that file *is* the documented way
to tune these keys (`AppUserSettings.vb:43`–`45`), and "the cap is off" is a reasonable thing for
the owner to intend by writing 0.

**Recommendation: mirror the button exactly** (`maxUsd > 0` guard + step-floored cap). Parity with
`ApplyRiskBasedSize` is the review anchor; `Math.Min` is a second, differently-behaved cap.

## 3. §1's fail-safe is too narrow — `entry` is NOT guarded upstream, and `risk <= 0` is unguarded

Spec §1 asserts, parenthetically: *"(both engine-authoritative; the levels guard has already
refused stop/target ≤ 0 before this code can run)"*, and provides a fail-safe for `dist <= 0` only.

**The levels guard does not cover `entry`.** `SignalBridge.vb:633`:

```vb
ElseIf p.StopLevel <= 0D OrElse p.Target <= 0D Then
    disposition = "refused: levels"
```

`p.Entry` is absent from that condition, and `ParsePayload` defaults a missing
`levels.<dir>.entry` to `0D` (`:857`). So a payload with a good stop/target and a missing or zero
`entry` reaches the act site, where:

```
dist     = Abs(0 - stop) = stop  > 0        ' the dist <= 0 fail-safe does NOT fire
riskSize = floor(risk * 0 / dist / 10) * 10 = 0
base     = Max(10, Min(0, max))             = 10
```

⇒ **a live trade at contract minimum, with no log line saying why.** `risk_per_trade_usd <= 0`
(which the SIZE button refuses outright, `frmMainPageV2.vb:290`–`293`) reaches the same place by
the same arithmetic.

**Recommendation:** extend §1's fail-safe to the full set — **`dist <= 0` OR `entry <= 0` OR
`riskUsd <= 0` ⇒ fall back to the raw Amount box + one yellow line**. That is §1's own stated
philosophy ("Never refuse a signal because sizing math hiccuped") applied to every input the
formula cannot use, and it keeps the bridge's guard set aligned with the button's.

*(Alternative, if the owner prefers the guard upstream: add `p.Entry <= 0D` to the `refused: levels`
condition. I did **not** propose this as the default — it changes a contract-token gate's
behaviour and would alter the soak's disposition stream, which is frozen. Flagging it as the
owner's call.)*

## 4. §4's seam signature makes shared-formula parity impossible, and collides with Do-not-touch

Spec §4 asks for `RiskSizedBase(riskUsd, maxSizeUsd, entry, stop)`; Do-not-touch says *"the manual
SIZE button (`ApplyRiskBasedSize`) is untouched"*; the review anchor is *"one formula, not a second
implementation that drifts"*. **All three cannot hold at once.**

The signature is the binding constraint: it takes a **stop level**, and the SIZE button has no stop
level. Its distance is `If(manualSLval > 0D, Math.Abs(refPrice - manualSLval), triggerDistance)`
(`frmMainPageV2.vb:282`) — in offset mode there is no stop *price* at all. So a seam keyed on
`(entry, stop)` can only ever be a **second copy** of the arithmetic, which fixtures can pin but
cannot bind: nothing stops the two copies drifting, which is the exact failure the anchor names.

A signature both callers can share:

```vb
' The step-floored, capped risk size. -1 = inputs cannot produce a size (caller's fail-safe).
Friend Shared Function RiskSizedBase(riskUsd As Decimal, maxSizeUsd As Decimal,
                                     refPrice As Decimal, dist As Decimal) As Decimal
```

with the **below-10 policy left to each caller**, because the two deliberately differ and both are
already ruled:

- SIZE button: result `< 10` ⇒ **refuse**, leave `txtAmount` alone (`:297`–`:300`);
- bridge: result `< 10` ⇒ **clamp to 10** (§1 `Math.Max`, matching `EffectiveSizeUsd`'s D3
  clamp-and-log ruling — H-4 §4.2).

Under this shape the button's diff is **two arithmetic lines → one call**, its three distinct
refusal messages and all its guards are untouched, and the `-1` arm is unreachable from it (its own
guards precede the call). Behaviour-identical, and the formula is genuinely singular.

**This needs an explicit ruling on what "untouched" means** — behaviour untouched (repoint it), or
text untouched (accept two copies). I recommend **behaviour untouched**; but I will not edit
`ApplyRiskBasedSize` on my own reading of a Do-not-touch line.

## 5. §2's geometry fallback does not exist — the form has no free row, and the caption does not fit

§2: *"if the Tooling group lacks clean space, the checkbox may go under the Risk/Max rows —
implementer's call within the pinned 512×856."*

I measured this rather than eyeballing it: a probe constructs the real `AutoTradeSettings`
(designer code, runtime DPI, no host, no network) and dumps live control bounds. Numbers below are
probe pixels; the form renders at 358×514 there (`AutoScaleMode.Font`, ×0.70 X / ×0.60 Y from the
designer's 512×856), so **compare them only with each other**.

- `grpTooling` T=334 B=**494**; `lblAtrNow` T=**496** B=512; form client height **514**.
  → **zero vertical slack**; a sixth row cannot be added inside 512×856.
- The row "under the Risk/Max rows" is **occupied** — it is the Min Net Profit row.
- Every remaining free space is a **mid-row gutter**:

| gutter | free | reads as |
|---|---|---|
| Tooling, ATR Length row (`lblAtrLenCap` R=108 → `txtAtrLength` L=231) | **123** | modifies *ATR Length* ✗ |
| Tooling, Min Net Profit row (R=131 → L=231) | **100** | modifies *Min Net Profit* ✗ |
| SIGNAL BRIDGE, right of `txtBridgeTiers` (R=226 → 330) | **104** | a bridge setting, in the ARM/Policy checkbox column ✓ |

And the spec's caption does not fit any of them, at the 12F style §2 asks for:

| caption | width | fits 123 | fits 104 | fits 100 |
|---|---|---|---|---|
| `Risk-size bridge trades` (spec's) | **174** | ✗ | ✗ | ✗ |
| `Risk-size bridge` | 128 | ✗ | ✗ | ✗ |
| `Risk-sized` | 90 | ✓ | ✓ | ✓ |
| `Risk-size` | 81 | ✓ | ✓ | ✓ |
| *(`Policy` = 64, `ARM AUTOTRADE` = 145, for calibration)* | | | | |

So a terse caption is forced — which is already house style (`Policy` carries the whole
session-policy feature, with the meaning in the tooltip).

**Recommendation: `Risk-size` in the SIGNAL BRIDGE group, right of the Tiers box.** It is a bridge
behaviour, it joins the right-hand checkbox column with `ARM AUTOTRADE` and `Policy`, and it is the
only placement that does not read as a modifier of an unrelated knob. This **deviates from §2's
named group** (Tooling), so it is the owner's call, not mine. Either way the geometry gets a
screenshot check before the item closes, per §2 and the WordWrap lesson.

## 6. Minor — no ruling needed, recorded so the review is not surprised

1. **Acceptance 3 says "Amount box irrelevant". It is not fully irrelevant:** gate 4.6's
   `refused: size` still reads the raw box (`:701`) and is on the Do-not-touch list, so a runtime
   test with the Amount box at **0** is refused *before* sizing runs. The owner's run must leave a
   non-zero box. Suggest amending the acceptance to "the Amount box no longer determines the size
   (it must still be non-zero to clear gate 4.6)".
2. **Impl-report filename:** the spec's Commits line says `docs/impl-report-risk-sized-bridge.md`;
   my brief says `docs/impl-report-risk-sized-bridge-trades.md`. I will use the brief's name.
3. **§4's fixture arithmetic, computed honestly** (the spec leaves this to the implementer). The
   spec's own example caps out, so it does not exercise the risk formula's result — a wider stop
   is needed for that:

| fixture | risk | entry | dist | max | `risk×entry/dist` | step-floored | after cap | note |
|---|---|---|---|---|---|---|---|---|
| spec's "typical" | 25 | 64735 | 54.62 | 500 | 29,629.71 | 29,620 | **500** | cap binds — same case as "cap binds" |
| uncapped | 25 | 64735 | 5000 | 500 | 323.675 | **320** | 320 | the real "typical" fixture |
| non-step floor | 25 | 64735 | 4000 | 500 | 404.59 | **400** | 400 | flooring is visible |
| floor binds | 0.01 | 64735 | 5000 | 500 | 0.129 | **0** | 0 → bridge clamps **10** | §2's below-10 split |
| non-step cap | 25 | 64735 | 2000 | 505 | 809.19 | 800 | button **500** / spec **505** | the §2 divergence |
| composed | — | — | — | — | `EffectiveSizeUsd(320, 0.5)` | | **160** | halves-then-floors, one chain |

## 7. What I need to proceed

| | ruling |
|---|---|
| **§1** | Amend §1's wording: `rawSize` stays bound to `SizeUsd`; the risk size enters as a separate `baseSize`, and the `sizeUsdOverride` elision keeps comparing against the **Amount box**. |
| **§2** | Cap = the button's `maxUsd > 0` guard + step-floored cap (recommended), or spec-literal `Math.Min`. |
| **§3** | Fail-safe covers `dist <= 0` **and** `entry <= 0` **and** `riskUsd <= 0` (recommended), or `dist <= 0` only. Optionally: add `p.Entry <= 0D` to `refused: levels` instead (changes a frozen gate token's behaviour — flagged, not recommended by me). |
| **§4** | "Untouched" = behaviour (repoint `ApplyRiskBasedSize` at a shared `(risk, max, refPrice, dist)` seam — recommended), or text (two copies of the formula). |
| **§5** | Checkbox placement + terse caption: SIGNAL BRIDGE right of Tiers (recommended), or a Tooling mid-row gutter, or grow the form past 856. |

Nothing is implemented and nothing is committed beyond this document. On the rulings I will execute
the spec's four commits as written (seam + fixtures · act-site integration behind the flag · Tooling
checkbox + persistence with seed-before-commit · impl report), gate per commit, and ship DISABLED.

Related: `spec-risk-sized-bridge-trades.md` (the spec), `spec-session-policy-gate.md` §4 (the
`size_mult` fold-in this builds on, and the origin of the `sizeUsdOverride` elision in §1),
`spec-risk-sizing-settings-ui.md` (the SIZE button's move to Tooling and the seed-before-commit
trap), `HANDOVER-4.md` §4.2 (unity-passthrough / sessionFactor-exactly-once ruling),
`HANDOVER-5.md` §3.1 (the four review anchors this item is built to).
