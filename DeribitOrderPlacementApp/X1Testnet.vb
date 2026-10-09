Imports System.IO
Imports Newtonsoft.Json.Linq

' TEMPORARY - X-1 testnet experiments only (docs/spec-x1-testnet-experiments.md). Remove this file
' and its three call sites (grep "X1Testnet.") when the run is recorded.
'
' Two switches, both owner-approved 2026-10-10 (raw echo) and 2026-10-08 (TP reduce-only):
'   X1_RAW_ECHO        - append verbatim order-path JSON (out and in) to x1-raw-echo.log
'   X1_TP_REDUCE_ONLY  - send the OTOCO take-profit leg with reduce_only = true (experiment (f))
' Each is ON only when AppSecrets.IsTestnet AND a flag file of that name exists in the exe folder.
' Default (no file) is OFF. A live session can never honour either.
'
' Thread class: called from the receive loop and from the send path. File I/O only, under a lock;
' no WinForms control is touched. Every failure is swallowed - a diagnostic never breaks trading.
Friend Module X1Testnet

    Private ReadOnly _lock As New Object()
    Private ReadOnly _dir As String = AppContext.BaseDirectory

    Private Function FlagOn(name As String) As Boolean
        Return AppSecrets.IsTestnet AndAlso File.Exists(Path.Combine(_dir, name))
    End Function

    ' File existence is checked once per process; the flags are set before launch, never mid-run.
    Private ReadOnly _rawEchoFlag As New Lazy(Of Boolean)(Function() File.Exists(Path.Combine(_dir, "X1_RAW_ECHO")))

    Friend ReadOnly Property RawEchoOn As Boolean
        Get
            Return AppSecrets.IsTestnet AndAlso _rawEchoFlag.Value
        End Get
    End Property

    Friend ReadOnly Property TpReduceOnlyOn As Boolean
        Get
            Return FlagOn("X1_TP_REDUCE_ONLY")
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
               method.StartsWith("private/get_open_orders") OrElse method.StartsWith("private/close_position") Then
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
                File.AppendAllText(Path.Combine(_dir, "x1-raw-echo.log"),
                                   DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") & " " & dirTag & " " & msg & Environment.NewLine)
            End SyncLock
        Catch
        End Try
    End Sub

End Module
