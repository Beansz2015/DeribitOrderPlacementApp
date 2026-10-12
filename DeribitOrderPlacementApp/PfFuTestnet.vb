Imports System.IO
Imports Newtonsoft.Json.Linq

' TEMPORARY - partial-fill follow-up testnet run only (docs/review-pf-phase2.md section 3, coordinator ruling
' on PF-SB2). Remove this file and its call sites (grep "PfFuTestnet.") straight after the run.
'
' Two switches:
'   PFFU_SKIP_CLOSE_CANCEL - CompletePositionClose does not SEND its cancel-all (id 30); every engine reset
'                            in CancelOrderAsync still runs. A TP left resting after a flatten is then F3's
'                            to find (PF-R4).
'   PFFU_RAW_ECHO          - append verbatim order-path JSON (out and in) to pffu-raw-echo.log (X-2's echo)
' Each is ON only when AppSecrets.IsTestnet AND a flag file of that name exists in the exe folder.
' Default (no file) is OFF. A live session can never honour either.
'
' Thread class: called from the receive loop and from the send path. File I/O only, under a lock;
' no WinForms control is touched. Every failure is swallowed - a diagnostic never breaks trading.
Friend Module PfFuTestnet

    Private ReadOnly _lock As New Object()
    Private ReadOnly _dir As String = AppContext.BaseDirectory

    ' Read at each close, so the flag can be removed without a restart.
    Friend ReadOnly Property SkipCloseCancelAllOn As Boolean
        Get
            Return AppSecrets.IsTestnet AndAlso File.Exists(Path.Combine(_dir, "PFFU_SKIP_CLOSE_CANCEL"))
        End Get
    End Property

    ' The echo flag is checked once per process (it is set before launch).
    Private ReadOnly _rawEchoFlag As New Lazy(Of Boolean)(Function() File.Exists(Path.Combine(_dir, "PFFU_RAW_ECHO")))

    Friend ReadOnly Property RawEchoOn As Boolean
        Get
            Return AppSecrets.IsTestnet AndAlso _rawEchoFlag.Value
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
                File.AppendAllText(Path.Combine(_dir, "pffu-raw-echo.log"),
                                   DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") & " " & dirTag & " " & msg & Environment.NewLine)
            End SyncLock
        Catch
        End Try
    End Sub

End Module
