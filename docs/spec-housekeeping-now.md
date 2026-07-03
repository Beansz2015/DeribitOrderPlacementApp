# Implementation Spec — Safe Housekeeping (run now; independent of HIGH testing)

**Source:** `docs/CODE_AUDIT.md`; carved out of `docs/spec-medium-housekeeping.md`.
**Project:** DeribitOrderPlacementApp — .NET 9 WinForms, VB.NET, Deribit BTC-PERPETUAL.

## Why this spec exists / when to run

Each item below was checked and **does not touch any function changed by the HIGH fixes (#2–#7), nor the live connect / receive / quote-handling paths** that HIGH runtime testing exercises. So they can be implemented **now — before, or in parallel with, the owner's HIGH testing** — without confounding it (a later HIGH test failure can't be caused by these).

**The line drawn:** deleting *entirely-unreachable* code (functions never called) is safe to move here; *editing* any function that runs during HIGH testing is not — those stay in `spec-medium-housekeeping.md`.

The *other* housekeeping items (throw-dead-code, duplicate account-summary, heartbeat-twice, magic numbers, ProcessEstimationData/id-890) stayed in `spec-medium-housekeeping.md` because they edit HIGH-modified functions or the live receive/connect path — those remain gated behind HIGH testing.

**Start point:** branch from the current `master` tip. Ideally after the HIGH **#3 follow-up** commit lands, but none of these items overlap HIGH code (different functions/files), so it's conflict-free either way.

> Line numbers are approximate (HIGH edits shifted them). Locate by symbol/snippet, not absolute line.

## Ground rules

1. Implement directly. **One local commit per item**, message referencing it (e.g. `Housekeeping H4: remove dead keepAliveTimer field`).
2. **Do NOT push.** Owner pushes after review + test.
3. **Build gate:** `dotnet build DeribitOrderPlacementApp.sln` → 0 errors after each item. Report new warnings.
4. **H2 (delete the obsolete form) needs explicit owner confirmation before doing it** — recommended, and verified safe (see item).
5. **Behavior-preserving:** none of these should change the runtime behavior of any live trading path. If you find an item is *not* as isolated as described, stop and flag it rather than proceeding.
6. Produce the post-implementation report at the end (see bottom) for the owner to paste back for review.

## Items

### H1 — Remove `btnChangeForm` (it opens the obsolete form)
**Where:** `frmMainPageV2.vb`, `btnChangeForm_Click` (~line 3572): `frmMainPage.Show()` / `Me.Hide()`.
**Change:** delete the handler, and remove the `btnChangeForm` control + its designer wiring from `frmMainPageV2.Designer.vb`. This is the **only** source reference to the obsolete `frmMainPage` (verified by reference search).
**Why safe:** button-handler/UI region; nothing in the auth/receive/quote paths HIGH changed.
**Acceptance:** build clean; button gone; no remaining `frmMainPage.Show()` in source.

### H2 — Delete the obsolete `frmMainPage.*`  *(requires owner confirmation)*
**Depends on:** H1 (remove its only reference first).
**Verified safe:** startup form is `frmMainPageV2` (`My Project/Application.myapp` → `<MainForm>frmMainPageV2</MainForm>`); the only code reference was `btnChangeForm`. The `bin/…/frmMainPageV2.vb` hits in a reference search are build-copied artifacts, not references.
**Change:**
- Delete `frmMainPage.vb`, `frmMainPage.Designer.vb`, `frmMainPage.resx`.
- Remove the `<Compile Update="frmMainPage.vb">` item from `DeribitOrderPlacementApp.vbproj`.
- While in the `.vbproj`: also drop the stray `<CopyToOutputDirectory>Always</CopyToOutputDirectory>` on the **`frmMainPageV2.vb`** compile item. It copies source `.vb` into `bin/` on every build (that's why `frmMainPageV2.vb` appears under `bin/`) and serves no runtime purpose. Keep the file; just remove the copy metadata.
**Why safe:** obsolete form, never shown, not the startup form, no references after H1; not part of HIGH testing.
**Acceptance:** build clean; `frmMainPage.*` gone; rebuild no longer copies `frmMainPageV2.vb` into `bin/`; app still launches to `frmMainPageV2`.

### H3 — Dead `Else` branches in the reduce handlers
**Where:** `frmMainPageV2.vb`, `btnReduceLimit_Click` and `btnReduceMarket_Click`: `If TradeMode = False … ElseIf TradeMode = True … Else "No position to reduce"`. The `Else` is unreachable for a Boolean.
**Change:** simplify each to `If/Else` (drop the dead `Else`). **Preserve the existing direction mapping exactly** (buy reduces a short, sell reduces a long).
**Why safe:** button handlers, separate from HIGH paths.
**Acceptance:** build clean; reduce buttons still send the correct direction.

### H4 — Remove the dead `keepAliveTimer` field
**Where:** `frmMainPageV2.vb`, `Private keepAliveTimer As System.Timers.Timer` — declared, never used.
**Change:** delete the declaration (confirm zero references first; there are none).
**Why safe:** unused declaration; zero behavioral impact.
**Acceptance:** build clean.

### H5 — Non-blocking close in `FrmIndicators_FormClosing`
**Where:** `FrmIndicators.vb`, `FrmIndicators_FormClosing`: `client.CloseAsync(...).Wait()` on the UI thread (deadlock risk on shutdown).
**Change:** replace the blocking `.Wait()` with a bounded wait (e.g. `.Wait(TimeSpan.FromSeconds(2))`) or a fire-and-forget close. Keep `pollTimer.Stop()` and the existing null/state checks.
**Why safe:** shutdown-only path; not the `#2`/`#7` functions (`ProcessAutomatedSignal`, `UpdateSignals`, `BacktestSignals`) HIGH touched.
**Acceptance:** build clean; form closes without hanging.

### H6 — Delete the dead reconnect subsystem  *(moved from MEDIUM #8)*
**Where:** `frmMainPageV2.vb` — `WebSocketCalls` and `ReconnectWebSocket`.
**Verified dead:** these two only call each other; nothing else reaches them. Live reconnect uses `ConnectToWebSocketDirectly` + `HandleWebSocketDisconnect` (confirmed by reference search). Re-confirm before deleting.
**Change:** delete both functions entirely — including the `WebSocketCallsBackgroundTask` block and any `throw`-then-dead-code lines inside them. Do **not** delete the functions they *call* (`ConnectToWebSocketDirectly`, `HandleWebSocketDisconnect`, `ReceiveWebSocketMessagesAsync`, `MonitorAuthentication`, `AuthorizeWebSocketConnection`, the `SubscribeTo*` functions) — those are used by the live path.
**Why safe:** entirely unreachable. Removing code never invoked from any live path cannot change anything HIGH testing exercises.
**Acceptance:** build clean; connect, disconnect (drop network), and auto-reconnect still work via the live path.

### H7 — `Option Strict On` for new files (policy)  *(moved from MEDIUM #12)*
**Change:** any **new** file starts with `Option Strict On` / `Option Explicit On` (as `AppSecrets.vb` does). Append a one-line note recording this convention to `docs/CODE_AUDIT.md` under #12. Do **not** flip Option Strict on existing files (that surfaces hundreds of conversion errors — out of scope).
**Why safe:** touches no existing live code — a going-forward convention plus a docs note.
**Acceptance:** new files compile under Option Strict On; no existing file changed; build unaffected.

## Implementation report (REQUIRED — produced AFTER implementation, for review)

Implement directly (raise only the H2 delete-confirmation with the owner). When done, committed locally, and the build is green, produce a short report for the owner to paste back so the reviewer can check the code. Include:

1. **Per-item summary (H1–H7):** what changed, final file + line numbers, the actual snippet/diff.
2. **H2 confirmation:** that the owner approved the delete; what was removed; proof the build still launches to `frmMainPageV2`.
3. **Deviations** from this spec, with reasons.
4. **Build status:** `dotnet build` result; new warnings.
5. **Commits:** local hashes + messages (one per item).
6. **Anything that turned out *not* to be isolated** from HIGH (should be none — flag if otherwise).
7. **Confirmation:** committed locally per item, did **not** push, build green, no live-trading behavior changed.

**Do not push to remote** — the owner pushes after review + test.
