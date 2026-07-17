Option Strict On
Option Explicit On

Imports System.IO
Imports Newtonsoft.Json.Linq

''' <summary>
''' Ergonomics item A (docs/spec-execution-ergonomics.md): the standing trade inputs, persisted in
''' a local, git-ignored orderapp-settings.json beside the executable (same discovery pattern as
''' secrets.json; orderapp-settings.example.json documents the shape).
'''
''' Persisted: the seven standing inputs (amount / take_profit / trigger / stop_loss /
''' trigger_offset / tp_offset / market_stop_loss), the two guard checkboxes + the ATR-slippage
''' multiplier, the item-B risk-sizing keys (risk_per_trade_usd / max_size_usd) and the item-D
''' alerts block. Per-trade values (txtManualTP/txtManualSL) and anything credential-like are
''' deliberately NEVER persisted. The bridge gate-config boxes (Auto Settings) are excluded too -
''' they reset per session by design (spec post-retirement note).
'''
''' Load is tolerant: a missing file or missing key leaves the Designer default untouched
''' (nullable = "key absent"); a malformed file reports and falls back whole. Failures never
''' block startup or shutdown - the caller logs yellow and carries on.
''' </summary>
Public NotInheritable Class AppUserSettings

    ' Standing inputs. Nothing = key absent in the file -> leave the Designer default alone.
    Public Amount As Decimal?
    Public TakeProfit As Decimal?
    Public Trigger As Decimal?
    Public StopLoss As Decimal?
    Public TriggerOffset As Decimal?
    Public TpOffset As Decimal?
    Public MarketStopLoss As Decimal?
    Public MaxSlippageAtrChecked As Boolean?
    Public MarketStopChecked As Boolean?
    Public MaxSlippageAtrMult As Decimal?

    ' Item B (risk-based sizing) - spec defaults; the owner tunes these in the file (no UI).
    Public RiskPerTradeUsd As Decimal = 25D
    Public MaxSizeUsd As Decimal = 500D

    ' Item D (alerts) - all default ON; toggled per kind in the file (no UI).
    Public AlertEntryFill As Boolean = True
    Public AlertCloseFill As Boolean = True
    Public AlertEmergencyStop As Boolean = True
    Public AlertOrderRejected As Boolean = True
    Public AlertConnection As Boolean = True

    ''' <summary>Where Save writes (beside the exe - the first Load candidate).</summary>
    Public Shared ReadOnly Property SavePath As String
        Get
            Return Path.Combine(AppContext.BaseDirectory, "orderapp-settings.json")
        End Get
    End Property

    Private Shared Function FindSettingsFile() As String
        Dim candidates As String() = {
            Path.Combine(AppContext.BaseDirectory, "orderapp-settings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "orderapp-settings.json")
        }
        For Each c In candidates
            If File.Exists(c) Then Return c
        Next
        Return Nothing
    End Function

    ''' <summary>
    ''' Never throws. message is Nothing on a clean load, else a human-readable note (missing or
    ''' malformed file - the caller logs it yellow); either way the returned instance is usable.
    ''' </summary>
    Public Shared Function Load(ByRef message As String) As AppUserSettings
        message = Nothing
        Dim result As New AppUserSettings()
        Try
            Dim filePath As String = FindSettingsFile()
            If filePath Is Nothing Then
                message = "orderapp-settings.json not found - starting on Designer defaults (right-click MARGINS/AMOUNT to save)"
                Return result
            End If

            Dim json As JObject = JObject.Parse(File.ReadAllText(filePath))
            result.Amount = json.SelectToken("amount")?.ToObject(Of Decimal?)()
            result.TakeProfit = json.SelectToken("take_profit")?.ToObject(Of Decimal?)()
            result.Trigger = json.SelectToken("trigger")?.ToObject(Of Decimal?)()
            result.StopLoss = json.SelectToken("stop_loss")?.ToObject(Of Decimal?)()
            result.TriggerOffset = json.SelectToken("trigger_offset")?.ToObject(Of Decimal?)()
            result.TpOffset = json.SelectToken("tp_offset")?.ToObject(Of Decimal?)()
            result.MarketStopLoss = json.SelectToken("market_stop_loss")?.ToObject(Of Decimal?)()
            result.MaxSlippageAtrChecked = json.SelectToken("max_slippage_atr_checked")?.ToObject(Of Boolean?)()
            result.MarketStopChecked = json.SelectToken("market_stop_checked")?.ToObject(Of Boolean?)()
            result.MaxSlippageAtrMult = json.SelectToken("max_slippage_atr_mult")?.ToObject(Of Decimal?)()

            result.RiskPerTradeUsd = If(json.SelectToken("risk_per_trade_usd")?.ToObject(Of Decimal?)(), 25D)
            result.MaxSizeUsd = If(json.SelectToken("max_size_usd")?.ToObject(Of Decimal?)(), 500D)

            Dim alerts As JToken = json.SelectToken("alerts")
            If alerts IsNot Nothing Then
                result.AlertEntryFill = If(alerts.SelectToken("entry_fill")?.ToObject(Of Boolean?)(), True)
                result.AlertCloseFill = If(alerts.SelectToken("close_fill")?.ToObject(Of Boolean?)(), True)
                result.AlertEmergencyStop = If(alerts.SelectToken("emergency_stop")?.ToObject(Of Boolean?)(), True)
                result.AlertOrderRejected = If(alerts.SelectToken("order_rejected")?.ToObject(Of Boolean?)(), True)
                result.AlertConnection = If(alerts.SelectToken("connection")?.ToObject(Of Boolean?)(), True)
            End If
        Catch ex As Exception
            message = $"orderapp-settings.json unreadable ({ex.Message}) - starting on Designer defaults"
            result = New AppUserSettings() ' a half-parsed file must not half-apply
        End Try
        Return result
    End Function

    ''' <summary>
    ''' Writes the full document (round-trips risk/alerts, so hand-edits to keys without UI
    ''' survive a save). Returns Nothing on success, else an error message (caller logs yellow).
    ''' </summary>
    Public Function Save() As String
        Try
            Dim json As New JObject From {
                {"amount", Amount},
                {"take_profit", TakeProfit},
                {"trigger", Trigger},
                {"stop_loss", StopLoss},
                {"trigger_offset", TriggerOffset},
                {"tp_offset", TpOffset},
                {"market_stop_loss", MarketStopLoss},
                {"max_slippage_atr_checked", MaxSlippageAtrChecked},
                {"market_stop_checked", MarketStopChecked},
                {"max_slippage_atr_mult", MaxSlippageAtrMult},
                {"risk_per_trade_usd", RiskPerTradeUsd},
                {"max_size_usd", MaxSizeUsd},
                {"alerts", New JObject From {
                    {"entry_fill", AlertEntryFill},
                    {"close_fill", AlertCloseFill},
                    {"emergency_stop", AlertEmergencyStop},
                    {"order_rejected", AlertOrderRejected},
                    {"connection", AlertConnection}
                }}
            }
            File.WriteAllText(SavePath, json.ToString())
            Return Nothing
        Catch ex As Exception
            Return $"orderapp-settings.json save failed: {ex.Message}"
        End Try
    End Function

End Class
