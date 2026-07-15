Option Strict On
Option Explicit On

Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json.Linq

' =====================================================================================================
' SignalBridge - the execution-side consumer of the VerdictEngine signal bridge.
'
' Contract: docs/integration-contract-verdictengine.md (FROZEN v1) - canonical for consumer behavior.
' Spec:     docs/spec-autotrade-tiein.md section 3.
'
' Consumes verdict_signal.json (atomic engine writes) via FileSystemWatcher + ~150 ms debounce +
' an independent 10-s staleness timer (FSW cannot detect a dead engine), runs the contract section-4
' gate chain in order, and drives the decouple-v2 public API (SetTradeTargets + PlaceAutomatedOrder).
' Every consumed payload gets exactly one disposition line (host log + bridge-dispositions.log) -
' the soak reviewers join these rows against the engine CSV on (instance_id, signal_id); keep the
' line format and disposition tokens STABLE once the soak starts.
'
' Threading: FSW callbacks, debounce/staleness timer callbacks, and the settings-form UI thread all
' enter here; the host's receive thread reads LastSignalAtr. State transitions take _sync briefly;
' LastSignalAtr is a plain field-backed read (no locking, no controls, allocation-free - receive-path
' rule). This class NEVER touches WinForms controls: UI output goes through the host log delegate
' (AppendColoredText self-marshals + handle-guards) and the StatusChanged event, which subscribers
' must marshal themselves (raised on arbitrary threads).
'
' Nothing here persists across app restarts except bridge-state.json (the acted de-dupe pair, a
' contract section-4.3 requirement) - mode/ARM/Started always reset to Off/unchecked/stopped.
' =====================================================================================================
Public Class SignalBridge
    Implements IDisposable

    Public Enum BridgeMode
        Off = 0
        LogOnly = 1
        Live = 2
    End Enum

    Private Const DebounceMs As Integer = 150
    Private Const StalenessPeriodMs As Integer = 10_000
    Private Const StaleAlertThreshold As Integer = 3
    Private Const DefaultPayloadPath As String = "C:\Dev\DeribitBridge\verdict_signal.json"

    Private ReadOnly _host As frmMainPageV2
    Private ReadOnly _log As Action(Of String, Color)
    Private ReadOnly _sync As New Object()

    ' ---- config ----
    ' RETIREMENT (docs/spec-back-autotrade-retirement.md): the gate config is no longer bridge-owned.
    ' It is live-read off the settings form's own boxes (Trade Gates + Inclusion Time Range + Tiers)
    ' and the main form's Amount box, so there is one place per setting and no SAVE step to forget.
    ' Those boxes commit on focus-loss/Enter into plain backing fields, so a half-typed value never
    ' reaches this class and nothing here touches a control off-thread. Consequence: the gate config
    ' resets to the designer defaults at every app start until the ergonomics Phase A config-save.
    ' bridge.json now carries only what has no natural home on a form: the payload path.
    Private _payloadPath As String = DefaultPayloadPath
    Private _slippageAtrMult As Decimal = 0.6D           ' informational in v1: the operative cap rides the
    '                                                      host's existing chkMaxSlippageATR machinery (spec section 1)

    Private ReadOnly Property Settings As AutoTradeSettings
        Get
            Return _host.AutoTradeSettingsForm
        End Get
    End Property

    ' Confidence tiers to accept. Falls back to the contract default if the box is somehow empty.
    Private ReadOnly Property Tiers As List(Of String)
        Get
            Dim s As AutoTradeSettings = Settings
            Dim csv As String = If(s IsNot Nothing, s.TiersCsv, "")
            Dim out As New List(Of String)
            For Each t In If(csv, "").Split(","c)
                Dim tt As String = t.Trim().ToUpperInvariant()
                If tt.Length > 0 AndAlso Not out.Contains(tt) Then out.Add(tt)
            Next
            If out.Count = 0 Then out.AddRange({"HIGH", "MEDIUM"})
            Return out
        End Get
    End Property

    ' Order size = the main form's Amount box (already mirrored into an engine field there, so this
    ' is a plain field read). One size for manual and automated entries - no second place to set it.
    Private ReadOnly Property SizeUsd As Decimal
        Get
            Return _host.OrderSizeUSD
        End Get
    End Property

    Private ReadOnly Property CooloffMin As Decimal
        Get
            Dim s As AutoTradeSettings = Settings
            Return If(s IsNot Nothing, s.CooloffMin, 0D)
        End Get
    End Property

    Private ReadOnly Property CircuitBreakerUsd As Decimal
        Get
            Dim s As AutoTradeSettings = Settings
            Return If(s IsNot Nothing, s.CircuitBreakerUsd, 0D)
        End Get
    End Property

    ' ---- acted de-dupe pair (bridge-state.json; persisted across restarts per contract section 4.3) ----
    Private _lastActedInstanceId As String = ""
    Private _lastActedSignalId As Long = -1

    ' ---- runtime state (all reset at construction; nothing persists) ----
    Private _mode As BridgeMode = BridgeMode.Off
    Private _localArmed As Boolean = False
    Private _started As Boolean = False
    Private _engineArmed As Boolean = False              ' from the latest parsed payload
    Private _lastPayloadGeneratedUtc As DateTime = DateTime.MinValue
    Private _lastExecResMin As Integer = 1
    Private _lastSignalAtr As Decimal = 0D               ' last actionable payload's atr; 0 when none/stale.
    '                                                      Read on the receive thread (CalculateATRSlippageLimit):
    '                                                      plain field, accepted Decimal torn-read class.
    Private _lastDisposition As String = ""
    Private _lastSignalSummary As String = ""
    Private _lastActionUtc As DateTime = DateTime.MinValue   ' cooloff anchor (acted / would-act)
    Private _staleChecks As Integer = 0                  ' consecutive stale staleness-timer checks
    Private _staleAlerted As Boolean = False
    Private _lastSeenInstanceId As String = ""           ' in-memory only: suppresses double-dispositions
    Private _lastSeenSignalId As Long = Long.MinValue    ' when FSW double-fires on the same payload

    ' ---- machinery ----
    Private _watcher As FileSystemWatcher
    Private ReadOnly _debounce As Threading.Timer
    Private ReadOnly _staleTimer As Threading.Timer
    Private _processing As Integer = 0                   ' single-flight: one payload evaluated at a time
    Private _rerun As Integer = 0                        ' a newer file event supersedes a queued one
    Private _disposed As Boolean = False

    ' Raised on ANY state/status change, on arbitrary threads - UI subscribers must marshal (BeginInvoke).
    Public Event StatusChanged()

    Public Sub New(host As frmMainPageV2, log As Action(Of String, Color))
        _host = host
        _log = log
        _debounce = New Threading.Timer(AddressOf OnDebounceFired, Nothing, Timeout.Infinite, Timeout.Infinite)
        _staleTimer = New Threading.Timer(AddressOf OnStalenessTick, Nothing, Timeout.Infinite, Timeout.Infinite)
        LoadConfig()
        LoadState()
        _log($"consumer ready (mode Off) - payload path: {_payloadPath}", Color.Gray)
    End Sub

    ' ================================ public surface (UI + host) ================================

    Public Property Mode As BridgeMode
        Get
            Return _mode
        End Get
        Set(value As BridgeMode)
            Dim changed As Boolean = False
            SyncLock _sync
                If _mode <> value Then
                    _mode = value
                    changed = True
                End If
            End SyncLock
            If Not changed Then Return
            ForceStop($"mode changed to {value}")
            If value = BridgeMode.Off Then
                StopWatching()
                _log("mode Off - watcher and staleness checks stopped", Color.Gray)
            Else
                StartWatching()
                _log($"mode {value} - watching {_payloadPath}", Color.DodgerBlue)
                EvaluateNow() ' initial read so status/dispositions don't wait for the next engine run
            End If
            RaiseEvent StatusChanged()
        End Set
    End Property

    Public Property LocalArmed As Boolean
        Get
            Return _localArmed
        End Get
        Set(value As Boolean)
            Dim changed As Boolean = False
            SyncLock _sync
                If _localArmed <> value Then
                    _localArmed = value
                    changed = True
                End If
            End SyncLock
            If Not changed Then Return
            If Not value Then ForceStop("ARM unchecked")
            _log(If(value, "ARM on (local)", "ARM off (local)"), If(value, Color.DodgerBlue, Color.Gray))
            RaiseEvent StatusChanged()
        End Set
    End Property

    Public ReadOnly Property Started As Boolean
        Get
            Return _started
        End Get
    End Property

    Public ReadOnly Property EngineArmed As Boolean
        Get
            Return _engineArmed
        End Get
    End Property

    ' Live-and-started - the state LogTradeDecision gating reads (replaces FrmIndicators.IsAutoTradingEnabled).
    Public ReadOnly Property IsLiveStarted As Boolean
        Get
            Return _mode = BridgeMode.Live AndAlso _started
        End Get
    End Property

    Public ReadOnly Property IsFreshNow As Boolean
        Get
            Dim gen As DateTime = _lastPayloadGeneratedUtc
            If gen = DateTime.MinValue Then Return False
            Return (DateTime.UtcNow - gen).TotalMinutes <= 2.5R * Math.Max(_lastExecResMin, 1)
        End Get
    End Property

    Public ReadOnly Property LastDisposition As String
        Get
            Return _lastDisposition
        End Get
    End Property

    Public ReadOnly Property LastSignalSummary As String
        Get
            Return _lastSignalSummary
        End Get
    End Property

    ' Read from CalculateATRSlippageLimit on the RECEIVE thread: plain field-backed, no locking,
    ' no controls, allocation-free. 0 when no actionable payload yet or when stale.
    Public ReadOnly Property LastSignalAtr As Decimal
        Get
            Return _lastSignalAtr
        End Get
    End Property

    ' Interlock (contract section 6, trader-fixed) - Nothing on success, else the refusal reason.
    ' START succeeds only when: mode = Live AND LocalArmed AND latest payload fresh AND engine armed
    ' AND chkMaxSlippageATR checked (spec section 1 design decision - the slippage-cap commitment
    ' rides the existing guard machinery).
    Public Function TryStart() As String
        Dim reason As String = Nothing
        SyncLock _sync
            If _mode <> BridgeMode.Live Then
                reason = "mode is not Live (log-only runs un-started by design)"
            ElseIf Not _localArmed Then
                reason = "ARM AUTOTRADE is unchecked"
            ElseIf Not IsFreshNow Then
                reason = "latest payload is stale (or none received yet)"
            ElseIf Not _engineArmed Then
                reason = "engine ARM is off in the latest payload"
            ElseIf Not _host.IsMaxSlippageGuardChecked Then
                reason = "Max Slippage ATR guard (chkMaxSlippageATR) is unchecked"
            Else
                _started = True
            End If
        End SyncLock
        If reason Is Nothing Then
            _log("STARTED - live auto-trading interlock satisfied", Color.LimeGreen)
        Else
            _log($"START refused: {reason}", Color.Yellow)
        End If
        RaiseEvent StatusChanged()
        Return reason
    End Function

    Public Sub [Stop]()
        ForceStop("STOP pressed")
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        StopWatching()
        _debounce.Dispose()
        _staleTimer.Dispose()
    End Sub

    ' ================================ file paths (beside the exe, like secrets.json) ================================

    Private Shared ReadOnly Property ConfigFilePath As String
        Get
            Return Path.Combine(AppContext.BaseDirectory, "bridge.json")
        End Get
    End Property

    Private Shared ReadOnly Property StateFilePath As String
        Get
            Return Path.Combine(AppContext.BaseDirectory, "bridge-state.json")
        End Get
    End Property

    Private Shared ReadOnly Property DispositionLogPath As String
        Get
            Return Path.Combine(AppContext.BaseDirectory, "bridge-dispositions.log")
        End Get
    End Property

    ' ================================ config + state files ================================

    ' bridge.json carries only the payload path now - the gate config lives on the settings form.
    Private Sub LoadConfig()
        Try
            If Not File.Exists(ConfigFilePath) Then
                _log($"bridge.json not found beside the exe - watching the default path {DefaultPayloadPath}", Color.Yellow)
                Return
            End If
            Dim json As JObject = JObject.Parse(File.ReadAllText(ConfigFilePath))
            _payloadPath = If(json.SelectToken("path")?.ToString(), DefaultPayloadPath)
            _slippageAtrMult = If(json.SelectToken("slippage_atr_mult")?.ToObject(Of Decimal)(), _slippageAtrMult)
        Catch ex As Exception
            _log($"bridge.json load failed ({ex.Message}) - watching the default path", Color.Yellow)
        End Try
    End Sub

    Private Sub LoadState()
        Try
            If Not File.Exists(StateFilePath) Then Return
            Dim json As JObject = JObject.Parse(File.ReadAllText(StateFilePath))
            _lastActedInstanceId = If(json.SelectToken("instance_id")?.ToString(), "")
            _lastActedSignalId = If(json.SelectToken("last_acted_signal_id")?.ToObject(Of Long)(), -1L)
            If _lastActedInstanceId.Length > 0 AndAlso _lastActedSignalId >= 0 Then
                _log($"de-dupe state restored: last acted signal {_lastActedSignalId} (engine {_lastActedInstanceId})", Color.Gray)
            End If
        Catch ex As Exception
            _log($"bridge-state.json load failed ({ex.Message}) - starting with no de-dupe history", Color.Yellow)
        End Try
    End Sub

    Private Sub PersistState()
        Try
            Dim jo As New JObject From {
                {"instance_id", _lastActedInstanceId},
                {"last_acted_signal_id", _lastActedSignalId}
            }
            ' F-2 (review-autotrade-tiein): temp + move so a crash mid-write can't tear the
            ' de-dupe watermark (same atomic-write discipline as the engine's payload emit).
            Dim tmp As String = StateFilePath & ".tmp"
            File.WriteAllText(tmp, jo.ToString())
            File.Move(tmp, StateFilePath, overwrite:=True)
        Catch ex As Exception
            _log($"bridge-state.json write failed: {ex.Message}", Color.Yellow)
        End Try
    End Sub

    ' ================================ watcher + timers ================================

    Private Sub StartWatching()
        SyncLock _sync
            If _watcher Is Nothing Then
                Try
                    Dim dir As String = Path.GetDirectoryName(_payloadPath)
                    If String.IsNullOrEmpty(dir) Then dir = "."
                    If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir) ' create-if-missing (emitter does too)
                    Dim w As New FileSystemWatcher(dir, Path.GetFileName(_payloadPath))
                    w.NotifyFilter = NotifyFilters.LastWrite Or NotifyFilters.FileName Or NotifyFilters.CreationTime Or NotifyFilters.Size
                    ' File.Replace on the engine side surfaces as rename/change - watch all three.
                    AddHandler w.Changed, AddressOf OnPayloadFileEvent
                    AddHandler w.Created, AddressOf OnPayloadFileEvent
                    AddHandler w.Renamed, AddressOf OnPayloadRenamed
                    w.EnableRaisingEvents = True
                    _watcher = w
                Catch ex As Exception
                    _log($"watcher start failed: {ex.Message}", Color.Red)
                End Try
            End If
        End SyncLock
        _staleTimer.Change(StalenessPeriodMs, StalenessPeriodMs) ' independent 10-s staleness poll
    End Sub

    Private Sub StopWatching()
        Dim w As FileSystemWatcher = Nothing
        SyncLock _sync
            w = _watcher
            _watcher = Nothing
            _staleChecks = 0
            _staleAlerted = False
        End SyncLock
        _staleTimer.Change(Timeout.Infinite, Timeout.Infinite)
        _debounce.Change(Timeout.Infinite, Timeout.Infinite)
        If w IsNot Nothing Then
            w.EnableRaisingEvents = False
            w.Dispose()
        End If
    End Sub

    Private Sub OnPayloadFileEvent(sender As Object, e As FileSystemEventArgs)
        KickDebounce()
    End Sub

    Private Sub OnPayloadRenamed(sender As Object, e As RenamedEventArgs)
        KickDebounce()
    End Sub

    Private Sub KickDebounce()
        Try
            _debounce.Change(DebounceMs, Timeout.Infinite) ' reset per event (~150 ms debounce)
        Catch ex As ObjectDisposedException
            ' In-flight FSW callback racing app shutdown - drop it.
        End Try
    End Sub

    Private Sub OnDebounceFired(state As Object)
        EvaluateNow()
    End Sub

    ' Independent staleness check: FSW cannot detect a dead engine (silence = dead, contract section 2).
    Private Sub OnStalenessTick(state As Object)
        If _mode = BridgeMode.Off OrElse _disposed Then Return
        If IsFreshNow Then
            SyncLock _sync
                _staleChecks = 0
                _staleAlerted = False
            End SyncLock
            Return
        End If
        Dim checkCount As Integer
        Dim alertNow As Boolean = False
        SyncLock _sync
            _staleChecks += 1
            checkCount = _staleChecks
            _lastSignalAtr = 0D ' stale => no bridge ATR (slippage guard falls back to FrmIndicators/$70)
            If checkCount >= StaleAlertThreshold AndAlso Not _staleAlerted Then
                _staleAlerted = True
                alertNow = True
            End If
        End SyncLock
        ForceStop("stale payload (staleness check)") ' stand down; no-op unless Started
        If alertNow Then
            _log($"STAND-DOWN ALERT: no fresh payload after {StaleAlertThreshold} consecutive checks - engine dead or stopped?", Color.Red)
            RaiseEvent StatusChanged()
        End If
    End Sub

    ' Any disarm drops to STOPPED (START is not sticky - contract section 6); resuming = full sequence.
    Private Sub ForceStop(cause As String)
        Dim wasStarted As Boolean = False
        SyncLock _sync
            wasStarted = _started
            _started = False
        End SyncLock
        If wasStarted Then
            _log($"auto-STOP: {cause}", Color.Red)
            RaiseEvent StatusChanged()
        End If
    End Sub

    ' ================================ payload processing ================================

    ' Single-flight entry point: one payload evaluated at a time; a newer file event supersedes
    ' a queued one (only the latest file content is ever evaluated - we re-read from disk per pass).
    Private Sub EvaluateNow()
        If _mode = BridgeMode.Off OrElse _disposed Then Return
        If Interlocked.Exchange(_processing, 1) = 1 Then
            Interlocked.Exchange(_rerun, 1)
            Return
        End If
        ProcessLoopAsync()
    End Sub

    Private Async Sub ProcessLoopAsync() ' Async Sub: top-level timer/FSW-driven entry; all exceptions caught
        Try
            Do
                Interlocked.Exchange(_rerun, 0)
                Await EvaluateLatestPayloadAsync()
            Loop While Interlocked.Exchange(_rerun, 0) = 1
        Catch ex As Exception
            _log($"payload processing error: {ex.Message}", Color.Red)
        Finally
            Interlocked.Exchange(_processing, 0)
        End Try
        ' A supersede that landed between the loop exit and the flag clear re-enters here; anything
        ' later is picked up by the next FSW event or the 10-s staleness tick.
        If Interlocked.CompareExchange(_rerun, 0, 0) = 1 Then EvaluateNow()
    End Sub

    Private Async Function EvaluateLatestPayloadAsync() As Task
        Dim text As String = Await ReadPayloadTextAsync()
        If text Is Nothing Then Return

        Dim json As JObject
        Try
            json = JObject.Parse(text)
        Catch ex As Exception
            _log($"payload parse error: {ex.Message}", Color.Yellow) ' torn files shouldn't happen (atomic writes)
            Return
        End Try

        Dim p As PayloadSnapshot
        Try
            p = ParsePayload(json)
        Catch ex As Exception
            _log($"payload field error: {ex.Message}", Color.Yellow)
            Return
        End Try

        ' F-1 (review-autotrade-tiein): a payload without join identity never enters the chain -
        ' the soak reviewers join on (instance_id, signal_id), so an id-less payload gets a yellow
        ' log and NO disposition row. Unreachable from the fixture-pinned emitter; defends against
        ' hand-crafted/foreign files.
        If p.InstanceId.Length = 0 OrElse p.SignalId < 0 Then
            _log($"payload rejected: missing identity (instance_id '{p.InstanceId}', signal_id {p.SignalId})", Color.Yellow)
            Return
        End If

        ' ---- status snapshot first (informational; updates even when the payload is refused) ----
        Dim fresh As Boolean = (DateTime.UtcNow - p.GeneratedUtc).TotalMinutes <= 2.5R * Math.Max(p.ExecResolutionMin, 1)
        SyncLock _sync
            _engineArmed = p.EngineArmed
            _lastPayloadGeneratedUtc = p.GeneratedUtc
            _lastExecResMin = Math.Max(p.ExecResolutionMin, 1)
            If fresh Then
                _staleChecks = 0
                _staleAlerted = False
                If p.Direction <> "NONE" AndAlso p.Atr > 0D Then _lastSignalAtr = p.Atr
            Else
                _lastSignalAtr = 0D
            End If
            _lastSignalSummary = $"#{p.SignalId} {p.Verdict} ({p.Confidence}/{p.Direction})"
        End SyncLock

        ' Engine ARM off in a payload is a disarm event (contract section 6) - a fresh payload
        ' clears the stale counter above but never re-starts.
        If Not p.EngineArmed Then ForceStop("engine ARM off in latest payload")

        ' Same (instance_id, signal_id) already disposed this session (FSW double-fire, startup re-read):
        ' every consumed payload gets EXACTLY one disposition line - suppress the re-read silently.
        SyncLock _sync
            If p.InstanceId = _lastSeenInstanceId AndAlso p.SignalId = _lastSeenSignalId Then
                RaiseEvent StatusChanged()
                Return
            End If
            _lastSeenInstanceId = p.InstanceId
            _lastSeenSignalId = p.SignalId
        End SyncLock

        ' ---- gate chain, exactly contract section 4 order; first failing gate = the disposition ----
        Dim disposition As String = Nothing

        ' 4.1 schema version (the ONLY version gates read; settings_version is informational)
        If p.SchemaVersion <> 1 Then
            disposition = "refused: schema_version"
            _log($"ALERT: payload schema_version {p.SchemaVersion} <> 1 - consumer update required, no action taken", Color.Red)
        End If

        ' 4.2 freshness + SKIPPED (stand down; never "hold the last signal")
        If disposition Is Nothing AndAlso Not fresh Then
            disposition = "stale"
            ForceStop("stale payload")
        End If
        If disposition Is Nothing AndAlso p.SignalState = "SKIPPED" Then
            disposition = "skipped"
            ForceStop("SKIPPED payload (engine stand-down)")
        End If

        ' 4.3 de-dupe against the persisted acted pair. signal_id is monotonic per instance_id, so
        ' anything <= the last acted id from the same engine process is a replay, never a new signal.
        If disposition Is Nothing AndAlso p.InstanceId = _lastActedInstanceId AndAlso p.SignalId <= _lastActedSignalId Then
            disposition = "duplicate"
        End If

        ' 4.4 action mapping (informational fields - verdict/skip_reason/cap_reason/scores/kelly/
        ' structural/settings_version - are NEVER gated on; BELOW_MIN_MOVE is the only pinned
        ' verdict_context value; WEAK carries its direction and is refused by the TIER gate;
        ' health.ws: only DOWN blocks - REST/DEGRADED/OK all pass)
        If disposition Is Nothing Then
            If p.SignalState <> "OK" Then
                disposition = "refused: signal_state"
            ElseIf p.Direction = "NONE" Then
                disposition = "refused: direction"
            ElseIf p.StopLevel <= 0D OrElse p.Target <= 0D Then
                ' F-1: missing/zero levels on an actionable direction would reach placement as
                ' manualTP/manualSL 0 and the host would silently fall back to OFFSET-DERIVED
                ' levels - a partial-apply, forbidden by the agreed failure semantics and R2
                ' (engine levels placed as-is). Reject + log instead.
                disposition = "refused: levels"
            ElseIf Not Tiers.Contains(p.Confidence) Then
                disposition = "refused: tier"
            ElseIf p.MtfBlocked Then
                disposition = "refused: mtf_blocked"
            ElseIf p.VerdictContext = "BELOW_MIN_MOVE" Then
                disposition = "refused: below_min_move"
            ElseIf p.LedgerMismatch Then
                disposition = "refused: ledger_mismatch"
            ElseIf p.WsHealth = "DOWN" Then
                disposition = "refused: ws_down"
            End If
        End If

        ' 4.5 dual-arm interlock - LIVE MODE ONLY (log-only places nothing and may run un-armed)
        If disposition Is Nothing AndAlso _mode = BridgeMode.Live Then
            If Not (_localArmed AndAlso _started AndAlso p.EngineArmed) Then
                disposition = "refused: interlock"
            End If
        End If

        ' 4.6 operational gates, contract order: connected, rate-limit, flat, no working entry,
        ' (cancel-pending lives inside PlaceAutomatedOrder - surfaces as rejected: cancel pending),
        ' cooloff, circuit breaker, session window
        If disposition Is Nothing Then
            Dim breaker As Decimal = CircuitBreakerUsd
            Dim cooloff As Decimal = CooloffMin
            Dim breakerBreached As Boolean = breaker > 0D AndAlso _host.SessionPnLUSD <= -breaker
            If Not _host.IsWebSocketConnected Then
                disposition = "refused: not_connected"
            ElseIf Not _host.CanMakeAPIRequest Then
                disposition = "refused: rate_limit"
            ElseIf Not _host.IsFlat Then
                disposition = "refused: not_flat"
            ElseIf _host.HasWorkingEntryOrder Then
                disposition = "refused: working_entry"
            ElseIf _lastActionUtc <> DateTime.MinValue AndAlso cooloff > 0D AndAlso
                   (DateTime.UtcNow - _lastActionUtc).TotalMinutes < CDbl(cooloff) Then
                disposition = "refused: cooloff"
            ElseIf breakerBreached Then
                disposition = "refused: circuit_breaker"
                ForceStop($"circuit breaker tripped (session PnL {_host.SessionPnLUSD.ToString("F2", CultureInfo.InvariantCulture)} USD <= -{breaker.ToString(CultureInfo.InvariantCulture)})")
            ElseIf Not IsInsideSessionWindow() Then
                disposition = "refused: window"
            ElseIf SizeUsd <= 0D Then
                ' Local precondition, appended AFTER the contract-ordered gates: size now comes from
                ' the main form's Amount box, so an empty/zero box would otherwise reach the exchange
                ' as a zero-amount order and come back as a rejection. Refuse it here instead.
                disposition = "refused: size"
            End If
        End If

        ' ---- all gates green: act (Live) or log the would-be action (LogOnly - the soak's whole point) ----
        If disposition Is Nothing Then
            Dim isLong As Boolean = p.Direction = "LONG"
            Dim inv As CultureInfo = CultureInfo.InvariantCulture
            If _mode = BridgeMode.Live Then
                ' Levels per contract section 5 + section 3 semantics: target = TP limit as-is (R2);
                ' manualSL = the stop-limit's LIMIT leg, one execution offset beyond the engine's stop
                ' (the app derives trigger = limit +/- StopLimitOffset, so the TRIGGER lands exactly
                ' on the engine's stop); entry is a reference only - the app enters at top-of-book
                ' under its own slippage cap.
                ' Size is deliberately NOT passed: it already IS the main form's Amount box, which is
                ' where SetTradeTargets would write it. Writing it back would be a no-op at best.
                Dim manualSl As Decimal = If(isLong, p.StopLevel - _host.StopLimitOffset, p.StopLevel + _host.StopLimitOffset)
                _host.SetTradeTargets(manualTP:=p.Target, manualSL:=manualSl)
                Dim result As frmMainPageV2.PlacementResult = Await _host.PlaceAutomatedOrder(If(isLong, "long", "short"), "limit")
                If result.Accepted Then
                    disposition = $"acted (id {result.OrderId})"
                    RecordActed(p)
                Else
                    disposition = $"rejected: {result.Reason}"
                End If
            Else
                disposition = $"would-act: {p.Direction} @ {p.Entry.ToString(inv)}, stop {p.StopLevel.ToString(inv)}, " &
                              $"target {p.Target.ToString(inv)}, size {SizeUsd.ToString(inv)}"
                ' Advance the de-dupe watermark in log-only too, so the soak's disposition stream is
                ' gate-for-gate identical to what live mode would have produced. (The cooloff anchor
                ' is NOT advanced here - it starts at the position close, and log-only opens none.)
                RecordActed(p)
            End If
        End If

        EmitDisposition(p, disposition)
        RaiseEvent StatusChanged()
    End Function

    Private Async Function ReadPayloadTextAsync() As Task(Of String)
        For attempt As Integer = 0 To 1
            Dim retryable As Boolean = False
            Try
                Return File.ReadAllText(_payloadPath)
            Catch ex As FileNotFoundException
                Return Nothing ' no payload yet - the staleness timer owns the alerting
            Catch ex As DirectoryNotFoundException
                Return Nothing
            Catch ex As IOException
                If attempt = 0 Then
                    retryable = True
                Else
                    _log($"payload read failed twice: {ex.Message}", Color.Yellow)
                End If
            End Try
            If retryable Then Await Task.Delay(100) ' one retry on a share violation mid-replace
        Next
        Return Nothing
    End Function

    Private Shared Function ParsePayload(json As JObject) As PayloadSnapshot
        Dim p As New PayloadSnapshot With {
            .SchemaVersion = If(json.SelectToken("schema_version")?.ToObject(Of Integer)(), 0),
            .SignalId = If(json.SelectToken("signal_id")?.ToObject(Of Long)(), -1L),
            .InstanceId = If(json.SelectToken("engine.instance_id")?.ToString(), ""),
            .EngineArmed = If(json.SelectToken("engine.autotrade_armed")?.ToObject(Of Boolean)(), False),
            .SignalState = If(json.SelectToken("signal_state")?.ToString(), ""),
            .Verdict = If(json.SelectToken("verdict")?.ToString(), ""),
            .Confidence = If(json.SelectToken("confidence")?.ToString(), ""),
            .Direction = If(json.SelectToken("direction")?.ToString(), "NONE"),
            .VerdictContext = If(json.SelectToken("verdict_context")?.ToString(), ""),
            .MtfBlocked = If(json.SelectToken("mtf_blocked")?.ToObject(Of Boolean)(), False),
            .ExecResolutionMin = If(json.SelectToken("exec_resolution_min")?.ToObject(Of Integer)(), 1),
            .Atr = If(json.SelectToken("atr")?.ToObject(Of Decimal)(), 0D),
            .LedgerMismatch = If(json.SelectToken("health.ledger_mismatch")?.ToObject(Of Boolean)(), False),
            .WsHealth = If(json.SelectToken("health.ws")?.ToString(), "OK")
        }

        Dim genTok As String = If(json.SelectToken("generated_at_utc")?.ToString(), "")
        Dim gen As DateTime
        If DateTime.TryParse(genTok, CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, gen) Then
            p.GeneratedUtc = gen
        Else
            p.GeneratedUtc = DateTime.MinValue ' unparseable timestamp reads as maximally stale
        End If

        If p.Direction = "LONG" OrElse p.Direction = "SHORT" Then
            Dim key As String = p.Direction.ToLowerInvariant()
            p.Entry = If(json.SelectToken($"levels.{key}.entry")?.ToObject(Of Decimal)(), 0D)
            p.StopLevel = If(json.SelectToken($"levels.{key}.stop")?.ToObject(Of Decimal)(), 0D)
            p.Target = If(json.SelectToken($"levels.{key}.target")?.ToObject(Of Decimal)(), 0D)
        End If
        Return p
    End Function

    ' UTC+8 session window, contract section 4.6: entries only INSIDE the configured window; BOTH
    ' boxes blank = unrestricted. Spans midnight when start > end (e.g. 22:00 - 02:00).
    ' FAIL-CLOSED on garbage: a non-blank value that will not parse refuses the entry rather than
    ' reading as "unrestricted" - otherwise the window gate would silently vanish for as long as the
    ' text is malformed. (The settings form also refuses to commit unparseable text, so this is the
    ' second line of defence, not the first.)
    Private Function IsInsideSessionWindow() As Boolean
        Dim s As AutoTradeSettings = Settings
        If s Is Nothing Then Return True
        Dim ws As String = If(s.WindowStart, "").Trim(), we As String = If(s.WindowEnd, "").Trim()
        If ws.Length = 0 AndAlso we.Length = 0 Then Return True ' both blank = no time restriction
        Dim tStart, tEnd As TimeSpan
        If Not TimeSpan.TryParse(ws, tStart) OrElse Not TimeSpan.TryParse(we, tEnd) Then Return False
        Dim nowT As TimeSpan = DateTime.UtcNow.AddHours(8).TimeOfDay
        If tStart <= tEnd Then
            Return nowT >= tStart AndAlso nowT <= tEnd
        End If
        Return nowT >= tStart OrElse nowT <= tEnd ' spans midnight (e.g. 22:00 - 02:00)
    End Function

    ' Records the acted/would-acted signal as the de-dupe watermark. Note it does NOT start the
    ' cooloff: the cooloff anchors on the position CLOSE (NotifyPositionClosed), because the flat
    ' gate already blocks entries for the whole life of the trade - a placement-anchored cooloff
    ' would burn off during the position and give no pause at all after the exit.
    Private Sub RecordActed(p As PayloadSnapshot)
        SyncLock _sync
            _lastActedInstanceId = p.InstanceId
            _lastActedSignalId = p.SignalId
        End SyncLock
        PersistState()
    End Sub

    ' Called by the host from CompletePositionClose (the single once-per-close completion path) -
    ' starts the cooloff clock from the moment the position goes flat. Any thread.
    Public Sub NotifyPositionClosed()
        SyncLock _sync
            _lastActionUtc = DateTime.UtcNow
        End SyncLock
        Dim cooloff As Decimal = CooloffMin
        If _mode <> BridgeMode.Off AndAlso cooloff > 0D Then
            _log($"cooloff started: {cooloff.ToString(CultureInfo.InvariantCulture)} min from position close", Color.Gray)
        End If
        RaiseEvent StatusChanged()
    End Sub

    ' One line per consumed payload: host log + append-only bridge-dispositions.log
    ' (contract section 4 commitment; v2 feedback-file precursor). Format and tokens are
    ' soak-stable: utc | instance_id | signal_id | verdict | confidence | direction | disposition
    Private Sub EmitDisposition(p As PayloadSnapshot, disposition As String)
        SyncLock _sync
            _lastDisposition = disposition
        End SyncLock

        Dim line As String = String.Join(" | ",
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            p.InstanceId, p.SignalId.ToString(CultureInfo.InvariantCulture),
            p.Verdict, p.Confidence, p.Direction, disposition)
        Try
            File.AppendAllText(DispositionLogPath, line & Environment.NewLine)
        Catch ex As Exception
            _log($"disposition log write failed: {ex.Message}", Color.Yellow)
        End Try

        Dim c As Color
        If disposition.StartsWith("acted", StringComparison.Ordinal) Then
            c = Color.LimeGreen
        ElseIf disposition.StartsWith("would-act", StringComparison.Ordinal) Then
            c = Color.Cyan
        ElseIf disposition.StartsWith("rejected", StringComparison.Ordinal) Then
            c = Color.Red
        ElseIf disposition = "stale" OrElse disposition = "skipped" Then
            c = Color.Yellow
        Else
            c = Color.Gray ' refused: <gate> / duplicate
        End If
        _log($"signal #{p.SignalId} {p.Verdict} ({p.Confidence}/{p.Direction}) -> {disposition}", c)
    End Sub

    Private Class PayloadSnapshot
        Public SchemaVersion As Integer
        Public SignalId As Long
        Public GeneratedUtc As DateTime
        Public InstanceId As String = ""
        Public EngineArmed As Boolean
        Public SignalState As String = ""
        Public Verdict As String = ""
        Public Confidence As String = ""
        Public Direction As String = "NONE"
        Public VerdictContext As String = ""
        Public MtfBlocked As Boolean
        Public ExecResolutionMin As Integer = 1
        Public Atr As Decimal
        Public LedgerMismatch As Boolean
        Public WsHealth As String = "OK"
        Public Entry As Decimal
        Public StopLevel As Decimal
        Public Target As Decimal
    End Class

End Class
