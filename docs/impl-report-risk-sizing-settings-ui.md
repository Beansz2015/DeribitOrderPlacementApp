# Implementation report — risk-sizing settings UI + SIZE button relocation

**Spec:** `docs/spec-risk-sizing-settings-ui.md` (coordinator-approved `b77b724`, as written).
**Commits:** 3, per the spec's plan — `fa11492` (§1) · `5997494` (§2) · `6959a7a` (§3), sequenced
after the raced-abort repair (`a32946c`) per the spec's ordering note. **Gate:**
`tools/checks/verify-gate.ps1` after each — Release 0/0, Debug (vbproj direct) 0/0, OrderCheck 48/48,
**GATE PASSED ×3**. **Implementer seat:** Fable (the spec's in-window recommendation).

## §1 — layout reflow (`fa11492`)

- Deleted `lblBacktestTitle` ("AutoTrading Section"), `lblBridgeSourceNote`, `lblToolingNote` —
  declaration, config block, `Controls.Add`, `WithEvents` each; post-delete grep confirms zero code
  references (only my explanatory comments name them).
- Tooltip migration **by appending** to the existing `AutoTradingToolTip` strings — the original
  bridge text on `grpSignalBridge` and readout text on `lblAtrNow` both survive, with the two
  source-note lines / the priority line added after them.
- Reflow exactly per the spec table: Inclusion Time Range (18,12) 482×100 · Trade Gates (18,124)
  482×130 · SIGNAL BRIDGE (18,266) 482×**256** · Tooling (18,534) 482×**270**. Arithmetic:
  12+100→124+130→(+12)266+256→(+12)534+270 = bottom **804** of the unchanged **512×856** ClientSize
  (the coordinator's re-check figure). `StickToHost` untouched. Group internals untouched in §1.

## §2 — risk-sizing config in Tooling (`5997494`)

- Two new rows styled identically to the ATR rows (14pt caption/box, 10pt USD unit, black/white
  box): `lblRiskPerTradeCap`/`txtRiskPerTrade` (25) at y≈126–130, `lblMaxSizeCap`/`txtMaxSize`
  (500) at y≈174–182; `lblAtrNow` moved 126 → 226. Designer defaults deliberately equal
  `AppUserSettings`' defaults. Tooltips state the meaning, the formula relationship (the Max Size
  one explains the cap-dominates-risk behaviour the owner hit in acceptance), and the json keys.
- **Host accessors** (`frmMainPageV2.vb`, beside `SetToolingValues`): `Friend RiskPerTradeUsd` /
  `MaxSizeUsd` (read `userSettings`, fall back 25/500) and `SetRiskSizingValues` — ignores
  non-positive values, the `SetToolingValues` convention, so a blank/garbage box keeps the last
  good value. Values persist via item A's existing save path; **no new save path added** (spec).
- **Settings form:** both boxes added to the `InitialiseSettings` wiring array (select-all,
  commit-on-leave, commit-on-Enter, `AccessibleName = tb.Name` for the harness);
  `CommitToolingConfig` extended to parse both and call `SetRiskSizingValues`;
  `ShowGateConfigWarnings` extended (unparseable/non-positive risk or max size joins the orange
  "Ignored (keeping last good)" line).
- **The §2 trap, fixed as specced:** new `SeedRiskSizingFromHost()` runs **first** in
  `InitialiseSettings`, writing the host's just-loaded `orderapp-settings.json` values into the
  boxes before the first `CommitToolingConfig` can push anything back. Without it, that first
  commit would overwrite the owner's tuned numbers with the Designer defaults on every start.
  The Load-ordering dependency (host loads `userSettings` before constructing `AutoTradeSettings`)
  was re-verified at HEAD and is stated in comments on **both** sides of the seam.
  **Verification of acceptance 4 (the trap test), static:** at `InitialiseSettings` time the call
  order is Seed → CommitGate → CommitTooling; Seed writes `_host.RiskPerTradeUsd`/`MaxSizeUsd`
  (= the file's values) into the boxes, so the commit reads back exactly what the file supplied.
  The live half (hand-edit 40/1200 → restart → boxes show 40/1200) is the owner's runtime check.

## §3 — button relocation (`6959a7a`)

- Main form: `btnRiskSize_Click` body extracted **verbatim** into `Friend Sub ApplyRiskBasedSize()`
  — sole textual change is the `Catch` line's method name following the rename (cosmetic; every
  formula line and all four refusal messages byte-identical to the runtime-verified item B).
  Designer: `btnRiskSize` fully removed (grep = 0 in both main-form files); `txtAmount` restored to
  **200×47** with the obsolete width comment replaced.
- Settings form: `btnRiskSize` 120×90 at Tooling (200,126) — the caption/textbox gutter, spanning
  both risk rows; caption "SIZE" single-line at 14pt bold (~55px < 120 — the `btnAutoSettings`
  wrap-clip lesson applied); tooltip text carried over verbatim. Handler is the spec's thin
  forwarder: `CommitToolingConfig()` **then** `_host.ApplyRiskBasedSize()` — commit-first so a
  half-typed risk/max-size edit can never decide position size through stale mirrors. Both forms
  are UI-thread; the direct cross-form call is safe (no marshalling, no receive-path contact).

## §4 must-not-change — checked

Formula/floor/clamp/refusals byte-identical (§3 above). Gate-config mirrors and commit semantics
untouched (`CommitGateConfig` unmodified; `CommitToolingConfig` only gained the two new parses).
Mode/ARM/Started still never persist — nothing bridge-related entered `AppUserSettings`.
`SetToolingValues`, the ATR readout timer, `StickToHost`, and item A's persistence all untouched.

## §5 harness impact — checked

- Both new boxes get `AccessibleName` via the wiring array (§2), so `tools/set-textbox.ps1` can
  target them; owner can confirm with `tools/inspect-tree.ps1` when convenient.
- `btnRiskSize` had no script references before the move (verified pre-spec) and gains none.
- `tools/trade-buttons.txt`: SIZE deliberately **not** added — it places no order (writes
  `txtAmount` only); reasoning recorded per spec so a reviewer doesn't flag the omission.
- Substring-shadowing check: `txtMaxSize` ⊄ `txtMaxSlippageATR` and vice versa (they diverge at
  "txtMaxSi"/"txtMaxSl"), and they live on different windows besides — no new shadowing pair.
  `txtRiskPerTrade` collides with nothing.

## Deviations

None of substance. Two cosmetic notes: (1) the extracted method's `Catch` message says
`ApplyRiskBasedSize` instead of `btnRiskSize_Click` (the method it is actually in); (2) the
relocated button's font is 14pt bold (was 10pt) to suit the 120×90 size — caption measured
single-line.

## Acceptance status (owner runtime pass pending)

Static halves done: geometry arithmetic (1), tooltip append (2), formula parity (7 — byte-equal
extraction), `txtAmount` 200 (8), gate (9). Owner's runtime checks: visual reflow + tooltips on
hover (1–3), the **config round-trip trap test** (4 — hand-edit `risk_per_trade_usd: 40`,
`max_size_usd: 1200` → restart → boxes must show 40/1200, SIZE uses them), edit-and-persist (5),
half-typed commit-first (6 — type a new Risk, do NOT tab away, click SIZE → computed from the
on-screen value), behaviour parity of the SIZE click itself (7).

## Suspicious-nearby NOT touched

- The SIGNAL BRIDGE panel handlers and `RefreshBridgePanel` — the reflow moved their group, not them.
- `_atrTimer` / `RefreshAtrReadout` — `lblAtrNow` moved within its group; the readout logic and its
  1 s tick are untouched.
- The `selectallclick` handler list on the main form (item B's button was never in it).
- `CommitGateConfig`'s fail-closed window semantics — near the edited `CommitToolingConfig` but
  deliberately unmodified.
