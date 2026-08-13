Option Strict On
Option Explicit On

Imports System.Globalization

' =====================================================================================================
' WsEdgeLog - the executor.ws DOWN/OK audit trail.
'
' Spec: docs/spec-ws-edge-audit.md. This is OBSERVABILITY for an already-shipped field (E6a), not a
' new feature: the ws value itself is correct and is not touched here.
'
' WHY THIS EXISTS. executor.ws is written ONLY into executor_feedback.json (ExecutorFeedback.vb:322),
' that file is OVERWRITTEN on every publish, and the emitter's 10 s heartbeat republishes it. A
' DOWN -> OK transition therefore leaves no trace once it has passed. On 2026-08-13 a real disconnect
' happened during a monitored LONDON log-only session - "Server closed connection - scheduling
' reconnect" then "Successfully reconnected" - and it could not be evidenced afterwards. The single
' hardest-to-provoke behaviour on C1's still-unobserved list occurred and left nothing behind.
'
' THE TRAP THAT DECIDES THE DESIGN (spec section 1.3): THE HOST LOG DOES NOT PERSIST. txtLogs is a
' RichTextBox - screen only, gone when the app closes. The only file writes on the log path are
' AutoTradeLog.txt (a narrow trade-event log) and crash.log (the ApplicationEvents backstop). So a
' gray log line ALONE does not satisfy this: it buys live visibility and no audit. The fix must write
' a file, and this class is that file. The gray line is emitted beside it by the caller, from the
' SAME row, so the screen and the file can never disagree about what happened.
'
' NOT INSIDE ExecutorFeedback, deliberately. ExecutorFeedback.Publish returns on a single Boolean
' read when bridge.json carries no feedback_output_path, so an audit trail hung off it would silently
' not exist on an unconfigured bin - the instrument would be absent exactly when nobody checked. The
' socket goes down whether or not the engine is listening, so this log is unconditional.
'
' NOT bridge-dispositions.log either, which is the obvious-looking home and is forbidden. That file's
' cardinality is FROZEN at one row per payload (HANDOVER-6.md section 6.3), the engine's soak join
' reads it, and a ws edge has no payload. Adding rows there would break a frozen contract to save
' creating one file.
'
' APPEND-ONLY, and deliberately NOT atomic - the same class as crash.log, AutoTradeLog.txt and the
' disposition log (HANDOVER-6.md section 6.10). The WriteAllText(tmp) + Move(overwrite:=True)
' invariant covers state files that are read back; this is a growing audit trail that is not.
'
' Everything above the writer is PURE (spec section 2.5), so the transition rule is provable with no
' app, no socket and no disconnect. That is the house pattern - ShouldSend, EffectiveSizeUsd,
' ShouldWrite, Select-BestMatchIndex - and it is what makes acceptance 1 possible at all.
' =====================================================================================================
Friend NotInheritable Class WsEdgeLog

    Private Sub New()
    End Sub

    ' The log's file name. Beside the exe, like bridge-dispositions.log - see LogPath.
    Friend Const FileName As String = "ws-edges.log"

    ' The row separator, named so a fixture pins the LITERAL rather than re-typing it.
    Friend Const FieldSeparator As String = " | "

    ''' <summary>
    ''' OK / DOWN. These are ExecutorFeedback's OWN pinned tokens, reused rather than re-typed: this
    ''' log exists to evidence THAT field, so a row reading "UP" where the file reads "OK" would be
    ''' evidence of a different thing. E2's failure mode was a wire string that was wrong in one
    ''' place only; sharing the constant makes agreement structural instead of careful.
    ''' </summary>
    Friend Shared Function StateWire(wsConnected As Boolean) As String
        Return If(wsConnected, ExecutorFeedback.WsOk, ExecutorFeedback.WsDown)
    End Function

    ''' <summary>
    ''' THE PURE SEAM (spec section 2.5). Log the TRANSITION, not the state: write only when the new
    ''' state differs from the last one LOGGED.
    '''
    ''' WHY A TRANSITION. The emitter's heartbeat republishes every 10 s. Logging state rather than
    ''' transition would produce roughly 8,600 identical rows a day and bury the one row that
    ''' matters - which is the same as having no audit trail, but slower to read.
    '''
    ''' THE FIELD IS WHAT MAKES "TRANSITION" TRUE, NOT THE CALL SITE (spec section 2.1). The two call
    ''' sites are edges by construction, and this function is what stops that being an assumption: a
    ''' failed reconnect can re-enter the receive-loop-exit path and offer DOWN a second time. Do not
    ''' replace this with "the caller only calls it on an edge".
    '''
    ''' lastLogged "" or Nothing is the FIRST-EVER call: nothing has been logged this process, so any
    ''' known state is a transition and the session's opening state gets recorded. An unknown CURRENT
    ''' state never is - there is nothing to record, and a blank row would be worse than no row.
    ''' </summary>
    Friend Shared Function ShouldLogWsEdge(lastLogged As String, current As String) As Boolean
        If String.IsNullOrEmpty(current) Then Return False
        Return Not String.Equals(If(lastLogged, ""), current, StringComparison.Ordinal)
    End Function

    ''' <summary>
    ''' One edge, one line, sortable and greppable:
    '''   2026-08-13T13:22:41Z | DOWN | server closed connection
    '''
    ''' The timestamp format is the one the disposition log and the feedback file already emit -
    ''' ISO-8601 UTC with a literal Z, invariant culture. Invariant is not decoration: the 8956baa
    ''' culture bug turned a contract timestamp into a culture-rendered date on d/M/yyyy machines,
    ''' and this app runs on one.
    ''' </summary>
    Friend Shared Function FormatRow(atUtc As DateTime, state As String, reason As String) As String
        Return String.Join(FieldSeparator,
            atUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            If(state, ""),
            FlattenReason(reason))
    End Function

    ''' <summary>
    ''' ONE ROW PER EDGE is the whole contract of this file, and the reason is the one field that can
    ''' break it: it carries exception messages, and an exception message can contain a line break.
    ''' Flatten CR and LF to spaces so a two-line reason cannot read as two edges.
    ''' </summary>
    Friend Shared Function FlattenReason(reason As String) As String
        Return If(reason, "").Replace(vbCr, " ").Replace(vbLf, " ").Trim()
    End Function

End Class
