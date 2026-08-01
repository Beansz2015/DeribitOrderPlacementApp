# Spec — N2: risk-sized bridge trades (ships DISABLED; owner enables at normal size)

> **AMENDED IN PLACE 2026-08-01 — all five defects in `spec-back-risk-sized-bridge-defects.md`
> UPHELD after the coordinator re-verified each against the code.** The implementer raised them
> before writing any code, which is the standing rule working: §1 as originally written would have
> shipped a **silent live/log divergence** — the size printed in the log and the size placed on the
> exchange differing on the DEFAULT configuration, invisible in Log-only mode, so the soak stream
> would have looked perfect. The amendments are folded into the sections below; the ruling and the
> evidence are in the spec-back's header block. Sections not marked AMENDED are unchanged.

**Origin:** ROADMAP-2026-08 §3 N2 — the principled endpoint of the size ladder: each bridge trade
sized by risk over the ENGINE's own stop distance instead of the flat Amount box. Anticipated by
`spec-session-policy-gate.md` §4 ("when the stop-distance formula becomes the bridge's size
source, the session mult folds in as its sessionFactor — ONE formula, never stacked").
**Sequencing note:** implementation may land NOW because it ships OFF (the disabled path is
byte-identical); the owner flips it only once fixed-size laddering has proven out.

**Recommended implementer:** **Opus HIGH, fresh conversation.** Touches the bridge act path.
**Target:** `SignalBridge.vb` (act/would-act site), `AutoTradeSettings.vb` + `.Designer.vb`
(one checkbox), `AppUserSettings.vb` (one key), `tools/OrderCheck` (fixtures).
**Ground rules:** standing (gate per commit, never push, one commit per section, impl report).
**Do-not-touch:** the §4 gate chain order/tokens; `refused: size` still reads the RAW Amount-box
`SizeUsd`; `txtAmount` is never written; `EffectiveSizeUsd`/`EffectiveSizeWasClamped` keep their
existing fixture-pinned semantics; the manual SIZE button (`ApplyRiskBasedSize`) is untouched.

## §1 — The formula (ONE chain, applied once, at the act/would-act site only) — **AMENDED**

**Do NOT overload `rawSize`.** It is bound to the Amount box and the `sizeUsdOverride` elision at
the act site is written against that: `If(effectiveSize <> rawSize, effectiveSize, 0D)`, where `0`
means *"read the Amount box exactly as before"* (`PlaceAutomatedOrder`:
`If sizeUsdOverride > 0D Then amount = sizeUsdOverride`). Rebinding `rawSize` to the risk size makes
the two equal at unity — i.e. **whenever session policy is off, the default** — so the override
elides to 0 and the **Amount box is placed while the log prints the risk size**. Live-only, silent,
and a Log-only acceptance would pass over it.

Introduce a separate base instead:

```vb
Dim rawSize   As Decimal = SizeUsd                  ' UNCHANGED - still the Amount box
Dim baseSize  As Decimal = <risk-sized base, else rawSize when disabled or fail-safe>
Dim sizeMult  As Decimal = PolicySizeMultFor(p)
Dim effectiveSize As Decimal = EffectiveSizeUsd(baseSize, sizeMult)
If EffectiveSizeWasClamped(baseSize, sizeMult) Then _log($"... (raw size {baseSize})", Yellow)
...
sizeUsdOverride:=If(effectiveSize <> rawSize, effectiveSize, 0D)   ' compare against the BOX
```

The clamp check and its log text both take **`baseSize`** — it is the value actually clamped, and
when disabled `baseSize = rawSize`, so the line stays character-for-character today's.

Risk size itself:

```
dist      = Abs(p.Entry - p.StopLevel)                     ' the ENGINE's own geometry
riskSize  = Math.Floor((RiskPerTradeUsd * p.Entry / dist) / 10D) * 10D
base      = riskSize capped by MaxSizeUsd EXACTLY as the button caps  (see below)
baseSize  = Math.Max(10D, base)                            ' bridge clamps up; the button refuses
```

- **Formula parity is the point:** `risk × ref ÷ dist`, step-floor, `max_size_usd` cap — the exact
  arithmetic the owner runtime-verified in the SIZE button (`ApplyRiskBasedSize`).
- **The cap must mirror the button, not `Math.Min`** (defect §2, upheld). The button is
  `If maxUsd > 0D AndAlso size > maxUsd Then size = Math.Floor(maxUsd / 10D) * 10D`. `Math.Min`
  loses **both** halves: with `max_size_usd <= 0` (a legitimate way to mean "no cap" in the
  hand-edited settings file) `Min` yields 0 and every bridge trade collapses to the contract
  minimum; with a non-step cap like 505 it emits 505, which Deribit rejects `-32602`.
- **Fail-safe — AMENDED, covers every input the formula cannot use** (defect §3, upheld):
  **`dist <= 0` OR `p.Entry <= 0` OR `RiskPerTradeUsd <= 0`** ⇒ fall back to the raw Amount box +
  one yellow line. `p.Entry` is **not** guarded upstream — the levels gate is
  `StopLevel <= 0 OrElse Target <= 0` and `ParsePayload` defaults a missing entry to `0D` — so a
  payload with good stop/target and no entry otherwise reaches the act site, computes
  `riskSize = 0`, clamps to 10, and places a live trade at the contract minimum with nothing in
  the log to say why. `RiskPerTradeUsd <= 0` reaches the same place by the same arithmetic and the
  button refuses it outright.
  **RULED AGAINST (do not implement):** adding `p.Entry <= 0D` to the `refused: levels` condition.
  It changes a frozen disposition-token gate's behaviour and would alter the soak stream. The
  fail-safe is app-side and touches no contract token.
- **Clamp logging:** one yellow line when the cap or the 10-floor binds (mirrors the session-mult
  clamp line style), so undersized-risk sessions are visible.
- Disabled (default): the act site is **byte-identical to today** — `rawSize = SizeUsd`, session
  mult applied exactly as now. Structure the code so the disabled path does not even compute.

## §2 — Config + UI

- `AppUserSettings`: `RiskSizeBridgeTrades As Boolean = False`, key `risk_size_bridge_trades`
  (absent ⇒ False). Persists on item A's atomic path.
- **AMENDED — placement RULED: the `SIGNAL BRIDGE` group, right of `txtBridgeTiers`; caption
  `Risk-size` (12F).** The spec originally said the Tooling group with a fallback "under the
  Risk/Max rows". That fallback **does not exist** (defect §5, upheld and measured): `grpTooling`
  has zero vertical slack inside the pinned 512×856 — the row under Risk/Max is the Min Net Profit
  row, and the EV reclaim already spent the only spare height getting `lblAtrNow` out of the group.
  Every remaining Tooling space is a mid-row gutter, so a checkbox there reads as a modifier of an
  unrelated knob (ATR Length, or Min Net Profit). The caption `Risk-size bridge trades` fits none
  of them at 12F.
  The SIGNAL BRIDGE slot is the better home on the merits, not merely the fallback: this is a
  **bridge** behaviour, and it joins the right-hand checkbox column beside `ARM AUTOTRADE` and
  `Policy` — where terse captions carrying a whole feature, with the meaning in the tooltip, are
  already house style. It reuses the Risk/Max boxes but does not have to sit next to them.
  *(Owner-visible change of group: vetoable at the screenshot check below.)*
- `chkRiskSizeBridge`, seed-before-commit (the standing trap, fifth application — seed with the
  other seeds, before the first commit); `CheckedChanged` commits like `chkSessionPolicyOn`
  (AddHandler after seed, not Handles). Tooltip: what it does, that OFF = Amount-box sizing exactly
  as today, which keys drive it, and that the session multiplier applies on top either way.
  **Geometry: screenshot-verify before the item closes** (the WordWrap lesson) — a probe that
  constructs the real form and dumps live bounds is the cheap pre-check, but a screenshot is still
  the only instrument that shows wrap and clip.
- Bridge live-read: a `Friend` mirror on the settings form (the TiersCsv pattern) — plain Boolean.

## §3 — Log-only / soak semantics

The `would-act:` line's `size` field carries the risk-sized `effective` when enabled — the
counterfactual becomes size-aware, which is exactly what the owner reviews before trusting it
live. Disabled ⇒ today's line, byte-identical.

## §4 — OrderCheck fixtures + the shared seam — **AMENDED**

**Seam signature RULED:**

```vb
' The step-floored, capped risk size. -1 = inputs cannot produce a size (caller's fail-safe).
Friend Shared Function RiskSizedBase(riskUsd As Decimal, maxSizeUsd As Decimal,
                                     refPrice As Decimal, dist As Decimal) As Decimal
```

The spec originally said `(riskUsd, maxSizeUsd, entry, stop)`. That signature **cannot be shared**
(defect §4, upheld): the SIZE button has no stop *price* — its distance is
`If(manualSLval > 0D, Math.Abs(refPrice - manualSLval), triggerDistance)`, and in offset mode there
is no stop level at all. A seam keyed on `(entry, stop)` could only ever be a **second copy** of the
arithmetic, which fixtures can pin but cannot bind — the precise drift the review anchor forbids.
Taking `(refPrice, dist)` lets both callers share one function.

**"Untouched" in the Do-not-touch list means BEHAVIOUR, not text — RULED.** Repoint
`ApplyRiskBasedSize` at the seam: its diff is two arithmetic lines → one call, with all three of
its refusal messages and every guard left exactly as they are. The anchor *"one formula, not a
second implementation that drifts"* is the stronger constraint, and a behaviour-identical repoint
honours both. Any behavioural change to the button is out of scope and would need its own ruling.

**The below-10 policy stays with each caller** — the two deliberately differ and both are already
ruled: the button **refuses** and leaves `txtAmount` alone; the bridge **clamps to 10**, matching
`EffectiveSizeUsd`'s D3 clamp-and-log ruling (H-4 §4.2). The seam returns the capped, step-floored
size and takes no position on it.

**Fixtures — arithmetic verified by the coordinator, adopt as-is:**

| fixture | risk | refPrice | dist | max | step-floored | after cap |
|---|---|---|---|---|---|---|
| uncapped ("typical") | 25 | 64735 | 5000 | 500 | **320** | 320 |
| non-step floor | 25 | 64735 | 4000 | 500 | **400** | 400 |
| cap binds | 25 | 64735 | 54.62 | 500 | 29,620 | **500** |
| floor binds | 0.01 | 64735 | 5000 | 500 | **0** | 0 ⇒ bridge clamps 10 |
| non-step cap | 25 | 64735 | 2000 | **505** | 800 | **500** (step-floored, per §1) |
| cap off | 25 | 64735 | 5000 | **0** | 320 | **320** (no cap — NOT 10) |
| composed | — | — | — | — | `EffectiveSizeUsd(320, 0.5)` | **160** |

The last two are the defect-§2 regression pins; the composed one proves sessionFactor applies
exactly once. Also pin the `-1` sentinel arm, and note it is unreachable from the button (its own
guards precede the call). Disabled-parity is structural (the branch); pin one fixture anyway.

## Acceptance

1. Gate per commit, new fixtures counted.
2. **Disabled parity (the ship-safe proof):** checkbox off ⇒ live/log-only behaviour and lines
   byte-identical to HEAD.
3. **Testnet (isolated-harness):** enabled — a crafted actionable payload with known entry/stop
   yields `would-act … size <computed>` matching the fixture arithmetic; with a LONDON 0.5 policy
   line the composed size halves-then-floors as one chain. **AMENDED:** the Amount box no longer
   *determines* the size, but it is not irrelevant — gate 4.6 (`ElseIf SizeUsd <= 0D` ⇒
   `refused: size`) still reads the raw box and is on the Do-not-touch list, so the run must leave
   it **non-zero** or the payload is refused before sizing ever runs.
3b. **⚠ A LIVE-mode leg is REQUIRED, not optional** (defect §1). Log-only cannot distinguish the
   size that was computed from the size that would have been placed — that is exactly how the
   original §1 defect would have shipped. One testnet **Live** act with the checkbox ON, session
   policy OFF (i.e. unity mult, the default), must place the **computed** size, not the Amount box.
   Verify against the exchange fill/position size, not against the log line.
4. **Persist round-trip:** tick on, restart ⇒ still on (and vice versa).
5. Greps: `txtAmount` writers unchanged; `refused: size` unchanged; `SizeUsd` property untouched;
   one new call site for `EffectiveSizeUsd` composition or the existing one refactored — either
   way sessionFactor applies EXACTLY once (fixture 3 proves it).

## Commits

1. `Risk-sized bridge trades: formula seam + fixtures` — includes repointing `ApplyRiskBasedSize`
at the shared seam (§4, behaviour-identical) · 2. `act-site integration behind the flag` ·
3. `SIGNAL BRIDGE checkbox + persistence (seed-before-commit)` (§2 as amended — the bridge group,
not Tooling) · 4. impl report (**`docs/impl-report-risk-sized-bridge-trades.md`** — corrected).
