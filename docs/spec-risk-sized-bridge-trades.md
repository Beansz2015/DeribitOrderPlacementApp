# Spec — N2: risk-sized bridge trades (ships DISABLED; owner enables at normal size)

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

## §1 — The formula (ONE chain, applied once, at the act/would-act site only)

When enabled, replace the act-site's `rawSize = SizeUsd` base with:

```
dist      = Abs(p.Entry - p.StopLevel)                     ' the ENGINE's own geometry
riskSize  = Math.Floor((RiskPerTradeUsd * p.Entry / dist) / 10D) * 10D   ' the runtime-verified
                                                           ' inverse-contract linearization,
                                                           ' floored to the 10-USD step
base      = Math.Max(10D, Math.Min(riskSize, MaxSizeUsd))  ' cap then contract-min clamp
effective = EffectiveSizeUsd(base, sessionMult)            ' the EXISTING sessionFactor fold -
                                                           ' unity passthrough and all
```

- **Formula parity is the point:** `risk × ref ÷ dist`, step-floor, `max_size_usd` cap — the exact
  arithmetic the owner runtime-verified in the SIZE button (`ApplyRiskBasedSize`), with `ref` =
  the payload `entry` and `dist` = the payload stop distance (both engine-authoritative; the
  levels guard has already refused stop/target ≤ 0 before this code can run).
- **Fail-safe:** `dist <= 0` (cannot occur past `refused: levels`, but belt-and-braces) ⇒ fall
  back to the raw Amount box + one yellow line. Never refuse a signal because sizing math hiccuped.
- **Clamp logging:** one yellow line when the cap or the 10-floor binds (mirrors the session-mult
  clamp line style), so undersized-risk sessions are visible.
- Disabled (default): the act site is **byte-identical to today** — `rawSize = SizeUsd`, session
  mult applied exactly as now. Structure the code so the disabled path does not even compute.

## §2 — Config + UI

- `AppUserSettings`: `RiskSizeBridgeTrades As Boolean = False`, key `risk_size_bridge_trades`
  (absent ⇒ False). Persists on item A's atomic path.
- `AutoTradeSettings` Tooling group: `chkRiskSizeBridge` ("Risk-size bridge trades", the
  12F checkbox style) beside the existing Risk/Max boxes it reuses. Seed-before-commit (the
  standing trap, fifth application — seed with the other seeds, before the first commit);
  `CheckedChanged` commits like `chkSessionPolicyOn` (AddHandler after seed, not Handles).
  Tooltip: what it does, that OFF = Amount-box sizing exactly as today, which keys drive it, and
  that the session multiplier applies on top either way. **Geometry: screenshot-verify** (the
  WordWrap lesson); if the Tooling group lacks clean space, the checkbox may go under the
  Risk/Max rows — implementer's call within the pinned 512×856.
- Bridge live-read: a `Friend` mirror on the settings form (the TiersCsv pattern) — plain Boolean.

## §3 — Log-only / soak semantics

The `would-act:` line's `size` field carries the risk-sized `effective` when enabled — the
counterfactual becomes size-aware, which is exactly what the owner reviews before trusting it
live. Disabled ⇒ today's line, byte-identical.

## §4 — OrderCheck fixtures (pure seam: extract the §1 computation as `Friend Shared`)

`RiskSizedBase(riskUsd, maxSizeUsd, entry, stop)` fixtures: typical (25 risk, 64735 entry, 54.6
dist ⇒ 29,640 → floor 29,640? — compute honestly: 25×64735/54.62 = 29,629 → 29,620 → capped 500);
cap binds (result = max_size); floor binds (tiny risk ⇒ 10); dist=0 ⇒ sentinel/fallback signal;
non-step flooring; then one composed fixture: `EffectiveSizeUsd(RiskSizedBase(...), 0.5)` chains
correctly. Disabled-parity is structural (the branch), pin one fixture anyway if the seam allows.

## Acceptance

1. Gate per commit, new fixtures counted.
2. **Disabled parity (the ship-safe proof):** checkbox off ⇒ live/log-only behaviour and lines
   byte-identical to HEAD.
3. **Testnet (isolated-harness):** enabled, Amount box irrelevant — a crafted actionable payload
   with known entry/stop yields `would-act … size <computed>` matching the fixture arithmetic;
   with a LONDON 0.5 policy line the composed size halves-then-floors as one chain.
4. **Persist round-trip:** tick on, restart ⇒ still on (and vice versa).
5. Greps: `txtAmount` writers unchanged; `refused: size` unchanged; `SizeUsd` property untouched;
   one new call site for `EffectiveSizeUsd` composition or the existing one refactored — either
   way sessionFactor applies EXACTLY once (fixture 3 proves it).

## Commits

1. `Risk-sized bridge trades: formula seam + fixtures` · 2. `act-site integration behind the
flag` · 3. `Tooling checkbox + persistence (seed-before-commit)` · 4. impl report
(`docs/impl-report-risk-sized-bridge.md`).
