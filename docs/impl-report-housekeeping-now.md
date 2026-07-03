# Implementation Report — Safe Housekeeping (now)

**Spec:** `docs/spec-housekeeping-now.md`
**Branch:** `housekeeping-now` (off `master` @ `d06f919`)
**Build:** `dotnet build DeribitOrderPlacementApp.sln` → **Build succeeded, 0 Warnings, 0 Errors** (incl. a final `--no-incremental` rebuild).
**Push status:** **Not pushed.** Eight local commits (H1–H8), all committed locally.

---

## 1. Per-item summary

### H1 — Remove `btnChangeForm` (opened obsolete `frmMainPage`)  ✅ committed `1b89f08`
**Files:** `frmMainPageV2.vb`, `frmMainPageV2.Designer.vb`.
- Deleted the handler (was ~`frmMainPageV2.vb:3571`):
  ```vb
  Private Sub btnChangeForm_Click(sender As Object, e As EventArgs) Handles btnChangeForm.Click
      frmMainPage.Show()
      Me.Hide()
  End Sub
  ```
- Removed all four designer references in `frmMainPageV2.Designer.vb`: the `btnChangeForm = New Button()` line, the `' btnChangeForm` config block in `InitializeComponent`, `Controls.Add(btnChangeForm)`, and `Friend WithEvents btnChangeForm As Button`.
**Acceptance:** build clean; button gone; the only source `frmMainPage.Show()` is gone.

### H2 — Delete obsolete `frmMainPage.*` + clean `.vbproj`  ✅ committed `206bddc`  *(owner-approved)*
**Owner confirmation:** approved before doing it.
**Removed:** `frmMainPage.vb` (3835 lines), `frmMainPage.Designer.vb` (2753), `frmMainPage.resx` (147).
**`.vbproj`:** dropped the `<Compile Update="frmMainPage.vb">` item **and** the stray `<CopyToOutputDirectory>Always</CopyToOutputDirectory>` that was on the `frmMainPageV2.vb` compile item. Both `<Compile Update>` items were removed entirely (SDK-style project compiles `.vb` by default; neither carried any other needed metadata).
**Proof app still launches to `frmMainPageV2`:**
- `My Project/Application.myapp:4` → `<MainForm>frmMainPageV2</MainForm>`
- `My Project/Application.Designer.vb:36` → `Me.MainForm = Global.DeribitOrderPlacementApp.frmMainPageV2`
- Build succeeds *after* deleting the whole form → nothing else referenced it. Post-change source search for `frmMainPage` (bare): **0 matches**.
**Note on `bin/`:** removing the copy metadata means future rebuilds no longer copy source `frmMainPageV2.vb` into `bin/`. Pre-existing copies under `bin/` from older builds are stale, git-ignored artifacts; left untouched (a clean rebuild won't recreate them).

### H3 — Drop dead `Else` in the reduce handlers  ✅ committed `bb5338f`
**File:** `frmMainPageV2.vb` — `btnReduceLimit_Click` (`:3681`), `btnReduceMarket_Click` (`:3716`).
Each had `If/ElseIf/Else` on the Boolean `TradeMode`; the trailing `Else` ("No position to reduce" + `Return`) is unreachable. Simplified to `If/Else`. Direction mapping and comments preserved verbatim:
- Limit: `TradeMode = False → "buy"`, else `"sell"`.
- Market: `TradeMode = True → "sell"`, else `"buy"`.
(Net: buy when `TradeMode=False`, sell when `TradeMode=True` — unchanged.)

### H4 — Remove dead `keepAliveTimer` field  ✅ committed `0d37d66`
**File:** `frmMainPageV2.vb`. Deleted `Private keepAliveTimer As System.Timers.Timer` (was `:25`). Confirmed zero references before and after. `lastMessageTime` now sits at `:24`.

### H5 — Bounded close in `FrmIndicators_FormClosing`  ✅ committed `d53dc02`
**File:** `FrmIndicators.vb:1652`.
```diff
- client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait()
+ ' Bounded wait on shutdown: a slow/unresponsive close must not hang the UI thread.
+ client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait(TimeSpan.FromSeconds(2))
```
`pollTimer.Stop()` and the `client IsNot Nothing AndAlso client.State = WebSocketState.Open` guard are unchanged.

### H6 — Delete dead reconnect subsystem  ✅ committed `1a8ce1f`
**File:** `frmMainPageV2.vb`. Re-confirmed dead before deleting: `WebSocketCalls` and `ReconnectWebSocket` only called each other; the sole remaining mention is a commented-out line. Deleted both functions entirely (80 + 22 = **102 lines**, deletion-only diff), including the `WebSocketCallsBackgroundTask` block and the `Throw New Exception(...)`-then-dead-code lines.
**Left intact** (live path, called by the deleted functions but used elsewhere): `ConnectToWebSocketDirectly`, `HandleWebSocketDisconnect`, `ReceiveWebSocketMessagesAsync`, `MonitorAuthentication`, `AuthorizeWebSocketConnection` (now `:241`), and the `SubscribeTo*` functions.

### H7 — `Option Strict On` for new files (policy + docs note)  ✅ committed `10b7040`  *(owner approved committing as-is)*
**File:** `docs/CODE_AUDIT.md`, under #12. Appended a one-line convention note:
> **Convention adopted (housekeeping H7, 2026-06-22):** every *new* `.vb` file begins with `Option Strict On` / `Option Explicit On` (as `AppSecrets.vb` does). Existing files are intentionally left unflipped — turning Option Strict on across legacy code surfaces hundreds of conversion errors and is out of scope.

No new `.vb` files were introduced by this change set, so there is no new-file code to compile under Option Strict On; the convention is going-forward. No existing file's Option setting was changed. Build unaffected.

### H8 — Remove standalone dead commented-out code blocks  ✅ committed `ebbf2f7`  *(owner requested)*
**File:** `frmMainPageV2.vb`. Removed two superseded old implementations, both sitting **between** methods (not inside any live function body), so no live/HIGH-path function body is edited:
- Old commented-out `btnConnect_Click` (the live handler at the same name replaced it) — also cleared the last stray `' Await WebSocketCalls()` comment left after H6.
- Old `EnableHeartbeat` stub (replaced by `EnableDeribitHeartbeatEnhanced`).
Comment-only deletion (28 lines, pure deletion — no added content). Build clean.

**Deliberately NOT removed** (left for the HIGH-gated MEDIUM phase, or kept by intent):
- Inline commented fragments *inside* live function bodies in the receive/quote/order path (e.g. `frmMainPageV2.vb` ~1593–1600, 1758–1761, 1874–1877, 3087–3090, 3330–3336; `FrmIndicators.vb` 468–474, 486–489, …). Removing these is behavior-safe but edits HIGH-path function bodies, which this spec deliberately avoids until HIGH testing is done.
- Valuable rationale kept verbatim: the token-refresh guard note (`:29–32`) and the `reduce_only` OTOCO NOTE (`:2298–2299`).
- `ApplicationEvents.vb` — standard VS-generated event-handler scaffolding (the "Example:" stubs), not leftover dead code; kept by convention.

---

## 2. H2 confirmation
Owner approved the delete before it ran. Removed `frmMainPage.vb`/`.Designer.vb`/`.resx` and the `.vbproj` items. Startup form remains `frmMainPageV2` (myapp + Application.Designer.vb both confirm); build green after deletion; 0 bare `frmMainPage` references remain in source.

## 3. Deviations
- **H7 commits a previously-untracked `docs/`.** `docs/` was **entirely untracked** at the start (`?? docs/`). The H7 commit `10b7040` adds `docs/CODE_AUDIT.md` to version control for the first time. That file quotes the live (throwaway) `ClientId`/`ClientSecret` verbatim (`CODE_AUDIT.md:19-20`). **Owner decision:** commit as-is — the key is for a throwaway sub-account and will be rotated out at production cutover; the secret is already in public git history via the original source, so this adds no new exposure. Only `CODE_AUDIT.md` was committed; the other `docs/` files (specs, this report) remain untracked — say the word to add them too.
- **H8 added beyond the spec** at owner request ("clean any dead blocks you see"). Scoped to standalone dead commented-out code blocks; inline fragments inside live HIGH-path functions were deliberately left (see H8).

## 4. Build status
`dotnet build DeribitOrderPlacementApp.sln` after **every** item: **Build succeeded, 0 Warnings, 0 Errors.** Final `--no-incremental` rebuild also clean. No new warnings introduced.

## 5. Commits (local, newest first)
```
ebbf2f7  Housekeeping H8: remove standalone dead commented-out code blocks
10b7040  Housekeeping H7: Option Strict On convention note in CODE_AUDIT.md
1a8ce1f  Housekeeping H6: delete dead reconnect subsystem
d53dc02  Housekeeping H5: bounded close in FrmIndicators_FormClosing
0d37d66  Housekeeping H4: remove dead keepAliveTimer field
bb5338f  Housekeeping H3: drop dead Else in reduce handlers
206bddc  Housekeeping H2: delete obsolete frmMainPage.* and clean vbproj
1b89f08  Housekeeping H1: remove btnChangeForm (opened obsolete frmMainPage)
```
One commit per item (H8 added at owner request). Code diffstat vs `master`: 7 source files changed, +5 / −6905 (incl. H8's 28-line comment removal). H7 additionally adds `docs/CODE_AUDIT.md` (+1 line).

## 6. Anything not isolated from HIGH
None. No item touched a HIGH-modified function (`ProcessAutomatedSignal`, `UpdateSignals`, `BacktestSignals`, `HandleQuoteUpdates`, `HandleTokenRefreshResponse`, the token-refresh path) or the live connect/receive/quote path. H6 removed only entirely-unreachable code; the live reconnect path (`ConnectToWebSocketDirectly` + `HandleWebSocketDisconnect`) is untouched.

## 7. Confirmation
Committed locally, one commit per item for H1–H8. **Did not push.** Build green throughout (clean `--no-incremental` rebuild included). No live-trading behavior changed.
