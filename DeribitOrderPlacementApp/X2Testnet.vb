Imports System.IO
Imports Newtonsoft.Json.Linq

' TEMPORARY - X-2 partial-fill testnet experiment only (docs/spec-partial-fill-fix.md section 3, owner
' ruling PF-R1 2026-10-12). Keep until the Phase 2 acceptance run (docs/spec-partial-fill-fix.md
' section 5 item 3) has used X2_TAKER_ENTRY. Then remove this file and its call sites (grep "X2Testnet.").
'
' Three switches:
'   X2_RAW_ECHO     - append verbatim order-path JSON (out and in) to x2-raw-echo.log (X-1's echo, restored)
'   X2_TAKER_ENTRY  - an EntryLimitOrder placement sends post_only false, priced at the best ask (buy) or
'                     the best bid (sell). Only the placement changes; chase edits stay as today.
'   X2_INCREMENTAL  - the entry OTOCO sends trigger_fill_condition "incremental"
' Each is ON only when AppSecrets.IsTestnet AND a flag file of that name exists in the exe folder.
' Default (no file) is OFF. A live session can never honour any of them.
'
' Thread class: called from the receive loop and from the send path. File I/O only, under a lock;
' no WinForms control is touched. Every failure is swallowed - a diagnostic never breaks trading.
Friend Module X2Testnet

    Private ReadOnly _lock As New Object()
    Private ReadOnly _dir As String = AppContext.BaseDirectory

    Private Function FlagOn(name As String) As Boolean
        Return AppSecrets.IsTestnet AndAlso File.Exists(Path.Combine(_dir, name))
    End Function

    ' The echo flag is checked once per process (it is set before launch); the two order-shape flags are
    ' read at each placement, so a seat can switch them between experiments without a restart.
    Private ReadOnly _rawEchoFlag As New Lazy(Of Boolean)(Function() File.Exists(Path.Combine(_dir, "X2_RAW_ECHO")))

    Friend ReadOnly Property RawEchoOn As Boolean
        Get
            Return AppSecrets.IsTestnet AndAlso _rawEchoFlag.Value
        End Get
    End Property

    Friend ReadOnly Property TakerEntryOn As Boolean
        Get
            Return FlagOn("X2_TAKER_ENTRY")
        End Get
    End Property

    Friend ReadOnly Property IncrementalOn As Boolean
        Get
            Return FlagOn("X2_INCREMENTAL")
        End Get
    End Property

    Private Function CarriesSecret(msg As String) As Boolean
        Return msg.Contains("access_token") OrElse msg.Contains("refresh_token") OrElse msg.Contains("client_secret")
    End Function

    Friend Sub Outbound(msg As String)
        If Not RawEchoOn OrElse String.IsNullOrEmpty(msg) OrElse CarriesSecret(msg) Then Return
        Try
            Dim method = JObject.Parse(msg).Value(Of String)("method")
            If method Is Nothing Then Return
            If method.StartsWith("private/buy") OrElse method.StartsWith("private/sell") OrElse
               method.StartsWith("private/edit") OrElse method.StartsWith("private/cancel") OrElse
               method.StartsWith("private/get_open_orders") OrElse method.StartsWith("private/close_position") OrElse
               method.StartsWith("private/get_cancel_on_disconnect") Then
                Write("OUT", msg)
            End If
        Catch
        End Try
    End Sub

    Friend Sub Inbound(msg As String)
        If Not RawEchoOn OrElse String.IsNullOrEmpty(msg) OrElse CarriesSecret(msg) Then Return
        Try
            Dim j = JObject.Parse(msg)
            Dim channel = j.SelectToken("params.channel")?.ToString()
            If channel IsNot Nothing Then
                If channel.StartsWith("user.changes") Then Write("IN ", msg)
                Return
            End If
            ' RPC responses (placements, edits, cancels, errors, the id-778 snapshot). Skip public/test.
            If j("id") IsNot Nothing AndAlso Not msg.Contains("""version""") Then Write("IN ", msg)
        Catch
        End Try
    End Sub

    Private Sub Write(dirTag As String, msg As String)
        Try
            SyncLock _lock
                File.AppendAllText(Path.Combine(_dir, "x2-raw-echo.log"),
                                   DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") & " " & dirTag & " " & msg & Environment.NewLine)
            End SyncLock
        Catch
        End Try
    End Sub

End Module
