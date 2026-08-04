Option Strict On
Option Explicit On

Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

' =====================================================================================================
' ExecutorFeedback - the v2 executor feedback file (executor_feedback.json).
'
' Contract: docs/integration-contract-verdictengine.md SECTION 8 - canonical for emitter behaviour.
' Spec:     docs/spec-c1-feedback-emitter.md - it IMPLEMENTS section 8 and deliberately never
'           restates it, so the contract is the authority on every field, enum and semantic.
'
' Telemetry, never commands (contract 8.1 / R1 in both directions): this app WRITES this file and
' never reads one. Orders and signals flow engine -> app through verdict_signal.json ONLY.
'
' RemoteNotifier.vb is the MODEL here and every rule in its header applies: fire-and-forget,
' fail-silent, inert without configuration, off the caller's thread, one shared resource for the
' app's lifetime. Three BINDING differences from the notifier (spec section 1):
'   * self-coalescing LAST-WINS, not rate-limit-and-drop. A dropped notification is a missed alert;
'     a dropped feedback write leaves the engine on a stale executor picture until the next trigger,
'     so the newest state must always win.
'   * SINGLE WRITER - one serialized worker. Two concurrent writes to one path is precisely the
'     failure this design exists to prevent.
'   * the snapshot is taken on the CALLER's thread and handed over as a flat immutable value; the
'     worker never reaches back into live state.
'
' SHIPS OFF: no feedback_output_path in bridge.json = no timer, no worker, NO FILE CREATED.
' Contract section 8.5: file absent = feature OFF, never an alarm.
' =====================================================================================================
Friend NotInheritable Class ExecutorFeedback

    Private Sub New()
    End Sub

    ' Contract section 8.2: beside the signal file, and the same default on both sides.
    Friend Const DefaultOutputPath As String = "C:\Dev\DeribitBridge\executor_feedback.json"

    ' The FEEDBACK file's own counter (contract section 8.3) - deliberately NOT the v1 signal
    ' schema's version, which this addendum leaves untouched.
    Friend Const SchemaVersion As Integer = 1
    Friend Const AppName As String = "DeribitOrderPlacementApp"

    ' Single-instrument app: every subscription, order and position path is hard-bound to
    ' BTC-PERPETUAL, so the schema's instrument field is a constant rather than a read.
    Friend Const Instrument As String = "BTC-PERPETUAL"

    ' Pinned enum strings (contract section 8.3). Named so a fixture pins the LITERAL, not a
    ' re-typing of it - E2's failure mode was a string that was wrong in one place only.
    Friend Const ModeOff As String = "OFF"
    Friend Const ModeLogOnly As String = "LOG_ONLY"
    Friend Const ModeLive As String = "LIVE"
    Friend Const WsOk As String = "OK"
    Friend Const WsDown As String = "DOWN"
    Friend Const DirLong As String = "LONG"
    Friend Const DirShort As String = "SHORT"
    Friend Const DirFlat As String = "FLAT"

    ' "" = not configured = inert. Set once by LoadConfig at startup and never again, so every
    ' IsConfigured check below is a plain field read from any thread.
    Private Shared _outputPath As String = ""

    ' executor.instance_id (spec section 3.1 / contract 8.4) - NEW STATE, nothing like it existed
    ' before. Minted ONCE per order-app process by this shared initialiser and never regenerated;
    ' that is what makes contract 8.4's guarantee structural rather than careful - a restart
    ' necessarily yields a new GUID with armed/started false, which is how section 6's
    ' restart-disarmed becomes visible engine-side.
    '
    ' NOT the same thing as the disposition log's column 2, which is the ENGINE's instance_id
    ' carried in the payload. The two are joined, never interchanged.
    Private Shared ReadOnly _instanceId As String = Guid.NewGuid().ToString()

    Friend Shared ReadOnly Property InstanceId As String
        Get
            Return _instanceId
        End Get
    End Property

    ''' <summary>
    ''' True when bridge.json carries a feedback_output_path key (the v2 master switch).
    ''' Mirrors RemoteNotifier.IsConfigured: ONE property, checked at every entry point, so the
    ''' disabled path is a single field read and nothing downstream has to remember to guard.
    ''' </summary>
    Friend Shared ReadOnly Property IsConfigured As Boolean
        Get
            Return _outputPath.Length > 0
        End Get
    End Property

    ''' <summary>The resolved output path, or "" when the emitter is inert. NOT a credential -
    ''' unlike RemoteNotifier's ntfy topic URL this may be logged.</summary>
    Friend Shared ReadOnly Property OutputPath As String
        Get
            Return _outputPath
        End Get
    End Property

    ' ================================ config ================================

    ''' <summary>
    ''' Read feedback_output_path out of the SAME bridge.json SignalBridge reads (the file that
    ''' "carries only what has no natural home on a form"). Call once at startup, before anything
    ''' else here. A missing/broken file leaves the emitter inert, exactly like an absent key -
    ''' this feature may never be the reason the app fails to start.
    ''' </summary>
    Friend Shared Sub LoadConfig()
        Try
            Dim cfg As String = SignalBridge.ConfigFilePath
            If Not File.Exists(cfg) Then Return                      ' no bridge.json at all = inert
            Dim json As JObject = JObject.Parse(File.ReadAllText(cfg))
            Dim tok As JToken = json.SelectToken("feedback_output_path")
            _outputPath = ResolveOutputPath(If(tok?.ToString(), ""), tok IsNot Nothing)
        Catch
            _outputPath = ""                                         ' fail-silent: inert, never fatal
        End Try
    End Sub

    ' The config decision as a pure seam (the house pattern - ShouldSend, EffectiveSizeUsd), pinned
    ' by OrderCheck fixtures. THREE cases, and the first two are NOT the same thing:
    '   * key ABSENT       => "" => fully inert: no timer, no worker, no file (spec section 2).
    '   * key PRESENT BLANK => the section-8.2 default path (the owner asked for the feature and
    '                          left the location to us).
    '   * key present       => that path, trimmed.
    Friend Shared Function ResolveOutputPath(rawValue As String, keyPresent As Boolean) As String
        If Not keyPresent Then Return ""
        Dim v As String = If(rawValue, "").Trim()
        If v.Length = 0 Then Return DefaultOutputPath
        Return v
    End Function

    ''' <summary>
    ''' The one startup line, matching the notifier's precedent (Q1: exactly one line, "configured"
    ''' or "disabled"). The path IS logged - it is not a credential.
    ''' </summary>
    Friend Shared Function StartupLine() As String
        Return If(IsConfigured,
                  $"Executor feedback: configured - {_outputPath}",
                  "Executor feedback: disabled (no feedback_output_path in bridge.json)")
    End Function

    ' ================================ the snapshot (spec section 3) ================================

    ''' <summary>
    ''' last_signal (spec section 3.3 / contract 8.4). IMMUTABLE and published BY REFERENCE: the
    ''' four members are read together on a thread that did not write them, and a reference
    ''' assignment is atomic, so a reader can never mix an old signal_id with a new disposition.
    ''' (This is exactly the mitigation E4 named and then did NOT need for the position block,
    ''' where the writing thread is also the publishing thread. It is cheap here because a
    ''' disposition happens once per consumed payload, not once per tick.)
    '''
    ''' Identity is the ENGINE's (instance_id, signal_id) - the soak-proven join key - NOT this
    ''' executor's instance_id, and not the disposition log's own column ordering.
    ''' </summary>
    Friend NotInheritable Class LastSignalRef
        Friend ReadOnly InstanceId As String
        Friend ReadOnly SignalId As Long
        Friend ReadOnly Disposition As String
        Friend ReadOnly AtUtc As DateTime

        Friend Sub New(instanceId As String, signalId As Long, disposition As String, atUtc As DateTime)
            Me.InstanceId = If(instanceId, "")
            Me.SignalId = signalId
            Me.Disposition = If(disposition, "")
            Me.AtUtc = atUtc
        End Sub
    End Class

    ''' <summary>
    ''' The flat immutable value handed from the caller's thread to the worker (spec section 1.3).
    ''' It is the WIRE CONTENT, already mapped: the flat-trap zeroing and the sign -> direction
    ''' decision happen in BuildSnapshot, so nothing downstream can re-derive them differently and
    ''' the worker never reaches back into live state.
    '''
    ''' feedback_id and generated_at_utc are deliberately NOT members: the heartbeat republishes
    ''' this exact value with fresh ones (spec section 4 (d)), so they belong to the publish, not
    ''' to the snapshot.
    ''' </summary>
    Friend Structure FeedbackSnapshot
        Public InstanceId As String              ' THIS executor process's GUID
        Public Mode As SignalBridge.BridgeMode
        Public Armed As Boolean                  ' the LOCAL toggle; the engine's ARM is never echoed back
        Public Started As Boolean
        Public BreakerTripped As Boolean
        Public WsConnected As Boolean
        Public Direction As String               ' LONG | SHORT | FLAT - derived from SizeUsd's sign
        Public SizeUsd As Decimal                ' signed; 0 when flat
        Public AvgEntry As Decimal               ' 0 when flat - see BuildSnapshot's flat trap
        Public WorkingStop As Decimal            ' 0 = unset, informational
        Public WorkingTarget As Decimal
        Public LastSignal As LastSignalRef       ' Nothing until this process consumes its first payload
    End Structure

    ' ---- E2: executor.mode. The pinned wire strings are NOT the enum's own names ----
    ' BridgeMode.LogOnly.ToString() is "LogOnly", which violates the section-8.3 pin "LOG_ONLY" -
    ' and violates it INVISIBLY, because the consumer's T8 enum tolerance renders an unrecognised
    ' value verbatim and takes the conservative arm without erroring. An explicit map is the whole
    ' fix; Case Else takes the conservative arm on our side too (an unmapped enum must never be
    ' able to read as LIVE).
    Friend Shared Function ModeWire(mode As SignalBridge.BridgeMode) As String
        Select Case mode
            Case SignalBridge.BridgeMode.Off : Return ModeOff
            Case SignalBridge.BridgeMode.LogOnly : Return ModeLogOnly
            Case SignalBridge.BridgeMode.Live : Return ModeLive
            Case Else : Return ModeOff
        End Select
    End Function

    ''' <summary>
    ''' Live state -> wire content. PURE, so the two traps below are fixture-pinnable rather than
    ''' buried in a form. Every argument is a BACKING FIELD read by the caller - never a control
    ''' (spec section 1: a single control read in the snapshot path is a review-blocking defect,
    ''' and reading one off the receive thread is what caused the edit-flood storm).
    '''
    ''' TRAP 1 - THE FLAT TRAP (spec section 3.2), the defect this whole spec exists to prevent.
    ''' rawAvgEntry is DELIBERATELY RETAINED through the flat echo: the position model keeps the
    ''' just-closed basis because the close-P/L computation reads it (frmMainPageV2.vb:2794 states
    ''' this outright, and :5224 is the consumer). That retention is CORRECT and must never be
    ''' "fixed" - so the emitter reads AROUND it, gating on IsFlat and emitting explicit zeros.
    ''' Publishing the retained value on a FLAT executor would hand the engine a stale fill to
    ''' attribute to the NEXT signal (contract 8.4 makes the avg_entry join the slippage record),
    ''' and it would do so silently: the number is plausible, well-formed and wrong, and it would
    ''' survive a soak. Same class as the placement log line that is not evidence of position size.
    '''
    ''' TRAP 2 - SIGN/DIRECTION CONSISTENCY (E3, contract 8.4). Both are derived from the ONE
    ''' signed value here, so they cannot disagree by construction rather than by care. The sign
    ''' itself is established, not assumed: positionSizeUSD is the exchange's positions[].size
    ''' verbatim and is NEGATIVE on a short - see docs/impl-report-c1-feedback-emitter.md section 3.
    ''' </summary>
    Friend Shared Function BuildSnapshot(instanceId As String,
                                         mode As SignalBridge.BridgeMode,
                                         armed As Boolean, started As Boolean,
                                         breakerTripped As Boolean, wsConnected As Boolean,
                                         rawSizeUsd As Decimal, rawAvgEntry As Decimal,
                                         rawWorkingStop As Decimal, rawWorkingTarget As Decimal,
                                         lastSignal As LastSignalRef) As FeedbackSnapshot
        Dim s As New FeedbackSnapshot With {
            .InstanceId = If(instanceId, ""),
            .Mode = mode,
            .Armed = armed,
            .Started = started,
            .BreakerTripped = breakerTripped,
            .WsConnected = wsConnected,
            .LastSignal = lastSignal
        }

        If rawSizeUsd = 0D Then
            ' IsFlat (frmMainPageV2.vb:712 is the same predicate). Contract 8.4: flat => FLAT + ZEROS.
            s.Direction = DirFlat
            s.SizeUsd = 0D
            s.AvgEntry = 0D
            s.WorkingStop = 0D
            s.WorkingTarget = 0D
        Else
            s.Direction = If(rawSizeUsd > 0D, DirLong, DirShort)
            s.SizeUsd = rawSizeUsd
            s.AvgEntry = rawAvgEntry
            s.WorkingStop = rawWorkingStop
            s.WorkingTarget = rawWorkingTarget
        End If
        Return s
    End Function

    ''' <summary>
    ''' Content equality over everything the file carries EXCEPT feedback_id and generated_at_utc.
    ''' This is the publish gate: a trigger whose snapshot is content-identical to the last
    ''' published one writes nothing, which is what lets the triggers sit on high-frequency echoes
    ''' (portfolio updates, quote-driven SL repositions) without rewriting the file per tick.
    '''
    ''' It is therefore also the thing that could SWALLOW a field - which is exactly what OrderCheck
    ''' fixture 8 asserts PER FIELD rather than in aggregate. Under spec section 4 (d) the heartbeat
    ''' republishes rather than re-reads, so a field this function forgot would be stale forever
    ''' rather than for 10 seconds.
    ''' </summary>
    Friend Shared Function SameContent(a As FeedbackSnapshot, b As FeedbackSnapshot) As Boolean
        If a.InstanceId <> b.InstanceId Then Return False
        If a.Mode <> b.Mode Then Return False
        If a.Armed <> b.Armed Then Return False
        If a.Started <> b.Started Then Return False
        If a.BreakerTripped <> b.BreakerTripped Then Return False
        If a.WsConnected <> b.WsConnected Then Return False
        If a.Direction <> b.Direction Then Return False
        If a.SizeUsd <> b.SizeUsd Then Return False
        If a.AvgEntry <> b.AvgEntry Then Return False
        If a.WorkingStop <> b.WorkingStop Then Return False
        If a.WorkingTarget <> b.WorkingTarget Then Return False
        Return SameLastSignal(a.LastSignal, b.LastSignal)
    End Function

    Friend Shared Function SameLastSignal(a As LastSignalRef, b As LastSignalRef) As Boolean
        If a Is Nothing OrElse b Is Nothing Then Return a Is b   ' null vs populated is a change
        Return a.InstanceId = b.InstanceId AndAlso a.SignalId = b.SignalId AndAlso
               a.Disposition = b.Disposition AndAlso a.AtUtc = b.AtUtc
    End Function

    ' ================================ serialization (contract section 8.3) ================================

    ' v1's serialization pins carry over (contract 8.1): JSON numbers, invariant culture,
    ' ISO-8601 UTC with a literal Z. Same format string the disposition log already emits.
    Friend Shared Function FormatUtc(value As DateTime) As String
        Return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
    End Function

    ''' <summary>
    ''' The snapshot as the section-8.3 document. Zeros-never-null for the suppressed numerics;
    ''' null (not an empty object) for an absent last_signal. feedback_id and generated_at_utc are
    ''' arguments, not snapshot members, because the heartbeat republishes one snapshot under many
    ''' of them.
    ''' </summary>
    Friend Shared Function Serialize(snap As FeedbackSnapshot, feedbackId As Long,
                                     generatedAtUtc As DateTime) As String
        Dim executor As New JObject From {
            {"instance_id", snap.InstanceId},
            {"app", AppName},
            {"mode", ModeWire(snap.Mode)},
            {"armed", snap.Armed},
            {"started", snap.Started},
            {"breaker_tripped", snap.BreakerTripped},
            {"ws", If(snap.WsConnected, WsOk, WsDown)}
        }

        Dim position As New JObject From {
            {"direction", snap.Direction},
            {"size_usd", snap.SizeUsd},
            {"avg_entry", snap.AvgEntry},
            {"working", New JObject From {
                {"stop", snap.WorkingStop},
                {"target", snap.WorkingTarget}
            }}
        }

        Dim lastSignal As JToken
        If snap.LastSignal Is Nothing Then
            lastSignal = JValue.CreateNull()   ' null until this process consumes its first payload
        Else
            lastSignal = New JObject From {
                {"instance_id", snap.LastSignal.InstanceId},
                {"signal_id", snap.LastSignal.SignalId},
                {"disposition", snap.LastSignal.Disposition},
                {"at_utc", FormatUtc(snap.LastSignal.AtUtc)}
            }
        End If

        Dim root As New JObject From {
            {"schema_version", SchemaVersion},
            {"feedback_id", feedbackId},
            {"generated_at_utc", FormatUtc(generatedAtUtc)},
            {"executor", executor},
            {"instrument", Instrument},
            {"position", position},
            {"last_signal", lastSignal}
        }
        Return root.ToString(Formatting.Indented)
    End Function

    ' ================================ the writer (spec section 1) ================================

    ' ~10 s (contract 8.5). The engine's staleness rule is now - generated_at_utc > 35 s, so three
    ' consecutive misses are tolerated before EXECUTOR STALE.
    Private Const HeartbeatMs As Integer = 10_000

    ''' <summary>
    ''' The coalescing slot (spec section 1.1 / contract 8.5). A burst of publishes collapses onto
    ''' the NEWEST snapshot and one take drains it - LAST WINS, deliberately not the notifier's
    ''' drop-the-newest rate limit.
    '''
    ''' Pure state: no I/O, no timers and no locking of its own (the caller owns the lock). That is
    ''' what lets OrderCheck pin last-wins as arithmetic rather than as an assertion about a file -
    ''' the same reason ShouldSend and EffectiveSizeUsd are shaped the way they are.
    ''' </summary>
    Friend NotInheritable Class CoalescingSlot
        Private _snap As FeedbackSnapshot
        Private _has As Boolean = False

        Friend ReadOnly Property HasPending As Boolean
            Get
                Return _has
            End Get
        End Property

        ''' <summary>Queue a snapshot, discarding any older one it supersedes.</summary>
        Friend Sub Offer(snap As FeedbackSnapshot)
            _snap = snap
            _has = True
        End Sub

        ''' <summary>Drop anything queued (the graceful-close write supersedes it).</summary>
        Friend Sub Clear()
            _has = False
        End Sub

        ''' <summary>Take the newest queued snapshot; False when the slot is empty.</summary>
        Friend Function TryTake(ByRef snap As FeedbackSnapshot) As Boolean
            If Not _has Then Return False
            snap = _snap
            _has = False
            Return True
        End Function
    End Class

    Private Shared ReadOnly _gate As New Object()      ' guards the queue + the ids
    Private Shared ReadOnly _writeGate As New Object() ' serializes the FILE write itself

    Private Shared ReadOnly _queue As New CoalescingSlot()
    Private Shared _lastPublished As FeedbackSnapshot  ' what the heartbeat republishes
    Private Shared _hasLastPublished As Boolean = False
    Private Shared _workerRunning As Boolean = False   ' SINGLE WRITER: at most one worker, ever
    Private Shared _feedbackId As Long = 0             ' monotonic per process, starts at 1
    Private Shared _heartbeat As Timer
    Private Shared _disposed As Boolean = False

    ''' <summary>
    ''' Arm the heartbeat. No-op when unconfigured - SHIPS OFF means no timer, no worker, no file.
    ''' </summary>
    Friend Shared Sub StartHeartbeat()
        If Not IsConfigured Then Return
        SyncLock _gate
            If _disposed OrElse _heartbeat IsNot Nothing Then Return
            _heartbeat = New Timer(AddressOf OnHeartbeat, Nothing, HeartbeatMs, HeartbeatMs)
        End SyncLock
    End Sub

    ''' <summary>
    ''' Publish a snapshot (spec section 4 triggers a/b/b2/c). Safe from ANY thread including the
    ''' receive path: never blocks on I/O, never throws, and returns after one Boolean field read
    ''' when the emitter is unconfigured.
    '''
    ''' SELF-COALESCING, LAST-WINS (contract 8.5) - deliberately NOT the notifier's
    ''' rate-limit-and-drop. A burst collapses onto the newest snapshot rather than dropping the
    ''' newest, because a dropped feedback write leaves the engine on a stale executor picture until
    ''' the next trigger, whereas a dropped notification is only a missed alert.
    '''
    ''' Content-identical snapshots write NOTHING. That is what lets the triggers sit on
    ''' high-frequency echoes (portfolio updates, quote-driven SL repositions) without rewriting the
    ''' file per tick, and it is why the heartbeat below must bypass this gate rather than route
    ''' through it.
    ''' </summary>
    Friend Shared Sub Publish(snap As FeedbackSnapshot)
        If Not IsConfigured Then Return
        Try
            SyncLock _gate
                If _disposed Then Return
                If _hasLastPublished AndAlso SameContent(_lastPublished, snap) Then Return
                _lastPublished = snap
                _hasLastPublished = True
                _queue.Offer(snap)
                If _workerRunning Then Return   ' the running worker will pick the newest up
                _workerRunning = True
            End SyncLock
            Task.Run(AddressOf DrainQueue)
        Catch
            ' Fail-silent at dispatch, exactly like RemoteNotifier.Post: telemetry must never be
            ' able to hurt the trading path.
        End Try
    End Sub

    ' Spec section 4 (d) - and it is COUNTER-INTUITIVE BY DESIGN (E4), so read this before "fixing" it.
    '
    ' The heartbeat REPUBLISHES THE LAST SNAPSHOT with a fresh feedback_id and generated_at_utc. It
    ' does NOT read live state, and it must not be changed to. Its job is the engine's staleness
    ' rule - a LIVENESS PROOF, not a data refresh - and every actual change to the four position
    ' fields already publishes from the thread that wrote it. Re-reading here would take the one
    ' coherence race E4 accepted (a timer thread reading four fields it did not write, mixing pre-
    ' and post-update values) and make it fire every ten seconds forever, to refresh data that is
    ' already fresh.
    '
    ' THE TRADE THIS MAKES: a MISSING trigger is no longer self-correcting - it republishes stale
    ' data indefinitely instead of healing within 10 s. OrderCheck fixture 8 is what makes that
    ' trade safe; do not keep this rule without it.
    Private Shared Sub OnHeartbeat(state As Object)
        Try
            If Not IsConfigured Then Return
            SyncLock _gate
                If _disposed Then Return                  ' never resurrect a disposed emitter
                If Not _hasLastPublished Then Return      ' nothing published yet: nothing to republish
                _queue.Offer(_lastPublished)
                If _workerRunning Then Return
                _workerRunning = True
            End SyncLock
            Task.Run(AddressOf DrainQueue)
        Catch
        End Try
    End Sub

    ' The SINGLE WRITER. At most one of these runs at a time (_workerRunning), so the ids it stamps
    ' are monotonic and the writes it makes are ordered - two concurrent writes to one path is the
    ' exact failure this design exists to prevent. It drains rather than writing once, so a burst
    ' that arrives mid-write still ends with the NEWEST snapshot on disk.
    Private Shared Sub DrainQueue()
        Try
            Do
                Dim snap As FeedbackSnapshot = Nothing
                Dim id As Long
                SyncLock _gate
                    If _disposed OrElse Not _queue.TryTake(snap) Then
                        _workerRunning = False
                        Return
                    End If
                    _feedbackId += 1
                    id = _feedbackId
                End SyncLock
                WriteAtomic(Serialize(snap, id, DateTime.UtcNow))
            Loop
        Catch
            ' Fail-silent, but never leave the latch set or nothing would ever write again.
            SyncLock _gate
                _workerRunning = False
            End SyncLock
        End Try
    End Sub

    ' E1 - the atomic write, and the reason contract 8.1 was amended before any of this was written.
    ' The house pattern is WriteAllText(tmp) + Move(overwrite:=True) (AppUserSettings.vb:207,
    ' SignalBridge.vb PersistState). The contract used to name File.Replace, which THROWS when the
    ' destination does not exist - precisely the first write and the ships-OFF -> ON transition.
    ' Atomicity was always the requirement; the API never was.
    Private Shared Sub WriteAtomic(text As String)
        SyncLock _writeGate   ' the graceful-close write and the worker must never overlap on one path
            Try
                Dim path As String = _outputPath
                If path.Length = 0 Then Return
                Dim dir As String = IO.Path.GetDirectoryName(path)
                If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                    Directory.CreateDirectory(dir)   ' create-if-missing, as the bridge watcher does
                End If
                Dim tmp As String = path & ".tmp"
                File.WriteAllText(tmp, text)
                File.Move(tmp, path, overwrite:=True)
            Catch
                ' Fail-silent: a locked/unwritable path costs the app nothing. The engine sees the
                ' file go stale, which is the honest signal.
            End Try
        End SyncLock
    End Sub

    ''' <summary>
    ''' Spec section 4 (e): the final write on graceful close, which is what makes "silence = dead
    ''' executor" honest. SYNCHRONOUS on the caller's thread by necessity - a queued write would
    ''' race the process exit and simply be lost - and bounded: one local file write under the same
    ''' lock the worker uses. Disposes the heartbeat and latches _disposed first, so nothing can
    ''' resurrect the emitter afterwards.
    ''' </summary>
    Friend Shared Sub ShutdownWithFinalWrite(final As FeedbackSnapshot)
        If Not IsConfigured Then Return
        Try
            Dim t As Timer
            Dim id As Long
            SyncLock _gate
                If _disposed Then Return
                _disposed = True
                t = _heartbeat
                _heartbeat = Nothing
                _queue.Clear()             ' anything queued is superseded by this final snapshot
                _lastPublished = final
                _hasLastPublished = True
                _feedbackId += 1
                id = _feedbackId
            End SyncLock
            t?.Dispose()
            WriteAtomic(Serialize(final, id, DateTime.UtcNow))
        Catch
            ' Never block shutdown on telemetry.
        End Try
    End Sub

End Class
