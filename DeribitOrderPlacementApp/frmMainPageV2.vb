Imports System.Collections.Concurrent
Imports System.Globalization
Imports System.IO
'Imports System.Net.Http
'Imports System.Net.Http.Headers
'Imports System.Net.WebRequestMethods
Imports System.Net.WebSockets
'Imports System.Reflection
'Imports System.Runtime
Imports System.Text
Imports System.Threading
'Imports System.Windows.Forms.VisualStyles
'Imports System.Xml
'Imports Microsoft.VisualBasic.ApplicationServices
Imports Newtonsoft.Json.Linq


Public Class frmMainPageV2

    Private _indicators As FrmIndicators
    Private _autotradesettings As AutoTradeSettings

    Private webSocketClient As ClientWebSocket
    Private cancellationTokenSource As CancellationTokenSource

    'For refresh authentication token
    Private refreshToken As String = Nothing
    Private refreshTokenExpiryTime As DateTime = DateTime.MinValue
    ' Single-flight guard for the id-3 token refresh. RefreshWebSocketAuthentication only SENDS;
    ' the id-3 response is handled in HandleTokenRefreshResponse (central receive loop), which
    ' advances refreshTokenExpiryTime and clears this flag. Self-clearing: if no response arrives
    ' within RefreshResponseTimeoutSeconds, the next gate check re-arms so refreshes can't wedge.
    Private refreshInFlight As Integer = 0  ' 0 = idle, 1 = a refresh send is awaiting its id-3 response (Interlocked)
    Private refreshSentAt As DateTime = DateTime.MinValue
    Private Const RefreshResponseTimeoutSeconds As Integer = 30

    ' API credentials are loaded at runtime from a git-ignored secrets.json (see AppSecrets.vb).
    ' Use AppSecrets.ClientId / AppSecrets.ClientSecret.

    'Public Variables
    Public BestBidPrice, BestAskPrice, TPTrailprice As Decimal
    Public StopLossTriggerOriginal As Decimal = 0 ' Original stop loss TRIGGER price (kept in sync with exchange-side moves)

    ' M.SL emergency-reduce baseline (docs/spec-back-msl-emergency-baseline.md). The price the emergency
    ' market-reduce measures from: 0 until the SL triggers -> the emergency falls back to
    ' StopLossTriggerOriginal (the trigger price). Once triggered it tracks the LIVE SL price (updated on
    ' every triggered echo - manual exchange-side moves + the app's own trailing), so the emergency stays
    ' M.SL below the current stop. Reset to 0 wherever StopLossTriggerOriginal is (placement / close /
    ' nuclear cancel / market reduce). Written on the receive/UI thread; read on the receive thread by the
    ' emergency (accepted Decimal torn-read class, same as StopLossTriggerOriginal).
    Public emergencyBaseline As Decimal = 0

    ' Commanded-SL-price set - triggered-SL reconciliation (docs/spec-reconcile-manual-sl-edits.md, 4a + P1).
    ' Post-trigger the app is the single writer of placedStopLossPrice, but a MANUAL SL edit on the exchange
    ' arrives as the SAME open StopLossOrder echo as a lagging echo of the app's OWN chase reposition. We tell
    ' them apart by remembering every SL price the app has commanded in the last ~2s: an open-echo price we did
    ' NOT command (and that differs from our current reference) is a manual edit -> follow it (correct the chase
    ' reference AND the emergency baseline to the true live SL). A price we DID command is our own (possibly
    ' out-of-order) echo -> ignore it, preserving the runaway/transition-race single-writer protection.
    ' Written on the receive thread (each SL edit send) and read on the UI thread (the open echo), so ALL access
    ' is under commandedSLLock. Cleared wherever the SL context resets (mirrors the 7 emergencyBaseline = 0
    ' sites). Match tolerance = half a tick (BTC-PERPETUAL tick is 0.5) so echo rounding can't cause a spurious
    ' "manual" detection while a real >= 1-tick manual move is still caught.
    Private ReadOnly commandedSLLock As New Object()
    Private ReadOnly commandedSLPrices As New List(Of CommandedSLEntry)()
    Private Const CommandedSLWindowMs As Double = 2000.0
    Private Const CommandedSLMatchTol As Decimal = 0.25D

    Private Structure CommandedSLEntry
        Public Price As Decimal
        Public Stamp As DateTime
    End Structure

    'For auto trading logging
    Public AutoPlacedPrice, AutoTakeProfit, AutoStopLoss As Decimal

    Private TradeMode As Boolean = True ' Tracks if Buy or Sell mode
    Private isTrailingStopLossPlaced As Boolean = False 'Tracks if trailing stop loss already placed once
    Private SLTriggered As Boolean = False ' Tracks if stop loss has been triggered

    Private latestOrderId As String = Nothing ' Track the most recent order ID

    'For rate limiter
    Private rateLimiter As DeribitRateLimiter
    Private accountLimits As RateLimitInfo

    'Database class calls
    Private tradeDatabase As TradeDatabase
    Private tradeAnalytics As TradeAnalytics

    'To prevent duplicate API calls
    Private isRequestingLiveData As Boolean = False
    Private lastLiveDataRequest As DateTime = DateTime.MinValue

    'For circuit breaker in auto-trading in frmindicators
    Public USDPublicSession As Decimal

    ' ============================================================================================
    ' Cross-thread fix (docs/spec-cross-thread-fix.md): engine-owned backing fields + UI marshalling.
    ' ReceiveWebSocketMessagesAsync and every handler it calls run on a thread-pool thread, so they
    ' must NOT read or write WinForms controls. These fields are the engine's source of truth for all
    ' hot-path decisions; the matching textboxes/labels are display mirrors only. User-input fields are
    ' kept current on the UI thread via SyncTradeInputsFromUi (TextChanged + at order placement);
    ' engine-managed fields (placedPrice, placedStopLossPrice, indexPriceVal, equityBTCVal) are set
    ' wherever the value authoritatively changes. Any display write off the UI thread goes through UiInvoke.
    ' ============================================================================================

    ' Engine-managed state (set by the engine; mirrored to controls for display only)
    Private placedPrice As Decimal = 0D            ' mirrors txtPlacedPrice  (drives entry reposition decision)
    Private placedStopLossPrice As Decimal = 0D    ' mirrors txtPlacedStopLossPrice (drives triggered-SL reposition)

    ' Transition-race fix: set True in CancelOrderAsync and held until the exchange confirms the cancel
    ' (cancelled echo / position-flat) or a fresh order is placed, with a timeout fallback. While pending,
    ' the reposition/edit blocks are gated off and lagging open/untriggered echoes are ignored, so nothing
    ' edits an order we've already cancelled and no stale echo re-populates placedPrice/order IDs.
    Private cancelPending As Boolean = False
    Private cancelPendingSince As DateTime = DateTime.MinValue
    Private ReadOnly cancelPendingTimeout As TimeSpan = TimeSpan.FromSeconds(4)
    Private indexPriceVal As Decimal = 0D          ' mirrors lblIndexPrice
    Private equityBTCVal As Decimal = 0D           ' mirrors lblBTCEquity

    ' User-input mirrors (kept == their textboxes by SyncTradeInputsFromUi on the UI thread)
    Private orderAmountVal As Decimal = 0D         ' mirrors txtAmount
    Private manualTPval As Decimal = 0D            ' mirrors txtManualTP
    Private manualSLval As Decimal = 0D            ' mirrors txtManualSL
    Private takeProfitOffset As Decimal = 0D       ' mirrors txtTakeProfit
    Private stopLossOffset As Decimal = 0D         ' mirrors txtStopLoss
    Private triggerDistance As Decimal = 0D        ' mirrors txtTrigger
    Private tpOffsetVal As Decimal = 0D            ' mirrors txtTPOffset
    Private commsVal As Decimal = 0D               ' mirrors txtComms
    Private marketStopThreshold As Decimal = 0D    ' mirrors txtMarketStopLoss
    Private maxSlippageATRmult As Decimal = 0D     ' mirrors txtMaxSlippageATR

    ' Transition-race fix: True while a cancel is in flight. Auto-clears once the timeout elapses so a
    ' missed cancel confirmation can never wedge repositioning permanently. Read by the hot-path decision
    ' gates (quote thread); the echo handler reads the raw cancelPending flag directly.
    Private Function IsCancelPending() As Boolean
        If Not cancelPending Then Return False
        If (DateTime.UtcNow - cancelPendingSince) > cancelPendingTimeout Then
            cancelPending = False
            Return False
        End If
        Return True
    End Function

    ' Marshal a display-only action onto the UI thread. Non-blocking (BeginInvoke) so a slow or failed
    ' paint can never stall or abort a receive-loop decision. Safe to call from any thread.
    Private Sub UiInvoke(action As Action)
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Me.BeginInvoke(action)
        Else
            action()
        End If
    End Sub

    ' Snapshot the user-input textboxes into engine fields. MUST run on the UI thread (wired to the
    ' inputs' TextChanged events and called at order placement). Blank/invalid -> 0, which the engine
    ' treats as "not set" exactly like the old TryParse hot-path (#6 blank-field safety).
    Private Sub SyncTradeInputsFromUi()
        Dim d As Decimal
        orderAmountVal = If(Decimal.TryParse(txtAmount.Text, d), d, 0D)
        manualTPval = If(Decimal.TryParse(txtManualTP.Text, d), d, 0D)
        manualSLval = If(Decimal.TryParse(txtManualSL.Text, d), d, 0D)
        takeProfitOffset = If(Decimal.TryParse(txtTakeProfit.Text, d), d, 0D)
        stopLossOffset = If(Decimal.TryParse(txtStopLoss.Text, d), d, 0D)
        triggerDistance = If(Decimal.TryParse(txtTrigger.Text, d), d, 0D)
        tpOffsetVal = If(Decimal.TryParse(txtTPOffset.Text, d), d, 0D)
        commsVal = If(Decimal.TryParse(txtComms.Text, d), d, 0D)
        marketStopThreshold = If(Decimal.TryParse(txtMarketStopLoss.Text, d), d, 0D)
        maxSlippageATRmult = If(Decimal.TryParse(txtMaxSlippageATR.Text, d), d, 0D)
    End Sub

    ' One TextChanged handler for every trade-input textbox: keeps the engine fields == the controls,
    ' so the receive loop always reads the latest user value without touching a control off-thread.
    Private Sub TradeInput_Changed(sender As Object, e As EventArgs) _
        Handles txtAmount.TextChanged, txtManualTP.TextChanged, txtManualSL.TextChanged,
                txtTakeProfit.TextChanged, txtStopLoss.TextChanged, txtTrigger.TextChanged,
                txtTPOffset.TextChanged, txtComms.TextChanged, txtMarketStopLoss.TextChanged,
                txtMaxSlippageATR.TextChanged
        SyncTradeInputsFromUi()
    End Sub

    ' Checkbox-toggle mirrors so the receive loop reads booleans, not chk*.Checked off-thread.
    Private maxSlippageATRchecked As Boolean = False
    Private marketStopLossChecked As Boolean = False

    ' Null-safe: the designer sets .Checked during InitializeComponent, which fires CheckedChanged before
    ' both checkboxes are constructed - read each only once it exists (Load re-seeds both afterward).
    Private Sub SyncToggleInputsFromUi()
        If chkMaxSlippageATR IsNot Nothing Then maxSlippageATRchecked = chkMaxSlippageATR.Checked
        If chkMarketStopLoss IsNot Nothing Then marketStopLossChecked = chkMarketStopLoss.Checked
    End Sub

    Private Sub ChkToggle_Changed(sender As Object, e As EventArgs) _
        Handles chkMaxSlippageATR.CheckedChanged, chkMarketStopLoss.CheckedChanged
        SyncToggleInputsFromUi()
    End Sub

    Public ReadOnly Property RateLimiterInstance As DeribitRateLimiter
        Get
            Return rateLimiter
        End Get
    End Property


    Public Class RateLimitInfo
        Public Property MaxCredits As Integer
        Public Property RefillRate As Integer
        Public Property BurstLimit As Integer
        Public Property CurrentEstimatedCredits As Integer
    End Class

    Public Class DeribitRateLimiter
        Private ReadOnly _maxCredits As Integer
        Private ReadOnly _refillRate As Integer = 15 ' Credits per millisecond 'Conservative = 10 | Reasonable = 20
        Private ReadOnly _costPerRequest As Integer = 83 ' Conservative estimate = 200 | Reasonable = 50
        Private _currentCredits As Integer
        Private _lastRefillTime As DateTime
        Private ReadOnly _lockObject As New Object()

        Public Sub New(maxCredits As Integer, costPerRequest As Integer)
            _maxCredits = maxCredits
            _costPerRequest = costPerRequest
            _currentCredits = maxCredits
            _lastRefillTime = DateTime.UtcNow
        End Sub

        Public Function CanMakeRequest() As Boolean
            SyncLock _lockObject
                RefillCredits()
                Return _currentCredits >= _costPerRequest
            End SyncLock
        End Function

        Public Function ConsumeCredits() As Boolean
            SyncLock _lockObject
                RefillCredits()
                If _currentCredits >= _costPerRequest Then
                    _currentCredits -= _costPerRequest
                    Return True
                End If
                Return False
            End SyncLock
        End Function

        Private Sub RefillCredits()
            Dim now As DateTime = DateTime.UtcNow
            Dim elapsedMs As Double = (now - _lastRefillTime).TotalMilliseconds

            If elapsedMs > 0 Then
                Dim creditsToAdd As Integer = CInt(elapsedMs * _refillRate)
                _currentCredits = Math.Min(_maxCredits, _currentCredits + creditsToAdd)
                _lastRefillTime = now
            End If
        End Sub

        Public Function GetWaitTimeMs() As Integer
            SyncLock _lockObject
                RefillCredits()
                If _currentCredits >= _costPerRequest Then
                    Return 0
                End If

                Dim creditsNeeded As Integer = _costPerRequest - _currentCredits
                Return CInt(Math.Ceiling(creditsNeeded / _refillRate))
            End SyncLock
        End Function

        Public Function GetDetailedStatus() As String
            SyncLock _lockObject
                RefillCredits()
                Return $"Credits: {_currentCredits}/{_maxCredits} | " &
                       $"Last Refill: {_lastRefillTime:HH:mm:ss.fff} | " &
                       $"Can Request: {CanMakeRequest()}"
            End SyncLock
        End Function

        Public Sub ForceRefillDebug()
            ' Manual credit refill for testing
            SyncLock _lockObject
                _currentCredits = _maxCredits
                _lastRefillTime = DateTime.UtcNow
            End SyncLock
        End Sub

    End Class

    'For AUTOMATED ORDER PLACEMENT
    '----------------------------------------------------------------------------------------------
    Public Function GetTradeMode() As Boolean
        Return TradeMode
    End Function

    Public ReadOnly Property WebSocketConnection As ClientWebSocket
        Get
            Return webSocketClient
        End Get
    End Property

    Public ReadOnly Property RateLimitManager As DeribitRateLimiter
        Get
            Return rateLimiter
        End Get
    End Property

    Public ReadOnly Property IsWebSocketConnected As Boolean
        Get
            Return webSocketClient IsNot Nothing AndAlso webSocketClient.State = WebSocketState.Open
        End Get
    End Property

    ' Optional: Add rate limit status check
    Public ReadOnly Property CanMakeAPIRequest As Boolean
        Get
            Return rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest()
        End Get
    End Property

    ' ===== PUBLIC AUTOMATION API (contract: docs/integration-contract-verdictengine.md) =====
    ' Thread contract: every member here is callable from ANY thread.

    Public ReadOnly Property OpenPositionSizeUSD As Decimal   ' signed: + long / - short
        Get
            Return positionSizeUSD
        End Get
    End Property

    Public ReadOnly Property OpenPositionAvgEntry As Decimal
        Get
            Return positionAvgEntry
        End Get
    End Property

    Public ReadOnly Property SessionPnLUSD As Decimal
        Get
            Return USDPublicSession
        End Get
    End Property

    Public ReadOnly Property HasWorkingEntryOrder As Boolean
        Get
            Return CurrentOpenOrderId IsNot Nothing
        End Get
    End Property

    Public ReadOnly Property IsFlat As Boolean
        Get
            Return positionSizeUSD = 0D
        End Get
    End Property

    ' Writes the trade-input textboxes on the UI thread; TextChanged syncs the engine mirrors -
    ' the same path the manual flow and the old btnATR paste use. Nothing/negative = leave as is.
    Public Sub SetTradeTargets(Optional takeProfit As Decimal? = Nothing,
                               Optional triggerDistance As Decimal? = Nothing,
                               Optional stopLoss As Decimal? = Nothing,
                               Optional sizeUSD As Decimal? = Nothing,
                               Optional manualTP As Decimal? = Nothing,
                               Optional manualSL As Decimal? = Nothing)
        Dim apply As Action = Sub()
                                  If takeProfit.HasValue AndAlso takeProfit.Value >= 0D Then txtTakeProfit.Text = takeProfit.Value.ToString()
                                  If triggerDistance.HasValue AndAlso triggerDistance.Value >= 0D Then txtTrigger.Text = triggerDistance.Value.ToString()
                                  If stopLoss.HasValue AndAlso stopLoss.Value >= 0D Then txtStopLoss.Text = stopLoss.Value.ToString()
                                  If sizeUSD.HasValue AndAlso sizeUSD.Value > 0D Then txtAmount.Text = sizeUSD.Value.ToString()
                                  If manualTP.HasValue AndAlso manualTP.Value >= 0D Then txtManualTP.Text = manualTP.Value.ToString()
                                  If manualSL.HasValue AndAlso manualSL.Value >= 0D Then txtManualSL.Text = manualSL.Value.ToString()
                              End Sub
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then Me.Invoke(apply) Else apply()
    End Sub

    ' Places an entry with the current targets. v1 policy: STRICT - refuses unless connected,
    ' rate-limit OK, flat, no working entry, and no cancel pending (the engine flattens first if
    ' it wants to flip). side: "long"/"short". kind: "limit"|"market"|"nospread". Returns the
    ' exchange ack (or a gate refusal / 5s timeout). Callable from any thread.
    Public Async Function PlaceAutomatedOrder(side As String, Optional kind As String = "limit",
                                              Optional ackTimeoutMs As Integer = 5000) As Task(Of PlacementResult)
        ' Gates (fields only - safe on any thread)
        If Not IsWebSocketConnected Then Return New PlacementResult With {.Accepted = False, .Reason = "not connected"}
        If rateLimiter Is Nothing Then Return New PlacementResult With {.Accepted = False, .Reason = "rate limiter not initialized"}
        If Not CanMakeAPIRequest Then Return New PlacementResult With {.Accepted = False, .Reason = "rate limit"}
        If IsCancelPending() Then Return New PlacementResult With {.Accepted = False, .Reason = "cancel pending"}
        If positionSizeUSD <> 0D Then Return New PlacementResult With {.Accepted = False, .Reason = "position open (flatten first)"}
        If CurrentOpenOrderId IsNot Nothing Then Return New PlacementResult With {.Accepted = False, .Reason = "working entry exists"}

        Dim isLong As Boolean
        Select Case If(side, "").ToLowerInvariant()
            Case "long", "buy" : isLong = True
            Case "short", "sell" : isLong = False
            Case Else : Return New PlacementResult With {.Accepted = False, .Reason = $"unknown side '{side}'"}
        End Select

        Dim typeOfOrder As String
        Select Case If(kind, "").ToLowerInvariant()
            Case "limit" : typeOfOrder = If(isLong, "BuyLimit", "SellLimit")
            Case "market" : typeOfOrder = If(isLong, "BuyMarket", "SellMarket")
            Case "nospread" : typeOfOrder = If(isLong, "BuyNoSpread", "SellNoSpread")
            Case Else : Return New PlacementResult With {.Accepted = False, .Reason = $"unknown kind '{kind}'"}
        End Select

        ' Pre-register the ack BEFORE the placement seeds engine state (snapshots re-taken at send).
        Dim reqId As Integer = Interlocked.Increment(nextPlacementId)
        Dim tcs As New TaskCompletionSource(Of PlacementResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        pendingPlacements(reqId) = New PendingPlacement With {.RequestId = reqId, .Tcs = tcs}

        ' Marshal the placement onto the UI thread (ExecuteOrderAsync reads controls) and await it
        ' from this thread. Control.Invoke of a Function(Of Task) returns the Task to await.
        Dim placeCall As Func(Of Task) = Function()
                                             SetTradeMode(isLong)
                                             Return ExecuteOrderAsync(typeOfOrder, reqId)
                                         End Function
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Await CType(Me.Invoke(placeCall), Task)
        Else
            Await placeCall()
        End If

        ' Await the exchange ack with a timeout. Timeout <> rejection: no rollback (the order may
        ' exist; echoes remain the source of truth) - the caller re-queries state.
        Dim done = Await Task.WhenAny(tcs.Task, Task.Delay(ackTimeoutMs))
        If done Is tcs.Task Then Return tcs.Task.Result
        Dim ignored As PendingPlacement = Nothing
        pendingPlacements.TryRemove(reqId, ignored)
        Return New PlacementResult With {.Accepted = False, .Reason = "timeout"}
    End Function

    ' Flatten the actual position at market (position-model sized; emergency-grade path).
    Public Async Function FlattenPositionAsync() As Task
        Await SendReduceMarketOrderAsync()
    End Function

    ' Nuclear: cancel every order on the instrument (the Cancel-All button's path).
    Public Async Function CancelAllOrdersAsync() As Task
        Await CancelOrderAsync()
    End Function

    ' Scoped: abandon the working entry only; an existing position's legs stay.
    Public Async Function CancelWorkingEntryAsync() As Task
        Await CancelWorkingEntryCoreAsync("API request")
    End Function
    '----------------------------------------------------------------------------------------------

    Private Sub frmMainPageV2_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Try
            ' Seed the engine input fields from whatever the controls currently hold (cross-thread fix).
            SyncTradeInputsFromUi()
            SyncToggleInputsFromUi()

            _indicators = New FrmIndicators(Me)     ' pass “self” as host
            _indicators.Show()                      ' non-modal; use .ShowDialog() if you prefer modal

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Startup Error: {ex.Message}{vbCrLf}{ex.StackTrace}", Color.Red)
            'Application.Exit()
        End Try
    End Sub

    Private Sub frmMainPageV2_Shown(sender As Object, e As EventArgs) Handles MyBase.Shown
        ' Load API credentials from git-ignored secrets.json before any connection attempt
        Dim secretsError As String = AppSecrets.Load()
        If secretsError IsNot Nothing Then
            AppendColoredText(txtLogs, $"API credentials: {secretsError}", Color.Red)
        Else
            AppendColoredText(txtLogs, "API credentials loaded", Color.LimeGreen)
        End If

        Try
            ' Initialize trade database
            tradeDatabase = New TradeDatabase()
            tradeAnalytics = New TradeAnalytics(tradeDatabase.DatabasePath)

            ' Subscribe to database events
            AddHandler tradeDatabase.DatabaseError, AddressOf OnDatabaseError
            AddHandler tradeDatabase.TradeRecorded, AddressOf OnTradeRecorded
            AddHandler tradeDatabase.TradeDeleted, AddressOf OnTradeDeleted

            AppendColoredText(txtLogs, "Trade database initialized successfully", Color.LimeGreen)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Failed to initialize trade database: {ex.Message}", Color.Red)
        End Try
    End Sub

    ' Event handlers
    Private Sub OnDatabaseError(message As String)
        AppendColoredText(txtLogs, $"Database Error: {message}", Color.Red)
    End Sub

    Private Sub OnTradeRecorded(tradeId As Integer, trade As TradeRecord)
        AppendColoredText(txtLogs, $"Trade #{tradeId} recorded in database", Color.Cyan)
    End Sub
    Private Sub OnTradeDeleted(tradeId As Integer)
        AppendColoredText(txtLogs, $"Trade #{tradeId} deleted from database", Color.Yellow)
    End Sub

    Private Async Function AuthorizeWebSocketConnection() As Task

        ' Create the authorization message using JObject
        Dim authPayload = New JObject(
            New JProperty("jsonrpc", "2.0"),
            New JProperty("id", 2),
            New JProperty("method", "public/auth"),
            New JProperty("params", New JObject(
                New JProperty("grant_type", "client_credentials"),
                New JProperty("client_id", AppSecrets.ClientId),
                New JProperty("client_secret", AppSecrets.ClientSecret)
            ))
        )
        'Await SendWebSocketMessageAsync(authPayload)
        Await SendWebSocketMessageAsync(authPayload.ToString())

        ' Read the response (F5: accumulate fragments until EndOfMessage - same fix as the receive loop)
        Dim buffer = New Byte(1024 * 4) {}
        Dim sb As New StringBuilder()
        Dim result As WebSocketReceiveResult
        Do
            result = Await webSocketClient.ReceiveAsync(New ArraySegment(Of Byte)(buffer), cancellationTokenSource.Token)
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))
        Loop Until result.EndOfMessage
        Dim response = sb.ToString()

        Dim json = JObject.Parse(response)
        Dim errorField = json.SelectToken("error")

        If errorField IsNot Nothing Then
            Throw New Exception("Authorization failed: " & errorField.ToString())
            'txtLogs.AppendText("Authorization failed: " & errorField.ToString() + Environment.NewLine)
            AppendColoredText(txtLogs, "Authorization failed: " & errorField.ToString(), Color.Red)
        Else
            'txtLogs.AppendText("WebSocket authorized successfully" + Environment.NewLine)
            AppendColoredText(txtLogs, "WebSocket authorized successfully", Color.DodgerBlue)
            Await EnableDeribitHeartbeatEnhanced()

            Dim refreshTokenToken = json.SelectToken("result.refresh_token")
            If refreshTokenToken IsNot Nothing Then
                refreshToken = refreshTokenToken.ToString()
            Else
                Throw New Exception("Refresh token not found in response")
                'txtLogs.AppendText("Refresh token not found in response" + Environment.NewLine)
                AppendColoredText(txtLogs, "Refresh token not found in response", Color.Yellow)
            End If

            Dim expiresInToken = json.SelectToken("result.expires_in")
            Dim expiresIn As Double = 0
            If expiresInToken IsNot Nothing Then
                expiresIn = expiresInToken.ToObject(Of Double)()
                'txtLogs.AppendText("Token Expires In: " & expiresIn.ToString + Environment.NewLine)       ' TEST
            End If

            refreshTokenExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn - 240) ' Refresh 4 minutes before expiry
            Interlocked.Exchange(refreshInFlight, 0) ' Fresh auth: no refresh is in flight on this connection

            'Give successful status update - marshal to UI thread (reconnect path runs on a thread-pool thread)
            Me.BeginInvoke(Sub()
                               lblStatus.ForeColor = Color.LimeGreen
                               btnConnect.Text = "ONLINE"
                               btnConnect.BackColor = Color.Lime
                           End Sub)

        End If
    End Function

    Private Async Function RefreshWebSocketAuthentication() As Task
        ' Self-clearing guard: if a previous refresh was sent but its id-3 response never arrived
        ' within the timeout, re-arm so future refreshes are not permanently wedged.
        If refreshInFlight = 1 AndAlso (DateTime.UtcNow - refreshSentAt).TotalSeconds > RefreshResponseTimeoutSeconds Then
            Interlocked.Exchange(refreshInFlight, 0)
            AppendColoredText(txtLogs, "Token refresh response timed out - re-arming refresh", Color.Yellow)
        End If

        If DateTime.UtcNow >= refreshTokenExpiryTime Then
            ' Single-flight: only one outstanding refresh send at a time. The id-3 response is
            ' consumed by the central receive loop (HandleTokenRefreshResponse), never here -
            ' ClientWebSocket forbids a second concurrent ReceiveAsync.
            If Interlocked.Exchange(refreshInFlight, 1) = 1 Then Return

            refreshSentAt = DateTime.UtcNow

            ' Create the refresh message using JObject
            Dim refreshPayload = New JObject(
                New JProperty("jsonrpc", "2.0"),
                New JProperty("id", 3),
                New JProperty("method", "public/auth"),
                New JProperty("params", New JObject(
                    New JProperty("grant_type", "refresh_token"),
                    New JProperty("refresh_token", refreshToken)
                ))
            )

            ' SEND ONLY. Do not ReceiveAsync here - HandleTokenRefreshResponse handles id 3 and
            ' updates refreshToken + refreshTokenExpiryTime and clears refreshInFlight.
            Await SendWebSocketMessageAsync(refreshPayload.ToString())
        End If
    End Function

    ' Handles the id-3 token-refresh response routed through the single receive loop.
    Private Sub HandleTokenRefreshResponse(response As String)
        Try
            Dim json = JObject.Parse(response)
            Dim messageId = json.SelectToken("id")?.ToObject(Of Integer)()
            ' Null-safe: messageId is Nothing for id-less subscription messages. Nothing <> 3 is Nothing
            ' (treated as False by If), which would fall through on every tick - so test HasValue explicitly.
            If Not (messageId.HasValue AndAlso messageId.Value = 3) Then Return

            Dim errorField = json.SelectToken("error")
            If errorField IsNot Nothing Then
                AppendColoredText(txtLogs, "Token refresh failed: " & errorField.ToString(), Color.Yellow)
                ' Re-arm so the next gate check can retry (transient errors recover; reconnect is the backstop).
                Interlocked.Exchange(refreshInFlight, 0)
                Return
            End If

            Dim refreshTokenToken = json.SelectToken("result.refresh_token")
            If refreshTokenToken IsNot Nothing Then
                refreshToken = refreshTokenToken.ToString()
            Else
                AppendColoredText(txtLogs, "Refresh token not found in refresh response", Color.Yellow)
                Interlocked.Exchange(refreshInFlight, 0)
                Return
            End If

            Dim expiresInToken = json.SelectToken("result.expires_in")
            Dim expiresIn As Double = 0
            If expiresInToken IsNot Nothing Then
                expiresIn = expiresInToken.ToObject(Of Double)()
            End If
            ' Advance the expiry so the once-a-minute gate stops firing until the next near-expiry window.
            refreshTokenExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn - 240) ' Refresh 4 minutes before expiry

            Interlocked.Exchange(refreshInFlight, 0)
            AppendColoredText(txtLogs, "WebSocket re-authenticated successfully (token rotated)", Color.DodgerBlue)
        Catch ex As Exception
            ' Non-id-3 messages / parse noise: ignore, like the other id-keyed handlers.
        End Try
    End Sub

    Private Async Function SendWebSocketMessageAsync(message As String) As Task
        Try
            Dim bytes = Encoding.UTF8.GetBytes(message)

            ' Attempt to send the message
            Await webSocketClient.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, cancellationTokenSource.Token)

        Catch ex As WebSocketException
            ' Log WebSocket-specific errors
            AppendColoredText(txtLogs, "WebSocket Error: " & ex.Message, Color.Red)
            Dim disconnectTask = HandleWebSocketDisconnect()

        Catch ex As OperationCanceledException
            ' Log if operation was canceled (e.g., during shutdown)
            AppendColoredText(txtLogs, "Operation Canceled: " & ex.Message, Color.Orange)
        Catch ex As Exception
            ' General exception handler for any other errors
            AppendColoredText(txtLogs, "Error sending message: " & ex.Message, Color.Red)
            ' Fire and forget reconnection attempt
            Dim disconnectTask = HandleWebSocketDisconnect()
        End Try
    End Function

    ' These fields are for reconnect logic
    Private isReconnecting As Integer = 0  ' 0 = no, 1 = yes (Interlocked)
    Private reconnectAttempts As Integer = 0
    Private maxReconnectAttempts As Integer = 10
    Private isClosing As Boolean = False

    Private Async Function HandleWebSocketDisconnect() As Task

        If Not isClosing Then
            ' Single-flight guard - only one reconnect at a time
            If Interlocked.Exchange(isReconnecting, 1) = 1 Then Return

            Try
                AppendColoredText(txtLogs, "Connection lost - initiating recovery sequence", Color.Orange)

                ' Update UI immediately on UI thread
                Me.BeginInvoke(Sub()
                                   btnConnect.Text = "Connect!"
                                   btnConnect.BackColor = Color.Red
                                   lblStatus.Text = "Disconnected"
                               End Sub)

                ' Wait before attempting reconnection
                Await Task.Delay(2000 + (reconnectAttempts * 1000)) ' Progressive backoff

                For attempt = 1 To maxReconnectAttempts
                    Dim success As Boolean = False
                    Dim errorMessage As String = ""

                    Try
                        AppendColoredText(txtLogs, $"Reconnection attempt {attempt}/{maxReconnectAttempts}", Color.Yellow)

                        ' Call connection method directly - NOT through UI button
                        Await ConnectToWebSocketDirectly()

                        ' If we reach here, connection succeeded
                        success = True

                    Catch ex As Exception
                        errorMessage = ex.Message
                    End Try

                    ' Handle results outside the Try/Catch
                    If success Then
                        reconnectAttempts = 0
                        AppendColoredText(txtLogs, "Successfully reconnected", Color.LimeGreen)
                        Return
                    Else
                        reconnectAttempts += 1
                        AppendColoredText(txtLogs, $"Reconnect attempt {attempt} failed: {errorMessage}", Color.Red)

                        ' Only delay if we have more attempts left
                        If attempt < maxReconnectAttempts Then
                            Dim delayMs = 2000 * Math.Min(attempt, 5) ' Cap at 10 second delays
                            Await Task.Delay(delayMs)
                        End If
                    End If
                Next

                ' All reconnection attempts failed
                AppendColoredText(txtLogs, "All reconnection attempts failed - manual intervention required", Color.Red)

            Finally
                Interlocked.Exchange(isReconnecting, 0)
            End Try
        Else
            Return
        End If

    End Function


    Private Async Function ConnectToWebSocketDirectly() As Task
        ' Clean shutdown of existing connection
        Try
            If cancellationTokenSource IsNot Nothing Then
                cancellationTokenSource.Cancel()
            End If
            If webSocketClient IsNot Nothing Then
                If webSocketClient.State = WebSocketState.Open Then
                    Await webSocketClient.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnecting", CancellationToken.None)
                End If
                webSocketClient.Dispose()
            End If
            cancellationTokenSource?.Dispose()
        Catch
            ' Ignore cleanup errors
        End Try

        ' Create fresh instances
        webSocketClient = New ClientWebSocket()
        webSocketClient.Options.KeepAliveInterval = TimeSpan.FromSeconds(30)
        ' F7: abort ReceiveAsync when pongs stop - dead links now surface as a WebSocketException
        ' in the receive loop, which lands in the existing reconnect path.
        webSocketClient.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20)
        cancellationTokenSource = New CancellationTokenSource()
        positionRestoreAnnounced = False ' fresh connection: the id-777 seed may announce again

        ' Connect with timeout
        Using connectTimeout As New CancellationTokenSource(TimeSpan.FromSeconds(30))
            Using combined = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationTokenSource.Token, connectTimeout.Token)

                Await webSocketClient.ConnectAsync(
                New Uri("wss://www.deribit.com/ws/api/v2"),
                combined.Token)
            End Using
        End Using

        ' Authenticate and subscribe
        Await AuthorizeWebSocketConnection()
        Await EnableDeribitHeartbeatEnhanced()
        Await SubscribeToIndexPrice()
        Await SubscribeToUserPortfolio()
        Await SubscribeToQuoteBTCPerpetual()
        Await SubscribeToUserOrders()

        ' Position model: seed size/avg-entry for a restart with an already-open position
        ' (id-777 response lands in ProcessPositionData via HandleMarginEstimationResponse).
        Await GetLivePositionData("BTC-PERPETUAL")

        ' Restore hardening: fetch the OTOCO children so a restarted session regains order
        ' context (entry/TP/SL ids + prices). Send-only; the id-778 response drains through
        ' HandleOpenOrdersSnapshot once the receive loop starts (same pattern as the id-777 seed).
        Await RequestOpenOrdersSnapshot()

        ' Update UI on success
        Me.BeginInvoke(Sub()
                           btnConnect.Text = "ONLINE"
                           btnConnect.BackColor = Color.Lime
                           lblStatus.Text = "Connected"
                       End Sub)

        ' Start background tasks - use proper variable names
        Dim authTask = Task.Run(AddressOf MonitorAuthentication) ' Fire and forget
        Dim receiveTask = Task.Run(Function() ReceiveWebSocketMessagesAsync()) ' Fire and forget

        ' Arm the rate limiter at connect (runtime test 5, 2026-07-03): it was previously created
        ' lazily (reposition fallback / manual ONLINE re-click), so CanMakeAPIRequest - and the
        ' PlaceAutomatedOrder gate - read False until the first manual order activity. Needs the
        ' receive loop above (id-999 response); fire-and-forget like the other startup tasks.
        ' Reconnects keep the existing limiter (Is Nothing guard); the conservative fallback
        ' guarantees an armed limiter even if the account-summary request times out.
        If rateLimiter Is Nothing Then
            Dim armLimiterTask = Task.Run(Async Function()
                                              Await InitializeRateLimitsAfterAuth()
                                              If rateLimiter Is Nothing Then rateLimiter = New DeribitRateLimiter(2000, 50)
                                          End Function)
        End If
    End Function



    Private Async Function ReceiveWebSocketMessagesAsync() As Task
        Dim buffer(65535) As Byte ' Larger buffer
        Dim reconnectNeeded As Boolean = False
        Dim sb As New StringBuilder()

        While webSocketClient.State = WebSocketState.Open
            Try
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

                ' Call the function to handle heartbeat requests from server
                HandleHeartbeat(response)
                ' Call the function to handle quote updates
                HandleQuoteUpdates(response)
                ' Call the function to handle index price updates
                HandleIndexUpdates(response)
                ' Call the function to handle user portfolio updates
                HandleBalanceUpdates(response)
                ' Call the function to handle user order/position updates
                HandleOrderPositionUpdates(response)
                ' Handle the id-3 token-refresh response (sent by RefreshWebSocketAuthentication)
                HandleTokenRefreshResponse(response)
                ' NEW: Handle account summary responses
                HandleAccountSummaryResponse(response)
                ' NEW: Handle rate limit errors
                HandleRateLimitError(response)
                ' NEW: Handle margin estimates for liquidation price calculations
                HandleMarginEstimationResponse(response)
                ' Restore hardening: OTOCO children snapshot at connect (id 778)
                HandleOpenOrdersSnapshot(response)
                ' Decouple v2: placement acks/rejections (ids >= PlacementIdBase)
                HandlePlacementResponse(response)
                ' Audit2 F2: surface JSON-RPC errors no dedicated handler owns (silent order rejections)
                HandleUnhandledJsonRpcError(response)
            Catch ex As WebSocketException
                AppendColoredText(txtLogs, $"WebSocket exception: {ex.Message}", Color.Red)
                reconnectNeeded = True
                Exit While

            Catch ex As OperationCanceledException
                ' Normal during shutdown
                AppendColoredText(txtLogs, "Receive operation cancelled", Color.Gray)
                Exit While

            Catch ex As Exception
                AppendColoredText(txtLogs, $"Receive error: {ex.Message}", Color.Red)
                reconnectNeeded = True
                Exit While
            End Try
        End While

        ' Only trigger reconnect if we detected a problem

        If reconnectNeeded Then
            If Not isClosing Then
                ' Fire and forget - don't await to avoid blocking
                Dim reconnectTask = Task.Run(Function() HandleWebSocketDisconnect())
            Else
                Return
            End If
        End If

    End Function

    Private accountSummaryTaskCompletionSource As TaskCompletionSource(Of RateLimitInfo)
    Private Async Sub MonitorAuthentication()
        Dim mySocket As ClientWebSocket = webSocketClient   ' F9: bound to THIS connection
        While mySocket Is webSocketClient AndAlso mySocket IsNot Nothing AndAlso mySocket.State = WebSocketState.Open
            Try
                Await RefreshWebSocketAuthentication()
                Await Task.Delay(60000) ' Check every minute
            Catch ex As Exception
                'txtLogs.AppendText("Error refreshing token: " & ex.Message + Environment.NewLine)
                AppendColoredText(txtLogs, "Error refreshing token: " & ex.Message, Color.Yellow)
            End Try
        End While
    End Sub

    Private Async Function EnableDeribitHeartbeatEnhanced() As Task
        Try
            ' Use Deribit's official heartbeat API with proper JSON structure
            Dim heartbeatPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 1001},
            {"method", "public/set_heartbeat"},
            {"params", New JObject From {
                {"interval", 30}
            }}
        }

            Await SendWebSocketMessageAsync(heartbeatPayload.ToString())
            'AppendColoredText(txtLogs, "Deribit official heartbeat enabled (30s interval)", Color.Cyan)

        Catch ex As Exception
            AppendColoredText(txtLogs, "Heartbeat setup failed: " & ex.Message, Color.Red)
        End Try
    End Function

    'Rate limiter functions below
    Private Sub HandleAccountSummaryResponse(response As String)
        Try
            Dim json = JObject.Parse(response)

            ' Check if this is an account summary response (ID 999 from our request)
            Dim messageId = json.SelectToken("id")?.ToObject(Of Integer)()
            If messageId = 999 Then

                Dim errorField = json.SelectToken("error")
                If errorField IsNot Nothing Then
                    AppendColoredText(txtLogs, $"Account summary error: {errorField.ToString()}", Color.Yellow)

                    ' Complete the task with conservative defaults on error
                    If accountSummaryTaskCompletionSource IsNot Nothing Then
                        accountSummaryTaskCompletionSource.SetResult(New RateLimitInfo With {
                        .MaxCredits = 1000,
                        .RefillRate = 10,
                        .BurstLimit = 10,
                        .CurrentEstimatedCredits = 1000
                    })
                    End If
                    Return
                End If

                ' Extract rate limit information from the result
                Dim result = json.SelectToken("result")
                If result IsNot Nothing Then
                    Dim rateLimitInfo = ExtractRateLimitsFromAccountSummary(result)
                    AppendColoredText(txtLogs, $"Account summary received - Max Credits: {rateLimitInfo.MaxCredits}", Color.LimeGreen)

                    ' Complete the waiting task
                    If accountSummaryTaskCompletionSource IsNot Nothing Then
                        accountSummaryTaskCompletionSource.SetResult(rateLimitInfo)
                    End If
                End If
            End If

        Catch ex As Exception
            ' Ignore parsing errors for non-account-summary responses
        End Try
    End Sub

    Private Function ExtractRateLimitsFromAccountSummary(accountData As JToken) As RateLimitInfo
        Try
            ' Look for limits in the account summary response
            Dim limits = accountData.SelectToken("limits")
            If limits IsNot Nothing Then
                Dim matchingEngineLimit = limits.SelectToken("matching_engine")
                If matchingEngineLimit IsNot Nothing Then
                    Dim burst = matchingEngineLimit.SelectToken("burst")?.ToObject(Of Integer)()
                    Dim rate = matchingEngineLimit.SelectToken("rate")?.ToObject(Of Integer)()

                    If burst.HasValue AndAlso rate.HasValue Then
                        ' Calculate total credits using Deribit's formula
                        Dim totalCredits As Integer = CInt(Math.Round(burst.Value * 10000 / rate.Value))

                        Return New RateLimitInfo With {
                        .MaxCredits = totalCredits,
                        .RefillRate = 10, ' Standard 10 credits per millisecond
                        .BurstLimit = burst.Value,
                        .CurrentEstimatedCredits = totalCredits
                    }
                    End If
                End If
            End If

            ' Fallback to conservative estimates if limits not found
            Return New RateLimitInfo With {
            .MaxCredits = 2000,
            .RefillRate = 10,
            .BurstLimit = 20,
            .CurrentEstimatedCredits = 2000
        }

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error extracting rate limits: {ex.Message}", Color.Yellow)
            ' Return very conservative defaults
            Return New RateLimitInfo With {
            .MaxCredits = 2000,
            .RefillRate = 20,
            .BurstLimit = 20,
            .CurrentEstimatedCredits = 2000
        }
        End Try
    End Function

    Private Sub HandleRateLimitError(response As String)
        Try
            Dim json = JObject.Parse(response)
            Dim errorField = json.SelectToken("error")

            If errorField IsNot Nothing Then
                Dim errorCode = errorField.SelectToken("code")?.ToObject(Of Integer)()
                If errorCode = 10028 Then ' too_many_requests
                    AppendColoredText(txtLogs, "Rate limit exceeded - reducing API frequency", Color.Red)
                    ' Add null check for rateLimiter
                    If rateLimiter IsNot Nothing AndAlso accountLimits IsNot Nothing Then
                        Dim newMaxCredits = CInt(accountLimits.MaxCredits * 0.7)
                        rateLimiter = New DeribitRateLimiter(newMaxCredits, 200)
                        accountLimits.MaxCredits = newMaxCredits ' Update the stored limits too
                        AppendColoredText(txtLogs, $"Rate limiter adjusted to {newMaxCredits} max credits", Color.Yellow)
                    End If
                End If
            End If
        Catch ex As Exception
            ' Ignore parsing errors for non-JSON responses
        End Try
    End Sub

    ' Audit2 F2 (logger half): surface JSON-RPC error responses that no dedicated handler owns.
    ' Ids 3/999/777/890 already log their own errors in their handlers; code 10028 is owned by
    ' HandleRateLimitError. Everything else (entry orders id 2, cancels id 30, edits 223344-223350,
    ' reduce orders id 1, subscribes) was previously dropped silently - a rejected order looked
    ' identical to a working one. LOGGING ONLY: no engine state is touched here (rollback is #10's job).
    Private Sub HandleUnhandledJsonRpcError(response As String)
        Try
            ' Fast path: skip the JSON parse for the vast majority of messages (quotes, echoes).
            If response.IndexOf("""error""", StringComparison.Ordinal) < 0 Then Return

            Dim json = JObject.Parse(response)
            Dim errorField = json.SelectToken("error")
            If errorField Is Nothing Then Return

            Dim messageId = json.SelectToken("id")?.ToObject(Of Integer)()
            ' Skip errors that already have dedicated logging (null-safe: HasValue AndAlso, never <>)
            If messageId.HasValue AndAlso
               (messageId.Value = 3 OrElse messageId.Value = 999 OrElse
                messageId.Value = 777 OrElse messageId.Value = 890) Then Return
            If messageId.HasValue AndAlso messageId.Value >= PlacementIdBase Then Return ' HandlePlacementResponse owns placements

            Dim errorCode = errorField.SelectToken("code")?.ToObject(Of Integer)()
            If errorCode.HasValue AndAlso errorCode.Value = 10028 Then Return ' HandleRateLimitError owns 10028

            Dim errorMessage = errorField.SelectToken("message")?.ToString()
            Dim errorData = errorField.SelectToken("data")?.ToString(Newtonsoft.Json.Formatting.None)

            AppendColoredText(txtLogs,
                $"API ERROR (id {If(messageId?.ToString(), "-")}{RequestNameForId(messageId)}): " &
                $"code {If(errorCode?.ToString(), "?")} - {errorMessage}" &
                $"{If(errorData IsNot Nothing, " | " & errorData, "")}",
                Color.Red)
        Catch
            ' Parse noise / unexpected shapes: ignore, like the other handlers.
        End Try
    End Sub

    ' Best-effort request-class hint for the ad-hoc id space (see CODE_AUDIT_FABLE5.md F15).
    Private Function RequestNameForId(messageId As Integer?) As String
        If Not messageId.HasValue Then Return ""
        Select Case messageId.Value
            Case 2 : Return " auth/entry order"
            Case 30 : Return " cancel-all/trailing stop"
            Case 1 : Return " subscribe/reduce order"
            Case 1001 : Return " set_heartbeat"
            Case 223344, 223345, 223346, 223347, 223348, 223349, 223350 : Return " order edit"
            Case Else : Return ""
        End Select
    End Function

    ' Consumes success/error responses for entry placements (ids >= PlacementIdBase). On rejection:
    ' restores the pre-placement engine snapshots (placedPrice/placedStopLossPrice were seeded
    ' optimistically at send) and completes the ack. On success: completes the ack with the order id.
    ' Runs on the receive thread - engine fields + self-marshalling output only.
    Private Sub HandlePlacementResponse(response As String)
        Try
            If response.IndexOf("""id""", StringComparison.Ordinal) < 0 Then Return
            Dim json = JObject.Parse(response)
            Dim messageId = json.SelectToken("id")?.ToObject(Of Integer)()
            If Not (messageId.HasValue AndAlso messageId.Value >= PlacementIdBase) Then Return

            Dim entry As PendingPlacement = Nothing
            If Not pendingPlacements.TryRemove(messageId.Value, entry) Then Return ' unknown/stale id

            Dim errorField = json.SelectToken("error")
            If errorField IsNot Nothing Then
                Dim code = errorField.SelectToken("code")?.ToObject(Of Integer)()
                Dim msg = errorField.SelectToken("message")?.ToString()
                Dim data = errorField.SelectToken("data")?.ToString(Newtonsoft.Json.Formatting.None)
                ' Rollback: restore, don't zero (a zero could stall an actively-trailing SL).
                placedPrice = entry.PrevPlacedPrice
                placedStopLossPrice = entry.PrevPlacedSL
                UiInvoke(Sub()
                             txtPlacedPrice.Text = entry.PrevPlacedPrice.ToString("F2")
                             txtPlacedStopLossPrice.Text = entry.PrevPlacedSL.ToString("F2")
                         End Sub)
                AppendColoredText(txtLogs,
                    $"ORDER REJECTED (id {messageId.Value}): code {If(code?.ToString(), "?")} - {msg}{If(data IsNot Nothing, " | " & data, "")} - engine state rolled back",
                    Color.Red)
                entry.Tcs?.TrySetResult(New PlacementResult With {.Accepted = False, .Reason = $"{code}: {msg}"})
                Return
            End If

            Dim orderId = json.SelectToken("result.order.order_id")?.ToString()
            entry.Tcs?.TrySetResult(New PlacementResult With {.Accepted = True, .OrderId = orderId})
        Catch
            ' Parse noise: ignore, like the other handlers.
        End Try
    End Sub

    Private Async Function InitializeRateLimitsAfterAuth() As Task
        Try
            AppendColoredText(txtLogs, "Updating rate limits from account summary...", Color.DodgerBlue)

            ' Try to get actual account limits (this should work now that we're authenticated)
            Dim actualLimits = Await GetAccountSummaryLimitsWithTimeout(3000) ' 3 second timeout

            If actualLimits IsNot Nothing Then
                ' Update with real limits
                rateLimiter = New DeribitRateLimiter(actualLimits.MaxCredits, 50)
                AppendColoredText(txtLogs, $"Rate limits updated: {actualLimits.MaxCredits} max credits, {actualLimits.MaxCredits / 50} req/sec", Color.LimeGreen)
            Else
                ' Keep using emergency rate limiter
                AppendColoredText(txtLogs, "Using emergency rate limiter: 2000 credits, 40 req/sec", Color.Yellow)
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Rate limit update error: {ex.Message}", Color.Yellow)
            ' Keep existing emergency rate limiter
        End Try
    End Function

    Private Async Function GetAccountSummaryLimitsWithTimeout(timeoutMs As Integer) As Task(Of RateLimitInfo)
        Try
            Using cts As New CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs))
                accountSummaryTaskCompletionSource = New TaskCompletionSource(Of RateLimitInfo)

                ' Send account summary request
                Dim payload As New JObject From {
                {"jsonrpc", "2.0"},
                {"id", 999},
                {"method", "private/get_account_summary"},
                {"params", New JObject From {{"currency", "BTC"}, {"extended", True}}}
            }

                Await SendWebSocketMessageAsync(payload.ToString())

                ' Wait for response with timeout
                Return Await accountSummaryTaskCompletionSource.Task.WaitAsync(cts.Token)
            End Using

        Catch ex As TaskCanceledException
            AppendColoredText(txtLogs, "Account summary request timed out - using fallback", Color.Yellow)
            Return Nothing
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Account summary error: {ex.Message}", Color.Yellow)
            Return Nothing
        End Try
    End Function


    'Price-related subscriptions below
    '---------------------------------------------------------------------------------------------------------
    Private Async Function SubscribeToIndexPrice() As Task
        ' Subscribe to the BTC-PERPETUAL index price and user portfolio
        Dim subscriptionPayload = New JObject(
                New JProperty("jsonrpc", "2.0"),
                New JProperty("id", 1),
                New JProperty("method", "public/subscribe"),
                New JProperty("params", New JObject(
                    New JProperty("channels", New JArray("deribit_price_index.btc_usd"))
                ))
            )

        'New JProperty("channels", New JArray("perpetual.BTC-PERPETUAL.raw"))

        Await SendWebSocketMessageAsync(subscriptionPayload.ToString())
    End Function

    Private Async Function SubscribeToUserPortfolio() As Task
        ' Subscribe to the BTC-PERPETUAL index price and user portfolio
        Dim subscriptionPayload = New JObject(
                New JProperty("jsonrpc", "2.0"),
                New JProperty("id", 4),
                New JProperty("method", "private/subscribe"),
                New JProperty("params", New JObject(
                    New JProperty("channels", New JArray("user.portfolio.btc"))
                ))
            )

        Await SendWebSocketMessageAsync(subscriptionPayload.ToString())
    End Function

    Private Async Function SubscribeToQuoteBTCPerpetual() As Task
        ' Create the subscription payload
        Dim subscriptionPayload = New JObject(
        New JProperty("jsonrpc", "2.0"),
        New JProperty("id", 6), ' Ensure a unique ID for this subscription
        New JProperty("method", "public/subscribe"),                      'Test if private/subscribe will work
        New JProperty("params", New JObject(
            New JProperty("channels", New JArray("quote.BTC-PERPETUAL"))
        ))
    )
        Await SendWebSocketMessageAsync(subscriptionPayload.ToString())
    End Function

    Private Async Function SubscribeToUserOrders() As Task
        ' Subscribe to the BTC-PERPETUAL index price and user portfolio
        Dim subscriptionPayload = New JObject(
                New JProperty("jsonrpc", "2.0"),
                New JProperty("id", 20),
                New JProperty("method", "private/subscribe"),
                New JProperty("params", New JObject(
                    New JProperty("channels", New JArray("user.changes.BTC-PERPETUAL.raw"))
                ))
            )

        Await SendWebSocketMessageAsync(subscriptionPayload.ToString())
    End Function

    'All Subscription Update Handling below
    '---------------------------------------------------------------------------------------------

    'Handle Index price updates from Websocket

    Private Async Sub HandleHeartbeat(response As String)
        Try
            ' Parse the WebSocket response
            Dim json = JObject.Parse(response)

            ' Check if the response is heartbeat request from server
            Dim messageType = json.SelectToken("method")?.ToString()

            ' Handle ping pong messages
            'Dim messageType2 As String = json.SelectToken("result")?.ToString()

            ' If messageType2 = "pong" Then
            ' txtLogs.AppendText("Pong" + Environment.NewLine)
            ' End If

            If messageType = "heartbeat" Then

                'Await SendWebSocketMessageAsync("{""jsonrpc"":""2.0"",""id"":4,""method"":""public/ping"",""params"":{}}")

                Me.Invoke(Sub()
                              '              txtLogs.AppendText("REQ. received." + Environment.NewLine)
                              radHeartBeat.BackColor = Color.Crimson
                          End Sub)

                Await SendWebSocketMessageAsync("{""jsonrpc"":""2.0"",""id"":4,""method"":""public/test"",""params"":{}}")

                Await Task.Delay(500)

                Me.Invoke(Sub()
                              'txtLogs.AppendText("ACK. sent." + Environment.NewLine)
                              radHeartBeat.BackColor = Color.Black
                          End Sub)

                ' Checks if got error message
                Dim errorField = json.SelectToken("error")
                If errorField IsNot Nothing Then
                    'txtLogs.AppendText("Error: " & errorField.ToString() + Environment.NewLine)
                    AppendColoredText(txtLogs, "Error: " & errorField.ToString(), Color.Yellow)
                End If

            End If
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in HandleHeartbeat: {ex.Message}", Color.Red)
        End Try
    End Sub

    Private Sub HandleIndexUpdates(response As String)
        Try
            ' Parse the WebSocket response
            Dim json = JObject.Parse(response)

            ' Check if the response is for the deribit_price_index.btc_usd channel
            Dim channel = json.SelectToken("params.channel")?.ToString()
            If channel = "deribit_price_index.btc_usd" Then
                ' Extract the index price
                Dim indexPrice As String = json.SelectToken("params.data.price")
                Dim comms As Decimal = Nothing

                comms = 0.0005 * indexPrice
                comms = Math.Abs(Math.Round(comms, 0, MidpointRounding.AwayFromZero))

                ' Update engine fields first (cross-thread fix: HandleBalanceUpdates reads indexPriceVal,
                ' not lblIndexPrice.Text), then mirror the display via UiInvoke.
                If indexPrice IsNot Nothing And IsNumeric(indexPrice) Then
                    indexPriceVal = CDec(indexPrice)
                    commsVal = comms
                    UiInvoke(Sub()
                                 lblIndexPrice.Text = indexPrice
                                 txtComms.Text = comms
                             End Sub)
                End If

                ' Checks if got error message
                Dim errorField = json.SelectToken("error")
                If errorField IsNot Nothing Then
                    'txtLogs.AppendText("Error: " & errorField.ToString() + Environment.NewLine)
                    AppendColoredText(txtLogs, "Error: " & errorField.ToString(), Color.Yellow)
                End If

            End If
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in HandleIndexUpdates: {ex.Message}", Color.Red)
        End Try
    End Sub

    'Handle user portfolio updates from Websocket
    Private Sub HandleBalanceUpdates(response As String)
        Try
            ' Parse the WebSocket response
            Dim json = JObject.Parse(response)

            ' Check if the response is for the deribit_price_index.btc_usd channel
            Dim channel = json.SelectToken("params.channel")?.ToString()
            If channel = "user.portfolio.btc" Then
                ' Extract the best bid and ask prices
                Dim btcEquity As Decimal = json.SelectToken("params.data.equity")
                Dim btcBalance As Decimal = json.SelectToken("params.data.balance")
                Dim btcSession As Decimal = btcEquity - btcBalance

                ' Cross-thread fix: keep equity for the engine (GetEquityBTC) and convert to USD using the
                ' indexPriceVal field (not lblIndexPrice.Text), then push every label + colour through ONE
                ' marshalled block (the ForeColor block used to run unguarded on the receive thread).
                equityBTCVal = btcEquity
                Dim idx As Decimal = indexPriceVal

                If idx > 0D Then
                    Dim USDEquity As Decimal = idx * btcEquity
                    Dim Equiv As Decimal = idx * btcBalance
                    Dim USDSession As Decimal = idx * btcSession
                    USDPublicSession = USDSession 'For circuitbreaker in auto trading in frmindicators
                    Dim sessionColor As Color = If(btcSession < 0, Color.Firebrick, Color.ForestGreen)

                    UiInvoke(Sub()
                                 lblBTCEquity.Text = btcEquity.ToString("F8")
                                 lblUSDEquity.Text = USDEquity.ToString("C", CultureInfo.CreateSpecificCulture("en-US"))
                                 lblBalance.Text = btcBalance.ToString("F8")
                                 lblEquiv.Text = Equiv.ToString("C", CultureInfo.CreateSpecificCulture("en-US"))
                                 lblBTCSession.Text = btcSession.ToString("F8")
                                 lblUSDSession.Text = USDSession.ToString("C", CultureInfo.CreateSpecificCulture("en-US"))

                                 lblBTCEquity.ForeColor = sessionColor
                                 lblBTCSession.ForeColor = sessionColor
                                 lblUSDEquity.ForeColor = sessionColor
                                 lblUSDSession.ForeColor = sessionColor
                             End Sub)
                End If

                ' Checks if got error message
                Dim errorField = json.SelectToken("error")
                If errorField IsNot Nothing Then
                    'txtLogs.AppendText("Error: " & errorField.ToString() + Environment.NewLine)
                    AppendColoredText(txtLogs, "Error: " & errorField.ToString(), Color.Yellow)
                End If
            End If
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in HandleBalanceUpdates: {ex.Message}", Color.Red)
        End Try
    End Sub

    'Modify below to control how often stop loss is repositioned
    Private lastStopLossUpdate As DateTime = DateTime.MinValue
    Private Const MinStopLossUpdateInterval As Integer = 333 ' 0.3 second minimum between updates
    Private Const MinPriceMovementThreshold As Decimal = 5D ' Minimum $5 movement to trigger update
    Private newPricePublic As Decimal = 0 'For storing the price during emergency reduce market order for logging

    ' #4 retry-amplifier fix: bounded backoff for a failed triggered-SL reposition. The old code reset
    ' lastStopLossUpdate = DateTime.MinValue on error, which cleared the throttle so a persistent failure
    ' retried on EVERY quote tick (amplifying the edit storm). Instead, escalate the next-allowed time
    ' (capped at 5s) by pushing lastStopLossUpdate forward; a success resets the counter.
    Private slUpdateFailures As Integer = 0
    Private Const SLUpdateMaxBackoffMs As Double = 5000
    Private Sub BackoffStopLossRetry(failedAt As DateTime)
        slUpdateFailures = Math.Min(slUpdateFailures + 1, 8)
        Dim backoffMs As Double = Math.Min(MinStopLossUpdateInterval * (2 ^ slUpdateFailures), SLUpdateMaxBackoffMs)
        ' Gate is (now - lastStopLossUpdate) >= MinInterval, so this delays the next attempt to failedAt + backoffMs.
        lastStopLossUpdate = failedAt.AddMilliseconds(backoffMs - MinStopLossUpdateInterval)
    End Sub

    ' #5: single-flight guard for the order-reposition section of HandleQuoteUpdates. 0 = idle, 1 = a
    ' reposition is awaiting (Interlocked). Mirrors isReconnecting. Does NOT cover the triggered-SL
    ' emergency block or the price/PnL labels - those run every tick.
    Private isRepositioning As Integer = 0

    ' #6: throttle for hot-path parse warnings so a held-down blank field can't spam the log
    Private lastParseWarn As DateTime = DateTime.MinValue
    Private Sub WarnParseThrottled(message As String)
        If (DateTime.UtcNow - lastParseWarn).TotalSeconds >= 5 Then
            lastParseWarn = DateTime.UtcNow
            AppendColoredText(txtLogs, message, Color.Gray)
        End If
    End Sub

    'Handle best bid/asks updates from Websocket
    Private Async Sub HandleQuoteUpdates(response As String)
        Try
            ' Parse the WebSocket response
            Dim json = JObject.Parse(response)

            ' Check if the response is for the quote.BTC-PERPETUAL channel
            Dim channel = json.SelectToken("params.channel")?.ToString()
            If channel = "quote.BTC-PERPETUAL" Then
                ' Extract the best bid and ask prices
                Dim bestBid = json.SelectToken("params.data.best_bid_price")?.ToObject(Of Decimal)()
                Dim bestAsk = json.SelectToken("params.data.best_ask_price")?.ToObject(Of Decimal)()

                ' Update public variables and textboxes on the UI thread
                If bestBid IsNot Nothing Then
                    BestBidPrice = bestBid
                    Me.Invoke(Sub()
                                  txtTopBid.Text = BestBidPrice.ToString("F2")
                              End Sub)
                End If

                If bestAsk IsNot Nothing Then
                    BestAskPrice = bestAsk
                    Me.Invoke(Sub()
                                  txtTopAsk.Text = BestAskPrice.ToString("F2")
                              End Sub)
                End If

                ' Cross-thread fix: hot-path decisions read engine fields, NEVER the controls. placedPrice is
                ' set at placement, from the exchange's open EntryLimitOrder, and after each reposition below;
                ' orderAmountVal mirrors txtAmount. A 0 field means "not set" (same as the old blank/#6 case).
                Dim placedPriceValid As Boolean = placedPrice > 0D
                Dim amountValid As Boolean = orderAmountVal > 0D

                ' Transition-race fix: suppress this warning while a cancel is pending - placedPrice = 0 with a
                ' still-set order context is exactly the expected transient state during a cancel.
                If (Not placedPriceValid) AndAlso (Not IsCancelPending()) AndAlso (CurrentOpenOrderId IsNot Nothing OrElse SLTriggered OrElse isTrailingPosition) Then
                    WarnParseThrottled("Placed price = 0 while an order context is active - skipping reposition/PnL this tick (expected briefly after a cancel; SL repositioning still runs)")
                End If

                'For keeping current order at top of orderbook. +/- 3 leeway to reduce too many edit orders sent
                ' #5: single-flight - acquire only when an order context is present; skip this tick's
                ' entry reposition if a previous tick's reposition is still in flight.
                ' Cross-thread fix #5: also gate on a live socket so edits aren't piled into a closing connection.
                ' Transition-race fix: don't edit an order we're cancelling (gate on Not IsCancelPending()).
                If IsWebSocketConnected _
                   AndAlso (Not IsCancelPending()) _
                   AndAlso ((CurrentOpenOrderId IsNot Nothing) And (CurrentTPOrderId IsNot Nothing) And (CurrentSLOrderId IsNot Nothing)) _
                   AndAlso Interlocked.Exchange(isRepositioning, 1) = 0 Then
                    Try
                        If TradeMode = True Then
                            If placedPriceValid AndAlso bestBid > (placedPrice + 3) Then
                                ' Add null check for rateLimiter
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    'Stop if repositioned past ATR slippage threshold
                                    If maxSlippageATRchecked And IsATRSlippageExcessive(bestBid, "LONG") Then
                                        Await CancelWorkingEntryCoreAsync("ATR slippage")
                                        'Return
                                    Else
                                        Await UpdateLimitOrderWithOTOCOAsync(bestBid)

                                        ' Runaway fix: advance engine state SYNCHRONOUSLY before the (non-blocking)
                                        ' display update, so the next tick's "bestBid > placedPrice + 3" reads the
                                        ' new price even if the textbox write is delayed/fails.
                                        If placedPrice > 0 Then
                                            AppendColoredText(txtLogs, $"Order repositioned: ${placedPrice:F2} → ${bestBid:F2}", Color.Yellow)
                                        End If

                                        placedPrice = bestBid
                                        UiInvoke(Sub() txtPlacedPrice.Text = bestBid)
                                    End If
                                Else
                                    ' Handle both null limiter and rate limiting scenarios
                                    If rateLimiter Is Nothing Then
                                        '-- First warn the log
                                        AppendColoredText(txtLogs, "Rate limiter not initialized – creating skipping order update", Color.Orange)

                                        '-- Fire-and-forget: get real limits without blocking the quote thread
                                        Dim _ignore = Task.Run(Async Function()
                                                                   Await InitializeRateLimits()
                                                               End Function)

                                        '-- Install a conservative limiter so the very next tick can proceed
                                        rateLimiter = New DeribitRateLimiter(1000, 50)

                                    Else
                                        ' Limiter exists but credits are currently insufficient
                                        AppendColoredText(txtLogs, "Skipping order update due to rate limits", Color.Orange)
                                    End If
                                End If
                            End If
                        Else
                            If placedPriceValid AndAlso bestAsk < (placedPrice - 3) Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    If maxSlippageATRchecked And IsATRSlippageExcessive(bestAsk, "SHORT") Then
                                        Await CancelWorkingEntryCoreAsync("ATR slippage")
                                        'Return
                                    Else
                                        Await UpdateLimitOrderWithOTOCOAsync(bestAsk)

                                        ' Runaway fix: advance engine state synchronously before the display mirror.
                                        If placedPrice > 0 Then
                                            AppendColoredText(txtLogs, $"Order repositioned: ${placedPrice:F2} → ${bestAsk:F2}", Color.Yellow)
                                        End If

                                        placedPrice = bestAsk
                                        UiInvoke(Sub() txtPlacedPrice.Text = bestAsk)
                                    End If
                                Else
                                    If rateLimiter Is Nothing Then
                                        AppendColoredText(txtLogs, "Rate limiter not initialized - skipping order update", Color.Orange)
                                        Dim _ignore = Task.Run(Async Function()
                                                                   Await InitializeRateLimits()
                                                               End Function)

                                        '-- Install a conservative limiter so the very next tick can proceed
                                        rateLimiter = New DeribitRateLimiter(1000, 50)
                                    Else
                                        AppendColoredText(txtLogs, "Skipping order update due to rate limits", Color.Orange)
                                    End If
                                End If
                            End If
                        End If
                    Finally
                        Interlocked.Exchange(isRepositioning, 0)
                    End Try
                End If

                'Reduce-limit reposition (docs/spec-reduce-reposition.md): keep a resting reduce-only
                'LIMIT order at top of book. Chase direction comes from the ORDER (reduceOrderIsBuy),
                'never TradeMode - a mode flip while the order rests must not invert the chase.
                'Same gate ordering as the entry block: IsCancelPending BEFORE the single-flight acquire.
                If IsWebSocketConnected _
                   AndAlso (Not IsCancelPending()) _
                   AndAlso ReduceOrderId IsNot Nothing _
                   AndAlso reduceOrderPrice > 0D AndAlso reduceOrderAmount > 0D _
                   AndAlso Interlocked.Exchange(isRepositioning, 1) = 0 Then
                    Try
                        If reduceOrderIsBuy Then
                            ' Closing a short: reduce BUY rests at the bid - chase up
                            If bestBid IsNot Nothing AndAlso bestBid > (reduceOrderPrice + 3) Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    rateLimiter.ConsumeCredits()
                                    Await SendReduceRepositionEdit(bestBid)
                                End If
                            End If
                        Else
                            ' Closing a long: reduce SELL rests at the ask - chase down
                            If bestAsk IsNot Nothing AndAlso bestAsk < (reduceOrderPrice - 3) Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    rateLimiter.ConsumeCredits()
                                    Await SendReduceRepositionEdit(bestAsk)
                                End If
                            End If
                        End If
                    Finally
                        Interlocked.Exchange(isRepositioning, 0)
                    End Try
                End If

                'For keeping triggered stop loss order at top of orderbook. +/- 5 leeway to reduce too many edit orders sent
                ' Cross-thread fix #5: also gate on a live socket so SL edits aren't piled into a closing connection.
                If IsWebSocketConnected AndAlso SLTriggered AndAlso PositionSLOrderId IsNot Nothing Then
                    Dim currentTime As DateTime = DateTime.UtcNow

                    ' Rate limiting: Only update if minimum time has passed
                    If (currentTime - lastStopLossUpdate).TotalMilliseconds >= MinStopLossUpdateInterval Then

                        ' Cross-thread fix: read the engine field, not txtPlacedStopLossPrice. placedStopLossPrice
                        ' is set at placement, from the exchange, and after each SL reposition below.
                        Dim currentStopPrice As Decimal = placedStopLossPrice
                        If currentStopPrice > 0 Then

                            ' Emergency condition: Check if price moved beyond emergency threshold (marketStopThreshold
                            ' mirrors txtMarketStopLoss). Blank OR 0 disables the emergency path; normal SL trailing below
                            ' still runs (deviation from the old TryParse, which treated "0" as an always-on threshold).
                            Dim emergencyThreshold As Decimal = marketStopThreshold
                            Dim emergencyThresholdValid As Boolean = marketStopThreshold > 0D
                            ' Restore hardening: an unknown baseline (0, e.g. after a restart before the
                            ' order-context snapshot lands) disables the emergency market-stop - otherwise
                            ' priceMovement below is measured from 0 and a short fires an INSTANT close
                            ' (bestBid - 0 >= threshold). Same philosophy as threshold-0-disables; normal
                            ' SL trailing further down is unaffected.
                            ' M.SL emergency baseline: the ACTUAL SL price once triggered (emergencyBaseline,
                            ' pinned at the trigger moment), else the trigger price (StopLossTriggerOriginal,
                            ' kept in sync with exchange-side moves). 0 => unknown => guard disables the stop.
                            Dim emgBaseline As Decimal = If(emergencyBaseline > 0D, emergencyBaseline, StopLossTriggerOriginal)
                            Dim baselineKnown As Boolean = emgBaseline > 0D
                            Dim priceMovement As Decimal = 0D

                            If TradeMode Then
                                priceMovement = emgBaseline - bestAsk
                            Else
                                priceMovement = bestBid - emgBaseline
                            End If

                            ' Call ForceStopLossUpdate if emergency conditions are met


                            If emergencyThresholdValid AndAlso baselineKnown AndAlso priceMovement >= emergencyThreshold Then
                                If marketStopLossChecked Then
                                    Await ForceStopLossUpdate(If(TradeMode, bestAsk, bestBid))
                                    Return ' Exit early after emergency update
                                End If
                            End If

                            'Normal conditions operation
                            Dim shouldUpdate As Boolean = False
                            Dim newStopPrice As Decimal = 0D

                            If TradeMode Then
                                ' Long position: Update when ask price moves significantly below current stop
                                If bestAsk < (currentStopPrice - MinPriceMovementThreshold) Then
                                    newStopPrice = bestAsk
                                    shouldUpdate = True
                                End If
                            Else
                                ' Short position: Update when bid price moves significantly above current stop
                                If bestBid > (currentStopPrice + MinPriceMovementThreshold) Then
                                    newStopPrice = bestBid
                                    shouldUpdate = True
                                End If
                            End If

                            ' Execute update if conditions are met
                            If shouldUpdate Then
                                Try
                                    ' Check if we should use force update instead of normal rate-limited update
                                    If emergencyThresholdValid AndAlso baselineKnown AndAlso priceMovement >= (emergencyThreshold * 0.5) Then ' 50% of emergency threshold
                                        Await ForceStopLossUpdate(newStopPrice)
                                    Else
                                        Await UpdateStopLossForTriggeredStopLossOrder(newStopPrice)
                                    End If

                                    ' Runaway fix: advance engine state synchronously before the display mirror.
                                    ' Reconcile fix (spec §4a): tie the emergency baseline to the same value so the M.SL
                                    ' emergency follows the app's OWN chase without relying on the (now-discriminated) echo -
                                    ' which is ignored for our own repositions. The commanded price was recorded at the send.
                                    placedStopLossPrice = newStopPrice
                                    emergencyBaseline = newStopPrice
                                    UiInvoke(Sub() txtPlacedStopLossPrice.Text = newStopPrice.ToString("F2"))
                                    lastStopLossUpdate = currentTime
                                    slUpdateFailures = 0   ' success clears the backoff

                                    AppendColoredText(txtLogs, $"SL repositioned: ${currentStopPrice:F2} → ${newStopPrice:F2}", Color.Orange)

                                Catch ex As Exception
                                    AppendColoredText(txtLogs, $"Critical SL update failed: {ex.Message}", Color.Red)

                                    ' #4 retry-amplifier fix: bounded backoff instead of DateTime.MinValue (which reset the
                                    ' throttle and retried every tick on a persistent failure, amplifying the storm).
                                    BackoffStopLossRetry(currentTime)
                                End Try
                            End If
                            'Else
                            '    AppendColoredText(txtLogs, "Invalid stop loss price for repositioning", Color.Yellow)
                        End If
                    Else
                        ' Log rate limiting (optional - can be removed to reduce noise)
                        Dim remainingMs = MinStopLossUpdateInterval - (currentTime - lastStopLossUpdate).TotalMilliseconds
                        If remainingMs > 1000 Then ' Only log if significant time remaining
                            AppendColoredText(txtLogs, $"SL update rate limited: {remainingMs / 1000:F1}s remaining", Color.Gray)
                        End If
                    End If
                End If



                'For keeping current order at top of orderbook for trailing stop loss orders. +/- 3 leeway to reduce too many edit orders sent
                ' #5: same single-flight guard - serialize trailing repositions with entry repositions.
                ' Cross-thread fix #5: also gate on a live socket so edits aren't piled into a closing connection.
                ' Transition-race fix: don't edit a trailing order we're cancelling (gate on Not IsCancelPending()).
                If IsWebSocketConnected _
                   AndAlso (Not IsCancelPending()) _
                   AndAlso ((CurrentOpenOrderId IsNot Nothing) And (CurrentSLOrderId IsNot Nothing) And (isTrailingStopLossPlaced = True)) _
                   AndAlso Interlocked.Exchange(isRepositioning, 1) = 0 Then
                    Try
                        If TradeMode = True Then
                            If placedPriceValid AndAlso bestBid > (placedPrice + 3) Then
                                ' Add null check for rateLimiter
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    If maxSlippageATRchecked And IsATRSlippageExcessive(bestAsk, "LONG") Then
                                        Await CancelWorkingEntryCoreAsync("ATR slippage")
                                        'Return
                                    Else
                                        Await UpdateStopLossForTrailingOrder(bestBid)
                                        placedPrice = bestBid
                                        UiInvoke(Sub() txtPlacedPrice.Text = bestBid)
                                    End If
                                Else
                                    ' Handle both null limiter and rate limiting scenarios
                                    If rateLimiter Is Nothing Then
                                        AppendColoredText(txtLogs, "Rate limiter not initialized - skipping trailing update", Color.Orange)
                                        Dim _ignore = Task.Run(Async Function()
                                                                   Await InitializeRateLimits()
                                                               End Function)

                                        '-- Install a conservative limiter so the very next tick can proceed
                                        rateLimiter = New DeribitRateLimiter(1000, 50)
                                    Else
                                        AppendColoredText(txtLogs, "Skipping trailing order update due to rate limits", Color.Orange)
                                    End If
                                End If
                            End If
                        Else
                            If placedPriceValid AndAlso bestAsk < (placedPrice - 3) Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    If maxSlippageATRchecked And IsATRSlippageExcessive(bestAsk, "SHORT") Then
                                        Await CancelWorkingEntryCoreAsync("ATR slippage")
                                        'Return
                                    Else
                                        Await UpdateStopLossForTrailingOrder(bestAsk)
                                        placedPrice = bestAsk
                                        UiInvoke(Sub() txtPlacedPrice.Text = bestAsk)
                                    End If
                                Else
                                    If rateLimiter Is Nothing Then
                                        AppendColoredText(txtLogs, "Rate limiter not initialized - skipping trailing update", Color.Orange)
                                        Dim _ignore = Task.Run(Async Function()
                                                                   Await InitializeRateLimits()
                                                               End Function)

                                        '-- Install a conservative limiter so the very next tick can proceed
                                        rateLimiter = New DeribitRateLimiter(1000, 50)
                                    Else
                                        AppendColoredText(txtLogs, "Skipping trailing order update due to rate limits", Color.Orange)
                                    End If
                                End If
                            End If
                        End If
                    Finally
                        Interlocked.Exchange(isRepositioning, 0)
                    End Try
                End If



                'Check if a trailing order is in position and current price has hit take profit price.
                'If yes, cancel stop loss and place trailing stop loss order
                ' #5: same single-flight guard - the trailing-TP trigger places an order; serialize it too.
                ' Cross-thread fix #5: also gate on a live socket so orders aren't sent into a closing connection.
                ' Transition-race fix: don't fire the trailing-TP trigger while a cancel is pending.
                If IsWebSocketConnected _
                   AndAlso (Not IsCancelPending()) _
                   AndAlso ((isTrailingPosition = True) And (isTrailingStopLossPlaced = True)) _
                   AndAlso Interlocked.Exchange(isRepositioning, 1) = 0 Then
                    Try
                        ' Cross-thread fix: trailing-trigger inputs come from engine fields, not controls. Manual TP
                        ' (manualTPval) overrides; otherwise derive from placedPrice + (tpOffset + comms) once a live
                        ' comms value has arrived. A 0/blank field is treated as "not set".
                        Dim haveTrigger As Boolean = False
                        If manualTPval > 0 Then
                            TPTrailprice = manualTPval
                            haveTrigger = True
                        ElseIf placedPriceValid AndAlso commsVal > 0 Then
                            TPTrailprice = If(TradeMode, placedPrice + (tpOffsetVal + commsVal), placedPrice - (tpOffsetVal + commsVal))
                            haveTrigger = True
                        End If

                        If haveTrigger Then
                            If TradeMode = True Then
                                If TPTrailprice <= bestAsk Then
                                    isTrailingStopLossPlaced = False
                                    Await TrailingStopLossOrderAsync()
                                End If
                            Else
                                If TPTrailprice >= bestBid Then
                                    isTrailingStopLossPlaced = False
                                    Await TrailingStopLossOrderAsync()
                                End If
                            End If
                        Else
                            WarnParseThrottled("Trailing TP inputs blank/invalid - skipping trailing trigger this tick")
                        End If
                    Finally
                        Interlocked.Exchange(isRepositioning, 0)
                    End Try
                End If

                ' Position model: when a position exists, display P/L against the avg-entry basis
                ' and the real size; otherwise keep the resting-order hypothetical (old behavior).
                Dim dispLong As Boolean = TradeMode
                Dim dispBasis As Decimal = placedPrice
                Dim dispAmt As Decimal = orderAmountVal
                If positionSizeUSD <> 0D AndAlso positionAvgEntry > 0D Then
                    dispLong = (positionSizeUSD > 0D)
                    dispBasis = positionAvgEntry
                    dispAmt = Math.Abs(positionSizeUSD)
                End If
                If dispBasis > 0D AndAlso dispAmt > 0D Then
                    Dim PnL As Decimal
                    If dispLong Then
                        PnL = (BestAskPrice - dispBasis) * (dispAmt / dispBasis)
                        If BestAskPrice < dispBasis Then
                            Me.Invoke(Sub()
                                          lblPnL.ForeColor = Color.Red
                                      End Sub)
                        Else
                            Me.Invoke(Sub()
                                          lblPnL.ForeColor = Color.Chartreuse
                                      End Sub)
                        End If
                    Else
                        PnL = (dispBasis - BestAskPrice) * (dispAmt / dispBasis)
                        If BestAskPrice > dispBasis Then
                            Me.Invoke(Sub()
                                          lblPnL.ForeColor = Color.Red
                                      End Sub)
                        Else
                            Me.Invoke(Sub()
                                          lblPnL.ForeColor = Color.Chartreuse
                                      End Sub)
                        End If
                    End If
                    Me.Invoke(Sub()
                                  lblPnL.Text = PnL.ToString("F2")
                              End Sub)
                Else
                    Me.Invoke(Sub()
                                  lblPnL.ForeColor = Color.Chartreuse
                                  lblPnL.Text = "0"
                              End Sub)
                End If
            End If
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in HandleQuoteUpdates: {ex.Message}", Color.Red)
        End Try
    End Sub

    'For margin calculations when in position
    Private Async Function GetLivePositionData(instrumentName As String) As Task
        If isRequestingLiveData Then Exit Function         ' secondary guard
        isRequestingLiveData = True
        Try
            ' Check rate limit before making API call
            If rateLimiter IsNot Nothing AndAlso Not rateLimiter.CanMakeRequest() Then
                Dim waitTime = rateLimiter.GetWaitTimeMs()
                AppendColoredText(txtLogs, $"Rate limit reached for position data, waiting {waitTime}ms", Color.Yellow)
                Await Task.Delay(waitTime)
            End If

            ' Consume credits for the API call
            If rateLimiter IsNot Nothing Then
                rateLimiter.ConsumeCredits()
            End If

            ' Create the get_position request (using different ID from estimation)
            Dim positionPayload = New JObject(
            New JProperty("jsonrpc", "2.0"),
            New JProperty("id", 777), ' Different ID for live position data
            New JProperty("method", "private/get_position"),
            New JProperty("params", New JObject(
                New JProperty("instrument_name", instrumentName)
            ))
        )

            Await SendWebSocketMessageAsync(positionPayload.ToString())

            'AppendColoredText(txtLogs, $"Live position data requested for {instrumentName}", Color.Cyan)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error requesting live position data: {ex.Message}", Color.Red)

        Finally
            isRequestingLiveData = False                ' unlock no matter what
        End Try
    End Function

    ' Restore hardening: send-only request for the open OTOCO children (id 778). The response
    ' drains through HandleOpenOrdersSnapshot once the receive loop starts. type omitted =>
    ' "all", so untriggered stop/trigger orders come back too.
    Private Async Function RequestOpenOrdersSnapshot() As Task
        Try
            If rateLimiter IsNot Nothing Then rateLimiter.ConsumeCredits()

            Dim payload = New JObject(
                New JProperty("jsonrpc", "2.0"),
                New JProperty("id", 778),
                New JProperty("method", "private/get_open_orders_by_instrument"),
                New JProperty("params", New JObject(
                    New JProperty("instrument_name", "BTC-PERPETUAL")
                ))
            )

            Await SendWebSocketMessageAsync(payload.ToString())
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error requesting open-orders snapshot: {ex.Message}", Color.Red)
        End Try
    End Function

    Private Function GetEquityBTC() As Decimal
        ' Cross-thread fix: return the engine field (mirrors lblBTCEquity, set in HandleBalanceUpdates)
        ' so callers on the receive thread (ProcessPositionData) never read the label.
        Return equityBTCVal
    End Function


    ' Variable to track the specific order ID of interest
    Private CurrentOpenOrderId, CurrentTPOrderId, CurrentSLOrderId As String
    Private PositionTPOrderId, PositionSLOrderId As String

    ' Reduce-limit reposition context (docs/spec-reduce-reposition.md). One tracked reduce order.
    ' Engine-owned; read/written on the receive thread - never read controls for these.
    Private ReduceOrderId As String = Nothing
    Private reduceOrderPrice As Decimal = 0D
    Private reduceOrderAmount As Decimal = 0D
    Private reduceOrderIsBuy As Boolean = False

    ' Position model (docs/spec-position-model.md): the engine's view of the ACTUAL position.
    ' size is signed USD (+long / -short) from the exchange; avg entry updates only while a
    ' position exists and RETAINS the just-closed basis on the flat echo (close-P/L reads it).
    ' Written on the receive thread; UI buttons read them (accepted Decimal torn-read class).
    Private positionSizeUSD As Decimal = 0D
    Private positionAvgEntry As Decimal = 0D
    ' Restart restore: one "Open position detected" announcement per connection (display only).
    Private positionRestoreAnnounced As Boolean = False

    ' Reliable close (docs/spec-close-completion-fix.md + review): the closing fill's P/L is captured
    ' in these fields so it survives across echoes - the fill and the flat-position update can arrive
    ' in SEPARATE user.changes messages. ApplyCloseFill writes them; CompletePositionClose (invoked on
    ' the size!=0 -> 0 transition) consumes and clears them. Cleared on a new-position open (stale guard).
    Private pendingCloseValid As Boolean = False
    Private pendingClosePorLAmt As Decimal = 0D
    Private pendingClosePorL As Boolean = True
    Private pendingCloseLabel As String = Nothing
    Private pendingCloseAmountUSD As Decimal = 0D
    Private pendingCloseWasLong As Boolean = False
    Private pendingCloseExecPrice As Decimal = 0D

    ' ============ Decouple v2 (docs/spec-decouple-v2.md) ============
    ' Unique JSON-RPC ids for entry placements (manual + API). Responses are consumed by
    ' HandlePlacementResponse; HandleUnhandledJsonRpcError skips this range (single owner).
    Private Const PlacementIdBase As Integer = 600000
    Private nextPlacementId As Integer = PlacementIdBase

    ' One entry per in-flight placement. Snapshots restore engine state on rejection
    ' (restore, not zero - a zero could stall an actively-trailing position SL).
    Private Class PendingPlacement
        Public RequestId As Integer
        Public Tcs As TaskCompletionSource(Of PlacementResult)   ' Nothing for manual placements
        Public PrevPlacedPrice As Decimal
        Public PrevPlacedSL As Decimal
        Public CreatedUtc As DateTime = DateTime.UtcNow
    End Class
    Private ReadOnly pendingPlacements As New ConcurrentDictionary(Of Integer, PendingPlacement)

    ' Ack result surfaced to API callers (and, later, over the IPC pipe).
    Public Class PlacementResult
        Public Property Accepted As Boolean
        Public Property OrderId As String     ' entry order id when accepted
        Public Property Reason As String      ' reject reason / "timeout" / gate refusal
    End Class

    ' Registers a placement just before its send. If the API pre-registered this id (Tcs attached),
    ' keep that entry; otherwise create a snapshot-only entry. Sweeps stale entries (>60s) as hygiene.
    Private Sub RegisterPendingPlacement(reqId As Integer)
        If Not pendingPlacements.ContainsKey(reqId) Then
            pendingPlacements(reqId) = New PendingPlacement With {.RequestId = reqId}
        End If
        Dim entry = pendingPlacements(reqId)
        entry.PrevPlacedPrice = placedPrice
        entry.PrevPlacedSL = placedStopLossPrice
        For Each stale In pendingPlacements.Values.Where(Function(pp) (DateTime.UtcNow - pp.CreatedUtc).TotalSeconds > 60).ToList()
            Dim removed As PendingPlacement = Nothing
            pendingPlacements.TryRemove(stale.RequestId, removed)
        Next
    End Sub

    Private isTrailingStop As Boolean = False
    Private isTrailingPosition As Boolean = False
    Private PositionEmpty As Boolean = False
    Private PositionLog As Boolean = False 'Flag to track if position log has been written
    Private OrderLog As Boolean = False 'Flag to track if order log has been written

    Private Async Sub HandleOrderPositionUpdates(response As String)
        Try

            ' Parse the WebSocket response
            Dim json = JObject.Parse(response)

            ' Check if the message is from the "user.changes.BTC-PERPETUAL.raw" channel
            Dim channel = json.SelectToken("params.channel")?.ToString()
            If channel = "user.changes.BTC-PERPETUAL.raw" Then
                Dim orderData = json.SelectToken("params.data")

                ' Check if the update relates to orders
                If orderData IsNot Nothing Then
                    ' Position model: update from EVERY positions echo, independent of the
                    ' order-context gates below (fills from other sources / liquidations included).
                    ' Reliable close: detect the position going flat (!=0 -> 0) so the close can be completed
                    ' after the orders block below - even when THIS echo carries no orders array (the split
                    ' case that lost the close). New position (0 -> !=0) drops any stale close capture.
                    Dim positionJustClosed As Boolean = False
                    Dim posTokens = orderData.SelectToken("positions")?.ToObject(Of List(Of JObject))()
                    If posTokens IsNot Nothing Then
                        For Each p In posTokens
                            Dim sz = p.SelectToken("size")?.ToObject(Of Decimal?)()
                            If sz.HasValue Then
                                Dim wasOpen As Boolean = (positionSizeUSD <> 0D)
                                positionSizeUSD = sz.Value
                                If sz.Value <> 0D Then
                                    If Not wasOpen Then pendingCloseValid = False
                                    Dim avg = p.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                    If avg.HasValue AndAlso avg.Value > 0D Then positionAvgEntry = avg.Value
                                ElseIf wasOpen Then
                                    positionJustClosed = True
                                End If
                            End If
                        Next
                    End If

                    Dim orders = orderData.SelectToken("orders")?.ToObject(Of List(Of JObject))()
                    If orders IsNot Nothing AndAlso orders.Count > 0 Then
                        Dim OpenOrderNo As Boolean = False
                        Dim unTrigOrder As Boolean = False
                        Dim OpenPositions As Boolean = False
                        Dim ExecPrice As Decimal    ' close-fill input to ApplyCloseFill; P/L now persists in pendingClose* fields

                        For Each order In orders
                            ' Extract relevant fields
                            Dim orderState = order.SelectToken("order_state")?.ToString()
                            Dim orderId = order.SelectToken("order_id")?.ToString()
                            Dim label = order.SelectToken("label")?.ToString()

                            'Process desc.:
                            'Step 1. Placed order : EntryLimitOrder = Open / TakeLimitProfit + StopLossOrder = Untriggered
                            'Step 2. In position : EntryLimitOrder = Filled / TakeLimitProfit + StopLossOrder = Triggered AND new TakeLimitProfit + StopLossOrder = Open

                            ' Process only "open" orders
                            If (orderState = "open") Then

                                Dim price = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                Dim triggerPrice = order.SelectToken("trigger_price")?.ToObject(Of Decimal?)()

                                ' Update textboxes based on the label
                                Me.Invoke(Sub()

                                              Select Case label
                                                  Case "EntryLimitOrder"
                                                      ' Transition-race fix: ignore this echo while a cancel is pending (it confirms an order
                                                      ' we've already cancelled). Otherwise seed placedPrice only when the engine doesn't
                                                      ' already own it (placedPrice = 0) so a lagging echo can't reset it backward while the
                                                      ' quote handler is repositioning; the display mirrors the exchange only when we seed.
                                                      If Not cancelPending Then
                                                          If placedPrice = 0D Then
                                                              placedPrice = If(price, 0D)
                                                              txtPlacedPrice.Text = If(price?.ToString("F2"), "0")
                                                          End If
                                                          OpenOrderNo = True
                                                          OpenPositions = False
                                                          ' Save the current order_id for tracking
                                                          CurrentOpenOrderId = orderId
                                                          lblOrderStatus.Text = "Order Placed"
                                                          lblOrderStatus.ForeColor = Color.Chartreuse
                                                      End If

                                                      'When order is executed, TakeLimitProfit/StopLossOrder becomes 2 orders each
                                                      '- 1 with triggered state (The order before execution) and 1 with open state (Triggered by execution)
                                                  Case "TakeLimitProfit"
                                                      PositionTPOrderId = orderId

                                                      lblOrderStatus.Text = "In Position"
                                                      lblOrderStatus.ForeColor = Color.Yellow
                                                      OpenPositions = True
                                                      OpenOrderNo = False
                                                  Case "StopLossOrder"
                                                      PositionSLOrderId = orderId

                                                      ' Reconcile fix (spec-reconcile-manual-sl-edits.md): the M.SL emergency baseline still
                                                      ' follows the LIVE SL, but no longer via an UNGATED update on every echo (that also swallowed
                                                      ' lagging/out-of-order echoes of the app's own reposition and could walk the baseline
                                                      ' backward). It now moves under the SAME commanded-price discriminator as placedStopLossPrice,
                                                      ' in the reconciliation block below: seeded at the trigger moment, advanced with the app's own
                                                      ' chase at the reposition (placedStopLossPrice = newStopPrice), and set to the true value on
                                                      ' a detected manual edit.
                                                      SLTriggered = True

                                                      ' Restore hardening: defensive mid-session heal - if the baseline was lost
                                                      ' (0) but the SL triggers now, recover it from this echo's trigger_price so
                                                      ' the emergency market-stop math has a reference (triggerPrice in scope above).
                                                      If StopLossTriggerOriginal = 0D Then StopLossTriggerOriginal = If(triggerPrice, 0D)

                                                      'This groups CRITICAL SL, Triggered SL messages together
                                                      If orderId = lastSLId Then
                                                          'AppendColoredText(txtLogs, pendingLocalMsg, Color.Red)
                                                          pendingLocalMsg = "" : lastSLId = ""
                                                      End If

                                                      If UpdateFlag = False Then
                                                          AppendColoredText(txtLogs, $"Triggered SL placed @ ${price}", Color.Red)
                                                      End If

                                                      ' Triggered-SL reconciliation (spec-reconcile-manual-sl-edits.md, discriminator 4a +
                                                      ' policy P1). Both a MANUAL exchange-side SL move and a lagging echo of the app's OWN chase
                                                      ' arrive here with a price; the commanded-price set tells them apart. All under the existing
                                                      ' Not cancelPending gate (scoped/nuclear cancel semantics, invariant #3).
                                                      If Not cancelPending Then
                                                          ' Seed the emergency baseline at the trigger moment: it starts 0, while placedStopLossPrice
                                                          ' carries over non-zero from the untriggered leg (same order, same limit price), so its own
                                                          ' seed-if-zero below won't fire now. Keeps the M.SL emergency measuring from the live SL.
                                                          If emergencyBaseline = 0D Then emergencyBaseline = If(price, emergencyBaseline)

                                                          If placedStopLossPrice = 0D Then
                                                              ' Trigger-moment seed (transition-race single-writer): only when the engine doesn't
                                                              ' already own the price. A lagging echo can't reset placedStopLossPrice backward - that
                                                              ' path is the ElseIf, guarded by the commanded-price set.
                                                              placedStopLossPrice = If(price, 0D)
                                                              txtPlacedStopLossPrice.Text = If(price?.ToString("F2"), "0")
                                                          ElseIf price.HasValue AndAlso price.Value <> placedStopLossPrice _
                                                                 AndAlso Not IsRecentlyCommandedSLPrice(price.Value) Then
                                                              ' A triggered-SL price that DIFFERS from our reference and that we did NOT command == a
                                                              ' manual exchange-side edit. P1: follow it - correct BOTH the chase reference and the
                                                              ' emergency baseline to the true live SL, then keep chasing from there (the maker fill).
                                                              placedStopLossPrice = price.Value
                                                              emergencyBaseline = price.Value
                                                              txtPlacedStopLossPrice.Text = price.Value.ToString("F2")
                                                              AppendColoredText(txtLogs, $"Manual SL edit: ${price.Value:F2}", Color.Cyan)
                                                          End If
                                                          ' else (price is in the commanded set, or unchanged): the app's own reposition or a lagging
                                                          ' echo of it -> ignore. placedStopLossPrice + emergencyBaseline were already advanced at the
                                                          ' reposition (placedStopLossPrice = newStopPrice). Preserves today's runaway/transition-race protection.
                                                      End If

                                                      lblOrderStatus.Text = "Stop Loss Triggered"
                                                      lblOrderStatus.ForeColor = Color.Red
                                                      OpenPositions = True
                                                      OpenOrderNo = False
                                                  Case "EntryTrailingOrder"
                                                      ' Transition-race fix: same as EntryLimitOrder - ignore the echo while cancelling, and
                                                      ' seed placedPrice/display only when the engine doesn't already own the price.
                                                      If Not cancelPending Then
                                                          If placedPrice = 0D Then
                                                              placedPrice = If(price, 0D)
                                                              txtPlacedPrice.Text = If(price?.ToString("F2"), "0")
                                                          End If
                                                          If Decimal.Parse(txtManualTP.Text) > 0 Then
                                                              txtPlacedTakeProfitPrice.Text = txtManualTP.Text
                                                          Else
                                                              If TradeMode = True Then
                                                                  txtPlacedTakeProfitPrice.Text = Decimal.Parse(If(price?.ToString("F2"), "0")) + ((Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text)))
                                                              Else
                                                                  txtPlacedTakeProfitPrice.Text = Decimal.Parse(If(price?.ToString("F2"), "0")) - ((Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text)))
                                                              End If

                                                          End If

                                                          OpenOrderNo = True
                                                          OpenPositions = False
                                                          isTrailingStop = True     'For checking if is trailing order when executing In Position code
                                                          isTrailingPosition = False  'For sanity confirm that it is not in position
                                                          ' Save the current order_id for tracking
                                                          CurrentOpenOrderId = orderId
                                                          lblOrderStatus.Text = "Order Placed"
                                                          lblOrderStatus.ForeColor = Color.Chartreuse
                                                      End If
                                                  Case "TrailingStopLoss"
                                                      lblOrderStatus.Text = "In Position"
                                                      lblOrderStatus.ForeColor = Color.Yellow
                                                      OpenPositions = True
                                                      OpenOrderNo = False
                                                      isTrailingStop = True     'For checking if is trailing order when executing In Position code
                                                      isTrailingPosition = True
                                                      isTrailingStopLossPlaced = False  'For sanity check that trailing stop loss has been placed
                                                      ' Save the current order_id for tracking
                                                      CurrentOpenOrderId = orderId
                                                  Case "ReduceLimitOrder"
                                                      ' Reposition context: capture the id; seed price/amount only when
                                                      ' the engine doesn't already own them (single-writer). Direction
                                                      ' always refreshed from the exchange (source of truth).
                                                      If Not cancelPending Then
                                                          ReduceOrderId = orderId
                                                          reduceOrderIsBuy = (order.SelectToken("direction")?.ToString() = "buy")
                                                          If reduceOrderPrice = 0D Then
                                                              reduceOrderPrice = If(price, 0D)
                                                          End If
                                                          If reduceOrderAmount = 0D Then
                                                              reduceOrderAmount = If(order.SelectToken("amount")?.ToObject(Of Decimal?)(), 0D)
                                                          End If
                                                      End If
                                                  Case "ReduceMarketOrder" ' (no-op here; market reduces are not repositioned)
                                              End Select
                                          End Sub)

                                'If UpdateFlag = True Then
                                ' If label = "StopLossOrder" Then
                                'AppendColoredText(txtLogs, $"Updated to: ${price}", Color.Crimson)
                                'Else
                                '   AppendColoredText(txtLogs, $"Updated to: ${price}", Color.Yellow)
                                'End If
                                'UpdateFlag = False
                                'End If


                            ElseIf (orderState = "untriggered") Then
                                'Dim price = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                'Dim triggerPrice = order.SelectToken("trigger_price")?.ToObject(Of Decimal?)()

                                ' Update textboxes based on the label
                                ' Transition-race fix: while a cancel is pending, ignore these child-leg echoes -
                                ' they confirm untriggered TP/SL legs we've already cancelled, and re-populating
                                ' CurrentTPOrderId/CurrentSLOrderId would re-arm the reposition on a dead context.
                                Me.Invoke(Sub()
                                              If cancelPending Then Return
                                              Select Case label
                                                  Case "TakeLimitProfit"
                                                      Dim price = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                                      Dim triggerPrice = order.SelectToken("trigger_price")?.ToObject(Of Decimal?)()

                                                      txtPlacedTakeProfitPrice.Text = If(price?.ToString("F2"), "0")
                                                      unTrigOrder = True
                                                      CurrentTPOrderId = orderId
                                                  Case "StopLossOrder"
                                                      Dim price = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                                      Dim triggerPrice = order.SelectToken("trigger_price")?.ToObject(Of Decimal?)()

                                                      txtPlacedTrigStopPrice.Text = If(triggerPrice?.ToString("F2"), "0")
                                                      txtPlacedStopLossPrice.Text = If(price?.ToString("F2"), "0")
                                                      placedStopLossPrice = If(price, 0D)   ' engine state mirrors exchange SL price (cross-thread fix)
                                                      ' Item 2: keep the trigger baseline in sync with exchange-side trigger moves while
                                                      ' UNTRIGGERED (this is the pre-trigger emergency baseline; replaces the manual btnMark
                                                      ' re-sync). Not trailing yet, so an unconditional mirror is safe - like placedStopLossPrice.
                                                      StopLossTriggerOriginal = If(triggerPrice, StopLossTriggerOriginal)
                                                      unTrigOrder = True
                                                      CurrentSLOrderId = orderId
                                                      PositionSLOrderId = orderId
                                                  Case "TrailingStopLoss"
                                                      'Trigger-price is received from channel only when order is placed/triggered/filled.
                                                      'It doesn't update when market price moves and it dynamically adjusts.

                                                      Dim triggerPrice = order.SelectToken("trigger_price")?.ToObject(Of Decimal?)()
                                                      AppendColoredText(txtLogs, $"Trigger Price: ${triggerPrice}", Color.Yellow)
                                                      txtPlacedTakeProfitPrice.Text = If(triggerPrice?.ToString("F2"), "0")
                                                      unTrigOrder = True
                                                      CurrentTPOrderId = orderId
                                              End Select
                                          End Sub)

                            ElseIf (orderState = "filled") Then
                                ' Cross-thread fix: this branch runs on the receive thread (NOT wrapped in Me.Invoke
                                ' like open/untriggered), so PnL math reads engine fields (placedPrice/orderAmountVal)
                                ' and the lblOrderStatus writes are marshalled via UiInvoke.
                                Select Case label
                                    Case "EntryLimitOrder"
                                        UiInvoke(Sub()
                                                     lblOrderStatus.Text = "In Position"
                                                     lblOrderStatus.ForeColor = Color.Yellow
                                                 End Sub)
                                        AppendColoredText(txtLogs, $"Position entered: {If(TradeMode, "LONG", "SHORT")} {orderAmountVal} @ ${placedPrice:F2}", Color.LimeGreen)
                                        OpenPositions = True
                                        OpenOrderNo = False
                                        UpdateFlag = False
                                    Case "TakeLimitProfit"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                        ApplyCloseFill(order, ExecPrice, label)

                                    Case "StopLossOrder"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                        SLTriggered = False
                                        ApplyCloseFill(order, ExecPrice, label)

                                    Case "EntryTrailingOrder"
                                        UiInvoke(Sub()
                                                     lblOrderStatus.Text = "In Position"
                                                     lblOrderStatus.ForeColor = Color.Yellow
                                                 End Sub)
                                        AppendColoredText(txtLogs, $"Position entered: {If(TradeMode, "LONG", "SHORT")} {orderAmountVal} @ ${placedPrice:F2}", Color.LimeGreen)
                                        OpenPositions = True
                                        OpenOrderNo = False
                                        isTrailingStop = True 'For checking if is trailing order when executing In Position code
                                        isTrailingPosition = False   'For sanity confirm that it is not in position
                                        UpdateFlag = False
                                    Case "TrailingStopLoss"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                        ApplyCloseFill(order, ExecPrice, label)

                                    Case "ReduceLimitOrder"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                        ApplyCloseFill(order, ExecPrice, label)

                                        ' Reduce order gone from the book - drop the reposition context.
                                        ReduceOrderId = Nothing
                                        reduceOrderPrice = 0D
                                        reduceOrderAmount = 0D

                                    Case "ReduceMarketOrder"
                                        OpenPositions = True 'Actually no positions but flagged true to use code in openpositions segment for cleanup
                                        If _indicators.IsAutoTradingEnabled Then
                                            LogTradeDecision("Exit Position - Market Order Loss", 0, 0) 'For autotrade log for when trade exit position
                                        End If
                                        ResetOrderAttempt() ' Reset ATR slippage tracking
                                        ' Audit2 fix 6 + position model: log the echo's actual fill price and track the
                                        ' close like every other fill (P/L vs avg entry; emergency closes hit the stats).
                                        Dim reduceFill = order.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                        ExecPrice = If(reduceFill, 0D)
                                        AppendColoredText(txtLogs, $"Position reduced at {If(reduceFill?.ToString("F2"), If(newPricePublic > 0D, newPricePublic.ToString("F2"), "?"))} (market order).", Color.Crimson)
                                        ApplyCloseFill(order, ExecPrice, label)

                                End Select
                            ElseIf orderState = "cancelled" Then
                                ' Transition-race fix: the exchange has confirmed a cancel - clear cancelPending so
                                ' repositioning/echo-seeding can resume for the next order context. The IDs stay null
                                ' (reset in CancelOrderAsync); a fresh placement or a genuine open echo re-establishes them.
                                cancelPending = False
                                Select Case label
                                    Case "TakeLimitProfit"
                                        OpenPositions = True
                                    Case "StopLossOrder"
                                        OpenPositions = True
                                    Case "ReduceLimitOrder"
                                        OpenPositions = True
                                        ' Reduce order gone from the book - drop the reposition context.
                                        ReduceOrderId = Nothing
                                        reduceOrderPrice = 0D
                                        reduceOrderAmount = 0D
                                End Select
                            End If

                        Next


                        If _indicators.IsAutoTradingEnabled And OrderLog = False Then
                            '    If Not (txtPlacedPrice.Text = "0") And (txtPlacedTrigStopPrice.Text = "0") And (txtPlacedTakeProfitPrice.Text = "0") Then
                            If (OpenPositions = False) And (OpenOrderNo = True) Then
                                LogTradeDecision("Order Placed", 0, 0) 'For autotrade log for when order is placed
                                OrderLog = True
                            End If
                        End If


                        If OpenPositions = True Then

                            ' Cross-thread fix: reset engine fields synchronously, mirror the controls via UiInvoke
                            ' (the TextChanged sync re-runs on the UI thread, keeping fields == textboxes).
                            manualSLval = 0D
                            manualTPval = 0D
                            UiInvoke(Sub()
                                         txtManualSL.Text = "0"
                                         txtManualTP.Text = "0"
                                     End Sub)


                            'If it is a trailing order, set flag that it is in position
                            If isTrailingStop = True Then
                                isTrailingPosition = True
                                'AppendColoredText(txtLogs, $"Trailing Position: ${isTrailingPosition}", Color.Yellow)
                            End If

                            ' === keep IDs alive while the ENTRY order is still open ===
                            'Dim entryStillPending As Boolean =
                            'orders.Any(Function(o) o.SelectToken("label")?.ToString() = "EntryLimitOrder" _
                            'AndAlso o.SelectToken("order_state")?.ToString() = "open")

                            'Stop autoplacement of orders at top of orderbook if position is found

                            'Dim tpPresent = orders.Any(Function(o) o.SelectToken("label")?.ToString() = "TakeLimitProfit")
                            'Dim slPresent = orders.Any(Function(o) o.SelectToken("label")?.ToString() = "StopLossOrder")

                            'If tpPresent AndAlso slPresent AndAlso Not entryStillPending Then
                            ' entry was filled/cancelled *and* both child legs are already on the book
                            CurrentOpenOrderId = Nothing
                            CurrentTPOrderId = Nothing
                            CurrentSLOrderId = Nothing
                            'End If

                            ' No orders found, check for positions
                            Dim positions = orderData.SelectToken("positions")?.ToObject(Of List(Of JObject))()
                            If positions IsNot Nothing AndAlso positions.Count > 0 Then

                                For Each position In positions

                                    Dim size = position.SelectToken("size")?.ToObject(Of Decimal)()

                                    If size <> 0 Then
                                        ' Prevent duplicate live data requests
                                        If Not isRequestingLiveData AndAlso (DateTime.Now - lastLiveDataRequest).TotalSeconds > 2 Then
                                            isRequestingLiveData = True
                                            lastLiveDataRequest = DateTime.Now

                                            Await GetLivePositionData("BTC-PERPETUAL")

                                            ' Reset flag after a delay
                                            Await Task.Delay(3000)
                                            isRequestingLiveData = False
                                        End If

                                        If _indicators.IsAutoTradingEnabled And (PositionLog = False) Then
                                            LogTradeDecision("In Position", 0, 0) 'For autotrade log for when trade is in position
                                            PositionLog = True ' Set flag to prevent duplicate logging
                                        End If

                                    Else
                                        ' Position closed - reset flags
                                        isRequestingLiveData = False
                                        lastLiveDataRequest = DateTime.MinValue

                                    End If

                                    ' Position-closed completion (message + DB record + flag/display cleanup)
                                    ' moved to CompletePositionClose, invoked after the orders block below so it
                                    ' also fires when the flat echo carries no orders array (the split case).

                                Next
                            End If



                        End If

                    End If

                    ' Reliable close: complete a !=0 -> 0 transition exactly once, even when the flat echo
                    ' carried no orders (the closing fill was captured in an earlier echo via pendingClose*).
                    If positionJustClosed Then Await CompletePositionClose()

                End If
            End If
            '            End If
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in HandleOrderPositionUpdates: {ex.Message}", Color.Red)
        End Try
    End Sub

    Private Sub HandleOrderUpdates(message As String)
        ' Log every received WebSocket message for debugging
        'AppendColoredText(txtLogs, "WebSocket Update Received: " & message, Color.LimeGreen)

        ' Check if the message contains any trigger price updates
        If message.Contains("trigger_price") Then
            AppendColoredText(txtLogs, "Detected Trigger Price Update: " & message, Color.LimeGreen)
        End If
    End Sub

    ' Add these variables to your order placement logic
    Private originalSignalPrice As Decimal = 0
    Private orderCreationTime As DateTime = DateTime.MinValue
    Private currentRequoteCount As Integer = 0

    Private Function CalculateATRSlippageLimit() As Decimal
        ' Cross-thread fix: read the engine fields, not controls. ATR comes from _indicators.CurrentATR
        ' (a backing field), the multiplier from maxSlippageATRmult (mirrors txtMaxSlippageATR).
        Dim currentATR As Decimal = If(_indicators IsNot Nothing, _indicators.CurrentATR, 0D)
        If currentATR <= 0D Then
            Return 70 ' Fallback to $70 if ATR unavailable
        End If

        ' Get ATR multiplier from settings (blank/0 -> default 0.6x ATR)
        Dim atrMultiplier As Decimal = If(maxSlippageATRmult > 0D, maxSlippageATRmult, 0.6D)

        Return currentATR * atrMultiplier
    End Function

    Private Function IsATRSlippageExcessive(currentPrice As Decimal, direction As String) As Boolean
        If originalSignalPrice = 0 Then
            originalSignalPrice = currentPrice ' Set initial price
            Return False
        End If

        Dim slippageLimit As Decimal = CalculateATRSlippageLimit()
        Dim actualSlippage As Decimal = Math.Abs(currentPrice - originalSignalPrice)

        ' Calculate slippage in ATR units for logging (cross-thread fix: read CurrentATR field, not lblATR)
        Dim currentATR As Decimal = If(_indicators IsNot Nothing, _indicators.CurrentATR, 0D)
        Dim slippageInATR As Decimal = If(currentATR > 0, actualSlippage / currentATR, 0)

        If actualSlippage > slippageLimit Then
            AppendColoredText(txtLogs, $"{direction} slippage ${actualSlippage:F2} ({slippageInATR:F2}x ATR) exceeds limit ${slippageLimit:F2}", Color.Red)

            ' Reset for next attempt
            ResetOrderAttempt() 'Temp fix - suppose to call LogFailedEntry below which is giving an error

            ' Log failed attempt
            'LogFailedEntry("ATR slippage exceeded", slippageInATR)
            Return True
        End If

        ' Log acceptable slippage
        'AppendColoredText(txtLogs, $"{direction} slippage ${actualSlippage:F2} ({slippageInATR:F2}x ATR) within limit", Color.Gray)
        Return False
    End Function

    Private Sub LogFailedEntry(reason As String, Optional slippageATR As Decimal = 0)
        ' Create failed trade record
        Dim failedTrade = New TradeRecord With {
        .AttemptType = "Failed",
        .OrderType = reason,
        .Timestamp = DateTime.UtcNow,
        .RequoteCount = currentRequoteCount,
        .SignalPrice = originalSignalPrice,
        .SlippageATR = slippageATR,
        .MaxSlippageExceeded = True
    }

        ' You could optionally save failed attempts to database for analysis

        ' Engage cooldown
        Dim cooloffMins = Integer.Parse(_autotradesettings.txtCooloff.Text)
        _indicators.lastAutoTradeTime = DateTime.Now.AddMinutes(cooloffMins)

        'AppendLog($"Entry failed: {reason}. Cooloff: {cooloffMins}min", Color.Orange)

        ' Reset for next attempt
        ResetOrderAttempt()
    End Sub

    Private Sub ResetOrderAttempt()
        currentRequoteCount = 0
        orderCreationTime = DateTime.MinValue
        originalSignalPrice = 0
    End Sub


    'All order execution code below
    '-----------------------------------------------------------------------
    Private Async Function ExecuteOrderAsync(TypeOfOrder As String, Optional requestId As Integer = 0) As Task
        Try
            Dim takeprofitprice As Decimal
            Dim stoplossTriggerPrice As Decimal
            Dim triggeroffset As Decimal
            Dim stoplossPrice As Decimal
            Dim BestPrice As Decimal
            Dim ordermethod As String = String.Empty ' Default value to avoid warnings
            Dim direction As String = String.Empty ' Default value for direction
            Dim ordertype As String = String.Empty
            Dim MarketOrderType As Boolean = False

            ' Ensure WebSocket is connected
            If webSocketClient Is Nothing OrElse webSocketClient.State <> WebSocketState.Open Then
                AppendColoredText(txtLogs, "WebSocket is not connected.", Color.Red)
                Return
            End If

            ' Validate the input amount
            Dim amountText As String = txtAmount.Text
            Dim amount As Decimal

            If Not Decimal.TryParse(amountText, amount) OrElse amount <= 0 Then
                AppendColoredText(txtLogs, "Please enter a valid positive amount.", Color.Yellow)
                Return
            End If

            Select Case TypeOfOrder
                Case "BuyLimit"

                    ' Ensure BestBidPrice is valid
                    If BestBidPrice <= 0 Then
                        AppendColoredText(txtLogs, "Best bid price is not valid.", Color.Yellow)
                        Return
                    End If

                    BestPrice = BestBidPrice

                    'If manual take profit textbox is not empty, use that
                    If Decimal.Parse(txtManualTP.Text) > 0 Then
                        takeprofitprice = Decimal.Parse(txtManualTP.Text)
                    Else
                        takeprofitprice = BestPrice + Decimal.Parse(txtTakeProfit.Text)
                    End If

                    takeprofitprice = If(Decimal.Parse(txtManualTP.Text) > 0, Decimal.Parse(txtManualTP.Text), BestPrice + Decimal.Parse(txtTakeProfit.Text))

                    'If manual stop loss textbox is not empty, use that
                    If Decimal.Parse(txtManualSL.Text) > 0 Then
                        stoplossPrice = Decimal.Parse(txtManualSL.Text)
                        stoplossTriggerPrice = Decimal.Parse(txtManualSL.Text) + Decimal.Parse(txtStopLoss.Text)
                    Else
                        stoplossTriggerPrice = BestPrice - Decimal.Parse(txtTrigger.Text)
                        stoplossPrice = BestPrice - (Decimal.Parse(txtStopLoss.Text) + Decimal.Parse(txtTrigger.Text))
                        'stoplossPrice = BestPrice - Decimal.Parse(txtTrigger.Text) + Decimal.Parse(txtStopLoss.Text)
                    End If

                    'For initiating ATR Slippage function
                    direction = "LONG"
                    If IsATRSlippageExcessive(BestPrice, direction) Then
                        Return
                    End If

                    triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                    ordermethod = "private/buy"
                    direction = "sell"
                    ordertype = "limit"


                Case "SellLimit"

                    ' Ensure BestAskPrice is valid
                    If BestAskPrice <= 0 Then
                        AppendColoredText(txtLogs, "Best ask price Is Not valid.", Color.Yellow)
                        Return
                    End If

                    BestPrice = BestAskPrice

                    'If manual take profit textbox is not empty, use that
                    If Decimal.Parse(txtManualTP.Text) > 0 Then
                        takeprofitprice = Decimal.Parse(txtManualTP.Text)
                    Else
                        takeprofitprice = BestPrice - Decimal.Parse(txtTakeProfit.Text)
                    End If

                    'If manual stop loss textbox is not empty, use that
                    If Decimal.Parse(txtManualSL.Text) > 0 Then
                        stoplossPrice = Decimal.Parse(txtManualSL.Text)
                        stoplossTriggerPrice = Decimal.Parse(txtManualSL.Text) - Decimal.Parse(txtStopLoss.Text)
                    Else
                        stoplossTriggerPrice = BestPrice + Decimal.Parse(txtTrigger.Text)
                        stoplossPrice = BestPrice + (Decimal.Parse(txtStopLoss.Text) + Decimal.Parse(txtTrigger.Text))
                        'stoplossPrice = BestPrice + Decimal.Parse(txtTrigger.Text) - Decimal.Parse(txtStopLoss.Text)
                    End If

                    'For initiating ATR Slippage function
                    direction = "SHORT"
                    If IsATRSlippageExcessive(BestPrice, direction) Then
                        Return
                    End If

                    triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                    ordermethod = "private/sell"
                    direction = "buy"
                    ordertype = "limit"


                Case "BuyNoSpread"

                    ' Ensure BestBidPrice is valid
                    If BestAskPrice <= 0 Then
                        AppendColoredText(txtLogs, "Best bid price Is Not valid.", Color.Yellow)
                        Return
                    End If

                    BestPrice = BestAskPrice - 0.5

                    'If manual take profit textbox is not empty, use that
                    If Decimal.Parse(txtManualTP.Text) > 0 Then
                        takeprofitprice = Decimal.Parse(txtManualTP.Text)
                    Else
                        takeprofitprice = BestPrice + Decimal.Parse(txtTakeProfit.Text)
                    End If

                    'If manual stop loss textbox is not empty, use that
                    If Decimal.Parse(txtManualSL.Text) > 0 Then
                        stoplossPrice = Decimal.Parse(txtManualSL.Text)
                        stoplossTriggerPrice = Decimal.Parse(txtManualSL.Text) + Decimal.Parse(txtStopLoss.Text)
                    Else
                        stoplossTriggerPrice = BestPrice - Decimal.Parse(txtTrigger.Text)
                        stoplossPrice = BestPrice - (Decimal.Parse(txtStopLoss.Text) + Decimal.Parse(txtTrigger.Text))
                    End If

                    'For initiating ATR Slippage function
                    direction = "LONG"
                    If IsATRSlippageExcessive(BestPrice, direction) Then
                        Return
                    End If

                    triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                    ordermethod = "private/buy"
                    direction = "sell"
                    ordertype = "limit"


                Case "SellNoSpread"

                    ' Ensure BestAskPrice is valid
                    If BestBidPrice <= 0 Then
                        AppendColoredText(txtLogs, "Best ask price Is Not valid.", Color.Yellow)
                        Return
                    End If

                    BestPrice = BestBidPrice + 0.5

                    'If manual take profit textbox is not empty, use that
                    If Decimal.Parse(txtManualTP.Text) > 0 Then
                        takeprofitprice = Decimal.Parse(txtManualTP.Text)
                    Else
                        takeprofitprice = BestPrice - Decimal.Parse(txtTakeProfit.Text)
                    End If

                    'If manual stop loss textbox is not empty, use that
                    If Decimal.Parse(txtManualSL.Text) > 0 Then
                        stoplossPrice = Decimal.Parse(txtManualSL.Text)
                        stoplossTriggerPrice = Decimal.Parse(txtManualSL.Text) - Decimal.Parse(txtStopLoss.Text)
                    Else
                        stoplossTriggerPrice = BestPrice + Decimal.Parse(txtTrigger.Text)
                        stoplossPrice = BestPrice + (Decimal.Parse(txtStopLoss.Text) + Decimal.Parse(txtTrigger.Text))
                    End If

                    'For initiating ATR Slippage function
                    direction = "SHORT"
                    If IsATRSlippageExcessive(BestPrice, direction) Then
                        Return
                    End If

                    triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                    ordermethod = "private/sell"
                    direction = "buy"
                    ordertype = "limit"


                Case "BuyMarket"

                    MarketOrderType = True

                    ' Ensure BestBidPrice is valid
                    If BestAskPrice <= 0 Then
                        AppendColoredText(txtLogs, "Best bid price Is Not valid.", Color.Yellow)
                        Return
                    End If

                    BestPrice = BestAskPrice - 0.5

                    'If manual take profit textbox is not empty, use that
                    If Decimal.Parse(txtManualTP.Text) > 0 Then
                        takeprofitprice = Decimal.Parse(txtManualTP.Text)
                    Else
                        takeprofitprice = BestPrice + Decimal.Parse(txtTakeProfit.Text)
                    End If

                    'If manual stop loss textbox is not empty, use that
                    If Decimal.Parse(txtManualSL.Text) > 0 Then
                        stoplossPrice = Decimal.Parse(txtManualSL.Text)
                        stoplossTriggerPrice = Decimal.Parse(txtManualSL.Text) + Decimal.Parse(txtStopLoss.Text)
                    Else
                        stoplossTriggerPrice = BestPrice - Decimal.Parse(txtTrigger.Text)
                        stoplossPrice = BestPrice - (Decimal.Parse(txtStopLoss.Text) + Decimal.Parse(txtTrigger.Text))
                    End If

                    triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                    ordermethod = "private/buy"
                    direction = "sell"
                    ordertype = "market"

                Case "SellMarket"

                    MarketOrderType = True

                    ' Ensure BestAskPrice is valid
                    If BestBidPrice <= 0 Then
                        AppendColoredText(txtLogs, "Best ask price Is Not valid.", Color.Yellow)
                        Return
                    End If

                    BestPrice = BestBidPrice + 0.5

                    'If manual take profit textbox is not empty, use that
                    If Decimal.Parse(txtManualTP.Text) > 0 Then
                        takeprofitprice = Decimal.Parse(txtManualTP.Text)
                    Else
                        takeprofitprice = BestPrice - Decimal.Parse(txtTakeProfit.Text)
                    End If

                    'If manual stop loss textbox is not empty, use that
                    If Decimal.Parse(txtManualSL.Text) > 0 Then
                        stoplossPrice = Decimal.Parse(txtManualSL.Text)
                        stoplossTriggerPrice = Decimal.Parse(txtManualSL.Text) - Decimal.Parse(txtStopLoss.Text)
                    Else
                        stoplossTriggerPrice = BestPrice + Decimal.Parse(txtTrigger.Text)
                        stoplossPrice = BestPrice + (Decimal.Parse(txtStopLoss.Text) + Decimal.Parse(txtTrigger.Text))
                    End If

                    triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                    ordermethod = "private/sell"
                    direction = "buy"
                    ordertype = "market"

                Case Else
                    ' Handle unexpected or unsupported order types
                    AppendColoredText(txtLogs, "Unsupported order type specified.", Color.IndianRed)
                    Return
            End Select

            StopLossTriggerOriginal = stoplossTriggerPrice ' Save the original SL trigger price
            emergencyBaseline = 0 ' new SL placement is pre-trigger - clear any pinned baseline
            ResetCommandedSLPrices() ' reconcile: pre-trigger SL context - drop any stale commanded prices

            ' Construct the JSON payload for the reduce-only order
            Dim params As New JObject(
     New JProperty("instrument_name", "BTC-PERPETUAL"),
        New JProperty("amount", amount),
        New JProperty("type", ordertype),
        New JProperty("label", If(MarketOrderType, "EntryMarketOrder", "EntryLimitOrder")),
        New JProperty("time_in_force", "good_til_cancelled"),
        New JProperty("linked_order_type", "one_triggers_one_cancels_other"),
        New JProperty("trigger_fill_condition", "first_hit"),
        New JProperty("reject_post_only", False),
        New JProperty("otoco_config", New JArray(
            New JObject(
                New JProperty("amount", amount),
                New JProperty("direction", direction),
                New JProperty("type", "limit"),
                New JProperty("label", "TakeLimitProfit"),
                New JProperty("price", takeprofitprice),
                New JProperty("time_in_force", "good_til_cancelled"),
                New JProperty("post_only", True)
            ),
            New JObject(
            New JProperty("amount", amount),
            New JProperty("direction", direction),
            New JProperty("type", "stop_limit"),
            New JProperty("trigger_price", stoplossTriggerPrice), ' Base trigger price                    
            New JProperty("price", stoplossPrice), ' Stop loss limit price
            New JProperty("label", "StopLossOrder"),
            New JProperty("time_in_force", "good_til_cancelled"),
            New JProperty("trigger_offset", triggeroffset), ' Offset for dynamic adjustment
            New JProperty("post_only", True),    'False guaranteed to work, but likely to become market order
            New JProperty("reduce_only", True),
            New JProperty("reject_post_only", False),
            New JProperty("trigger", "last_price")
            )
        ))
    )

            'Lines below taken out from stop loss order OTOCO code
            'New JProperty("trigger_offset", triggeroffset), ' Offset for dynamic adjustment
            'New JProperty("post_only", True)    'False guaranteed to work, but likely to become market order
            'New JProperty("reject_post_only", False),

            'New JProperty("reduce_only", True),                 
            'NOTE: It seems putting reduce_only calls in take profit will cause cancellation of both take profit and stop loss orders when stop loss trigger is hit, leaving a position open with no stop loss.


            ' Add price property only for limit orders
            If Not MarketOrderType Then
                params.Add("price", BestPrice)
                params.Add("post_only", True) ' Post-only is valid only for limit orders
            End If

            ' Decouple v2: unique id per placement + registry entry (snapshots for rejection
            ' rollback). Manual buttons pass no id -> self-allocate, no ack awaiter.
            Dim reqId As Integer = If(requestId > 0, requestId, Interlocked.Increment(nextPlacementId))
            RegisterPendingPlacement(reqId)

            ' Prepare the payload for the linked order
            Dim OrderPayload As New JObject(
    New JProperty("jsonrpc", "2.0"),
    New JProperty("id", reqId),
    New JProperty("method", ordermethod),
    New JProperty("params", params)
)

            'AppendColoredText(txtLogs, OrderPayload.ToString(), Color.LightGray)


            ' Send the order and capture the server's response
            rateLimiter?.ConsumeCredits()   ' F8: placements are the priciest calls - account for them
            Await SendWebSocketMessageAsync(OrderPayload.ToString())

            txtPlacedTakeProfitPrice.Text = takeprofitprice.ToString("F2")
            txtPlacedTrigStopPrice.Text = stoplossTriggerPrice.ToString("F2")
            txtPlacedStopLossPrice.Text = stoplossPrice.ToString("F2")

            txtPlacedPrice.Text = BestPrice.ToString("F2")
            placedPrice = BestPrice               ' seed engine state at placement (cross-thread fix)
            placedStopLossPrice = stoplossPrice
            cancelPending = False                 ' transition-race fix: a fresh order re-establishes a clean context

            If TypeOfOrder = "BuyLimit" Then
                ' Optional: Handle post-order logic (e.g., display confirmation)
                AppendColoredText(txtLogs, $"Buy limit order placed For {amount} at {BestPrice}.", Color.MediumSeaGreen)
            ElseIf TypeOfOrder = "SellLimit" Then
                ' Optional: Handle post-order logic (e.g., display confirmation)
                AppendColoredText(txtLogs, $"Sell limit order placed For {amount} at {BestPrice}.", Color.Crimson)
            ElseIf TypeOfOrder = "BuyNoSpread" Then
                AppendColoredText(txtLogs, $"Buy limit no spread order placed For {amount} at {BestPrice}.", Color.MediumSeaGreen)
            ElseIf TypeOfOrder = "SellNoSpread" Then
                AppendColoredText(txtLogs, $"Sell limit no spread order placed For {amount} at {BestPrice}.", Color.Crimson)
            ElseIf TypeOfOrder = "BuyMarket" Then
                AppendColoredText(txtLogs, $"Market buy order placed For {amount} starting at {BestPrice}.", Color.MediumSeaGreen)
            ElseIf TypeOfOrder = "SellMarket" Then
                AppendColoredText(txtLogs, $"Market sell order placed For {amount} starting at {BestPrice}.", Color.Crimson)
            End If

        Catch ex As Exception
            ' Handle any errors
            AppendColoredText(txtLogs, "Error placing order: " & ex.Message, Color.Red)
        End Try
    End Function


    Private Async Function CancelOrderAsync() As Task

        ' Connection guard (runtime test 4, 2026-07-03): pre-connect the socket fields are Nothing -
        ' an unguarded send NREs into the reconnect machinery. Same early-return as the entry paths;
        ' nothing was sent, so no engine state is touched either.
        If Not IsWebSocketConnected Then
            AppendColoredText(txtLogs, "WebSocket is not connected - cancel-all skipped.", Color.Red)
            Return
        End If

        Dim cancelPayload As New JObject(
        New JProperty("jsonrpc", "2.0"),
        New JProperty("id", 30),
        New JProperty("method", "private/cancel_all_by_instrument"),
        New JProperty("params", New JObject(
        New JProperty("instrument_name", "BTC-PERPETUAL"),
        New JProperty("type", "all")
        ))
    )

        Await SendWebSocketMessageAsync(cancelPayload.ToString())

        ' Reset engine state synchronously (cross-thread fix) so no reposition/SL decision reads a stale price.
        placedPrice = 0D
        placedStopLossPrice = 0D

        ' Transition-race fix: mark the cancel in flight and drop the order context up front. Nulling the IDs
        ' plus the cancelPending gate stops any reposition/edit from firing on the just-cancelled order, and
        ' lagging open echoes can't re-arm the reposition (they're ignored while pending; see the echo handler).
        cancelPending = True
        cancelPendingSince = DateTime.UtcNow
        CurrentOpenOrderId = Nothing
        CurrentTPOrderId = Nothing
        CurrentSLOrderId = Nothing
        ReduceOrderId = Nothing
        reduceOrderPrice = 0D
        reduceOrderAmount = 0D

        ' Cross-thread fix: CancelOrderAsync runs on both the UI and receive threads; marshal the status
        ' label with the placed-price resets (it was previously written unguarded off the receive thread).
        Me.Invoke(Sub()
                      txtPlacedPrice.Text = "0"
                      txtPlacedTakeProfitPrice.Text = "0"
                      txtPlacedTrigStopPrice.Text = "0"
                      txtPlacedStopLossPrice.Text = "0"
                      lblOrderStatus.Text = "Awaiting Orders"
                      lblOrderStatus.ForeColor = Color.DeepSkyBlue
                  End Sub)

        'Reset all flags
        isTrailingStop = False
        isTrailingPosition = False
        isTrailingStopLossPlaced = False
        SLTriggered = False
        StopLossTriggerOriginal = 0
        emergencyBaseline = 0
        ResetCommandedSLPrices() ' reconcile: nuclear cancel - triggered-SL context is gone

        'PositionEmpty = True
        PositionLog = False ' Reset position log flag so it can log next new position
        OrderLog = False ' Reset order log flag so it can log next new order

        ResetOrderAttempt() ' Reset ATR slippage tracking

        'Clearing margin displays
        Me.Invoke(Sub()
                      lblEstimatedLiquidation.Text = "L.Liq: N/A"
                      lblInitialMargin.Text = "L.IM: N/A"
                      lblMaintenanceMargin.Text = "L.MM: N/A"
                      lblEstimatedLeverage.Text = "L.Lev: N/A"

                      ' Reset colors
                      lblEstimatedLiquidation.ForeColor = Color.Gray
                      lblEstimatedLeverage.ForeColor = Color.Gray
                  End Sub)

        If PositionEmpty = False Then
            AppendColoredText(txtLogs, $"Cancelled all open orders", Color.Yellow)
        Else
            PositionEmpty = False
        End If

    End Function


    ' Scoped cancel (docs/spec-decouple-v2.md): abandon the WORKING ENTRY only. Cancels the OTOCO
    ' primary by id (Deribit cancels the linked untriggered children with it - verified in tests);
    ' an existing position's legs (PositionTPOrderId/PositionSLOrderId), its triggered-SL trailing
    ' state (SLTriggered/placedStopLossPrice/StopLossTriggerOriginal), and the trailing/position
    ' flags are deliberately NOT touched. Receive-thread safe: fields + self-marshalling output.
    Private Async Function CancelWorkingEntryCoreAsync(reason As String) As Task
        Dim entryId As String = CurrentOpenOrderId
        If entryId Is Nothing Then Return

        Dim cancelPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 31},
            {"method", "private/cancel"},
            {"params", New JObject From {{"order_id", entryId}}}
        }
        Await SendWebSocketMessageAsync(cancelPayload.ToString())

        ' Same transition-race protection as the nuclear cancel: gate repositions/echo-seeding
        ' for the window, drop the entry context, reset the entry price.
        cancelPending = True
        cancelPendingSince = DateTime.UtcNow
        CurrentOpenOrderId = Nothing
        CurrentTPOrderId = Nothing
        CurrentSLOrderId = Nothing
        placedPrice = 0D
        ResetOrderAttempt() ' reset ATR slippage tracking for the next attempt

        UiInvoke(Sub()
                     txtPlacedPrice.Text = "0"
                     txtPlacedTakeProfitPrice.Text = "0"
                     txtPlacedTrigStopPrice.Text = "0"
                     If positionSizeUSD <> 0D Then
                         lblOrderStatus.Text = "In Position"
                         lblOrderStatus.ForeColor = Color.Yellow
                     Else
                         lblOrderStatus.Text = "Awaiting Orders"
                         lblOrderStatus.ForeColor = Color.DeepSkyBlue
                     End If
                 End Sub)

        AppendColoredText(txtLogs, $"Working entry cancelled ({reason}) - position legs untouched", Color.Yellow)
    End Function


    Private Async Function SendReduceOrderAsync(price As Decimal?, amount As Decimal, direction As String, isMarketOrder As Boolean) As Task
        Try
            ' Connection guard (runtime test 4, 2026-07-03): same early-return as the entry paths,
            ' before any state mutation (StopLossTriggerOriginal/trailing flags below).
            If Not IsWebSocketConnected Then
                AppendColoredText(txtLogs, "WebSocket is not connected - reduce order skipped.", Color.Red)
                Return
            End If

            'Remember to do a cancel all orders here before sending reduce order
            'Await CancelOrderAsync()

            ' Determine the order type (limit or market)
            Dim orderType As String = If(isMarketOrder, "market", "limit")

            If isMarketOrder Then
                StopLossTriggerOriginal = 0
                emergencyBaseline = 0
                ResetCommandedSLPrices() ' reconcile: market reduce - triggered-SL context is gone
            End If

            ' Construct the JSON payload for the reduce-only order
            Dim params As New JObject(
            New JProperty("instrument_name", "BTC-PERPETUAL"),
            New JProperty("amount", amount),
            New JProperty("type", orderType),
            New JProperty("reduce_only", True),
            New JProperty("time_in_force", "good_til_cancelled"),
            New JProperty("label", If(isMarketOrder, "ReduceMarketOrder", "ReduceLimitOrder"))
        )

            ' Add price property only for limit orders
            If Not isMarketOrder Then
                If price Is Nothing OrElse price <= 0 Then
                    Throw New ArgumentException("Price must be specified for limit orders.")
                End If
                params.Add("price", price)
                params.Add("post_only", True) ' Post-only is valid only for limit orders
            End If

            Dim payload As New JObject(
            New JProperty("jsonrpc", "2.0"),
            New JProperty("id", 1),
            New JProperty("method", If(direction = "buy", "private/buy", "private/sell")),
            New JProperty("params", params)
        )
            'Reset trailing order flags
            isTrailingPosition = False
            isTrailingStop = False

            ' Send the payload via WebSocket
            rateLimiter?.ConsumeCredits()   ' F8: placements are the priciest calls - account for them
            Await SendWebSocketMessageAsync(payload.ToString())

            If Not isMarketOrder Then
                ' Seed the reposition context at placement. The open echo captures the order id and
                ' re-seeds price/amount only when 0 (single-writer rule - see HandleOrderPositionUpdates).
                reduceOrderPrice = If(price, 0D)
                reduceOrderAmount = amount
                reduceOrderIsBuy = (direction = "buy")
            End If

            Dim orderDescription As String = $"{orderType.ToUpper()} {direction} {amount} {(If(isMarketOrder, "", $"@ {price}"))}"
            AppendColoredText(txtLogs, $"Reduce-only {orderDescription} order sent.", Color.Green)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in SendReduceOrderAsync: {ex.Message}", Color.Red)
        End Try
    End Function
    Private UpdateFlag As Boolean = False
    Private Async Function UpdateLimitOrderWithOTOCOAsync(newPrice As Decimal) As Task
        Try
            ' Ensure rate limiter exists
            If rateLimiter Is Nothing Then
                AppendColoredText(txtLogs, "Rate limiter not initialized - creating emergency limiter", Color.Yellow)
                rateLimiter = New DeribitRateLimiter(1000, 50) ' Emergency conservative limiter
            End If

            ' Check rate limit before making API calls
            If Not rateLimiter.CanMakeRequest() Then
                Dim waitTime = rateLimiter.GetWaitTimeMs()
                AppendColoredText(txtLogs, $"Rate limit reached, waiting {waitTime}ms", Color.Yellow)
                Await Task.Delay(waitTime)
            End If

            ' Only proceed if we can consume credits for all 3 API calls needed
            If Not (rateLimiter.ConsumeCredits() AndAlso rateLimiter.CanMakeRequest() AndAlso rateLimiter.CanMakeRequest()) Then
                AppendColoredText(txtLogs, "Insufficient credits for order update - skipping", Color.Orange)
                Return
            End If

            Dim newTPprice, newTrigSLprice, newSLprice As Decimal
            ' Cross-thread fix: read engine input fields, never the textboxes (runs on the receive thread).
            Dim amount As Decimal = orderAmountVal
            If amount <= 0D Then
                ' Preserve the old "no edit on a bad amount" behaviour (Decimal.Parse used to throw on blank).
                AppendColoredText(txtLogs, "Order amount blank/zero - skipping order update", Color.Orange)
                Return
            End If

            ' Your existing price calculation logic remains the same
            If TradeMode = True Then
                If manualTPval > 0 Then
                    newTPprice = manualTPval
                Else
                    newTPprice = newPrice + takeProfitOffset
                End If

                If manualSLval > 0 Then
                    newSLprice = manualSLval
                    newTrigSLprice = newSLprice + stopLossOffset
                Else
                    newTrigSLprice = newPrice - triggerDistance
                    newSLprice = newTrigSLprice - stopLossOffset
                End If
            Else
                If manualTPval > 0 Then
                    newTPprice = manualTPval
                Else
                    newTPprice = newPrice - takeProfitOffset
                End If

                If manualSLval > 0 Then
                    newSLprice = manualSLval
                    newTrigSLprice = newSLprice - stopLossOffset
                Else
                    newTrigSLprice = newPrice + triggerDistance
                    newSLprice = newTrigSLprice + stopLossOffset
                End If
            End If

            StopLossTriggerOriginal = newTrigSLprice ' Save the original SL trigger price
            emergencyBaseline = 0 ' new SL placement is pre-trigger - clear any pinned baseline
            ResetCommandedSLPrices() ' reconcile: pre-trigger SL context - drop any stale commanded prices

            ' Send all three updates with rate limiting
            Await SendRateLimitedUpdate("main", CurrentOpenOrderId, newPrice, amount)
            rateLimiter.ConsumeCredits() ' Consume for second call
            Await SendRateLimitedUpdate("takeprofit", CurrentTPOrderId, newTPprice, amount)
            rateLimiter.ConsumeCredits() ' Consume for third call
            Await SendRateLimitedUpdate("stoploss", CurrentSLOrderId, newSLprice, amount, newTrigSLprice)

            UpdateFlag = True

        Catch ex As Exception
            AppendColoredText(txtLogs, "Error in rate-limited UpdateLimitOrderWithOTOCOAsync: " & ex.Message, Color.Red)
        End Try
    End Function

    Private Async Function SendRateLimitedUpdate(orderType As String, orderId As String, price As Decimal, amount As Decimal, Optional triggerPrice As Decimal? = Nothing) As Task
        Try
            Dim updatePayload As JObject

            If triggerPrice.HasValue Then
                ' Stop loss order with trigger price
                updatePayload = New JObject From {
                {"jsonrpc", "2.0"},
                {"id", 223346},
                {"method", "private/edit"},
                {"params", New JObject From {
                    {"order_id", orderId},
                    {"price", price},
                    {"trigger_price", triggerPrice.Value},
                    {"amount", amount}
                }}
            }
            Else
                ' Regular limit order
                updatePayload = New JObject From {
                {"jsonrpc", "2.0"},
                {"id", 223344},
                {"method", "private/edit"},
                {"params", New JObject From {
                    {"order_id", orderId},
                    {"price", price},
                    {"amount", amount}
                }}
            }
            End If

            Await SendWebSocketMessageAsync(updatePayload.ToString())

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in SendRateLimitedUpdate ({orderType}): {ex.Message}", Color.Red)
        End Try
    End Function

    ' Edits the tracked reduce-limit order to a new top-of-book price. Receive-thread safe:
    ' engine fields only; AppendColoredText self-marshals.
    Private Async Function SendReduceRepositionEdit(newPrice As Decimal) As Task
        Try
            Dim editPayload As New JObject From {
                {"jsonrpc", "2.0"},
                {"id", 223349},
                {"method", "private/edit"},
                {"params", New JObject From {
                    {"order_id", ReduceOrderId},
                    {"price", newPrice},
                    {"amount", reduceOrderAmount}
                }}
            }
            Await SendWebSocketMessageAsync(editPayload.ToString())

            AppendColoredText(txtLogs, $"Reduce order repositioned: ${reduceOrderPrice:F2} → ${newPrice:F2}", Color.Yellow)
            ' Runaway-fix pattern: advance engine state synchronously so the next tick compares
            ' against the new price even if the exchange echo lags.
            reduceOrderPrice = newPrice
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error repositioning reduce order: {ex.Message}", Color.Red)
        End Try
    End Function


    Private lastSLId As String = ""
    Private pendingLocalMsg As String = ""

    Private Async Function UpdateStopLossForTriggeredStopLossOrder(newPrice As Decimal) As Task
        Try
            ' Your existing emergency market order logic first. Cross-thread fix: marketStopThreshold mirrors
            ' txtMarketStopLoss; a 0/blank threshold disables this emergency market-stop (was: Parse threw on
            ' blank and aborted the whole SL update; "0" fired the market stop on any adverse movement).
            ' Audit2 F3: chkMarketStopLoss (via the marketStopLossChecked mirror - receive thread!) is the
            ' master enable for this emergency market close; threshold 0/blank additionally disables.
            ' Restore hardening + M.SL baseline: an unknown baseline (0) disables the emergency market-stop,
            ' or the short branch below fires instantly (newPrice - 0 >= threshold). emgBaseline = the actual
            ' SL price once triggered (emergencyBaseline), else the trigger price (StopLossTriggerOriginal).
            Dim emgBaseline As Decimal = If(emergencyBaseline > 0D, emergencyBaseline, StopLossTriggerOriginal)
            If marketStopLossChecked AndAlso marketStopThreshold > 0D AndAlso emgBaseline > 0D AndAlso (TradeMode = True) AndAlso (emgBaseline - newPrice >= marketStopThreshold) Then
                Await CancelOrderAsync()
                newPricePublic = newPrice 'For storing reduce market order price for logging
                Await SendReduceMarketOrderAsync()   ' cross-thread fix: was btnReduceMarket.PerformClick()
                AppendColoredText(txtLogs, "Emergency Sell Market Order Executed.", Color.Red)
                Return ' Exit early after emergency execution
            ElseIf marketStopLossChecked AndAlso marketStopThreshold > 0D AndAlso emgBaseline > 0D AndAlso (TradeMode = False) AndAlso (newPrice - emgBaseline >= marketStopThreshold) Then
                Await CancelOrderAsync()
                newPricePublic = newPrice 'For storing reduce market order price for logging
                Await SendReduceMarketOrderAsync()   ' cross-thread fix: was btnReduceMarket.PerformClick()
                AppendColoredText(txtLogs, "Emergency Buy Market Order Executed.", Color.Red)
                Return ' Exit early after emergency execution
            End If

            ' Enhanced rate limiter handling for critical operations
            If rateLimiter Is Nothing Then
                AppendColoredText(txtLogs, "CRITICAL: Rate limiter not initialized for SL update", Color.Red)
                rateLimiter = New DeribitRateLimiter(1000, 50) ' Emergency conservative limiter
            End If

            ' Force execution with timeout for critical stop loss updates
            Dim maxWaitTime As Integer = 3000 ' Maximum 3 seconds wait for SL updates
            Dim waitStartTime As DateTime = DateTime.UtcNow

            While Not rateLimiter.CanMakeRequest()
                If (DateTime.UtcNow - waitStartTime).TotalMilliseconds > maxWaitTime Then
                    AppendColoredText(txtLogs, "CRITICAL SL: Forcing execution despite rate limits", Color.Orange)
                    Exit While
                End If
                Await Task.Delay(100)
            End While

            ' Consume credits and proceed with update
            rateLimiter.ConsumeCredits()

            ' Restore hardening: a triggered SL covers the POSITION - size edits from the position
            ' model, falling back to the input mirror only when the model is unseeded. After a restart
            ' (or whenever txtAmount <> position size) orderAmountVal would resize the stop off the position.
            ' (cross-thread fix retained: read engine fields, not txtAmount.)
            Dim amount As Decimal = If(positionSizeUSD <> 0D, Math.Abs(positionSizeUSD), orderAmountVal)
            If amount <= 0 Then
                AppendColoredText(txtLogs, "Invalid amount for SL update", Color.Red)
                Return
            End If

            ' Validate order ID
            If String.IsNullOrEmpty(PositionSLOrderId) Then
                AppendColoredText(txtLogs, "PositionSLOrderId is null - cannot update SL", Color.Red)
                Return
            End If

            ' Validate WebSocket connection
            If webSocketClient Is Nothing OrElse webSocketClient.State <> WebSocketState.Open Then
                AppendColoredText(txtLogs, "WebSocket disconnected - cannot send critical SL update", Color.Red)
                Return
            End If

            ' Construct and send the update payload
            Dim updateOrderPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 223350},
            {"method", "private/edit"},
            {"params", New JObject From {
                {"order_id", PositionSLOrderId},
                {"price", newPrice},
                {"amount", amount}
            }}
        }

            Await SendWebSocketMessageAsync(updateOrderPayload.ToString())
            UpdateFlag = True

            ' Reconcile fix (spec-reconcile-manual-sl-edits.md 4a): this is the SINGLE send point for every
            ' triggered-SL edit (normal chase and the emergency ForceStopLossUpdate path both route here), so
            ' recording newPrice here means the open echo of THIS edit - or a lagging one - is recognised as ours
            ' and not misread as a manual SL move. The echo can only arrive after this send completes, so the
            ' record is always in place first.
            RecordCommandedSLPrice(newPrice)

            ' Store pending message for confirmation
            pendingLocalMsg = $"CRITICAL SL repositioned to: ${newPrice:F2}"
            lastSLId = PositionSLOrderId

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in UpdateStopLossForTriggeredStopLossOrder: {ex.Message}", Color.Red)

            ' Handle rate limit errors specifically
            If ex.Message.Contains("too_many_requests") OrElse ex.Message.Contains("10028") Then
                AppendColoredText(txtLogs, "Rate limit hit during critical SL update - will retry", Color.Yellow)
                ' #4 retry-amplifier fix: bounded backoff instead of DateTime.MinValue (immediate per-tick retry).
                BackoffStopLossRetry(DateTime.UtcNow)
            End If
        End Try
    End Function

    Private Async Function UpdateStopLossForTrailingOrder(newPrice As Decimal) As Task
        Try

            ' Ensure rate limiter exists
            If rateLimiter Is Nothing Then
                AppendColoredText(txtLogs, "Rate limiter not initialized - creating emergency limiter for trailing order", Color.Yellow)
                rateLimiter = New DeribitRateLimiter(1000, 50) ' Emergency conservative limiter
            End If

            ' Check rate limit before making multiple API calls (this function makes 2 calls)
            If Not rateLimiter.CanMakeRequest() Then
                Dim waitTime = rateLimiter.GetWaitTimeMs()
                AppendColoredText(txtLogs, $"Rate limit reached for trailing update, waiting {waitTime}ms", Color.Yellow)
                Await Task.Delay(waitTime)
            End If

            ' Check if we have enough credits for both API calls (main order + stop loss)
            If Not (rateLimiter.ConsumeCredits() AndAlso rateLimiter.CanMakeRequest()) Then
                AppendColoredText(txtLogs, "Insufficient credits for trailing order update - skipping", Color.Orange)
                Return
            End If

            Dim newTrigSLprice, newSLprice As Decimal
            ' Cross-thread fix: read engine input fields, never the textboxes (runs on the receive thread).
            Dim amount As Decimal = orderAmountVal
            If amount <= 0D Then
                ' Preserve the old "no edit on a bad amount" behaviour (Decimal.Parse used to throw on blank).
                AppendColoredText(txtLogs, "Order amount blank/zero - skipping trailing order update", Color.Orange)
                Return
            End If

            ' Calculate the new stop loss prices based on direction
            If TradeMode = True Then
                ' Buy direction
                If manualSLval > 0 Then
                    newSLprice = manualSLval
                    newTrigSLprice = newSLprice + stopLossOffset
                Else
                    newTrigSLprice = newPrice - triggerDistance
                    newSLprice = newTrigSLprice - stopLossOffset
                End If
            Else
                ' Sell direction
                If manualSLval > 0 Then
                    newSLprice = manualSLval
                    newTrigSLprice = newSLprice - stopLossOffset
                Else
                    newTrigSLprice = newPrice + triggerDistance
                    newSLprice = newTrigSLprice + stopLossOffset
                End If
            End If

            StopLossTriggerOriginal = newTrigSLprice ' Save the original SL trigger price
            emergencyBaseline = 0 ' new SL placement is pre-trigger - clear any pinned baseline
            ResetCommandedSLPrices() ' reconcile: pre-trigger SL context - drop any stale commanded prices

            ' Update main trailing order
            Dim updateOrderPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 223347},
            {"method", "private/edit"},
            {"params", New JObject From {
                {"order_id", CurrentOpenOrderId},
                {"price", newPrice},
                {"amount", amount}
            }}
        }

            Await SendWebSocketMessageAsync(updateOrderPayload.ToString())

            ' Consume credits for second API call
            rateLimiter.ConsumeCredits()

            ' Update stop loss order
            Dim updateStopLossPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 223348},
            {"method", "private/edit"},
            {"params", New JObject From {
                {"order_id", CurrentSLOrderId},
                {"price", newSLprice},
                {"trigger_price", newTrigSLprice},
                {"amount", amount}
            }}
        }

            Await SendWebSocketMessageAsync(updateStopLossPayload.ToString())

            UpdateFlag = True
            AppendColoredText(txtLogs, $"Rate-limited trailing order updated to: ${newPrice}", Color.Cyan)

        Catch ex As Exception
            AppendColoredText(txtLogs, "Error in UpdateStopLossForTrailingOrder: " & ex.Message, Color.Red)
        End Try
    End Function

    Private Async Function StopLossForTrailingOrderAsync(TypeOfOrder As String) As Task
        Try

            Dim stoplossTriggerPrice As Decimal
            Dim triggeroffset As Decimal
            Dim stoplossPrice As Decimal
            Dim BestPrice As Decimal
            Dim ordermethod As String = String.Empty ' Default value to avoid warnings
            Dim direction As String = String.Empty ' Default value for direction
            Dim ordertype As String = String.Empty

            ' Ensure WebSocket is connected
            If webSocketClient Is Nothing OrElse webSocketClient.State <> WebSocketState.Open Then
                'txtLogs.AppendText("WebSocket is not connected." + Environment.NewLine)
                AppendColoredText(txtLogs, "WebSocket is not connected.", Color.Red)
                Return
            End If

            ' Validate the input amount
            Dim amountText As String = txtAmount.Text
            Dim amount As Decimal

            If Not Decimal.TryParse(amountText, amount) OrElse amount <= 0 Then
                'txtLogs.AppendText("Please enter a valid positive amount." + Environment.NewLine)
                AppendColoredText(txtLogs, "Please enter a valid positive amount.", Color.Yellow)
                Return
            End If

            Select Case TypeOfOrder
                Case "BuyTrail"

                    ' Ensure BestBidPrice is valid
                    If BestBidPrice <= 0 Then
                        'txtLogs.AppendText("Best bid price is not valid." + Environment.NewLine)
                        AppendColoredText(txtLogs, "Best bid price is not valid.", Color.Yellow)
                        Return
                    End If

                    BestPrice = BestBidPrice

                    'To set price at which trailing stop loss order is triggered for placement
                    'If manual take profit textbox is not empty, use that
                    If Decimal.Parse(txtManualTP.Text) > 0 Then
                        TPTrailprice = Decimal.Parse(txtManualTP.Text)
                        If TPTrailprice < (BestPrice + Decimal.Parse(txtComms.Text)) Then
                            AppendColoredText(txtLogs, "Manual TP is less than comms paid.", Color.Yellow)
                        End If
                    Else
                        TPTrailprice = BestPrice + (Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text))
                    End If

                    'If manual stop loss textbox is not empty, use that
                    If Decimal.Parse(txtManualSL.Text) > 0 Then
                        stoplossPrice = Decimal.Parse(txtManualSL.Text)
                        stoplossTriggerPrice = Decimal.Parse(txtManualSL.Text) + Decimal.Parse(txtStopLoss.Text)
                    Else
                        stoplossTriggerPrice = BestPrice - Decimal.Parse(txtTrigger.Text)
                        stoplossPrice = BestPrice - (Decimal.Parse(txtStopLoss.Text) + Decimal.Parse(txtTrigger.Text))
                    End If

                    'For initiating ATR Slippage function
                    direction = "LONG"
                    If IsATRSlippageExcessive(BestPrice, direction) Then
                        Return
                    End If

                    triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                    ordermethod = "private/buy"
                    direction = "sell"

                Case "SellTrail"

                    ' Ensure BestAskPrice is valid
                    If BestAskPrice <= 0 Then
                        'txtLogs.AppendText("Best ask price Is Not valid." + Environment.NewLine)
                        AppendColoredText(txtLogs, "Best ask price Is Not valid.", Color.Yellow)
                        Return
                    End If

                    BestPrice = BestAskPrice

                    'If manual take profit textbox is not empty, use that
                    If Decimal.Parse(txtManualTP.Text) > 0 Then
                        TPTrailprice = Decimal.Parse(txtManualTP.Text)
                        If TPTrailprice > (BestPrice - Decimal.Parse(txtComms.Text)) Then
                            AppendColoredText(txtLogs, "Manual TP is less than comms paid.", Color.Yellow)
                        End If
                    Else
                        TPTrailprice = BestPrice - (Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text))
                    End If

                    'If manual stop loss textbox is not empty, use that
                    If Decimal.Parse(txtManualSL.Text) > 0 Then
                        stoplossPrice = Decimal.Parse(txtManualSL.Text)
                        stoplossTriggerPrice = Decimal.Parse(txtManualSL.Text) - Decimal.Parse(txtStopLoss.Text)
                    Else
                        stoplossTriggerPrice = BestPrice + Decimal.Parse(txtTrigger.Text)
                        stoplossPrice = BestPrice + (Decimal.Parse(txtStopLoss.Text) + Decimal.Parse(txtTrigger.Text))
                    End If

                    'For initiating ATR Slippage function
                    direction = "SHORT"
                    If IsATRSlippageExcessive(BestPrice, direction) Then
                        Return
                    End If

                    triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                    ordermethod = "private/sell"
                    direction = "buy"

                Case Else

                    ' Handle unexpected or unsupported order types
                    AppendColoredText(txtLogs, "Unsupported order type specified.", Color.IndianRed)
                    Return

            End Select

            StopLossTriggerOriginal = stoplossTriggerPrice ' Save the original SL trigger price
            emergencyBaseline = 0 ' new SL placement is pre-trigger - clear any pinned baseline
            ResetCommandedSLPrices() ' reconcile: pre-trigger SL context - drop any stale commanded prices

            ' Construct the JSON payload for the reduce-only order
            Dim params As New JObject(
             New JProperty("instrument_name", "BTC-PERPETUAL"),
                New JProperty("amount", amount),
                New JProperty("type", "limit"),
                New JProperty("label", "EntryTrailingOrder"),
                New JProperty("time_in_force", "good_til_cancelled"),
                New JProperty("linked_order_type", "one_triggers_other"),
                New JProperty("trigger_fill_condition", "first_hit"),
                New JProperty("reject_post_only", False),
                New JProperty("otoco_config", New JArray(
                    New JObject(
                    New JProperty("amount", amount),
                    New JProperty("direction", direction),
                    New JProperty("type", "stop_limit"),
                    New JProperty("trigger_price", stoplossTriggerPrice), ' Base trigger price
                    New JProperty("trigger_offset", triggeroffset), ' Offset for dynamic adjustment
                    New JProperty("price", stoplossPrice), ' Stop loss limit price
                    New JProperty("label", "StopLossOrder"),
                    New JProperty("reduce_only", True),
                    New JProperty("time_in_force", "good_til_cancelled"),
                    New JProperty("post_only", True),
                    New JProperty("trigger", "last_price")
                    )
                ))
            )

            params.Add("price", BestPrice)
            params.Add("post_only", True) ' Post-only is valid only for limit orders

            ' Decouple v2: unique id per placement + registry entry (snapshots for rejection rollback).
            Dim reqId As Integer = Interlocked.Increment(nextPlacementId)
            RegisterPendingPlacement(reqId)

            ' Prepare the payload for the linked order
            Dim OrderPayload As New JObject(
            New JProperty("jsonrpc", "2.0"),
            New JProperty("id", reqId),
            New JProperty("method", ordermethod),
            New JProperty("params", params)
        )

            ' Send the order and capture the server's response
            rateLimiter?.ConsumeCredits()   ' F8: placements are the priciest calls - account for them
            Await SendWebSocketMessageAsync(OrderPayload.ToString())

            If Decimal.Parse(txtManualTP.Text) > 0 Then
                txtPlacedTakeProfitPrice.Text = txtManualTP.Text
            Else
                If TradeMode = True Then
                    txtPlacedTakeProfitPrice.Text = BestPrice + ((Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text)))
                Else
                    txtPlacedTakeProfitPrice.Text = BestPrice - ((Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text)))
                End If

            End If

            txtPlacedTrigStopPrice.Text = stoplossTriggerPrice.ToString("F2")
            txtPlacedStopLossPrice.Text = stoplossPrice.ToString("F2")

            txtPlacedPrice.Text = BestPrice.ToString("F2")
            placedPrice = BestPrice               ' seed engine state at placement (cross-thread fix)
            placedStopLossPrice = stoplossPrice
            cancelPending = False                 ' transition-race fix: a fresh order re-establishes a clean context

            isTrailingStopLossPlaced = True

            If TypeOfOrder = "BuyTrail" Then
                ' Optional: Handle post-order logic (e.g., display confirmation)
                AppendColoredText(txtLogs, $"Buy Trailing order placed For {amount} at {BestPrice}.", Color.MediumSeaGreen)
            ElseIf TypeOfOrder = "SellTrail" Then
                ' Optional: Handle post-order logic (e.g., display confirmation)
                AppendColoredText(txtLogs, $"Sell Trailing order placed For {amount} at {BestPrice}.", Color.Red)
            End If

        Catch ex As Exception
            ' Handle any errors
            AppendColoredText(txtLogs, "Error in StopLossForTrailingOrderAsync: " & ex.Message, Color.Red)
        End Try
    End Function

    'This function is called when market price has reached the Take Profit price set in Manual TP textbox or auto-calc by txtplacedprice + txtcomms + txttpoffset 
    'after user clicks the trailing stop loss button. Call comes from HandleQuoteUpdates function.
    Private Async Function TrailingStopLossOrderAsync() As Task
        Try
            ' Ensure WebSocket is connected
            If webSocketClient Is Nothing OrElse webSocketClient.State <> WebSocketState.Open Then
                'txtLogs.AppendText("WebSocket is not connected." + Environment.NewLine)
                AppendColoredText(txtLogs, "WebSocket is not connected.", Color.Red)
                Return
            End If

            ' Validate the input amount (cross-thread fix: read engine fields, not the controls)
            Dim amount As Decimal = orderAmountVal

            If amount <= 0 Then
                'txtLogs.AppendText("Please enter a valid positive amount." + Environment.NewLine)
                AppendColoredText(txtLogs, "Please enter a valid positive amount.", Color.Yellow)
                Return
            End If

            Dim method As String
            Dim startoffset As Decimal = tpOffsetVal
            Dim triggerpricing As Decimal

            If TradeMode = True Then
                method = "private/sell"
                triggerpricing = TPTrailprice - startoffset
            Else
                method = "private/buy"
                triggerpricing = TPTrailprice + startoffset
            End If

            'To cancel the current stop loss order
            Dim cancelPayload As New JObject(
        New JProperty("jsonrpc", "2.0"),
        New JProperty("id", 30),
        New JProperty("method", "private/cancel_all_by_instrument"),
        New JProperty("params", New JObject(
        New JProperty("instrument_name", "BTC-PERPETUAL"),
        New JProperty("type", "all")
        ))
    )
            Await SendWebSocketMessageAsync(cancelPayload.ToString())
            AppendColoredText(txtLogs, "Cancelled current stop loss order.", Color.Green)


            ' Construct the JSON payload for the trailing stop loss order
            Dim payload As New JObject(
                New JProperty("jsonrpc", "2.0"),
                New JProperty("id", 30),
                New JProperty("method", method),
                New JProperty("params", New JObject(
                    New JProperty("instrument_name", "BTC-PERPETUAL"),
                            New JProperty("amount", amount),
                            New JProperty("type", "trailing_stop"),
                            New JProperty("label", "TrailingStopLoss"),
                            New JProperty("trail_offset", startoffset), 'Offset for the trailing stop to maintain from market price
                            New JProperty("trigger_offset", startoffset), 'Starting offset for the trailing stop
                            New JProperty("reduce_only", True),
                            New JProperty("trigger", "last_price"),
                            New JProperty("time_in_force", "good_til_cancelled")
                ))
            )

            ' Send the payload via WebSocket
            Await SendWebSocketMessageAsync(payload.ToString())

            Dim orderDescription As String = $"Trailing Stop Loss order set at: {startoffset} offset"
            AppendColoredText(txtLogs, $"Reduce-only {orderDescription}.", Color.Green)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in TrailingStopLossOrderAsync: {ex.Message}", Color.Red)
        End Try
    End Function

    Private Async Function ForceStopLossUpdate(newPrice As Decimal, Optional reason As String = "Emergency Update") As Task
        ' Bypass all rate limiting for emergency situations
        lastStopLossUpdate = DateTime.MinValue
        'AppendColoredText(txtLogs, $"EMERGENCY SL UPDATE: {reason}", Color.Red)
        Await UpdateStopLossForTriggeredStopLossOrder(newPrice)
    End Function

    ' --- Commanded-SL-price set helpers (docs/spec-reconcile-manual-sl-edits.md 4a) -----------------------
    ' Record every SL price the app sends to the exchange. Called from the single SL-edit send point
    ' (UpdateStopLossForTriggeredStopLossOrder) on the receive thread. Purges entries older than the window so
    ' an idle-then-manual-edit to a long-ago commanded price is still detected as manual.
    Private Sub RecordCommandedSLPrice(price As Decimal)
        Dim nowUtc As DateTime = DateTime.UtcNow
        SyncLock commandedSLLock
            PurgeCommandedSLPrices(nowUtc)
            commandedSLPrices.Add(New CommandedSLEntry With {.Price = price, .Stamp = nowUtc})
            ' Backstop against unbounded growth; the window keeps this a handful of entries in practice.
            If commandedSLPrices.Count > 32 Then commandedSLPrices.RemoveAt(0)
        End SyncLock
    End Sub

    ' True if price matches an SL price the app commanded within the window => our own (possibly out-of-order)
    ' echo, not a manual edit. Read on the UI thread from the open StopLossOrder echo handler.
    Private Function IsRecentlyCommandedSLPrice(price As Decimal) As Boolean
        SyncLock commandedSLLock
            PurgeCommandedSLPrices(DateTime.UtcNow)
            For Each e In commandedSLPrices
                If Math.Abs(e.Price - price) <= CommandedSLMatchTol Then Return True
            Next
            Return False
        End SyncLock
    End Function

    ' Clear the commanded set wherever the SL context resets (mirrors the 7 emergencyBaseline = 0 sites:
    ' 4 SL-placement paths + CompletePositionClose + nuclear cancel + market-reduce).
    Private Sub ResetCommandedSLPrices()
        SyncLock commandedSLLock
            commandedSLPrices.Clear()
        End SyncLock
    End Sub

    ' Drop expired commanded prices. Caller MUST hold commandedSLLock.
    Private Sub PurgeCommandedSLPrices(nowUtc As DateTime)
        Dim i As Integer = commandedSLPrices.Count - 1
        While i >= 0
            If (nowUtc - commandedSLPrices(i).Stamp).TotalMilliseconds > CommandedSLWindowMs Then
                commandedSLPrices.RemoveAt(i)
            End If
            i -= 1
        End While
    End Sub


    'Non-order execution functions below
    '------------------------------------------------
    ' Define a function to add colored text

    Private Async Function GetAccountSummaryLimits() As Task(Of RateLimitInfo)
        Try
            ' Create a task completion source to wait for the response
            accountSummaryTaskCompletionSource = New TaskCompletionSource(Of RateLimitInfo)()

            ' Create the account summary request
            Dim accountSummaryPayload = New JObject(
            New JProperty("jsonrpc", "2.0"),
            New JProperty("id", 999), ' Unique ID to identify this request
            New JProperty("method", "private/get_account_summary"),
            New JProperty("params", New JObject(
                New JProperty("currency", "BTC"),
                New JProperty("extended", True)
            ))
        )

            ' Send the request via WebSocket
            Await SendWebSocketMessageAsync(accountSummaryPayload.ToString())

            ' Wait for the response (with timeout)
            Dim timeoutTask = Task.Delay(5000) ' 5 second timeout
            Dim completedTask = Await Task.WhenAny(accountSummaryTaskCompletionSource.Task, timeoutTask)

            ' Use 'Is' operator instead of '=' for task comparison
            If completedTask Is timeoutTask Then
                ' Timeout occurred
                AppendColoredText(txtLogs, "Account summary request timed out - using conservative defaults", Color.Yellow)
                Return New RateLimitInfo With {
                .MaxCredits = 2000,
                .RefillRate = 20,
                .BurstLimit = 20,
                .CurrentEstimatedCredits = 2000
            }
            Else
                ' Response received
                Return Await accountSummaryTaskCompletionSource.Task
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error getting account limits: {ex.Message}", Color.Yellow)
            ' Return very conservative defaults on error
            Return New RateLimitInfo With {
            .MaxCredits = 500,
            .RefillRate = 10,
            .BurstLimit = 5,
            .CurrentEstimatedCredits = 500
        }
        End Try
    End Function

    ' --- place in frmMainPageV2 (replace existing helper) -------------------
    Private Sub AppendColoredText(rtb As RichTextBox, text As String, color As Color)
        Const RL_MSG As String = "Rate limiter not initialized - skipping order update"
        Static skipNext As Boolean = False          ' <-- single persistent flag

        'If several other types of repeating messages, use below code to suppress them and replace skipnext as Boolean
        '        Static lastMsg As String = ""
        '       If text.Equals(lastMsg, StringComparison.Ordinal) Then Exit Sub
        '      lastMsg = text

        ' 1. Decide whether this call should be written
        If text.Equals(RL_MSG, StringComparison.Ordinal) Then
            If skipNext Then Exit Sub               ' already shown → suppress
            skipNext = True                         ' first time → show & arm flag
        Else
            skipNext = False                        ' any other message resets flag
        End If

        ' 2. Normal logging. Handle-race guard: AppendColoredText is called from the receive thread; a raw
        ' Me.Invoke throws "handle not created" if a background log fires before the form handle exists or
        ' during teardown. Drop the line in that window rather than crash (matches the UiInvoke guard).
        If Not (Me.IsHandleCreated AndAlso Not Me.IsDisposed) Then Return
        Try
            Me.Invoke(Sub()
                          rtb.SelectionStart = rtb.TextLength
                          rtb.SelectionLength = 0
                          rtb.SelectionColor = color
                          rtb.AppendText(text & Environment.NewLine)
                          rtb.SelectionColor = rtb.ForeColor
                      End Sub)
        Catch
            ' handle went away between the check and the invoke - drop this log line
        End Try
    End Sub

    Private Sub ButtonDisabler()
        btnLimit.Enabled = False
        btnNoSpread.Enabled = False
        btnTrail.Enabled = False
        btnMarket.Enabled = False

        btnReduceMarket.Enabled = True
        btnReduceLimit.Enabled = True
        btnCancelAllOpen.Enabled = True

    End Sub

    Private Sub ButtonEnabler()
        btnLimit.Enabled = True
        btnNoSpread.Enabled = True
        btnTrail.Enabled = True
        btnMarket.Enabled = True

        btnReduceMarket.Enabled = False
        btnReduceLimit.Enabled = False
        btnCancelAllOpen.Enabled = False

    End Sub

    ' Position-model close P/L (docs/spec-position-model.md). Basis = exchange average entry
    ' (positionAvgEntry, retained through the flat echo); side = the closing fill's OWN direction
    ' (a closing SELL means the position was long); size = the fill's own amount. Replaces the
    ' old placedPrice/TradeMode/orderAmountVal math, which was wrong after Cancel-All (basis
    ' zeroed), after adds (order price <> avg entry), and after mode flips. Receive-thread safe:
    ' reads engine fields only. If the model is unseeded (avg = 0) the P/L is 0 -> the close
    ' lands in the fix-7 scratch path instead of recording garbage.
    Private Sub ApplyCloseFill(order As JObject, execPrice As Decimal, label As String)
        Dim fillDir As String = order.SelectToken("direction")?.ToString()
        Dim fillAmt As Decimal = If(order.SelectToken("amount")?.ToObject(Of Decimal?)(), 0D)
        Dim signedPL As Decimal = 0D
        If execPrice > 0D AndAlso positionAvgEntry > 0D AndAlso fillAmt > 0D Then
            signedPL = If(fillDir = "sell", execPrice - positionAvgEntry, positionAvgEntry - execPrice) * (fillAmt / execPrice)
        End If
        ' Reliable close: persist to fields so the capture survives until the flat-position echo (which may
        ' be a separate message) triggers CompletePositionClose. Side/size from the fill itself (restore-safe).
        pendingClosePorL = (signedPL >= 0D)
        pendingClosePorLAmt = Math.Abs(Math.Round(signedPL, 2, MidpointRounding.AwayFromZero))
        pendingCloseAmountUSD = fillAmt
        pendingCloseWasLong = (fillDir = "sell")
        pendingCloseExecPrice = execPrice
        pendingCloseLabel = label
        pendingCloseValid = True
    End Sub

    ' Reliable close (docs/spec-close-completion-fix.md + review): runs exactly once per !=0 -> 0 position
    ' transition (co-echo OR split). Consumes the pendingClose* capture for the message + DB record; falls
    ' back to a bare "Position closed." when no fill was captured (external/liquidation close). Receive-thread:
    ' engine fields written directly, UI via Me.Invoke, AppendColoredText self-marshals. OpenPositions is a
    ' per-echo local of the caller and is intentionally NOT reset here (it has no reader after the loop).
    Private Async Function CompletePositionClose() As Task
        ' Audit2 F1 + position model: snapshot the closing basis BEFORE CancelOrderAsync zeroes placedPrice.
        ' Avg entry is the true basis (survives adds/Cancel-All); placedPrice is the unseeded-model fallback.
        Dim entryPriceAtClose As Decimal = If(positionAvgEntry > 0D, positionAvgEntry, placedPrice)

        'Reset all flags
        isTrailingStop = False
        isTrailingPosition = False
        isTrailingStopLossPlaced = False
        SLTriggered = False
        StopLossTriggerOriginal = 0
        emergencyBaseline = 0
        ResetCommandedSLPrices() ' reconcile: position closed - triggered-SL context is gone

        PositionEmpty = True
        PositionLog = False ' Reset position log flag so it can log next new position
        OrderLog = False ' Reset order log flag so it can log next new order

        Await CancelOrderAsync()

        'Clearing margin displays
        Me.Invoke(Sub()
                      lblEstimatedLiquidation.Text = "L.Liq: N/A"
                      lblInitialMargin.Text = "L.IM: N/A"
                      lblMaintenanceMargin.Text = "L.MM: N/A"
                      lblEstimatedLeverage.Text = "L.Lev: N/A"

                      ' Reset colors
                      lblEstimatedLiquidation.ForeColor = Color.Gray
                      lblEstimatedLeverage.ForeColor = Color.Gray
                  End Sub)

        If pendingCloseValid Then
            If (pendingClosePorL = True) And (pendingClosePorLAmt > 0) Then
                AppendColoredText(txtLogs, $"Position executed at {pendingCloseExecPrice}.", Color.LimeGreen)
                AppendColoredText(txtLogs, $"Profit made: ${pendingClosePorLAmt}.", Color.LimeGreen)

                If _indicators.IsAutoTradingEnabled Then
                    LogTradeDecision("Exit Position - Profit", pendingClosePorLAmt, pendingCloseExecPrice)
                End If

            ElseIf (pendingClosePorL = False) And (pendingClosePorLAmt > 0) Then
                AppendColoredText(txtLogs, $"Position executed at {pendingCloseExecPrice}.", Color.Crimson)
                AppendColoredText(txtLogs, $"Loss of: ${pendingClosePorLAmt}.", Color.Crimson)

                If _indicators.IsAutoTradingEnabled Then
                    LogTradeDecision("Exit Position - Loss", pendingClosePorLAmt, pendingCloseExecPrice)
                End If

            Else
                ' Audit2 fix 7 + position model: a tracked close whose P/L rounds to $0.00 (scratch) still
                ' logs and records. No LogTradeDecision here: it has no scratch branch (would write an empty line).
                AppendColoredText(txtLogs, $"Position executed at {pendingCloseExecPrice}.", Color.Yellow)
                AppendColoredText(txtLogs, "Scratch close: P/L ≈ $0.00.", Color.Yellow)
            End If

            ' Audit2 fix 7 + position model: record every computed close - $0.00 scratches and market
            ' reduces included; they are real trades and their absence biased the stats.
            Dim tradeId = RecordCompletedTrade(
                entryPriceAtClose,
                pendingCloseExecPrice,
                If(pendingCloseAmountUSD > 0D, pendingCloseAmountUSD, orderAmountVal),
                pendingClosePorLAmt,
                pendingClosePorL,
                pendingCloseWasLong,
                pendingCloseLabel
            )

            pendingCloseValid = False
        Else
            ' No tracked fill (external/liquidation close): complete the cleanup, nothing to record.
            AppendColoredText(txtLogs, "Position closed.", Color.Yellow)
        End If

        ' Update last trade time immediately to prevent multiple rapid executions
        _indicators.lastAutoTradeTime = DateTime.Now
    End Function

    Public Function RecordCompletedTrade(entryPrice As Decimal, exitPrice As Decimal,
                                   orderSizeUSD As Decimal, profitLossUSD As Decimal,
                                   isProfit As Boolean, tradeMode As Boolean,
                                   orderType As String) As Integer
        Try
            If tradeDatabase Is Nothing Then
                AppendColoredText(txtLogs, "Trade database not initialized", Color.Red)
                Return 0
            End If

            ' Create completed trade record
            Dim completedTrade As New TradeRecord(
            orderType,
            If(tradeMode, "Long", "Short"),
            entryPrice,
            exitPrice,
            orderSizeUSD,
            profitLossUSD,
            isProfit
        )

            ' Record the completed trade (synchronous)
            Dim tradeId As Integer = tradeDatabase.RecordCompletedTrade(completedTrade)

            Return tradeId

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error recording completed trade: {ex.Message}", Color.Red)
            Return 0
        End Try
    End Function



    Private Function CalculateDeribitInverseLiquidationPrice(
        positionSizeUSD As Decimal, leverage As Decimal,
        entryPrice As Decimal, isShort As Boolean) _
        As Dictionary(Of String, Decimal)

        ' ---------------- basics ----------------
        Dim equityBTC As Decimal = GetEquityBTC()
        Dim posBTC As Decimal = positionSizeUSD / entryPrice        ' signed
        Dim absPosBTC As Decimal = Math.Abs(posBTC)

        ' -------- Standard-Margin tier-0 IM/MM (BTC PERP) ----------
        Const BASE_IM As Decimal = 0.02D   ' 2 %
        Const BASE_MM As Decimal = 0.01D   ' 1 %

        Dim initialMarginBTC = absPosBTC * BASE_IM
        Dim maintenanceMarginBTC = absPosBTC * BASE_MM

        ' ------------- liquidation math -----------------------------
        ' Δ  = (Equity – MM) / |posBTC|
        Dim delta As Decimal = 0D
        If absPosBTC > 0D Then _
        delta = (equityBTC - maintenanceMarginBTC) / absPosBTC

        Dim liqPrice As Decimal
        If isShort Then                     ' short → 1 – Δ
            liqPrice = If(delta >= 1D, 0D, entryPrice / (1D - delta))
        Else                                ' long  → 1 + Δ
            liqPrice = entryPrice / (1D + delta)
        End If

        Return New Dictionary(Of String, Decimal) From {
        {"InitialMarginBTC", initialMarginBTC},
        {"MaintenanceMarginBTC", maintenanceMarginBTC},
        {"EstimatedLiquidationPrice", liqPrice},
        {"EffectiveLeverage", leverage}
    }
    End Function




    'Might need to delete if not used later for liquidation estimation with positions
    Private Sub HandleMarginEstimationResponse(response As String)
        Try
            Dim json = JObject.Parse(response)

            ' Check if this is a margin estimation response (ID 890) OR live position data (ID 777)
            Dim messageId = json.SelectToken("id")?.ToObject(Of Integer)()

            If messageId = 890 OrElse messageId = 777 Then

                Dim errorField = json.SelectToken("error")
                If errorField IsNot Nothing Then
                    Dim errorType = If(messageId = 777, "Live position", "Margin estimation")
                    AppendColoredText(txtLogs, $"{errorType} error: {errorField.ToString()}", Color.Yellow)
                    Return
                End If

                Dim result = json.SelectToken("result")
                If result IsNot Nothing Then

                    If messageId = 777 Then
                        ' Handle live position data (single position object)
                        ProcessPositionData(result)
                    Else
                        ' Handle margin estimation data (your existing logic)
                        ProcessEstimationData(result)
                    End If
                End If
            End If

        Catch ex As Exception
            ' Ignore parsing errors for non-relevant responses
        End Try
    End Sub

    ' Restore hardening: restore OTOCO order context from the id-778 snapshot (get_open_orders).
    ' Receive-thread handler: engine fields written here directly, displays via UiInvoke. Mirrors
    ' the echo handler's single-writer discipline - prices seed only when the engine field is 0,
    ' so a lagging echo/active trailing can't be reset backward. Whole body gated on Not cancelPending.
    Private Sub HandleOpenOrdersSnapshot(response As String)
        Try
            Dim json = JObject.Parse(response)
            Dim messageId = json.SelectToken("id")?.ToObject(Of Integer?)()
            If Not (messageId.HasValue AndAlso messageId.Value = 778) Then Return

            ' Echo-handler convention: a cancel in flight means the context we'd restore is being torn down.
            If cancelPending Then Return

            Dim errorField = json.SelectToken("error")
            If errorField IsNot Nothing Then
                AppendColoredText(txtLogs, $"Open-orders snapshot error: {errorField.ToString()}", Color.Yellow)
                Return
            End If

            Dim result = TryCast(json.SelectToken("result"), JArray)
            If result Is Nothing OrElse result.Count = 0 Then Return ' flat restart: nothing to restore, no announce

            ' First pass: is a working (unfilled) entry still open? CurrentTPOrderId/CurrentSLOrderId are
            ' the "there is a live OTOCO working order" ids - only adopt them when the entry leg is open.
            ' Also capture the entry's side. A restart with a working entry leaves the POSITION flat, so the
            ' id-777 announce (gated on size <> 0) can't call SetTradeMode - TradeMode would stay at its LONG
            ' default and the SL-trailing/emergency branches would run the wrong side once the entry fills
            ' (owner runtime test 2026-07-03: SHORT restored with Buy highlighted; SL never repositioned).
            Dim workingEntryFound As Boolean = False
            Dim entryIsLong As Boolean = False
            For Each o In result
                Dim lbl = o.SelectToken("label")?.ToString()
                Dim st = o.SelectToken("order_state")?.ToString()
                If (lbl = "EntryLimitOrder" OrElse lbl = "EntryTrailingOrder") AndAlso st = "open" Then
                    workingEntryFound = True
                    entryIsLong = (o.SelectToken("direction")?.ToString() = "buy")
                End If
            Next

            ' Restore hardening (owner runtime fix): set the trade side from the working entry's own
            ' direction, so trailing/emergency run the correct branch the moment the entry fills.
            ' SetTradeMode touches controls -> UiInvoke. Harmless if id-777 already set the same side for a
            ' filled position; the filled-at-connect case is still handled by the id-777 announce block.
            If workingEntryFound Then UiInvoke(Sub() SetTradeMode(entryIsLong))

            Dim entryDesc As String = "none", tpDesc As String = "none", slDesc As String = "none"

            For Each o In result
                Dim label = o.SelectToken("label")?.ToString()
                Dim state = o.SelectToken("order_state")?.ToString()
                Dim id = o.SelectToken("order_id")?.ToString()
                Dim price = o.SelectToken("price")?.ToObject(Of Decimal?)()
                Dim triggerPrice = o.SelectToken("trigger_price")?.ToObject(Of Decimal?)()

                Select Case label
                    Case "EntryLimitOrder", "EntryTrailingOrder"
                        If state = "open" Then
                            CurrentOpenOrderId = id
                            Dim seeded As Boolean = False
                            If placedPrice = 0D Then
                                placedPrice = If(price, 0D)
                                seeded = True
                            End If
                            If seeded AndAlso price.HasValue Then UiInvoke(Sub() txtPlacedPrice.Text = price.Value.ToString("F2"))
                            entryDesc = $"{id}@{If(price?.ToString("F2"), "?")}"
                        End If

                    Case "TakeLimitProfit"
                        If state = "untriggered" OrElse state = "open" Then
                            PositionTPOrderId = id
                            If workingEntryFound Then CurrentTPOrderId = id
                            If price.HasValue Then UiInvoke(Sub() txtPlacedTakeProfitPrice.Text = price.Value.ToString("F2"))
                            tpDesc = $"{id}@{If(price?.ToString("F2"), "?")}"
                        End If

                    Case "StopLossOrder"
                        If state = "untriggered" Then
                            PositionSLOrderId = id
                            If workingEntryFound Then CurrentSLOrderId = id
                            Dim seeded As Boolean = False
                            If placedStopLossPrice = 0D Then
                                placedStopLossPrice = If(price, 0D)
                                seeded = True
                            End If
                            If StopLossTriggerOriginal = 0D Then StopLossTriggerOriginal = If(triggerPrice, 0D)
                            UiInvoke(Sub()
                                         If triggerPrice.HasValue Then txtPlacedTrigStopPrice.Text = triggerPrice.Value.ToString("F2")
                                         If seeded AndAlso price.HasValue Then txtPlacedStopLossPrice.Text = price.Value.ToString("F2")
                                     End Sub)
                            slDesc = $"{id}@trig {If(triggerPrice?.ToString("F2"), "?")} (untriggered)"
                        ElseIf state = "open" Then
                            ' Already triggered: this is the working stop-market leg.
                            SLTriggered = True
                            PositionSLOrderId = id
                            Dim seeded As Boolean = False
                            If placedStopLossPrice = 0D Then
                                placedStopLossPrice = If(price, 0D)
                                seeded = True
                            End If
                            If StopLossTriggerOriginal = 0D Then StopLossTriggerOriginal = If(triggerPrice, 0D)
                            ' Item 1: restored into an already-triggered SL - the emergency baseline is the actual SL price.
                            If emergencyBaseline = 0D Then emergencyBaseline = If(price, 0D)
                            UiInvoke(Sub()
                                         If triggerPrice.HasValue Then txtPlacedTrigStopPrice.Text = triggerPrice.Value.ToString("F2")
                                         If seeded AndAlso price.HasValue Then txtPlacedStopLossPrice.Text = price.Value.ToString("F2")
                                     End Sub)
                            slDesc = $"{id}@{If(price?.ToString("F2"), "?")} (triggered)"
                        End If

                    Case "TrailingStopLoss"
                        ' Out of scope v1: restored trailing context is rarer and hairier - don't guess.
                        AppendColoredText(txtLogs, "Restore: trailing order found; manual re-attach required (out of scope v1).", Color.Yellow)

                End Select
            Next

            If entryDesc <> "none" OrElse tpDesc <> "none" OrElse slDesc <> "none" Then
                AppendColoredText(txtLogs, $"Restored order context: entry={entryDesc}, TP={tpDesc}, SL={slDesc}", Color.Cyan)
            End If

        Catch ex As Exception
            ' Ignore parsing errors for non-relevant responses
        End Try
    End Sub

    Private Sub ProcessPositionData(positionData As JToken)
        Try
            ' Extract live position information from Deribit
            Dim initialMargin = positionData.SelectToken("initial_margin")?.ToObject(Of Decimal?)()
            Dim maintenanceMargin = positionData.SelectToken("maintenance_margin")?.ToObject(Of Decimal?)()
            Dim estimatedLiquidation = positionData.SelectToken("estimated_liquidation_price")?.ToObject(Of Decimal?)()
            Dim positionSize = positionData.SelectToken("size")?.ToObject(Of Decimal?)()
            Dim markPrice = positionData.SelectToken("mark_price")?.ToObject(Of Decimal?)()
            Dim averagePrice = positionData.SelectToken("average_price")?.ToObject(Of Decimal?)()

            ' Position model: keep the engine fields current from id-777 snapshots too.
            If positionSize.HasValue Then
                positionSizeUSD = positionSize.Value
                If positionSize.Value <> 0D AndAlso averagePrice.HasValue AndAlso averagePrice.Value > 0D Then
                    positionAvgEntry = averagePrice.Value
                End If
            End If

            ' Restart restore (display only - engine fields already correct; placedPrice is
            ' order-context and is NOT seeded here). Announce once per connection.
            If positionSize.HasValue AndAlso positionSize.Value <> 0D AndAlso Not positionRestoreAnnounced Then
                positionRestoreAnnounced = True
                Dim side As String = If(positionSize.Value > 0D, "LONG", "SHORT")
                AppendColoredText(txtLogs, $"Open position detected: {side} {Math.Abs(positionSize.Value)} @ {If(averagePrice?.ToString("F2"), "?")}", Color.Yellow)

                ' Restore hardening: trade context must match the REAL position, or the SL-trailing
                ' and emergency branches run the wrong side (TradeMode defaults to LONG at startup).
                ' SetTradeMode touches controls -> inside the existing UiInvoke.
                UiInvoke(Sub()
                             SetTradeMode(positionSize.Value > 0D)
                             lblOrderStatus.Text = "In Position"
                             lblOrderStatus.ForeColor = Color.Yellow
                             If averagePrice.HasValue Then txtPlacedPrice.Text = averagePrice.Value.ToString("F2")
                         End Sub)

                ' placedPrice: restart-restore exception to placement-only seeding (it IS 0 here;
                ' no working entry exists, so no reposition can act on it) - gives the PnL/display
                ' path its basis back and stops the 5s "Placed price = 0" warning loop.
                If placedPrice = 0D Then placedPrice = If(averagePrice, 0D)
            End If

            ' CORRECTED: For BTC-PERPETUAL, positionSize is in USD, not BTC
            Dim effectiveLeverage As Decimal = 0
            Dim accountBalanceUSD As Decimal = 0 ' hoisted: 0 = equity not yet received ("pending" display)

            If positionSize.HasValue AndAlso markPrice.HasValue Then
                ' Position value in USD is simply the absolute position size (already in USD)
                Dim positionValueUSD As Decimal = Math.Abs(positionSize.Value)

                ' Account balance in USD
                Dim accountBalanceBTC As Decimal = GetEquityBTC()
                accountBalanceUSD = accountBalanceBTC * markPrice.Value

                ' Calculate leverage as position value / account balance
                If accountBalanceUSD > 0 Then
                    effectiveLeverage = positionValueUSD / accountBalanceUSD
                End If

                ' Debug logging with corrected values
                'AppendColoredText(txtLogs, $"DEBUG CORRECTED: positionValueUSD={Math.Abs(positionSize.Value):F2}, accountBalanceUSD={accountBalanceUSD:F2}", Color.Gray)



            End If

            Me.Invoke(Sub()
                          ' Update UI with LIVE Deribit data
                          If estimatedLiquidation.HasValue AndAlso estimatedLiquidation.Value > 0 Then
                              lblEstimatedLiquidation.Text = $"L.Liq: ${estimatedLiquidation.Value:F2}"
                              lblEstimatedLiquidation.ForeColor = Color.Red
                          Else
                              lblEstimatedLiquidation.Text = "L.Liq: N/A"
                              lblEstimatedLiquidation.ForeColor = Color.Gray
                          End If

                          If initialMargin.HasValue Then
                              lblInitialMargin.Text = $"L.IM: {initialMargin.Value:F8} BTC"
                          End If

                          If maintenanceMargin.HasValue Then
                              lblMaintenanceMargin.Text = $"L.MM: {maintenanceMargin.Value:F8} BTC"
                          End If

                          ' Display proper leverage based on account balance
                          ' ("pending" until the first equity update arrives - avoids a misleading 0.00x)
                          lblEstimatedLeverage.Text = If(accountBalanceUSD = 0D, "L.Lev: pending", $"L.Lev: {effectiveLeverage:F2}x")

                          ' Color code leverage risk
                          If effectiveLeverage > 10 Then
                              lblEstimatedLeverage.ForeColor = Color.Red
                          ElseIf effectiveLeverage > 5 Then
                              lblEstimatedLeverage.ForeColor = Color.Orange
                          Else
                              lblEstimatedLeverage.ForeColor = Color.LimeGreen
                          End If
                      End Sub)

            ' Improved logging with corrected calculation
            Dim liquidationText As String = If(estimatedLiquidation.HasValue AndAlso estimatedLiquidation.Value > 0,
                                          "$" & estimatedLiquidation.Value.ToString("F2"), "N/A")

            AppendColoredText(txtLogs, $"LIVE position data - Liq: {liquidationText}, Leverage: {If(accountBalanceUSD = 0D, "pending", $"{effectiveLeverage:F2}x")}", Color.Red)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error processing live position data: {ex.Message}", Color.Red)
        End Try
    End Sub


    'For text file trade logging
    Private Sub LogTradeDecision(ordertype As String, PLAmt As Decimal, ExitP As Decimal)

        ' Cross-thread fix: LogTradeDecision is called from the receive thread, so snapshot the placed-price
        ' displays on the UI thread (TryParse, so a blank field logs 0 instead of throwing the handler).
        Dim placedPrice As Decimal = 0D
        Dim TakeProfit As Decimal = 0D
        Dim triggerPrice As Decimal = 0D
        Dim snapshot As Action = Sub()
                                     Decimal.TryParse(txtPlacedPrice.Text, placedPrice)
                                     Decimal.TryParse(txtPlacedTakeProfitPrice.Text, TakeProfit)
                                     Decimal.TryParse(txtPlacedTrigStopPrice.Text, triggerPrice)
                                 End Sub
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then Me.Invoke(snapshot) Else snapshot()
        Dim logentry As String = String.Empty

        'Dim TPPrice, SLPrice As Decimal

        'If TradeMode Then
        ' TPPrice = placedPrice + TakeProfit
        ' SLPrice = placedPrice - triggerPrice
        'Else
        ' TPPrice = placedPrice - TakeProfit
        'SLPrice = placedPrice + triggerPrice
        'End If

        If ordertype.Contains("Exit Position") Then

            If ordertype.Contains("Exit Position - Profit") Then
                logentry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | " &
                       $"Type: {ordertype} | " &
                        $"Exit Price: {ExitP} | " &
                       $"Profit: {PLAmt} | "
            ElseIf ordertype.Contains("Exit Position - Loss") Then
                logentry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | " &
                    $"Type: {ordertype} | " &
                     $"Exit Price: {ExitP} | " &
                    $"Loss: {PLAmt} | "
            ElseIf ordertype.Contains("Exit Position - Market Order Loss") Then
                logentry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | " &
                    $"Type: {ordertype} | " &
                     $"Exit Price: {newPricePublic} | Loss: Check Order History "
            End If

        Else
            logentry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | " &
                       $"Type: {ordertype} | " &
                        $"Placed Price: {placedPrice} | " &
                       $"Take Profit: {TakeProfit} | " &
                       $"Stop Loss Trigger: {triggerPrice} "
        End If

        ' Write to file for later analysis
        Try
            System.IO.File.AppendAllText("AutoTradeLog.txt", logentry & Environment.NewLine)
        Catch
            AppendColoredText(txtLogs, "Text file IO error", Color.Red) ' Handle file write errors
        End Try

    End Sub


    Private Sub ProcessEstimationData(estimationData As JToken)
        ' Your existing estimation logic remains the same...
        ' (Keep your current estimation processing code here)
    End Sub


    'All button logic below
    '----------------------------------------------------------------------------------

    Private Async Sub btnConnect_Click(sender As Object, e As EventArgs) Handles btnConnect.Click
        Try
            If btnConnect.Text = "Connect!" Then
                btnConnect.Enabled = False
                btnConnect.Text = "Connecting..."
                btnConnect.BackColor = Color.Orange

                Try
                    Await ConnectToWebSocketDirectly()
                    AppendColoredText(txtLogs, "Connected successfully", Color.LimeGreen)
                Catch ex As Exception
                    AppendColoredText(txtLogs, $"Connection failed: {ex.Message}", Color.Red)
                    btnConnect.Text = "Connect!"
                    btnConnect.BackColor = Color.Red
                Finally
                    btnConnect.Enabled = True
                End Try

            ElseIf btnConnect.Text = "ONLINE" Then
                ' Manual rate limit refresh when already connected
                Dim refreshratelimits = Task.Run(Async Function()
                                                     Try
                                                         Await InitializeRateLimitsAfterAuth()
                                                         AppendColoredText(txtLogs, "Rate limits manually refreshed", Color.LimeGreen)
                                                     Catch ex As Exception
                                                         AppendColoredText(txtLogs, $"Rate limit refresh failed: {ex.Message}", Color.Yellow)
                                                     End Try
                                                 End Function)
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Button click error: {ex.Message}", Color.Red)
        End Try
    End Sub



    Public Async Function InitializeRateLimits() As Task
        Try
            AppendColoredText(txtLogs, "Initializing rate limits from account summary...", Color.DodgerBlue)

            ' Get actual account limits
            accountLimits = Await GetAccountSummaryLimits()

            ' Initialize rate limiter with actual limits
            rateLimiter = New DeribitRateLimiter(accountLimits.MaxCredits, 50) 'Conservative = 200 | Reasonable = 50

            AppendColoredText(txtLogs, $"Rate limits: {accountLimits.MaxCredits} max credits, sustainable rate: {accountLimits.MaxCredits / 50} req/sec", Color.LimeGreen) 'Conservative = 200 | Reasonable = 50

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Rate limit initialization error: {ex.Message}", Color.Yellow)
            ' Initialize with very conservative defaults
            'rateLimiter = New DeribitRateLimiter(1000, 200)

            ' Initialize with more reasonable defaults
            rateLimiter = New DeribitRateLimiter(2000, 50) ' Reduced cost per request
        End Try
    End Function

    ' Single shutdown path: the title-bar X and Alt+F4 land here (CS_NOCLOSE override removed). The old
    ' in-app btnClose "-X-" button was removed - the title-bar X runs this same full shutdown.
    ' Note for the ergonomics implementer: config save goes at the TOP of this handler, before teardown.
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

    Private Sub btnClearLog_Click(sender As Object, e As EventArgs) Handles btnClearLog.Click
        txtLogs.Clear()

    End Sub

    ' Decouple v2: mode switching extracted from btnBuy_Click/btnSell_Click (bodies unchanged) so
    ' the automation API can set direction on the UI thread without PerformClick.
    Private Sub SetTradeMode(isLong As Boolean)
        If isLong Then

            'Sets mode to Buy mode
            TradeMode = True

            'Set btnBuy color to on
            btnBuy.FlatStyle = FlatStyle.Flat
            btnBuy.FlatAppearance.BorderSize = 2 ' Optional: Highlight border
            btnBuy.BackColor = Color.Lime ' Change to "depressed" color
            btnBuy.ForeColor = Color.Black

            'Reset btnSell color
            btnSell.FlatStyle = FlatStyle.Popup
            btnSell.FlatAppearance.BorderSize = 0
            btnSell.BackColor = Color.DarkRed ' Reset to default color
            btnSell.ForeColor = Color.White


            btnLimit.BackColor = Color.DarkGreen
            btnNoSpread.BackColor = Color.Green
            btnTrail.BackColor = Color.ForestGreen
            btnMarket.BackColor = Color.SeaGreen

            btnLimit.Text = "Limit BUY"
            btnNoSpread.Text = "No Sprd. BUY"
            btnTrail.Text = "Trail BUY"
            btnMarket.Text = "Mkt. BUY"
            btnReduceLimit.Text = "Reduce SELL"
            btnReduceMarket.Text = "Mkt. Rdc. Sell"

            TradeButtons.Text = "Long"
            PlacedOrders.Text = "Placed Long"

            txtPlacedTakeProfitPrice.Location = New Point(173, 95)
            txtPlacedPrice.Location = New Point(173, 146)
            txtPlacedTrigStopPrice.Location = New Point(173, 197)
            txtPlacedStopLossPrice.Location = New Point(173, 248)

            lblPlacedTakeProfitPrice.Location = New Point(12, 97)
            lblPlacedPrice.Location = New Point(21, 150)
            lblPlacedTrigStopPrice.Location = New Point(24, 201)
            lblPlacedStopLossPrice.Location = New Point(22, 254)

            btnEditTPPrice.Location = New Point(379, 93)
            btnEditSLPrice.Location = New Point(379, 194)
            btnTPOffset.Location = New Point(379, 247)

        Else

            'Sets mode to Sell mode
            TradeMode = False

            'Set btnSell color to on
            btnSell.FlatStyle = FlatStyle.Flat
            btnSell.FlatAppearance.BorderSize = 2 ' Optional: Highlight border
            btnSell.BackColor = Color.Red ' Change to "depressed" color
            btnSell.ForeColor = Color.Black

            'Reset btnBuy color
            btnBuy.FlatStyle = FlatStyle.Popup
            btnBuy.FlatAppearance.BorderSize = 0
            btnBuy.BackColor = Color.DarkGreen ' Reset to default color
            btnBuy.ForeColor = Color.White


            btnLimit.BackColor = Color.DarkRed
            btnNoSpread.BackColor = Color.Firebrick
            btnTrail.BackColor = Color.IndianRed
            btnMarket.BackColor = Color.LightCoral

            btnLimit.Text = "Limit SELL"
            btnNoSpread.Text = "No Sprd. SELL"
            btnTrail.Text = "Trail SELL"
            btnMarket.Text = "Mkt. SELL"
            btnReduceLimit.Text = "Reduce BUY"
            btnReduceMarket.Text = "Mkt. Rdc. Buy"

            TradeButtons.Text = "Short"
            PlacedOrders.Text = "Placed Short"

            txtPlacedStopLossPrice.Location = New Point(173, 95)
            txtPlacedTrigStopPrice.Location = New Point(173, 146)
            txtPlacedPrice.Location = New Point(173, 197)
            txtPlacedTakeProfitPrice.Location = New Point(173, 248)

            lblPlacedStopLossPrice.Location = New Point(22, 97)
            lblPlacedTrigStopPrice.Location = New Point(24, 150)
            lblPlacedPrice.Location = New Point(21, 201)
            lblPlacedTakeProfitPrice.Location = New Point(12, 254)

            btnEditTPPrice.Location = New Point(379, 247)
            btnEditSLPrice.Location = New Point(379, 146)
            btnTPOffset.Location = New Point(379, 93)

        End If
    End Sub

    Private Sub btnSell_Click(sender As Object, e As EventArgs) Handles btnSell.Click
        SetTradeMode(False)
    End Sub

    Private Sub btnBuy_Click(sender As Object, e As EventArgs) Handles btnBuy.Click
        SetTradeMode(True)
    End Sub

    Private Async Sub btnLimit_Click(sender As Object, e As EventArgs) Handles btnLimit.Click
        Try
            ' Get margin estimation before placing order
            btnEstimateMargins_Click(Nothing, Nothing) ' Call the estimation function

            ' Wait a moment for UI update
            Await Task.Delay(500)

            ' Then execute the order
            If TradeMode = True Then
                Await ExecuteOrderAsync("BuyLimit")
            Else
                Await ExecuteOrderAsync("SellLimit")
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnLimit_Click: {ex.Message}", Color.Red)
        End Try
    End Sub



    Private Async Sub btnNoSpread_Click(sender As Object, e As EventArgs) Handles btnNoSpread.Click
        Try
            ' Get margin estimation before placing order
            btnEstimateMargins_Click(Nothing, Nothing) ' Call the estimation function

            ' Wait a moment for UI update
            Await Task.Delay(500)

            ' Then execute the order
            If TradeMode = True Then
                Await ExecuteOrderAsync("BuyNoSpread")
            Else
                Await ExecuteOrderAsync("SellNoSpread")
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnNoSpread_Click: {ex.Message}", Color.Red)
        End Try

    End Sub

    Private Async Sub btnLCancelAllOpen_Click(sender As Object, e As EventArgs) Handles btnCancelAllOpen.Click
        Await CancelOrderAsync()
    End Sub

    Private Sub selectallclick(sender As Object, e As EventArgs) Handles txtAmount.Click, txtTakeProfit.Click, txtTrigger.Click, txtStopLoss.Click, txtTriggerOffset.Click, txtTPOffset.Click, txtComms.Click, txtManualTP.Click, txtManualSL.Click, txtPlacedTakeProfitPrice.Click, txtPlacedTrigStopPrice.Click, txtPlacedStopLossPrice.Click
        'Cast the sender to a TextBox
        Dim txtBox = CType(sender, TextBox)

        'Select all text in the TextBox
        txtBox.SelectionStart = 0
        txtBox.SelectionLength = txtBox.Text.Length
    End Sub

    Private Async Sub btnMarket_Click(sender As Object, e As EventArgs) Handles btnMarket.Click
        Try
            ' Get margin estimation before placing order
            btnEstimateMargins_Click(Nothing, Nothing) ' Call the estimation function

            ' Wait a moment for UI update
            Await Task.Delay(500)
            ' Then execute the order
            If TradeMode = True Then
                Await ExecuteOrderAsync("BuyMarket")
            Else
                Await ExecuteOrderAsync("SellMarket")
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnMarket_Click: {ex.Message}", Color.Red)
        End Try

    End Sub

    Private Async Sub btnReduceLimit_Click(sender As Object, e As EventArgs) Handles btnReduceLimit.Click
        Try
            ' Position model: reduce the ACTUAL position. Direction from the position sign (a
            ' wrong TradeMode used to produce a silently-rejected reduce-only order); amount =
            ' full position size (owner's full-close workflow; reduce_only caps there anyway).
            Dim posSize As Decimal = positionSizeUSD
            If posSize = 0D Then
                AppendColoredText(txtLogs, "No open position to reduce.", Color.Yellow)
                Return
            End If
            Dim direction As String = If(posSize > 0D, "sell", "buy")
            Dim amount As Decimal = Math.Abs(posSize)

            ' Passive side for the chosen direction (engine quote fields, not textbox parses)
            Dim price As Decimal = If(direction = "buy", BestBidPrice, BestAskPrice)
            If price <= 0 Then
                AppendColoredText(txtLogs, "Invalid price.", Color.Red)
                Return
            End If

            Await SendReduceOrderAsync(price, amount, direction, isMarketOrder:=False)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnReduceLimit_Click: {ex.Message}", Color.Red)
        End Try
    End Sub



    ' Shared reduce-only MARKET order logic (cross-thread fix). Called by btnReduceMarket_Click (UI thread)
    ' AND by the emergency stop path in UpdateStopLossForTriggeredStopLossOrder (receive thread), so that
    ' path no longer needs a cross-thread btnReduceMarket.PerformClick(). Reads orderAmountVal, not txtAmount.
    Private Async Function SendReduceMarketOrderAsync() As Task
        ' Connection guard first (runtime test 4 follow-up): return before the position-model
        ' fallback logs, so a disconnected click logs the skip line alone - not fallback noise
        ' followed by the skip. SendReduceOrderAsync keeps its own guard for the other callers.
        If Not IsWebSocketConnected Then
            AppendColoredText(txtLogs, "WebSocket is not connected - reduce order skipped.", Color.Red)
            Return
        End If

        ' Position model: flatten the ACTUAL position - the emergency stop must close what is
        ' really open, not what txtAmount says (a stale amount used to under-close after adds).
        Dim posSize As Decimal = positionSizeUSD
        Dim direction As String
        Dim amount As Decimal
        If posSize <> 0D Then
            direction = If(posSize > 0D, "sell", "buy")
            amount = Math.Abs(posSize)
        Else
            ' Safety fallback: model unseeded (shouldn't happen after the connect seed) - behave
            ' exactly like the old path so the emergency stop is never WEAKER than before.
            AppendColoredText(txtLogs, "Position model empty - using TradeMode/txtAmount fallback for market reduce", Color.Orange)
            direction = If(TradeMode, "sell", "buy")
            amount = orderAmountVal
            If amount <= 0 Then
                AppendColoredText(txtLogs, "Invalid amount.", Color.Red)
                Return
            End If
        End If

        ' Call the function to send the reduce-only market order
        Await SendReduceOrderAsync(Nothing, amount, direction, isMarketOrder:=True)
    End Function

    Private Async Sub btnReduceMarket_Click(sender As Object, e As EventArgs) Handles btnReduceMarket.Click
        Try
            Await SendReduceMarketOrderAsync()
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnReduceMarket_Click: {ex.Message}", Color.Red)
        End Try
    End Sub

    Private Async Sub btnEditTPPrice_Click(sender As Object, e As EventArgs) Handles btnEditTPPrice.Click
        Try
            If (isTrailingStop = True) And (isTrailingPosition = True) And (isTrailingStopLossPlaced = True) Then
                If Decimal.Parse(txtManualTP.Text) > 0 Then
                    txtPlacedTakeProfitPrice.Text = txtManualTP.Text
                    If TradeMode = True Then
                        If Decimal.Parse(txtPlacedTakeProfitPrice.Text) < (Decimal.Parse(txtPlacedPrice.Text) + Decimal.Parse(txtComms.Text)) Then
                            AppendColoredText(txtLogs, "Manual TP is less than comms paid.", Color.Yellow)
                        End If
                    Else
                        If Decimal.Parse(txtPlacedTakeProfitPrice.Text) > (Decimal.Parse(txtPlacedPrice.Text) - Decimal.Parse(txtComms.Text)) Then
                            AppendColoredText(txtLogs, "Manual TP is less than comms paid.", Color.Yellow)
                        End If
                    End If

                Else
                    If TradeMode = True Then
                        txtPlacedTakeProfitPrice.Text = Decimal.Parse(txtPlacedPrice.Text) + (Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text))
                    Else
                        txtPlacedTakeProfitPrice.Text = Decimal.Parse(txtPlacedPrice.Text) - (Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text))
                    End If
                End If
                AppendColoredText(txtLogs, $"Updated Trailing SL target to: ${txtPlacedTakeProfitPrice.Text}", Color.Yellow)

            Else
                If Decimal.Parse(txtPlacedTakeProfitPrice.Text) > 0 Then
                    Dim newTPprice = Decimal.Parse(txtPlacedTakeProfitPrice.Text)
                    Dim amount = Decimal.Parse(txtAmount.Text)
                    Dim TPOrderID As String = Nothing

                    ' Ensure WebSocket is connected
                    If webSocketClient Is Nothing OrElse webSocketClient.State <> WebSocketState.Open Then
                        'txtLogs.AppendText("WebSocket is not connected." + Environment.NewLine)
                        AppendColoredText(txtLogs, "WebSocket is not connected.", Color.Red)
                        Return
                    End If

                    If CurrentTPOrderId IsNot Nothing Then
                        TPOrderID = CurrentTPOrderId
                    ElseIf PositionTPOrderId IsNot Nothing Then
                        TPOrderID = PositionTPOrderId
                    Else
                        AppendColoredText(txtLogs, "T.P. Order ID not found for edit.", Color.Yellow)
                        Return ' Audit2 F4: don't send an edit with a null order_id
                    End If

                    ' Construct the payload for updating the take profit order
                    Dim updateTakeProfitPayload As New JObject From {
                {"jsonrpc", "2.0"},
                {"id", 223345},
                {"method", "private/edit"},
                {"params", New JObject From {
                    {"order_id", TPOrderID}, ' Replace with the take profit order ID
                    {"price", newTPprice},
                    {"amount", amount}
                }}
            }

                    ' Send the payload to update the take profit order
                    Await SendWebSocketMessageAsync(updateTakeProfitPayload.ToString)

                    AppendColoredText(txtLogs, $"Updated T.P. to: ${newTPprice}", Color.Yellow)

                    'Else
                    'AppendColoredText(txtLogs, "T.P. textbox is 0 or no Trailing S.L. order.", Color.Yellow)
                End If
            End If
        Catch ex As Exception
            txtLogs.AppendText("Error in btnEditTPPrice: " & ex.Message & Environment.NewLine)
        End Try
    End Sub

    Private Async Sub btnEditSLPrice_Click(sender As Object, e As EventArgs) Handles btnEditSLPrice.Click
        Try
            If Decimal.Parse(txtPlacedTrigStopPrice.Text) > 0 Then
                Dim newTSprice As Decimal = Decimal.Parse(txtPlacedTrigStopPrice.Text)
                Dim newSLprice As Decimal
                Dim amount As Decimal = Decimal.Parse(txtAmount.Text)
                Dim SLOrderID As String = Nothing

                ' Ensure WebSocket is connected
                If webSocketClient Is Nothing OrElse webSocketClient.State <> WebSocketState.Open Then
                    'txtLogs.AppendText("WebSocket is not connected." + Environment.NewLine)
                    AppendColoredText(txtLogs, "WebSocket is not connected.", Color.Red)
                    Return
                End If

                If CurrentSLOrderId IsNot Nothing Then
                    SLOrderID = CurrentSLOrderId
                ElseIf PositionSLOrderId IsNot Nothing Then
                    SLOrderID = PositionSLOrderId
                Else
                    AppendColoredText(txtLogs, "S.L. Order ID not found for edit.", Color.Yellow)
                    Return ' Audit2 F4: don't send an edit with a null order_id
                End If

                If TradeMode = True Then
                    newSLprice = newTSprice - Decimal.Parse(txtStopLoss.Text)
                Else
                    newSLprice = newTSprice + Decimal.Parse(txtStopLoss.Text)
                End If

                ' Construct the payload for updating the take profit order
                Dim updateTakeProfitPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 223346},
            {"method", "private/edit"},
            {"params", New JObject From {
                {"order_id", SLOrderID}, ' Replace with the take profit order ID
                {"price", newSLprice},
                {"trigger_price", newTSprice},
                {"amount", amount}
            }}
        }

                ' Send the payload to update the take profit order
                Await SendWebSocketMessageAsync(updateTakeProfitPayload.ToString())

                AppendColoredText(txtLogs, $"Updated T.S. to: ${newTSprice}", Color.Yellow)
                AppendColoredText(txtLogs, $"Updated S.L. to: ${newSLprice}", Color.Yellow)
            Else
                AppendColoredText(txtLogs, "S.L. textbox is 0", Color.Yellow)
            End If

        Catch ex As Exception
            txtLogs.AppendText("Error in btnEditSLPrice: " & ex.Message & Environment.NewLine)
        End Try
    End Sub

    Private Async Sub btnTrail_Click(sender As Object, e As EventArgs) Handles btnTrail.Click
        Try
            ' Get margin estimation before placing order
            btnEstimateMargins_Click(Nothing, Nothing) ' Call the estimation function

            ' Wait a moment for UI update
            Await Task.Delay(500)

            ' Then execute the order
            If TradeMode = True Then
                Await StopLossForTrailingOrderAsync("BuyTrail")
            Else
                Await StopLossForTrailingOrderAsync("SellTrail")
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnTrail_Click: {ex.Message}", Color.Red)
        End Try

    End Sub

    Private Sub btnViewTrades_Click(sender As Object, e As EventArgs) Handles btnViewTrades.Click
        Try
            If tradeDatabase Is Nothing Then
                AppendColoredText(txtLogs, "Trade database not initialized", Color.Red)
                Return
            End If

            Dim trades = tradeDatabase.GetAllTrades

            If trades.Count > 0 Then
                Dim viewForm As New Form
                viewForm.Text = "Trade History - Right-click to Delete"
                viewForm.Size = New Size(1000, 700)
                viewForm.StartPosition = FormStartPosition.CenterScreen

                Dim dataGrid As New DataGridView
                dataGrid.Dock = DockStyle.Fill
                dataGrid.AutoGenerateColumns = False
                dataGrid.ReadOnly = True
                dataGrid.AllowUserToAddRows = False
                dataGrid.AllowUserToDeleteRows = False
                dataGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
                dataGrid.MultiSelect = True ' Allow multiple row selection

                ' Your existing column definitions here...
                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "ID",
                .DataPropertyName = "TradeId",
                .Width = 50,
                .DefaultCellStyle = New DataGridViewCellStyle With {.Alignment = DataGridViewContentAlignment.MiddleCenter}
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Date/Time",
                .DataPropertyName = "Timestamp",
                .Width = 140,
                .DefaultCellStyle = New DataGridViewCellStyle With {.Format = "MM/dd/yyyy HH:mm:ss"}
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Type",
                .DataPropertyName = "OrderType",
                .Width = 70,
                .DefaultCellStyle = New DataGridViewCellStyle With {.Alignment = DataGridViewContentAlignment.MiddleCenter}
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Direction",
                .DataPropertyName = "Direction",
                .Width = 70,
                .DefaultCellStyle = New DataGridViewCellStyle With {.Alignment = DataGridViewContentAlignment.MiddleCenter}
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Entry Price",
                .DataPropertyName = "EntryPrice",
                .Width = 100,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Exit Price",
                .DataPropertyName = "ExitPrice",
                .Width = 100,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Size (USD)",
                .DataPropertyName = "OrderSizeUSD",
                .Width = 100,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "P/L (USD)",
                .DataPropertyName = "ProfitLossUSD",
                .Width = 100,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                ' Add result column
                Dim resultColumn As New DataGridViewTextBoxColumn With {
                .HeaderText = "Result",
                .Name = "ResultColumn",
                .Width = 70,
                .DefaultCellStyle = New DataGridViewCellStyle With {.Alignment = DataGridViewContentAlignment.MiddleCenter}
            }
                dataGrid.Columns.Add(resultColumn)

                ' Bind data and color code rows
                dataGrid.DataSource = trades

                For Each row As DataGridViewRow In dataGrid.Rows
                    If row.DataBoundItem IsNot Nothing Then
                        Dim trade = CType(row.DataBoundItem, TradeRecord)

                        If trade.IsProfit Then
                            row.Cells("ResultColumn").Value = "WIN"
                            row.DefaultCellStyle.BackColor = Color.LightGreen
                            row.DefaultCellStyle.ForeColor = Color.DarkGreen
                        Else
                            row.Cells("ResultColumn").Value = "LOSS"
                            row.DefaultCellStyle.BackColor = Color.LightCoral
                            row.DefaultCellStyle.ForeColor = Color.DarkRed
                        End If
                    End If
                Next

                ' Add context menu for deletion
                Dim contextMenu As New ContextMenuStrip

                Dim deleteSelectedItem As New ToolStripMenuItem("Delete Selected Trade(s)")
                AddHandler deleteSelectedItem.Click, Sub()
                                                         DeleteSelectedTrades(dataGrid, trades)
                                                     End Sub

                Dim deleteAllItem As New ToolStripMenuItem("Delete All Trades")
                AddHandler deleteAllItem.Click, Sub()
                                                    DeleteAllTrades(dataGrid, trades)
                                                End Sub

                contextMenu.Items.Add(deleteSelectedItem)
                contextMenu.Items.Add(New ToolStripSeparator)
                contextMenu.Items.Add(deleteAllItem)

                dataGrid.ContextMenuStrip = contextMenu

                ' Add summary panel (your existing code)
                Dim summaryPanel As New Panel
                summaryPanel.Height = 80
                summaryPanel.Dock = DockStyle.Bottom
                summaryPanel.BackColor = Color.LightGray

                ' Calculate summary statistics
                Dim totalTrades = trades.Count
                Dim winningTrades = 0
                Dim totalPnL As Decimal = 0

                For Each trade In trades
                    If trade.IsProfit Then
                        winningTrades += 1
                    End If
                    totalPnL += trade.ProfitLossUSD
                Next

                Dim losingTrades = totalTrades - winningTrades
                Dim winRate = If(totalTrades > 0, winningTrades / totalTrades * 100, 0)

                Dim summaryLabel As New Label
                summaryLabel.Text = $"Total Trades: {totalTrades} | " &
                               $"Wins: {winningTrades} | " &
                               $"Losses: {losingTrades} | " &
                               $"Win Rate: {winRate:F1}% | " &
                               $"Total P/L: ${totalPnL:F2}"
                summaryLabel.Font = New Font("Calibri", 12, FontStyle.Bold)
                summaryLabel.ForeColor = If(totalPnL >= 0, Color.DarkGreen, Color.DarkRed)
                summaryLabel.AutoSize = True
                summaryLabel.Location = New Point(10, 30)

                summaryPanel.Controls.Add(summaryLabel)

                viewForm.Controls.Add(dataGrid)
                viewForm.Controls.Add(summaryPanel)
                viewForm.Show()

                AppendColoredText(txtLogs, $"Displaying {totalTrades} trades - Right-click to delete", Color.LimeGreen)

            Else
                AppendColoredText(txtLogs, "No trades found in database", Color.Yellow)
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error viewing trades: {ex.Message}", Color.Red)
        End Try
    End Sub

    Private Sub DeleteSelectedTrades(dataGrid As DataGridView, trades As List(Of TradeRecord))
        Try
            If dataGrid.SelectedRows.Count = 0 Then
                MessageBox.Show("Please select one or more trades to delete.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            Dim selectedTradeIds As New List(Of Integer)
            For Each row As DataGridViewRow In dataGrid.SelectedRows
                If row.DataBoundItem IsNot Nothing Then
                    Dim trade = CType(row.DataBoundItem, TradeRecord)
                    selectedTradeIds.Add(trade.TradeId)
                End If
            Next

            Dim result = MessageBox.Show($"Are you sure you want to delete {selectedTradeIds.Count} selected trade(s)?",
                                   "Confirm Deletion", MessageBoxButtons.YesNo, MessageBoxIcon.Question)

            If result = DialogResult.Yes Then
                Dim deletedCount = tradeDatabase.DeleteMultipleTrades(selectedTradeIds)

                If deletedCount > 0 Then
                    AppendColoredText(txtLogs, $"Successfully deleted {deletedCount} trade(s)", Color.LimeGreen)

                    ' Refresh the data grid
                    RefreshTradeGrid(dataGrid)
                Else
                    AppendColoredText(txtLogs, "No trades were deleted", Color.Yellow)
                End If
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error deleting selected trades: {ex.Message}", Color.Red)
        End Try
    End Sub

    Private Sub DeleteAllTrades(dataGrid As DataGridView, trades As List(Of TradeRecord))
        Try
            Dim result = MessageBox.Show($"Are you sure you want to delete ALL {trades.Count} trades? This action cannot be undone!",
                                   "Confirm Delete All", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)

            If result = DialogResult.Yes Then
                Dim allTradeIds = trades.Select(Function(t) t.TradeId).ToList()
                Dim deletedCount = tradeDatabase.DeleteMultipleTrades(allTradeIds)

                If deletedCount > 0 Then
                    AppendColoredText(txtLogs, $"Successfully deleted all {deletedCount} trades", Color.LimeGreen)

                    ' Refresh the data grid
                    RefreshTradeGrid(dataGrid)
                Else
                    AppendColoredText(txtLogs, "No trades were deleted", Color.Yellow)
                End If
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error deleting all trades: {ex.Message}", Color.Red)
        End Try
    End Sub

    Private Sub RefreshTradeGrid(dataGrid As DataGridView)
        Try
            ' Get updated trade list
            Dim updatedTrades = tradeDatabase.GetAllTrades()

            ' Update the data source
            dataGrid.DataSource = updatedTrades

            ' Reapply color coding
            For Each row As DataGridViewRow In dataGrid.Rows
                If row.DataBoundItem IsNot Nothing Then
                    Dim trade = CType(row.DataBoundItem, TradeRecord)

                    If trade.IsProfit Then
                        row.Cells("ResultColumn").Value = "WIN"
                        row.DefaultCellStyle.BackColor = Color.LightGreen
                        row.DefaultCellStyle.ForeColor = Color.DarkGreen
                    Else
                        row.Cells("ResultColumn").Value = "LOSS"
                        row.DefaultCellStyle.BackColor = Color.LightCoral
                        row.DefaultCellStyle.ForeColor = Color.DarkRed
                    End If
                End If
            Next

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error refreshing trade grid: {ex.Message}", Color.Red)
        End Try
    End Sub


    Private Sub ExportTradesToCSV(trades As List(Of TradeRecord))
        Try
            Dim saveDialog As New SaveFileDialog()
            saveDialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
            saveDialog.FileName = $"TradingHistory_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            saveDialog.Title = "Export Trade History"

            If saveDialog.ShowDialog() = DialogResult.OK Then
                Using writer As New StreamWriter(saveDialog.FileName)
                    ' Write headers
                    writer.WriteLine("TradeId,DateTime,OrderType,Direction,EntryPrice,ExitPrice,OrderSizeUSD,ProfitLossUSD,Result")

                    ' Write data
                    For Each trade In trades
                        Dim result = If(trade.IsProfit, "WIN", "LOSS")
                        writer.WriteLine($"{trade.TradeId},{trade.Timestamp:yyyy-MM-dd HH:mm:ss},{trade.OrderType},{trade.Direction},{trade.EntryPrice:F2},{trade.ExitPrice:F2},{trade.OrderSizeUSD:F2},{trade.ProfitLossUSD:F2},{result}")
                    Next
                End Using

                AppendColoredText(txtLogs, $"Trade data exported to: {saveDialog.FileName}", Color.LimeGreen)

                ' Optionally open the file
                If MessageBox.Show("Export complete. Open file now?", "Export Success", MessageBoxButtons.YesNo, MessageBoxIcon.Question) = DialogResult.Yes Then
                    Process.Start(saveDialog.FileName)
                End If
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error exporting trades: {ex.Message}", Color.Red)
            MessageBox.Show($"Error exporting data: {ex.Message}", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub btnEstimateMargins_Click(sender As Object,
                                           e As EventArgs) _
                                           Handles btnEstimateMargins.Click
        Try
            '---------------------------  input validation  --------------------
            If String.IsNullOrEmpty(txtAmount.Text) OrElse
           Not IsNumeric(txtAmount.Text) Then
                AppendColoredText(txtLogs, "Please enter a valid amount", Color.Yellow)
                Return
            End If

            Dim positionSizeUSD As Decimal = Decimal.Parse(txtAmount.Text)
            Dim currentPrice As Decimal = If(TradeMode, BestBidPrice, BestAskPrice)
            If currentPrice <= 0D Then
                AppendColoredText(txtLogs, "Invalid market price for estimation", Color.Yellow)
                Return
            End If

            ' **Deribit equity = total BTC in account (lblBTCEquity)**
            Dim accountBalanceBTC As Decimal = GetEquityBTC()
            Dim accountBalanceUSD As Decimal = accountBalanceBTC * currentPrice

            '-----------------------  effective leverage  ----------------------
            Dim effectiveLeverage As Decimal =
            If(accountBalanceUSD = 0D, 0D, positionSizeUSD / accountBalanceUSD)

            '----------------  call the corrected margin routine  --------------
            Dim isShort As Boolean = Not TradeMode          ' True = short
            Dim margins = CalculateDeribitInverseLiquidationPrice(
                           positionSizeUSD,
                           effectiveLeverage,
                           currentPrice,
                           isShort)

            '--------------------  update GUI labels  --------------------------
            Me.Invoke(Sub()
                          ' Liquidation price
                          If margins("EstimatedLiquidationPrice") = 0D Then
                              lblEstimatedLiquidation.Text = "Est.Liq: N/A"
                              lblEstimatedLiquidation.ForeColor = Color.Gray
                          Else
                              lblEstimatedLiquidation.Text =
                    $"Est.Liq: ${margins("EstimatedLiquidationPrice"):F2}"
                              lblEstimatedLiquidation.ForeColor = Color.Orange
                          End If

                          ' Margins
                          lblInitialMargin.Text = $"IM: {margins("InitialMarginBTC"):F8}"
                          lblMaintenanceMargin.Text = $"MM: {margins("MaintenanceMarginBTC"):F8}"

                          ' Leverage & colour-coding
                          lblEstimatedLeverage.Text = $"Lev: {margins("EffectiveLeverage"):F1}x"
                          Select Case margins("EffectiveLeverage")
                              Case > 10 : lblEstimatedLeverage.ForeColor = Color.Red
                              Case > 5 : lblEstimatedLeverage.ForeColor = Color.Orange
                              Case Else : lblEstimatedLeverage.ForeColor = Color.LimeGreen
                          End Select
                      End Sub)

            Dim liqTxt = If(margins("EstimatedLiquidationPrice") = 0D,
                        "N/A",
                        "$" & margins("EstimatedLiquidationPrice").ToString("F2"))
            AppendColoredText(txtLogs,
                          $"Liq: {liqTxt}, Lev: {margins("EffectiveLeverage"):F1}x",
                          Color.LimeGreen)

        Catch ex As Exception
            AppendColoredText(txtLogs,
                          $"Error in Deribit inverse margin estimation: {ex.Message}",
                          Color.Red)
        End Try
    End Sub

    Private Async Sub btnRefreshLiveData_Click(sender As Object, e As EventArgs) Handles btnRefreshLiveData.Click
        Try
            ' Only refresh if we have a position
            If Decimal.Parse(txtPlacedPrice.Text) > 0 Then
                Await GetLivePositionData("BTC-PERPETUAL")
            Else
                AppendColoredText(txtLogs, "No active position to refresh", Color.Yellow)
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error refreshing live data: {ex.Message}", Color.Red)
        End Try
    End Sub

End Class

Public Class CustomLabel
    Inherits Label

    Public Sub New()
        ' Set default properties
        Me.Font = New Font("Calibri", 14, FontStyle.Regular) ' Change to your preferred font
        Me.ForeColor = Color.WhiteSmoke              ' Change to your preferred color
        Me.AutoSize = True                            ' Optional: Ensure the label resizes automatically
    End Sub
End Class
Public Class CustomTextBox
    Inherits TextBox

    Public Sub New()
        ' Set default properties
        Me.Font = New Font("Calibri", 16, FontStyle.Bold)
        Me.ForeColor = SystemColors.WindowText
        Me.BackColor = Color.WhiteSmoke
        Me.TextAlign = HorizontalAlignment.Center
    End Sub

    Protected Overrides Sub OnCreateControl()
        MyBase.OnCreateControl()
        Me.Size = New Size(200, 47) ' Enforce size
        If String.IsNullOrEmpty(Me.Text) Then
            Me.Text = "0" ' Set default text if none exists
        End If
    End Sub
End Class

