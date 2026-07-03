# Implementation report — spec-resilience.md

**Date:** 2026-07-03
**Implementer:** Fable 5 (high)
**Base:** `3f6394e` on `master` (decouple-v2 cleanup landed; tree clean). Anchors matched the spec; no drift worth noting.
**Commits:** `0349b17` (1/6), `a54fd69` (2/6), `2600ce0` (3/6), `269e4c6` (4/6), `282d86a` (5/6), `a42aa76` (6/6). Build `dotnet build DeribitOrderPlacementApp.vbproj` = **0 errors / 0 warnings before each commit** (verified six times, once per commit; baseline before commit 1 also 0/0). Committed locally, not pushed.

Line numbers refer to the file state **after** all six commits unless marked "was".

---

## Commit 1 — `0349b17` — F5+F7: fragment-safe receive, real dead-link detection, delete the fake one

- **KeepAliveTimeout** (`frmMainPageV2.vb:720-722`): added verbatim after the `KeepAliveInterval` line in `ConnectToWebSocketDirectly`. Note: setting `KeepAliveTimeout` also switches `ClientWebSocket` from unsolicited-pong to active ping/pong — that is the mechanism F7 relies on (missing pongs → aborted socket → `WebSocketException` in the receive loop → existing reconnect path).
- **Receive loop** (`frmMainPageV2.vb:775-846`): `Dim sb As New StringBuilder()` before the `While`; body rewritten exactly as the spec block — Close-frame check unchanged (F6 semantics intact), fragment accumulation, `Continue While` until `EndOfMessage AndAlso Text`, then `response = sb.ToString()` / `sb.Clear()`. The eleven-handler dispatch block is byte-identical and in the same order (`HandleHeartbeat` … `HandlePlacementResponse`, `HandleUnhandledJsonRpcError`). All three `Catch` blocks untouched.
- **Deleted:** the trailing 75-s timeout `If` (was after the `Try/Catch`), the whole `MonitorConnectionHealth` function (was :886-907), the `lastMessageTime` field (was :25), and its three write sites (loop start, post-receive, `HandleIndexUpdates` was :1289).
- **Auth response read** (`frmMainPageV2.vb:480-488`, `AuthorizeWebSocketConnection`): single `ReceiveAsync` replaced with the minimal `Do … Loop Until result.EndOfMessage` StringBuilder accumulation, then parse. Same 4 KB buffer.

**Greps (spec acceptance):** `lastMessageTime` → 0 hits; `MonitorConnectionHealth` → 0 hits. Dead-link test (adapter off → reconnect within ~50 s) is an owner runtime test.

Diff: 18 insertions, 42 deletions.

## Commit 2 — `a54fd69` — F8 remaining half: placements consume credits

`rateLimiter?.ConsumeCredits()` with the spec's comment added directly before the order-payload `Await SendWebSocketMessageAsync(...)` in:

- `ExecuteOrderAsync` (:2812)
- `SendReduceOrderAsync` (:3023)
- `StopLossForTrailingOrderAsync` (:3537)

Consume-only — no gating added anywhere; the three paths' behavior is otherwise unchanged (each function's other sends — cancels, edits — untouched). Connect-arming was **not** re-added (already in `9cb97f4`, per the spec's status update).

Diff: 3 insertions, 0 deletions.

## Commit 3 — `2600ce0` — F9: MonitorAuthentication bound to its connection

`frmMainPageV2.vb:856-858`: `Dim mySocket As ClientWebSocket = webSocketClient` captured before the loop; `While` condition is the spec's `mySocket Is webSocketClient AndAlso mySocket IsNot Nothing AndAlso mySocket.State = WebSocketState.Open`. Loop body unchanged. After a reconnect swaps the field, the old loop's next wake fails `mySocket Is webSocketClient` and exits.

Diff: 2 insertions, 1 deletion.

## Commit 4 — `269e4c6` — F12: exception backstops + crash.log

`ApplicationEvents.vb` made real: `Option Strict On` / `Option Explicit On` added; `Startup` wires `AppDomain.CurrentDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`; `MyApplication_UnhandledException` logs and sets `e.ExitApplication = False`; `OnUnobservedTask` logs and `SetObserved()`; `WriteCrashLog` appends to `crash.log` (relative path → app working directory) and never throws.

**Signature adjustments (spec anticipated these — report what compiled):**

1. `OnDomainUnhandled` takes **`System.UnhandledExceptionEventArgs`** (qualified). The file-level `Imports Microsoft.VisualBasic.ApplicationServices` would otherwise capture the unqualified name (file imports shadow project imports in VB resolution) and break the `AddressOf` delegate match. A comment in the file records this.
2. Everything else compiled exactly as the spec wrote it: `StartupEventArgs` (resolves via the file import), `ApplicationServices.UnhandledExceptionEventArgs` (resolves via the default `Microsoft.VisualBasic` project import), `UnobservedTaskExceptionEventArgs` and `TaskScheduler` (default `System.Threading.Tasks` project import), `IO.File` (default `System` import).

**Cosmetic deviation:** the stub's commented-out `ApplyApplicationDefaults` example block was dropped while making the file real; the event-list comment header was kept.

The debugger-only deliberate-throw test (spec acceptance) is an owner runtime test — no test line was ever committed.

Diff: 36 insertions, 17 deletions.

## Commit 5 — `282d86a` — restart position restore (display) + leverage-pending polish

- **Field** (:1854-1855): `positionRestoreAnnounced` under the position-model fields with a one-line comment. **Reset** (:724): `positionRestoreAnnounced = False` in `ConnectToWebSocketDirectly` with the fresh-instance setup (directly after `cancellationTokenSource = New CancellationTokenSource()`).
- **Announce block** (:3915-3926, `ProcessPositionData`): inserted verbatim from the spec directly after the position-model field-update block — once per connection, only when `positionSize <> 0`; log line + `lblOrderStatus` "In Position" + avg entry into `txtPlacedPrice` via `UiInvoke`. `placedPrice` (engine field) deliberately **not** seeded, per spec. `btnRefreshLiveData` re-entry can't re-announce (flag already set).
- **Leverage pending** (:3970-3972 label, :3988 log line): `accountBalanceUSD` hoisted out of the `If positionSize.HasValue AndAlso markPrice.HasValue` block (initialized `0`); when it is still `0` (equity not yet received) the label shows `L.Lev: pending` and the seed's log line shows `Leverage: pending` instead of `0.00x`.

**Judgment call to review:** the spec's change 3 names "the same function's leverage display"; the problem statement specifically calls out the *log line* showing `Leverage: 0.00x`. I applied the pending-form to **both** the label and the `LIVE position data` log line. If the log line should keep `0.00x`, revert the one-line change at :3988.

Diff: 21 insertions, 3 deletions.

## Commit 6 — `a42aa76` — window-X close runs the real shutdown

- **`CreateParams` override deleted** entirely (was :4980-4987; CS_NOCLOSE gone — X and Alt+F4 work again).
- **`frmMainPageV2_FormClosing`** (:4126-4145): spec's handler verbatim — `shutdownStarted` re-entry guard, `isClosing = True` first (suppresses the reconnect path during teardown), cancel, bounded `CloseAsync(...).Wait(TimeSpan.FromSeconds(2))` only when the socket is Open, dispose both, swallow-all `Catch`. The ergonomics note (config save goes at the TOP of this handler) is in the comment above it.
- **`btnClose_Click`** (:4147-4149): body is just `Me.Close()`. **Deviation from the spec's letter:** the `Async` modifier was dropped — with no remaining `Await`, keeping it would emit BC42356 (async method lacks await) and break the 0-warning gate. `Handles btnClose.Click` unchanged.

Grep: `Me.Close()` now has exactly one call site (btnClose); no `Application.Exit` calls (one pre-existing commented occurrence at :422).

Diff: 17 insertions, 35 deletions.

---

## Acceptance summary (implementer-verifiable)

| Assertion | Result |
|---|---|
| Build green ×6 (once per commit) + baseline | ✅ 0 errors / 0 warnings each time |
| `lastMessageTime` / `MonitorConnectionHealth` | ✅ 0 hits each |
| Dispatch block order/content unchanged (commit 1) | ✅ diff shows dispatch lines only as context |
| Three placement paths consume; no other behavior change | ✅ 3 insertions only, each directly above the payload send |
| Old auth-monitor loop exits when superseded | ✅ by construction (`mySocket Is webSocketClient` fails after field swap) |
| Announce-once: flag set before any await, reset only at connect | ✅ :3917-3918, :724 |
| Single shutdown path, bounded 2 s, `isClosing` set first | ✅ :4126-4145 |

Owner runtime tests (spec test plan 1-6: dead link, limits at connect, restore, X-close, crash log, full trade-cycle regression) remain to be run on the test sub-account.

---

## Deviations

1. **Commit 4:** `OnDomainUnhandled` parameter qualified as `System.UnhandledExceptionEventArgs` (compile requirement, see above). Stub's `ApplyApplicationDefaults` example comment dropped.
2. **Commit 5:** pending-form applied to the log line as well as the label (judgment call, flagged above).
3. **Commit 6:** `btnClose_Click` loses `Async` (0-warning gate; no `Await` remains).

All other inserted blocks are byte-for-byte the spec's code.

---

## Suspicious nearby — deliberately not touched

1. **Multi-byte UTF-8 split across fragments:** both accumulation sites decode each fragment independently (`Encoding.UTF8.GetString` per fragment, matching FrmIndicators' proven pattern per spec). A multi-byte character split exactly on a fragment boundary would decode as U+FFFD. Deribit JSON-RPC payloads are ASCII in practice, so this is theoretical; a `Decoder`-based accumulator would close it. Housekeeping candidate.
2. **Auth read still consumes "the next message"** (`AuthorizeWebSocketConnection`): the single-response assumption is pre-existing and safe (no subscriptions are active before auth); only fragmentation was fixed, per spec scope.
3. **`ConnectToWebSocketDirectly` is also the reconnect path** — `AuthorizeWebSocketConnection` is called there *before* the receive loop starts, so its inline `ReceiveAsync` never races the loop (loop starts at :758). Unchanged shape, just noting it held after the rewrite.
4. **`MonitorAuthentication` heals but doesn't kill on reconnect *failure*:** if a reconnect never succeeds, the last loop still exits via socket-state; no leak. No change needed — noted for completeness while verifying F9.
5. **`InitializeRateLimits` vs `InitializeRateLimitsAfterAuth` near-duplication** — known housekeeping item, left per spec's explicit instruction.
6. **`SLTriggered`/trailing flags during the new FormClosing teardown:** shutdown doesn't reset trade context (it never did via btnClose either); irrelevant post-process-exit. Nothing added, keeping the handler minimal per spec.
7. **`crash.log` / `AutoTradeLog.txt` relative paths** resolve to the working directory (bin dir under VS, install dir otherwise). Pre-existing convention (`AutoTradeLog.txt` does the same); the ergonomics pass may want a stable location.
