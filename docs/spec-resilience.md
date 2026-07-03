# Spec — Resilience pass (connection death, exception backstops, shutdown, restart restore)

**Date:** 2026-07-03
**Source:** `CODE_AUDIT_FABLE5.md` F5/F7/F8/F9/F12 + two owner requests (2026-07-02). Read `HANDOVER-2.md` §4 invariants first.
**Recommended implementer:** Opus at **high**. Rewrites the receive-loop body and the shutdown path — invariant-sensitive.
**Sequencing:** after decouple-v2 is pushed; **before** the tie-in implementation (both touch `frmMainPageV2.vb`; this one owns the connect/receive/shutdown regions).
**Ground rules:** standing — build 0/0 per commit, local only, never push, scope discipline, impl report (`docs/impl-report-resilience.md`). Locate by symbol; line numbers drift.

---

## Commit 1 — F5 + F7: fragmentation-safe receive, real dead-link detection, delete the fake one

**Problems.** (a) `ReceiveWebSocketMessagesAsync` ignores `EndOfMessage` — a fragmented or >64 KB message decodes as broken JSON halves silently swallowed by every handler. (b) The 75-s silence check at the bottom of the loop is unreachable (it runs only right after a message updated `lastMessageTime`). (c) `MonitorConnectionHealth` — the ping loop that would detect a dead link — is defined but never started. (d) No `KeepAliveTimeout`, so protocol pings never abort on missing pongs. Net: the app can sit "ONLINE" on a dead socket.

**Changes.**
1. In `ConnectToWebSocketDirectly`, after `webSocketClient.Options.KeepAliveInterval = TimeSpan.FromSeconds(30)` add:
   ```vb
        ' F7: abort ReceiveAsync when pongs stop - dead links now surface as a WebSocketException
        ' in the receive loop, which lands in the existing reconnect path.
        webSocketClient.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20)
   ```
2. Rewrite the receive-loop body (fragment accumulation, FrmIndicators' proven pattern). Before the `While`: `Dim sb As New StringBuilder()`. The `Try` body becomes:
   ```vb
                Dim result = Await webSocketClient.ReceiveAsync(
                New ArraySegment(Of Byte)(buffer),
                cancellationTokenSource.Token)

                If result.MessageType = WebSocketMessageType.Close Then
                    ' Audit2 F6: a server-initiated close is a dead connection - schedule recovery.
                    ' isClosing (checked below) still suppresses this during user-initiated shutdown.
                    AppendColoredText(txtLogs, "Server closed connection - scheduling reconnect", Color.Yellow)
                    reconnectNeeded = True
                    Exit While
                End If

                ' F5: accumulate fragments; dispatch only complete text messages (a >64KB or
                ' intermediary-fragmented frame previously decoded as broken JSON halves).
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))
                If Not (result.EndOfMessage AndAlso result.MessageType = WebSocketMessageType.Text) Then Continue While

                Dim response = sb.ToString()
                sb.Clear()

                ' (existing handler dispatch block - UNCHANGED, same order:
                '  HandleHeartbeat … HandlePlacementResponse, HandleUnhandledJsonRpcError)
   ```
   The `Catch` blocks stay as they are. **Delete** the trailing 75-s timeout `If` block after the `Try/Catch` (unreachable by construction), the whole unused `MonitorConnectionHealth` function, the `lastMessageTime` field, and its three write sites (loop start, post-receive, `HandleIndexUpdates`). Keep everything else in the loop byte-identical — especially the dispatch order and the F6 `reconnectNeeded` semantics.
3. Same accumulation fix, minimal form, for the one other raw read — the auth response in `AuthorizeWebSocketConnection`: loop `ReceiveAsync` appending to a `StringBuilder` until `result.EndOfMessage`, then parse.

**Acceptance.** Build green. Regression: all message handling identical in normal operation. Dead-link test: with the app connected and idle, disable the network adapter → within ~50 s (interval + timeout) the receive loop logs a WebSocket exception and the reconnect sequence starts (attempts fail until the adapter returns, then recover). Grep: `lastMessageTime` zero hits; `MonitorConnectionHealth` zero hits.

## Commit 2 — F8 (remaining half): placements consume credits

**Status update (2026-07-03):** the connect-arming half of F8 was **already implemented during decouple-v2 runtime testing** — commit `9cb97f4` fires `InitializeRateLimitsAfterAuth` (fire-and-forget, `Is Nothing`-guarded, layered 2000/50 fallback) after the receive loop starts, and `PlaceAutomatedOrder` gained the distinct `"rate limiter not initialized"` reason. **Do not re-add connect initialization.** (The `InitializeRateLimits` vs `InitializeRateLimitsAfterAuth` near-duplication is a known housekeeping item — leave both.)

**Remaining change.** In `ExecuteOrderAsync`, `StopLossForTrailingOrderAsync`, and `SendReduceOrderAsync`, directly before each `Await SendWebSocketMessageAsync(...)` that sends the order payload, add:
   ```vb
            rateLimiter?.ConsumeCredits()   ' F8: placements are the priciest calls - account for them
   ```
   Consume-only — never block or refuse here (manual clicks must always go through; the auto path already gates on `CanMakeAPIRequest` in `PlaceAutomatedOrder`).

**Acceptance.** Build green; the three placement paths consume; no behavioral change otherwise (consume-only verified by code). Connect-arming already verified in decouple-v2 test 5.

## Commit 3 — F9: reconnects stop leaking `MonitorAuthentication` loops

**Problem.** Each reconnect starts a new loop; old loops re-read the *current* `webSocketClient` field, see it Open, and keep polling — N loops after N reconnects.

**Change.** In `MonitorAuthentication`, capture the instance and exit when superseded:
```vb
    Private Async Sub MonitorAuthentication()
        Dim mySocket As ClientWebSocket = webSocketClient   ' F9: bound to THIS connection
        While mySocket Is webSocketClient AndAlso mySocket IsNot Nothing AndAlso mySocket.State = WebSocketState.Open
```
(loop body unchanged). **Acceptance:** build green; code-review — after a reconnect the old loop's next wake sees `mySocket IsNot webSocketClient` and exits.

## Commit 4 — F12: exception backstops + crash log

**Problem.** `ApplicationEvents.vb` is an empty stub; the app leans on fire-and-forget `Async Sub`s. An exception escaping one on a thread-pool context kills the process with no trace.

**Changes** (all in `ApplicationEvents.vb`; keep `Option Strict On` semantics — add the option lines since this file is being made real):
```vb
        Private Sub MyApplication_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
            AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf OnDomainUnhandled
            AddHandler TaskScheduler.UnobservedTaskException, AddressOf OnUnobservedTask
        End Sub

        Private Sub MyApplication_UnhandledException(sender As Object, e As ApplicationServices.UnhandledExceptionEventArgs) Handles Me.UnhandledException
            WriteCrashLog("UI thread", e.Exception)
            e.ExitApplication = False   ' a UI-thread fault should not kill a live trading session
        End Sub

        Private Sub OnDomainUnhandled(sender As Object, e As UnhandledExceptionEventArgs)
            WriteCrashLog("AppDomain (fatal)", TryCast(e.ExceptionObject, Exception))
        End Sub

        Private Sub OnUnobservedTask(sender As Object, e As UnobservedTaskExceptionEventArgs)
            WriteCrashLog("Unobserved task", e.Exception)
            e.SetObserved()
        End Sub

        Private Sub WriteCrashLog(source As String, ex As Exception)
            Try
                IO.File.AppendAllText("crash.log",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] {If(ex?.ToString(), "(no exception object)")}{Environment.NewLine}")
            Catch
            End Try
        End Sub
```
(Adjust the exact event-args namespaces to what compiles — report any signature adjustments.) **Acceptance:** build green; under the debugger, a deliberately-thrown test exception in a button handler lands in `crash.log` and the app survives (remove the test line after).

## Commit 5 — restart position restore (display) + leverage-pending polish

**Problem (owner, 2026-07-02).** After a restart with an open position, the connect seed fills the engine fields but the UI shows "Awaiting Orders"/zeros; also the seed's log line shows `Leverage: 0.00x` because equity hasn't arrived yet.

**Changes** in `ProcessPositionData`, after the position-model field update block:
1. New engine field `Private positionRestoreAnnounced As Boolean = False`, reset to `False` in `ConnectToWebSocketDirectly` (with the fresh-instance setup).
2. ```vb
            ' Restart restore (display only - engine fields already correct; placedPrice is
            ' order-context and is NOT seeded here). Announce once per connection.
            If positionSize.HasValue AndAlso positionSize.Value <> 0D AndAlso Not positionRestoreAnnounced Then
                positionRestoreAnnounced = True
                Dim side As String = If(positionSize.Value > 0D, "LONG", "SHORT")
                AppendColoredText(txtLogs, $"Open position detected: {side} {Math.Abs(positionSize.Value)} @ {If(averagePrice?.ToString("F2"), "?")}", Color.Yellow)
                UiInvoke(Sub()
                             lblOrderStatus.Text = "In Position"
                             lblOrderStatus.ForeColor = Color.Yellow
                             If averagePrice.HasValue Then txtPlacedPrice.Text = averagePrice.Value.ToString("F2")
                         End Sub)
            End If
   ```
3. Polish: in the same function's leverage display, when `accountBalanceUSD = 0` (equity not yet received) show `L.Lev: pending` instead of `0.00x`.

**Acceptance:** open a position, restart, connect → log line + "In Position" + avg entry visible in the placed-price box; a reduce still works (regression); no announcement when starting flat; `btnRefreshLiveData` does not re-announce.

## Commit 6 — window-X close runs the real shutdown

**Problem (owner).** `CreateParams` sets `CS_NOCLOSE`, so only `btnClose` shuts down cleanly.

**Changes.**
1. Delete the `CreateParams` override entirely.
2. New handler (single shutdown path; note for the future ergonomics implementer — their config save goes at the TOP of this handler, before teardown):
   ```vb
    Private shutdownStarted As Boolean = False
    Private Sub frmMainPageV2_FormClosing(sender As Object, e As FormClosingEventArgs) Handles Me.FormClosing
        If shutdownStarted Then Return
        shutdownStarted = True
        isClosing = True
        Try
            cancellationTokenSource?.Cancel()
            If webSocketClient IsNot Nothing AndAlso webSocketClient.State = WebSocketState.Open Then
                ' Bounded: a wedged close handshake must not hang shutdown (worst case 2s).
                webSocketClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "User closing", CancellationToken.None) _
                    .Wait(TimeSpan.FromSeconds(2))
            End If
            webSocketClient?.Dispose()
            cancellationTokenSource?.Dispose()
        Catch
            ' Never block shutdown on cleanup errors.
        End Try
    End Sub
   ```
3. `btnClose_Click` body becomes just `Me.Close()` (everything else it did now lives in FormClosing).

**Acceptance:** X, Alt+F4, and btnClose all close cleanly (no crash, socket closed on the exchange side); `isClosing` suppresses the reconnect path during shutdown (existing gate — verify no reconnect log lines appear when closing while connected).

---

## Test plan summary (owner, test sub-account)

1. **Dead link (commit 1):** disable network while connected/idle → reconnect sequence within ~50 s; re-enable → recovery.
2. **Limits at connect (commit 2):** connect log shows real limits; no "Rate limiter not initialized" on first reposition.
3. **Restore (commit 5):** restart-with-position scenario end-to-end.
4. **Shutdown (commit 6):** X-close while connected; confirm on Deribit the WS session ended; restart works.
5. **Crash log (commit 4):** debugger-only deliberate throw.
6. **Regression:** one full manual trade cycle (place, reposition, close, DB row) — the receive-loop rewrite must be invisible.

## Implementation report

`docs/impl-report-resilience.md`, standard format: per commit — exact changes, build results, deviations (expected in commit 4's event signatures — report what compiled), suspicious-nearby not touched.
