Imports System.Collections.Concurrent
Imports System.Globalization
Imports System.IO
'Imports System.Net.Http
'Imports System.Net.Http.Headers
'Imports System.Net.WebRequestMethods
Imports System.Net.WebSockets
'Imports System.Reflection
Imports System.Runtime.InteropServices ' ergonomics: FlashWindowEx (item D) + GetScrollInfo/SendMessage (item I)
Imports System.Text
Imports System.Threading
'Imports System.Windows.Forms.VisualStyles
'Imports System.Xml
'Imports Microsoft.VisualBasic.ApplicationServices
Imports Newtonsoft.Json.Linq


Public Class frmMainPageV2

    ' RETIREMENT (docs/spec-back-autotrade-retirement.md): FrmIndicators is no longer shown - it runs
    ' headless purely as the indicator/ATR engine (StartHeadless). This form now OWNS the settings form
    ' (btnAutoSettings opens it); _autotradesettings was a dead never-assigned field before this pass.
    Private _indicators As FrmIndicators
    Private _autotradesettings As AutoTradeSettings

    ' Signal-bridge tie-in (docs/spec-autotrade-tiein.md): the VerdictEngine signal consumer.
    ' Constructed after Shown init; mode/ARM/START live on the AutoTradeSettings SIGNAL BRIDGE panel.
    Private signalBridge As SignalBridge

    ' Tooling mirrors, owned here so the receive thread reads plain fields (never a cross-form control).
    ' AutoTradeSettings pushes these on commit (focus-loss/Enter, not per keystroke - a half-typed value
    ' must never reach the engine). ATR period default 7 mirrors the ENGINE's settings.json ATR.period
    ' (owner ruling 2026-07-24, spec-breaker-persist-atr7-item8.md R3 - the old 14 followed the retired
    ' FrmIndicators autotrading; this is only the FALLBACK, the payload ATR stays first).
    Private atrLengthVal As Integer = 7            ' ATR period for FrmIndicators' headless ATR calc
    Private atrFallbackVal As Decimal = 70D        ' slippage-limit fallback when no ATR is available

    Friend ReadOnly Property AtrLength As Integer
        Get
            Return If(atrLengthVal > 0, atrLengthVal, 7)
        End Get
    End Property

    ' The settings form, for the bridge's live gate-config reads (its own commit-on-blur mirrors).
    Friend ReadOnly Property AutoTradeSettingsForm As AutoTradeSettings
        Get
            Return _autotradesettings
        End Get
    End Property

    ' Order size for automated entries = the manual Amount box (mirrored into orderAmountVal on the
    ' UI thread), so there is exactly one place to set size. Field read - safe on any thread.
    Public ReadOnly Property OrderSizeUSD As Decimal
        Get
            Return orderAmountVal
        End Get
    End Property

    Friend Sub SetToolingValues(atrLength As Integer, atrFallback As Decimal)
        If atrLength > 0 Then atrLengthVal = atrLength
        If atrFallback > 0D Then atrFallbackVal = atrFallback
    End Sub

    ' Risk-sizing UI spec §2: the item-B keys, surfaced to the AutoTradeSettings Tooling boxes.
    ' The values LIVE in userSettings (loaded at frmMainPageV2_Load BEFORE AutoTradeSettings is
    ' constructed - the settings form's seed-before-commit depends on that ordering) and persist
    ' via item A's existing save path; there is deliberately no new save path here.
    Friend ReadOnly Property RiskPerTradeUsd As Decimal
        Get
            Return If(userSettings IsNot Nothing, userSettings.RiskPerTradeUsd, 25D)
        End Get
    End Property

    Friend ReadOnly Property MaxSizeUsd As Decimal
        Get
            Return If(userSettings IsNot Nothing, userSettings.MaxSizeUsd, 500D)
        End Get
    End Property

    ' N2 (docs/spec-risk-sized-bridge-trades.md §2) - the same arrangement as the risk keys above:
    ' the value LIVES in userSettings, persists on item A's save path, and the settings form seeds
    ' its checkbox from here before its first commit. A Boolean, so unlike the risk keys there is no
    ' "non-positive keeps last good" convention - False is a real value and must be persistable.
    Friend ReadOnly Property RiskSizeBridgeTrades As Boolean
        Get
            Return userSettings IsNot Nothing AndAlso userSettings.RiskSizeBridgeTrades
        End Get
    End Property

    Friend Sub SetRiskSizeBridgeTrades(value As Boolean)
        If userSettings Is Nothing Then userSettings = New AppUserSettings()
        userSettings.RiskSizeBridgeTrades = value
    End Sub

    ' Same convention as SetToolingValues: non-positive (= blank/garbage box) keeps the last good value.
    Friend Sub SetRiskSizingValues(riskPerTrade As Decimal, maxSize As Decimal)
        If userSettings Is Nothing Then userSettings = New AppUserSettings()
        If riskPerTrade > 0D Then userSettings.RiskPerTradeUsd = riskPerTrade
        If maxSize > 0D Then userSettings.MaxSizeUsd = maxSize
    End Sub

    ' Circuit breaker (spec-breaker-persist-atr7-item8.md R1) - same arrangement as the risk keys:
    ' lives in userSettings, persists on item A's save path, settings form seeds from here before
    ' its first commit. UNLIKE the risk keys, any parsed value is accepted (<= 0 = a deliberate,
    ' persistable disable); only a PARSE failure keeps last good (the settings form's TryParse).
    Friend ReadOnly Property CircuitBreakerUsd As Decimal
        Get
            Return If(userSettings IsNot Nothing, userSettings.CircuitBreakerUsd, 10D)
        End Get
    End Property

    Friend Sub SetCircuitBreakerUsd(value As Decimal)
        If userSettings Is Nothing Then userSettings = New AppUserSettings()
        userSettings.CircuitBreakerUsd = value
    End Sub

    ' Session policy (docs/spec-session-policy-gate.md D2/§5.4) - the same arrangement as the
    ' risk-sizing keys above: the value LIVES in userSettings, persists on item A's save path, and
    ' the settings form seeds its box from here at construction. Never returns Nothing, so the
    ' settings form and the bridge can both read it without a null dance.
    Friend ReadOnly Property SessionPolicy As SessionPolicyConfig
        Get
            If userSettings Is Nothing OrElse userSettings.SessionPolicy Is Nothing Then
                Return SessionPolicyConfig.Defaults()
            End If
            Return userSettings.SessionPolicy
        End Get
    End Property

    Friend Sub SetSessionPolicy(config As SessionPolicyConfig)
        If config Is Nothing Then Return   ' a failed parse keeps the last good config
        If userSettings Is Nothing Then userSettings = New AppUserSettings()
        userSettings.SessionPolicy = config
    End Sub

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

    ' M.SL emergency-reduce baseline = the LOSS-CAP anchor the emergency market-reduce measures from
    ' (docs/spec-emergency-baseline-fix.md + spec-back-emergency-baseline-hybrid.md).
    ' 0 until the SL triggers -> the emergency falls back to StopLossTriggerOriginal (the trigger price).
    ' At the trigger flip it adopts the flip price, then LATCHES onto the ACTUAL top-of-book SL at its first
    ' post-trigger reposition (emergencyBaselineSettled), and is FROZEN thereafter - it does NOT follow the
    ' app's ongoing chase (owner ruling 2026-07-08: the cap is a hard loss cap at anchor +/- M.SL, not a
    ' trailing guard). A detected MANUAL SL edit re-anchors it (and re-freezes). Reset to 0 wherever
    ' StopLossTriggerOriginal is. Written on the receive/UI thread; read on the receive thread by the
    ' emergency (accepted Decimal torn-read class, same as StopLossTriggerOriginal).
    Public emergencyBaseline As Decimal = 0
    ' Hybrid fix: False from the trigger adopt until the first post-trigger chase reposition captures the
    ' actual top-of-book SL (then True = frozen). A manual edit / restore seed sets it True directly.
    Private emergencyBaselineSettled As Boolean = False

    ' N1 emergency hoist (docs/spec-emergency-hoist.md): single-fire latch for the M.SL emergency
    ' market-stop. The threshold CHECK now runs on every qualifying quote tick (outside the
    ' MinStopLossUpdateInterval / BackoffStopLossRetry throttle), so without a latch consecutive ticks
    ' could dispatch a second market reduce before the flat echo lands. Set immediately BEFORE the
    ' dispatch at both fire branches of UpdateStopLossForTriggeredStopLossOrder (set-then-send: the set
    ' is synchronous, ahead of the first Await, so a re-entrant tick during the await sees it), and
    ' cleared at exactly three sites - CompletePositionClose and the two "fresh order re-establishes a
    ' clean context" placement seeds (beside their cancelPending reset). Deliberately NOT cleared in
    ' the SL-edit paths and NOT in the cancel teardowns: a mid-cancel latch must survive the cancel
    ' window. This is NOT an 8th SL-context reset site - it zeroes no price, no commanded set, no
    ' baseline. Quote-thread owned (the fire path and the hoisted check are both on it); the placement
    ' clears are UI-thread Boolean writes, atomic and benign, same class as the baseline-zeroing
    ' placement writes beside them.
    Private emergencyFired As Boolean = False

    ' Commanded-SL-price set - triggered-SL reconciliation (docs/spec-reconcile-manual-sl-edits.md, 4a + P1).
    ' Post-trigger the app is the single writer of placedStopLossPrice, but a MANUAL SL edit on the exchange
    ' arrives as the SAME open StopLossOrder echo as a lagging echo of the app's OWN chase reposition. We tell
    ' them apart by remembering every SL price the app has commanded in the last ~2s: an open-echo price we did
    ' NOT command (and that differs from our current reference) is a manual edit -> follow it (correct the chase
    ' reference AND the emergency baseline to the true live SL). A price we DID command is our own (possibly
    ' out-of-order) echo -> ignore it, preserving the runaway/transition-race single-writer protection.
    ' Written on the receive thread (each SL edit send) and read on the UI thread (the open echo), so ALL access
    ' is under commandedSLLock. Cleared wherever the SL context resets (mirrors the 7 emergencyBaseline = 0
    ' sites; was briefly 8 with entry-chase v2's fill re-anchor, reverted to 7 by the TP-only fill-reanchor fix).
    ' Match tolerance = half a tick (BTC-PERPETUAL tick is 0.5) so echo rounding can't cause a spurious
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

    ' Ergonomics item A (docs/spec-execution-ergonomics.md): persisted standing inputs + the
    ' item-B/item-D config keys. Loaded at frmMainPageV2_Load (before the mirror sync), saved at
    ' FormClosing (before teardown) and via the "Save Trade Defaults" context item.
    Private userSettings As AppUserSettings

    ' Apply on the UI thread only: writes the textboxes/checkboxes; the existing TextChanged /
    ' CheckedChanged handlers sync the engine mirrors (never set mirror fields directly - spec).
    Private Sub ApplyUserSettingsToControls()
        If userSettings Is Nothing Then Return
        If userSettings.Amount.HasValue Then txtAmount.Text = userSettings.Amount.Value.ToString()
        If userSettings.TakeProfit.HasValue Then txtTakeProfit.Text = userSettings.TakeProfit.Value.ToString()
        If userSettings.Trigger.HasValue Then txtTrigger.Text = userSettings.Trigger.Value.ToString()
        If userSettings.StopLoss.HasValue Then txtStopLoss.Text = userSettings.StopLoss.Value.ToString()
        If userSettings.TriggerOffset.HasValue Then txtTriggerOffset.Text = userSettings.TriggerOffset.Value.ToString()
        If userSettings.TpOffset.HasValue Then txtTPOffset.Text = userSettings.TpOffset.Value.ToString()
        If userSettings.Comms.HasValue Then txtComms.Text = userSettings.Comms.Value.ToString()
        If userSettings.MarketStopLoss.HasValue Then txtMarketStopLoss.Text = userSettings.MarketStopLoss.Value.ToString()
        If userSettings.MaxSlippageAtrMult.HasValue Then txtMaxSlippageATR.Text = userSettings.MaxSlippageAtrMult.Value.ToString()
        If userSettings.MaxSlippageAtrChecked.HasValue Then chkMaxSlippageATR.Checked = userSettings.MaxSlippageAtrChecked.Value
        If userSettings.MarketStopChecked.HasValue Then chkMarketStopLoss.Checked = userSettings.MarketStopChecked.Value
    End Sub

    ' Snapshot the current control values into the settings instance (UI thread only). Blank or
    ' unparseable text persists as 0 - same "not set" convention as the engine mirrors.
    Private Sub CaptureUserSettingsFromControls()
        If userSettings Is Nothing Then userSettings = New AppUserSettings()
        Dim d As Decimal
        userSettings.Amount = If(Decimal.TryParse(txtAmount.Text, d), d, 0D)
        userSettings.TakeProfit = If(Decimal.TryParse(txtTakeProfit.Text, d), d, 0D)
        userSettings.Trigger = If(Decimal.TryParse(txtTrigger.Text, d), d, 0D)
        userSettings.StopLoss = If(Decimal.TryParse(txtStopLoss.Text, d), d, 0D)
        userSettings.TriggerOffset = If(Decimal.TryParse(txtTriggerOffset.Text, d), d, 0D)
        userSettings.TpOffset = If(Decimal.TryParse(txtTPOffset.Text, d), d, 0D)
        userSettings.Comms = If(Decimal.TryParse(txtComms.Text, d), d, 0D)
        userSettings.MarketStopLoss = If(Decimal.TryParse(txtMarketStopLoss.Text, d), d, 0D)
        userSettings.MaxSlippageAtrMult = If(Decimal.TryParse(txtMaxSlippageATR.Text, d), d, 0D)
        userSettings.MaxSlippageAtrChecked = chkMaxSlippageATR.Checked
        userSettings.MarketStopChecked = chkMarketStopLoss.Checked
    End Sub

    Private Sub SaveUserSettings(Optional announce As Boolean = True)
        Try
            CaptureUserSettingsFromControls()
            Dim err As String = userSettings.Save()
            If err IsNot Nothing Then
                AppendColoredText(txtLogs, err, Color.Yellow)
            ElseIf announce Then
                AppendColoredText(txtLogs, "Trade defaults saved to orderapp-settings.json", Color.LimeGreen)
            End If
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Trade defaults save failed: {ex.Message}", Color.Yellow)
        End Try
    End Sub

    ' Ergonomics item B (docs/spec-execution-ergonomics.md): risk-based sizing. UI thread only
    ' (called from this form's UI thread or the AutoTradeSettings SIZE button - same thread);
    ' reads the engine mirrors + best-price fields, writes ONLY txtAmount.Text (the
    ' TextChanged sync mirrors it into orderAmountVal - same path as typing).
    ' size = risk x ref / dist (inverse-contract linearization), floored to the 10-USD contract
    ' step (a non-multiple rejects -32602), clamped to max_size_usd. Refusals leave txtAmount alone.
    ' Risk-sizing UI spec §3: the button moved to AutoTradeSettings' Tooling group (its handler is
    ' a thin forwarder that commits half-typed risk/max-size edits first); the LOGIC stays here,
    ' where the mirrors, prices and log live. Body unchanged from the runtime-verified item B.
    Friend Sub ApplyRiskBasedSize()
        Try
            Dim refPrice As Decimal = If(TradeMode, BestBidPrice, BestAskPrice)
            If refPrice <= 0D Then
                AppendColoredText(txtLogs, "SIZE: no live best price for the active side - connect first", Color.Yellow)
                Return
            End If

            ' Stop distance: an explicit manual SL when set, else the trigger distance (the trigger
            ' distance IS the planned stop distance in offset mode - spec item B).
            Dim dist As Decimal = If(manualSLval > 0D, Math.Abs(refPrice - manualSLval), triggerDistance)
            If dist <= 0D Then
                AppendColoredText(txtLogs, "SIZE: stop distance is 0 (set Manual SL or Trig. P.) - amount untouched", Color.Yellow)
                Return
            End If

            Dim riskUsd As Decimal = If(userSettings IsNot Nothing, userSettings.RiskPerTradeUsd, 25D)
            Dim maxUsd As Decimal = If(userSettings IsNot Nothing, userSettings.MaxSizeUsd, 500D)
            If riskUsd <= 0D Then
                AppendColoredText(txtLogs, "SIZE: risk_per_trade_usd is 0 in orderapp-settings.json - amount untouched", Color.Yellow)
                Return
            End If

            ' N2 (docs/spec-risk-sized-bridge-trades.md §4): the two arithmetic lines that used to
            ' live here ARE the formula, and the bridge act path now needs the same one. They moved
            ' verbatim into SignalBridge.RiskSizedBase so there is ONE formula rather than two copies
            ' that can drift - the ruling reads "untouched" in the spec's Do-not-touch list as
            ' BEHAVIOUR, not text. Nothing about this button changed: the guards above, all three
            ' refusal messages, and the below-10 refusal below are exactly as they were, and the
            ' seam's -1 arm is unreachable from here because those guards already rejected every
            ' non-positive input it tests.
            Dim size As Decimal = SignalBridge.RiskSizedBase(riskUsd, maxUsd, refPrice, dist)
            If size < 10D Then
                AppendColoredText(txtLogs, $"SIZE: risk ${riskUsd:0.##} over ${dist:0.##} rounds below the 10-USD contract step - amount untouched", Color.Yellow)
                Return
            End If

            txtAmount.Text = size.ToString("0")
            AppendColoredText(txtLogs, $"Size: ${size:0} (risk ${riskUsd:0.##} over ${dist:0.##} stop distance)", Color.LimeGreen)
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in ApplyRiskBasedSize: {ex.Message}", Color.Red)
        End Try
    End Sub

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

    ' --- Entry-chase v2 (docs/spec-entry-chase-v2.md) ---
    Private Const ChaseTickUSD As Decimal = 0.5D            ' BTC-PERPETUAL tick (matches the NoSpread branches)

    ' Exchange tick grid (BTC-PERPETUAL = 0.5). Engine levels and average_price fills are the two
    ' fractional price sources in this app; every exchange-bound price derived from them must land
    ' on the grid or Deribit rejects -32602 (harness finding 2026-07-17, testnet fill 64066.83).
    ' NEAREST tick (owner ruling): midpoints round away from zero for determinism.
    Friend Shared Function RoundToTick(price As Decimal) As Decimal
        Return Math.Round(price * 2D, MidpointRounding.AwayFromZero) / 2D
    End Function
    Private Const EntryChaseMinIntervalMs As Integer = 350  ' floor between chase edits (entry-only mode default);
    ' use 700-1000 if EntryOnlyChase is reverted to False
    Private Const EntryOnlyChase As Boolean = True          ' OWNER RULING: default ON. One-line revert switch.
    Private lastEntryChaseUtc As DateTime = DateTime.MinValue ' UTC stamp of the last chase edit (entry + trailing share it)
    Private lastReduceChaseUtc As DateTime = DateTime.MinValue ' separate stamp: the reduce chase must not be starved by entry-chase stamps
    ' legAnchorPrice/legReanchorDriftMax are ORDER-context fields (same lifecycle as placedPrice:
    ' seeded at placement, reset where placedPrice context dies) - NOT SL context; do not add them
    ' to the SL-context reset sites.
    Private legAnchorPrice As Decimal = 0D                  ' entry price the OTOCO legs' geometry is currently based on
    Private legReanchorDriftMax As Decimal = 0D             ' per-placement bound, computed at placement (see spec §4)
    ' Fill-reanchor fix (docs/spec-fill-reanchor-fix.md): staged at the filled-entry echo, consumed at the
    ' post-fill open TakeLimitProfit echo (the TP leg's live id doesn't exist until then). ORDER-context
    ' field - same lifecycle as legAnchorPrice; reset to 0 at the order-context death sites and on consume.
    Private pendingReanchorFill As Decimal = 0D             ' 0 = no pending TP re-anchor

    ' Chase-preserve-placed-size (docs/spec-chase-preserve-placed-size.md): the size ACTUALLY SENT with
    ' the resting working entry. Before this the chase edits re-derived the amount from orderAmountVal
    ' (the Amount-box mirror), so the first reposition rewrote a risk-sized / size_mult-reduced order
    ' back to the box - placed 310, held 10 (review-risk-sized-bridge-trades.md §2). ORDER-context
    ' field, same lifecycle as legAnchorPrice above: seeded at both placement sends, cleared wherever a
    ' working entry ceases to exist. 0 keeps sizeUsdOverride's established meaning - "nothing retained,
    ' use the box" - deliberately NOT a second sentinel.
    ' WRITTEN unconditionally at each placement - the placement is therefore itself a clear, so a manual
    ' order can never inherit a previous act's size even if some exotic path skipped every teardown.
    ' The VALUE is conditional (owner veto 2026-08-01): only an OVERRIDE-sourced size is retained; a
    ' manual placement writes 0 so that a mid-chase Amount-box edit still resizes the resting order.
    ' Written on the UI thread at placement, read on the receive thread by the chase - the same
    ' accepted Decimal torn-read class as placedPrice/legAnchorPrice, which share this exact lifecycle.
    Private placedOrderSizeUsd As Decimal = 0D              ' 0 = nothing retained; the chase reads the box

    ' --- EV chase budget (docs/spec-ev-chase-budget.md) ---
    ' Deribit's 2026-08-01 schedule (maker 1.5 bps / taker 3.5 bps) makes the tail of a chase
    ' unprofitable well before the ATR cap trips: past a point the remaining move to the target no
    ' longer pays for the round trip. These are the HOT-PATH mirrors - the four reposition gates
    ' read them on the receive thread on every quote tick, so they are plain fields (the same
    ' convention as the ATR tooling values), while the persisted copies live in userSettings on
    ' item A's save path.
    '
    ' minNetMovePctVal is a price FRACTION, not a percent: 0.0005 = 0.05% = 5 bps. The Tooling box
    ' is the only percent-flavoured surface in the whole path (it divides by 100 on commit).
    ' <= 0 = the EV condition is OFF entirely, and 0 is the shipped default.
    Private minNetMovePctVal As Decimal = 0D
    ' Maker fee in bps. File-only knob (no UI): the schedule changes by exchange announcement about
    ' once a year, by relay. Nothing hardcodes the 3.0 bps round trip - it derives from this.
    Private makerFeeBpsVal As Decimal = 1.5D
    ' Taker fee in bps, same file-only knob and same relay discipline (docs/spec-fee-comms-repoint.md).
    ' Read on the RECEIVE thread by HandleIndexUpdates on every index tick to set the default comms,
    ' so it is a plain field like its maker sibling - never a control read.
    Private takerFeeBpsVal As Decimal = 3.5D

    ' Round-trip maker fee as a price fraction: maker entry + maker TP (the trader's standing
    ' maker-first flow, per the fee relay). Pure; OrderCheck-pinned.
    Friend Shared Function RoundTripFeePctFromMakerBps(makerFeeBps As Decimal) As Decimal
        Return 2D * makerFeeBps / 10000D
    End Function

    ' Default comms: the taker fee on the full position value, in whole dollars (the comms box has
    ' always held a rounded dollar amount, and the derived TP / break-even trigger add it directly).
    ' Derives from the persisted schedule so a fee announcement is one number in the settings file -
    ' the 2024 constant this replaced was ~43% over the 2026-08-01 taker. Pure; OrderCheck-pinned.
    Friend Shared Function DefaultCommsFromTakerBps(takerFeeBps As Decimal, indexPrice As Decimal) As Decimal
        Return Math.Abs(Math.Round(takerFeeBps / 10000D * indexPrice, 0, MidpointRounding.AwayFromZero))
    End Function

    ' Stop chasing when the remaining move to the target no longer clears round-trip fees plus the
    ' trader's minimum net move. Pure; Friend Shared; OrderCheck-pinned (spec §1).
    Friend Shared Function IsChaseEvExhausted(targetInForce As Decimal, currentPrice As Decimal,
                                              roundTripFeePct As Decimal, minNetMovePct As Decimal) As Boolean
        If minNetMovePct <= 0D OrElse targetInForce <= 0D OrElse currentPrice <= 0D Then Return False ' OFF/undefined = never binds
        Dim remainingNet As Decimal = Math.Abs(targetInForce - currentPrice) - roundTripFeePct * currentPrice
        Return remainingNet < minNetMovePct * currentPrice
    End Function

    ' The knob and the derived round-trip fee, as the gates and the settings form read them.
    Friend ReadOnly Property MinNetMovePct As Decimal
        Get
            Return minNetMovePctVal
        End Get
    End Property

    Friend ReadOnly Property RoundTripFeePct As Decimal
        Get
            Return RoundTripFeePctFromMakerBps(makerFeeBpsVal)
        End Get
    End Property

    ' The BREAKER's contract, deliberately not SetToolingValues' (spec §4): 0 is a legitimate,
    ' persistable OFF, so ANY parsed value is accepted here and only a PARSE failure keeps the last
    ' good one (that TryParse lives in the settings form). Takes the FRACTION - the box has already
    ' divided by 100. A negative value is simply another way to spell OFF (the predicate's guard).
    Friend Sub SetMinNetMovePct(value As Decimal)
        If userSettings Is Nothing Then userSettings = New AppUserSettings()
        minNetMovePctVal = value
        userSettings.MinNetMovePct = value
    End Sub

    ' Seed the hot-path mirrors from the loaded settings. Called at Load right after
    ' ApplyUserSettingsToControls and BEFORE AutoTradeSettings is constructed, because the Tooling
    ' box seeds itself from MinNetMovePct (the standing seed-before-commit ordering).
    Private Sub ApplyEvChaseBudgetFromSettings()
        If userSettings Is Nothing Then Return
        minNetMovePctVal = userSettings.MinNetMovePct
        makerFeeBpsVal = userSettings.MakerFeeBps
        takerFeeBpsVal = userSettings.TakerFeeBps
    End Sub

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

        ' Entry-chase v2 (docs/spec-entry-chase-v2.md §6): true when at least `requests` full request
        ' costs are available. The chase gates require headroom of 4 (1 edit + 3 reserve in entry-only
        ' mode) so chase edits can never starve a nuclear cancel / emergency path of credits.
        Public Function HasHeadroom(requests As Integer) As Boolean
            SyncLock _lockObject
                RefillCredits()
                Return _currentCredits >= requests * _costPerRequest
            End SyncLock
        End Function

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

    ' ===== C1 v2 EXECUTOR FEEDBACK (docs/spec-c1-feedback-emitter.md section 3) =====
    '
    ' The snapshot's host half: live state -> the flat immutable value the emitter's worker writes.
    ' Callable from ANY thread and taken on the CALLER's thread by design (spec section 1.3) - the
    ' position and working-level triggers fire from the very thread that just wrote those fields,
    ' which is what makes the position block coherent without a lock (E4).
    '
    ' EVERY READ HERE IS A BACKING FIELD. Not one of them is a control, and that is absolute:
    ' this runs on the receive thread, where reading a WinForms control caused the edit-flood storm
    ' (docs/spec-cross-thread-fix.md). positionSizeUSD/positionAvgEntry are engine-owned;
    ' placedStopLossPrice is the engine's SL mirror; manualTPval is kept == txtManualTP by
    ' SyncTradeInputsFromUi ON THE UI THREAD. A single control read here is a review-blocking defect.
    '
    ' The flat trap and the sign -> direction mapping are NOT done here - BuildSnapshot owns both,
    ' so they are pure and fixture-pinnable. This function only supplies the raw values.
    Friend Function CaptureFeedbackSnapshot() As ExecutorFeedback.FeedbackSnapshot
        Dim b As SignalBridge = signalBridge   ' one reference read; Nothing before Load finishes
        Dim mode As SignalBridge.BridgeMode = SignalBridge.BridgeMode.Off
        Dim armed As Boolean = False
        Dim started As Boolean = False
        Dim breakerTripped As Boolean = False
        Dim lastSignal As ExecutorFeedback.LastSignalRef = Nothing
        If b IsNot Nothing Then
            mode = b.Mode
            armed = b.LocalArmed          ' the LOCAL toggle - the engine's own ARM is never echoed back
            started = b.Started
            breakerTripped = b.BreakerTripped
            lastSignal = b.FeedbackLastSignal
        End If

        Return ExecutorFeedback.BuildSnapshot(ExecutorFeedback.InstanceId, mode, armed, started,
                                              breakerTripped, IsWebSocketConnected,
                                              positionSizeUSD, positionAvgEntry,
                                              placedStopLossPrice, manualTPval, lastSignal)
    End Function

    ' The SL stop-limit execution offset (mirrors txtStopLoss). The bridge derives its manualSL
    ' (the LIMIT leg) one offset beyond the engine's stop so the TRIGGER lands exactly on it.
    Public ReadOnly Property StopLimitOffset As Decimal
        Get
            Return stopLossOffset
        End Get
    End Property

    ' Bridge START precondition (spec-autotrade-tiein section 1): live mode rides the existing
    ' ATR-slippage guard, so the guard checkbox must be on. Field-backed - safe on any thread.
    Friend ReadOnly Property IsMaxSlippageGuardChecked As Boolean
        Get
            Return maxSlippageATRchecked
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

    ' Q2: the bridge act path stages the signal tag here, beside its SetTradeTargets call and
    ' BEFORE PlaceAutomatedOrder (see the pendingSignal* field block for the full lifecycle).
    Friend Sub SetPendingSignalTag(signalId As Long, confidence As String)
        pendingSignalId = signalId
        pendingSignalConfidence = If(confidence, "")
    End Sub

    ' Q2: a DEFINITIVE placement refusal (gate refusal / exchange rejection - NOT "timeout") means
    ' no order can ever fill from this act: unstage, so the tag can never attach to a later trade.
    Friend Sub ClearPendingSignalTag()
        pendingSignalId = -1
        pendingSignalConfidence = ""
    End Sub

    ' Places an entry with the current targets. v1 policy: STRICT - refuses unless connected,
    ' rate-limit OK, flat, no working entry, and no cancel pending (the engine flattens first if
    ' it wants to flip). side: "long"/"short". kind: "limit"|"market"|"nospread". Returns the
    ' exchange ack (or a gate refusal / 5s timeout). Callable from any thread.
    ' sizeUsdOverride (docs/spec-session-policy-gate.md section 4): 0 = read the Amount box exactly as
    ' before, so every existing call site is byte-identical. Only the bridge act path passes a value,
    ' and only when the session policy's size_mult actually changes the size.
    Public Async Function PlaceAutomatedOrder(side As String, Optional kind As String = "limit",
                                              Optional ackTimeoutMs As Integer = 5000,
                                              Optional sizeUsdOverride As Decimal = 0D) As Task(Of PlacementResult)
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
                                             Return ExecuteOrderAsync(typeOfOrder, reqId, sizeUsdOverride)
                                         End Function
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Await CType(Me.Invoke(placeCall), Task)
        Else
            Await placeCall()
        End If

        ' Await the exchange ack with a timeout. Timeout <> rejection: no rollback (the order may
        ' exist; echoes remain the source of truth) - the caller re-queries state. The entry STAYS
        ' registered, flagged TimedOut, so a >5s-late rejection is logged instead of silently
        ' swallowed (HandlePlacementResponse handles it log-only; the 60-s sweep still GCs it).
        Dim done = Await Task.WhenAny(tcs.Task, Task.Delay(ackTimeoutMs))
        If done Is tcs.Task Then Return tcs.Task.Result
        Dim lateEntry As PendingPlacement = Nothing
        If pendingPlacements.TryGetValue(reqId, lateEntry) Then lateEntry.TimedOut = True
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
        ' Load API credentials from git-ignored secrets.json before any connection attempt.
        ' Moved here from Shown (harness spec section 1): the headless indicator engine below reads
        ' AppSecrets.WsUrl on a background task the moment StartHeadless runs, so the environment
        ' must be resolved BEFORE it starts - loading at Shown raced it. The form handle exists by
        ' Load, so AppendColoredText's handle guard passes and these lines still reach the log.
        Dim secretsError As String = AppSecrets.Load()
        If secretsError IsNot Nothing Then
            AppendColoredText(txtLogs, $"API credentials: {secretsError}", Color.Red)
        Else
            AppendColoredText(txtLogs, "API credentials loaded", Color.LimeGreen)
        End If

        ' Q1 (docs/spec-quickwins-notifier-signalcols.md): exactly one startup line; the topic URL
        ' is a credential and is never logged.
        AppendColoredText(txtLogs, If(RemoteNotifier.IsConfigured,
                                      "Remote notifier: configured",
                                      "Remote notifier: disabled (no ntfy_url)"), Color.Gray)

        ' C1 (docs/spec-c1-feedback-emitter.md section 2): resolve feedback_output_path out of
        ' bridge.json and say so in exactly one line, on the notifier's precedent. Absent key =>
        ' fully inert: no timer, no worker, NO FILE. The path is not a credential, so it is logged.
        ExecutorFeedback.LoadConfig()
        AppendColoredText(txtLogs, ExecutorFeedback.StartupLine(), Color.Gray)

        ' Window title environment convention (harness spec section 1) - LOAD-BEARING:
        ' the prefix "Deribit Order Placement App" is frozen forever (every harness script matches
        ' on it) and the environment suffix is what the script safety tier gates on. The version
        ' may change; the prefix and the suffix placement may not.
        Me.Text = "Deribit Order Placement App V2.2" & If(AppSecrets.IsTestnet, " — TESTNET", " — LIVE")

        Try
            ' Ergonomics item A: restore the persisted standing inputs BEFORE the mirror sync below,
            ' so the engine fields snapshot the restored values (the TextChanged handlers fire on
            ' assignment too - order matters only for correctness-by-construction). Missing/broken
            ' file = Designer defaults + a yellow note; never blocks startup.
            Dim settingsMsg As String = Nothing
            userSettings = AppUserSettings.Load(settingsMsg)
            If settingsMsg IsNot Nothing Then
                AppendColoredText(txtLogs, settingsMsg, Color.Yellow)
            Else
                AppendColoredText(txtLogs, "Trade defaults restored from orderapp-settings.json", Color.LimeGreen)
            End If
            ApplyUserSettingsToControls()
            ' EV chase budget §4: the knob + fee block are file-only reads, so they seed the plain
            ' hot-path fields directly rather than riding a control's TextChanged. Must precede the
            ' AutoTradeSettings construction below - its Tooling box seeds itself from these.
            ApplyEvChaseBudgetFromSettings()

            ' Item A save affordance (implementer's choice per spec: context item over a button -
            ' no free space near the inputs): right-click the MARGINS or AMOUNT($) group.
            Dim saveDefaultsMenu As New ContextMenuStrip()
            saveDefaultsMenu.Items.Add("Save Trade Defaults", Nothing, Sub(s, ev) SaveUserSettings())
            MarginControl.ContextMenuStrip = saveDefaultsMenu
            OrderAmount.ContextMenuStrip = saveDefaultsMenu

            ' Seed the engine input fields from whatever the controls currently hold (cross-thread fix).
            SyncTradeInputsFromUi()
            SyncToggleInputsFromUi()

            ' RETIREMENT: the indicators form is never shown now - it is a headless indicator/ATR engine.
            ' It is NOT Shown, so Form.Load never fires; StartHeadless does the init Load used to do.
            ' Its handle is realized in the constructor, which keeps the receive loop's marshals legal.
            _indicators = New FrmIndicators(Me)     ' pass “self” as host
            _indicators.StartHeadless()

            ' This form owns the settings window now (FrmIndicators used to); btnAutoSettings shows it.
            ' InitialiseSettings seeds the gate-config mirrors from the designer defaults and wires the
            ' select-all/commit-on-blur behaviour - it must not wait for Load, which only fires if the
            ' form is ever shown (the bridge reads those mirrors regardless).
            _autotradesettings = New AutoTradeSettings(Me)
            _autotradesettings.InitialiseSettings()

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Startup Error: {ex.Message}{vbCrLf}{ex.StackTrace}", Color.Red)
            'Application.Exit()
        End Try

        ' UI-test-harness hooks (docs/spec-ui-test-harness.md section 2) - config-gated, zero
        ' contact with the order/receive paths. AccessibleNames are NOT gated: they are inert
        ' metadata (UIA only), and the drive scripts need them whether or not the hotkey is on.
        txtLogs.AccessibleName = "txtLogs"
        txtAmount.AccessibleName = "txtAmount"
        InitialiseHarnessHooks()

        ' Q1: startup ping - doubles as the ntfy connectivity test AND marks every host restart
        ' (the AWS box rebooting shows up as this line on the phone). Post is internally
        ' fire-and-forget and inert without ntfy_url.
        RemoteNotifier.Post("OrderApp", $"OrderApp started — {If(AppSecrets.IsTestnet, "TESTNET", "LIVE")}, breaker ${CircuitBreakerUsd.ToString(Globalization.CultureInfo.InvariantCulture)}")
    End Sub

    ' ============ UI-test-harness hooks (docs/spec-ui-test-harness.md section 2) ============
    ' harness.json beside the exe (git-ignored; harness.example.json documents it), read ONCE at
    ' startup. Absent / unreadable / enabled=false = the KeyDown handler is never even added and
    ' KeyPreview stays False - byte-identical behavior to before this hook existed.
    Private Sub InitialiseHarnessHooks()
        Try
            Dim cfgPath As String = Path.Combine(AppContext.BaseDirectory, "harness.json")
            If Not File.Exists(cfgPath) Then Return
            Dim enabled As Boolean =
                If(JObject.Parse(File.ReadAllText(cfgPath)).SelectToken("enabled")?.ToObject(Of Boolean)(), False)
            If Not enabled Then Return
            Me.KeyPreview = True
            AddHandler Me.KeyDown, AddressOf OnHarnessScreenshotHotkey
            AppendColoredText(txtLogs, "[HARNESS] hooks enabled (Ctrl+Shift+S full-form screenshot)", Color.Gray)
        Catch ex As Exception
            ' A malformed harness.json must never break startup - report and stay dormant.
            AppendColoredText(txtLogs, $"[HARNESS] harness.json ignored: {ex.Message}", Color.Gray)
        End Try
    End Sub

    ' Ctrl+Shift+S: full-form capture via DrawToBitmap (renders the complete form, including
    ' regions clipped off-screen - the engine handoff section 3b technique). The output path
    ' comes from the marker file verify\.screenshot-target beside the exe; the marker is deleted
    ' after the save so the driving script can poll for completion. UI thread only (KeyDown).
    Private Sub OnHarnessScreenshotHotkey(sender As Object, e As KeyEventArgs)
        If Not (e.Control AndAlso e.Shift AndAlso e.KeyCode = Keys.S) Then Return
        e.Handled = True
        e.SuppressKeyPress = True
        Try
            Dim marker As String = Path.Combine(AppContext.BaseDirectory, "verify", ".screenshot-target")
            If Not File.Exists(marker) Then
                AppendColoredText(txtLogs, "[HARNESS] screenshot hotkey: no verify\.screenshot-target marker", Color.Gray)
                Return
            End If
            Dim target As String = File.ReadAllText(marker).Trim()
            If target.Length = 0 Then Return
            Dim outDir As String = Path.GetDirectoryName(target)
            If Not String.IsNullOrEmpty(outDir) AndAlso Not Directory.Exists(outDir) Then
                Directory.CreateDirectory(outDir)
            End If
            Using bmp As New Bitmap(Me.Width, Me.Height)
                Me.DrawToBitmap(bmp, New Rectangle(0, 0, Me.Width, Me.Height))
                bmp.Save(target, Drawing.Imaging.ImageFormat.Png)
            End Using
            File.Delete(marker)
            AppendColoredText(txtLogs, $"[HARNESS] screenshot → {target}", Color.Gray)
        Catch ex As Exception
            AppendColoredText(txtLogs, $"[HARNESS] screenshot failed: {ex.Message}", Color.Gray)
        End Try
    End Sub

    Private Sub frmMainPageV2_Shown(sender As Object, e As EventArgs) Handles MyBase.Shown
        ' (Secrets now load at Load - see frmMainPageV2_Load - so the headless indicator engine
        ' and the window-title environment suffix see the selected environment from the start.)
        Try
            ' Initialize trade database
            tradeDatabase = New TradeDatabase()

            ' Subscribe to database events
            AddHandler tradeDatabase.DatabaseError, AddressOf OnDatabaseError
            AddHandler tradeDatabase.DatabaseInfo, AddressOf OnDatabaseInfo   ' item 14a
            AddHandler tradeDatabase.TradeRecorded, AddressOf OnTradeRecorded
            AddHandler tradeDatabase.TradeDeleted, AddressOf OnTradeDeleted

            AppendColoredText(txtLogs, "Trade database initialized successfully", Color.LimeGreen)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Failed to initialize trade database: {ex.Message}", Color.Red)
        End Try

        ' Signal-bridge tie-in (docs/spec-autotrade-tiein.md section 3c): construct the consumer.
        ' Starts in mode Off (nothing watches, nothing places) until the SIGNAL BRIDGE panel drives it.
        Try
            signalBridge = New SignalBridge(Me, AddressOf BridgeLog)
            ' This form owns the settings window now (retirement) - hand the bridge to its panel directly.
            If _autotradesettings IsNot Nothing Then _autotradesettings.Bridge = signalBridge
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Signal bridge init failed: {ex.Message}", Color.Red)
        End Try
    End Sub

    ' Bridge log sink: prefixes + routes to the main log. AppendColoredText self-marshals and is
    ' handle-guarded, so the bridge may call this from any thread.
    Private Sub BridgeLog(msg As String, c As Color)
        AppendColoredText(txtLogs, "[BRIDGE] " & msg, c)
    End Sub

    ' Event handlers
    Private Sub OnDatabaseError(message As String)
        AppendColoredText(txtLogs, $"Database Error: {message}", Color.Red)
    End Sub

    ' item 14a: success/info path raised by DatabaseInfo (green, not red).
    Private Sub OnDatabaseInfo(message As String)
        AppendColoredText(txtLogs, $"Database: {message}", Color.LimeGreen)
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
        Else
            'txtLogs.AppendText("WebSocket authorized successfully" + Environment.NewLine)
            AppendColoredText(txtLogs, "WebSocket authorized successfully", Color.DodgerBlue)

            Dim refreshTokenToken = json.SelectToken("result.refresh_token")
            If refreshTokenToken IsNot Nothing Then
                refreshToken = refreshTokenToken.ToString()
            Else
                Throw New Exception("Refresh token not found in response")
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
                Alert("connection") ' item D: disconnect

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
                Alert("connection") ' item D: reconnect-failure

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
                New Uri(AppSecrets.WsUrl),
                combined.Token)
            End Using
        End Using

        ' Environment self-documentation (harness spec section 6.2): say out loud when this
        ' connection is against the test exchange.
        If AppSecrets.IsTestnet Then
            AppendColoredText(txtLogs, "TESTNET environment — test.deribit.com", Color.LimeGreen)
        End If

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

                    ' Complete the task with conservative defaults on error.
                    ' item 14c: TrySetResult — a late response after timeout no longer throws.
                    If accountSummaryTaskCompletionSource IsNot Nothing Then
                        accountSummaryTaskCompletionSource.TrySetResult(New RateLimitInfo With {
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

                    ' Complete the waiting task. item 14c: TrySetResult — late response safe.
                    If accountSummaryTaskCompletionSource IsNot Nothing Then
                        accountSummaryTaskCompletionSource.TrySetResult(rateLimitInfo)
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
    ' Ids 3/999/777 already log their own errors in their handlers; code 10028 is owned by
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
                messageId.Value = 777) Then Return
            If messageId.HasValue AndAlso messageId.Value >= PlacementIdBase Then Return ' HandlePlacementResponse owns placements

            Dim errorCode = errorField.SelectToken("code")?.ToObject(Of Integer)()
            If errorCode.HasValue AndAlso errorCode.Value = 10028 Then Return ' HandleRateLimitError owns 10028

            Dim errorMessage = errorField.SelectToken("message")?.ToString()

            ' Expected chase race: a triggered-SL / trailing edit (order-edit ids 223344-223350, see
            ' RequestNameForId) can reach the exchange just after the limit has filled - the order is gone,
            ' so the edit is rejected "already_closed". This is benign (the fill already closed the leg,
            ' usually at a better price than the chase target), so downgrade it from a red API ERROR to a quiet
            ' gray note - still logged for visibility, just not alarming. Any OTHER edit error stays loud below.
            If messageId.HasValue AndAlso messageId.Value >= 223344 AndAlso messageId.Value <= 223350 _
               AndAlso errorMessage IsNot Nothing _
               AndAlso errorMessage.IndexOf("already_closed", StringComparison.OrdinalIgnoreCase) >= 0 Then
                AppendColoredText(txtLogs, $"Order edit skipped (id {messageId.Value}): already filled/closed - benign chase race", Color.Gray)
                Return
            End If

            ' Expected ABORT race (owner testnet 2026-07-18, spec-back-execution-ergonomics-runtime.md
            ' item 1): the SCOPED entry cancel (id 31, a private/cancel by order_id) can reach the
            ' exchange just after the entry filled - the order is no longer open, so it comes back
            ' not_open_order / order_not_found. This is benign and EXPECTED whenever the ATR-slippage
            ' guard races a fast fill: the fill simply won, the position is real and its legs are live.
            ' Same treatment as the 223344-223350 already_closed chase race above - still logged, just
            ' not alarming. Deliberately NARROW: only id 31 (the by-id cancel that can produce this),
            ' never the nuclear id-30 path, and any OTHER id-31 error stays loud below.
            If messageId.HasValue AndAlso messageId.Value = 31 _
               AndAlso errorMessage IsNot Nothing _
               AndAlso (errorMessage.IndexOf("not_open_order", StringComparison.OrdinalIgnoreCase) >= 0 _
                        OrElse errorMessage.IndexOf("order_not_found", StringComparison.OrdinalIgnoreCase) >= 0) Then
                AppendColoredText(txtLogs, "Entry cancel skipped (id 31): order already filled - benign abort race (the fill won)", Color.Gray)

                ' Raced-abort repair (spec-back-execution-ergonomics-runtime.md item 2): this response
                ' is the exchange's authoritative "the cancel LOST - the entry filled". The scoped
                ' cancel's optimistic teardown has already zeroed the entry/TP/trigger displays, and
                ' cancelPending is suppressing the very echoes that would repopulate them - while the
                ' 'cancelled' echo that normally clears the gate will never arrive (the order filled).
                ' Repair, in order:
                '  1. Clear the cancel gate NOW. Safe by socket ordering (coordinator note, 2026-07-20):
                '     this error is a response on the same WebSocket as the order echoes and Deribit
                '     delivers in order, so every echo of the raced entry (open -> filled) has already
                '     been processed - there is no lagging pre-fill echo left to slip through. The
                '     clear must precede the dispatch below: HandleOpenOrdersSnapshot returns early
                '     while cancelPending is set.
                cancelPending = False
                cancelPendingSince = DateTime.MinValue
                '  2. Re-arm the restore announce. The raced state IS the restore state (position, no
                '     working entry, placedPrice = 0): ProcessPositionData's once-per-connection
                '     announce block is the ONLY writer of txtPlacedPrice/placedPrice in that state
                '     (the id-778 snapshot deliberately never seeds order-context without an open
                '     entry), so without this reset the Entry Buy box stays 0 for the position's life.
                positionRestoreAnnounced = False
                '  3. Re-sync from the exchange - never hand-write prices here. The id-778 snapshot
                '     repopulates the TP/trigger displays and the POSITION leg ids only: it assigns
                '     CurrentOpenOrderId/CurrentTPOrderId/CurrentSLOrderId solely when an entry leg is
                '     order_state "open", and ours is FILLED - so the working-entry context (and the
                '     bridge's HasWorkingEntryOrder gate) structurally cannot be re-armed. Fire-and-
                '     forget off the receive thread (rate-limiter re-init pattern); GetLivePositionData
                '     self-debounces via isRequestingLiveData (no new limiter).
                AppendColoredText(txtLogs, "Entry filled before the cancel landed - re-syncing order/position state from exchange", Color.Yellow)
                Dim _resync = Task.Run(Async Function()
                                           Await RequestOpenOrdersSnapshot()
                                           Await GetLivePositionData("BTC-PERPETUAL")
                                       End Function)
                Return
            End If

            Dim errorData = errorField.SelectToken("data")?.ToString(Newtonsoft.Json.Formatting.None)

            AppendColoredText(txtLogs,
                $"API ERROR (id {If(messageId?.ToString(), "-")}{RequestNameForId(messageId)}): " &
                $"code {If(errorCode?.ToString(), "?")} - {errorMessage}" &
                $"{If(errorData IsNot Nothing, " | " & errorData, "")}",
                Color.Red)

            ' N1b (docs/spec-sl-backoff-coupling.md commit 2; coordinator ruling 2026-07-28, option (a)):
            ' couple a GENUINE SL-edit failure into the retry backoff. This is the FIRST reachable
            ' trigger the backoff has ever had - both pre-existing call sites are dead (the send site
            ' swallows on all three catch arms, and the awaited edit body is itself fully wrapped), so
            ' the failure counter has been 0 for the life of the app.
            ' SL ids ONLY: 223346 (pre-fill secondary SL + the manual SL button), 223348 (trailing SL),
            ' 223350 (the triggered-SL chase - the one path the throttle this feeds actually gates).
            ' The rest of the edit-id block is deliberately EXCLUDED: entry-main / TP re-anchor, manual
            ' TP, trailing main and reduce-reposition are not SL edits and must never throttle the SL
            ' chase - the TP re-anchor in particular fires post-fill, while the chase is live.
            ' Placement is load-bearing: hanging this on the RED emission is what excludes the benign
            ' races - the already_closed chase race and the id-31 abort race both Return above, so a
            ' fill WINNING a race can never be miscounted as an edit failing. (A 10028 also returns
            ' earlier, to its own owner, so the rate-limit class does not couple here.)
            ' A CONFIRMED success resets the counter - N1c moved that reset off the chase's send
            ' completion (which ran on rejected edits too, pinning the counter at 0<->1) and onto the
            ' commanded-price echo branch in HandleOrderPositionUpdates; search "confirmed success
            ' clears the backoff". Do not add a second reset. Receive-thread write to the same
            ' lock-free engine fields their existing post-await writers already use - no new thread
            ' class, no new lock.
            If messageId.HasValue AndAlso
               (messageId.Value = 223346 OrElse messageId.Value = 223348 OrElse messageId.Value = 223350) Then
                Dim failedAt As DateTime = DateTime.UtcNow
                BackoffStopLossRetry(failedAt)
                ' N1c commit 2 (3b(ii)): make the climb legible the first time SL edits fail for real.
                ' The delay is read back OUT of the stamp the call just wrote (stamp = failedAt +
                ' backoff - one interval) rather than re-derived from the formula, so this line can
                ' never disagree with the gate it is describing. Log delegate only - no UI touch, safe
                ' on the receive thread like the red API ERROR line above.
                Dim backoffMs As Double = (lastStopLossUpdate - failedAt).TotalMilliseconds + MinStopLossUpdateInterval
                AppendColoredText(txtLogs,
                    $"SL-edit failure #{slUpdateFailures} - backoff {backoffMs / 1000:F1}s", Color.Gray)
            End If
        Catch
            ' Parse noise / unexpected shapes: ignore, like the other handlers.
        End Try
    End Sub

    ' Best-effort request-class hint for the ad-hoc id space (see CODE_AUDIT_FABLE5.md F15).
    Private Function RequestNameForId(messageId As Integer?) As String
        If Not messageId.HasValue Then Return ""
        Select Case messageId.Value
            Case 2 : Return " auth"
            Case 30 : Return " cancel-all/trailing stop"
            Case 31 : Return " scoped entry cancel"
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

            If entry.TimedOut Then
                ' Late response after the ack timed out: LOG ONLY. No rollback (a newer placement's
                ' state may be live - restoring old snapshots could clobber it), no TCS completion.
                Dim lateErr = json.SelectToken("error")
                AppendColoredText(txtLogs,
                    $"LATE placement response (id {messageId.Value}, after ack timeout): " &
                    If(lateErr IsNot Nothing, $"REJECTED code {lateErr.SelectToken("code")} - {lateErr.SelectToken("message")}", "accepted"),
                    Color.Orange)
                Return
            End If

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
                Alert("order_rejected") ' item D
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

                UiInvoke(Sub()
                             '              txtLogs.AppendText("REQ. received." + Environment.NewLine)
                             radHeartBeat.BackColor = Color.Crimson
                         End Sub)

                Await SendWebSocketMessageAsync("{""jsonrpc"":""2.0"",""id"":4,""method"":""public/test"",""params"":{}}")

                Await Task.Delay(500)

                UiInvoke(Sub()
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

                ' Default comms from the PERSISTED taker schedule (taker_fee_bps), not a hardcoded
                ' rate - docs/spec-fee-comms-repoint.md. Same shape as before: computed here, used
                ' below only when the price parses (a non-numeric price throws into the Catch, as it
                ' always has).
                comms = DefaultCommsFromTakerBps(takerFeeBpsVal, indexPrice)

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
    ' MinPriceMovementThreshold ($5 leeway) removed by SL-chase v2 - the triggered-SL chase now uses the
    ' entry chase's 1-tick best-non-crossing gate (ChaseTickUSD), not a distance gate.
    Private newPricePublic As Decimal = 0 'For storing the price during emergency reduce market order for logging

    ' #4 retry-amplifier fix: bounded backoff for a failed triggered-SL reposition. The old code reset
    ' lastStopLossUpdate = DateTime.MinValue on error, which cleared the throttle so a persistent failure
    ' retried on EVERY quote tick (amplifying the edit storm). Instead, escalate the next-allowed time
    ' (capped at 5s) by pushing lastStopLossUpdate forward; a success resets the counter.
    Private slUpdateFailures As Integer = 0
    Private Const SLUpdateMaxBackoffMs As Double = 5000

    ' N1b closing commit (docs/spec-sl-backoff-coupling.md §Acceptance 5, the 3b(i) extraction): the
    ' backoff arithmetic as a PURE seam so OrderCheck can pin it and prove the chase-path behaviour
    ' deterministically, with no trade and no testnet. Same math, same constants, same clamp as before
    ' the extraction - the caller below owns the two field writes and nothing else moved.
    ' Contract: (failures, failedAt) -> (newFailures, stamp). The counter clamps at 8; the delay is
    ' Min(333 * 2^n, 5000) ms; the stamp is failedAt + backoffMs - 333, because the gate downstream is
    ' (now - lastStopLossUpdate) >= 333, so subtracting one interval makes the next attempt land at
    ' exactly failedAt + backoffMs. Pure; Friend Shared; OrderCheck-pinned.
    Friend Shared Function NextSlBackoff(failures As Integer, failedAt As DateTime) As (Failures As Integer, Stamp As DateTime)
        Dim n As Integer = Math.Min(failures + 1, 8)
        Dim backoffMs As Double = Math.Min(MinStopLossUpdateInterval * (2 ^ n), SLUpdateMaxBackoffMs)
        Return (n, failedAt.AddMilliseconds(backoffMs - MinStopLossUpdateInterval))
    End Function

    Private Sub BackoffStopLossRetry(failedAt As DateTime)
        Dim next_ = NextSlBackoff(slUpdateFailures, failedAt)
        slUpdateFailures = next_.Failures
        lastStopLossUpdate = next_.Stamp
    End Sub

    ' #5: single-flight guard for the order-reposition section of HandleQuoteUpdates. 0 = idle, 1 = a
    ' reposition is awaiting (Interlocked). Mirrors isReconnecting. Does NOT cover the triggered-SL
    ' emergency block or the price/PnL labels - those run every tick.
    Private isRepositioning As Integer = 0

    ' SL-chase v2 (docs/spec-sl-chase-v2.md §3): DEDICATED single-flight for the triggered-SL chase
    ' EXECUTE section only (NOT isRepositioning - the SL chase must never wedge against the entry/reduce
    ' chases, or vice versa). lastStopLossUpdate advances only on success, so without this a second quote
    ' tick can pass the 333 ms gate while a send's Await is in flight and dispatch a duplicate edit. The
    ' full-emergency market-stop stays OUTSIDE this flag - it must never be blocked by an in-flight chase.
    Private isSLRepositioning As Integer = 0

    ' Single-flight (docs/spec-placement-single-flight.md): 0 = idle, 1 = a user-actuated placement is
    ' awaiting. ONE latch shared by all six placement buttons (btnLimit/btnNoSpread/btnTrail/btnMarket
    ' entries + btnReduceLimit/btnReduceMarket exits) - two DIFFERENT placement buttons pressed inside
    ' the same window is the same hazard as one pressed twice, so a per-button latch would leave that
    ' open. Origin: docs/investigation-triple-placement-2026-08-01.md - the buttons stay enabled across
    ' the await, so N actuations from ANY source (double-click, UIA, input replay) produced N orders at
    ' one cached BestPrice. Every take is paired with a Finally release: a leaked latch would silently
    ' disable placement for the whole session, which is worse than the defect being fixed.
    ' Deliberately NOT taken by the automated paths: the bridge act path (PlaceAutomatedOrder) is
    ' already serialised, and the emergency stop reaches SendReduceMarketOrderAsync directly - it must
    ' never be blocked by an in-flight manual placement.
    Private isPlacingOrder As Integer = 0

    ' Debounce (docs/spec-placement-single-flight-v2.md): the latch above is necessary but NOT
    ' sufficient. Acceptance 2 showed the duplicate actuations are dispatched SEQUENTIALLY by the
    ' message pump - each handler runs to completion, releasing in its Finally, before the next
    ' queued click is dispatched - so mutual exclusion never sees them coincide. Three actuations in
    ' 26 ms still produced three entries with the latch verifiably in the running binary. It is a
    ' RATE problem, not an overlap problem, so the guard has to be time-based.
    ' 500 ms is the Windows default double-click time: the threshold below which the OS itself
    ' treats two clicks as one gesture rather than two intents (owner ruling, v2 §2 - do not
    ' re-tune without a new ruling, and note that below ~300 ms it stops catching real
    ' double-clicks). The cost is that a deliberate second placement inside the window is silently
    ' dropped; the owner ruled that trade acceptable for this workflow.
    ' Stamped on ADMISSION ONLY, never on rejection: had a rejected actuation refreshed the stamp,
    ' a stuck or repeating button would extend the lockout for as long as the input persisted.
    ' Stamping only what we admit bounds the lockout at exactly one window.
    ' ONE shared stamp for all six buttons, mirroring the shared latch - two DIFFERENT placement
    ' buttons inside the window is the same hazard as one pressed twice.
    ' Deliberately NOT applied inside SendReduceMarketOrderAsync or ExecuteOrderAsync, only in the
    ' six handlers: an emergency reduce arriving just after a manual placement must never be
    ' swallowed. That would be a far worse defect than the one this fixes.
    Private Const PlacementDebounceMs As Integer = 500
    Private lastPlacementAdmittedUtc As DateTime = DateTime.MinValue

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
                    UiInvoke(Sub()
                                 txtTopBid.Text = BestBidPrice.ToString("F2")
                             End Sub)
                End If

                If bestAsk IsNot Nothing Then
                    BestAskPrice = bestAsk
                    UiInvoke(Sub()
                                 txtTopAsk.Text = BestAskPrice.ToString("F2")
                             End Sub)
                End If

                ' Item C (journal enrichment): track the raw price extremes since entry - two guarded
                ' compares per tick, engine fields only, no allocation (spec hot-path budget). The
                ' maePrice = 0 arm reseeds after a restart-restore, where no flat->nonzero transition
                ' was observed. Direction is applied at close (CompletePositionClose).
                If positionSizeUSD <> 0D Then
                    If BestBidPrice > 0D AndAlso (maePrice = 0D OrElse BestBidPrice < maePrice) Then maePrice = BestBidPrice
                    If BestAskPrice > mfePrice Then mfePrice = BestAskPrice
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

                'Entry-chase v2 (docs/spec-entry-chase-v2.md §2): chase the ENTRY to the most aggressive
                'NON-CROSSING price - one tick inside the opposite side (preserves the NoSpread edge).
                'While our buy rests at placedPrice the ask is always above it (a crossing ask would have
                'filled us), so chaseTarget >= placedPrice and ">" is inherent one-tick hysteresis.
                'Time-throttled (EntryChaseMinIntervalMs) instead of the old $3 distance gate: bounded
                'request rate in a fast tape, and no resting $2.99 behind top of book on a quiet one.
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
                            Dim chaseTarget As Decimal? = bestAsk - ChaseTickUSD
                            If placedPriceValid AndAlso bestBid IsNot Nothing AndAlso chaseTarget > placedPrice _
                               AndAlso (DateTime.UtcNow - lastEntryChaseUtc).TotalMilliseconds >= EntryChaseMinIntervalMs Then
                                ' Add null check for rateLimiter; HasHeadroom(4) reserves credits so the
                                ' chase can never starve a nuclear cancel / emergency path (§6).
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() AndAlso rateLimiter.HasHeadroom(4) Then
                                    'Stop if repositioned past ATR slippage threshold (guard input stays the
                                    'raw own-side quote - it measures market drift, not our limit price)
                                    ' EV chase budget §2: the ATR cap and the EV floor are the two arms of one
                                    ' abort decision now - ChaseAbortReason names whichever binds first (Nothing
                                    ' = keep chasing). Same arm switch, same guard input, ATR evaluated first.
                                    Dim abortReason As String = If(maxSlippageATRchecked, ChaseAbortReason(bestBid, "LONG"), Nothing)
                                    If abortReason IsNot Nothing Then
                                        Await CancelWorkingEntryCoreAsync(abortReason)
                                        'Return
                                    Else
                                        ' Entry-chase v2 §4 (EntryOnlyChase, owner-ruled default ON): within the
                                        ' drift bound move ONLY the entry leg (1 edit); beyond it re-anchor the
                                        ' whole bracket (3 edits) and advance the legs' anchor.
                                        If EntryOnlyChase AndAlso Math.Abs(chaseTarget.Value - legAnchorPrice) < legReanchorDriftMax Then
                                            Await UpdateEntryOrderOnlyAsync(chaseTarget)
                                        Else
                                            Await UpdateLimitOrderWithOTOCOAsync(chaseTarget)
                                            legAnchorPrice = chaseTarget
                                        End If

                                        ' Runaway fix: advance engine state SYNCHRONOUSLY before the (non-blocking)
                                        ' display update, so the next tick's "chaseTarget > placedPrice" reads the
                                        ' new price even if the textbox write is delayed/fails.
                                        If placedPrice > 0 Then
                                            AppendColoredText(txtLogs, $"Order repositioned: ${placedPrice:F2} → ${chaseTarget:F2}", Color.Yellow)
                                        End If

                                        placedPrice = chaseTarget
                                        lastEntryChaseUtc = DateTime.UtcNow
                                        UiInvoke(Sub() txtPlacedPrice.Text = chaseTarget)
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
                            ' SHORT mirror: most aggressive non-crossing ask = one tick above the bid.
                            Dim chaseTarget As Decimal? = bestBid + ChaseTickUSD
                            If placedPriceValid AndAlso bestAsk IsNot Nothing AndAlso chaseTarget < placedPrice _
                               AndAlso (DateTime.UtcNow - lastEntryChaseUtc).TotalMilliseconds >= EntryChaseMinIntervalMs Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() AndAlso rateLimiter.HasHeadroom(4) Then
                                    ' EV chase budget §2: ATR cap OR EV floor, whichever binds first.
                                    Dim abortReason As String = If(maxSlippageATRchecked, ChaseAbortReason(bestAsk, "SHORT"), Nothing)
                                    If abortReason IsNot Nothing Then
                                        Await CancelWorkingEntryCoreAsync(abortReason)
                                        'Return
                                    Else
                                        ' Entry-chase v2 §4: entry-only within the drift bound, full bracket beyond.
                                        If EntryOnlyChase AndAlso Math.Abs(chaseTarget.Value - legAnchorPrice) < legReanchorDriftMax Then
                                            Await UpdateEntryOrderOnlyAsync(chaseTarget)
                                        Else
                                            Await UpdateLimitOrderWithOTOCOAsync(chaseTarget)
                                            legAnchorPrice = chaseTarget
                                        End If

                                        ' Runaway fix: advance engine state synchronously before the display mirror.
                                        If placedPrice > 0 Then
                                            AppendColoredText(txtLogs, $"Order repositioned: ${placedPrice:F2} → ${chaseTarget:F2}", Color.Yellow)
                                        End If

                                        placedPrice = chaseTarget
                                        lastEntryChaseUtc = DateTime.UtcNow
                                        UiInvoke(Sub() txtPlacedPrice.Text = chaseTarget)
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
                        ' Entry-chase v2 §5: same best-non-crossing target + time throttle as the entry
                        ' chase, on the reduce chase's own stamp (lastReduceChaseUtc) so it is never
                        ' starved by entry-chase edits. Direction from reduceOrderIsBuy, NEVER TradeMode.
                        If reduceOrderIsBuy Then
                            ' Closing a short: reduce BUY - most aggressive non-crossing bid, chase up
                            Dim chaseTarget As Decimal? = bestAsk - ChaseTickUSD
                            If chaseTarget > reduceOrderPrice _
                               AndAlso (DateTime.UtcNow - lastReduceChaseUtc).TotalMilliseconds >= EntryChaseMinIntervalMs Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    rateLimiter.ConsumeCredits()
                                    Await SendReduceRepositionEdit(chaseTarget)
                                    lastReduceChaseUtc = DateTime.UtcNow
                                End If
                            End If
                        Else
                            ' Closing a long: reduce SELL - most aggressive non-crossing ask, chase down
                            Dim chaseTarget As Decimal? = bestBid + ChaseTickUSD
                            If chaseTarget < reduceOrderPrice _
                               AndAlso (DateTime.UtcNow - lastReduceChaseUtc).TotalMilliseconds >= EntryChaseMinIntervalMs Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    rateLimiter.ConsumeCredits()
                                    Await SendReduceRepositionEdit(chaseTarget)
                                    lastReduceChaseUtc = DateTime.UtcNow
                                End If
                            End If
                        End If
                    Finally
                        Interlocked.Exchange(isRepositioning, 0)
                    End Try
                End If

                'For keeping triggered stop loss order at top of orderbook.
                ' Cross-thread fix #5: also gate on a live socket so SL edits aren't piled into a closing connection.
                ' SL-chase v2 (§3): Not IsCancelPending() - the documented gate-ordering invariant (cancel check
                ' BEFORE any reposition work) so a chase edit can't race a nuclear cancel and burn a wasted edit.
                If IsWebSocketConnected AndAlso (Not IsCancelPending()) AndAlso SLTriggered AndAlso PositionSLOrderId IsNot Nothing Then
                    Dim currentTime As DateTime = DateTime.UtcNow

                    ' Cross-thread fix: read the engine field, not txtPlacedStopLossPrice. placedStopLossPrice
                    ' is set at placement, from the exchange, and after each SL reposition below.
                    ' N1 hoist: read here (above the throttle) so the emergency check below and the chase
                    ' machinery further down share the one read of the chase reference.
                    Dim currentStopPrice As Decimal = placedStopLossPrice

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

                    If currentStopPrice > 0 Then
                        If TradeMode Then
                            priceMovement = emgBaseline - bestAsk
                        Else
                            priceMovement = bestBid - emgBaseline
                        End If

                        ' Call ForceStopLossUpdate if emergency conditions are met
                        ' N1 emergency hoist (docs/spec-emergency-hoist.md): this THRESHOLD CHECK used to sit
                        ' inside the MinStopLossUpdateInterval gate below. CORRECTED FRAMING (2026-07-28,
                        ' docs/spec-sl-backoff-coupling.md commit 1): the old comment claimed that cost up to
                        ' SLUpdateMaxBackoffMs (~5 s) of emergency-detection delay via BackoffStopLossRetry.
                        ' That was FALSE. Pre-N1b the backoff was never reachable - the send site swallows every
                        ' exception and the awaited edit body is itself fully wrapped - so the failure counter
                        ' stayed at zero and this gate was a flat 333 ms. The REAL pre-hoist exposure
                        ' was <= one throttle interval (333 ms), plus the latent double-fire race the N1
                        ' single-fire latch below closes. The hoist is still correct, and now load-bearing: N1b
                        ' (same doc) couples genuine SL-edit failures into the backoff, and N1c
                        ' (docs/spec-sl-backoff-confirmed-reset.md) makes them ESCALATE - a persistently failing
                        ' chase now walks 666 ms -> 5 s instead of oscillating at 666. This check is already out
                        ' from under that gate: it runs on EVERY qualifying tick, and only the SL-edit machinery
                        ' below is delayed.
                        ' Hoisting changes WHEN we look, not WHAT we respect: every gate is carried over verbatim
                        ' - the block's IsWebSocketConnected / Not IsCancelPending() / SLTriggered /
                        ' PositionSLOrderId gate (the 2026-07-08 owner ruling deliberately leaves the emergency
                        ' gated during the <= 4 s cancel window), currentStopPrice > 0, the M.SL checkbox +
                        ' threshold mirrors, the FROZEN emergencyBaseline anchor, and the comparison direction.
                        ' emergencyFired is the new single-fire latch (set at the dispatch inside
                        ' UpdateStopLossForTriggeredStopLossOrder): without the throttle, consecutive ticks could
                        ' otherwise double-fire the market reduce before the flat echo lands. The exposed window
                        ' is CancelOrderAsync's own send-await inside the fire path - it sets cancelPending /
                        ' SLTriggered = False / emergencyBaseline = 0 only AFTER its send returns, so until then
                        ' this block's gate is still open. Reading the latch HERE (not only at the fire branch)
                        ' additionally stops a tick in that window from re-entering ForceStopLossUpdate and, with
                        ' the fire branch already latched out, falling through to edit the resting SL to the
                        ' own-side touch.
                        If emergencyThresholdValid AndAlso baselineKnown AndAlso priceMovement >= emergencyThreshold Then
                            If marketStopLossChecked AndAlso Not emergencyFired Then
                                Await ForceStopLossUpdate(If(TradeMode, bestAsk, bestBid))
                                Return ' Exit early after emergency update
                            End If
                        End If
                    End If

                    ' Rate limiting: Only update if minimum time has passed.
                    ' Spec item 16 trade-off, CORRECTED (2026-07-28, docs/spec-sl-backoff-coupling.md commit 1):
                    ' the old comment said a persistently failing SL edit pushes lastStopLossUpdate forward and
                    ' delays the SL-EDIT machinery by up to SLUpdateMaxBackoffMs (5 s). The mechanism is real
                    ' but had never fired pre-N1b - no failure path reached BackoffStopLossRetry (the swallowing
                    ' send site + the fully wrapped edit body), so the failure counter stayed at zero and this
                    ' was a flat 333 ms gate. N1b made it reachable and N1c (the confirmed-reset move) makes it
                    ' ESCALATE: a red SL-edit rejection now compounds, up to the 5 s cap, until the exchange
                    ' confirms one of our edits. When it does escalate, only
                    ' the SL-EDIT machinery below is delayed - the N1 hoist (2026-07-27) moved the emergency
                    ' threshold check out of this gate, so it never shares the delay and is evaluated above,
                    ' every tick. The emergency comparison is measured from the FROZEN emergencyBaseline anchor
                    ' (set at the moment the SL first triggers), not from the reposition attempt time.
                    If (currentTime - lastStopLossUpdate).TotalMilliseconds >= MinStopLossUpdateInterval Then

                        If currentStopPrice > 0 Then

                            'Normal conditions operation
                            ' SL-chase v2 (docs/spec-sl-chase-v2.md §2): chase the triggered SL to the most
                            ' aggressive NON-CROSSING price - one tick inside the opposite side - on a 1-tick
                            ' gate, mirroring the entry chase. Replaces the old $5-leeway join-own-side-top.
                            Dim shouldUpdate As Boolean = False
                            Dim newStopPrice As Decimal = 0D

                            If TradeMode Then
                                ' Long exit = resting SELL limit: most aggressive non-crossing ask = one tick above the bid.
                                ' While the sell rests, bestBid < placedStopLossPrice, so chaseTarget <= placedStopLossPrice;
                                ' "<" is inherent one-tick hysteresis (same argument as the entry chase, mirrored).
                                Dim chaseTarget As Decimal? = bestBid + ChaseTickUSD
                                If chaseTarget < currentStopPrice Then
                                    newStopPrice = chaseTarget
                                    shouldUpdate = True
                                End If
                            Else
                                ' Short exit = resting BUY limit: most aggressive non-crossing bid = one tick below the ask.
                                Dim chaseTarget As Decimal? = bestAsk - ChaseTickUSD
                                If chaseTarget > currentStopPrice Then
                                    newStopPrice = chaseTarget
                                    shouldUpdate = True
                                End If
                            End If

                            ' Execute update if conditions are met
                            ' SL-chase v2 (§3): dedicated single-flight around the EXECUTE section only. Without
                            ' it a second quote tick can pass the 333 ms gate while a send's Await is in flight
                            ' (lastStopLossUpdate advances only on success) and dispatch a duplicate edit. The
                            ' Finally guarantees release. The full-emergency block above is deliberately OUTSIDE
                            ' this flag - the market-stop must never be blocked by an in-flight chase edit.
                            If shouldUpdate AndAlso Interlocked.Exchange(isSLRepositioning, 1) = 0 Then
                                Try
                                    ' Check if we should use force update instead of normal rate-limited update
                                    If emergencyThresholdValid AndAlso baselineKnown AndAlso priceMovement >= (emergencyThreshold * 0.5) Then ' 50% of emergency threshold
                                        Await ForceStopLossUpdate(newStopPrice)
                                    Else
                                        Await UpdateStopLossForTriggeredStopLossOrder(newStopPrice)
                                    End If

                                    ' Runaway fix: advance the CHASE reference synchronously before the display mirror.
                                    ' Invariant (2026-07-08, spec-emergency-baseline-fix.md + hybrid; REPLACES "references move
                                    ' together post-trigger"): post-trigger the two references serve different masters and
                                    ' deliberately diverge. placedStopLossPrice is the CHASE reference - it tracks the app's own
                                    ' repositioning (advanced here every tick). emergencyBaseline is the LOSS-CAP anchor - it
                                    ' LATCHES onto the actual top-of-book SL at the FIRST post-trigger reposition (below), then is
                                    ' FROZEN; never advanced by subsequent chases. So the M.SL emergency measures the market
                                    ' against a fixed anchor and fires at anchor +/- M.SL (advancing it every tick made it track
                                    ' the book, disabling the cap: owner #53/#55/#56; anchoring on the pre-settle flip price fired
                                    ' it too early: owner #67).
                                    placedStopLossPrice = newStopPrice
                                    ' Hybrid fix (spec-back-emergency-baseline-hybrid.md, Option 2): capture the actual top-of-book
                                    ' SL as the loss-cap anchor at the FIRST reposition after the trigger flip, then freeze. The flip
                                    ' adopt seeded emergencyBaseline with the trailed/live limit; this is where the SL first reaches
                                    ' the best-non-crossing top of book.
                                    If Not emergencyBaselineSettled Then
                                        emergencyBaseline = newStopPrice
                                        emergencyBaselineSettled = True
                                    End If
                                    UiInvoke(Sub() txtPlacedStopLossPrice.Text = newStopPrice.ToString("F2"))
                                    ' N1c (docs/spec-sl-backoff-confirmed-reset.md commit 1): lastStopLossUpdate STAYS here, on
                                    ' the attempt. It is the anti-duplicate throttle stamp (SL-chase v2 §3 - without it a second
                                    ' tick passes the gate while this send's Await is in flight and dispatches a duplicate edit),
                                    ' NOT a success signal. Only the failure COUNTER's reset moved out: the send site swallows
                                    ' every exception, so this line also runs when the exchange REJECTS the edit, and clearing the
                                    ' counter here pinned it at 0<->1 and the retry backoff at 666 ms for the life of the app. The
                                    ' counter is now cleared where the exchange CONFIRMS one of our own SL edits - the
                                    ' commanded-price echo branch in HandleOrderPositionUpdates (search "confirmed success clears
                                    ' the backoff").
                                    lastStopLossUpdate = currentTime

                                    ' N1c commit 2 (log honesty): "sent", not "repositioned". This line runs on send
                                    ' completion, which the swallowing send site reaches for a rejected edit too - the
                                    ' same optimistic completion that produced the reset defect. No mechanism change:
                                    ' the CONFIRMATION is already visible through the reconcile machinery's own
                                    ' recognition of the echo, so there is deliberately no second "confirmed" line.
                                    AppendColoredText(txtLogs, $"SL reposition sent: ${currentStopPrice:F2} → ${newStopPrice:F2}", Color.Orange)

                                Catch ex As Exception
                                    AppendColoredText(txtLogs, $"Critical SL update failed: {ex.Message}", Color.Red)

                                    ' #4 retry-amplifier fix: bounded backoff instead of DateTime.MinValue (which reset the
                                    ' throttle and retried every tick on a persistent failure, amplifying the storm).
                                    BackoffStopLossRetry(currentTime)
                                Finally
                                    ' SL-chase v2 (§3): release the single-flight even if the send threw.
                                    Interlocked.Exchange(isSLRepositioning, 0)
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



                'Entry-chase v2 (docs/spec-entry-chase-v2.md §2): trailing-entry chase - same
                'best-non-crossing target + time throttle as the entry block (the throttle stamp is
                'shared; only one of the two blocks can own the order context at a time anyway).
                ' #5: same single-flight guard - serialize trailing repositions with entry repositions.
                ' Cross-thread fix #5: also gate on a live socket so edits aren't piled into a closing connection.
                ' Transition-race fix: don't edit a trailing order we're cancelling (gate on Not IsCancelPending()).
                If IsWebSocketConnected _
                   AndAlso (Not IsCancelPending()) _
                   AndAlso ((CurrentOpenOrderId IsNot Nothing) And (CurrentSLOrderId IsNot Nothing) And (isTrailingStopLossPlaced = True)) _
                   AndAlso Interlocked.Exchange(isRepositioning, 1) = 0 Then
                    Try
                        If TradeMode = True Then
                            Dim chaseTarget As Decimal? = bestAsk - ChaseTickUSD
                            If placedPriceValid AndAlso chaseTarget > placedPrice _
                               AndAlso (DateTime.UtcNow - lastEntryChaseUtc).TotalMilliseconds >= EntryChaseMinIntervalMs Then
                                ' Add null check for rateLimiter; HasHeadroom(4) per §6.
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() AndAlso rateLimiter.HasHeadroom(4) Then
                                    ' Owner ruling 2026-07-24 (audit F18 flag closed, spec-breaker-persist-atr7-item8.md
                                    ' R2): own-side convention like the other three reposition gates - LONG measures
                                    ' drift on the BID (was bestAsk, copy/paste drift; guard now trips ~1 tick later).
                                    ' EV chase budget §2: ATR cap OR EV floor, whichever binds first.
                                    Dim abortReason As String = If(maxSlippageATRchecked, ChaseAbortReason(bestBid, "LONG"), Nothing)
                                    If abortReason IsNot Nothing Then
                                        Await CancelWorkingEntryCoreAsync(abortReason)
                                        'Return
                                    Else
                                        ' Entry-chase v2 §4: entry-only within the drift bound; the full 2-edit
                                        ' path (which moves the SL and updates the trigger bookkeeping) beyond it.
                                        If EntryOnlyChase AndAlso Math.Abs(chaseTarget.Value - legAnchorPrice) < legReanchorDriftMax Then
                                            Await UpdateEntryOrderOnlyAsync(chaseTarget)
                                        Else
                                            Await UpdateStopLossForTrailingOrder(chaseTarget)
                                            legAnchorPrice = chaseTarget
                                        End If
                                        placedPrice = chaseTarget
                                        lastEntryChaseUtc = DateTime.UtcNow
                                        UiInvoke(Sub() txtPlacedPrice.Text = chaseTarget)
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
                            ' SHORT mirror: most aggressive non-crossing ask = one tick above the bid.
                            Dim chaseTarget As Decimal? = bestBid + ChaseTickUSD
                            If placedPriceValid AndAlso bestAsk IsNot Nothing AndAlso chaseTarget < placedPrice _
                               AndAlso (DateTime.UtcNow - lastEntryChaseUtc).TotalMilliseconds >= EntryChaseMinIntervalMs Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() AndAlso rateLimiter.HasHeadroom(4) Then
                                    ' EV chase budget §2: ATR cap OR EV floor, whichever binds first.
                                    Dim abortReason As String = If(maxSlippageATRchecked, ChaseAbortReason(bestAsk, "SHORT"), Nothing)
                                    If abortReason IsNot Nothing Then
                                        Await CancelWorkingEntryCoreAsync(abortReason)
                                        'Return
                                    Else
                                        ' Entry-chase v2 §4: entry-only within the drift bound, full 2-edit path beyond.
                                        If EntryOnlyChase AndAlso Math.Abs(chaseTarget.Value - legAnchorPrice) < legReanchorDriftMax Then
                                            Await UpdateEntryOrderOnlyAsync(chaseTarget)
                                        Else
                                            Await UpdateStopLossForTrailingOrder(chaseTarget)
                                            legAnchorPrice = chaseTarget
                                        End If
                                        placedPrice = chaseTarget
                                        lastEntryChaseUtc = DateTime.UtcNow
                                        UiInvoke(Sub() txtPlacedPrice.Text = chaseTarget)
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
                    ' item 15: consolidated colour+text into one UiInvoke per branch (non-blocking).
                    If dispLong Then
                        ' item 14g: [owner-visible] long closes hit the BID, not the ask.
                        PnL = (BestBidPrice - dispBasis) * (dispAmt / dispBasis)
                        Dim pnlColorLong As Color = If(BestBidPrice < dispBasis, Color.Red, Color.Chartreuse)
                        UiInvoke(Sub()
                                     lblPnL.ForeColor = pnlColorLong
                                     lblPnL.Text = PnL.ToString("F2")
                                 End Sub)
                    Else
                        ' Short branch (ask) is correct — leave unchanged.
                        PnL = (dispBasis - BestAskPrice) * (dispAmt / dispBasis)
                        Dim pnlColorShort As Color = If(BestAskPrice > dispBasis, Color.Red, Color.Chartreuse)
                        UiInvoke(Sub()
                                     lblPnL.ForeColor = pnlColorShort
                                     lblPnL.Text = PnL.ToString("F2")
                                 End Sub)
                    End If
                Else
                    UiInvoke(Sub()
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

    ' Ergonomics item C (docs/spec-execution-ergonomics.md): trade-quality trackers. Receive-thread
    ' engine fields only. maePrice/mfePrice are the RAW low/high extremes seen since entry (min bid /
    ' max ask - direction is applied at close, where MAE/MFE become sign-adjusted USD excursions);
    ' plannedStopAtEntry snapshots StopLossTriggerOriginal at the flat->nonzero transition (the
    ' bracket's trigger was recorded at placement); cumFeesBTC accumulates user.changes trades[].fee.
    ' Reset at the flat->nonzero transition and cleared again after each recorded close.
    Private maePrice As Decimal = 0D
    Private mfePrice As Decimal = 0D
    Private plannedStopAtEntry As Decimal = 0D
    Private cumFeesBTC As Decimal = 0D

    ' Q2 (docs/spec-quickwins-notifier-signalcols.md): the signal-tag lifecycle. pending* is STAGED
    ' by the bridge act path (SetPendingSignalTag, beside its SetTradeTargets call) BEFORE the
    ' placement is sent - the entry-fill echo can beat the placement ack, so the tag must already
    ' be staged when the fill lands. Promoted to current* at the flat->nonzero transition (ANY
    ' entry consumes the stage - a manual entry promotes the empty stage and so records NULL),
    ' cleared in both cancel teardowns and on a definitive placement refusal (never on "timeout" -
    ' the order may exist, echoes are the source of truth), and current* is written into the DB row
    ' at CompletePositionClose then cleared with the item-C trackers. Plain engine fields, written
    ' only on the receive/bridge paths that already own the surrounding state.
    Private pendingSignalId As Long = -1
    Private pendingSignalConfidence As String = ""
    Private currentTradeSignalId As Long = -1
    Private currentTradeSignalConfidence As String = ""

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
        Public TimedOut As Boolean                               ' ack timed out; a late response is LOG ONLY
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
                                    Dim avg = p.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                    If avg.HasValue AndAlso avg.Value > 0D Then positionAvgEntry = avg.Value
                                    If Not wasOpen Then
                                        pendingCloseValid = False
                                        ' Item C: this IS the flat->nonzero transition (spec: reset HERE,
                                        ' never duplicate the detection). Seed both extremes at the incoming
                                        ' average entry; snapshot the placed bracket's trigger as the planned
                                        ' stop; zero the fee accumulator (this echo's own trades[] fees are
                                        ' summed AFTER the positions pass, so the entry fee still counts).
                                        maePrice = positionAvgEntry
                                        mfePrice = positionAvgEntry
                                        plannedStopAtEntry = StopLossTriggerOriginal
                                        cumFeesBTC = 0D
                                        ' Q2: promote the staged signal tag to the live position and
                                        ' clear the stage - one entry consumes one tag. A manual
                                        ' entry promotes the EMPTY stage (-1/"") and records NULL.
                                        currentTradeSignalId = pendingSignalId
                                        currentTradeSignalConfidence = pendingSignalConfidence
                                        pendingSignalId = -1
                                        pendingSignalConfidence = ""
                                    End If
                                ElseIf wasOpen Then
                                    positionJustClosed = True
                                End If
                            End If
                        Next
                    End If

                    ' Item C: user.changes carries the fills' trades array, previously ignored -
                    ' accumulate the BTC fees (fee_currency is BTC on this instrument; null-safe).
                    ' Runs AFTER the positions pass so an entry echo's fee lands after the reset,
                    ' and BEFORE CompletePositionClose below so the closing fill's fee is counted.
                    Dim feeTokens = orderData.SelectToken("trades")?.ToObject(Of List(Of JObject))()
                    If feeTokens IsNot Nothing Then
                        For Each t In feeTokens
                            cumFeesBTC += If(t.SelectToken("fee")?.ToObject(Of Decimal?)(), 0D)
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

                                ' Fill-reanchor fix (docs/spec-fill-reanchor-fix.md §3): staged inside the lambda
                                ' (UI thread, where PositionTPOrderId is set), dispatched after it (receive thread -
                                ' the lambda is a Sub and cannot Await). 0-id = nothing staged this echo.
                                Dim tpReanchorFill As Decimal = 0D
                                Dim tpReanchorTarget As Decimal = 0D
                                Dim tpReanchorId As String = Nothing

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

                                                      ' Fill-reanchor fix: this is the live post-fill TP leg. If the entry
                                                      ' chased away from the leg anchor, re-anchor THIS leg to the fill.
                                                      ' Stage the id/prices here; dispatch the edit after the lambda.
                                                      If pendingReanchorFill > 0D Then
                                                          ' Tick-rounding (docs/spec-tick-rounding.md): the fill is average_price and can be
                                                          ' fractional; the whole derivation is rounded (idempotent for on-tick values).
                                                          Dim newTP As Decimal = RoundToTick(If(manualTPval > 0D, manualTPval,
                                                                                    If(TradeMode, pendingReanchorFill + takeProfitOffset,
                                                                                                  pendingReanchorFill - takeProfitOffset)))
                                                          ' Skip a pointless edit when the leg already rests at the target
                                                          ' (e.g. manual-TP mode - the absolute price didn't move).
                                                          If (Not price.HasValue) OrElse Math.Abs(newTP - price.Value) > 0.01D Then
                                                              tpReanchorFill = pendingReanchorFill
                                                              tpReanchorTarget = newTP
                                                              tpReanchorId = orderId
                                                          End If
                                                          pendingReanchorFill = 0D   ' consumed (edit or skip)
                                                      End If
                                                  Case "StopLossOrder"
                                                      PositionSLOrderId = orderId

                                                      ' Reconcile fix + emergency-baseline fix (spec-emergency-baseline-fix.md, 2026-07-08):
                                                      ' emergencyBaseline is the M.SL LOSS-CAP anchor. It is ADOPTED here at the trigger flip
                                                      ' (set to the actual triggered SL price), and moved thereafter ONLY by a detected MANUAL SL
                                                      ' edit (below) and the id-778 restore seed - NEVER by the app's own chase (that chase-advance
                                                      ' was deleted; advancing it tracked the book and disabled the cap - owner #53/#55/#56).
                                                      ' placedStopLossPrice is the separate CHASE reference that DOES track the app's repositioning.
                                                      ' Capture the pre-echo triggered state BEFORE flipping it, so the block can tell the one-time
                                                      ' trigger flip (adopt silently) from a later manual edit (discriminate + log). A TRIGGER-only
                                                      ' move makes the flip price differ from the last untriggered mirror - that must NOT log "manual".
                                                      Dim wasTriggered As Boolean = SLTriggered
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

                                                      ' Triggered-SL reconciliation (spec-reconcile-manual-sl-edits.md, discriminator 4a + policy P1).
                                                      ' All under the existing Not cancelPending gate (scoped/nuclear cancel semantics, invariant #3).
                                                      If Not cancelPending Then
                                                          If Not wasTriggered OrElse emergencyBaseline = 0D Then
                                                              ' Trigger FLIP (untriggered -> triggered), OR the reference isn't adopted yet (emergencyBaseline
                                                              ' still 0, e.g. the flip echo had a null price): adopt the exchange's authoritative triggered SL
                                                              ' price as the post-trigger reference for BOTH the chase and the emergency. This is the
                                                              ' transition, never a manual edit - no discriminator, no log. Covers a TRIGGER-only move
                                                              ' (flip price can differ from the last untriggered mirror) with no false "Manual SL edit", and
                                                              ' guarantees the emergency baseline gets seeded (a 0 baseline disables the M.SL emergency).
                                                              ' The one-time overwrite is safe: no chase is running yet at the flip.
                                                              If price.HasValue Then
                                                                  placedStopLossPrice = price.Value
                                                                  emergencyBaseline = price.Value
                                                                  ' Hybrid fix: the flip price is the SL's live/trailed limit, which may sit BELOW
                                                                  ' top-of-book. Re-arm the latch so the FIRST post-trigger chase reposition captures
                                                                  ' the actual top-of-book SL as the frozen loss-cap anchor (owner #67).
                                                                  emergencyBaselineSettled = False
                                                                  txtPlacedStopLossPrice.Text = price.Value.ToString("F2")
                                                              End If
                                                          ElseIf price.HasValue AndAlso price.Value <> placedStopLossPrice _
                                                                 AndAlso Not IsRecentlyCommandedSLPrice(price.Value) Then
                                                              ' ALREADY triggered + a price we did NOT command + it changed == a genuine manual SL (limit)
                                                              ' edit. P1: follow it - correct the chase reference to the true live SL and keep chasing from
                                                              ' there (the maker fill). Hybrid fix: a manual re-set DEFINES the new loss-cap anchor, so move
                                                              ' emergencyBaseline to it AND freeze (settled = True) - a subsequent chase pullback must not
                                                              ' override the owner's manual cap.
                                                              placedStopLossPrice = price.Value
                                                              emergencyBaseline = price.Value
                                                              emergencyBaselineSettled = True
                                                              txtPlacedStopLossPrice.Text = price.Value.ToString("F2")
                                                              AppendColoredText(txtLogs, $"Manual SL edit: ${price.Value:F2}", Color.Cyan)
                                                          End If
                                                          ' else (already triggered; price in the commanded set or unchanged): the app's own reposition
                                                          ' or a lagging echo of it -> ignore. placedStopLossPrice was advanced at the reposition;
                                                          ' emergencyBaseline is the frozen loss-cap anchor (not touched by the chase). Preserves the
                                                          ' runaway/transition-race protection.

                                                          ' N1c (docs/spec-sl-backoff-confirmed-reset.md commit 1): the SL-edit retry backoff's failure
                                                          ' counter is cleared HERE - on the exchange's CONFIRMATION of an edit we sent, i.e. an open
                                                          ' echo carrying a price the app itself commanded (the 4a discriminator's set). It used to be
                                                          ' cleared at the chase's send completion, which the swallowing send site made unconditional -
                                                          ' a REJECTED edit reset it too - so the counter oscillated 0<->1 and the backoff never
                                                          ' escalated past 666 ms. Only the triggered-SL chase (id 223350) feeds that set, so the chase
                                                          ' is the one coupled id that gains a confirmed reset; the other two (223346 manual/pre-fill SL,
                                                          ' 223348 trailing SL) keep exactly the shared counter they already had. The discriminator
                                                          ' itself is NOT widened - this is an extra POSITIVE test of the same set, deliberately not the
                                                          ' else-arm above: the ordinary success reaches that else via "price unchanged" (the chase
                                                          ' advances placedStopLossPrice optimistically at the send), which short-circuits before the set
                                                          ' is ever consulted. Residual, accepted in the spec: a lost or unrecognised echo leaves the
                                                          ' counter armed, so a later transient failure starts one step escalated - bounded by the 5 s
                                                          ' cap and self-healing on the next confirmed edit.
                                                          ' Thread class: UI thread (this Select runs inside Me.Invoke off the receive loop) writing a
                                                          ' field the receive loop and the chase's post-await continuations already write lock-free -
                                                          ' the same accepted class as N1b's coupling. No new synchronisation.
                                                          If price.HasValue AndAlso IsRecentlyCommandedSLPrice(price.Value) Then
                                                              slUpdateFailures = 0   ' confirmed success clears the backoff
                                                          End If
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
                                                          ' item 12: use mirror fields (manualTPval/tpOffsetVal/commsVal) kept current by SyncTradeInputsFromUi
                                                          ' instead of parsing textboxes — avoids FormatException on blank field.
                                                          If manualTPval > 0 Then
                                                              txtPlacedTakeProfitPrice.Text = txtManualTP.Text
                                                          Else
                                                              If TradeMode = True Then
                                                                  txtPlacedTakeProfitPrice.Text = Decimal.Parse(If(price?.ToString("F2"), "0")) + (tpOffsetVal + commsVal)
                                                              Else
                                                                  txtPlacedTakeProfitPrice.Text = Decimal.Parse(If(price?.ToString("F2"), "0")) - (tpOffsetVal + commsVal)
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

                                ' Fill-reanchor fix (docs/spec-fill-reanchor-fix.md §3): dispatch the staged TP
                                ' re-anchor OUTSIDE the lambda (receive thread here; the lambda is a Sub, cannot
                                ' Await). Targets PositionTPOrderId - the live post-fill leg, not the retired one.
                                If tpReanchorId IsNot Nothing Then
                                    Await ReanchorTPToFillAsync(tpReanchorId, tpReanchorFill, tpReanchorTarget)
                                End If

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
                                        ' Item J (owner-requested 2026-07-17): the log line shows the TRUE
                                        ' volume-weighted fill (the echo's average_price, same read pattern as
                                        ' the fill-reanchor) when present/non-zero, else the order price as
                                        ' before. Display/log-line ONLY: placedPrice / txtPlacedPrice (the
                                        ' chase and order reference) and the DB record are untouched.
                                        Dim entryShownPrice As Decimal = If(order.SelectToken("average_price")?.ToObject(Of Decimal?)(), 0D)
                                        If entryShownPrice <= 0D Then entryShownPrice = placedPrice
                                        ' N2b §3: print the size that actually ENTERED, not the Amount-box mirror. The
                                        ' echo's own amount is the fill's own size - the same message the price above is
                                        ' read from, and the same token ApplyCloseFill uses for the close's size. Falls
                                        ' back to the retained placed size, then the box. (The OpenPositions clear of
                                        ' placedOrderSizeUsd runs after this loop, so the fallback is still live here.)
                                        Dim entryShownSize As Decimal = If(order.SelectToken("amount")?.ToObject(Of Decimal?)(), 0D)
                                        If entryShownSize <= 0D Then entryShownSize = If(placedOrderSizeUsd > 0D, placedOrderSizeUsd, orderAmountVal)
                                        AppendColoredText(txtLogs, $"Position entered: {If(TradeMode, "LONG", "SHORT")} {entryShownSize} @ ${entryShownPrice:F2}", Color.LimeGreen)
                                        Alert("entry_fill") ' item D
                                        OpenPositions = True
                                        OpenOrderNo = False
                                        UpdateFlag = False

                                        ' Fill-reanchor fix (docs/spec-fill-reanchor-fix.md §3): DEFER. At this
                                        ' filled echo the live post-fill TP leg does not exist under its new id yet
                                        ' (the OTOCO fill retires the pre-fill legs), so editing here hits
                                        ' order_not_found. Stage the fill; the post-fill open TakeLimitProfit echo
                                        ' re-anchors the TP leg. The SL leg is a native trailing stop (trigger_offset)
                                        ' - the exchange trails it, so it is deliberately NOT re-anchored.
                                        ' Manual-TP fix (spec-emergency-baseline-fix.md §6b): a manual TP is an ABSOLUTE
                                        ' price (placed correctly, fill-independent) - it must NOT be re-anchored to the
                                        ' fill+offset. Decide here (manualTPval is still intact; the OpenPositions=True
                                        ' block below clears it), so a later open-TP echo can't re-anchor over it after
                                        ' the clear. Gate on manualTPval <= 0 => only auto-offset TPs re-anchor.
                                        Dim entryFillPrice As Decimal = If(order.SelectToken("average_price")?.ToObject(Of Decimal?)(),
                                                                           If(order.SelectToken("price")?.ToObject(Of Decimal?)(), 0D))
                                        If legAnchorPrice <> 0D AndAlso entryFillPrice > 0D AndAlso entryFillPrice <> legAnchorPrice AndAlso manualTPval <= 0D Then
                                            pendingReanchorFill = entryFillPrice
                                        End If
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
                                        ' Item J: same true-average-fill treatment as the EntryLimitOrder
                                        ' branch (display/log-line only).
                                        Dim trailShownPrice As Decimal = If(order.SelectToken("average_price")?.ToObject(Of Decimal?)(), 0D)
                                        If trailShownPrice <= 0D Then trailShownPrice = placedPrice
                                        ' N2b §3: same treatment as the EntryLimitOrder sibling. A trailing bracket is
                                        ' always placed at the box, so this reads identically to before in practice -
                                        ' it is here so the two "Position entered:" lines cannot drift apart.
                                        Dim trailShownSize As Decimal = If(order.SelectToken("amount")?.ToObject(Of Decimal?)(), 0D)
                                        If trailShownSize <= 0D Then trailShownSize = If(placedOrderSizeUsd > 0D, placedOrderSizeUsd, orderAmountVal)
                                        AppendColoredText(txtLogs, $"Position entered: {If(TradeMode, "LONG", "SHORT")} {trailShownSize} @ ${trailShownPrice:F2}", Color.LimeGreen)
                                        Alert("entry_fill") ' item D
                                        OpenPositions = True
                                        OpenOrderNo = False
                                        isTrailingStop = True 'For checking if is trailing order when executing In Position code
                                        isTrailingPosition = False   'For sanity confirm that it is not in position
                                        UpdateFlag = False

                                        ' Fill-reanchor fix (docs/spec-fill-reanchor-fix.md §3): the trailing
                                        ' bracket has no TP leg and its SL is the native trailing stop, so there is
                                        ' nothing to re-anchor here - the old (buggy) SL re-anchor hook was removed.
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
                                        If signalBridge IsNot Nothing AndAlso signalBridge.IsLiveStarted Then
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


                        If (signalBridge IsNot Nothing AndAlso signalBridge.IsLiveStarted) And OrderLog = False Then
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
                            ' N2b: THE fill/OpenPositions transition (spec §2). This is the single site where
                            ' the working-entry id context dies on a fill, and it covers both entry labels
                            ' (EntryLimitOrder and EntryTrailingOrder both set OpenPositions = True above), so
                            ' the retained size dies with the ids it belongs to rather than at two per-label
                            ' echoes. Without this a later MANUAL order could inherit a bridge size.
                            placedOrderSizeUsd = 0D
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

                                        If (signalBridge IsNot Nothing AndAlso signalBridge.IsLiveStarted) And (PositionLog = False) Then
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

    ' Add these variables to your order placement logic
    Private originalSignalPrice As Decimal = 0
    Private orderCreationTime As DateTime = DateTime.MinValue
    Private currentRequoteCount As Integer = 0

    ' The ATR the slippage guard will use, and where it came from. Bridge-first
    ' (docs/spec-autotrade-tiein.md section 3c): the last actionable bridge payload's atr when fresh
    ' (LastSignalAtr is 0 when none/stale), else FrmIndicators' headless CurrentATR, else none.
    ' Both reads are plain fields and the sources are literals, so this stays receive-thread safe and
    ' allocation-free (ValueTuple is a struct). Single source of truth for the guard AND the readout.
    Friend Function GetEffectiveAtr() As (Atr As Decimal, Source As String)
        Dim bridgeAtr As Decimal = If(signalBridge IsNot Nothing, signalBridge.LastSignalAtr, 0D)
        If bridgeAtr > 0D Then Return (bridgeAtr, "signal payload")
        Dim indicatorAtr As Decimal = If(_indicators IsNot Nothing, _indicators.CurrentATR, 0D)
        If indicatorAtr > 0D Then Return (indicatorAtr, "indicator")
        Return (0D, "none")
    End Function

    ' Exposed so the Tooling readout shows exactly what the guard would enforce, not a re-derivation.
    Friend ReadOnly Property CurrentSlippageLimit As Decimal
        Get
            Return CalculateATRSlippageLimit()
        End Get
    End Property

    Private Function CalculateATRSlippageLimit() As Decimal
        ' Cross-thread fix: read the engine fields, not controls.
        Dim eff = GetEffectiveAtr()
        If eff.Atr <= 0D Then
            ' No ATR at all: the fallback IS the limit and is deliberately NOT multiplied - that is
            ' the original "Return 70" behaviour, now configurable from Tooling.
            Return atrFallbackVal
        End If

        ' Get ATR multiplier from settings (blank/0 -> default 0.6x ATR)
        Dim atrMultiplier As Decimal = If(maxSlippageATRmult > 0D, maxSlippageATRmult, 0.6D)

        Return eff.Atr * atrMultiplier
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
            ResetOrderAttempt()

            ' RETIREMENT: LogFailedEntry was removed here - it never ran (this call site was commented
            ' out) and it read the never-assigned _autotradesettings field, so it would have thrown a
            ' NullReferenceException on its first real call. It also stamped a cooloff on an abandoned
            ' entry; if that behaviour is wanted, it belongs on the bridge, not here.
            Return True
        End If

        ' Log acceptable slippage
        'AppendColoredText(txtLogs, $"{direction} slippage ${actualSlippage:F2} ({slippageInATR:F2}x ATR) within limit", Color.Gray)
        Return False
    End Function

    Private Sub ResetOrderAttempt()
        currentRequoteCount = 0
        orderCreationTime = DateTime.MinValue
        originalSignalPrice = 0
    End Sub

    ' EV chase budget §3 (D1, ruled): the target the budget measures the remaining move against is
    ' the TP IN FORCE - manualTPval, which a bridge act always sets (it carries the engine target)
    ' and a manual trade sets when a TP is typed. 0 = no target in force, which makes the predicate
    ' return False: an OFFSET-flow trade keeps the ATR cap alone, exactly as today, because its
    ' target is derived from the chase anchor and an EV check against it would be self-referential.
    Private Function ChaseEvTargetInForce() As Decimal
        Return If(manualTPval > 0D, manualTPval, 0D)
    End Function

    ' EV chase budget §2: the chase-abort reason for the four own-side reposition gates, or Nothing
    ' to carry on chasing. Order is load-bearing - the ATR cap is evaluated FIRST and untouched, so
    ' it keeps owning the originalSignalPrice seeding and its own attempt-reset side effect, and an
    ' ATR trip still logs its existing reason byte-identically. The EV floor is the second, opt-in
    ' arm and carries its own reason (the two literals below are the ONLY place either is written -
    ' the cancel REASON is the counterfactual instrument here, deliberately NOT a second disposition
    ' row; that file is one row per payload, written at consumption).
    ' Both arms stay under the caller's maxSlippageATRchecked switch: housekeeping 8b
    ' made that checkbox the single arm for chase-abort guards and this keeps it that way.
    ' Receive-thread safe: plain fields + the pure predicate, no control reads.
    Private Function ChaseAbortReason(ownSideQuote As Decimal, direction As String) As String
        If IsATRSlippageExcessive(ownSideQuote, direction) Then Return "ATR slippage"
        If IsChaseEvExhausted(ChaseEvTargetInForce(), ownSideQuote, RoundTripFeePct, minNetMovePctVal) Then Return "EV floor"
        Return Nothing
    End Function


    'All order execution code below
    '-----------------------------------------------------------------------
    Private Async Function ExecuteOrderAsync(TypeOfOrder As String, Optional requestId As Integer = 0,
                                             Optional sizeUsdOverride As Decimal = 0D) As Task
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

            ' Session policy size_mult (docs/spec-session-policy-gate.md section 4). 0 = "use the box",
            ' so every manual path reaches here unchanged; only the bridge act path can pass a value.
            ' The box is still validated FIRST - an override must not paper over an empty Amount box.
            ' txtAmount is deliberately NOT written: the multiplier must never mutate the trader's
            ' standing input, so what is on screen after an automated entry is still what they typed.
            If sizeUsdOverride > 0D Then amount = sizeUsdOverride

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
                    ' item 14e: deleted the duplicate ternary that immediately overwrote takeprofitprice

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
                    If maxSlippageATRchecked AndAlso IsATRSlippageExcessive(BestPrice, direction) Then
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
                    If maxSlippageATRchecked AndAlso IsATRSlippageExcessive(BestPrice, direction) Then
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
                    If maxSlippageATRchecked AndAlso IsATRSlippageExcessive(BestPrice, direction) Then
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
                    If maxSlippageATRchecked AndAlso IsATRSlippageExcessive(BestPrice, direction) Then
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
            emergencyFired = False                ' N1: a fresh order re-establishes a clean emergency context too
            ' N2b: retain the size actually SENT - but ONLY when it came from an OVERRIDE (owner veto
            ' 2026-08-01; spec §2 as amended). The distinction the fix needs is WHERE the placed size
            ' came from, not whether one was retained. A manual placement writes 0, so its chase falls
            ' back to the Amount box and editing that box while the order rests STILL resizes the
            ' resting order - a control the owner uses deliberately, because the chase can walk the entry
            ' closer to the TP and they resize in that moment. A resting BRIDGE entry deliberately does
            ' NOT follow a box edit: that is the fix. Cancel it to intervene.
            ' The WRITE stays unconditional, and that is load-bearing: it is what makes a placement
            ' itself a clear, so a stale size from a previous act can never survive into a later manual
            ' order even through a missed teardown. Only the VALUE is conditional - do not weaken this
            ' to a conditional write.
            ' Seeded HERE, beside the sibling placement seeds and AFTER the send, rather than at the
            ' override fold: every early Return between the two (bad quote, ATR-slippage abort,
            ' unsupported type) would otherwise leave a size retained for an order that was never placed.
            placedOrderSizeUsd = If(sizeUsdOverride > 0D, amount, 0D)

            ' Entry-chase v2 §4: the legs' geometry is anchored to this placement price. Bound the
            ' geometry error so a chased entry can never overrun its own TP:
            ' fill <= anchor + driftMax <= anchor + tpOffset/2 < anchor + tpOffset = TP. Same logic
            ' for the trigger side.
            legAnchorPrice = BestPrice
            legReanchorDriftMax = Math.Min(takeProfitOffset, triggerDistance) / 2D
            If legReanchorDriftMax <= 0D Then legReanchorDriftMax = 10D  ' offsets unset (manual-targets mode) - modest default

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
        legAnchorPrice = 0D          ' entry-chase v2: order context dies with placedPrice
        pendingReanchorFill = 0D     ' fill-reanchor fix: drop any un-consumed TP re-anchor
        placedOrderSizeUsd = 0D      ' N2b: nuclear teardown - no working entry, so no retained size

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
        ' Q2: an aborted bridge entry must never tag a later trade (currentTrade* untouched -
        ' a live position's tag survives a nuclear cancel and dies at its close).
        pendingSignalId = -1
        pendingSignalConfidence = ""

        ' Cross-thread fix: CancelOrderAsync runs on both the UI and receive threads; marshal the status
        ' label with the placed-price resets (it was previously written unguarded off the receive thread).
        ' item 15: non-blocking UiInvoke (display-only writes; ordering preserved via the message queue).
        UiInvoke(Sub()
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
        emergencyBaselineSettled = False   ' hybrid fix: clear the loss-cap latch with the triggered-SL context
        ResetCommandedSLPrices() ' reconcile: nuclear cancel - triggered-SL context is gone

        'PositionEmpty = True
        PositionLog = False ' Reset position log flag so it can log next new position
        OrderLog = False ' Reset order log flag so it can log next new order

        ResetOrderAttempt() ' Reset ATR slippage tracking

        'Clearing margin displays (item 15: UiInvoke — display-only)
        UiInvoke(Sub()
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
        legAnchorPrice = 0D          ' entry-chase v2: order context dies with placedPrice
        pendingReanchorFill = 0D     ' fill-reanchor fix: drop any un-consumed TP re-anchor
        placedOrderSizeUsd = 0D      ' N2b: scoped teardown - the working entry is gone, drop its size too
        ' Q2: an aborted bridge entry (ATR-slippage abort routes here) must never tag a later trade.
        pendingSignalId = -1
        pendingSignalConfidence = ""
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

        ' Ergonomics item G (owner-requested 2026-07-14): display-hygiene clear of the dead
        ' bracket's SL for the PROVABLY-FLAT case only. The guard is the load-bearing part -
        ' with any live position / triggered-SL context this must NOT run (HANDOVER-2 §4
        ' invariant 3: those fields may belong to a live position's legs, and a zero can stall
        ' an actively-trailing SL). This is NOT an 8th SL-context reset site:
        ' emergencyBaseline / the commanded set are already 0 on this path from the placement
        ' reset and are deliberately not touched.
        If positionSizeUSD = 0D AndAlso Not SLTriggered AndAlso PositionSLOrderId Is Nothing Then
            placedStopLossPrice = 0D
            UiInvoke(Sub()
                         txtPlacedStopLossPrice.Text = "0"
                         txtPlacedTrigStopPrice.Text = "0" ' idempotent - already cleared above
                     End Sub)
        End If

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
                emergencyBaselineSettled = False   ' hybrid fix: clear the loss-cap latch with the triggered-SL context
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
            ' N2b: a reposition must re-send the size the order was PLACED at, not the Amount box - the box
            ' read here is what silently reverted every risk-sized / size_mult-reduced bridge entry. Same
            ' 0-means-the-box convention as sizeUsdOverride, so a manual placement is byte-identical.
            Dim amount As Decimal = orderAmountVal
            If placedOrderSizeUsd > 0D Then amount = placedOrderSizeUsd
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

            ' Send all three updates with rate limiting. main = primary (post_only OK); TP/SL are pre-fill
            ' OTOCO secondary legs - post_only is REJECTED on their edits (postOnly:=False; see SendRateLimitedUpdate).
            Await SendRateLimitedUpdate("main", CurrentOpenOrderId, newPrice, amount)
            rateLimiter.ConsumeCredits() ' Consume for second call
            Await SendRateLimitedUpdate("takeprofit", CurrentTPOrderId, newTPprice, amount, postOnly:=False)
            rateLimiter.ConsumeCredits() ' Consume for third call
            Await SendRateLimitedUpdate("stoploss", CurrentSLOrderId, newSLprice, amount, newTrigSLprice, postOnly:=False)

            UpdateFlag = True

        Catch ex As Exception
            AppendColoredText(txtLogs, "Error in rate-limited UpdateLimitOrderWithOTOCOAsync: " & ex.Message, Color.Red)
        End Try
    End Function

    ' Entry-chase v2 §4: slim chase path - move ONLY the entry leg (1 matching-engine edit instead of
    ' the bracket's 3). Deliberately does NOT touch StopLossTriggerOriginal, emergencyBaseline, or the
    ' commanded-SL set: the SL leg did not move, so the recorded trigger stays truthful to the order
    ' actually resting on the exchange (wider-than-ideal geometry until re-anchor, never a false record).
    Private Async Function UpdateEntryOrderOnlyAsync(newPrice As Decimal) As Task
        Try
            ' Ensure rate limiter exists
            If rateLimiter Is Nothing Then
                AppendColoredText(txtLogs, "Rate limiter not initialized - creating emergency limiter", Color.Yellow)
                rateLimiter = New DeribitRateLimiter(1000, 50) ' Emergency conservative limiter
            End If

            ' Check rate limit before making the API call
            If Not rateLimiter.CanMakeRequest() Then
                Dim waitTime = rateLimiter.GetWaitTimeMs()
                AppendColoredText(txtLogs, $"Rate limit reached, waiting {waitTime}ms", Color.Yellow)
                Await Task.Delay(waitTime)
            End If

            If Not rateLimiter.ConsumeCredits() Then
                AppendColoredText(txtLogs, "Insufficient credits for entry-only update - skipping", Color.Orange)
                Return
            End If

            ' Cross-thread fix: read engine input fields, never the textboxes (runs on the receive thread).
            ' N2b: same placed-size preservation as the full-bracket path - this is the arm EntryOnlyChase
            ' (default ON) actually takes, so it is the one the runtime 3b failure ran through.
            Dim amount As Decimal = orderAmountVal
            If placedOrderSizeUsd > 0D Then amount = placedOrderSizeUsd
            If amount <= 0D Then
                ' Preserve the old "no edit on a bad amount" behaviour (Decimal.Parse used to throw on blank).
                AppendColoredText(txtLogs, "Order amount blank/zero - skipping order update", Color.Orange)
                Return
            End If

            Await SendRateLimitedUpdate("main", CurrentOpenOrderId, newPrice, amount)

            UpdateFlag = True

        Catch ex As Exception
            AppendColoredText(txtLogs, "Error in UpdateEntryOrderOnlyAsync: " & ex.Message, Color.Red)
        End Try
    End Function

    ' Fill-reanchor fix (docs/spec-fill-reanchor-fix.md): re-anchor the TP leg ONLY, to the actual fill.
    ' Called from the post-fill open TakeLimitProfit echo with that echo's live PositionTPOrderId (the
    ' pre-fill CurrentTPOrderId is retired by the OTOCO fill - the old bug). One edit; the price is derived
    ' by the caller. The SL leg is a native trailing stop (trigger_offset) - deliberately NOT touched, so
    ' this is NOT an SL-context reset site (no StopLossTriggerOriginal/emergencyBaseline/commanded writes).
    ' Runs on the receive thread (after the echo's Me.Invoke): engine fields only, self-marshalling output.
    Private Async Function ReanchorTPToFillAsync(tpOrderId As String, fillPrice As Decimal, newTPprice As Decimal) As Task
        Try
            If rateLimiter Is Nothing Then
                AppendColoredText(txtLogs, "Rate limiter not initialized - creating emergency limiter", Color.Yellow)
                rateLimiter = New DeribitRateLimiter(1000, 50) ' Emergency conservative limiter
            End If

            ' Cross-thread fix: read engine input fields, never the textboxes.
            Dim amount As Decimal = orderAmountVal
            If amount <= 0D Then
                AppendColoredText(txtLogs, "TP re-anchor skipped - amount blank/zero", Color.Orange)
                Return
            End If

            If Not rateLimiter.CanMakeRequest() Then
                AppendColoredText(txtLogs, "TP re-anchor skipped - credits unavailable (TP stays at placement geometry)", Color.Orange)
                Return
            End If

            rateLimiter.ConsumeCredits()
            Await SendRateLimitedUpdate("takeprofit", tpOrderId, newTPprice, amount)
            UpdateFlag = True

            ' Fill-reanchor fix: refresh the TP display to the re-anchored price. The post-fill open
            ' TakeLimitProfit echo does NOT update txtPlacedTakeProfitPrice, so without this the field
            ' stays at the stale placement TP while the live order sits at the re-anchored price
            ' (mirrors how the triggered-SL chase updates txtPlacedStopLossPrice on each reposition).
            UiInvoke(Sub() txtPlacedTakeProfitPrice.Text = newTPprice.ToString("F2"))

            AppendColoredText(txtLogs, $"TP re-anchored to fill ${fillPrice:F2}: ${newTPprice:F2}", Color.Cyan)

        Catch ex As Exception
            AppendColoredText(txtLogs, "Error in ReanchorTPToFillAsync: " & ex.Message, Color.Red)
        End Try
    End Function

    ' post_only bug fix (owner runtime trades #53/#56): Deribit rejects post_only as a private/edit param
    ' on a PRE-fill OTOCO child (secondary) order - code -32602 "post_only not allowed for secondary orders".
    ' It is accepted at placement (otoco_config) and on primary/post-fill/triggered edits. So callers editing
    ' a pre-fill TP/SL leg (UpdateLimitOrderWithOTOCOAsync's full-bracket re-anchor) pass postOnly:=False -
    ' safe, because the leg was created post_only and Deribit preserves flags across an edit that omits them
    ' (trade #45), and the TP/SL sit far from the book so they can't take anyway. Primary (main entry) and
    ' post-fill (ReanchorTPToFillAsync) edits keep post_only:=True.
    Private Async Function SendRateLimitedUpdate(orderType As String, orderId As String, price As Decimal, amount As Decimal, Optional triggerPrice As Decimal? = Nothing, Optional postOnly As Boolean = True) As Task
        Try
            Dim params As New JObject From {
                {"order_id", orderId},
                {"price", price},
                {"amount", amount}
            }
            If triggerPrice.HasValue Then params.Add("trigger_price", triggerPrice.Value)
            If postOnly Then
                params.Add("post_only", True)          ' maker guarantee: keep post_only across the edit (matches placement)
                params.Add("reject_post_only", False)  ' a would-be-taker is repriced to maker, not rejected/filled
            End If

            ' trigger branch = SL edit (id 223346); regular branch = main/TP edit (id 223344).
            Dim updatePayload As New JObject From {
                {"jsonrpc", "2.0"},
                {"id", If(triggerPrice.HasValue, 223346, 223344)},
                {"method", "private/edit"},
                {"params", params}
            }

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
                    {"amount", reduceOrderAmount},
                    {"post_only", True},         ' maker guarantee: keep post_only across the edit (matches placement)
                    {"reject_post_only", False}  ' a would-be-taker is repriced to maker, not rejected/filled
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
            If marketStopLossChecked AndAlso Not emergencyFired AndAlso marketStopThreshold > 0D AndAlso emgBaseline > 0D AndAlso (TradeMode = True) AndAlso (emgBaseline - newPrice >= marketStopThreshold) Then
                ' N1 single-fire latch: SET-THEN-SEND. This assignment and the branch test are synchronous
                ' (the first Await is the CancelOrderAsync below), so a re-entrant quote tick arriving during
                ' the await already sees the latch and cannot dispatch a second market reduce. Cleared only at
                ' CompletePositionClose and the two fresh-order placement seeds.
                emergencyFired = True
                Await CancelOrderAsync()
                newPricePublic = newPrice 'For storing reduce market order price for logging
                Await SendReduceMarketOrderAsync()   ' cross-thread fix: was btnReduceMarket.PerformClick()
                AppendColoredText(txtLogs, "Emergency Sell Market Order Executed.", Color.Red)
                Alert("emergency_stop") ' item D
                RemoteNotifier.Post("OrderApp", "Emergency Sell Market Order Executed.", priority:="urgent") ' Q1
                Return ' Exit early after emergency execution
            ElseIf marketStopLossChecked AndAlso Not emergencyFired AndAlso marketStopThreshold > 0D AndAlso emgBaseline > 0D AndAlso (TradeMode = False) AndAlso (newPrice - emgBaseline >= marketStopThreshold) Then
                ' N1 single-fire latch: SET-THEN-SEND (see the long branch above).
                emergencyFired = True
                Await CancelOrderAsync()
                newPricePublic = newPrice 'For storing reduce market order price for logging
                Await SendReduceMarketOrderAsync()   ' cross-thread fix: was btnReduceMarket.PerformClick()
                AppendColoredText(txtLogs, "Emergency Buy Market Order Executed.", Color.Red)
                Alert("emergency_stop") ' item D
                RemoteNotifier.Post("OrderApp", "Emergency Buy Market Order Executed.", priority:="urgent") ' Q1
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
                {"amount", amount},
                {"post_only", True},         ' maker guarantee: keep post_only across the edit (matches placement)
                {"reject_post_only", False}  ' a would-be-taker is repriced to maker, not rejected/filled
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
                {"amount", amount},
                {"post_only", True},         ' maker guarantee: keep post_only across the edit (matches placement)
                {"reject_post_only", False}  ' a would-be-taker is repriced to maker, not rejected/filled
            }}
        }

            Await SendWebSocketMessageAsync(updateOrderPayload.ToString())

            ' Consume credits for second API call
            rateLimiter.ConsumeCredits()

            ' Update stop loss order. post_only bug fix: this SL is a PRE-fill OTOCO secondary leg - Deribit
            ' rejects post_only on its edit (-32602), so it is omitted here (the leg keeps its placement
            ' post_only, which Deribit preserves across the edit). The trailing main above is primary - it keeps it.
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
                    If maxSlippageATRchecked AndAlso IsATRSlippageExcessive(BestPrice, direction) Then
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
                    If maxSlippageATRchecked AndAlso IsATRSlippageExcessive(BestPrice, direction) Then
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
            emergencyFired = False                ' N1: a fresh order re-establishes a clean emergency context too
            ' N2b: CLEAR, not a set. This bracket is placed at `amount` = orderAmountVal (the box), so 0 -
            ' "use the box" - is the truthful record, and it leaves the whole trailing path byte-identical
            ' to before this change. Load-bearing: this is the ONLY site that sets isTrailingStopLossPlaced,
            ' the sole gate on the trailing reposition block, so it guarantees no bridge size can survive
            ' into a trailing chase - including the one trailing edit path that deliberately has no read of
            ' this field (UpdateStopLossForTrailingOrder; see docs/spec-back-chase-preserve-placed-size.md D3).
            placedOrderSizeUsd = 0D

            ' Entry-chase v2 §4: anchor the SL leg's geometry to this placement price. Same spec
            ' formula as the OTOCO placement; this bracket has no TP leg, so the takeProfitOffset
            ' term can only tighten the bound (never loosen it) - safe.
            legAnchorPrice = BestPrice
            legReanchorDriftMax = Math.Min(takeProfitOffset, triggerDistance) / 2D
            If legReanchorDriftMax <= 0D Then legReanchorDriftMax = 10D  ' offsets unset (manual-targets mode) - modest default

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
            ' item 14f: set cancelPending before the send so echoes from the cancel_all_by_instrument
            ' cannot be misread while the trailing placement is in flight. The 4-s self-clear handles
            ' reset. Do NOT call CancelOrderAsync -- it resets the SL context (emergencyBaseline /
            ' commanded-SL set / trigger baseline) which would break the trailing transition.
            cancelPending = True
            cancelPendingSince = DateTime.UtcNow
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
    ' 4 SL-placement paths + CompletePositionClose + nuclear cancel + market-reduce). Was briefly 8 with
    ' entry-chase v2's fill re-anchor (ReanchorLegsAsync); the TP-only fill-reanchor fix removed that SL
    ' edit, reverting to 7.
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

    ' ===== Ergonomics item D (docs/spec-execution-ergonomics.md): alerts =====
    ' One line per call site; config-gated via item A's alerts block (all default ON; reposition
    ' noise is not alertable at all - no such kind exists). Sound = SystemSounds (Exclamation for
    ' adverse, Asterisk for benign; safe from any thread); taskbar flash = FlashWindowEx,
    ' marshalled + handle-guarded. Best-effort: never lets a failure break an engine path.
    <StructLayout(LayoutKind.Sequential)>
    Private Structure FLASHWINFO
        Public cbSize As UInteger
        Public hwnd As IntPtr
        Public dwFlags As UInteger
        Public uCount As UInteger
        Public dwTimeout As UInteger
    End Structure

    <DllImport("user32.dll")>
    Private Shared Function FlashWindowEx(ByRef pwfi As FLASHWINFO) As Boolean
    End Function

    Private Const FLASHW_ALL As UInteger = 3UI          ' flash caption + taskbar button
    Private Const FLASHW_TIMERNOFG As UInteger = 12UI   ' keep flashing until the window is foregrounded

    Private Sub Alert(kind As String)
        Try
            Dim s As AppUserSettings = userSettings
            Dim enabled As Boolean
            Dim adverse As Boolean
            Select Case kind
                Case "entry_fill"
                    enabled = If(s Is Nothing, True, s.AlertEntryFill) : adverse = False
                Case "close_profit", "close_scratch"
                    enabled = If(s Is Nothing, True, s.AlertCloseFill) : adverse = False
                Case "close_loss"
                    enabled = If(s Is Nothing, True, s.AlertCloseFill) : adverse = True
                Case "external_close"
                    ' Owner ruling 2026-07-18: own key, ADVERSE - the position went flat without a
                    ' fill we tracked (liquidation / external or Deribit-UI close). Highest-surprise
                    ' close, so it stays audible even with routine close chimes off.
                    enabled = If(s Is Nothing, True, s.AlertExternalClose) : adverse = True
                Case "emergency_stop"
                    enabled = If(s Is Nothing, True, s.AlertEmergencyStop) : adverse = True
                Case "order_rejected"
                    enabled = If(s Is Nothing, True, s.AlertOrderRejected) : adverse = True
                Case "connection"
                    enabled = If(s Is Nothing, True, s.AlertConnection) : adverse = True
                Case Else
                    enabled = True : adverse = True ' unknown kind: fail audible, never silent
            End Select
            If Not enabled Then Return

            If adverse Then
                System.Media.SystemSounds.Exclamation.Play()
            Else
                System.Media.SystemSounds.Asterisk.Play()
            End If

            UiInvoke(Sub()
                         If Not (Me.IsHandleCreated AndAlso Not Me.IsDisposed) Then Return
                         Dim fi As New FLASHWINFO With {
                             .cbSize = CUInt(Marshal.SizeOf(GetType(FLASHWINFO))),
                             .hwnd = Me.Handle,
                             .dwFlags = FLASHW_ALL Or FLASHW_TIMERNOFG,
                             .uCount = UInteger.MaxValue,
                             .dwTimeout = 0UI
                         }
                         FlashWindowEx(fi)
                     End Sub)
        Catch
            ' Alerts are best-effort.
        End Try
    End Sub

    ' ===== Ergonomics item I (docs/spec-execution-ergonomics.md, owner-decided 2026-07-17): =====
    ' sticky-bottom log follow. P/Invoke local to the form: GetScrollInfo reads the vertical
    ' scroll state BEFORE an append; WM_VSCROLL/SB_BOTTOM scrolls AFTER it WITHOUT touching the
    ' caret or selection - which is exactly why ScrollToCaret/SelectionStart are NOT used to scroll.
    <StructLayout(LayoutKind.Sequential)>
    Private Structure SCROLLINFO
        Public cbSize As UInteger
        Public fMask As UInteger
        Public nMin As Integer
        Public nMax As Integer
        Public nPage As UInteger
        Public nPos As Integer
        Public nTrackPos As Integer
    End Structure

    <DllImport("user32.dll")>
    Private Shared Function GetScrollInfo(hwnd As IntPtr, fnBar As Integer, ByRef lpsi As SCROLLINFO) As Boolean
    End Function

    <DllImport("user32.dll")>
    Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
    End Function

    Private Const SB_VERT As Integer = 1
    Private Const WM_VSCROLL As Integer = &H115
    Private Const SB_BOTTOM As Integer = 7
    Private Const SIF_RANGE As UInteger = &H1UI
    Private Const SIF_PAGE As UInteger = &H2UI
    Private Const SIF_POS As UInteger = &H4UI

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
        ' item 15: Me.BeginInvoke (non-blocking) — queue ordering keeps log lines in order; UI-thread
        ' callers now append after the current handler returns (cosmetic, no behaviour change).
        If Not (Me.IsHandleCreated AndAlso Not Me.IsDisposed) Then Return
        Try
            ' Item I: check + append + scroll all inside the ONE marshalled action (atomic per
            ' append, UI thread only, display-only).
            Me.BeginInvoke(Sub()
                          ' BEFORE the append: were we at (or within ~one line of) the bottom?
                          ' No scrollbar yet (nPage 0) counts as at-bottom - always follow.
                          Dim atBottom As Boolean = True
                          Dim si As New SCROLLINFO With {
                              .cbSize = CUInt(Marshal.SizeOf(GetType(SCROLLINFO))),
                              .fMask = SIF_RANGE Or SIF_PAGE Or SIF_POS
                          }
                          If GetScrollInfo(rtb.Handle, SB_VERT, si) AndAlso CInt(si.nPage) > 0 Then
                              atBottom = (si.nPos + CInt(si.nPage)) >= (si.nMax - rtb.Font.Height)
                          End If

                          ' item 14d: log cap — trim the front when the log grows large to prevent
                          ' long unattended sessions from degrading the UI thread. The trim is inside
                          ' the marshalled lambda (UI thread only) and is occasional (>400 000 chars).
                          If rtb.TextLength > 400000 Then
                              rtb.Select(0, 100000)
                              rtb.SelectedText = ""
                          End If

                          ' The coloring below moves the caret, so a user's in-progress selection
                          ' (copying mid-stream) is saved and restored around the append - required
                          ' by the item's acceptance; rtb.Select does not scroll.
                          Dim selStart As Integer = rtb.SelectionStart
                          Dim selLength As Integer = rtb.SelectionLength

                          rtb.SelectionStart = rtb.TextLength
                          rtb.SelectionLength = 0
                          rtb.SelectionColor = color
                          rtb.AppendText(text & Environment.NewLine)
                          rtb.SelectionColor = rtb.ForeColor

                          If selLength > 0 Then rtb.Select(selStart, selLength)

                          ' AFTER the append: follow only if the view was at the bottom. WM_VSCROLL
                          ' scrolls without touching caret/selection (never ScrollToCaret).
                          If atBottom Then SendMessage(rtb.Handle, WM_VSCROLL, New IntPtr(SB_BOTTOM), IntPtr.Zero)
                      End Sub)
        Catch
            ' handle went away between the check and the invoke - drop this log line
        End Try
    End Sub

    ' ButtonDisabler()/ButtonEnabler() lived here: a never-called pair carried over from the retired
    ' frmMainPage (no call site since the initial import; they were the ONLY .Enabled writes on any
    ' placement button in the codebase). Deleted with the single-flight guard that supersedes them -
    ' docs/spec-placement-single-flight.md §2 explicitly rules OUT wiring them up, because they also
    ' flip btnReduce*/btnCancelAllOpen, which is a position-state lifecycle that does not exist yet.
    ' Left as dead code they read as a ready-made fix and invite exactly that mistake.

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
        emergencyBaselineSettled = False   ' hybrid fix: clear the loss-cap latch with the triggered-SL context
        emergencyFired = False             ' N1: the trade is over - a new position gets its own single emergency
        ResetCommandedSLPrices() ' reconcile: position closed - triggered-SL context is gone
        legAnchorPrice = 0D          ' entry-chase v2: order context is gone (CancelOrderAsync below also clears it)
        pendingReanchorFill = 0D     ' fill-reanchor fix: drop any un-consumed TP re-anchor
        placedOrderSizeUsd = 0D      ' N2b: trade over - no retained size survives into the next one

        PositionEmpty = True
        PositionLog = False ' Reset position log flag so it can log next new position
        OrderLog = False ' Reset order log flag so it can log next new order

        Await CancelOrderAsync()

        'Clearing margin displays (item 15: UiInvoke — display-only)
        UiInvoke(Sub()
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
                Alert("close_profit") ' item D
                RemoteNotifier.Post("OrderApp", $"Position closed at {pendingCloseExecPrice}: profit ${pendingClosePorLAmt}", priority:="high") ' Q1

                If signalBridge IsNot Nothing AndAlso signalBridge.IsLiveStarted Then
                    LogTradeDecision("Exit Position - Profit", pendingClosePorLAmt, pendingCloseExecPrice)
                End If

            ElseIf (pendingClosePorL = False) And (pendingClosePorLAmt > 0) Then
                AppendColoredText(txtLogs, $"Position executed at {pendingCloseExecPrice}.", Color.Crimson)
                AppendColoredText(txtLogs, $"Loss of: ${pendingClosePorLAmt}.", Color.Crimson)
                Alert("close_loss") ' item D
                RemoteNotifier.Post("OrderApp", $"Position closed at {pendingCloseExecPrice}: loss ${pendingClosePorLAmt}", priority:="high") ' Q1

                If signalBridge IsNot Nothing AndAlso signalBridge.IsLiveStarted Then
                    LogTradeDecision("Exit Position - Loss", pendingClosePorLAmt, pendingCloseExecPrice)
                End If

            Else
                ' Audit2 fix 7 + position model: a tracked close whose P/L rounds to $0.00 (scratch) still
                ' logs and records. No LogTradeDecision here: it has no scratch branch (would write an empty line).
                AppendColoredText(txtLogs, $"Position executed at {pendingCloseExecPrice}.", Color.Yellow)
                AppendColoredText(txtLogs, "Scratch close: P/L ≈ $0.00.", Color.Yellow)
                Alert("close_scratch") ' item D
                RemoteNotifier.Post("OrderApp", $"Position closed at {pendingCloseExecPrice}: scratch (P/L ~ $0.00)", priority:="high") ' Q1
            End If

            ' Item C (journal enrichment): trade-quality metrics, computed HERE next to the record
            ' call (spec: the close path lives in CompletePositionClose; accounting reads the
            ' pendingClose* fields). Direction-aware: MAE is the adverse excursion (<= 0), MFE the
            ' favorable one (>= 0), both linearized at amount/entry like the close P/L itself.
            Dim closeAmt As Decimal = If(pendingCloseAmountUSD > 0D, pendingCloseAmountUSD, orderAmountVal)
            Dim maeUsd As Decimal = 0D, mfeUsd As Decimal = 0D, rMult As Decimal = 0D
            If entryPriceAtClose > 0D AndAlso closeAmt > 0D Then
                Dim btcSize As Decimal = closeAmt / entryPriceAtClose
                If maePrice > 0D AndAlso mfePrice > 0D Then
                    If pendingCloseWasLong Then
                        maeUsd = Math.Min(0D, (maePrice - entryPriceAtClose) * btcSize)
                        mfeUsd = Math.Max(0D, (mfePrice - entryPriceAtClose) * btcSize)
                    Else
                        maeUsd = Math.Min(0D, (entryPriceAtClose - mfePrice) * btcSize)
                        mfeUsd = Math.Max(0D, (entryPriceAtClose - maePrice) * btcSize)
                    End If
                End If
                ' R-multiple vs the PLANNED stop (0 when the planned risk is unknown - spec).
                If plannedStopAtEntry > 0D Then
                    Dim plannedRiskUSD As Decimal = Math.Abs(entryPriceAtClose - plannedStopAtEntry) * btcSize
                    If plannedRiskUSD > 0D Then
                        Dim signedPL As Decimal = If(pendingClosePorL, pendingClosePorLAmt, -pendingClosePorLAmt)
                        rMult = Math.Round(signedPL / plannedRiskUSD, 2, MidpointRounding.AwayFromZero)
                    End If
                End If
            End If
            Dim feesUsd As Decimal = Math.Round(cumFeesBTC * indexPriceVal, 2, MidpointRounding.AwayFromZero)

            ' Audit2 fix 7 + position model: record every computed close - $0.00 scratches and market
            ' reduces included; they are real trades and their absence biased the stats.
            Dim tradeId = RecordCompletedTrade(
                entryPriceAtClose,
                pendingCloseExecPrice,
                closeAmt,
                pendingClosePorLAmt,
                pendingClosePorL,
                pendingCloseWasLong,
                pendingCloseLabel,
                Math.Round(maeUsd, 2, MidpointRounding.AwayFromZero),
                Math.Round(mfeUsd, 2, MidpointRounding.AwayFromZero),
                plannedStopAtEntry,
                rMult,
                feesUsd
            )

            pendingCloseValid = False
        Else
            ' No tracked fill (external/liquidation close): complete the cleanup, nothing to record.
            AppendColoredText(txtLogs, "Position closed.", Color.Yellow)
            Alert("external_close") ' item D, owner ruling 2026-07-18 - the highest-surprise close
            RemoteNotifier.Post("OrderApp", "Position closed with NO tracked fill (external / liquidation?)", priority:="urgent") ' Q1
        End If

        ' Item C: clear the trackers after the close is accounted (both branches - the next
        ' flat->nonzero transition reseeds them anyway; this keeps restart states honest).
        maePrice = 0D
        mfePrice = 0D
        plannedStopAtEntry = 0D
        cumFeesBTC = 0D
        ' Q2: the closed position's signal tag dies with it (both branches - an external close of
        ' a bridge trade must not leave a tag behind; the next promotion would overwrite it anyway).
        currentTradeSignalId = -1
        currentTradeSignalConfidence = ""

        ' Cooloff anchor: the position is now closed. The old autotrader stamped
        ' _indicators.lastAutoTradeTime here; the bridge's cooloff anchors on the same event, so
        ' "cooloff" means "wait N minutes after going flat" (owner ruling 2026-07-15) rather than
        ' N minutes after the entry was placed - which would expire during the trade itself.
        signalBridge?.NotifyPositionClosed()
    End Function

    ' Item C: the five trade-quality metrics are optional (default 0) so the signature stays
    ' compatible; SignalId/SignalConfidence are written empty in Phase A (bridge fills them in
    ' Phase B via the TradeRecord properties).
    Public Function RecordCompletedTrade(entryPrice As Decimal, exitPrice As Decimal,
                                   orderSizeUSD As Decimal, profitLossUSD As Decimal,
                                   isProfit As Boolean, tradeMode As Boolean,
                                   orderType As String,
                                   Optional maeUsd As Decimal = 0D,
                                   Optional mfeUsd As Decimal = 0D,
                                   Optional plannedStop As Decimal = 0D,
                                   Optional rMultiple As Decimal = 0D,
                                   Optional feesUsd As Decimal = 0D) As Integer
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
            completedTrade.MaeUSD = maeUsd
            completedTrade.MfeUSD = mfeUsd
            completedTrade.PlannedStop = plannedStop
            completedTrade.RMultiple = rMultiple
            completedTrade.FeesUSD = feesUsd

            ' Q2: a bridge-tagged position records its signal pair; a manual trade's current* is
            ' empty (-1/"") and keeps the item-C ''-defaults = the spec's NULL semantics.
            If currentTradeSignalId >= 0 Then
                completedTrade.SignalId = currentTradeSignalId.ToString(Globalization.CultureInfo.InvariantCulture)
                completedTrade.SignalConfidence = currentTradeSignalConfidence
            End If

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

            ' Handle live position data (ID 777).
            ' ID 890 (margin estimation) was removed - nothing sends it; ProcessEstimationData was
            ' an empty stub. Reference search confirmed zero 890-sending sites (2026-07-22).
            Dim messageId = json.SelectToken("id")?.ToObject(Of Integer)()

            If messageId = 777 Then

                Dim errorField = json.SelectToken("error")
                If errorField IsNot Nothing Then
                    AppendColoredText(txtLogs, $"Live position error: {errorField.ToString()}", Color.Yellow)
                    Return
                End If

                Dim result = json.SelectToken("result")
                If result IsNot Nothing Then
                    ProcessPositionData(result)
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
                            ' N2b (spec §2 as amended, spec-back D2 UPHELD): restore the entry's AMOUNT too.
                            ' Without this a restart with a live risk-sized entry resting leaves the field 0,
                            ' so the first post-restart reposition resizes the order back to the Amount box -
                            ' the very defect N2b exists to fix, surviving a restart. Same single-writer rule
                            ' placedPrice uses just above: seed only when 0, so a snapshot can never overwrite
                            ' a live in-process value. Seeding a trailing entry's box-sized amount is harmless
                            ' - it equals what the chase would have used anyway.
                            If placedOrderSizeUsd = 0D Then
                                placedOrderSizeUsd = If(o.SelectToken("amount")?.ToObject(Of Decimal?)(), 0D)
                            End If
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
                            ' Hybrid fix: the restored SL is the live top-of-book stop, so it IS the settled loss-cap
                            ' anchor - freeze it (settled = True) so the first post-restore chase doesn't override it.
                            If emergencyBaseline = 0D Then
                                emergencyBaseline = If(price, 0D)
                                emergencyBaselineSettled = True
                            End If
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

            ' item 15: UiInvoke — display-only label updates (margin/leverage).
            UiInvoke(Sub()
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

            ' Get actual account limits (timeout version - same as InitializeRateLimitsAfterAuth)
            accountLimits = Await GetAccountSummaryLimitsWithTimeout(5000)

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
        ' Ergonomics item A: persist the standing inputs FIRST, before any teardown (spec: config
        ' save at the top of this handler). Its own Try - a save failure must never block shutdown.
        Try
            SaveUserSettings(announce:=False)
        Catch
        End Try
        Try
            signalBridge?.Dispose() ' stop the watcher/timers before the sockets go down
            ' Retirement: this form owns the settings window now (FrmIndicators used to close it).
            If _autotradesettings IsNot Nothing AndAlso Not _autotradesettings.IsDisposed Then _autotradesettings.Close()
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

    ' Retirement: opens the settings window (SIGNAL BRIDGE panel + gate config). This replaces
    ' FrmIndicators' btnAutoTradeSettings, which went with that form's UI. Toggles like the old one.
    Private Sub btnAutoSettings_Click(sender As Object, e As EventArgs) Handles btnAutoSettings.Click
        If _autotradesettings Is Nothing OrElse _autotradesettings.IsDisposed Then Return
        If _autotradesettings.Visible Then
            _autotradesettings.Hide()
        Else
            ' Show OWNED by the main form so it hides on minimize and reappears on restore
            ' (re-parenting off FrmIndicators at retirement B cdc7ce4 left it unowned/independent).
            _autotradesettings.Show(Me)
            _autotradesettings.BringToFront()
        End If
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
        If (DateTime.UtcNow - lastPlacementAdmittedUtc).TotalMilliseconds < PlacementDebounceMs Then Return   ' duplicate actuation
        If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return   ' a placement is already in flight
        lastPlacementAdmittedUtc = DateTime.UtcNow
        Try
            ' Get margin estimation before placing order
            btnEstimateMargins_Click(Nothing, Nothing) ' Call the estimation function

            ' Then execute the order
            If TradeMode = True Then
                Await ExecuteOrderAsync("BuyLimit")
            Else
                Await ExecuteOrderAsync("SellLimit")
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnLimit_Click: {ex.Message}", Color.Red)
        Finally
            Interlocked.Exchange(isPlacingOrder, 0)
        End Try
    End Sub



    Private Async Sub btnNoSpread_Click(sender As Object, e As EventArgs) Handles btnNoSpread.Click
        If (DateTime.UtcNow - lastPlacementAdmittedUtc).TotalMilliseconds < PlacementDebounceMs Then Return   ' duplicate actuation
        If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return   ' a placement is already in flight
        lastPlacementAdmittedUtc = DateTime.UtcNow
        Try
            ' Get margin estimation before placing order
            btnEstimateMargins_Click(Nothing, Nothing) ' Call the estimation function

            ' Then execute the order
            If TradeMode = True Then
                Await ExecuteOrderAsync("BuyNoSpread")
            Else
                Await ExecuteOrderAsync("SellNoSpread")
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnNoSpread_Click: {ex.Message}", Color.Red)
        Finally
            Interlocked.Exchange(isPlacingOrder, 0)
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
        If (DateTime.UtcNow - lastPlacementAdmittedUtc).TotalMilliseconds < PlacementDebounceMs Then Return   ' duplicate actuation
        If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return   ' a placement is already in flight
        lastPlacementAdmittedUtc = DateTime.UtcNow
        Try
            ' Get margin estimation before placing order
            btnEstimateMargins_Click(Nothing, Nothing) ' Call the estimation function

            ' Then execute the order
            If TradeMode = True Then
                Await ExecuteOrderAsync("BuyMarket")
            Else
                Await ExecuteOrderAsync("SellMarket")
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnMarket_Click: {ex.Message}", Color.Red)
        Finally
            Interlocked.Exchange(isPlacingOrder, 0)
        End Try

    End Sub

    Private Async Sub btnReduceLimit_Click(sender As Object, e As EventArgs) Handles btnReduceLimit.Click
        If (DateTime.UtcNow - lastPlacementAdmittedUtc).TotalMilliseconds < PlacementDebounceMs Then Return   ' duplicate actuation
        If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return   ' a placement is already in flight
        lastPlacementAdmittedUtc = DateTime.UtcNow
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
        Finally
            Interlocked.Exchange(isPlacingOrder, 0)
        End Try
    End Sub



    ' Shared reduce-only MARKET order logic. Callers: btnReduceMarket_Click (UI thread),
    ' FlattenPositionAsync (UI thread), and the two emergency sites in
    ' UpdateStopLossForTriggeredStopLossOrder (receive thread — avoids a cross-thread
    ' btnReduceMarket.PerformClick()). Reads positionSizeUSD (position model) first;
    ' falls back to TradeMode/orderAmountVal only when the model is unseeded.
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
        ' Only the BUTTON is latched. The emergency/flatten callers reach SendReduceMarketOrderAsync
        ' directly and are deliberately unaffected - by the debounce below as well as by the latch.
        If (DateTime.UtcNow - lastPlacementAdmittedUtc).TotalMilliseconds < PlacementDebounceMs Then Return   ' duplicate actuation
        If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return   ' a placement is already in flight
        lastPlacementAdmittedUtc = DateTime.UtcNow
        Try
            Await SendReduceMarketOrderAsync()
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnReduceMarket_Click: {ex.Message}", Color.Red)
        Finally
            Interlocked.Exchange(isPlacingOrder, 0)
        End Try
    End Sub

    Private Async Sub btnEditTPPrice_Click(sender As Object, e As EventArgs) Handles btnEditTPPrice.Click
        Try
            ' item 12: use mirror fields for manualTPval/commsVal/tpOffsetVal/orderAmountVal;
            ' TryParse for display-only fields (txtPlacedPrice / txtPlacedTakeProfitPrice).
            Dim d As Decimal
            Dim displayPlacedPrice As Decimal = If(Decimal.TryParse(txtPlacedPrice.Text, d), d, 0D)
            If (isTrailingStop = True) And (isTrailingPosition = True) And (isTrailingStopLossPlaced = True) Then
                If manualTPval > 0 Then
                    txtPlacedTakeProfitPrice.Text = txtManualTP.Text
                    If TradeMode = True Then
                        If manualTPval < (displayPlacedPrice + commsVal) Then
                            AppendColoredText(txtLogs, "Manual TP is less than comms paid.", Color.Yellow)
                        End If
                    Else
                        If manualTPval > (displayPlacedPrice - commsVal) Then
                            AppendColoredText(txtLogs, "Manual TP is less than comms paid.", Color.Yellow)
                        End If
                    End If

                Else
                    If TradeMode = True Then
                        txtPlacedTakeProfitPrice.Text = displayPlacedPrice + (tpOffsetVal + commsVal)
                    Else
                        txtPlacedTakeProfitPrice.Text = displayPlacedPrice - (tpOffsetVal + commsVal)
                    End If
                End If
                AppendColoredText(txtLogs, $"Updated Trailing SL target to: ${txtPlacedTakeProfitPrice.Text}", Color.Yellow)

            Else
                Dim newTPprice As Decimal = If(Decimal.TryParse(txtPlacedTakeProfitPrice.Text, d), d, 0D)
                If newTPprice > 0 Then
                    Dim amount As Decimal = orderAmountVal
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
                    {"amount", amount},
                    {"post_only", True},         ' maker guarantee: keep post_only across the edit (matches placement)
                    {"reject_post_only", False}  ' a would-be-taker is repriced to maker, not rejected/filled
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

    ' Ergonomics item E: the SL-edit core, extracted verbatim from btnEditSLPrice_Click (id
    ' resolution incl. the audit-2 fixed fallbacks, limit = trigger -/+ txtStopLoss offset,
    ' payload id 223346, send, logs). UI thread only (button paths). BINDING (spec item E +
    ' spec-back-session-2026-07-04 §9/§10): this is a USER edit path, so it must NOT call
    ' RecordCommandedSLPrice - the commanded-price discriminator deliberately treats its echo
    ' as a manual edit and follows it; recording here would make it ignore the user's own move.
    Private Async Function EditStopLossTo(newTrigger As Decimal) As Task
        Dim newTSprice As Decimal = newTrigger
        Dim newSLprice As Decimal
        Dim amount As Decimal = Decimal.Parse(txtAmount.Text)
        Dim SLOrderID As String = Nothing

        ' Ensure WebSocket is connected
        If webSocketClient Is Nothing OrElse webSocketClient.State <> WebSocketState.Open Then
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
                {"amount", amount},
                {"post_only", True},         ' maker guarantee: keep post_only across the edit (matches placement)
                {"reject_post_only", False}  ' a would-be-taker is repriced to maker, not rejected/filled
            }}
        }

        ' Send the payload to update the take profit order
        Await SendWebSocketMessageAsync(updateTakeProfitPayload.ToString())

        AppendColoredText(txtLogs, $"Updated T.S. to: ${newTSprice}", Color.Yellow)
        AppendColoredText(txtLogs, $"Updated S.L. to: ${newSLprice}", Color.Yellow)
    End Function

    Private Async Sub btnEditSLPrice_Click(sender As Object, e As EventArgs) Handles btnEditSLPrice.Click
        Try
            ' item 12: TryParse guard — blank textbox no longer throws FormatException.
            Dim trigPrice As Decimal
            If Not Decimal.TryParse(txtPlacedTrigStopPrice.Text, trigPrice) OrElse trigPrice <= 0D Then
                AppendColoredText(txtLogs, "S.L. textbox is 0", Color.Yellow)
            Else
                Await EditStopLossTo(trigPrice)
            End If

        Catch ex As Exception
            txtLogs.AppendText("Error in btnEditSLPrice: " & ex.Message & Environment.NewLine)
        End Try
    End Sub

    ' Item E: one-click break-even stop - trigger = avg entry + Comms (long) / - Comms (short),
    ' covering round-trip cost, rounded to the 0.5 tick. Gates on a live position; the id
    ' refusal (rare no-SL states) lives in the shared core. Direction from the POSITION sign
    ' (the model is the truth), not TradeMode.
    Private Async Sub btnBreakEven_Click(sender As Object, e As EventArgs) Handles btnBreakEven.Click
        Try
            If positionSizeUSD = 0D OrElse positionAvgEntry <= 0D Then
                AppendColoredText(txtLogs, "B.E.: no open position", Color.Yellow)
                Return
            End If
            Dim isLong As Boolean = positionSizeUSD > 0D
            Dim beTrigger As Decimal = RoundToTick(If(isLong, positionAvgEntry + commsVal, positionAvgEntry - commsVal))

            ' Item E follow-up (owner testnet 2026-07-18): a break-even stop can only REST once
            ' price has moved past the break-even level - a long's sell-stop must sit below the
            ' market, a short's buy-stop above it. Placing it early is exactly what the exchange
            ' rejects as 10034 trigger_price_too_high (witnessed: long BE 63994 vs market ~63959).
            ' Pre-check against top-of-book and refuse cleanly instead of firing a doomed edit that
            ' logs a red API ERROR. The exchange (mark-price based) stays the final arbiter; the
            ' 0-price arms just skip the check if a quote hasn't arrived yet.
            If isLong AndAlso BestBidPrice > 0D AndAlso beTrigger >= BestBidPrice Then
                AppendColoredText(txtLogs, $"B.E.: not there yet - break-even ${beTrigger:F2} is at/above the bid ${BestBidPrice:F2} (price must rise past break-even first)", Color.Yellow)
                Return
            ElseIf (Not isLong) AndAlso BestAskPrice > 0D AndAlso beTrigger <= BestAskPrice Then
                AppendColoredText(txtLogs, $"B.E.: not there yet - break-even ${beTrigger:F2} is at/below the ask ${BestAskPrice:F2} (price must fall past break-even first)", Color.Yellow)
                Return
            End If

            AppendColoredText(txtLogs, $"B.E.: moving stop trigger to ${beTrigger:F2} (entry {positionAvgEntry:F2} {If(isLong, "+", "-")} comms {commsVal:F2})", Color.Yellow)
            Await EditStopLossTo(beTrigger)
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnBreakEven_Click: {ex.Message}", Color.Red)
        End Try
    End Sub

    Private Async Sub btnTrail_Click(sender As Object, e As EventArgs) Handles btnTrail.Click
        If (DateTime.UtcNow - lastPlacementAdmittedUtc).TotalMilliseconds < PlacementDebounceMs Then Return   ' duplicate actuation
        If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return   ' a placement is already in flight
        lastPlacementAdmittedUtc = DateTime.UtcNow
        Try
            ' Get margin estimation before placing order
            btnEstimateMargins_Click(Nothing, Nothing) ' Call the estimation function

            ' Then execute the order
            If TradeMode = True Then
                Await StopLossForTrailingOrderAsync("BuyTrail")
            Else
                Await StopLossForTrailingOrderAsync("SellTrail")
            End If

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error in btnTrail_Click: {ex.Message}", Color.Red)
        Finally
            Interlocked.Exchange(isPlacingOrder, 0)
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
                ' Item C: 1000 -> 1180 to seat the four new metric columns (Entry/Exit/Size/PL
                ' shrank 100 -> 90 each to help; nothing was dropped).
                ' Q2 grid follow-up: 1180 -> 1340 to seat the two signal-tag columns (Signal ID 70
                ' + Confidence 90); the grid is DockStyle.Fill and scrolls if a display is narrower.
                viewForm.Size = New Size(1340, 700)
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
                .Width = 90,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Exit Price",
                .DataPropertyName = "ExitPrice",
                .Width = 90,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Size (USD)",
                .DataPropertyName = "OrderSizeUSD",
                .Width = 90,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "P/L (USD)",
                .DataPropertyName = "ProfitLossUSD",
                .Width = 90,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                ' Item C: the four trade-quality columns (MAE <= 0 <= MFE; R vs the planned stop;
                ' net fees). Old rows (pre-migration) show 0.00 - the columns backfill as defaults.
                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "MAE",
                .DataPropertyName = "MaeUSD",
                .Width = 80,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "MFE",
                .DataPropertyName = "MfeUSD",
                .Width = 80,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "R",
                .DataPropertyName = "RMultiple",
                .Width = 60,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Fees",
                .DataPropertyName = "FeesUSD",
                .Width = 80,
                .DefaultCellStyle = New DataGridViewCellStyle With {
                    .Format = "F2",
                    .Alignment = DataGridViewContentAlignment.MiddleRight
                }
            })

                ' Q2 grid follow-up (docs/review-quickwins.md): surface the bridge signal tag -
                ' which engine signal drove this trade (empty for manual trades). Bound to the same
                ' TradeRecord properties the tag lifecycle already writes; the DB is unchanged.
                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Signal ID",
                .DataPropertyName = "SignalId",
                .Width = 70,
                .DefaultCellStyle = New DataGridViewCellStyle With {.Alignment = DataGridViewContentAlignment.MiddleCenter}
            })

                dataGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .HeaderText = "Confidence",
                .DataPropertyName = "SignalConfidence",
                .Width = 90,
                .DefaultCellStyle = New DataGridViewCellStyle With {.Alignment = DataGridViewContentAlignment.MiddleCenter}
            })

                ' Add result column
                Dim resultColumn As New DataGridViewTextBoxColumn With {
                .HeaderText = "Result",
                .Name = "ResultColumn",
                .Width = 70,
                .DefaultCellStyle = New DataGridViewCellStyle With {.Alignment = DataGridViewContentAlignment.MiddleCenter}
            }
                dataGrid.Columns.Add(resultColumn)

                ' Bind data. The row styling CANNOT run here: dataGrid is still unparented at this
                ' point (it is added to viewForm further down), an unparented control has no
                ' BindingContext, and DataGridView DEFERS the bind until it gets one. So Rows is
                ' empty right after this assignment and a styling loop here silently does nothing -
                ' that was the "no colours AND a blank Result column until you delete a row" bug
                ' (the delete path rebinds an already-shown grid, which is why only it looked right).
                ' ApplyTradeRowStyling is called after viewForm.Show() below instead.
                dataGrid.DataSource = trades

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

                ' Now the grid is parented and shown, the deferred bind has completed and Rows is
                ' populated - style it here (see the note at the DataSource assignment above).
                ApplyTradeRowStyling(dataGrid)

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

            ' Update the data source. This grid is already shown, so the bind completes synchronously
            ' and Rows is populated by the time the styling runs.
            dataGrid.DataSource = updatedTrades

            ' Reapply color coding
            ApplyTradeRowStyling(dataGrid)

        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error refreshing trade grid: {ex.Message}", Color.Red)
        End Try
    End Sub

    ' The View Trades grid's row presentation: the unbound Result column's WIN/LOSS text plus the
    ' win/loss row colours. Single implementation shared by the initial open and the post-delete
    ' refresh - the two used to carry byte-identical copies of this loop, and only the refresh copy
    ' ever ran (see the bind-site note in the ViewTrades handler). Safe to call repeatedly.
    Private Sub ApplyTradeRowStyling(dataGrid As DataGridView)
        If dataGrid Is Nothing Then Return

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
    End Sub


    Private Sub btnEstimateMargins_Click(sender As Object,
                                           e As EventArgs) _
                                           Handles btnEstimateMargins.Click
        Try
            '---------------------------  input validation  --------------------
            ' item 12+13: TryParse replaces IsNumeric+Parse; local renamed orderValueUSD (was
            ' positionSizeUSD) to avoid shadowing the engine's position-model field.
            Dim orderValueUSD As Decimal
            If Not Decimal.TryParse(txtAmount.Text, orderValueUSD) OrElse orderValueUSD <= 0D Then
                AppendColoredText(txtLogs, "Please enter a valid amount", Color.Yellow)
                Return
            End If

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
            If(accountBalanceUSD = 0D, 0D, orderValueUSD / accountBalanceUSD)

            '----------------  call the corrected margin routine  --------------
            Dim isShort As Boolean = Not TradeMode          ' True = short
            Dim margins = CalculateDeribitInverseLiquidationPrice(
                           orderValueUSD,
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
            ' item 12: check the engine field (placedPrice) directly — avoids a Decimal.Parse on the
            ' display textbox which can throw if the box is blank.
            If placedPrice > 0D Then
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

