Imports System
Imports System.Collections.Generic
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Timers
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports Skender.Stock.Indicators

Public Class FrmIndicators
    Private client As ClientWebSocket
    Private Shared ohlcList As New List(Of Quote)()
    Private Const DeribitUrl As String = "wss://www.deribit.com/ws/api/v2"
    Private lastTimestamp As Long
    Private pollTimer As New Timers.Timer(60000) ' 60 000 ms = 1 minute

    ' ── Form Load ───────────────────────────────────────────────────────────────
    Private Sub FrmIndicators_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        pollTimer.AutoReset = True
        AddHandler pollTimer.Elapsed, AddressOf OnPollElapsed
    End Sub

    ' ── Start Button ───────────────────────────────────────────────────────────
    Private Sub btnStart_Click(sender As Object, e As EventArgs) Handles btnStart.Click
        btnStart.Enabled = False
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
                .start_timestamp = CLng(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds()),
                .end_timestamp = CLng(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            }
        }
            Await SendJson(histReq)

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
            Dim buffer(8192) As Byte
            While client.State = WebSocketState.Open
                Try
                    Dim r = Await client.ReceiveAsync(New ArraySegment(Of Byte)(buffer), CancellationToken.None)
                    If r.MessageType = WebSocketMessageType.Text Then
                        Dim resp = Encoding.UTF8.GetString(buffer, 0, r.Count)
                        ProcessMessage(resp)
                    ElseIf r.MessageType = WebSocketMessageType.Close Then
                        Exit While
                    End If
                Catch ex As WebSocketException
                    ' Log error and attempt reconnection
                    Console.WriteLine($"WebSocket error: {ex.Message}")
                    Exit While
                End Try
            End While
        Catch ex As Exception
            ' Handle connection errors
            Console.WriteLine($"Connection error: {ex.Message}")
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
                             Console.WriteLine($"Polling error: {ex.Message}")
                         End Try
                         Return Nothing ' Explicit return for Function
                     End Function)
        Catch ex As Exception
            Console.WriteLine($"Timer error: {ex.Message}")
        End Try
    End Sub

    ' ── Message Processor ─────────────────────────────────────────────────────
    Private Sub ProcessMessage(raw As String)
        Try
            ' Add debug logging
            Console.WriteLine($"Received: {raw}")

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
                            If ohlcList.Count > 1000 Then ohlcList.RemoveAt(0)
                        End If
                    Next

                    ' Start fallback polling after history loaded
                    If Not pollTimer.Enabled Then pollTimer.Start()
                End SyncLock

                Invoke(Sub() UpdateSignals())
                Return
            End If

            ' Handle live candle subscription
            If msg("method")?.ToString() = "subscription" Then
                Dim channelName = msg("params")("channel")?.ToString()
                Console.WriteLine($"Subscription message from channel: {channelName}")

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
                            If ohlcList.Count > 1000 Then ohlcList.RemoveAt(0)
                        End If
                    End SyncLock

                    Invoke(Sub() UpdateSignals())
                End If
            End If
        Catch ex As JsonException
            Console.WriteLine($"JSON parsing error: {ex.Message}")
        Catch ex As Exception
            Console.WriteLine($"Message processing error: {ex.Message}")
        End Try
    End Sub

    ' ── Indicator Calculations & UI Update ────────────────────────────────────
    Private Sub UpdateSignals()
        Dim quotes As List(Of Quote)
        SyncLock ohlcList
            quotes = New List(Of Quote)(ohlcList)
        End SyncLock

        If quotes.Count < 14 Then Return

        ' Reset labels
        'lblDMI.Text = "-"
        'lblMACD.Text = "-"
        'lblRSI.Text = "-"
        'lblStoch.Text = "-"

        ' DMI (9): +DI>–DI & ADX>25 → Buy; < → Sell
        ' DMI (9): Enhanced with signal persistence and trend strength
        Static prevPDI As Decimal = -1, prevMDI As Decimal = -1, prevADX As Decimal = -1
        Static dmiInitialized As Boolean = False
        Static lastDMISignal As String = "-"
        Static lastDMISignalTime As DateTime = DateTime.MinValue

        Dim dmi = quotes.GetAdx(9).LastOrDefault()
        If dmi IsNot Nothing AndAlso dmi.Pdi.HasValue AndAlso dmi.Mdi.HasValue AndAlso dmi.Adx.HasValue Then
            Dim pdi = CDec(dmi.Pdi.Value)
            Dim mdi = CDec(dmi.Mdi.Value)
            Dim adx = CDec(dmi.Adx.Value)

            ' Initialize on first run
            If Not dmiInitialized Then
                prevPDI = pdi : prevMDI = mdi : prevADX = adx
                dmiInitialized = True
                lblDMI.Text = "INIT" : lblDMI.ForeColor = Color.Gray
                lastDMISignal = "INIT"
                Return
            End If

            Dim newDMISignal As String = lastDMISignal
            Dim newDMIColor As Color = lblDMI.ForeColor

            ' Check for crossover with trend strength
            If pdi > mdi AndAlso prevPDI <= prevMDI AndAlso adx > 15 Then
                ' Bullish crossover with trend strength
                If adx > 30 Then
                    newDMISignal = "STRONG BUY" : newDMIColor = Color.Lime
                ElseIf adx > 20 Then
                    newDMISignal = "BUY" : newDMIColor = Color.LightGreen
                Else
                    newDMISignal = "WEAK BUY" : newDMIColor = Color.YellowGreen
                End If
                lastDMISignalTime = DateTime.Now

            ElseIf pdi < mdi AndAlso prevPDI >= prevMDI AndAlso adx > 15 Then
                ' Bearish crossover with trend strength
                If adx > 30 Then
                    newDMISignal = "STRONG SELL" : newDMIColor = Color.Red
                ElseIf adx > 20 Then
                    newDMISignal = "SELL" : newDMIColor = Color.Orange
                Else
                    newDMISignal = "WEAK SELL" : newDMIColor = Color.Yellow
                End If
                lastDMISignalTime = DateTime.Now
            End If

            ' Only update if signal changed
            If newDMISignal <> lastDMISignal Then
                lblDMI.Text = newDMISignal
                lblDMI.ForeColor = newDMIColor
                lastDMISignal = newDMISignal
                Console.WriteLine($"DMI Signal Updated: {newDMISignal} (PDI:{pdi:F1}, MDI:{mdi:F1}, ADX:{adx:F1})")
            End If

            prevPDI = pdi
            prevMDI = mdi
            prevADX = adx
        End If

        '-----------------------------------------------------------------------------------------------------------------------

        ' MACD (6,13,5): Enhanced with momentum confirmation
        Static prevHistogram As Decimal = 0, prevMacdLine As Decimal = 0, prevSignalLine As Decimal = 0
        Static macdInitialized As Boolean = False
        Static lastMACDSignal As String = "-"
        Static lastMACDSignalTime As DateTime = DateTime.MinValue

        Dim macd = quotes.GetMacd(6, 13, 5).LastOrDefault()
        If macd IsNot Nothing AndAlso macd.Histogram.HasValue AndAlso macd.Macd.HasValue AndAlso macd.Signal.HasValue Then
            Dim histogram = CDec(macd.Histogram.Value)
            Dim macdLine = CDec(macd.Macd.Value)
            Dim signalLine = CDec(macd.Signal.Value)

            ' Initialize on first run
            If Not macdInitialized Then
                prevHistogram = histogram : prevMacdLine = macdLine : prevSignalLine = signalLine
                macdInitialized = True
                lblMACD.Text = "INIT" : lblMACD.ForeColor = Color.Gray
                lastMACDSignal = "INIT"
                Return
            End If

            Dim newMACDSignal As String = lastMACDSignal
            Dim newMACDColor As Color = lblMACD.ForeColor

            ' Check for MACD line crossing signal line with momentum
            If macdLine > signalLine AndAlso prevMacdLine <= prevSignalLine Then
                ' Bullish crossover
                If histogram > 0.5 Then
                    newMACDSignal = "STRONG BUY" : newMACDColor = Color.Lime
                ElseIf histogram > 0 Then
                    newMACDSignal = "BUY" : newMACDColor = Color.LightGreen
                Else
                    newMACDSignal = "WEAK BUY" : newMACDColor = Color.YellowGreen
                End If
                lastMACDSignalTime = DateTime.Now

            ElseIf macdLine < signalLine AndAlso prevMacdLine >= prevSignalLine Then
                ' Bearish crossover
                If histogram < -0.5 Then
                    newMACDSignal = "STRONG SELL" : newMACDColor = Color.Red
                ElseIf histogram < 0 Then
                    newMACDSignal = "SELL" : newMACDColor = Color.Orange
                Else
                    newMACDSignal = "WEAK SELL" : newMACDColor = Color.Yellow
                End If
                lastMACDSignalTime = DateTime.Now
            End If

            ' Only update if signal changed
            If newMACDSignal <> lastMACDSignal Then
                lblMACD.Text = newMACDSignal
                lblMACD.ForeColor = newMACDColor
                lastMACDSignal = newMACDSignal
                Console.WriteLine($"MACD Signal Updated: {newMACDSignal} (MACD:{macdLine:F3}, Signal:{signalLine:F3}, Hist:{histogram:F3})")
            End If

            prevHistogram = histogram : prevMacdLine = macdLine : prevSignalLine = signalLine
        End If

        '-----------------------------------------------------------------------------------------------------------------------

        ' RSI (9): Enhanced with momentum and divergence detection
        Static prevRSI As Decimal = -1, prevPrice As Decimal = -1
        Static rsiInitialized As Boolean = False
        Static lastRSISignal As String = "-"
        Static lastRSISignalTime As DateTime = DateTime.MinValue

        Dim rsi = quotes.GetRsi(9).LastOrDefault()
        Dim currentPrice = quotes.Last().Close

        If rsi IsNot Nothing AndAlso rsi.Rsi.HasValue Then
            Dim rsiValue = CDec(rsi.Rsi.Value)

            ' Initialize on first run
            If Not rsiInitialized Then
                prevRSI = rsiValue : prevPrice = currentPrice
                rsiInitialized = True
                lblRSI.Text = "INIT" : lblRSI.ForeColor = Color.Gray
                lastRSISignal = "INIT"
                Return
            End If

            Dim newRSISignal As String = lastRSISignal
            Dim newRSIColor As Color = lblRSI.ForeColor

            ' Enhanced RSI signals with momentum confirmation
            If rsiValue < 30 AndAlso prevRSI >= 30 Then
                ' Entering oversold territory
                If rsiValue < 15 Then
                    newRSISignal = "STRONG BUY" : newRSIColor = Color.Lime
                ElseIf rsiValue < 25 Then
                    newRSISignal = "BUY" : newRSIColor = Color.LightGreen
                Else
                    newRSISignal = "WEAK BUY" : newRSIColor = Color.YellowGreen
                End If
                lastRSISignalTime = DateTime.Now

            ElseIf rsiValue > 70 AndAlso prevRSI <= 70 Then
                ' Entering overbought territory
                If rsiValue > 85 Then
                    newRSISignal = "STRONG SELL" : newRSIColor = Color.Red
                ElseIf rsiValue > 75 Then
                    newRSISignal = "SELL" : newRSIColor = Color.Orange
                Else
                    newRSISignal = "WEAK SELL" : newRSIColor = Color.Yellow
                End If
                lastRSISignalTime = DateTime.Now

                ' RSI divergence detection (advanced)
            ElseIf rsiValue > 50 AndAlso prevRSI <= 50 AndAlso currentPrice > prevPrice Then
                ' Bullish momentum confirmation
                newRSISignal = "MOMENTUM BUY" : newRSIColor = Color.CornflowerBlue
                lastRSISignalTime = DateTime.Now

            ElseIf rsiValue < 50 AndAlso prevRSI >= 50 AndAlso currentPrice < prevPrice Then
                ' Bearish momentum confirmation  
                newRSISignal = "MOMENTUM SELL" : newRSIColor = Color.Coral
                lastRSISignalTime = DateTime.Now
            End If

            ' Only update if signal changed
            If newRSISignal <> lastRSISignal Then
                lblRSI.Text = newRSISignal
                lblRSI.ForeColor = newRSIColor
                lastRSISignal = newRSISignal
                Console.WriteLine($"RSI Signal Updated: {newRSISignal} (RSI:{rsiValue:F1}, Price:{currentPrice:F2})")
            End If

            prevRSI = rsiValue : prevPrice = currentPrice
        End If

        '-----------------------------------------------------------------------------------------------------------------------

        ' Stochastic %K5%D2
        ' Aggressive scalping version with momentum confirmation
        ' Enhanced Stochastic with Signal Persistence
        Static prevK As Decimal = -1, prevD As Decimal = -1
        Static initialized As Boolean = False
        Static lastSignal As String = "-"  ' Store the last signal
        Static lastSignalTime As DateTime = DateTime.MinValue

        If quotes.Count() < 15 Then
            lblStoch.Text = "INSUFFICIENT DATA"
            lblStoch.ForeColor = Color.Gray
            Return
        End If

        Dim st = quotes.GetStoch(8, 3, 3).LastOrDefault()
        If st IsNot Nothing AndAlso st.Oscillator.HasValue AndAlso st.Signal.HasValue Then
            Dim k = CDec(st.Oscillator.Value)
            Dim d = CDec(st.Signal.Value)

            ' Initialize on first run
            If Not initialized Then
                prevK = k : prevD = d : initialized = True
                lblStoch.Text = "INIT" : lblStoch.ForeColor = Color.Gray
                lastSignal = "INIT"
                Return
            End If

            Dim newSignal As String = lastSignal ' Default to keeping current signal
            Dim newColor As Color = lblStoch.ForeColor

            ' **Check for new signals only**
            If k > d AndAlso prevK <= prevD Then
                ' Crossover up occurred
                If d < 25 Then
                    newSignal = "STRONG BUY" : newColor = Color.Lime
                ElseIf d < 40 Then
                    newSignal = "BUY" : newColor = Color.LightGreen
                Else
                    newSignal = "WEAK BUY" : newColor = Color.YellowGreen
                End If
                lastSignalTime = DateTime.Now

            ElseIf k < d AndAlso prevK >= prevD Then
                ' Crossover down occurred
                If d > 75 Then
                    newSignal = "STRONG SELL" : newColor = Color.Red
                ElseIf d > 60 Then
                    newSignal = "SELL" : newColor = Color.Orange
                Else
                    newSignal = "WEAK SELL" : newColor = Color.Yellow
                End If
                lastSignalTime = DateTime.Now
            End If

            ' **Only update if signal actually changed**
            If newSignal <> lastSignal Then
                lblStoch.Text = newSignal
                lblStoch.ForeColor = newColor
                lastSignal = newSignal

                ' Debug logging
                Console.WriteLine($"Stoch Signal Updated: {newSignal} (K:{k:F1}, D:{d:F1})")
            End If

            prevK = k : prevD = d
        End If

    End Sub

    ' ── Cleanup ────────────────────────────────────────────────────────────────
    Private Sub FrmIndicators_FormClosing(sender As Object, e As FormClosingEventArgs) Handles Me.FormClosing
        pollTimer.Stop()
        If client IsNot Nothing AndAlso client.State = WebSocketState.Open Then
            client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait()
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

End Class
