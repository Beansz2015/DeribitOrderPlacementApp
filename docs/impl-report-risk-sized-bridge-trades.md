# Impl report — N2 risk-sized bridge trades (`docs/spec-risk-sized-bridge-trades.md`)

**Implementer seat:** Opus HIGH, fresh conversation, 2026-08-01.
**Commits:** `43534b7` (spec-back, no code) · `6850c7a` (formula seam + fixtures + the SIZE-button
repoint) · `a1262f0` (act-site integration behind the flag) · `7bc8daa` (SIGNAL BRIDGE checkbox +
persistence) · this report.
**Base:** `677a635`. **Gate at every commit:** GATE PASSED. **OrderCheck: 153 → 173.**
**Nothing pushed** (owner is the only pusher). **No trade placed, no bridge armed.**

**Ships DISABLED.** Absent `risk_size_bridge_trades` ⇒ `False` ⇒ the act path is what it was at
`677a635`.

---

## 0. The spec was amended before any code was written

Five defects were raised in `spec-back-risk-sized-bridge-defects.md` and **all five upheld**; the
spec was amended in place and this report implements the amended text. The one that shaped the
code most is §1, and it is worth restating because it is the reason the act site looks the way it
does:

> Applying the original §1 literally — *"replace the act-site's `rawSize = SizeUsd` base"* —
> re-points `rawSize` at the risk-sized base. The `sizeUsdOverride` elision compares against
> `rawSize` and `0` there means *"read the Amount box"*. At unity mult — **session policy off, the
> default** — the two become equal, the override elides to `0`, and **the Amount box is placed while
> the log prints the risk size.** Log-only places nothing, so the soak stream looks perfect;
> acceptance 3 would have passed on a broken build.

That is why `rawSize` is still `SizeUsd` and the risk size travels as `baseSize`.

## 1. What shipped

### §4 — the one formula (`SignalBridge.RiskSizedBase`)

```vb
Friend Shared Function RiskSizedBase(riskUsd, maxSizeUsd, refPrice, dist) As Decimal
    If riskUsd <= 0D OrElse refPrice <= 0D OrElse dist <= 0D Then Return -1D
    Dim size As Decimal = Math.Floor(riskUsd * refPrice / dist / 10D) * 10D
    If maxSizeUsd > 0D AndAlso size > maxSizeUsd Then size = Math.Floor(maxSizeUsd / 10D) * 10D
    Return size
End Function
```

The body is `ApplyRiskBasedSize`'s two arithmetic lines **verbatim**, including the expression's
exact association (`riskUsd * refPrice / dist / 10D`) — Decimal division rounds, so the parenthesis
placement is not cosmetic. `ApplyRiskBasedSize` now calls it: two lines → one call, guards and all
three refusal messages untouched.

`(refPrice, dist)` rather than the spec's original `(entry, stop)`: the button has no stop **price**
— in offset mode its distance *is* the trigger distance — so an `(entry, stop)` seam could only
have been a second copy of the formula. The signature is what makes "one formula" true rather than
merely asserted.

The cap mirrors the button rather than `Math.Min`, which lost both halves of it (`max <= 0` meaning
"no cap" vs. collapsing to the contract minimum; a non-step cap emitting an off-step order size the
exchange rejects `-32602`). Both are pinned as regression fixtures.

**Below-10 stays with each caller** and the two deliberately differ: the button **refuses** and
leaves `txtAmount` alone; the bridge **clamps up to 10** (the D3 clamp-and-log ruling). The seam
takes no position on it.

### §1 — the act site

```vb
Dim rawSize As Decimal = SizeUsd                 ' the Amount box - UNCHANGED
Dim baseSize As Decimal = rawSize
If RiskSizeBridgeTrades Then baseSize = RiskSizedBaseForPayload(p, rawSize)
Dim sizeMult As Decimal = PolicySizeMultFor(p)
Dim effectiveSize As Decimal = EffectiveSizeUsd(baseSize, sizeMult)
If EffectiveSizeWasClamped(baseSize, sizeMult) Then _log($"... (raw size {baseSize})", Yellow)
...
sizeUsdOverride:=If(effectiveSize <> rawSize, effectiveSize, 0D)   ' vs the BOX - unchanged
```

`RiskSizedBaseForPayload` owns this caller's fail-safe and logging; `RiskSizedOrFallback` is the
pure decision underneath it (sentinel ⇒ the raw box **un-floored**, per the unity-passthrough
ruling; otherwise clamped up to 10).

**Fail-safe covers `dist <= 0`, `p.Entry <= 0`, `RiskPerTradeUsd <= 0`** — each falls back to the
Amount box with one yellow line, never a refusal. `entry` is the one that mattered: gate 4.4 reads
`StopLevel`/`Target` only, and `ParsePayload` defaults a missing entry to `0`, so a payload with
good stop/target and no entry would otherwise have computed `riskSize = 0` and placed a **live trade
at the contract minimum with nothing in the log to say why**. Guarding it at 4.4 instead was ruled
against — it would alter a frozen disposition token.

Clamp logging: one yellow line, whichever bound. The floor message wins when both do (the size is
10 either way). "Did the cap bind" is answered by calling the same seam with the cap off (`0` = no
cap) rather than by a second copy of the arithmetic.

### §2 — config + UI

`AppUserSettings.RiskSizeBridgeTrades` / key `risk_size_bridge_trades`, on item A's atomic save
path; host mirror + `SetRiskSizeBridgeTrades`; `AutoTradeSettings._riskSizeBridgeTrades` on the
TiersCsv pattern; `chkRiskSizeBridge` ("Risk-size", 12F) in the SIGNAL BRIDGE group right of the
Tiers box. Seed-before-commit is the standing trap's **sixth** application — and I corrected the EV
seed's now-stale *"N2's is still queued"* parenthetical in the same pass.

## 2. The four review anchors — and how each was established

The brief asked me not to hand-verify by reasoning alone. Where I could measure, I measured.

| Anchor | How established | Result |
|---|---|---|
| **Formula parity with `ApplyRiskBasedSize`** | Structural (one function, no copy) **plus a measurement**: a scratchpad probe evaluated the two lines *as they stood at `677a635`* against the seam over a dense grid of risk × max × price × dist — including `max = 0`, negative max, non-step max, sub-step results, fractional prices. | **3024 combinations, 0 mismatches.** Guard arms checked separately. |
| **sessionFactor applied EXACTLY once** | There is exactly one `EffectiveSizeUsd` call site in the act path (grep below), and the risk base is computed *before* it, never multiplied. Fixture-pinned end to end: `320 × 0.5 = 160`; a second application would give 80. | Fixtures + single call site |
| **Floored to the contract step, min-10 clamp semantics, unity passes through UNTOUCHED** | Fixtures on the seam (step-floor, non-step cap floored, sub-step ⇒ 0) and on `RiskSizedOrFallback` (0 ⇒ 10; sentinel ⇒ the raw box **not** floored). The pre-existing 8 `EffectiveSizeUsd` unity fixtures are unchanged and still pass. | 20 fixtures |
| **Knob-off byte-identity** | **Structural, and deliberately so:** the only new statement on the disabled path is `If RiskSizeBridgeTrades Then`, so nothing is computed. `baseSize = rawSize` when off, which makes every downstream expression — including the clamp line's text and the override comparison — character-for-character the pre-N2 one. Commit `a1262f0` shipped the integration with **nothing able to write the flag**, so the feature was provably unreachable at that commit. | See §3 for what this does *not* prove |

## 3. What I did NOT establish — the runtime legs are genuinely outstanding

I want to be exact about the limits of the above, because the era's lesson is that a convincing
static story is not evidence.

1. **Acceptance 2 (disabled parity) is argued structurally, not observed.** I did not run the app
   and diff a disposition stream against HEAD. The argument is strong (`baseSize = rawSize` makes
   the expressions textually identical) but it is an argument.
2. **Acceptance 3 / 3b (testnet) are not run** — implementer seats place no trades. **3b matters
   most:** it is the leg that would have caught the §1 defect, and by construction Log-only cannot.
   It needs one **Live** act with the checkbox ON and session policy OFF (unity — the default), with
   the placed size verified **against the exchange fill/position**, not against the log line.
   ⚠ The Amount box must be **non-zero**: gate 4.6 still reads the raw box and refuses before
   sizing ever runs.
3. **Acceptance 4's UI restart round-trip is not run.** The *file* layer is verified — I
   round-tripped the real `AppUserSettings.Save`/`Load` in an isolated directory: `true/false/true`
   all survive, an absent key reads `False`, and neighbouring keys are not clobbered. The
   seed-before-commit path through the form is still only reasoned about.
4. **The §2 screenshot check is owed.** The probe gives live bounds (checkbox at 238..319 against
   `txtBridgeTiers` ending 226 and the group's proven right edge 330 — 12px and 11px clear,
   vertically centred on the Tiers row), but bounds cannot show wrap or clip. The WordWrap lesson
   says only a screenshot can.
   ⚠ Whoever runs it: `FormClosing` persists all 11 geometry fields, and now this key too — back up
   `orderapp-settings.json`, and verify the restore.

## 4. Censuses + acceptance-5 greps (re-run at final HEAD, `frmMainPageV2.vb`-scoped)

| token | expected | got |
|---|---|---|
| `emergencyFired` | 10 (9 code + 1 comment) | **10** ✓ |
| `IsATRSlippageExcessive` | 8 | **8** ✓ |
| `NextSlBackoff` | 2 | **2** ✓ |
| `TakerFeeRate` | 0 | **0** ✓ |
| `RecordCommandedSLPrice` | 3 (ONE recording site) | **3** ✓ |
| `slUpdateFailures = 0` | exactly 1, at the echo branch | **1** ✓ |
| `txtAmount.Text =` writers | 3, unchanged | **3** ✓ (lines shifted only) |
| `refused: size` | 2 (1 code + 1 comment) | **2** ✓ |
| bare `SizeUsd` | decl + gate read + comment + act-site bind | **4, same structure** ✓ |
| `EffectiveSizeUsd` call sites in the act path | 1 | **1** ✓ |

⚠ **Grep trap for the reviewer:** a bare `grep -c SizeUsd` on `SignalBridge.vb` returns 11 and looks
like drift — it is matching `EffectiveSizeUsd`, `MaxSizeUsd`, `maxSizeUsd` and `rawSizeUsd`. The
`SizeUsd` property itself is untouched.

## 5. Notes for the reviewer

1. **The probe is in the scratchpad, not the repo** — deliberately. It reaches the `Friend` seams by
   setting `<AssemblyName>OrderCheck</AssemblyName>` to match the existing `InternalsVisibleTo`; no
   repo file was changed to accommodate it. If you want the parity sweep as a permanent gate
   fixture, that is a reasonable ask, but it would mean a *second* copy of the formula living in the
   harness forever — which is the thing this item exists to avoid. I left it out on those grounds.
2. **`RiskSizedOrFallback` returns the raw box UN-FLOORED** on the fail-safe path. That is
   intentional and follows the unity-passthrough ruling: the box is the trader's typed value, and
   flooring it would resize a non-step Amount (25 → 20) the moment a bad-entry payload arrived.
3. **`RiskPerTradeUsd`/`MaxSizeUsd` are read off the host from a bridge thread.** Same accepted
   Decimal torn-read class as `LastSignalAtr`; they change only when the owner edits a Tooling box.
   Noted rather than fixed, consistent with the file's existing stance.
4. **The `-1` sentinel is unreachable from the SIZE button** — its own three guards Return first.
   Pinned by fixtures anyway, and stated in the comment so a future edit to those guards does not
   silently open the arm.
5. **`chkRiskSizeBridge` got `TabIndex = 7`** rather than renumbering `chkSessionPolicyOn` (5) and
   `txtSessionPolicy` (6) to slot it after the Tiers box. Tab order therefore reaches it last in
   the group. Say the word if you would rather I renumber.

Related: `spec-risk-sized-bridge-trades.md` (amended 2026-08-01),
`spec-back-risk-sized-bridge-defects.md` (the five defects + the ruling block),
`spec-session-policy-gate.md` §4 (the `size_mult` fold this builds on, and the origin of the
`sizeUsdOverride` elision), `spec-risk-sizing-settings-ui.md` (the SIZE button and the
seed-before-commit trap), `HANDOVER-4.md` §4.2 (unity-passthrough / sessionFactor-exactly-once).
