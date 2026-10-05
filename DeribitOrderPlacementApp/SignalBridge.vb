Option Strict On
Option Explicit On

Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports Newtonsoft.Json
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
' Mode Off is not consumption: the watcher still runs, but only to read the payload's ATR for the
' host's slippage guard (docs/spec-frmindicators-retirement.md R2) - no disposition, no act.
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

    ' The committed session policy (docs/spec-session-policy-gate.md section 3), live-read off the
    ' settings form exactly like TiersCsv above. The form only ever REFERENCE-SWAPS an immutable
    ' snapshot, so this single reference read is safe from the FSW/timer/processing threads and can
    ' never observe a half-committed config. Never Nothing in practice; callers guard anyway.
    Private ReadOnly Property SessionPolicy As SessionPolicyConfig
        Get
            Dim s As AutoTradeSettings = Settings
            If s Is Nothing Then Return Nothing
            Return s.SessionPolicy
        End Get
    End Property

    ' The size multiplier in force for a payload's session. 1.0 (the identity) whenever the policy is
    ' absent or disabled, so the act path's arithmetic is unchanged until the owner opts in.
    Private Function PolicySizeMultFor(p As PayloadSnapshot) As Decimal
        Dim pol As SessionPolicyConfig = SessionPolicy
        If pol Is Nothing OrElse Not pol.Enabled Then Return 1D
        Return pol.RuleFor(SessionBucketForPayload(p.GeneratedUtc)).SizeMult
    End Function

    ' Order size = the main form's Amount box (already mirrored into an engine field there, so this
    ' is a plain field read). One size for manual and automated entries - no second place to set it.
    Private ReadOnly Property SizeUsd As Decimal
        Get
            Return _host.OrderSizeUSD
        End Get
    End Property

    ' N2 (docs/spec-risk-sized-bridge-trades.md §2): whether bridge trades are risk-sized off the
    ' ENGINE's own stop distance instead of the Amount box. Live-read off the settings form exactly
    ' like TiersCsv above - a plain Boolean there, written only on the UI thread, so this single read
    ' is safe from the FSW/timer/processing threads. No settings form (or no tick) = False = today's
    ' Amount-box sizing, so the feature is off by construction rather than by care.
    Private ReadOnly Property RiskSizeBridgeTrades As Boolean
        Get
            Dim s As AutoTradeSettings = Settings
            Return s IsNot Nothing AndAlso s.RiskSizeBridgeTrades
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
    Private _lastSignalAtr As Decimal = 0D               ' last fresh OK payload's atr, in EVERY mode (Off too -
    '                                                      spec-frmindicators-retirement.md R2); 0 when none/stale.
    '                                                      Read on the receive thread (GetEffectiveAtr): plain
    '                                                      field, accepted Decimal torn-read class.
    Private _lastDisposition As String = ""
    Private _lastSignalSummary As String = ""
    Private _lastActionUtc As DateTime = DateTime.MinValue   ' cooloff anchor: position CLOSE only (NotifyPositionClosed)
    Private _staleChecks As Integer = 0                  ' consecutive stale staleness-timer checks
    Private _staleAlerted As Boolean = False
    Private _lastSeenInstanceId As String = ""           ' in-memory only: suppresses double-dispositions
    Private _lastSeenSignalId As Long = Long.MinValue    ' when FSW double-fires on the same payload

    ' C1 v2 feedback, last_signal (spec section 3.3): Nothing until this executor process consumes
    ' its first payload, then swapped BY REFERENCE at consumption - once. The section-4
    ' disposition-cardinality freeze extends here: a post-acted chase abort updates the host log's
    ' cancel REASON and must NOT touch this, exactly as it must not write a second disposition row.
    Private _feedbackLastSignal As ExecutorFeedback.LastSignalRef = Nothing

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
        ' R2 (docs/spec-frmindicators-retirement.md): the watcher runs from construction, because the
        ' bridge starts in mode Off on every launch and the slippage guard takes its ATR from the
        ' engine payload in Off too. Off is a READ-ONLY ATR tap: EvaluateLatestPayloadAsync returns
        ' right after the status snapshot, so no disposition, no de-dupe mark, no act.
        StartWatching()
        EvaluateNow()
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
                ' R2 (docs/spec-frmindicators-retirement.md): Off no longer stops the watcher - it
                ' stays on as a read-only ATR tap. The stale counter resets here, as StopWatching
                ' used to reset it, so a later Off -> Log-only starts a fresh 3-check count.
                SyncLock _sync
                    _staleChecks = 0
                    _staleAlerted = False
                End SyncLock
                _log("mode Off - watcher stays on for the engine ATR only (read-only: no dispositions, no acts)", Color.Gray)
            Else
                StartWatching()
                _log($"mode {value} - watching {_payloadPath}", Color.DodgerBlue)
                EvaluateNow() ' initial read so status/dispositions don't wait for the next engine run
            End If
            RaiseEvent StatusChanged()
            _host.PublishExecutorFeedback() ' C1 trigger (c): mode transition
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
            _host.PublishExecutorFeedback() ' C1 trigger (c): ARM transition
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

    ' Live-and-started - the state LogTradeDecision gating reads (replaces the retired indicator form's IsAutoTradingEnabled).
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

    ' ---- C1 v2 feedback: the executor-state reads the emitter's snapshot takes (spec section 3.1) ----
    ' All three are callable from ANY thread: two are plain field reads, and BreakerTripped composes
    ' a settings-form-owned Decimal with a host field through the SHARED gate predicate, so the
    ' emitted value and gate 4.6 can never disagree.

    Public ReadOnly Property BreakerTripped As Boolean
        Get
            Return IsBreakerTripped(CircuitBreakerUsd, _host.SessionPnLUSD)
        End Get
    End Property

    ' One reference read of an immutable object - coherent by construction, never half-updated.
    Friend ReadOnly Property FeedbackLastSignal As ExecutorFeedback.LastSignalRef
        Get
            Return _feedbackLastSignal
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
        _host.PublishExecutorFeedback() ' C1 trigger (c): START transition (published on refusal too -
        '                                 the snapshot is content-gated, so a refused START writes nothing)
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

    ' Friend (was Private) so ExecutorFeedback reads its feedback_output_path out of the SAME
    ' bridge.json this class reads, from ONE expression rather than a second copy of the filename.
    Friend Shared ReadOnly Property ConfigFilePath As String
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
                    ' Watch Changed, Created AND Renamed because THE WRITER'S API IS NOT OURS TO
                    ' ASSUME. An atomic write surfaces as a different event depending on how the
                    ' other side implements it (Replace vs Move vs plain write), and that is another
                    ' repo's implementation detail which may change without notice. Subscribing to
                    ' all three is correct for any of them.
                    '
                    ' This used to read "File.Replace on the engine side surfaces as rename/change".
                    ' True when written, and a claim about the ENGINE's internals living in OUR code.
                    ' Reworded 2026-08-04: the engine seat is queuing a swap of File.Replace ->
                    ' File.Move, which would have falsified the stated reason while leaving this code
                    ' correct - a comment that is wrong for a reason nothing here can detect, which
                    ' is the worst shape a comment can be in.
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
            ' Called on dispose only (mode Off keeps the watcher - R2, which supersedes the 2026-07-16
            ' "bridge Off = back on our own 14-period ATR" ruling). Zeroed so nothing can quote a frozen
            ' engine ATR once the staleness timer that would otherwise zero it is stopped below.
            _lastSignalAtr = 0D
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
        If _disposed Then Return
        If IsFreshNow Then
            SyncLock _sync
                _staleChecks = 0
                _staleAlerted = False
            End SyncLock
            Return
        End If
        ' R2: in mode Off the tick ONLY drops a stale engine ATR, so the guard falls to the Flat ATR.
        ' No stale count, no stand-down, no alert - the owner trading manually is not paged about
        ' the engine.
        If _mode = BridgeMode.Off Then
            SyncLock _sync
                _lastSignalAtr = 0D
            End SyncLock
            Return
        End If
        Dim checkCount As Integer
        Dim alertNow As Boolean = False
        SyncLock _sync
            _staleChecks += 1
            checkCount = _staleChecks
            _lastSignalAtr = 0D ' stale => no bridge ATR (slippage guard falls back to the Flat ATR)
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
            RemoteNotifier.Post("OrderApp bridge", $"auto-STOP: {cause}", priority:="urgent") ' Q1
            RaiseEvent StatusChanged()
            _host.PublishExecutorFeedback() ' C1 trigger (c): STOP transition (started -> false)
        End If
    End Sub

    ' ================================ payload processing ================================

    ' Single-flight entry point: one payload evaluated at a time; a newer file event supersedes
    ' a queued one (only the latest file content is ever evaluated - we re-read from disk per pass).
    ' Runs in mode Off too (R2): the pass stops after the status snapshot there.
    Private Sub EvaluateNow()
        If _disposed Then Return
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
            json = ParsePayloadJson(text)
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

        ' An unparseable timestamp still stands down (below, as "stale") because freshness cannot be
        ' established - but say so explicitly. The contract pins ISO-8601-with-Z, so this means the
        ' payload is malformed, NOT that the engine is dead, and those need different fixes.
        If Not p.TimestampOk Then
            _log($"payload #{p.SignalId}: generated_at_utc is not ISO-8601 ('{p.GeneratedAtRaw}') - " &
                 "cannot establish freshness, standing down. This is a MALFORMED PAYLOAD, not a dead engine.", Color.Red)
        End If

        ' ---- status snapshot first (informational; updates even when the payload is refused) ----
        Dim fresh As Boolean = (DateTime.UtcNow - p.GeneratedUtc).TotalMinutes <= 2.5R * Math.Max(p.ExecResolutionMin, 1)
        SyncLock _sync
            _engineArmed = p.EngineArmed
            _lastPayloadGeneratedUtc = p.GeneratedUtc
            _lastExecResMin = Math.Max(p.ExecResolutionMin, 1)
            ' ATR for the host's slippage guard (owner ruling 2026-07-16). Refresh from EVERY fresh
            ' OK payload, NO TRADE included: atr is an execution-resolution market measurement, not a
            ' trade decision - the verdict is about conviction, the ATR is about volatility. The
            ' contract's "guaranteed non-zero when direction <> NONE" is a GUARANTEE, not a claim that
            ' other payloads' atr is junk (a live NO TRADE payload carries a real one).
            '
            ' Gating this on direction <> NONE (as it first shipped) meant a lone WEAK signal's atr was
            ' held INDEFINITELY through the NO TRADE stretches that are most of the session, while
            ' fresher values streamed past every run - observed live: 34.91 held from a WEAK SHORT
            ' while the current payload said 24.78, leaving the guard ~41% too loose.
            '
            ' Zeroed on stale/SKIPPED (contract 4.2: never "hold the last signal"), so the guard falls
            ' back to the host's Flat ATR rather than freezing on a stale engine number. NOT zeroed on
            ' mode Off any more: R2 (docs/spec-frmindicators-retirement.md, owner 2026-10-06) makes
            ' the engine ATR the guard's ATR in every mode, and supersedes the 2026-07-16 ruling that
            ' "bridge Off" meant "back on the app's own 14-period ATR" - that ATR is retired.
            If fresh AndAlso p.SignalState = "OK" Then
                _staleChecks = 0
                _staleAlerted = False
                If p.Atr > 0D Then _lastSignalAtr = p.Atr
            ElseIf fresh Then
                ' Fresh but SKIPPED: engine stood down, so do not keep quoting its last ATR.
                _staleChecks = 0
                _staleAlerted = False
                _lastSignalAtr = 0D
            Else
                _lastSignalAtr = 0D
            End If
            ' Not in Off: the "Last:" line pairs this summary with _lastDisposition, and Off writes no
            ' disposition - a new summary beside an old payload's disposition would mislabel it.
            If _mode <> BridgeMode.Off Then _lastSignalSummary = $"#{p.SignalId} {p.Verdict} ({p.Confidence}/{p.Direction})"
        End SyncLock

        ' R2 (docs/spec-frmindicators-retirement.md): mode Off is a READ-ONLY ATR tap and stops HERE.
        ' Nothing below runs in Off - no ForceStop, no de-dupe mark, no gate chain, no disposition
        ' row, no act, no feedback publish. The de-dupe pair is deliberately NOT marked, so Off ->
        ' Log-only still gives the current payload its one disposition via the mode setter's
        ' EvaluateNow, exactly as before.
        If _mode = BridgeMode.Off Then
            RaiseEvent StatusChanged()
            Return
        End If

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
        If disposition Is Nothing AndAlso IsDuplicateOf(p.InstanceId, p.SignalId, _lastActedInstanceId, _lastActedSignalId) Then
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

        ' 4.4b session policy (docs/spec-session-policy-gate.md; consumer-side, contract addendum
        ' 2026-07-21). The trader's own per-session subset filter, deliberately placed AFTER the whole
        ' contract-4.4 chain - including the GLOBAL tier gate - so:
        '   * every payload the contract itself would refuse keeps its exact contract token, and the
        '     soak's gate-for-gate join semantics stay intact even once this is enabled;
        '   * only signals the ENGINE considers actionable can be policy-refused, so these rows
        '     measure precisely "engine-actionable, declined by my policy" - the counterfactual the
        '     whole feature exists to produce, joinable on the session named in the token.
        ' It can only ever NARROW what the global Tiers box allows (D1: both gates, intersection).
        ' Runs in Log-only too: post-enable, the soak stream shows exactly what the policy declines.
        If disposition Is Nothing Then
            Dim pol As SessionPolicyConfig = SessionPolicy   ' one reference read - immutable snapshot
            If pol IsNot Nothing AndAlso pol.Enabled Then
                ' GeneratedUtc = MinValue cannot reach here: gate 4.2 freshness refused it already.
                disposition = PolicyRefusalFor(pol, SessionBucketForPayload(p.GeneratedUtc),
                                               p.Confidence, p.VerdictContext)
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
            ' C1 spec section 3.1: the SHARED seam, not a second copy of the expression. The gate
            ' below and executor.breaker_tripped in the feedback file are now the same predicate by
            ' construction, so they cannot drift and the emitted value is fixture-pinnable.
            Dim breakerBreached As Boolean = IsBreakerTripped(breaker, _host.SessionPnLUSD)
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

            ' Session policy size_mult (spec section 4), applied EXACTLY ONCE and only here, at the
            ' act / would-act site: every gate above - including refused: size - reads the raw
            ' SizeUsd. Unity passes through untouched, so with the policy off (or any session at
            ' mult 1.0) this is the identity and the size below is byte-for-byte what it was before.
            ' When the stop-distance formula later becomes the bridge's size source, this folds in as
            ' that formula's sessionFactor term - ONE formula, never stacked hidden multipliers.
            ' rawSize IS THE AMOUNT BOX AND MUST STAY BOUND TO IT (N2 spec §1, amended - this is the
            ' defect the implementer seat raised before writing any code). The sizeUsdOverride
            ' elision below compares against rawSize, and 0 there means "read the Amount box exactly
            ' as before". Re-pointing rawSize at the risk-sized base makes the two equal at unity -
            ' i.e. WHENEVER SESSION POLICY IS OFF, THE DEFAULT - so the override would elide to 0 and
            ' the Amount box would be placed while the log printed the risk size. Live-only, silent,
            ' and Log-only mode cannot see it. The risk size therefore travels as its OWN variable.
            Dim rawSize As Decimal = SizeUsd
            Dim baseSize As Decimal = rawSize
            ' Disabled (the shipped default) does not even compute - the branch is the byte-identity.
            If RiskSizeBridgeTrades Then baseSize = RiskSizedBaseForPayload(p, rawSize)
            Dim sizeMult As Decimal = PolicySizeMultFor(p)
            ' The sessionFactor fold, applied EXACTLY ONCE and only here, to whichever base is in
            ' force. Unity passes through untouched, so with the policy off this is the identity.
            Dim effectiveSize As Decimal = EffectiveSizeUsd(baseSize, sizeMult)
            ' baseSize, not rawSize: it is the value actually clamped. Disabled => baseSize = rawSize
            ' => this line is character-for-character what it was before N2.
            If EffectiveSizeWasClamped(baseSize, sizeMult) Then
                _log($"size_mult {sizeMult.ToString(inv)} clamped to contract min 10 " &
                     $"(raw size {baseSize.ToString(inv)})", Color.Yellow)
            End If

            If _mode = BridgeMode.Live Then
                ' Levels per contract section 5 + section 3 semantics: target = TP limit as-is (R2,
                ' satisfied to the exchange tick grid - docs/spec-tick-rounding.md: engine levels are
                ' ATR-derived fractionals and off-tick prices reject -32602); manualSL = the stop-limit's
                ' LIMIT leg, one execution offset beyond the engine's stop, tick-rounded (the app derives
                ' trigger = limit +/- StopLimitOffset, so the TRIGGER lands within a quarter-tick of the
                ' engine's stop); entry is a reference only - the app enters at top-of-book
                ' under its own slippage cap.
                ' Size is deliberately NOT passed: it already IS the main form's Amount box, which is
                ' where SetTradeTargets would write it. Writing it back would be a no-op at best.
                Dim manualSl As Decimal = DeriveManualSl(isLong, p.StopLevel, _host.StopLimitOffset)
                _host.SetTradeTargets(manualTP:=frmMainPageV2.RoundToTick(p.Target), manualSL:=manualSl)
                ' Q2 (docs/spec-quickwins-notifier-signalcols.md): stage the signal tag BEFORE the
                ' placement is sent - the entry-fill echo can beat the placement ack, and the fill
                ' is what promotes the tag onto the position.
                _host.SetPendingSignalTag(p.SignalId, p.Confidence)
                ' The size override is passed ONLY when the computed size actually differs from the
                ' AMOUNT BOX, so the common path reaches PlaceAutomatedOrder exactly as it did
                ' before these features. The comparison is against rawSize (= the box) and MUST STAY
                ' THAT WAY: 0 means "read the Amount box", so comparing against anything else would
                ' silently place the box whenever the computed size happened to match the base.
                Dim result As frmMainPageV2.PlacementResult =
                    Await _host.PlaceAutomatedOrder(If(isLong, "long", "short"), "limit",
                                                    sizeUsdOverride:=If(effectiveSize <> rawSize, effectiveSize, 0D))
                If result.Accepted Then
                    disposition = $"acted (id {result.OrderId})"
                    RecordActed(p)
                Else
                    disposition = $"rejected: {result.Reason}"
                    ' Q2: a definitive refusal means no order can ever fill from this act - unstage
                    ' the tag so it cannot attach to a later manual trade. "timeout" is deliberately
                    ' NOT definitive (the order may exist; echoes are the source of truth - the fill,
                    ' if it comes, still promotes this stage; a dead stage dies at the next teardown
                    ' or is overwritten by the next act).
                    If result.Reason <> "timeout" Then _host.ClearPendingSignalTag()
                End If
            Else
                ' Levels display at 2dp (owner request 2026-07-17) - engine emits full-precision
                ' doubles (e.g. stop 62881.5767038064). Display precision only: the values stay
                ' engine-raw (NOT tick-rounded - that is placement mechanics, spec-tick-rounding.md
                ' section 2), and the token prefix/format stays soak-stable.
                disposition = $"would-act: {p.Direction} @ {p.Entry.ToString("F2", inv)}, stop {p.StopLevel.ToString("F2", inv)}, " &
                              $"target {p.Target.ToString("F2", inv)}, size {effectiveSize.ToString(inv)}"
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

    ' Parse the payload with date auto-conversion OFF.
    '
    ' Newtonsoft's default DateParseHandling.DateTime turns any ISO-8601-looking STRING into a Date
    ' token, and JToken.ToString() then renders that token in the CURRENT CULTURE. On this machine
    ' (en-MY, d/M/yyyy) "2026-07-15T16:50:08Z" came back out of ToString() as "15/7/2026 4:50:08 PM",
    ' our InvariantCulture (M/d/yyyy) TryParse rejected month "15", and the payload fell to
    ' DateTime.MinValue = maximally stale. Net effect: EVERY payload read as stale and the bridge
    ' could never act - silently, and only on day-first cultures (en-US happened to work, which is
    ' exactly why this survived review). The contract pins ISO-8601-with-Z + invariant culture and
    ' the engine emits precisely that; keeping tokens as raw strings is what honours it.
    ' Friend (was Private) for the OrderCheck logic harness - the day-first-culture parse
    ' fixture pins exactly the failure mode described above. Behavior unchanged.
    Friend Shared Function ParsePayloadJson(text As String) As JObject
        Using reader As New JsonTextReader(New StringReader(text))
            reader.DateParseHandling = DateParseHandling.None
            Return JObject.Load(reader)
        End Using
    End Function

    ' Friend (was Private) for the OrderCheck logic harness - fixtures assert on the parse
    ' defaults (identity, levels, timestamp) that the F-1 guard and the gate chain rely on.
    Friend Shared Function ParsePayload(json As JObject) As PayloadSnapshot
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

        ' With DateParseHandling.None this is the contract's raw ISO-8601 string, not a culture-
        ' rendered date (see ParsePayloadJson). The 'Z' + AssumeUniversal/AdjustToUniversal give UTC.
        Dim genTok As String = If(json.SelectToken("generated_at_utc")?.ToString(), "")
        p.GeneratedAtRaw = genTok
        Dim gen As DateTime
        If DateTime.TryParse(genTok, CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, gen) Then
            p.GeneratedUtc = gen
            p.TimestampOk = True
        Else
            ' Stand down (we cannot establish freshness), but the caller says so OUT LOUD - reading
            ' this as a plain "stale" is what made the culture bug above look like a dead engine.
            p.GeneratedUtc = DateTime.MinValue
            p.TimestampOk = False
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
        Return IsInsideWindowCore(DateTime.UtcNow.AddHours(8).TimeOfDay, s.WindowStart, s.WindowEnd)
    End Function

    ' The window math, extracted (behavior-identical) as an OrderCheck testability seam - the
    ' fixtures PIN the shipped fail-closed semantics; any change here is spec-back material.
    Friend Shared Function IsInsideWindowCore(nowUtc8 As TimeSpan, startText As String, endText As String) As Boolean
        Dim ws As String = If(startText, "").Trim(), we As String = If(endText, "").Trim()
        If ws.Length = 0 AndAlso we.Length = 0 Then Return True ' both blank = no time restriction
        Dim tStart, tEnd As TimeSpan
        If Not TimeSpan.TryParse(ws, tStart) OrElse Not TimeSpan.TryParse(we, tEnd) Then Return False
        If tStart <= tEnd Then
            Return nowUtc8 >= tStart AndAlso nowUtc8 <= tEnd
        End If
        Return nowUtc8 >= tStart OrElse nowUtc8 <= tEnd ' spans midnight (e.g. 22:00 - 02:00)
    End Function

    ' The section-4.3 watermark comparison, extracted (behavior-identical) as an OrderCheck seam.
    ' signal_id is monotonic per instance_id, so same-instance <= watermark is a replay.
    Friend Shared Function IsDuplicateOf(instanceId As String, signalId As Long,
                                         lastInstanceId As String, lastSignalId As Long) As Boolean
        Return instanceId = lastInstanceId AndAlso signalId <= lastSignalId
    End Function

    ' The act-path manualSL derivation, extracted (behavior-identical) as an OrderCheck seam.
    ' manualSL = the stop-limit's LIMIT leg, one execution offset BEYOND the engine's stop, rounded
    ' to the exchange tick grid (docs/spec-tick-rounding.md: engine stops are ATR-derived fractionals
    ' and off-tick prices reject -32602). The offset is integer, so the app-derived trigger
    ' (limit +/- offset) stays on-tick and lands within a quarter-tick (0.25) of the engine's stop.
    Friend Shared Function DeriveManualSl(isLong As Boolean, stopLevel As Decimal, offset As Decimal) As Decimal
        Return frmMainPageV2.RoundToTick(If(isLong, stopLevel - offset, stopLevel + offset))
    End Function

    ' Session policy gate (docs/spec-session-policy-gate.md section 2): the pinned, engine-identical
    ' UTC analysis-session buckets - ASIA 00:00-07:59, LONDON 08:00-12:59, NY 13:00-23:59.
    '
    ' WARNING - two different clocks live in this file, deliberately:
    '   * these buckets are UTC, and describe which market session ANALYSED the signal;
    '   * the Inclusion Time Range (IsInsideWindowCore above) is the owner's UTC+8 LOCAL hard
    '     window, gate 4.6, fail-closed.
    ' Different clocks, different jobs. Do not "unify" them.
    Friend Shared Function SessionBucketFor(utcHour As Integer) As String
        If utcHour >= 13 Then Return "NY"
        If utcHour >= 8 Then Return "LONDON"
        Return "ASIA"
    End Function

    ' The UTC hour a payload was generated in. GeneratedUtc already comes back from ParsePayload as
    ' Kind=Utc (AdjustToUniversal), but the conversion is defensive: reading .Hour off a Local or
    ' Unspecified DateTime would silently bucket signals into the wrong session for anyone east or
    ' west of UTC, and the symptom - a policy that refuses the wrong things at the wrong times -
    ' looks nothing like a timezone bug. Pinned through ParsePayload by an OrderCheck fixture.
    ' (GeneratedUtc = MinValue cannot reach here: gate 4.2 freshness refuses first.)
    Friend Shared Function SessionBucketForPayload(generatedUtc As DateTime) As String
        Dim utc As DateTime = If(generatedUtc.Kind = DateTimeKind.Utc, generatedUtc, generatedUtc.ToUniversalTime())
        Return SessionBucketFor(utc.Hour)
    End Function

    ' The whole 4.4b decision as one pure seam (spec section 3), so the policy's semantics are
    ' fixture-pinned rather than buried in the gate chain. Returns the disposition token, or Nothing
    ' when the policy has nothing to say - which is ALWAYS the case while it is disabled, and is what
    ' makes "disabled = byte-identical disposition stream" true by construction rather than by care.
    ' Tier is checked before context so the token names the FIRST reason, matching the chain's
    ' first-failing-gate convention.
    Friend Shared Function PolicyRefusalFor(policy As SessionPolicyConfig, session As String,
                                            confidence As String, verdictContext As String) As String
        If policy Is Nothing OrElse Not policy.Enabled Then Return Nothing
        Dim rule As SessionPolicyRule = policy.RuleFor(session)
        If Not rule.AllowsTier(confidence) Then Return $"refused: policy({session}/tier)"
        If Not rule.AllowsContext(verdictContext) Then Return $"refused: policy({session}/context)"
        Return Nothing
    End Function

    ' size_mult applied to the raw Amount-box size (spec section 4), as a pure seam.
    '
    ' UNITY PASSES THROUGH UNTOUCHED. The step-floor and the 10-USD clamp apply ONLY to an actual
    ' reduction: a reduced non-step result (25 x 0.5 = 12.5) is unplaceable and must floor, but an
    ' un-reduced size must stay exactly what the trader typed, whatever it is. Flooring at mult 1.0
    ' would silently resize a non-step Amount (25 -> 20) the moment the policy was switched on, which
    ' would break the "enabled with a blank box is byte-identical to today" acceptance and turn a
    ' pure FILTER into a sizing change. (Spec section 4, corrected 2026-07-21.)
    Friend Shared Function EffectiveSizeUsd(rawSizeUsd As Decimal, mult As Decimal) As Decimal
        If mult = 1D Then Return rawSizeUsd
        Return Math.Max(10D, Math.Floor(rawSizeUsd * mult / 10D) * 10D)
    End Function

    ' N2 (docs/spec-risk-sized-bridge-trades.md §1): the act site's risk-sized base, wrapping the
    ' shared seam with this caller's own fail-safe and clamp logging. Returns the size to feed the
    ' sessionFactor fold; returns rawSize (the Amount box) unchanged whenever the formula cannot run.
    '
    ' FAIL-SAFE, amended by ruling to cover EVERY input the formula cannot use - dist <= 0, entry
    ' <= 0, risk <= 0. Only dist was in the original spec, on the premise that "the levels guard has
    ' already refused stop/target <= 0". It has - but the levels gate at 4.4 reads StopLevel and
    ' Target ONLY; p.Entry is absent from it, and ParsePayload defaults a missing entry to 0. So a
    ' payload with good stop/target and no entry reaches here with dist > 0, computes riskSize 0, and
    ' would clamp to a live trade at the contract minimum with nothing in the log to say why. Adding
    ' entry to `refused: levels` was RULED AGAINST (it changes a frozen disposition token's
    ' behaviour and would alter the soak stream), so the guard lives here, app-side.
    '
    ' Never refuses a signal: a sizing hiccup falls back to the Amount box and says so out loud.
    ' The reads of RiskPerTradeUsd/MaxSizeUsd are plain host field reads off a settings-form-owned
    ' value - the same accepted Decimal torn-read class as LastSignalAtr, and these change only when
    ' the owner edits a Tooling box.
    Private Function RiskSizedBaseForPayload(p As PayloadSnapshot, rawSize As Decimal) As Decimal
        Dim inv As CultureInfo = CultureInfo.InvariantCulture
        Dim riskUsd As Decimal = _host.RiskPerTradeUsd
        Dim maxUsd As Decimal = _host.MaxSizeUsd
        Dim dist As Decimal = Math.Abs(p.Entry - p.StopLevel)

        Dim capped As Decimal = RiskSizedBase(riskUsd, maxUsd, p.Entry, dist)
        If capped < 0D Then
            _log($"risk-size unavailable (entry {p.Entry.ToString(inv)}, stop distance {dist.ToString(inv)}, " &
                 $"risk {riskUsd.ToString(inv)}) - using the Amount box {rawSize.ToString(inv)}", Color.Yellow)
            Return RiskSizedOrFallback(rawSize, capped)
        End If

        Dim sized As Decimal = RiskSizedOrFallback(rawSize, capped)

        ' One yellow line when the cap or the 10-floor binds, so an undersized-risk session is
        ' visible. The uncapped value comes from the same seam with the cap switched off (0 = no
        ' cap), so "did the cap bind" is answered by the formula rather than by a second copy of it.
        Dim uncapped As Decimal = RiskSizedBase(riskUsd, 0D, p.Entry, dist)
        If sized > capped Then
            _log($"risk size {capped.ToString(inv)} clamped up to the contract min 10 " &
                 $"(risk {riskUsd.ToString(inv)} over a {dist.ToString(inv)} stop is under one step)", Color.Yellow)
        ElseIf capped < uncapped Then
            _log($"risk size {uncapped.ToString(inv)} capped to {capped.ToString(inv)} " &
                 $"by max_size_usd {maxUsd.ToString(inv)}", Color.Yellow)
        End If
        Return sized
    End Function

    ' The act site's base-size decision once risk sizing is ON, as a pure seam (N2 §1).
    ' riskSized = whatever RiskSizedBase returned; -1 means the formula could not run.
    '
    '   * cannot compute  => the RAW AMOUNT BOX, unchanged and un-floored. That matters: the box is
    '     the trader's own typed value and the unity-passthrough ruling says an un-reduced size stays
    '     exactly what they typed, whatever it is. Flooring the fallback would resize a non-step
    '     Amount (25 -> 20) the moment a payload arrived with a bad entry.
    '   * otherwise       => clamped UP to the 10-USD contract minimum. The bridge CLAMPS where the
    '     SIZE button REFUSES; the two policies differ deliberately (D3 clamp-and-log: at
    '     live-at-min-size the box IS 10, and refusing would silently kill every signal), which is
    '     exactly why the shared seam takes no position on below-10.
    '
    ' Disabled parity is NOT this function's job - it is the `If RiskSizeBridgeTrades` guard at the
    ' act site, which means the disabled path never computes any of this.
    Friend Shared Function RiskSizedOrFallback(rawSizeUsd As Decimal, riskSized As Decimal) As Decimal
        If riskSized < 0D Then Return rawSizeUsd
        Return Math.Max(10D, riskSized)
    End Function

    ' Risk-based size (docs/spec-risk-sized-bridge-trades.md §1/§4) - THE ONE FORMULA, shared by both
    ' sizing callers: the manual SIZE button (frmMainPageV2.ApplyRiskBasedSize) and the bridge act
    ' site. The body is the button's runtime-verified arithmetic, moved here verbatim; the button now
    ' calls this instead of carrying its own copy.
    '
    ' SIGNATURE (spec §4, amended by ruling): it takes (refPrice, dist), NOT (entry, stop). The
    ' button has no stop PRICE - in offset mode its distance IS the trigger distance and no stop
    ' level exists - so an (entry, stop) seam could only ever be a SECOND COPY of this arithmetic,
    ' which fixtures can pin but cannot bind. One function is what makes drift structurally
    ' impossible, which is the whole point of the extraction.
    '
    ' THE CAP MIRRORS THE BUTTON EXACTLY, and Math.Min would lose both halves of it:
    '   * maxSizeUsd <= 0 means NO CAP (the legitimate way to spell "uncapped" in the hand-edited
    '     settings file). Math.Min would yield 0 and collapse every sized trade to the contract min.
    '   * the cap is itself step-floored, so a non-step cap (505) can never emit an off-step order
    '     size. Math.Min would emit 505 and the exchange would reject it -32602.
    '
    ' Returns -1 when the inputs cannot produce a size. CALLERS OWN THEIR FAIL-SAFE, and they differ:
    ' the bridge falls back to the raw Amount box + one yellow line (never refuse a signal because
    ' sizing math hiccuped); the button keeps its three distinct refusal messages and cannot reach
    ' this arm at all, because its own guards precede the call.
    '
    ' The BELOW-10 POLICY IS DELIBERATELY NOT HERE. The two callers differ and both are ruled: the
    ' button REFUSES and leaves txtAmount alone; the bridge CLAMPS UP to 10 (the D3 clamp-and-log
    ' ruling EffectiveSizeUsd already follows - refusing would silently kill every signal in a
    ' tight-stop session). This function returns the capped, step-floored size and takes no position.
    Friend Shared Function RiskSizedBase(riskUsd As Decimal, maxSizeUsd As Decimal,
                                         refPrice As Decimal, dist As Decimal) As Decimal
        If riskUsd <= 0D OrElse refPrice <= 0D OrElse dist <= 0D Then Return -1D
        Dim size As Decimal = Math.Floor(riskUsd * refPrice / dist / 10D) * 10D
        If maxSizeUsd > 0D AndAlso size > maxSizeUsd Then size = Math.Floor(maxSizeUsd / 10D) * 10D
        Return size
    End Function

    ' The circuit-breaker predicate as a pure seam (C1 spec section 3.1), shared by BOTH callers:
    ' gate 4.6 above and executor.breaker_tripped in the v2 feedback file. Extracted rather than
    ' duplicated for the reason the house keeps extracting these (ShouldSend, EffectiveSizeUsd):
    ' a second copy of a live-trading predicate drifts silently, and the emitted telemetry would
    ' then disagree with the gate that actually stops trading - the worst possible direction for
    ' a field the engine displays as "can the executor act".
    '
    ' breakerUsd <= 0 means NO BREAKER (the legitimate way to spell "off" in the settings form),
    ' so it can never be tripped - that arm is why this is a function and not an inline comparison.
    ' sessionPnLUsd is signed; the breaker is configured as a positive loss magnitude.
    Friend Shared Function IsBreakerTripped(breakerUsd As Decimal, sessionPnLUsd As Decimal) As Boolean
        Return breakerUsd > 0D AndAlso sessionPnLUsd <= -breakerUsd
    End Function

    ' True when EffectiveSizeUsd had to clamp UP to the 10-USD contract minimum (D3: clamp and log,
    ' never refuse - at live-at-min-size the Amount box IS 10, and refusing would silently kill every
    ' signal in a reduced-size session).
    Friend Shared Function EffectiveSizeWasClamped(rawSizeUsd As Decimal, mult As Decimal) As Boolean
        If mult = 1D Then Return False
        Return Math.Floor(rawSizeUsd * mult / 10D) * 10D < 10D
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

    ' Ergonomics item H (docs/spec-execution-ergonomics.md, owner-decided + amended 2026-07-17):
    ' the pure significance predicate for the host-log filter below. True iff the disposition
    ' starts with "acted" or "rejected" (ordinal). "would-act" cannot occur in Live; it returns
    ' False anyway (defensive). Pinned by OrderCheck fixtures.
    Friend Shared Function IsSignificantDisposition(disposition As String) As Boolean
        If disposition Is Nothing Then Return False
        Return disposition.StartsWith("acted", StringComparison.Ordinal) OrElse
               disposition.StartsWith("rejected", StringComparison.Ordinal)
    End Function

    ' One line per consumed payload: host log + append-only bridge-dispositions.log
    ' (contract section 4 commitment; v2 feedback-file precursor). Format and tokens are
    ' soak-stable: utc | instance_id | signal_id | verdict | confidence | direction | disposition
    Private Sub EmitDisposition(p As PayloadSnapshot, disposition As String)
        Dim consumedUtc As DateTime = DateTime.UtcNow
        SyncLock _sync
            _lastDisposition = disposition
            ' C1 spec section 3.3: last_signal is written ONCE, AT CONSUMPTION, and this is the
            ' consumption site - the same single place that owns the one-row-per-payload
            ' disposition append. Hooking here rather than at the act site is what extends the
            ' cardinality freeze into the feedback file: a post-acted chase abort never reaches
            ' this method, so it can no more update last_signal than it can write a second row.
            ' Identity is the ENGINE's pair, and the disposition token is passed through EXACTLY -
            ' it is the soak-stable string the reviewers join on.
            _feedbackLastSignal = New ExecutorFeedback.LastSignalRef(p.InstanceId, p.SignalId,
                                                                    disposition, consumedUtc)
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

        ' Item H (owner ruling + 2026-07-17 amendment, signal #124): the HOST-LOG line only.
        ' Significant dispositions (acted / rejected:) always print; the full chatter stream
        ' prints only outside Live AND while flat with no working entry - so Live is always
        ' quiet, Log-only mid-position/mid-chase is quiet too, and Log-only + flat keeps the
        ' complete soak stream. IsFlat/HasWorkingEntryOrder are plain field-backed host reads
        ' (same as the gate chain - safe from this thread). Everything above this line (the
        ' disposition FILE append, _lastDisposition, and the caller's StatusChanged) is
        ' unconditional - join integrity and the panel label see every disposition.
        ' Q1: significant dispositions (acted / rejected: - the item-H predicate) also go to the
        ' remote notifier. Post is internally fire-and-forget + fail-silent, so this thread (FSW/
        ' timer/processing) is never blocked; the message is the host-log line text, computed once.
        Dim hostLine As String = $"signal #{p.SignalId} {p.Verdict} ({p.Confidence}/{p.Direction}) -> {disposition}"
        If IsSignificantDisposition(disposition) Then RemoteNotifier.Post("OrderApp bridge", hostLine)

        If IsSignificantDisposition(disposition) OrElse
           (_mode <> BridgeMode.Live AndAlso _host.IsFlat AndAlso Not _host.HasWorkingEntryOrder) Then
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
            _log(hostLine, c)
        End If

        ' C1 trigger (a): one publish per consumed payload, from the site that already owns the
        ' one-row-per-payload commitment. Unconditional and BELOW the host-log filter, exactly like
        ' the disposition file append above - the feedback file must see every disposition, not the
        ' filtered chatter stream.
        _host.PublishExecutorFeedback()
    End Sub

    Friend Class PayloadSnapshot
        Public SchemaVersion As Integer
        Public SignalId As Long
        Public GeneratedUtc As DateTime
        Public GeneratedAtRaw As String = ""
        Public TimestampOk As Boolean
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
