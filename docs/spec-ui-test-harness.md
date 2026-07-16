# Spec — Autonomous build/drive/verify harness (testnet-tiered)

**Date:** 2026-07-16. **Base:** `c7cc8a4` (verify = origin, tree clean; anchor by SYMBOL — line refs drift).
**Source pattern:** `C:\Dev\DeribitVerdictEngine\docs\ui-automation-harness-handoff-orderapp.md` (the engine coordinator's handoff — READ it first; its §1–§4 skeletons and `C:\Dev\DeribitVerdictEngine\tools\*.ps1` may be **copied freely INTO this repo**; never write to theirs).
**Recommended implementer:** **Fable high while the window lasts (~Jul 19), else Opus high — one conversation.** NOT Sonnet: commits 1–3 create the app's first environment fork (endpoint switch, credential selection) and the safety-wrapper refuse-logic; that must be right the first time. The PowerShell ports ride along.
**Ground rules:** standing — build **0/0 in BOTH Debug and Release** per commit (sln default = Release; the owner debugs in Debug), local commits, never push, scope discipline, impl report `docs/impl-report-ui-test-harness.md`. New VB files `Option Strict On`/`Option Explicit On`.

**Owner decisions on record (2026-07-16, all four asked-and-answered):** (1) testnet profile + tiered scripts; (2) in-app hooks config-gated (title env-suffix always; screenshot hotkey behind a local flag); (3) pre-push hook = build + logic fixtures only; (4) this lands NOW, own conversation, before ergonomics Phase A.

**Owner prerequisite (needed for the runtime test phase, not for the commits):** register a `test.deribit.com` account + API key pair.

---

## 0. Philosophy (adopted from the engine handoff §0)

Two layers. **Layer 1 — logic harness:** a console project (`OrderCheck`) running deterministic fixtures against the app's pure seams; exit 0/1; this is where most verification lives. **Layer 2 — UI automation:** PowerShell + UIA driving the *running* app — because a clean build proves nothing about geometry or wiring (our own `btnAutoSettings` caption-clip and the §9.3 commit-on-blur behavior are exactly this class). A gate script wraps layer 1 + builds; layer 2 stays on-demand.

## 1. Commit 1 — testnet environment profile (app)

- **`AppSecrets`**: `secrets.json` gains optional `"Environment": "live"|"testnet"` (absent/other ⇒ **live** — existing files keep working) and an optional `"DeribitTestnet": { ClientId, ClientSecret }` block. `Load()` selects the credential block by environment; testnet selected + testnet block missing ⇒ load error (fail closed, no silent live fallback). New members: `Shared ReadOnly Property IsTestnet As Boolean`, `Shared ReadOnly Property WsUrl As String` → `wss://test.deribit.com/ws/api/v2` | `wss://www.deribit.com/ws/api/v2`.
- **Endpoint consumers**: `frmMainPageV2`'s `New Uri("wss://www.deribit.com/ws/api/v2")` (connect sequence, ~`:879`) → `New Uri(AppSecrets.WsUrl)`; `FrmIndicators.DeribitUrl` const (`:22`, the headless ATR engine's quote stream) → reads `AppSecrets.WsUrl`. Nothing else in the connect/receive path changes.
- **Window title (LOAD-BEARING CONVENTION, day one):** at Load, after secrets load: `Me.Text = "Deribit Order Placement App V2.2" & If(AppSecrets.IsTestnet, " — TESTNET", " — LIVE")`. The **prefix `Deribit Order Placement App` is frozen forever** — every script matches on it; the environment suffix is what the safety tier gates on. Version may change; prefix and suffix placement may not.
- One green log line at connect when testnet: `TESTNET environment — test.deribit.com`.
- `secrets.example.json` updated with the new fields + comments.
- **Acceptance:** `Environment` absent ⇒ behavior byte-identical except the `— LIVE` title suffix; testnet ⇒ connects/auths against test.deribit.com.

## 2. Commit 2 — harness hooks (app, config-gated)

- **`harness.json`** beside the exe (git-ignored; `.example` shipped; vbproj copy-if-present — the `bridge.json` pattern): `{ "enabled": false }`. Loaded once at startup.
- **Full-form screenshot hotkey** (engine handoff §3b): only when enabled — `KeyPreview = True` + a Ctrl+Shift+S `KeyDown` handler (use `AddHandler` inside the `enabled` branch so the handler does not exist otherwise): read the output path from marker file `verify\.screenshot-target` under `AppContext.BaseDirectory`, `Me.DrawToBitmap` (full form incl. off-screen), save PNG, delete marker, gray log `[HARNESS] screenshot → <path>`. Try/Catch; UI thread only; zero contact with order/receive paths.
- **`AccessibleName`**: set only on controls a commit-4 script actually needs and cannot match by text; enumerate them in the impl report.
- **Acceptance:** `harness.json` absent/false ⇒ no handler hooked, no behavior change. Enabled ⇒ hotkey round-trip works.

## 3. Commit 3 — `OrderCheck` logic harness (new project + testability seams)

- `tools/OrderCheck/OrderCheck.vbproj` — net9.0-windows console, ProjectReference to the app, added to the sln. App vbproj gains `<InternalsVisibleTo Include="OrderCheck" />`.
- **SignalBridge testability seams — behavior-identical extractions/widenings, call-site swap only, each pinned by a fixture in the same commit:**
  - `ParsePayloadJson` → `Friend Shared` (already the separable culture-safe parse post-`8956baa`; just widen).
  - New `Friend Shared DeriveManualSl(isLong As Boolean, stopLevel As Decimal, offset As Decimal) As Decimal` — the act-path manualSL derivation moves into it.
  - New `Friend Shared IsInsideWindowCore(nowUtc8 As TimeSpan, startText As String, endText As String) As Boolean` — extract the window math **preserving the shipped FAIL-CLOSED semantics exactly as they are at `c7cc8a4`** (fixtures PIN shipped behavior; do not re-derive or "fix" anything here — deviations are spec-back material).
  - New `Friend Shared IsDuplicateOf(instanceId As String, signalId As Long, lastInstanceId As String, lastSignalId As Long) As Boolean` — the §4.3 watermark comparison.
- **Fixture set v1 (~15–20, exit 0/1, one line each + summary):**
  1. **The day-first-culture parse fixture** (the named backlog hole behind the `8956baa` culture bug): contract §3 example parsed under forced `en-MY`, `de-DE`, `en-GB`, `en-US` `CurrentCulture` ⇒ `GeneratedUtc` equals the exact UTC instant in all four.
  2. Unparseable `generated_at_utc` ⇒ `DateTime.MinValue` (maximally stale, never a throw).
  3. Missing identity ⇒ parse defaults (`InstanceId=""`, `SignalId=-1`) — the F-1 guard's precondition.
  4. Missing/zero levels on an actionable payload ⇒ 0s — the `refused: levels` precondition.
  5. WEAK payload (`direction:"LONG"`, `confidence:"LOW"`) parses direction-carrying (tier refusal is the gate's job, never inference from direction).
  6. `DeriveManualSl`: LONG = stop − offset; SHORT = stop + offset (the trigger-lands-on-stop identity).
  7. `IsInsideWindowCore`: blank/blank, normal range in/out, midnight wrap in/out, invalid text (pin the shipped fail-closed result), half-blank.
  8. `IsDuplicateOf`: same-instance ≤ watermark = dup; same-instance greater = new; different instance = never dup.
- **Acceptance:** `dotnet run --project tools/OrderCheck` prints per-fixture lines + `OK n/n`, exit 0; any failure exit 1.

## 4. Commit 4 — UI automation scripts (`tools/*.ps1`, ported from the engine)

Common conventions (all scripts): locate the form by the **frozen title prefix**; exit codes `0` ok / `1` app not found / `2` target not found (print ALL candidate names first — the self-diagnostic) / `3` refused or action failed; artifacts to git-ignored `verify/out/` (delete after use — standing rule).

| Script | Notes beyond the engine template |
|---|---|
| `find-mainform.ps1` | `-RequireTestnet` switch: title must contain `TESTNET` else exit 3 |
| `launch-app.ps1` / `stop-app.ps1` | builds Debug, runs `bin\Debug\...exe`, writes a PID file under `verify/`; **REFUSES to launch if any `Deribit Order Placement App` window already exists** (never drive the owner's session); `stop` kills only the PID it launched |
| `click-button.ps1` | **generic tier — carries the trade-deny rule:** refuse (exit 3, name the reason) any button whose Name matches `(?i)buy|sell|reduce|rdc|cancel|trail|sprd|limit|mkt|edit t|^ts$` **or** the maintained list `tools/trade-buttons.txt`; false-positive refusals are safe by design |
| `click-PLACES-ORDER.ps1` | the **only** script without the deny rule; hard-requires the TESTNET title (verify + print it before acting); used only with explicit intent |
| `set-textbox.ps1` | `ValuePattern.SetValue` + **`-CommitViaBlur`** (focus the form after set) — the §9.3 lesson: SetValue alone does NOT fire the commit-on-blur mirrors |
| `toggle-checkbox.ps1` / `select-combo-item.ps1` / `close-popup.ps1` | per the engine table (+ ExpandCollapse before SelectionItem for combos) |
| `inspect-tree.ps1` | dump BoundingRectangles for **Buttons + CheckBoxes + Labels + Edits** (the caption-clip lesson); regex filter; UIA-to-UIA comparisons only (DPI) |
| `read-log.ps1` | `txtLogs` via TextPattern → stdout (callers assert with `Select-String`) — the §9.4/§9.5 disposition-token assert |
| `screenshot-mainform.ps1` / `screenshot-full.ps1` | PrintWindow; marker + `^+s` + poll (10 s) + the **Win11 foreground-steal bypass copied verbatim** from the engine script |
| `write-payload.ps1` / `restore-payload.ps1` | crafts a `verdict_signal.json` variant at the path in the running bin's `bridge.json` (temp+move, mimicking `File.Replace`); **backs up the current file first**, `restore` puts it back; **REFUSES if a DeribitVerdictEngine process is running** (it overwrites every interval — the §9.4/§9.5 engine-stopped protocol, now enforced by script) |

## 5. Commit 5 — gate + hook installer

- `tools/checks/verify-gate.ps1`: build **Release** → build **Debug** → run `OrderCheck` → repo guards (`secrets.json`/`bridge.json`/`harness.json` untracked; `trade-buttons.txt` + `.example` files present) → one `GATE PASSED`/`GATE FAILED` line, exit 0/1. **No UI layer on the gate.**
- `tools/install-hooks.ps1`: writes `.git/hooks/pre-push` invoking the gate (hooks are local — the owner runs the installer once; document in the script header).

## 6. ⚠ SAFETY MODEL (non-negotiable; the engine handoff §5, hardened for live orders)

1. **Two tiers, enforced in code not convention:** the generic click script structurally refuses trade-affecting buttons everywhere (deny rule); only `click-PLACES-ORDER.ps1` can touch them, and it refuses everything except a TESTNET-titled window.
2. **Environment self-documentation:** the title suffix is written by the app from `AppSecrets` — a screenshot can never lie about which world it was in.
3. **Never drive the owner's session:** `launch-app.ps1` refuses when a matching window pre-exists; drive scripts operate on the harness-launched PID's window.
4. **Kill rule:** any script that cannot confirm its required environment exits non-zero and does nothing.
5. **Payload isolation:** payload-writing tests run engine-stopped (script-enforced) with backup/restore — the harness never fights the live emitter.
6. **Human authority:** the harness proves mechanics; the owner flips anything that touches live trading. No script ever changes `secrets.json`, `Environment`, or ARM/START state in live mode.
7. Testnet proves **mechanics, never market behavior** (thin book, fantasy fills) — sizing/geometry conclusions still come from the owner's live-tiny-size sessions.

## 7. Test plan

**Implementer (no exchange needed):** build 0/0 ×2 configs ×5 commits; `OrderCheck` green; exit-code matrix per script (app absent → 1; bogus target → 2 + name dump); `click-button.ps1` pointed at `SELL` → exit 3 with the deny reason; `launch-app.ps1` with the app already open → refusal.

**Owner (testnet key ready):** `Environment=testnet` → launch via `launch-app.ps1` → title `— TESTNET`, green testnet connect line; **automated §9.3 replay** — `set-textbox` ATR Length with `-CommitViaBlur` → `read-log`/readout confirms the commit; **automated §9.4/§9.5** — engine stopped, `write-payload` (actionable variant + out-of-window variant + undersize variant) → `read-log` asserts `refused: window` / `refused: size` tokens → `restore-payload`; `click-PLACES-ORDER.ps1` places a min-size limit far off-market on testnet → `Cancel All` via the same tier → clean teardown; regression — normal live start (Environment absent) looks and behaves exactly as today plus the `— LIVE` suffix.

## 8. Impl report

`docs/impl-report-ui-test-harness.md`, standard format + explicitly: the AccessibleName additions; the four extraction diffs (before/after, call-site swap evidence); the deny-rule test transcript; which engine scripts were copied vs rewritten and why; any UIA quirks found (WinForms exposes control Text as UIA Name — deviations noted).

## Do-not-touch

The receive loop, order/SL edit paths, the 7 SL-context reset sites, the echo discriminator, the bridge gate chain **except** the four named seams (behavior-identical, fixture-pinned, same commit as their fixtures). The endpoint URI swap and title line are the only connect-sequence touches. `FrmIndicators` stays headless — no `Show`, no new UI on it.
