# Spec — Risk-sizing settings UI + SIZE button relocation

**Origin:** owner request 2026-07-19, after runtime-testing ergonomics item B. Two problems with the
shipped item B: (1) `risk_per_trade_usd` / `max_size_usd` are file-only — there is no way to tune them
without hand-editing `orderapp-settings.json`; (2) the `SIZE` button on the main form is cramped and
visually hidden beside the Amount textbox. Both move to the **Tooling** section of `AutoTradeSettings`.

**Supersedes:** `docs/spec-execution-ergonomics.md` item B's *placement* decision only ("a small `SIZE`
button near `txtAmount`"). **The sizing formula, the config keys, the 10-USD floor, the `max_size_usd`
clamp and every refusal message are unchanged and must stay byte-identical** — the owner has already
runtime-verified them (impl report, 2026-07-18 acceptance).

**Recommended implementer:** Opus at **high** — mostly mechanical, but §2's initialisation ordering is a
silent-data-loss trap and the cross-form call in §3 must not break the commit-on-blur safety model.
One conversation.
**Target:** `AutoTradeSettings.Designer.vb`, `AutoTradeSettings.vb`, `frmMainPageV2.Designer.vb`,
`frmMainPageV2.vb`. **Base:** current master HEAD (contains ergonomics Phase A). **Anchors by symbol;
verify against HEAD at implementation time.**
**Ground rules:** the standing ones — build 0/0 per commit via `tools/checks/verify-gate.ps1`
(**GATE PASSED required**, it builds both configs and runs the OrderCheck fixtures), local commits,
never push, receive-thread invariants untouched (nothing here goes near the receive path), scope
discipline, impl report at the end.

---

## §1 — Layout reflow: drop the title, notes become tooltips

**Delete three controls** (verified 2026-07-19: referenced **only** in the Designer — no code-behind,
script, or fixture references, so deletion is safe):

| Control | Current | Disposition |
|---|---|---|
| `lblBacktestTitle` | (103,20) 318×44, "AutoTrading Section" | **delete** (legacy name from the retired backtest era) |
| `lblBridgeSourceNote` | (11,246) 460×52 inside `grpSignalBridge` | **delete**, text → tooltip |
| `lblToolingNote` | (11,158) 460×28 inside `grpTooling` | **delete**, text → tooltip |

**Tooltips — APPEND, do not overwrite.** The form already has an `AutoTradingToolTip` component and
**both target controls already carry tooltips**. Append the migrated text to the existing strings:

- `grpSignalBridge` (already: *"VerdictEngine signal-bridge consumer (contract v1)…"*) — append
  `"Size = main form's Amount box."` + `"Cooloff / max loss / window = sections above."`
- `lblAtrNow` (already: *"The ATR the slippage guard is using right now…"*) — append
  `"Priority: payload, then indicator, then fallback."`

**Reflow.** Everything shifts up into the freed title space; `grpSignalBridge` shrinks by the removed
note. **Hard constraints:** `ClientSize` stays **512×856** (the form is positioned by `StickToHost` at
the host's right edge, top-aligned — matching heights is deliberate); keep the existing **18px left
margin / 482 width / 12px inter-group gutter**; nothing clipped or overlapping.

Proposed geometry (implementer may fine-tune within the constraints):

| Group | New Location | New Size |
|---|---|---|
| `GroupBox1` (Inclusion Time Range) | (18, 12) | 482×100 |
| `grpTradeGates` | (18, 124) | 482×130 |
| `grpSignalBridge` | (18, 266) | 482×**256** (was 300; last child `txtBridgeTiers` bottoms at 246) |
| `grpTooling` | (18, 534) | 482×**270** (was 194) |

That leaves ~50px slack at the bottom. Internals of `GroupBox1`, `grpTradeGates` and `grpSignalBridge`
are **unchanged** (they move with their group).

## §2 — Risk-sizing config in Tooling

### New controls (inside `grpTooling`, coordinates relative to the group)

| Control | Location | Size | Text |
|---|---|---|---|
| `lblRiskPerTradeCap` | (11, 130) | ~178×35 | `Risk / Trade:` |
| `txtRiskPerTrade` | (330, 126) | 64×42 | `25` |
| `lblRiskPerTradeUnit` | (400, 134) | 50×24 | `USD` |
| `lblMaxSizeCap` | (11, 178) | ~178×35 | `Max Size:` |
| `txtMaxSize` | (330, 174) | 64×42 | `500` |
| `lblMaxSizeUnit` | (400, 182) | 50×24 | `USD` |
| `btnRiskSize` | (200, 126) | 120×90 | `SIZE` |
| `lblAtrNow` | (11, **226**) | 460×30 | *(moved down from y=126)* |

The two existing ATR rows keep their coordinates (30 / 78). `btnRiskSize` sits in the free gutter
between the captions (which end ~x=189) and the textbox column (x=330), spanning both new rows — big
and obvious, which is the point of the move. **Caption check:** measure `SIZE` at its font on ONE line
(the `btnAutoSettings` lesson — a Button silently wraps and clips instead of complaining).

Tooltips for the new controls: reuse the main form's existing `btnRiskSize` tooltip text verbatim, and
give the two boxes their own (what each value means; that they persist via `orderapp-settings.json`).

### Plumbing — host accessors

`frmMainPageV2` already owns the values inside `userSettings` (`AppUserSettings.RiskPerTradeUsd` /
`.MaxSizeUsd`). Add:

```vb
Friend ReadOnly Property RiskPerTradeUsd As Decimal   ' If(userSettings IsNot Nothing, .RiskPerTradeUsd, 25D)
Friend ReadOnly Property MaxSizeUsd As Decimal        ' If(userSettings IsNot Nothing, .MaxSizeUsd, 500D)
Friend Sub SetRiskSizingValues(riskPerTrade As Decimal, maxSize As Decimal)
```

`SetRiskSizingValues` writes into `userSettings` and **ignores non-positive values** — the same
convention as the existing `SetToolingValues`, so a blank/garbage box keeps the last good value.

**Persistence is free:** item A's existing save path (`FormClosing` + the "Save Trade Defaults"
context item on MARGINS/AMOUNT) already writes `risk_per_trade_usd` / `max_size_usd`. **Do not add a
new save path.** Consequence to document: an edit is persisted at the next save/close, exactly like
every other standing input.

### Plumbing — settings form (follow the existing commit-on-blur model)

1. Add `txtRiskPerTrade` and `txtMaxSize` to the `InitialiseSettings` wiring array (alongside
   `txtAtrLength`, `txtAtrFallback`). That gives them select-all-on-entry, commit-on-leave,
   commit-on-Enter **and** `AccessibleName = tb.Name`, which the harness's `set-textbox.ps1` needs.
2. Extend `CommitToolingConfig()` to parse both boxes and call `_host.SetRiskSizingValues(...)`.
3. Extend `ShowGateConfigWarnings()` to flag unparseable / non-positive risk & max-size, consistent
   with the existing ATR entries.

### ⚠ The initialisation-ordering trap (the one thing most likely to be got wrong)

`InitialiseSettings()` currently calls `CommitGateConfig()` **then** `CommitToolingConfig()` **before**
wiring handlers. Once `CommitToolingConfig` also pushes risk/max-size, that first call would read the
**Designer defaults (25/500)** and **overwrite the values just loaded from `orderapp-settings.json`** —
silently resetting the owner's tuned numbers on every single start.

**Required fix:** seed the two boxes **from the host** before the first `CommitToolingConfig()`:

```vb
Friend Sub InitialiseSettings()
    SeedRiskSizingFromHost()   ' NEW - MUST precede CommitToolingConfig
    CommitGateConfig()
    CommitToolingConfig()
    For Each tb As TextBox In { … , txtRiskPerTrade, txtMaxSize }
    …
```

with `SeedRiskSizingFromHost()` writing `_host.RiskPerTradeUsd` / `_host.MaxSizeUsd` into the boxes.

**Ordering dependency to preserve (state it in a comment):** in `frmMainPageV2_Load`,
`userSettings = AppUserSettings.Load(...)` runs **before** `_autotradesettings = New AutoTradeSettings(Me)`
and `InitialiseSettings()` — verified at HEAD. The seed relies on that. If anyone reorders Load, this
breaks silently.

## §3 — Relocate the SIZE button

**Keep the logic on the host.** The sizing math reads host engine mirrors (`TradeMode`,
`BestBidPrice`/`BestAskPrice`, `manualSLval`, `triggerDistance`, `userSettings`), writes
`txtAmount.Text`, and logs to the main form's `txtLogs`. It stays where those live.

1. **`frmMainPageV2`:** extract the current `btnRiskSize_Click` body **verbatim** into
   `Friend Sub ApplyRiskBasedSize()`. Delete the old handler.
2. **`frmMainPageV2.Designer.vb`:** remove `btnRiskSize` entirely (declaration, instantiation,
   property block, `OrderAmount.Controls.Add`) **and restore `txtAmount.Size` to `New Size(200, 47)`** —
   it was narrowed 200→148 by ergonomics commit 2/9 purely to make room for this button. Drop the
   now-obsolete "width 200 -> 148" comment.
3. **`AutoTradeSettings`:** the new button's handler is a thin forwarder:

```vb
Private Sub btnRiskSize_Click(sender As Object, e As EventArgs) Handles btnRiskSize.Click
    CommitToolingConfig()                 ' commit a half-typed risk/max-size edit BEFORE using it
    If _host IsNot Nothing Then _host.ApplyRiskBasedSize()
End Sub
```

The explicit `CommitToolingConfig()` is required, not decorative: clicking the button normally fires
`Leave` on the focused box first, but relying on focus order for a value that decides position size is
exactly the half-typed hazard the commit-on-blur model exists to prevent. Commit-first guarantees the
click uses the number the owner can see.

**Threading:** both forms are UI-thread; the settings-form click and the host method run on the same
thread, so the host may touch its own controls directly. No marshalling, no receive-path contact.

## §4 — Must NOT change

- The sizing formula, the 10-USD floor, the `max_size_usd` clamp, and all four refusal messages
  (runtime-verified 2026-07-18) — **byte-identical**, only relocated.
- The bridge gate-config mirrors and their commit semantics; `_cooloffMin`/`_circuitBreakerUsd`/
  `_windowStart`/`_windowEnd`/`_tiersCsv` and their properties.
- **Mode / ARM / Started never persist** (contract §6: restart = disarmed) — do not let this pass
  sweep them into `orderapp-settings.json`.
- `SetToolingValues` semantics (host ignores non-positive) and the ATR readout timer.
- `StickToHost` positioning and the host Location/Size handler wiring.
- Item A's persistence: **no new save path.**

## §5 — Harness impact

- New textboxes get `AccessibleName` automatically **only if** added to the `InitialiseSettings` array
  (§2.1) — `tools/set-textbox.ps1` matches on it. Verify with `tools/inspect-tree.ps1`.
- `btnRiskSize` has **no** existing script references (verified 2026-07-19), so the move breaks nothing.
- `tools/trade-buttons.txt` (the PLACES-ORDER deny list) — **SIZE does not belong on it**: it places no
  order, it only writes `txtAmount`. Note the reasoning so a reviewer does not flag the omission.
- Known nit (unchanged, do not fix here): `set-textbox.ps1`'s substring match cannot target `txtTrigger`
  because `txtTriggerOffset` shadows it in tree order. Avoid creating a new shadowing pair — check that
  `txtMaxSize` / `txtMaxSlippageATR` do not collide under substring matching.

## Acceptance

1. **Layout:** form opens with no "AutoTrading Section" title; all four groups visible, nothing clipped
   or overlapping; `ClientSize` still 512×856; the form still sits flush to the host's right edge.
2. **Tooltips:** hovering `grpSignalBridge` shows the original bridge text **plus** the two migrated
   lines; hovering the ATR readout shows the original text **plus** the priority line. Neither original
   tooltip was lost.
3. **Tooling contents:** ATR Length, ATR Fallback, Risk / Trade, Max Size, a prominent `SIZE` button,
   and the live ATR readout — all legible, captions on one line.
4. **Config round-trip (the §2 trap):** hand-edit `orderapp-settings.json` to
   `risk_per_trade_usd: 40`, `max_size_usd: 1200` → restart → **the boxes show 40 and 1200**, not the
   Designer defaults, and a SIZE click uses them.
5. **Edit + persist:** change Risk to 50, tab away, close the app (or right-click MARGINS → Save Trade
   Defaults) → reopen → 50 restored and in force.
6. **Half-typed safety:** type a new Risk value *without* leaving the box, click `SIZE` → the size is
   computed from the value on screen (commit-first), not the stale one.
7. **Behaviour parity:** with a live price and Trig P 60, `SIZE` writes the same amount and logs the
   same `Size: $N (risk $R over $D stop distance)` line **in the main form's log** as before the move;
   the zero-distance refusal is unchanged.
8. **Main form:** `txtAmount` back to width 200, no SIZE button, AMOUNT($) group otherwise untouched.
9. **Gate:** `tools/checks/verify-gate.ps1` → GATE PASSED (both configs, OrderCheck 48/48).

## Commits

1. `Settings UI: drop the AutoTrading title, notes become tooltips, reflow groups` (§1)
2. `Settings UI: risk-per-trade / max-size boxes in Tooling (+ host accessors, seed-before-commit)` (§2)
3. `Settings UI: relocate the SIZE button to Tooling` (§3, incl. restoring txtAmount to 200)

## Implementation report

`docs/impl-report-risk-sizing-settings-ui.md`, standard format: per section — exact changes, build
results, deviations with justification, a note on how the §2 ordering trap was verified (acceptance 4
is the evidence), and suspicious-nearby not touched.
