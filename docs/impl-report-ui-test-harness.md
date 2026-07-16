# Impl report — Autonomous build/drive/verify harness (testnet-tiered)

**Date:** 2026-07-16/17. **Spec:** `docs/spec-ui-test-harness.md` (`0ff6895`). **Base:** `0ff6895` (= `c7cc8a4` + the spec commit; tree clean, baseline build 0/0 both configs before commit 1).
**Commits (local, not pushed):** `195e724` (1/5 testnet profile) · `fca5886` (2/5 harness hooks) · `1e1e8ce` (3/5 OrderCheck + seams) · `ea172ac` (4/5 UIA scripts) · `766bddc` (5/5 gate + installer) · `d9b8f6c`+`6d3e358` (5/5 fixup: LF pin for the sh hook template; the second corrects the first — see §7).
**Build:** 0 errors / 0 warnings in **both Release and Debug** verified per commit. Debug is built via the **vbproj directly** — the sln maps `Debug|Any CPU` to the app's **Release** config (pre-existing sln quirk), so `dotnet build <sln> -c Debug` silently skips the config the owner debugs in. The verify gate encodes this.
**Engine handoff:** the spec's named source file `C:\Dev\DeribitVerdictEngine\docs\ui-automation-harness-handoff-orderapp.md` does not exist; the actual document is `ui-automation-harness-recipe.md` (same repo, same content role). Read first, as instructed; skeletons and `tools/*.ps1` were used as the porting source.

---

## 1. Commit 1 — testnet environment profile (`195e724`)

As specced: `AppSecrets` gains optional `Environment` (`"testnet"` case-insensitive; absent/other ⇒ live, existing files byte-identical) + `DeribitTestnet` block; testnet selected with the block missing ⇒ **load error, never a silent live fallback**. New `IsTestnet` / `WsUrl` (`wss://test.deribit.com/ws/api/v2` vs live). Both endpoint consumers swapped: the connect sequence's `New Uri(...)` and `FrmIndicators.DeribitUrl` (Const → property over `AppSecrets.WsUrl`). Title = frozen prefix + version + ` — TESTNET`/` — LIVE`. Green `TESTNET environment — test.deribit.com` line after a successful testnet connect. `secrets.example.json` documents everything.

**Deviation (flagged): secrets load moved `Shown` → `Load`.** The spec says "at Load, after secrets load" — but the shipped code loaded secrets at **Shown**, while `FrmIndicators.StartHeadless()` (called at **Load**) reads `AppSecrets.WsUrl` on a background task immediately. Loading at Shown would have raced the headless quote stream's environment selection nondeterministically. The whole credentials block now runs at the top of Load (handle exists by Load, so `AppendColoredText`'s handle guard passes — the "API credentials loaded" line still reaches the log; runtime-verified). One environment decision, made once, before anything reads it. `_isTestnet` is set **before** credential validation, so a testnet profile with a broken block still titles as TESTNET (can't connect anywhere anyway — it must never *look* like live).

**Note:** `IsTestnet` selection failure mode verified by code-path inspection; the actual testnet connect (green line, auth) is the owner's runtime item (needs the test.deribit.com key — spec's owner prerequisite).

## 2. Commit 2 — harness hooks (`fca5886`)

`harness.json` beside the exe (git-ignored, `.example` shipped, vbproj copy-if-present — the `bridge.json` pattern), read once at startup. Absent/false/malformed ⇒ the KeyDown handler is **never added** and `KeyPreview` stays False (`AddHandler` inside the enabled branch, per spec). Enabled ⇒ Ctrl+Shift+S reads `verify\.screenshot-target` under `AppContext.BaseDirectory`, `Me.DrawToBitmap` (full form incl. off-screen), saves PNG, deletes marker, gray `[HARNESS] screenshot → <path>`. Try/Catch, UI thread only, zero contact with order/receive paths. **Round-trip runtime-verified** (see §6).

**AccessibleName additions (spec asks these enumerated):**

| Control | Form | Why a commit-4 script needs it |
|---|---|---|
| `txtLogs` | frmMainPageV2 | `read-log.ps1` (RichTextBox has no UIA Name from text) |
| `txtAmount` | frmMainPageV2 | §9.5 size-gate setup (`set-textbox.ps1`) |
| `txtCooloff`, `txtCircuitBreaker`, `txtStartTime`, `txtEndTime`, `txtBridgeTiers`, `txtAtrLength`, `txtAtrFallback` | AutoTradeSettings | §9.3/§9.4 gate-config setup — bare TextBoxes expose no UIA Name (set in the existing `InitialiseSettings` wiring loop: `tb.AccessibleName = tb.Name`) |
| `cboBridgeMode` | AutoTradeSettings | `select-combo-item.ps1` mode switching |

AccessibleNames are deliberately **not** gated by `harness.json`: they are inert UIA metadata, and drive scripts need them whenever they run.

## 3. Commit 3 — OrderCheck + the four seams (`1e1e8ce`)

`tools/OrderCheck/OrderCheck.vbproj`: net9.0-**windows8.0** (must match the app's TFM — plain `net9.0-windows` may not reference a project pinned to a higher Windows platform version), `UseWindowsForms` (the referenced assembly is WinForms), Option Strict/Explicit On, added to the sln; app vbproj gains `<InternalsVisibleTo Include="OrderCheck" />`.

**The four seams — all behavior-identical, call-site swap only, pinned by fixtures in the same commit:**

1. **`ParsePayloadJson`** — pure widen `Private` → `Friend`. No body change.
2. **`DeriveManualSl(isLong, stopLevel, offset)`** — extracted from the act path. Before: `Dim manualSl As Decimal = If(isLong, p.StopLevel - _host.StopLimitOffset, p.StopLevel + _host.StopLimitOffset)`. After: the identical `If(...)` lives in the Friend Shared function; call site passes the same three values. One call site.
3. **`IsInsideWindowCore(nowUtc8, startText, endText)`** — the entire body of `IsInsideSessionWindow` after the `Settings Is Nothing` guard moved verbatim (trim, blank-blank=True, TryParse fail-closed=False, inclusive range, midnight wrap). The instance method keeps the guard and passes `DateTime.UtcNow.AddHours(8).TimeOfDay` + the two raw box values. Only observable difference: `UtcNow` is now sampled before (not after) the text checks — microseconds, no behavioral consequence. **Shipped FAIL-CLOSED semantics preserved exactly and pinned by 11 fixtures** (nothing "fixed").
4. **`IsDuplicateOf(instanceId, signalId, lastInstanceId, lastSignalId)`** — the §4.3 comparison `instanceId = lastInstanceId AndAlso signalId <= lastSignalId` extracted; call site swaps in the same four values. One call site (the FSW double-fire suppressor above it is a *seen* check, not the acted watermark, and was not touched).

**Deviation (flagged): two additional widenings.** `ParsePayload` and `PayloadSnapshot` also went `Private` → `Friend` (accessibility only, zero body change). The spec's own fixtures 1–5 assert on **ParsePayload outputs** (`GeneratedUtc`, identity defaults, level defaults, WEAK direction-carrying), which `ParsePayloadJson` alone cannot reach. Without this the named culture fixture would have to re-implement the parse — testing a copy instead of the shipped path, which is exactly what the culture bug taught us not to do.

**Fixtures: 25, `OK 25/25`, exit 0** (negative path exercised via the gate test, §5). Includes: the **day-first-culture parse fixture** — contract §3 example (embedded verbatim as `fixtures/contract-s3.json`) parsed under forced `en-MY`/`de-DE`/`en-GB`/`en-US` ⇒ `GeneratedUtc` = exactly `2026-07-03T14:31:02Z` UTC in all four; unparseable timestamp ⇒ `MinValue`+`TimestampOk=False`, no throw; missing identity ⇒ `""`/`-1` (F-1 precondition); missing levels on actionable ⇒ 0s (`refused: levels` precondition); WEAK LONG parses direction-carrying; `DeriveManualSl` both sides; window blank/normal-in/normal-out/boundaries-inclusive/midnight-wrap-in×2/out/invalid/half-blank×2; watermark equal/less/greater/different-instance.

## 4. Commit 4 — UIA scripts (`ea172ac`)

**Copied vs rewritten:** the Win11 **foreground-steal bypass** and the **PrintWindow** P/Invoke block are copied **verbatim** from the engine's `screenshot-mainform-full.ps1`/`screenshot-mainform.ps1` (per spec). Everything else is a **rewrite on the engine's skeletons**, for three reasons: (a) shared plumbing moved into a dot-sourced `harness-common.ps1` so the frozen title prefix, exit-code convention, TESTNET check and PID-scoping exist **once** (the engine repeats window discovery per script; with 16 scripts and a safety tier, copy-drift in the deny/refuse logic is the failure mode that matters); (b) the order app needs the two-tier safety model the engine doesn't have; (c) multi-window support — the gate config lives on the separate `AutoTradeSettings` top-level window, so drive scripts search **all windows of the harness PID**, not just the main form.

Script inventory = exactly the spec §4 table (find-mainform, launch/stop-app, click-button, click-PLACES-ORDER, set-textbox `-CommitViaBlur`, toggle-checkbox, select-combo-item, close-popup, inspect-tree, read-log, screenshot-mainform, screenshot-full, write-payload/restore-payload) + `harness-common.ps1` + `trade-buttons.txt`.

**Safety hardening beyond the spec table (flagged as judgment calls):**
- **PID-scoping enforced in code:** spec §6.3 says drive scripts "operate on the harness-launched PID's window" — implemented as a hard refusal (`-RequireHarnessPid` in the common `Get-MainForm`): every mutating script (click/set/toggle/combo/close) exits 3 unless the window belongs to the PID in `verify/app.pid`. Observation scripts (find/inspect/read-log/screenshots) may look at any matching session.
- **`toggle-checkbox` refuses ARM controls and `select-combo-item` refuses LIVE-matching items outside a TESTNET title.** The spec puts the deny rule only on click-button, but `chkBridgeArm` + `cboBridgeMode→Live` are exactly the §6.6 "no script ever changes ARM state in live mode" surface, reachable without any button. False-positive refusals are safe by design.
- **`trade-buttons.txt` ships with `START`/`STOP`** (the bridge start/stop button — trade-affecting in live, not caught by the spec regex). The deny check runs against **both** the caption and the `AutomationId`, so a caption change can never sneak a trade button past the tier (the btnAutoSettings "Auto" clip incident shows captions drift).
- `launch-app` also refuses when a **previous harness PID is still alive** (not just when a window exists), and `stop-app` tries `CloseMainWindow` before `Stop-Process` so the app runs its teardown.

**write-payload:** first-write backup (`<path>.harness-backup`; later writes keep the original), temp+move in the payload's own directory (the `File.Replace` mimic), payload path read from the **running** bin's `bridge.json` (exe dir resolved from the window's process; `-BinDir` override; app-default path fallback), fresh invariant ISO timestamp, new `harness-<guid>` instance per write (never a duplicate), parameters for direction/levels/confidence/`SignalState`/armed — the §9.4/§9.5 variants are parameter choices plus local gate-config setup via `set-textbox`.

## 5. Commit 5 — gate + installer (`766bddc` + `d9b8f6c`/`6d3e358`)

`tools/checks/verify-gate.ps1`: Release (sln) → Debug (app vbproj + OrderCheck vbproj, see the sln quirk above) → OrderCheck (`exit 0` **and** `OK n/n` required) → repo guards (`secrets.json`/`bridge.json`/`bridge-state.json`/`harness.json` untracked by basename over `git ls-files`; `trade-buttons.txt` + the 3 `.example` templates present) → one `GATE PASSED`/`GATE FAILED` line. No UI layer (owner decision 3). `tools/install-hooks.ps1` copies the tracked `tools/checks/pre-push` sh template into `.git/hooks` (run once per clone; header documents it). Installed and verified in this clone.

## 6. Test transcript (implementer tier — no exchange contact; app never connected)

Baseline + per-commit builds: **0/0 Release and Debug, all 5 commits.** `OrderCheck`: **OK 25/25 exit 0**.

Exit-code matrix, run against a **real harness-launched session** (`launch-app.ps1` → built Debug, launched, title `Deribit Order Placement App V2.2 — LIVE`, PID recorded; **Connect! never clicked**):

| Test | Result |
|---|---|
| `find-mainform` (app absent) | exit 1 |
| `launch-app` | exit 0 — window found, PID file written |
| `find-mainform` | exit 0, prints title/PID/rect |
| `find-mainform -RequireTestnet` (LIVE title) | exit 3 refusal |
| **`click-button SELL`** | **exit 3 — `REFUSED: button 'SELL' (id 'btnSell') is trade-affecting — matches the trade-deny regex. Use tools/click-PLACES-ORDER.ps1 (TESTNET only) if this is intentional.`** |
| `click-button zzz-bogus` | exit 2 + full name dump (all buttons with AutomationIds — the self-diagnostic) |
| `click-PLACES-ORDER "Limit BUY"` (LIVE title) | exit 3 — TESTNET required, title printed |
| `launch-app` again (window exists) | exit 3 refusal |
| `click-button "Auto Settings"` | exit 0 (settings window opens — not denied, correct) |
| `set-textbox txtAtrLength 7 -CommitViaBlur` | exit 0 — found on the AutoTradeSettings window, blur handed focus back |
| `toggle-checkbox "ARM AUTOTRADE"` (LIVE) | exit 3 ARM refusal |
| `select-combo-item cboBridgeMode Live` (LIVE) | exit 3 LIVE refusal |
| `select-combo-item cboBridgeMode Off` | exit 0 (expand → select → collapse) |
| `read-log` / `-Tail` | exit 0 — full log via TextPattern, token-greppable |
| `inspect-tree -Pattern "ATR now\|Auto Settings"` | exit 0 — incl. `lblAtrNow 'ATR now: 33.90 (indicator)'` (headless ATR engine alive through the endpoint-property change) |
| `screenshot-mainform` | exit 0 (1102×912 PNG) |
| `screenshot-full` | first attempt exit 2 (see quirk 3), after `close-popup AutoTradeSettings`: **exit 0 — 88 943-byte full-form PNG via the commit-2 hotkey round-trip** (visually confirmed: — LIVE title, `[HARNESS]` log lines) |
| `write-payload` (scratch `-BinDir`) | **exit 3 — the owner's DeribitVerdictEngine was genuinely running (PID 31904) and the §6.5 kill rule fired for real.** Left the owner's engine alone. |
| `restore-payload` | exit 3, same refusal |
| `stop-app` / again | exit 0 (graceful close) / exit 1 |

Screenshots deleted after use (standing rule). **Not tested (and why):** write-payload's happy path (engine running — refusing to stop the owner's live emitter *is* the harness behaving correctly; the crafted JSON construction was validated standalone and the shape is what the fixtures pin); the testnet connect line + `— TESTNET` title (owner prerequisite: test.deribit.com key); the §7 owner sequence.

## 7. UIA quirks + lessons (deviations from naive expectations)

1. **PS 5.1 reads BOM-less files as ANSI** — em-dashes in comments mangled into cp1252 *smart quotes*, which PowerShell parses as string terminators: 10 of 16 scripts failed to parse at all. All `.ps1` are saved **UTF-8 with BOM**. (The engine's scripts are pure ASCII, which is why it never hit this.)
2. **WinForms → UIA mapping:** control **Text** → UIA `Name`; control **designer name** → UIA `AutomationId` (so `btnSell` is matchable even if the caption changes — the deny rule uses both); **`AccessibleName` → UIA `Name`** for name-less controls (TextBoxes); a **Label is `ControlType.Text`**, not "Label"; a RichTextBox exposes `TextPattern` fine but separates lines with **bare CR** (`read-log -Tail` splits on all three conventions).
3. **Owned-window focus steals the hotkey:** with AutoTradeSettings open, the foreground bypass brings the *app* forward but keyboard focus lands on the owned settings window, and the **main form's KeyPreview never sees Ctrl+Shift+S** → screenshot-full times out. Recipe: `close-popup AutoTradeSettings` first; the timeout message now says exactly that.
4. **The sln `Debug|Any CPU` → app `Release|Any CPU` mapping** (pre-existing): any "sln Debug" build skips the owner's debug config. Gate + launch-app build the vbproj directly.
5. **`.gitattributes` incident (owner should know):** commit `d9b8f6c` accidentally **replaced** the repo's standard VS `.gitattributes` instead of appending the one LF rule; `6d3e358` restores the original 63 lines verbatim with the rule appended (net diff vs pre-incident = additions only). The rule itself matters: autocrlf would CRLF the sh hook template on fresh checkout and break `.git/hooks/pre-push`.

## 8. What the owner runs next (spec §7 owner tier)

Register the test.deribit.com key → `secrets.json`: `"Environment": "testnet"` + `DeribitTestnet` block → `tools/launch-app.ps1` → expect `— TESTNET` title + green testnet connect line → automated §9.3 replay (`set-textbox txtAtrLength ... -CommitViaBlur` → readout) → §9.4/§9.5 with the engine **stopped** (`write-payload` variants + `set-textbox` window/size setup → `read-log | Select-String "refused: window"` / `"refused: size"` → `restore-payload`) → `click-PLACES-ORDER` min-size far-off-market limit + `Cancel All` through the same tier → `stop-app` → regression: normal live start looks identical plus ` — LIVE`. Install the push gate once: `tools/install-hooks.ps1`.
