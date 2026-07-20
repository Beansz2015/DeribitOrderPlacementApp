# Coordinator review — risk-sizing settings UI (`fa11492` · `5997494` · `6959a7a` + report `a0bb35c`)

**Date:** 2026-07-21 · **Reviewer:** coordinator seat (Fable) · **Verdict: ALL 3 APPROVED** —
owner runtime pass pending (acceptance 1–7; **4 is the trap's proof**).

**Method:** all three diffs read in full; the §2 trap and §3 extraction verified against actual
code at HEAD; **`tools/checks/verify-gate.ps1` EXECUTED at `6959a7a`** in an isolated worktree →
Release 0/0, Debug 0/0, OrderCheck **48/48**, repo guards OK, **GATE PASSED**.

## §1 (`fa11492`) — APPROVED

Three deletions Designer-complete (declaration, config block, `Controls.Add`, `WithEvents`; zero
surviving references). Both tooltips APPENDED — the original bridge and readout strings survive
verbatim in the diff. Geometry re-derived: 12+100 → 124+130 → (+12) 266+256 → (+12) 534+270 =
bottom **804** of the unchanged 512×856; `grpSignalBridge` 256 still clears `txtBridgeTiers`
(bottoms 246). `StickToHost` and all group internals untouched.

## §2 (`5997494`) — APPROVED — the trap is fixed as specced

- `SeedRiskSizingFromHost()` is the FIRST statement of `InitialiseSettings`, ahead of
  `CommitGateConfig`/`CommitToolingConfig`; it writes `_host.RiskPerTradeUsd`/`MaxSizeUsd` (= the
  file's just-loaded values) into the boxes, so the first commit reads back exactly what the file
  supplied. **The Load-ordering dependency was re-verified by the reviewer at HEAD:**
  `userSettings = AppUserSettings.Load` (:688) precedes `New AutoTradeSettings(Me)` (:717) and
  `InitialiseSettings()` (:718), and the dependency is stated in comments on both sides.
- Both boxes in the wiring array → select-all, commit-on-leave/Enter, `AccessibleName` (harness).
- `CommitToolingConfig` extension and `SetRiskSizingValues` follow the ignore-non-positive
  convention; `ShowGateConfigWarnings` extended consistently. Host accessors default 25/500.
- **No new save path** — persistence rides item A; nothing bridge-related (Mode/ARM/Started)
  entered `AppUserSettings`. `CommitGateConfig` unmodified.
- Reviewer note (accepted): `SetRiskSizingValues`' defensive `If userSettings Is Nothing Then New`
  is unreachable in practice (Load precedes construction) — harmless belt-and-braces.

## §3 (`6959a7a`) — APPROVED

- Extraction verified byte-identical by the diff itself: the only changed lines are the signature
  (`Private … Handles btnRiskSize.Click` → `Friend Sub ApplyRiskBasedSize()`), the comment block,
  and the `Catch` message's method name. Formula, 10-USD floor, clamp, and all four refusal
  messages untouched — §4 must-not-change holds.
- Main form: `btnRiskSize` grep = 0 across `frmMainPageV2.vb`/`.Designer.vb`; `txtAmount` restored
  to 200×47.
- Forwarder: `CommitToolingConfig()` **then** `_host.ApplyRiskBasedSize()` — the commit-first
  guarantee is real (half-typed box can never size a position through stale mirrors). Both forms
  UI-thread; no receive-path contact anywhere in the three commits.

## §5 harness — checked

`AccessibleName` via the wiring array ✓; SIZE deliberately NOT on `tools/trade-buttons.txt`
(places no order — writes `txtAmount` only; reasoning accepted); no new substring-shadowing pair
(`txtMaxSize`/`txtMaxSlippageATR` diverge at "txtMaxSi"/"txtMaxSl" and live on different windows).

## Deviations

Both cosmetic notes accepted (Catch message names the method it is actually in — arguably more
correct; 14pt bold button font, caption measured single-line per the wrap-clip lesson).

## Remaining before "done"

Owner runtime acceptance 1–8 (report §Acceptance status) — **acceptance 4 is the §2 trap's
runtime proof**: hand-edit `risk_per_trade_usd: 40` / `max_size_usd: 1200` → restart → the boxes
must show 40/1200 (not 25/500) and a SIZE click must use them.
