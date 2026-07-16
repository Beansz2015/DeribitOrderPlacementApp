
Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Timers
'Imports System.Windows.Forms.VisualStyles.VisualStyleElement
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports Skender
Imports Skender.Stock
Imports Skender.Stock.Indicators
'Imports Windows.Win32.Storage
'Imports Windows.Win32.System

Public Class FrmIndicators
    Private ReadOnly _host As Form          ' reference to frmMainPageV2
    Private client As ClientWebSocket
    Private Shared ohlcList As New List(Of Quote)()
    ' Environment-selected endpoint (harness spec section 1): was a live-only Const; now follows
    ' secrets.json's Environment. The host loads secrets at Load BEFORE constructing this form,
    ' so ConnectAndStream (and any reconnect) always reads the resolved environment.
    Private ReadOnly Property DeribitUrl As String
        Get
            Return AppSecrets.WsUrl
        End Get
    End Property
    Private lastTimestamp As Long
    'Private pollTimer As New Timers.Timer(60000) ' 60 000 ms = 1 minute
    Private pollTimer As New Timers.Timer(5000) ' 5-second intervals
    Private heartbeatTimer As New System.Windows.Forms.Timer() With {.Interval = 500, .Enabled = False}
    Private score As Integer = 0
    Private startupFired As Boolean = False
    ' Cross-thread fix: latest computed ATR, published for frmMainPageV2's receive-loop slippage check
    ' to read off-thread without touching lblATR. Updated wherever lblATR.Text is set.
    Private _currentATR As Decimal = 0D
    Public ReadOnly Property CurrentATR As Decimal
        Get
            Return _currentATR
        End Get
    End Property

    Public Sub New(host As Form)
        InitializeComponent()               ' designer code
        _host = host
        ' Deeper handle-race fix (owner runtime crash 2026-07-04): realize the window handle NOW, on the
        ' UI thread (New is called from the host's Load), so ConnectAndStream's background receive loop can
        ' never marshal (Me.Invoke -> UpdateSignals) before the handle exists. UiInvokeSafe additionally
        ' guards the reconnect/teardown windows where the handle can momentarily be absent.
        ' RETIREMENT (docs/spec-back-autotrade-retirement.md): this form is never Shown any more, so the
        ' handle realized here is the ONLY thing that makes the receive loop's marshals legal - do not remove.
        Dim forceHandle As IntPtr = Me.Handle
    End Sub

    ' ── Headless start ──────────────────────────────────────────────────────────
    ' RETIREMENT: the autotrade + backtest modules and this form's UI are retired (the VerdictEngine
    ' signal bridge is the sole signal source, contract R1). The form is no longer Shown, so Form.Load
    ' would never fire - the host calls this instead, right after construction. What survives is the
    ' indicator/ATR engine: the WS stream keeps filling ohlcList and UpdateSignals keeps publishing
    ' _currentATR, which frmMainPageV2's slippage guard uses as its fallback ATR source (payload atr
    ' first, then this, then the configurable fallback constant).
    Public Sub StartHeadless()
        AddHandler heartbeatTimer.Tick, AddressOf heartbeatTimer_Tick
        pollTimer.AutoReset = True
        AddHandler pollTimer.Elapsed, AddressOf OnPollElapsed

        Task.Run(AddressOf ConnectAndStream)
    End Sub


    ' ── WebSocket Connection & Subscription ────────────────────────────────────
    Private Async Sub ConnectAndStream()
        Try
            client = New ClientWebSocket()
            Await client.ConnectAsync(New Uri(DeribitUrl), CancellationToken.None)

            ' 1) Load last hour of 1-min bars
            Dim histReq = New With {
            .jsonrpc = "2.0", .id = 1,
            .method = "public/get_tradingview_chart_data",
            .params = New With {
                .instrument_name = "BTC-PERPETUAL",
                .resolution = "1",
                .start_timestamp = CLng(DateTimeOffset.UtcNow.AddHours(-72).ToUnixTimeMilliseconds()),
                .end_timestamp = CLng(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            }
        }
            Await SendJson(histReq)

            'For startup only
            If startupFired = False Then
                Dim quotes = SyncLockCopy(ohlcList)
                FireStartupSignals(quotes)
            End If

            ' 2) Subscribe to live 1-min OHLC candles
            Dim subReq = New With {
                .jsonrpc = "2.0", .id = 2,
                .method = "public/subscribe",
                .params = New With {
                    .channels = New String() {"chart.trades.BTC-PERPETUAL.1"}
                }
            }
            Await SendJson(subReq)

            ' 3) Read loop
            ' Enhanced message reading loop
            'Dim buffer(8192) As Byte 'Original buffer size

            ' Enhanced version with debugging
            Dim buffer(65535) As Byte
            Dim sb As New StringBuilder()

            While client.State = WebSocketState.Open
                Try
                    Dim res = Await client.ReceiveAsync(New ArraySegment(Of Byte)(buffer), CancellationToken.None)

                    If res.MessageType = WebSocketMessageType.Close Then Exit While

                    ' Accumulate fragments
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count))

                    ' Process only complete messages
                    If res.EndOfMessage AndAlso res.MessageType = WebSocketMessageType.Text Then
                        Dim jsonText As String = sb.ToString()
                        sb.Clear()

                        ProcessMessage(jsonText)
                    End If

                Catch ex As WebSocketException
                    AppendLog($"WebSocket error: {ex.Message}", Color.Red)
                    Exit While
                End Try
            End While



        Catch ex As Exception
            ' Handle connection errors
            AppendLog($"WebSocket error: {ex.Message}", Color.Red)
            ' Implement reconnection logic here
        End Try

    End Sub

    Private Async Function SendJson(msg As Object) As Task
        Dim json = JsonConvert.SerializeObject(msg)
        Dim bytes = Encoding.UTF8.GetBytes(json)
        Await client.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, CancellationToken.None)
    End Function

    ' ── Poll Handler (fallback) ────────────────────────────────────────────────
    Private Sub OnPollElapsed(sender As Object, e As ElapsedEventArgs)

        Try
            If client Is Nothing OrElse client.State <> WebSocketState.Open Then
                ' Attempt reconnection
                Task.Run(AddressOf ConnectAndStream)
                Return
            End If

            Dim nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            Dim req = New With {
            .jsonrpc = "2.0", .id = 3,
            .method = "public/get_tradingview_chart_data",
            .params = New With {
                .instrument_name = "BTC-PERPETUAL",
                .resolution = "1",
                .start_timestamp = lastTimestamp + 1,
                .end_timestamp = nowMs
            }
        }
            Task.Run(Async Function()
                         Try
                             Await SendJson(req)
                         Catch ex As Exception
                             AppendLog($"Polling error: {ex.Message}", Color.Red)
                         End Try
                         Return Nothing ' Explicit return for Function
                     End Function)
        Catch ex As Exception
            AppendLog($"Timer error: {ex.Message}", Color.Red)
        End Try
    End Sub

    ' Handle-race guard for background marshals. The receive loop and the poll/reconnect timer run on
    ' threadpool threads and can call this before the window handle is realized (fast restart burst) or
    ' during teardown; a raw Me.Invoke throws "handle not created" then. Drops the UI update rather than
    ' crashing when there is no live handle (the next message re-runs it once the form is up).
    Private Sub UiInvokeSafe(action As Action)
        Try
            If Me.IsHandleCreated AndAlso Not Me.IsDisposed Then Me.Invoke(action)
        Catch
            ' handle went away between the check and the invoke - drop this UI update
        End Try
    End Sub

    ' ── Message Processor ─────────────────────────────────────────────────────
    Private Sub ProcessMessage(raw As String)
        Try
            ' Add debug logging
            'AppendLog($"Received: {raw}", Color.LightGray)

            Dim msg = JObject.Parse(raw)

            ' Ignore subscribe‐confirmations (result=array)
            If msg("result") IsNot Nothing AndAlso TypeOf msg("result") Is JArray Then Return

            Dim id = msg("id")?.ToObject(Of Integer)()

            ' Handle history (id=1) or poll (id=3)
            If (id = 1 OrElse id = 3) AndAlso msg("result") IsNot Nothing Then
                Dim res = msg("result")
                Dim times = res("ticks").ToObject(Of List(Of Long))()
                Dim opens = res("open").ToObject(Of List(Of Decimal))()
                Dim highs = res("high").ToObject(Of List(Of Decimal))()
                Dim lows = res("low").ToObject(Of List(Of Decimal))()
                Dim closes = res("close").ToObject(Of List(Of Decimal))()
                Dim vols = res("volume").ToObject(Of List(Of Decimal))()

                SyncLock ohlcList
                    ' Append only new bars
                    For i = 0 To times.Count - 1
                        Dim t = times(i)
                        If t > lastTimestamp Then
                            lastTimestamp = t
                            ohlcList.Add(New Quote With {
                                .Date = DateTimeOffset.FromUnixTimeMilliseconds(t).LocalDateTime,
                                .Open = opens(i),
                                .High = highs(i),
                                .Low = lows(i),
                                .Close = closes(i),
                                .Volume = vols(i)
                            })
                            If ohlcList.Count > 4320 Then ohlcList.RemoveAt(0)
                        End If
                    Next

                    ' Start fallback polling after history loaded
                    If Not pollTimer.Enabled Then pollTimer.Start()
                End SyncLock

                Task.Run(Sub() UiInvokeSafe(Sub() UpdateSignals()))

                Return
            End If

            ' Handle live candle subscription
            If msg("method")?.ToString() = "subscription" Then
                Dim channelName = msg("params")("channel")?.ToString()

                ' Enhanced thread-safe heartbeat indication with error handling
                Try
                    Me.Invoke(Sub()
                                  redHeartBeat.BackColor = Color.Crimson
                                  heartbeatTimer.Stop()
                                  heartbeatTimer.Start()
                              End Sub)
                Catch ex As Exception
                    ' Fallback logging if UI update fails
                    System.Diagnostics.Debug.WriteLine($"Heartbeat UI update failed: {ex.Message}")
                End Try
                'End heartbeat indication

                If channelName?.StartsWith("chart.trades.BTC-PERPETUAL") Then
                    Dim data = msg("params")("data").ToObject(Of DeribitCandle)()
                    Dim barMs = data.ticks
                    Dim q = New Quote With {
                .Date = DateTimeOffset.FromUnixTimeMilliseconds(barMs).LocalDateTime,
                .Open = data.open,
                .High = data.high,
                .Low = data.low,
                .Close = data.close,
                .Volume = data.volume
            }

                    SyncLock ohlcList
                        If barMs > lastTimestamp Then
                            lastTimestamp = barMs
                            ohlcList.Add(q)
                            If ohlcList.Count > 4320 Then ohlcList.RemoveAt(0)
                        End If
                    End SyncLock

                    Task.Run(Sub() UiInvokeSafe(Sub() UpdateSignals()))
                End If
            End If
        Catch ex As JsonException
            AppendLog($"JSON parsing error: {ex.Message}", Color.Red)
        Catch ex As Exception
            AppendLog($"Message processing error: {ex.Message}", Color.Red)
        End Try
    End Sub


    Private Function SyncLockCopy(src As List(Of Quote)) As List(Of Quote)
        SyncLock src
            Return New List(Of Quote)(src)
        End SyncLock
    End Function

    ' ── Indicator Calculations & UI Update ────────────────────────────────────

    'One time startup signals
    Private Sub FireStartupSignals(quotes As IList(Of Quote))
        If quotes.Count < 14 Then Return

        ' Evaluate each indicator’s current label text
        UpdateDmi(quotes)
        UpdateMacd(quotes)
        UpdateRsi(quotes)
        UpdateStochastic(quotes) 'startupFired = True is placed inside here to stop re-execution

        startupFired = True
    End Sub


    '--------------------------------------------

    'Start of live signal calls & updates
    Private Sub UpdateSignals()
        ' RETIREMENT: the #7 isBacktesting suppression went with the backtest module; the autotrade
        ' integration point that used to sit below went with R1 (the signal bridge is the sole source).
        ' What remains is the indicator readout + the ATR publish the slippage guard falls back on.
        Dim quotes = SyncLockCopy(ohlcList)
        If quotes.Count < 14 Then Return

        UpdateDmi(quotes)
        UpdateMacd(quotes)
        UpdateRsi(quotes)
        UpdateStochastic(quotes)
        EvaluateEmaVwapSignals(quotes)
        UpdateATR(quotes)

        Dim signedBias As Decimal = (score / 21) * 100

        ' Log current score for monitoring
        'AppendLog($"Current Signal Score: {score}/21 ({signedBias:F1}%)", Color.LightBlue)

        ' The auto-trading integration point that used to sit here is GONE, along with the whole
        ' autotrade module it called (contract R1: the VerdictEngine verdict is the sole signal
        ' source; SignalBridge drives the order API now). See git history before the retirement
        ' commit if the old scoring trigger is ever needed for reference.

        If signedBias > 0 Then

            'lblOverall.ForeColor = Color.DodgerBlue
            lblScore.ForeColor = Color.DodgerBlue
            lblScore.Text = $"BUY {Math.Abs(signedBias):F1}% - ({score}/21)"
        Else
            'lblOverall.ForeColor = Color.Crimson
            lblScore.ForeColor = Color.Crimson
            lblScore.Text = $"SELL {Math.Abs(signedBias):F1}% - ({score}/21)"
        End If

        score = 0

    End Sub

    ' ── DMI Section ───────────────────────────────────────────────────────
    Private Sub UpdateDmi(quotes As IList(Of Quote))
        ' ═══════════════════════════════════════════════════════════════════
        ' DMI - Hybrid with Signal Confirmation Window
        ' ═══════════════════════════════════════════════════════════════════
        Static prevPDI As Decimal = -1, prevMDI As Decimal = -1, prevADX As Decimal = -1
        Static dmiInitialized As Boolean = False
        Static lastDMISignal As String = "-"
        Static lastDMISignalTime As DateTime = DateTime.MinValue
        Static dmiPeriodsAfterCrossover As Integer = 0

        Dim dmi = quotes.GetAdx(9).LastOrDefault()
        If dmi IsNot Nothing AndAlso dmi.Pdi.HasValue AndAlso dmi.Mdi.HasValue AndAlso dmi.Adx.HasValue Then
            Dim pdi = CDec(dmi.Pdi.Value)
            Dim mdi = CDec(dmi.Mdi.Value)
            Dim adx = CDec(dmi.Adx.Value)

            ' Initialize on first run
            If Not dmiInitialized Then
                prevPDI = pdi : prevMDI = mdi : prevADX = adx
                dmiInitialized = True
                lblDMI.Text = "INITIALIZING." : lblDMI.ForeColor = Color.Gray
                lastDMISignal = "INITIALIZING."
                Return
            End If

            Dim newDMISignal As String = lastDMISignal
            Dim newDMIColor As Color = lblDMI.ForeColor

            'Check for startup flag
            If startupFired = False Then
                If pdi > mdi AndAlso adx > 22 Then
                    ' NEW Bullish crossover with trend strength - reset confirmation window
                    dmiPeriodsAfterCrossover = 0
                    If adx > 40 Then
                        newDMISignal = "STRONG BUY - S" : newDMIColor = Color.Lime
                    ElseIf adx > 30 Then
                        newDMISignal = "BUY - S" : newDMIColor = Color.LightGreen
                    Else
                        newDMISignal = "WEAK BUY - S" : newDMIColor = Color.YellowGreen
                    End If
                    lastDMISignalTime = DateTime.Now

                ElseIf pdi < mdi AndAlso adx > 22 Then
                    ' NEW Bearish crossover with trend strength - reset confirmation window
                    dmiPeriodsAfterCrossover = 0
                    If adx > 40 Then
                        newDMISignal = "STRONG SELL - S" : newDMIColor = Color.Red
                    ElseIf adx > 30 Then
                        newDMISignal = "SELL - S" : newDMIColor = Color.Orange
                    Else
                        newDMISignal = "WEAK SELL - S" : newDMIColor = Color.Yellow
                    End If
                    lastDMISignalTime = DateTime.Now

                ElseIf lastDMISignal.Contains("BUY") AndAlso dmiPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for BUY signals
                    If adx > 40 AndAlso Not lastDMISignal.StartsWith("STRONG") Then
                        newDMISignal = "STRONG BUY - S" : newDMIColor = Color.Lime
                        AppendLog("DMI ↑ STRONG BUY : ADX inc.", Color.Lime)
                    ElseIf adx > 30 AndAlso lastDMISignal = "WEAK BUY" Then
                        newDMISignal = "BUY - S" : newDMIColor = Color.LightGreen
                        AppendLog("DMI ↑ BUY : ADX inc.", Color.Lime)
                    End If

                ElseIf lastDMISignal.Contains("SELL") AndAlso dmiPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for SELL signals
                    If adx > 40 AndAlso Not lastDMISignal.StartsWith("STRONG") Then
                        newDMISignal = "STRONG SELL - S" : newDMIColor = Color.Red
                        AppendLog("DMI ↑ STRONG SELL : ADX inc.", Color.Lime)
                    ElseIf adx > 30 AndAlso lastDMISignal = "WEAK SELL" Then
                        newDMISignal = "SELL - S" : newDMIColor = Color.Orange
                        AppendLog("DMI ↑ SELL : ADX inc.", Color.Lime)
                    End If
                End If

            Else
                ' Check for new crossover signals
                If pdi > mdi AndAlso prevPDI <= prevMDI AndAlso adx > 22 Then
                    ' NEW Bullish crossover with trend strength - reset confirmation window
                    dmiPeriodsAfterCrossover = 0
                    If adx > 40 Then
                        newDMISignal = "STRONG BUY" : newDMIColor = Color.Lime
                    ElseIf adx > 30 Then
                        newDMISignal = "BUY" : newDMIColor = Color.LightGreen
                    Else
                        newDMISignal = "WEAK BUY" : newDMIColor = Color.YellowGreen
                    End If
                    lastDMISignalTime = DateTime.Now

                ElseIf pdi < mdi AndAlso prevPDI >= prevMDI AndAlso adx > 22 Then
                    ' NEW Bearish crossover with trend strength - reset confirmation window
                    dmiPeriodsAfterCrossover = 0
                    If adx > 40 Then
                        newDMISignal = "STRONG SELL" : newDMIColor = Color.Red
                    ElseIf adx > 30 Then
                        newDMISignal = "SELL" : newDMIColor = Color.Orange
                    Else
                        newDMISignal = "WEAK SELL" : newDMIColor = Color.Yellow
                    End If
                    lastDMISignalTime = DateTime.Now

                ElseIf lastDMISignal.Contains("BUY") AndAlso dmiPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for BUY signals
                    If adx > 40 AndAlso Not lastDMISignal.StartsWith("STRONG") Then
                        newDMISignal = "STRONG BUY" : newDMIColor = Color.Lime
                        AppendLog("DMI ↑ STRONG BUY : ADX inc.", Color.Lime)
                    ElseIf adx > 30 AndAlso lastDMISignal = "WEAK BUY" Then
                        newDMISignal = "BUY" : newDMIColor = Color.LightGreen
                        AppendLog("DMI ↑ BUY : ADX inc.", Color.Lime)
                    End If

                ElseIf lastDMISignal.Contains("SELL") AndAlso dmiPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for SELL signals
                    If adx > 40 AndAlso Not lastDMISignal.StartsWith("STRONG") Then
                        newDMISignal = "STRONG SELL" : newDMIColor = Color.Red
                        AppendLog("DMI ↑ STRONG SELL : ADX inc.", Color.Lime)
                    ElseIf adx > 30 AndAlso lastDMISignal = "WEAK SELL" Then
                        newDMISignal = "SELL" : newDMIColor = Color.Orange
                        AppendLog("DMI ↑ SELL : ADX inc.", Color.Lime)
                    End If
                End If
            End If

            ' Update display only if signal changed
            If newDMISignal <> lastDMISignal Then
                lblDMI.Text = newDMISignal
                lblDMI.ForeColor = newDMIColor
                lastDMISignal = newDMISignal
                AppendLog($"DMI: {newDMISignal} (PDI:{pdi:F1}, MDI:{mdi:F1}, ADX:{adx:F1})", Color.Yellow)
            End If

            'To calculate leaning to bias indicator strength:
            Select Case newDMISignal
                Case "WEAK BUY" : score += 1
                Case "BUY" : score += 2
                Case "STRONG BUY" : score += 3
                Case "WEAK SELL" : score -= 1
                Case "SELL" : score -= 2
                Case "STRONG SELL" : score -= 3
            End Select

            ' Increment confirmation window counter
            dmiPeriodsAfterCrossover += 1
            prevPDI = pdi : prevMDI = mdi : prevADX = adx
        End If
    End Sub

    ' ── MACD Section ──────────────────────────────────────────────────────
    Private Sub UpdateMacd(quotes As IList(Of Quote))
        '═══════════════════════════════════════════════════════════════════
        ' MACD - Hybrid with Signal Confirmation Window
        '═══════════════════════════════════════════════════════════════════
        Static prevHistogram As Decimal = 0, prevMacdLine As Decimal = 0, prevSignalLine As Decimal = 0
        Static macdInitialized As Boolean = False
        Static lastMACDSignal As String = "-"
        Static lastMACDSignalTime As DateTime = DateTime.MinValue
        Static macdPeriodsAfterCrossover As Integer = 0

        Dim macd = quotes.GetMacd(6, 13, 5).LastOrDefault()
        If macd IsNot Nothing AndAlso macd.Histogram.HasValue AndAlso macd.Macd.HasValue AndAlso macd.Signal.HasValue Then
            Dim histogram = CDec(macd.Histogram.Value)
            Dim macdLine = CDec(macd.Macd.Value)
            Dim signalLine = CDec(macd.Signal.Value)

            ' Initialize on first run
            If Not macdInitialized Then
                prevHistogram = histogram : prevMacdLine = macdLine : prevSignalLine = signalLine
                macdInitialized = True
                lblMACD.Text = "INITIALIZING." : lblMACD.ForeColor = Color.Gray
                lastMACDSignal = "INITIALIZING."
                Return
            End If

            Dim newMACDSignal As String = lastMACDSignal
            Dim newMACDColor As Color = lblMACD.ForeColor

            'Check for startup flag
            If startupFired = False Then
                If macdLine > signalLine Then
                    ' NEW Bullish crossover - reset confirmation window
                    macdPeriodsAfterCrossover = 0
                    If histogram > 10 Then
                        newMACDSignal = "STRONG BUY - S" : newMACDColor = Color.Lime
                    ElseIf histogram > 3 Then
                        newMACDSignal = "BUY - S" : newMACDColor = Color.LightGreen
                    ElseIf histogram > 0.5 Then
                        newMACDSignal = "WEAK BUY - S" : newMACDColor = Color.YellowGreen
                    End If
                    lastMACDSignalTime = DateTime.Now

                ElseIf macdLine < signalLine Then
                    ' NEW Bearish crossover - reset confirmation window
                    macdPeriodsAfterCrossover = 0
                    If histogram < -10 Then
                        newMACDSignal = "STRONG SELL - S" : newMACDColor = Color.Red
                    ElseIf histogram < -3 Then
                        newMACDSignal = "SELL - S" : newMACDColor = Color.Orange
                    ElseIf histogram < -0.5 Then
                        newMACDSignal = "WEAK SELL - S" : newMACDColor = Color.Yellow
                    End If
                    lastMACDSignalTime = DateTime.Now

                ElseIf lastMACDSignal.Contains("BUY") AndAlso macdPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for BUY signals
                    If histogram > 10 AndAlso Not lastMACDSignal.StartsWith("STRONG") Then
                        newMACDSignal = "STRONG BUY - S" : newMACDColor = Color.Lime
                        AppendLog("MACD ↑ STRONG BUY : Hist. inc.", Color.Lime)
                    ElseIf histogram > 3 AndAlso lastMACDSignal = "WEAK BUY" Then
                        newMACDSignal = "BUY - S" : newMACDColor = Color.LightGreen
                        AppendLog("MACD ↑ BUY : Hist. inc.", Color.Lime)
                    End If

                ElseIf lastMACDSignal.Contains("SELL") AndAlso macdPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for SELL signals
                    If histogram < -10 AndAlso Not lastMACDSignal.StartsWith("STRONG") Then
                        newMACDSignal = "STRONG SELL - S" : newMACDColor = Color.Red
                        AppendLog("MACD ↑ STRONG SELL : Hist. dec.", Color.Lime)
                    ElseIf histogram < -3 AndAlso lastMACDSignal = "WEAK SELL" Then
                        newMACDSignal = "SELL - S" : newMACDColor = Color.Orange
                        AppendLog("MACD ↑ SELL : Hist. dec.", Color.Lime)
                    End If
                End If

            Else
                ' Check for new crossover signals
                If macdLine > signalLine AndAlso prevMacdLine <= prevSignalLine Then
                    ' NEW Bullish crossover - reset confirmation window
                    macdPeriodsAfterCrossover = 0
                    If histogram > 10 Then
                        newMACDSignal = "STRONG BUY" : newMACDColor = Color.Lime
                    ElseIf histogram > 3 Then
                        newMACDSignal = "BUY" : newMACDColor = Color.LightGreen
                    ElseIf histogram > 0.5 Then
                        newMACDSignal = "WEAK BUY" : newMACDColor = Color.YellowGreen
                    End If
                    lastMACDSignalTime = DateTime.Now

                ElseIf macdLine < signalLine AndAlso prevMacdLine >= prevSignalLine Then
                    ' NEW Bearish crossover - reset confirmation window
                    macdPeriodsAfterCrossover = 0
                    If histogram < -10 Then
                        newMACDSignal = "STRONG SELL" : newMACDColor = Color.Red
                    ElseIf histogram < -3 Then
                        newMACDSignal = "SELL" : newMACDColor = Color.Orange
                    ElseIf histogram < -0.5 Then
                        newMACDSignal = "WEAK SELL" : newMACDColor = Color.Yellow
                    End If
                    lastMACDSignalTime = DateTime.Now

                ElseIf lastMACDSignal.Contains("BUY") AndAlso macdPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for BUY signals
                    If histogram > 10 AndAlso Not lastMACDSignal.StartsWith("STRONG") Then
                        newMACDSignal = "STRONG BUY" : newMACDColor = Color.Lime
                        AppendLog("MACD ↑ STRONG BUY : Hist. inc.", Color.Lime)
                    ElseIf histogram > 3 AndAlso lastMACDSignal = "WEAK BUY" Then
                        newMACDSignal = "BUY" : newMACDColor = Color.LightGreen
                        AppendLog("MACD ↑ BUY : Hist. inc.", Color.Lime)
                    End If

                ElseIf lastMACDSignal.Contains("SELL") AndAlso macdPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for SELL signals
                    If histogram < -10 AndAlso Not lastMACDSignal.StartsWith("STRONG") Then
                        newMACDSignal = "STRONG SELL" : newMACDColor = Color.Red
                        AppendLog("MACD ↑ STRONG SELL : Hist. dec.", Color.Lime)
                    ElseIf histogram < -3 AndAlso lastMACDSignal = "WEAK SELL" Then
                        newMACDSignal = "SELL" : newMACDColor = Color.Orange
                        AppendLog("MACD ↑ SELL : Hist. dec.", Color.Lime)
                    End If
                End If
            End If
            ' ── EMA Confirmation by Signal Text Matching ──────────────────────
            Dim currentEmaSignal As String = lblEMA.Text

            If newMACDSignal.Contains("BUY") AndAlso currentEmaSignal.Contains("BUY") Then
                If Not newMACDSignal.Contains(" - EMA OK") Then
                    newMACDSignal &= " - EMA OK"
                    'newMACDColor = Color.Cyan
                End If
            ElseIf newMACDSignal.Contains("SELL") AndAlso currentEmaSignal.Contains("SELL") Then
                If Not newMACDSignal.Contains(" - EMA OK") Then
                    newMACDSignal &= " - EMA OK"
                    'newMACDColor = Color.Cyan
                End If
            End If

            ' Only update if signal changed
            If newMACDSignal <> lastMACDSignal Then
                lblMACD.Text = newMACDSignal
                lblMACD.ForeColor = newMACDColor
                lastMACDSignal = newMACDSignal
                AppendLog($"MACD: {newMACDSignal} (MACD:{macdLine:F3}, Signal:{signalLine:F3}, Hist:{histogram:F3})", Color.Yellow)
            End If

            'To calculate leaning to bias indicator strength:
            If newMACDSignal.Contains("WEAK BUY") Then
                score += 1
            ElseIf newMACDSignal.Contains("BUY") Then
                score += 2
            ElseIf newMACDSignal.Contains("STRONG BUY") Then
                score += 3
            ElseIf newMACDSignal.Contains("WEAK SELL") Then
                score -= 1
            ElseIf newMACDSignal.Contains("SELL") Then
                score -= 2
            ElseIf newMACDSignal.Contains("STRONG SELL") Then
                score -= 3
            End If

            If newMACDSignal.Contains("BUY") AndAlso newMACDSignal.Contains("EMA OK") Then
                score += 1
            ElseIf newMACDSignal.Contains("SELL") AndAlso newMACDSignal.Contains("EMA OK") Then
                score -= 1
            End If

            ' Increment confirmation window counter
            macdPeriodsAfterCrossover += 1
            prevHistogram = histogram : prevMacdLine = macdLine : prevSignalLine = signalLine
        End If
    End Sub

    Private currentRsiValue As Decimal = 0 ' Global variable to hold RSI value  
    ' ── RSI Section ───────────────────────────────────────────────────────
    Private Sub UpdateRsi(quotes As IList(Of Quote))
        '═══════════════════════════════════════════════════════════════════
        ' RSI - Hybrid with Signal Confirmation Window
        '═══════════════════════════════════════════════════════════════════
        Static prevRSI As Decimal = -1, prevPrice As Decimal = -1
        Static rsiInitialized As Boolean = False
        Static lastRSISignal As String = "-"
        Static lastRSISignalTime As DateTime = DateTime.MinValue
        Static rsiPeriodsAfterCrossover As Integer = 0

        Dim rsi = quotes.GetRsi(9).LastOrDefault()
        Dim currentPrice = quotes.Last().Close

        If rsi IsNot Nothing AndAlso rsi.Rsi.HasValue Then
            Dim rsiValue = CDec(rsi.Rsi.Value)
            currentRsiValue = rsiValue

            ' Initialize on first run
            If Not rsiInitialized Then
                prevRSI = rsiValue : prevPrice = currentPrice
                rsiInitialized = True
                lblRSI.Text = "INITIALIZING." : lblRSI.ForeColor = Color.Gray
                lastRSISignal = "INITIALIZING."
                Return
            End If

            Dim newRSISignal As String = lastRSISignal
            Dim newRSIColor As Color = lblRSI.ForeColor

            'check for startup flag
            If startupFired = False Then
                If rsiValue < 30 Then
                    ' NEW Oversold entry - reset confirmation window
                    rsiPeriodsAfterCrossover = 0
                    If rsiValue < 15 Then
                        newRSISignal = "STRONG BUY - S" : newRSIColor = Color.Lime
                    ElseIf rsiValue < 25 Then
                        newRSISignal = "BUY - S" : newRSIColor = Color.LightGreen
                    Else
                        newRSISignal = "WEAK BUY - S" : newRSIColor = Color.YellowGreen
                    End If
                    lastRSISignalTime = DateTime.Now

                ElseIf rsiValue > 70 Then
                    ' NEW Overbought entry - reset confirmation window
                    rsiPeriodsAfterCrossover = 0
                    If rsiValue > 85 Then
                        newRSISignal = "STRONG SELL - S" : newRSIColor = Color.Red
                    ElseIf rsiValue > 75 Then
                        newRSISignal = "SELL - S" : newRSIColor = Color.Orange
                    Else
                        newRSISignal = "WEAK SELL - S" : newRSIColor = Color.Yellow
                    End If
                    lastRSISignalTime = DateTime.Now

                ElseIf rsiValue > 50 Then
                    ' NEW Momentum confirmation - reset confirmation window
                    rsiPeriodsAfterCrossover = 0
                    newRSISignal = "MOMENTUM BUY - S" : newRSIColor = Color.CornflowerBlue
                    lastRSISignalTime = DateTime.Now

                ElseIf rsiValue < 50 Then
                    ' NEW Momentum confirmation - reset confirmation window  
                    rsiPeriodsAfterCrossover = 0
                    newRSISignal = "MOMENTUM SELL - S" : newRSIColor = Color.Coral
                    lastRSISignalTime = DateTime.Now

                ElseIf lastRSISignal.Contains("BUY") AndAlso Not lastRSISignal.Contains("MOMENTUM") AndAlso rsiPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for BUY signals
                    If rsiValue < 15 AndAlso Not lastRSISignal.StartsWith("STRONG") Then
                        newRSISignal = "STRONG BUY - S" : newRSIColor = Color.Lime
                        AppendLog("RSI ↑ STRONG BUY : Deeper oversold", Color.Lime)
                    ElseIf rsiValue < 25 AndAlso lastRSISignal = "WEAK BUY" Then
                        newRSISignal = "BUY" : newRSIColor = Color.LightGreen
                        AppendLog("RSI ↑ BUY : Deeper oversold", Color.Lime)
                    End If

                ElseIf lastRSISignal.Contains("SELL") AndAlso Not lastRSISignal.Contains("MOMENTUM") AndAlso rsiPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for SELL signals
                    If rsiValue > 85 AndAlso Not lastRSISignal.StartsWith("STRONG") Then
                        newRSISignal = "STRONG SELL - S" : newRSIColor = Color.Red
                        AppendLog("RSI ↑ STRONG SELL : Deeper overbought", Color.Lime)
                    ElseIf rsiValue > 75 AndAlso lastRSISignal = "WEAK SELL" Then
                        newRSISignal = "SELL" : newRSIColor = Color.Orange
                        AppendLog("RSI ↑ SELL : Deeper overbought", Color.Lime)
                    End If
                End If

            Else
                ' Check for new zone breach signals
                If rsiValue < 30 AndAlso prevRSI >= 30 Then
                    ' NEW Oversold entry - reset confirmation window
                    rsiPeriodsAfterCrossover = 0
                    If rsiValue < 15 Then
                        newRSISignal = "STRONG BUY" : newRSIColor = Color.Lime
                    ElseIf rsiValue < 25 Then
                        newRSISignal = "BUY" : newRSIColor = Color.LightGreen
                    Else
                        newRSISignal = "WEAK BUY" : newRSIColor = Color.YellowGreen
                    End If
                    lastRSISignalTime = DateTime.Now

                ElseIf rsiValue > 70 AndAlso prevRSI <= 70 Then
                    ' NEW Overbought entry - reset confirmation window
                    rsiPeriodsAfterCrossover = 0
                    If rsiValue > 85 Then
                        newRSISignal = "STRONG SELL" : newRSIColor = Color.Red
                    ElseIf rsiValue > 75 Then
                        newRSISignal = "SELL" : newRSIColor = Color.Orange
                    Else
                        newRSISignal = "WEAK SELL" : newRSIColor = Color.Yellow
                    End If
                    lastRSISignalTime = DateTime.Now

                ElseIf rsiValue > 50 AndAlso prevRSI <= 50 AndAlso currentPrice > prevPrice Then
                    ' NEW Momentum confirmation - reset confirmation window
                    rsiPeriodsAfterCrossover = 0
                    newRSISignal = "MOMENTUM BUY" : newRSIColor = Color.CornflowerBlue
                    lastRSISignalTime = DateTime.Now

                ElseIf rsiValue < 50 AndAlso prevRSI >= 50 AndAlso currentPrice < prevPrice Then
                    ' NEW Momentum confirmation - reset confirmation window  
                    rsiPeriodsAfterCrossover = 0
                    newRSISignal = "MOMENTUM SELL" : newRSIColor = Color.Coral
                    lastRSISignalTime = DateTime.Now

                ElseIf lastRSISignal.Contains("BUY") AndAlso Not lastRSISignal.Contains("MOMENTUM") AndAlso rsiPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for BUY signals
                    If rsiValue < 15 AndAlso Not lastRSISignal.StartsWith("STRONG") Then
                        newRSISignal = "STRONG BUY" : newRSIColor = Color.Lime
                        AppendLog("RSI ↑ STRONG BUY : Deeper oversold", Color.Lime)
                    ElseIf rsiValue < 25 AndAlso lastRSISignal = "WEAK BUY" Then
                        newRSISignal = "BUY" : newRSIColor = Color.LightGreen
                        AppendLog("RSI ↑ BUY : Deeper oversold", Color.Lime)
                    End If

                ElseIf lastRSISignal.Contains("SELL") AndAlso Not lastRSISignal.Contains("MOMENTUM") AndAlso rsiPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for SELL signals
                    If rsiValue > 85 AndAlso Not lastRSISignal.StartsWith("STRONG") Then
                        newRSISignal = "STRONG SELL" : newRSIColor = Color.Red
                        AppendLog("RSI ↑ STRONG SELL : Deeper overbought", Color.Lime)
                    ElseIf rsiValue > 75 AndAlso lastRSISignal = "WEAK SELL" Then
                        newRSISignal = "SELL" : newRSIColor = Color.Orange
                        AppendLog("RSI ↑ SELL : Deeper overbought", Color.Lime)
                    End If
                End If
            End If

            ' ── Stoch Confirmation by Signal Text Matching ──────────────────────
            Dim currentStochSignal As String = lblStoch.Text

            If newRSISignal.Contains("BUY") AndAlso currentStochSignal.Contains("BUY") AndAlso
               rsiValue < 65 AndAlso stochD < 40 Then
                If Not newRSISignal.Contains(" - STOCH OK") Then
                    newRSISignal &= " - STOCH OK"
                    'rsiColor = Color.Cyan
                End If

            ElseIf newRSISignal.Contains("SELL") AndAlso currentStochSignal.Contains("SELL") AndAlso
                rsiValue > 35 AndAlso stochD > 60 Then
                If Not newRSISignal.Contains(" - STOCH OK") Then
                    newRSISignal &= " - STOCH OK"
                    'rsiColor = Color.Cyan
                End If
            End If

            ' Only update if signal changed
            If newRSISignal <> lastRSISignal Then
                lblRSI.Text = newRSISignal
                lblRSI.ForeColor = newRSIColor
                lastRSISignal = newRSISignal
                AppendLog($"RSI: {newRSISignal} (RSI:{rsiValue:F1}, Price:{currentPrice:F2})", Color.Yellow)
            End If

            'To calculate leaning to bias indicator strength:
            If newRSISignal.Contains("WEAK BUY") Then
                score += 1
            ElseIf newRSISignal.Contains("BUY") Then
                score += 2
            ElseIf newRSISignal.Contains("MOMENTUM BUY") Then
                score += 2
            ElseIf newRSISignal.Contains("STRONG BUY") Then
                score += 3
            ElseIf newRSISignal.Contains("WEAK SELL") Then
                score -= 1
            ElseIf newRSISignal.Contains("SELL") Then
                score -= 2
            ElseIf newRSISignal.Contains("MOMENTUM SELL") Then
                score -= 2
            ElseIf newRSISignal.Contains("STRONG SELL") Then
                score -= 3
            End If

            If newRSISignal.Contains("BUY") AndAlso newRSISignal.Contains("STOCH OK") Then
                score += 1
            ElseIf newRSISignal.Contains("SELL") AndAlso newRSISignal.Contains("STOCH OK") Then
                score -= 1
            End If

            ' Increment confirmation window counter
            rsiPeriodsAfterCrossover += 1
            prevRSI = rsiValue : prevPrice = currentPrice
        End If
    End Sub

    Private stochD As Decimal = 0 ' Global variable to hold Stochastic D value

    ' ── Stochastic Section ────────────────────────────────────────────────
    Private Sub UpdateStochastic(quotes As IList(Of Quote))
        '═══════════════════════════════════════════════════════════════════
        ' STOCHASTIC - Hybrid with Signal Confirmation Window
        '═══════════════════════════════════════════════════════════════════
        Static prevK As Decimal = -1, prevD As Decimal = -1
        Static initialized As Boolean = False
        Static lastSignal As String = "-"
        Static lastSignalTime As DateTime = DateTime.MinValue
        Static stochPeriodsAfterCrossover As Integer = 0

        If quotes.Count() < 15 Then
            lblStoch.Text = "INSUFFICIENT DATA"
            lblStoch.ForeColor = Color.Gray
            Return
        End If

        Dim st = quotes.GetStoch(8, 3, 3).LastOrDefault()
        If st IsNot Nothing AndAlso st.Oscillator.HasValue AndAlso st.Signal.HasValue Then
            Dim k = CDec(st.Oscillator.Value)
            Dim d = CDec(st.Signal.Value)
            stochD = d ' Store D value globally for use in RSI confirmation

            ' Initialize on first run
            If Not initialized Then
                prevK = k : prevD = d : initialized = True
                lblStoch.Text = "INITIALIZING." : lblStoch.ForeColor = Color.Gray
                lastSignal = "INITIALIZING."
                Return
            End If

            Dim newSignal As String = lastSignal
            Dim newColor As Color = lblStoch.ForeColor

            'check for startup flag
            If startupFired = False Then
                If k > d Then
                    ' NEW Bullish crossover - reset confirmation window
                    stochPeriodsAfterCrossover = 0
                    If d < 25 Then
                        newSignal = "STRONG BUY - S" : newColor = Color.Lime
                    ElseIf d < 40 Then
                        newSignal = "BUY - S" : newColor = Color.LightGreen
                    Else
                        newSignal = "WEAK BUY - S" : newColor = Color.YellowGreen
                    End If
                    lastSignalTime = DateTime.Now

                ElseIf k < d Then
                    ' NEW Bearish crossover - reset confirmation window
                    stochPeriodsAfterCrossover = 0
                    If d > 75 Then
                        newSignal = "STRONG SELL - S" : newColor = Color.Red
                    ElseIf d > 60 Then
                        newSignal = "SELL - S" : newColor = Color.Orange
                    Else
                        newSignal = "WEAK SELL - S" : newColor = Color.Yellow
                    End If
                    lastSignalTime = DateTime.Now

                ElseIf lastSignal.Contains("BUY") AndAlso stochPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for BUY signals
                    If d < 25 AndAlso Not lastSignal.StartsWith("STRONG") Then
                        newSignal = "STRONG BUY - S" : newColor = Color.Lime
                        AppendLog($"STOCH. ↑ STRONG BUY", Color.Lime)
                    ElseIf d < 40 AndAlso lastSignal = "WEAK BUY" Then
                        newSignal = "BUY - S" : newColor = Color.LightGreen
                        AppendLog("STOCH. ↑ BUY", Color.Lime)
                    End If

                ElseIf lastSignal.Contains("SELL") AndAlso stochPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for SELL signals
                    If d > 75 AndAlso Not lastSignal.StartsWith("STRONG") Then
                        newSignal = "STRONG SELL - S" : newColor = Color.Red
                        AppendLog("STOCH. ↑ STRONG SELL", Color.Lime)
                    ElseIf d > 60 AndAlso lastSignal = "WEAK SELL" Then
                        newSignal = "SELL - S" : newColor = Color.Orange
                        AppendLog("STOCH. ↑ SELL", Color.Lime)
                    End If
                End If
                startupFired = True
            Else
                ' Check for new crossover signals
                If k > d AndAlso prevK <= prevD Then
                    ' NEW Bullish crossover - reset confirmation window
                    stochPeriodsAfterCrossover = 0
                    If d < 25 Then
                        newSignal = "STRONG BUY" : newColor = Color.Lime
                    ElseIf d < 40 Then
                        newSignal = "BUY" : newColor = Color.LightGreen
                    Else
                        newSignal = "WEAK BUY" : newColor = Color.YellowGreen
                    End If
                    lastSignalTime = DateTime.Now

                ElseIf k < d AndAlso prevK >= prevD Then
                    ' NEW Bearish crossover - reset confirmation window
                    stochPeriodsAfterCrossover = 0
                    If d > 75 Then
                        newSignal = "STRONG SELL" : newColor = Color.Red
                    ElseIf d > 60 Then
                        newSignal = "SELL" : newColor = Color.Orange
                    Else
                        newSignal = "WEAK SELL" : newColor = Color.Yellow
                    End If
                    lastSignalTime = DateTime.Now

                ElseIf lastSignal.Contains("BUY") AndAlso stochPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for BUY signals
                    If d < 25 AndAlso Not lastSignal.StartsWith("STRONG") Then
                        newSignal = "STRONG BUY" : newColor = Color.Lime
                        AppendLog($"STOCH. ↑ STRONG BUY", Color.Lime)
                    ElseIf d < 40 AndAlso lastSignal = "WEAK BUY" Then
                        newSignal = "BUY" : newColor = Color.LightGreen
                        AppendLog("STOCH. ↑ BUY", Color.Lime)
                    End If

                ElseIf lastSignal.Contains("SELL") AndAlso stochPeriodsAfterCrossover <= 3 Then
                    ' Allow strength UPGRADES within confirmation window for SELL signals
                    If d > 75 AndAlso Not lastSignal.StartsWith("STRONG") Then
                        newSignal = "STRONG SELL" : newColor = Color.Red
                        AppendLog("STOCH. ↑ STRONG SELL", Color.Lime)
                    ElseIf d > 60 AndAlso lastSignal = "WEAK SELL" Then
                        newSignal = "SELL" : newColor = Color.Orange
                        AppendLog("STOCH. ↑ SELL", Color.Lime)
                    End If
                End If
            End If

            Dim currentRsiSignal As String = lblRSI.Text

            If newSignal.Contains("BUY") AndAlso currentRsiSignal.Contains("BUY") AndAlso
    currentRsiValue < 65 AndAlso d < 40 Then
                If Not newSignal.Contains(" - RSI OK") Then
                    newSignal &= " - RSI OK"
                    'stochColor = Color.Cyan
                End If
            ElseIf newSignal.Contains("SELL") AndAlso currentRsiSignal.Contains("SELL") AndAlso
            currentRsiValue > 35 AndAlso d > 60 Then
                If Not newSignal.Contains(" - RSI OK") Then
                    newSignal &= " - RSI OK"
                    'stochColor = Color.Cyan
                End If
            End If
            ' Only update if signal actually changed
            If newSignal <> lastSignal Then
                lblStoch.Text = newSignal
                lblStoch.ForeColor = newColor
                lastSignal = newSignal
                AppendLog($"Stoch: {newSignal} (K:{k:F1}, D:{d:F1})", Color.Yellow)
            End If

            'To calculate leaning to bias indicator strength:
            If newSignal.Contains("WEAK BUY") Then
                score += 1
            ElseIf newSignal.Contains("BUY") Then
                score += 2
            ElseIf newSignal.Contains("STRONG BUY") Then
                score += 3
            ElseIf newSignal.Contains("WEAK SELL") Then
                score -= 1
            ElseIf newSignal.Contains("SELL") Then
                score -= 2
            ElseIf newSignal.Contains("STRONG SELL") Then
                score -= 3
            End If

            If newSignal.Contains("BUY") AndAlso newSignal.Contains("RSI OK") Then
                score += 1
            ElseIf newSignal.Contains("SELL") AndAlso newSignal.Contains("RSI OK") Then
                score -= 1
            End If

            ' Increment confirmation window counter
            stochPeriodsAfterCrossover += 1
            prevK = k : prevD = d
        End If
    End Sub

    '========================================================================
    '  Evaluate 9-21-50 EMA + Daily VWAP with 3-bar strength upgrades & EMA50 filter
    '========================================================================
    Private Sub EvaluateEmaVwapSignals(quotes As IList(Of Quote))
        If quotes Is Nothing OrElse quotes.Count < 60 Then Exit Sub

        Static lastSignal As String = "-"
        Static lastColor As Color = Color.Gray
        Static signalAge As Integer = 0

        ' Get indicator values
        Dim e9 = quotes.GetEma(9).LastOrDefault()?.Ema
        Dim e21 = quotes.GetEma(21).LastOrDefault()?.Ema
        Dim e50 = quotes.GetEma(50).LastOrDefault()?.Ema
        Dim vwap = quotes.GetVwap().LastOrDefault()?.Vwap
        If e9 Is Nothing OrElse e21 Is Nothing OrElse e50 Is Nothing OrElse vwap Is Nothing Then Exit Sub

        Dim ema9 = e9.Value
        Dim ema21v = e21.Value
        Dim ema50 = e50.Value
        Dim vvw = vwap.Value
        Dim price = quotes.Last().Close

        ' Only proceed if price and EMA9/21 are on the same side of EMA50
        Dim bullishTrend = (price > ema50 AndAlso ema9 > ema21v)
        Dim bearishTrend = (price < ema50 AndAlso ema9 < ema21v)
        If Not (bullishTrend OrElse bearishTrend) Then
            ' Show neutral if outside trend
            If Me.InvokeRequired Then
                Me.Invoke(Sub()
                              lblEMA.Text = "EMA: Neutral Trend"
                              lblEMA.ForeColor = Color.Gray
                              lblVWAP.Text = "VWAP: —"
                              lblVWAP.ForeColor = Color.Gray
                          End Sub)
            Else
                lblEMA.Text = "EMA: Neutral Trend"
                lblEMA.ForeColor = Color.Gray
                lblVWAP.Text = "VWAP: —"
                lblVWAP.ForeColor = Color.Gray
            End If
            Return
        End If

        ' Pre-calc distance bps
        Dim bps As Decimal = Math.Abs((price - ema9) / price) * 10000D

        ' 1) Base EMA9/21 signal
        Dim sig As String = "-"
        Dim col As Color = Color.Gray
        If bullishTrend Then
            Select Case bps
                Case < 10D : sig = "WEAK BUY"
                    col = Color.YellowGreen
                Case < 25D : sig = "BUY"
                    col = Color.LightGreen
                Case Else : sig = "STRONG BUY"
                    col = Color.Lime
            End Select
        ElseIf bearishTrend Then
            Select Case bps
                Case < 10D : sig = "WEAK SELL"
                    col = Color.Yellow
                Case < 25D : sig = "SELL"
                    col = Color.Orange
                Case Else : sig = "STRONG SELL"
                    col = Color.Red
            End Select
        End If

        ' 2) VWAP bias
        Dim bias As String = "-"
        Dim biasCol As Color = Color.Gray
        Dim pctVwap As Decimal = (price - vvw) / vvw * 100D
        If pctVwap > 0D Then
            bias = "BUY BIAS"
            biasCol = Color.LightGreen
        ElseIf pctVwap < 0D Then
            bias = "SELL BIAS"
            biasCol = Color.Orange
        End If

        ' 3) Confirmation
        Dim confirmed = (sig.Contains("BUY") AndAlso bias.Contains("BUY")) _
                 Or (sig.Contains("SELL") AndAlso bias.Contains("SELL"))
        If confirmed AndAlso sig <> "-" Then
            sig &= " – VWAP OK"
            bias &= " – EMA OK"
            col = Color.Cyan
            biasCol = Color.Cyan
        End If

        ' 4) 3-bar confirmation upgrades
        If sig <> lastSignal Then
            lastSignal = sig
            lastColor = col
            signalAge = 0
        ElseIf signalAge < 3 Then
            ' allow deeper-strength upgrades
            If sig.Contains("BUY") Then
                If sig.StartsWith("WEAK") AndAlso bps >= 10D Then
                    sig = If(confirmed, "BUY – VWAP OK", "BUY")
                    col = Color.LightGreen
                ElseIf bps >= 25D Then
                    sig = If(confirmed, "STRONG BUY – VWAP OK", "STRONG BUY")
                    col = Color.Lime
                End If
            ElseIf sig.Contains("SELL") Then
                If sig.StartsWith("WEAK") AndAlso bps >= 10D Then
                    sig = If(confirmed, "SELL – VWAP OK", "SELL")
                    col = Color.Orange
                ElseIf bps >= 25D Then
                    sig = If(confirmed, "STRONG SELL – VWAP OK", "STRONG SELL")
                    col = Color.Red
                End If
            End If
            lastSignal = sig
            lastColor = col
            signalAge += 1
        End If

        'To calculate EMA - leaning to bias indicator strength:
        If sig.Contains("WEAK BUY") Then
            score += 1
        ElseIf sig.Contains("BUY") Then
            score += 2
        ElseIf sig.Contains("STRONG BUY") Then
            score += 3
        ElseIf sig.Contains("WEAK SELL") Then
            score -= 1
        ElseIf sig.Contains("SELL") Then
            score -= 2
        ElseIf sig.Contains("STRONG SELL") Then
            score -= 3
        End If

        If sig.Contains("BUY") AndAlso sig.Contains("VWAP OK") Then
            score += 1
        ElseIf sig.Contains("SELL") AndAlso sig.Contains("VWAP OK") Then
            score -= 1
        End If

        'To calculate VWAP - leaning to bias indicator strength:
        If bias.Contains("BUY BIAS") Then
            score += 1
        ElseIf bias.Contains("SELL BIAS") Then
            score -= 1
        End If

        If bias.Contains("BUY") AndAlso bias.Contains("EMA OK") Then
            score += 1
        ElseIf bias.Contains("SELL") AndAlso bias.Contains("EMA OK") Then
            score -= 1
        End If

        ' 5) UI update
        If Me.InvokeRequired Then
            Me.Invoke(Sub()
                          lblEMA.Text = sig
                          lblEMA.ForeColor = col
                          lblVWAP.Text = bias
                          lblVWAP.ForeColor = biasCol
                      End Sub)
        Else
            lblEMA.Text = sig
            lblEMA.ForeColor = col
            lblVWAP.Text = bias
            lblVWAP.ForeColor = biasCol
        End If

    End Sub

    Private Sub UpdateATR(quotes As IList(Of Quote))
        ' ═══════════════════════════════════════════════════════════════════
        'ATR
        ' ═══════════════════════════════════════════════════════════════════
        ' Compute 7‐period ATR using Skender

        ' RETIREMENT: the ATR length lives on the host now (AutoTradeSettings' Tooling section mirrors
        ' it into frmMainPageV2.AtrLength). Host-owned + already validated, so no parse/throw here -
        ' the old Integer.Parse on a raw textbox threw on any non-numeric edit.
        Dim textATR As Integer = CType(_host, frmMainPageV2).AtrLength
        Dim atrSeries = quotes.GetAtr(textATR)
        Dim atrValue = atrSeries.LastOrDefault()?.Atr

        ' Only proceed if ATR exists
        If atrValue.HasValue Then
            ' Cross-thread fix: publish ATR to a backing field so frmMainPageV2's receive-loop slippage
            ' check (CalculateATRSlippageLimit/IsATRSlippageExcessive) reads CurrentATR instead of lblATR.Text.
            _currentATR = CDec(atrValue.Value)
            Dim atrText = $"{atrValue.Value:F2}"
            ' Thread‐safe UI update
            If Me.InvokeRequired Then
                Me.Invoke(Sub()
                              lblATR.Text = atrText
                              lblATR.ForeColor = Color.White
                          End Sub)
            Else
                lblATR.Text = atrText
                lblATR.ForeColor = Color.White
            End If
        End If


    End Sub

    '--------------------------------------------------------------------------
    ' Helper to update the two labels on the UI thread
    '--------------------------------------------------------------------------
    Private Sub UpdateEmaVwapLabels(emaText As String, emaClr As Color, vwapText As String, vwapClr As Color)
        lblEMA.Text = emaText
        lblEMA.ForeColor = emaClr
        lblVWAP.Text = vwapText
        lblVWAP.ForeColor = vwapClr
    End Sub



    Private Sub AppendLog(text As String, Optional color As Color = Nothing)
        Try
            ' Check if we're on the UI thread
            If Me.InvokeRequired Then
                ' We're on a background thread - marshal to UI thread
                Me.BeginInvoke(Sub() AppendLog(text, color))
                Return
            End If

            Dim currentTime As DateTime = DateTime.Now
            Dim formattedTime As String = currentTime.ToString("HH:mm:ss")

            ' We're on the UI thread - safe to update control
            txtIndLogs.SelectionStart = txtIndLogs.TextLength
            txtIndLogs.SelectionLength = 0

            ' Apply color if specified
            If color <> Nothing AndAlso color <> Color.Empty Then
                txtIndLogs.SelectionColor = color
            Else
                txtIndLogs.SelectionColor = txtIndLogs.ForeColor
            End If

            txtIndLogs.AppendText(formattedTime & " - " & text & Environment.NewLine)
            txtIndLogs.SelectionColor = txtIndLogs.ForeColor ' Reset color

            ' Auto-scroll to bottom
            txtIndLogs.ScrollToCaret()

        Catch ex As Exception
            ' Fallback to debug output if UI logging fails
            System.Diagnostics.Debug.WriteLine($"Logging error: {ex.Message}")
        End Try
    End Sub



    ' ── Cleanup ────────────────────────────────────────────────────────────────
    Private Sub FrmIndicators_FormClosing(sender As Object, e As FormClosingEventArgs) Handles Me.FormClosing
        pollTimer.Stop()
        If client IsNot Nothing AndAlso client.State = WebSocketState.Open Then
            ' Bounded wait on shutdown: a slow/unresponsive close must not hang the UI thread.
            client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait(TimeSpan.FromSeconds(2))
        End If
    End Sub

    ' ── Data Models ───────────────────────────────────────────────────────────
    Private Class DeribitCandle
        Public Property ticks As Long
        Public Property open As Decimal
        Public Property high As Decimal
        Public Property low As Decimal
        Public Property close As Decimal
        Public Property volume As Decimal
    End Class

    Private Sub heartbeatTimer_Tick(sender As Object, e As EventArgs)
        redHeartBeat.BackColor = Color.Black
        heartbeatTimer.Stop()
    End Sub


End Class
