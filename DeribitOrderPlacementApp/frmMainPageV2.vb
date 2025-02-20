Imports System.Globalization
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports Newtonsoft.Json.Linq
Imports System.Net.WebSockets
Imports System.Threading
Imports System.Text
Imports System.Net
Imports System.Reflection
Imports Microsoft.VisualBasic.ApplicationServices
Imports System.Runtime

Public Class frmMainPageV2

    Private webSocketClient As ClientWebSocket
    Private cancellationTokenSource As CancellationTokenSource
    Private lastMessageTime As DateTime
    Private keepAliveTimer As System.Timers.Timer

    'For refresh authentication token
    Private refreshToken As String = Nothing
    Private refreshTokenExpiryTime As DateTime = DateTime.MinValue

    ' Replace with your client ID and client secret
    Private Const ClientId As String = "YZCnDmWo"
    Private Const ClientSecret As String = "EUKusjG9fnmMgsBmPl9TmHod5Otuan8YCnaMy1DvEgA"

    'Public Variables
    Public BestBidPrice, BestAskPrice, TPTrailprice As Decimal

    Private TradeMode As Boolean = True ' Tracks if Buy or Sell mode
    Private isTrailingStopLossPlaced As Boolean = False 'Tracks if trailing stop loss already placed once

    Private latestOrderId As String = Nothing ' Track the most recent order ID


    Private Async Function WebSocketCalls() As Task

        Dim reconnectNeeded As Boolean = False

        ' Initialize WebSocket
        webSocketClient = New ClientWebSocket()
        cancellationTokenSource = New CancellationTokenSource()

        Try
            ' Connect to Deribit WebSocket
            Await webSocketClient.ConnectAsync(New Uri("wss://www.deribit.com/ws/api/v2"), cancellationTokenSource.Token)

            ' Start fetching real-time server time
            'Await Task.Run(AddressOf FetchServerTimeContinuously)

            ' Authorize the connection
            Await AuthorizeWebSocketConnection()

            ' Subscribe to the BTC-PERPETUAL index price user portfolio
            Await SubscribeToIndexPrice()

            ' Subscribe to the user portfolio
            Await SubscribeToUserPortfolio()

            ' Subscribe to the quote.BTC-PERPETUAL channel
            Await SubscribeToQuoteBTCPerpetual()

            ' Subscribe to the user.changes.BTC-PERPETUAL.raw channel (Changes to orders, trades & positions)
            Await SubscribeToUserOrders()

            ' Start monitoring authentication
            Await Task.Run(AddressOf MonitorAuthentication)

            ' Subscribe to the BTC-PERPETUAL index price
            'Dim subscriptionPayload As String = "{""jsonrpc"":""2.0"",""id"":1,""method"":""public/subscribe"",""params"":{""channels"":[""perpetual.BTC-PERPETUAL.agg2"", ""user.portfolio.btc""]}}"

            ' Start receiving messages
            Await ReceiveWebSocketMessagesAsync()

        Catch ex As Exception
            Throw New Exception("Authentication failed: " & ex.Message)
            'txtLogs.AppendText("Authentication failed." + ex.Message + Environment.NewLine)
            AppendColoredText(txtLogs, "Authentication failed." + ex.Message, Color.Red)
            reconnectNeeded = True

        End Try
        If reconnectNeeded Then
            Await ReconnectWebSocket()
        End If
    End Function

    Private Async Function AuthorizeWebSocketConnection() As Task

        ' Create the authorization message using JObject
        Dim authPayload = New JObject(
            New JProperty("jsonrpc", "2.0"),
            New JProperty("id", 2),
            New JProperty("method", "public/auth"),
            New JProperty("params", New JObject(
                New JProperty("grant_type", "client_credentials"),
                New JProperty("client_id", ClientId),
                New JProperty("client_secret", ClientSecret)
            ))
        )
        'Await SendWebSocketMessageAsync(authPayload)
        Await SendWebSocketMessageAsync(authPayload.ToString())

        ' Read the response
        Dim buffer = New Byte(1024 * 4) {}
        Dim result = Await webSocketClient.ReceiveAsync(New ArraySegment(Of Byte)(buffer), cancellationTokenSource.Token)
        Dim response = Encoding.UTF8.GetString(buffer, 0, result.Count)

        Dim json = JObject.Parse(response)
        Dim errorField = json.SelectToken("error")

        If errorField IsNot Nothing Then
            Throw New Exception("Authorization failed: " & errorField.ToString())
            'txtLogs.AppendText("Authorization failed: " & errorField.ToString() + Environment.NewLine)
            AppendColoredText(txtLogs, "Authorization failed: " & errorField.ToString(), Color.Red)
        Else
            'txtLogs.AppendText("WebSocket authorized successfully" + Environment.NewLine)
            AppendColoredText(txtLogs, "WebSocket authorized successfully", Color.DodgerBlue)
            Await EnableHeartbeat(30)

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

            'Give successful status update
            lblStatus.ForeColor = Color.LimeGreen
            btnConnect.Text = "ONLINE"
            btnConnect.BackColor = Color.Lime

        End If
    End Function

    Private Async Function RefreshWebSocketAuthentication() As Task
        If DateTime.UtcNow >= refreshTokenExpiryTime Then

            'Dim refreshPayload As String = $"{{""jsonrpc"":""2.0"",""id"":3,""method"":""public/auth"",""params"":{{""grant_type"":""refresh_token"",""refresh_token"":""{refreshToken}""}}}}"
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
            'Await SendWebSocketMessageAsync(refreshPayload)
            Await SendWebSocketMessageAsync(refreshPayload.ToString())

            Dim buffer = New Byte(1024 * 4) {}
            Dim result = Await webSocketClient.ReceiveAsync(New ArraySegment(Of Byte)(buffer), cancellationTokenSource.Token)
            Dim response = Encoding.UTF8.GetString(buffer, 0, result.Count)

            Dim json = JObject.Parse(response)
            If json.SelectToken("error") IsNot Nothing Then
                Throw New Exception("Token refresh failed: " & json.SelectToken("error").ToString())
                'txtLogs.AppendText("Token refresh failed: " & json.SelectToken("error").ToString() + Environment.NewLine)
                AppendColoredText(txtLogs, "Token refresh failed: " & json.SelectToken("error").ToString(), Color.Yellow)
            End If

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
            End If

            'txtLogs.AppendText("WebSocket re-authenticated successfully" + Environment.NewLine)
            AppendColoredText(txtLogs, "WebSocket re-authenticated successfully", Color.DodgerBlue)
        End If
    End Function

    Private Async Function SendWebSocketMessageAsync(message As String) As Task
        Try
            Dim bytes = Encoding.UTF8.GetBytes(message)

            ' Attempt to send the message
            Await webSocketClient.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, cancellationTokenSource.Token)

        Catch ex As WebSocketException
            ' Log WebSocket-specific errors
            AppendColoredText(txtLogs, "WebSocket Error: " & ex.Message, Color.Red)
        Catch ex As OperationCanceledException
            ' Log if operation was canceled (e.g., during shutdown)
            AppendColoredText(txtLogs, "Operation Canceled: " & ex.Message, Color.Orange)
        Catch ex As Exception
            ' General exception handler for any other errors
            AppendColoredText(txtLogs, "Error sending message: " & ex.Message, Color.Red)
        End Try
    End Function

    Private Async Function ReceiveWebSocketMessagesAsync() As Task
        Dim buffer = New Byte(1024 * 4) {}
        Dim reconnectNeeded As Boolean = False

        ' Initialize lastMessageTime to the current time
        lastMessageTime = DateTime.Now

        While webSocketClient.State = WebSocketState.Open
            Try
                Dim result = Await webSocketClient.ReceiveAsync(New ArraySegment(Of Byte)(buffer), cancellationTokenSource.Token)
                Dim response = Encoding.UTF8.GetString(buffer, 0, result.Count)

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

                ' Timeout check
                If (DateTime.Now - lastMessageTime).TotalSeconds > 75 Then
                    Throw New Exception("No messages received in the last 75 seconds.")
                    'txtLogs.AppendText("No messages received in the last 75 seconds." + Environment.NewLine)
                    AppendColoredText(txtLogs, "No messages received in the last 75 seconds.", Color.Yellow)
                End If


            Catch ex As Exception
                reconnectNeeded = True
                AppendColoredText(txtLogs, ex.Message + Environment.NewLine, Color.Red)
                Exit While
            End Try
        End While
        ' Reconnect if necessary
        If reconnectNeeded Then
            Await ReconnectWebSocket()
        End If
    End Function

    Private Async Sub MonitorAuthentication()
        While webSocketClient.State = WebSocketState.Open
            Try
                Await RefreshWebSocketAuthentication()
                Await Task.Delay(60000) ' Check every minute
            Catch ex As Exception
                'txtLogs.AppendText("Error refreshing token: " & ex.Message + Environment.NewLine)
                AppendColoredText(txtLogs, "Error refreshing token: " & ex.Message, Color.Yellow)
            End Try
        End While
    End Sub

    Private Async Function ReconnectWebSocket() As Task
        'lblStatus.Text = "Reconnecting..."
        'txtLogs.AppendText("Reconnecting..." + Environment.NewLine)
        AppendColoredText(txtLogs, "Reconnecting...", Color.DodgerBlue)
        If webSocketClient IsNot Nothing Then
            webSocketClient.Dispose()
        End If

        Await WebSocketCalls()
    End Function

    Private Async Function EnableHeartbeat(intervalSeconds As Integer) As Task
        Dim heartbeatPayload As String = $"{{""jsonrpc"":""2.0"",""id"":3,""method"":""public/set_heartbeat"",""params"":{{""interval"":{intervalSeconds}}}}}"
        Await SendWebSocketMessageAsync(heartbeatPayload)
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

                ' Update public variables and textboxes on the UI thread
                If indexPrice IsNot Nothing And IsNumeric(indexPrice) Then
                    lastMessageTime = DateTime.Now
                    Me.Invoke(Sub()
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

                Dim USDEquity, Equiv, USDSession As Decimal

                ' Update public variables and textboxes on the UI thread
                '---------------------------------------------------------------

                If (btcEquity <> Nothing) And IsNumeric(lblIndexPrice.Text) Then
                    Me.Invoke(Sub()
                                  lblBTCEquity.Text = btcEquity.ToString("F8")
                                  USDEquity = Decimal.Parse(lblIndexPrice.Text.Trim) * btcEquity
                                  lblUSDEquity.Text = USDEquity.ToString("C", CultureInfo.CreateSpecificCulture("en-US"))
                              End Sub)
                End If

                If (btcBalance <> Nothing) And IsNumeric(lblIndexPrice.Text) Then
                    Me.Invoke(Sub()
                                  ' Update the label with the BTC balance
                                  lblBalance.Text = btcBalance.ToString("F8")
                                  Equiv = Decimal.Parse(lblIndexPrice.Text.Trim) * Decimal.Parse(lblBalance.Text.Trim)
                                  lblEquiv.Text = Equiv.ToString("C", CultureInfo.CreateSpecificCulture("en-US"))
                              End Sub)
                End If

                If (btcSession <> Nothing) And IsNumeric(lblIndexPrice.Text) Then
                    Me.Invoke(Sub()
                                  lblBTCSession.Text = btcSession.ToString("F8")
                                  USDSession = Decimal.Parse(lblIndexPrice.Text.Trim) * Decimal.Parse(lblBTCSession.Text.Trim)
                                  lblUSDSession.Text = USDSession.ToString("C", CultureInfo.CreateSpecificCulture("en-US"))
                              End Sub)

                End If

                ' Checks if got error message
                Dim errorField = json.SelectToken("error")
                If errorField IsNot Nothing Then
                    'txtLogs.AppendText("Error: " & errorField.ToString() + Environment.NewLine)
                    AppendColoredText(txtLogs, "Error: " & errorField.ToString(), Color.Yellow)
                End If

                '---------------------------------------------------------------

                If btcSession < 0 Then
                    lblBTCEquity.ForeColor = Color.Firebrick
                    lblBTCSession.ForeColor = Color.Firebrick
                    lblUSDEquity.ForeColor = Color.Firebrick
                    lblUSDSession.ForeColor = Color.Firebrick
                Else
                    lblBTCEquity.ForeColor = Color.ForestGreen
                    lblBTCSession.ForeColor = Color.ForestGreen
                    lblUSDEquity.ForeColor = Color.ForestGreen
                    lblUSDSession.ForeColor = Color.ForestGreen
                End If
            End If
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in HandleBalanceUpdates: {ex.Message}", Color.Red)
        End Try
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

                'For keeping current order at top of orderbook. +/- 5 leeway to reduce too many edit orders sent
                If (CurrentOpenOrderId IsNot Nothing) And (CurrentTPOrderId IsNot Nothing) And (CurrentSLOrderId IsNot Nothing) Then
                    If TradeMode = True Then
                        If bestBid > ((Decimal.Parse(txtPlacedPrice.Text)) + 5) Then
                            Await UpdateLimitOrderWithOTOCOAsync(bestBid)
                            txtPlacedPrice.Text = bestBid
                        End If
                    Else
                        If bestAsk < ((Decimal.Parse(txtPlacedPrice.Text)) - 5) Then
                            Await UpdateLimitOrderWithOTOCOAsync(bestAsk)
                            txtPlacedPrice.Text = bestAsk
                        End If
                    End If
                End If

                'For keeping current order at top of orderbook for trailing stop loss orders. +/- 5 leeway to reduce too many edit orders sent
                If (CurrentOpenOrderId IsNot Nothing) And (CurrentSLOrderId IsNot Nothing) And (isTrailingStopLossPlaced = True) Then
                    If TradeMode = True Then
                        If bestBid > ((Decimal.Parse(txtPlacedPrice.Text)) + 5) Then
                            Await UpdateStopLossForTrailingOrder(bestBid)
                            txtPlacedPrice.Text = bestBid
                        End If
                    Else
                        If bestAsk < ((Decimal.Parse(txtPlacedPrice.Text)) - 5) Then
                            Await UpdateStopLossForTrailingOrder(bestAsk)
                            txtPlacedPrice.Text = bestAsk
                        End If
                    End If
                End If

                'Check if a trailing order is in position and current price has hit take profit price. 
                'If yes, cancel stop loss and place trailing stop loss order
                If (isTrailingPosition = True) And (isTrailingStopLossPlaced = True) Then

                    If TradeMode = True Then

                        'To set price at which trailing stop loss order is triggered for placement. This directly uses txtTPOffset and txtComms numbers in realtime.
                        'If manual take profit textbox is not empty, use that
                        If Decimal.Parse(txtManualTP.Text) > 0 Then
                            TPTrailprice = Decimal.Parse(txtManualTP.Text)
                        Else
                            TPTrailprice = Decimal.Parse(txtPlacedPrice.Text) + (Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text))
                        End If
                        If TPTrailprice <= bestAsk Then
                            isTrailingStopLossPlaced = False
                            Await TrailingStopLossOrderAsync()
                        End If
                    Else
                        If Decimal.Parse(txtManualTP.Text) > 0 Then
                            TPTrailprice = Decimal.Parse(txtManualTP.Text)
                        Else
                            TPTrailprice = Decimal.Parse(txtPlacedPrice.Text) - (Decimal.Parse(txtTPOffset.Text) + Decimal.Parse(txtComms.Text))
                        End If
                        If TPTrailprice >= bestBid Then
                            isTrailingStopLossPlaced = False
                            Await TrailingStopLossOrderAsync()
                        End If
                    End If
                End If

                If Decimal.Parse(txtPlacedPrice.Text) > 0 Then
                    Dim PnL As Decimal
                    If TradeMode = True Then
                        PnL = (BestAskPrice - Decimal.Parse(txtPlacedPrice.Text)) * (Decimal.Parse(txtAmount.Text) / Decimal.Parse(txtPlacedPrice.Text))
                        If BestAskPrice < Decimal.Parse(txtPlacedPrice.Text) Then
                            Me.Invoke(Sub()
                                          lblPnL.ForeColor = Color.Red
                                      End Sub)
                        Else
                            Me.Invoke(Sub()
                                          lblPnL.ForeColor = Color.Chartreuse
                                      End Sub)
                        End If
                    Else
                        PnL = (Decimal.Parse(txtPlacedPrice.Text) - BestAskPrice) * (Decimal.Parse(txtAmount.Text) / Decimal.Parse(txtPlacedPrice.Text))
                        If BestAskPrice > Decimal.Parse(txtPlacedPrice.Text) Then
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

    ' Variable to track the specific order ID of interest
    Private CurrentOpenOrderId, CurrentTPOrderId, CurrentSLOrderId As String
    Private PositionTPOrderId, PositionSLOrderId As String
    Private isTrailingStop As Boolean = False
    Private isTrailingPosition As Boolean = False
    Private PositionEmpty As Boolean = False

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
                    Dim orders = orderData.SelectToken("orders")?.ToObject(Of List(Of JObject))()
                    If orders IsNot Nothing AndAlso orders.Count > 0 Then
                        Dim OpenOrderNo As Boolean = False
                        Dim unTrigOrder As Boolean = False
                        Dim OpenPositions As Boolean = False
                        Dim ExecPrice, PorLAmt As Decimal
                        Dim PorL As Boolean = True

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
                                                      txtPlacedPrice.Text = If(price?.ToString("F2"), "0")
                                                      OpenOrderNo = True
                                                      OpenPositions = False
                                                      ' Save the current order_id for tracking
                                                      CurrentOpenOrderId = orderId
                                                      lblOrderStatus.Text = "Order Placed"
                                                      lblOrderStatus.ForeColor = Color.Chartreuse

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

                                                      lblOrderStatus.Text = "In Position"
                                                      lblOrderStatus.ForeColor = Color.Yellow
                                                      OpenPositions = True
                                                      OpenOrderNo = False
                                                  Case "EntryTrailingOrder"
                                                      txtPlacedPrice.Text = If(price?.ToString("F2"), "0")
                                                      CurrentOpenOrderId = orderId
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
                                              End Select
                                          End Sub)

                                If UpdateFlag = True Then
                                    AppendColoredText(txtLogs, $"Updated to: ${price}", Color.Yellow)
                                    UpdateFlag = False
                                End If

                            ElseIf (orderState = "untriggered") Then
                                'Dim price = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                'Dim triggerPrice = order.SelectToken("trigger_price")?.ToObject(Of Decimal?)()

                                ' Update textboxes based on the label
                                Me.Invoke(Sub()
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
                                Select Case label
                                    Case "EntryLimitOrder"
                                        lblOrderStatus.Text = "In Position"
                                        lblOrderStatus.ForeColor = Color.Yellow
                                        OpenPositions = True
                                        OpenOrderNo = False
                                    Case "TakeLimitProfit"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                        PorLAmt = (ExecPrice - Decimal.Parse(txtPlacedPrice.Text)) * (Decimal.Parse(txtAmount.Text) / ExecPrice)
                                        PorLAmt = Math.Abs(Math.Round(PorLAmt, 2, MidpointRounding.AwayFromZero))
                                        PorL = True
                                    Case "StopLossOrder"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                        PorLAmt = (Decimal.Parse(txtPlacedPrice.Text) - ExecPrice) * (Decimal.Parse(txtAmount.Text) / ExecPrice)
                                        PorLAmt = Math.Abs(Math.Round(PorLAmt, 2, MidpointRounding.AwayFromZero))
                                        PorL = False
                                        'Need to calculate PorL when trailing stop loss is triggered
                                    Case "EntryTrailingOrder"
                                        lblOrderStatus.Text = "In Position"
                                        lblOrderStatus.ForeColor = Color.Yellow
                                        OpenPositions = True
                                        OpenOrderNo = False
                                        isTrailingStop = True 'For checking if is trailing order when executing In Position code
                                        isTrailingPosition = False   'For sanity confirm that it is not in position
                                    Case "TrailingStopLoss"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                        PorLAmt = (ExecPrice - Decimal.Parse(txtPlacedPrice.Text)) * (Decimal.Parse(txtAmount.Text) / ExecPrice)
                                        PorLAmt = Math.Abs(Math.Round(PorLAmt, 2, MidpointRounding.AwayFromZero))
                                        PorL = True
                                    Case "ReduceLimitOrder"
                                        OpenPositions = True
                                        ExecPrice = order.SelectToken("price")?.ToObject(Of Decimal?)()
                                        If TradeMode = True Then
                                            If ExecPrice > Decimal.Parse(txtPlacedPrice.Text) Then
                                                PorLAmt = (ExecPrice - Decimal.Parse(txtPlacedPrice.Text)) * (Decimal.Parse(txtAmount.Text) / ExecPrice)
                                                PorLAmt = Math.Abs(Math.Round(PorLAmt, 2, MidpointRounding.AwayFromZero))
                                                PorL = True
                                            Else
                                                PorLAmt = (Decimal.Parse(txtPlacedPrice.Text) - ExecPrice) * (Decimal.Parse(txtAmount.Text) / ExecPrice)
                                                PorLAmt = Math.Abs(Math.Round(PorLAmt, 2, MidpointRounding.AwayFromZero))
                                                PorL = False
                                            End If
                                        Else
                                            If ExecPrice > Decimal.Parse(txtPlacedPrice.Text) Then
                                                PorLAmt = (Decimal.Parse(txtPlacedPrice.Text) - ExecPrice) * (Decimal.Parse(txtAmount.Text) / ExecPrice)
                                                PorLAmt = Math.Abs(Math.Round(PorLAmt, 2, MidpointRounding.AwayFromZero))
                                                PorL = False
                                            Else
                                                PorLAmt = (ExecPrice - Decimal.Parse(txtPlacedPrice.Text)) * (Decimal.Parse(txtAmount.Text) / ExecPrice)
                                                PorLAmt = Math.Abs(Math.Round(PorLAmt, 2, MidpointRounding.AwayFromZero))
                                                PorL = True
                                            End If
                                        End If

                                End Select
                            ElseIf orderState = "cancelled" Then
                                Select Case label
                                    Case "TakeLimitProfit"
                                        OpenPositions = True
                                    Case "StopLossOrder"
                                        OpenPositions = True
                                    Case "ReduceLimitOrder"
                                        OpenPositions = True
                                End Select
                            End If

                        Next

                        If OpenPositions = True Then

                            txtManualSL.Text = "0"
                            txtManualTP.Text = "0"

                            'If it is a trailing order, set flag that it is in position
                            If isTrailingStop = True Then
                                isTrailingPosition = True
                                'AppendColoredText(txtLogs, $"Trailing Position: ${isTrailingPosition}", Color.Yellow)
                            End If

                            'Stop autoplacement of orders at top of orderbook if position is found
                            CurrentOpenOrderId = Nothing
                            CurrentTPOrderId = Nothing
                            CurrentSLOrderId = Nothing

                            ' No orders found, check for positions
                            Dim positions = orderData.SelectToken("positions")?.ToObject(Of List(Of JObject))()
                            If positions IsNot Nothing AndAlso positions.Count > 0 Then

                                For Each position In positions

                                    Dim size = position.SelectToken("size")?.ToObject(Of Decimal)()
                                    If size = 0 Then ' Position has been closed

                                        'Reset all flags
                                        OpenPositions = False
                                        isTrailingStop = False
                                        isTrailingPosition = False
                                        isTrailingStopLossPlaced = False

                                        PositionEmpty = True

                                        Await CancelOrderAsync()

                                        If PorL = True Then
                                            AppendColoredText(txtLogs, $"Position executed at {ExecPrice}.", Color.LimeGreen)
                                            AppendColoredText(txtLogs, $"Profit made: ${PorLAmt}.", Color.LimeGreen)
                                        Else
                                            AppendColoredText(txtLogs, $"Position executed at {ExecPrice}.", Color.Crimson)
                                            AppendColoredText(txtLogs, $"Loss of: ${PorLAmt}.", Color.Crimson)
                                        End If

                                    Else
                                        OpenPositions = True
                                        'lblOrderStatus.Text = "In Position"
                                        'lblOrderStatus.ForeColor = Color.Yellow
                                        'Dim price = position.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                        'txtPlacedPrice.Text = If(price?.ToString("F2"), "0")
                                    End If


                                Next
                            End If



                        End If

                    End If


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



    'All order execution code below
    '-----------------------------------------------------------------------
    Private Async Function ExecuteOrderAsync(TypeOfOrder As String) As Task
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

            If TypeOfOrder = "BuyLimit" Then

                ' Ensure BestBidPrice is valid
                If BestBidPrice <= 0 Then
                    'txtLogs.AppendText("Best bid price is not valid." + Environment.NewLine)
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
                ordertype = "limit"


            ElseIf TypeOfOrder = "SellLimit" Then

                ' Ensure BestAskPrice is valid
                If BestAskPrice <= 0 Then
                    'txtLogs.AppendText("Best ask price Is Not valid." + Environment.NewLine)
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
                End If

                triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                ordermethod = "private/sell"
                direction = "buy"
                ordertype = "limit"


            ElseIf TypeOfOrder = "BuyNoSpread" Then

                ' Ensure BestBidPrice is valid
                If BestAskPrice <= 0 Then
                    'txtLogs.AppendText("Best bid price Is Not valid." + Environment.NewLine)
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
                ordertype = "limit"


            ElseIf TypeOfOrder = "SellNoSpread" Then

                ' Ensure BestAskPrice is valid
                If BestBidPrice <= 0 Then
                    'txtLogs.AppendText("Best ask price Is Not valid." + Environment.NewLine)
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
                ordertype = "limit"


            ElseIf TypeOfOrder = "BuyMarket" Then

                MarketOrderType = True

                ' Ensure BestBidPrice is valid
                If BestAskPrice <= 0 Then
                    'txtLogs.AppendText("Best bid price Is Not valid." + Environment.NewLine)
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

            ElseIf TypeOfOrder = "SellMarket" Then

                MarketOrderType = True

                ' Ensure BestAskPrice is valid
                If BestBidPrice <= 0 Then
                    'txtLogs.AppendText("Best ask price Is Not valid." + Environment.NewLine)
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

            Else
                ' Handle unexpected or unsupported order types
                'txtLogs.AppendText("Unsupported order type specified." + Environment.NewLine)
                AppendColoredText(txtLogs, "Unsupported order type specified.", Color.IndianRed)
                Return
            End If


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
                        New JProperty("reduce_only", True),
                        New JProperty("time_in_force", "good_til_cancelled"),
                        New JProperty("post_only", True)
                    ),
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

            ' Add price property only for limit orders
            If Not MarketOrderType Then
                params.Add("price", BestPrice)
                params.Add("post_only", True) ' Post-only is valid only for limit orders
            End If

            ' Prepare the payload for the linked order
            Dim OrderPayload As New JObject(
            New JProperty("jsonrpc", "2.0"),
            New JProperty("id", 2),
            New JProperty("method", ordermethod),
            New JProperty("params", params)
        )

            ' Send the order and capture the server's response
            Await SendWebSocketMessageAsync(OrderPayload.ToString())

            txtPlacedTakeProfitPrice.Text = takeprofitprice.ToString("F2")
            txtPlacedTrigStopPrice.Text = stoplossTriggerPrice.ToString("F2")
            txtPlacedStopLossPrice.Text = stoplossPrice.ToString("F2")

            txtPlacedPrice.Text = BestPrice.ToString("F2")

            If TypeOfOrder = "BuyLimit" Then
                ' Optional: Handle post-order logic (e.g., display confirmation)
                '                txtLogs.AppendText($"Buy limit order placed For {amount} at {BestPrice}." + Environment.NewLine)
                AppendColoredText(txtLogs, $"Buy limit order placed For {amount} at {BestPrice}.", Color.MediumSeaGreen)
            ElseIf TypeOfOrder = "SellLimit" Then
                ' Optional: Handle post-order logic (e.g., display confirmation)
                'txtLogs.AppendText($"Sell limit order placed For {amount} at {BestPrice}." + Environment.NewLine)
                AppendColoredText(txtLogs, $"Sell limit order placed For {amount} at {BestPrice}.", Color.DarkRed)
            ElseIf TypeOfOrder = "BuyNoSpread" Then
                'txtLogs.AppendText($"Buy limit no spread order placed For {amount} at {BestPrice}." + Environment.NewLine)
                AppendColoredText(txtLogs, $"Buy limit no spread order placed For {amount} at {BestPrice}.", Color.MediumSeaGreen)
            ElseIf TypeOfOrder = "SellNoSpread" Then
                'txtLogs.AppendText($"Sell limit no spread order placed For {amount} at {BestPrice}." + Environment.NewLine)
                AppendColoredText(txtLogs, $"Sell limit no spread order placed For {amount} at {BestPrice}.", Color.DarkRed)
            ElseIf TypeOfOrder = "BuyMarket" Then
                'txtLogs.AppendText($"Market buy order placed For {amount} starting at {BestPrice}." + Environment.NewLine)
                AppendColoredText(txtLogs, $"Market buy order placed For {amount} starting at {BestPrice}.", Color.MediumSeaGreen)
            ElseIf TypeOfOrder = "SellMarket" Then
                'txtLogs.AppendText($"Market sell order placed For {amount} starting at {BestPrice}." + Environment.NewLine)
                AppendColoredText(txtLogs, $"Market sell order placed For {amount} starting at {BestPrice}.", Color.DarkRed)
            End If

        Catch ex As Exception
            ' Handle any errors
            'txtLogs.AppendText("Error placing order: " & ex.Message + Environment.NewLine)
            AppendColoredText(txtLogs, "Error placing order: " & ex.Message, Color.Red)
        End Try
    End Function

    Private Async Function CancelOrderAsync() As Task

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

        Me.Invoke(Sub()
                      txtPlacedPrice.Text = "0"
                      txtPlacedTakeProfitPrice.Text = "0"
                      txtPlacedTrigStopPrice.Text = "0"
                      txtPlacedStopLossPrice.Text = "0"
                  End Sub)
        lblOrderStatus.Text = "Awaiting Orders"
        lblOrderStatus.ForeColor = Color.DeepSkyBlue

        If PositionEmpty = False Then
            AppendColoredText(txtLogs, $"Cancelled all open orders", Color.Yellow)
        Else
            PositionEmpty = False
        End If


    End Function


    Private Async Function SendReduceOrderAsync(price As Decimal?, amount As Decimal, direction As String, isMarketOrder As Boolean) As Task
        Try
            'Remember to do a cancel all orders here before sending reduce order

            ' Determine the order type (limit or market)
            Dim orderType As String = If(isMarketOrder, "market", "limit")

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
            Await SendWebSocketMessageAsync(payload.ToString())

            Dim orderDescription As String = $"{orderType.ToUpper()} {direction} {amount} {(If(isMarketOrder, "", $"@ {price}"))}"
            AppendColoredText(txtLogs, $"Reduce-only {orderDescription} order sent.", Color.Green)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in SendReduceOrderAsync: {ex.Message}", Color.Red)
        End Try
    End Function
    Private UpdateFlag As Boolean = False
    Private Async Function UpdateLimitOrderWithOTOCOAsync(newPrice As Decimal) As Task
        Try

            Dim newTPprice, newTrigSLprice, newSLprice As Decimal
            Dim amount As Decimal = Decimal.Parse(txtAmount.Text)

            ' Calculate the new OTOCO order prices
            'If Buy direction
            If TradeMode = True Then

                'If Manual TP textbox is not empty
                If Decimal.Parse(txtManualTP.Text) > 0 Then
                    newTPprice = Decimal.Parse(txtManualTP.Text)
                Else
                    newTPprice = newPrice + Decimal.Parse(txtTakeProfit.Text)
                End If

                'If Manual SL textbox is not empty
                If Decimal.Parse(txtManualSL.Text) > 0 Then
                    newSLprice = Decimal.Parse(txtManualSL.Text)
                    newTrigSLprice = newSLprice + Decimal.Parse(txtStopLoss.Text)
                Else
                    newTrigSLprice = newPrice - Decimal.Parse(txtTrigger.Text)
                    newSLprice = newTrigSLprice - Decimal.Parse(txtStopLoss.Text)
                End If

                'If Sell direction
            Else

                If Decimal.Parse(txtManualTP.Text) > 0 Then
                    newTPprice = Decimal.Parse(txtManualTP.Text)
                Else
                    newTPprice = newPrice - Decimal.Parse(txtTakeProfit.Text)
                End If

                If Decimal.Parse(txtManualSL.Text) > 0 Then
                    newSLprice = Decimal.Parse(txtManualSL.Text)
                    newTrigSLprice = newSLprice - Decimal.Parse(txtStopLoss.Text)
                Else
                    newTrigSLprice = newPrice + Decimal.Parse(txtTrigger.Text)
                    newSLprice = newTrigSLprice + Decimal.Parse(txtStopLoss.Text)
                End If

            End If

            ' Construct the payload for updating the main limit order
            Dim updateOrderPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 223344},
            {"method", "private/edit"},
            {"params", New JObject From {
                {"order_id", CurrentOpenOrderId},
                {"price", newPrice},
                {"amount", amount}
            }}
        }

            ' Send the payload to update the main limit order
            Await SendWebSocketMessageAsync(updateOrderPayload.ToString())

            ' Construct the payload for updating the take profit order
            Dim updateTakeProfitPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 223345},
            {"method", "private/edit"},
            {"params", New JObject From {
                {"order_id", CurrentTPOrderId}, ' Replace with the take profit order ID
                {"price", newTPprice},
                {"amount", amount}
            }}
        }

            ' Send the payload to update the take profit order
            Await SendWebSocketMessageAsync(updateTakeProfitPayload.ToString())

            ' Construct the payload for updating the stop loss order
            Dim updateStopLossPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 223346},
            {"method", "private/edit"},
            {"params", New JObject From {
                {"order_id", CurrentSLOrderId}, ' Replace with the stop loss order ID
                {"price", newSLprice},
                {"trigger_price", newTrigSLprice},
                {"amount", amount}
            }}
        }

            ' Send the payload to update the stop loss order
            Await SendWebSocketMessageAsync(updateStopLossPayload.ToString())

            UpdateFlag = True
            'AppendColoredText(txtLogs, $"Updated to: ${newPrice}", Color.Yellow)
        Catch ex As Exception
            txtLogs.AppendText("Error in UpdateLimitOrderWithOTOCOAsync: " & ex.Message & Environment.NewLine)
        End Try
    End Function

    Private Async Function UpdateStopLossForTrailingOrder(newPrice As Decimal) As Task
        Try

            Dim newTrigSLprice, newSLprice As Decimal
            Dim amount As Decimal = Decimal.Parse(txtAmount.Text)

            ' Calculate the new OTOCO order prices
            'If Buy direction
            If TradeMode = True Then

                'If Manual SL textbox is not empty
                If Decimal.Parse(txtManualSL.Text) > 0 Then
                    newSLprice = Decimal.Parse(txtManualSL.Text)
                    newTrigSLprice = newSLprice + Decimal.Parse(txtStopLoss.Text)
                Else
                    newTrigSLprice = newPrice - Decimal.Parse(txtTrigger.Text)
                    newSLprice = newTrigSLprice - Decimal.Parse(txtStopLoss.Text)
                End If

                'If Sell direction
            Else

                If Decimal.Parse(txtManualSL.Text) > 0 Then
                    newSLprice = Decimal.Parse(txtManualSL.Text)
                    newTrigSLprice = newSLprice - Decimal.Parse(txtStopLoss.Text)
                Else
                    newTrigSLprice = newPrice + Decimal.Parse(txtTrigger.Text)
                    newSLprice = newTrigSLprice + Decimal.Parse(txtStopLoss.Text)
                End If

            End If

            ' Construct the payload for updating the main limit order
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

            ' Send the payload to update the main limit order
            Await SendWebSocketMessageAsync(updateOrderPayload.ToString())

            ' Construct the payload for updating the stop loss order
            Dim updateStopLossPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 223348},
            {"method", "private/edit"},
            {"params", New JObject From {
                {"order_id", CurrentSLOrderId}, ' Replace with the stop loss order ID
                {"price", newSLprice},
                {"trigger_price", newTrigSLprice},
                {"amount", amount}
            }}
        }

            ' Send the payload to update the stop loss order
            Await SendWebSocketMessageAsync(updateStopLossPayload.ToString())

            UpdateFlag = True
            'AppendColoredText(txtLogs, $"Updated to: ${newPrice}", Color.Yellow)
        Catch ex As Exception
            txtLogs.AppendText("Error in UpdateStopLossForTrailingOrder: " & ex.Message & Environment.NewLine)
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

                    triggeroffset = Decimal.Parse(txtTriggerOffset.Text)

                    ordermethod = "private/sell"
                    direction = "buy"

                Case Else

                    ' Handle unexpected or unsupported order types
                    AppendColoredText(txtLogs, "Unsupported order type specified.", Color.IndianRed)
                    Return

            End Select


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

            ' Prepare the payload for the linked order
            Dim OrderPayload As New JObject(
            New JProperty("jsonrpc", "2.0"),
            New JProperty("id", 2),
            New JProperty("method", ordermethod),
            New JProperty("params", params)
        )

            ' Send the order and capture the server's response
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

            ' Validate the input amount
            Dim amountText As String = txtAmount.Text
            Dim amount As Decimal

            If Not Decimal.TryParse(amountText, amount) OrElse amount <= 0 Then
                'txtLogs.AppendText("Please enter a valid positive amount." + Environment.NewLine)
                AppendColoredText(txtLogs, "Please enter a valid positive amount.", Color.Yellow)
                Return
            End If

            Dim method As String
            Dim startoffset As Decimal = Decimal.Parse(txtTPOffset.Text)
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

    'Non-order execution functions below
    '------------------------------------------------
    ' Define a function to add colored text
    Private Sub AppendColoredText(rtb As RichTextBox, text As String, color As Color)
        Me.Invoke(Sub()
                      rtb.SelectionStart = rtb.TextLength
                      rtb.SelectionLength = 0
                      rtb.SelectionColor = color
                      rtb.AppendText(text + Environment.NewLine)
                      rtb.SelectionColor = rtb.ForeColor ' Reset color back to default
                  End Sub)
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

    'All button logic below
    '----------------------------------------------------------------------------------

    Private Async Sub btnConnect_Click(sender As Object, e As EventArgs) Handles btnConnect.Click
        Try
            Await WebSocketCalls()

            If btnConnect.Text = "Connect!" Then
                AppendColoredText(txtLogs, "Connected." + Environment.NewLine, Color.DodgerBlue)
                'ElseIf btnConnect.Text = "Update!" Then
                '    AppendColoredText(txtLogs, "Balance Updated." + Environment.NewLine, Color.DodgerBlue)
            End If


        Catch ex As Exception
            AppendColoredText(txtLogs, "Connect failed." + Environment.NewLine, Color.Red)
        End Try
    End Sub


    Private Sub btnChangeForm_Click(sender As Object, e As EventArgs) Handles btnChangeForm.Click
        frmMainPage.Show()
        Me.Hide()

    End Sub

    Private Async Sub btnClose_Click(sender As Object, e As EventArgs) Handles btnClose.Click
        Try
            ' Ensure WebSocket client is initialized
            If webSocketClient IsNot Nothing Then
                Select Case webSocketClient.State
                    Case WebSocketState.Open
                        ' Send the logout message
                        Dim logoutPayload = New JObject(
                        New JProperty("jsonrpc", "2.0"),
                        New JProperty("id", 20),
                        New JProperty("method", "private/logout"),
                        New JProperty("params", New JObject())
                    )

                        Await SendWebSocketMessageAsync(logoutPayload.ToString())

                        ' Wait briefly for logout to complete
                        'Await Task.Delay(1000)

                        ' Gracefully close the WebSocket connection
                        Await webSocketClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None)

                    Case WebSocketState.Closed, WebSocketState.Aborted
                        ' If already closed or aborted, just log and continue
                        MessageBox.Show("WebSocket is already closed or aborted.", "WebSocket State", MessageBoxButtons.OK, MessageBoxIcon.Information)

                    Case Else
                        ' Handle other states if necessary
                        MessageBox.Show("WebSocket is not in an open state.", "WebSocket State", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                End Select
            End If
        Catch ex As WebSocketException
            ' Handle WebSocket-specific errors
            MessageBox.Show("WebSocket error during closing: " & ex.Message, "WebSocket Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Catch ex As Exception
            ' Handle all other exceptions
            MessageBox.Show("Error during button closing: " & ex.Message, "Closing Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            ' Clean up resources
            If webSocketClient IsNot Nothing Then
                webSocketClient.Dispose()
            End If
            If cancellationTokenSource IsNot Nothing Then
                cancellationTokenSource.Dispose()
            End If

            ' Ensure the application exits
            Application.Exit()
        End Try
    End Sub


    Private Sub btnClearLog_Click(sender As Object, e As EventArgs) Handles btnClearLog.Click
        txtLogs.Clear()

    End Sub

    Private Sub btnSell_Click(sender As Object, e As EventArgs) Handles btnSell.Click

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

        GroupBoxButtons.Text = "Short"
        GroupBoxPlaced.Text = "Placed Short"

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

    End Sub

    Private Sub btnBuy_Click(sender As Object, e As EventArgs) Handles btnBuy.Click

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

        GroupBoxButtons.Text = "Long"
        GroupBoxPlaced.Text = "Placed Long"

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

    End Sub

    Private Async Sub btnLimit_Click(sender As Object, e As EventArgs) Handles btnLimit.Click
        If TradeMode = True Then
            Await ExecuteOrderAsync("BuyLimit")
        Else
            Await ExecuteOrderAsync("SellLimit")
        End If
    End Sub

    Private Async Sub btnNoSpread_Click(sender As Object, e As EventArgs) Handles btnNoSpread.Click
        If TradeMode = True Then
            Await ExecuteOrderAsync("BuyNoSpread")
        Else
            Await ExecuteOrderAsync("SellNoSpread")
        End If
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
        If TradeMode = True Then
            Await ExecuteOrderAsync("BuyMarket")
        Else
            Await ExecuteOrderAsync("SellMarket")
        End If
    End Sub

    Private Async Sub btnReduceLimit_Click(sender As Object, e As EventArgs) Handles btnReduceLimit.Click
        Try
            Dim direction As String

            ' Determine the direction based on the current position
            If TradeMode = False Then
                direction = "buy" ' To reduce a short, we buy
            ElseIf TradeMode = True Then
                direction = "sell" ' To reduce a long, we sell
            Else
                AppendColoredText(txtLogs, "No position to reduce.", Color.Red)
                Return
            End If

            ' Validate the amount
            Dim amount As Decimal
            If Not Decimal.TryParse(txtAmount.Text, amount) OrElse amount <= 0 Then
                AppendColoredText(txtLogs, "Invalid amount.", Color.Red)
                Return
            End If

            ' Validate the price
            Dim price As Decimal
            If Not Decimal.TryParse(If(TradeMode = False, txtTopBid.Text, txtTopAsk.Text), price) OrElse price <= 0 Then
                AppendColoredText(txtLogs, "Invalid price.", Color.Red)
                Return
            End If

            ' Call the function to send the reduce-only limit order
            Await SendReduceOrderAsync(price, amount, direction, isMarketOrder:=False)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnReduceLimit_Click: {ex.Message}", Color.Red)
        End Try
    End Sub



    Private Async Sub btnReduceMarket_Click(sender As Object, e As EventArgs) Handles btnReduceMarket.Click
        Try
            Dim direction As String

            ' Determine the direction based on the current position
            If TradeMode = True Then
                direction = "sell" ' To reduce a short, we buy
            ElseIf TradeMode = False Then
                direction = "buy" ' To reduce a long, we sell
            Else
                AppendColoredText(txtLogs, "No position to reduce.", Color.Red)
                Return
            End If

            ' Validate the amount
            Dim amount As Decimal
            If Not Decimal.TryParse(txtAmount.Text, amount) OrElse amount <= 0 Then
                AppendColoredText(txtLogs, "Invalid amount.", Color.Red)
                Return
            End If

            ' Call the function to send the reduce-only market order
            Await SendReduceOrderAsync(Nothing, amount, direction, isMarketOrder:=True)

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
                ElseIf PositionTPOrderId IsNot Nothing Then
                    SLOrderID = PositionSLOrderId
                Else
                    AppendColoredText(txtLogs, "S.L. Order ID not found for edit.", Color.Yellow)
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
        If TradeMode = True Then
            Await StopLossForTrailingOrderAsync("BuyTrail")
        Else
            Await StopLossForTrailingOrderAsync("SellTrail")
        End If
    End Sub

    Protected Overrides ReadOnly Property CreateParams As CreateParams
        Get
            Dim cp As CreateParams = MyBase.CreateParams
            ' Disable the maximize and close buttons
            cp.ClassStyle = cp.ClassStyle Or &H200  ' CS_NOCLOSE: Disables the close button
            Return cp
        End Get
    End Property

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

