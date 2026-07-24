Option Strict On
Option Explicit On

Imports System.Net.Http
Imports System.Threading.Tasks

' =====================================================================================================
' RemoteNotifier - ntfy.sh push alerts (docs/spec-quickwins-notifier-signalcols.md Q1).
'
' Sound/taskbar alerts (ergonomics item D) are worthless off-RDP; production is a remote AWS box
' (cutover checklist section 9). This posts the same handful of events to a secret ntfy topic so
' they reach the owner's phone.
'
' Design rules (all load-bearing - the call sites sit beside hot order/receive paths):
'   * Post() is fully fire-and-forget INTERNALLY: the HTTP send runs on the thread pool via
'     Task.Run and every exception - both at dispatch and inside the send - is swallowed. An
'     alert channel must never be able to hurt the trading path, so every call site is a plain
'     one-liner with no wrapper, no Await, no Try.
'   * Inert without configuration: no ntfy_url in secrets.json = every Post returns immediately
'     (ships safe). The topic URL is a CREDENTIAL - it is never logged; the startup line says
'     only "configured" or "disabled".
'   * Self-rate-limited: non-urgent posts respect a 5 s min interval and a 12/min window (drop
'     silently beyond); priority "urgent" bypasses both. The decision is the pure ShouldSend
'     seam below, pinned by OrderCheck fixtures.
' =====================================================================================================
Friend NotInheritable Class RemoteNotifier

    Private Sub New()
    End Sub

    Private Const MinIntervalSeconds As Integer = 5
    Private Const MaxPerWindow As Integer = 12
    Private Const WindowSeconds As Integer = 60

    ' One shared client for the app's lifetime (socket-exhaustion rule); 10 s timeout so a dead
    ' network can never pin thread-pool tasks for long.
    Private Shared ReadOnly _http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(10)}

    Private Shared ReadOnly _gate As New Object()
    Private Shared _lastSentUtc As DateTime = DateTime.MinValue
    Private Shared _windowStartUtc As DateTime = DateTime.MinValue
    Private Shared _sentInWindow As Integer = 0

    ''' <summary>True when secrets.json carries a non-blank ntfy_url (the v1 master switch).</summary>
    Friend Shared ReadOnly Property IsConfigured As Boolean
        Get
            Return Not String.IsNullOrWhiteSpace(AppSecrets.NtfyUrl)
        End Get
    End Property

    ''' <summary>
    ''' Post one notification. Safe from ANY thread including the receive/FSW/quote paths: never
    ''' blocks on the network, never throws. priority is the ntfy header value ("default", "high",
    ''' "urgent"); "urgent" bypasses the self-rate-limit. tags is the optional ntfy Tags header
    ''' (comma-separated emoji short codes), empty = omitted.
    ''' </summary>
    Friend Shared Sub Post(title As String, message As String,
                           Optional priority As String = "default",
                           Optional tags As String = "")
        Try
            Dim url As String = AppSecrets.NtfyUrl
            If String.IsNullOrWhiteSpace(url) Then Return ' not configured: inert by design

            Dim isUrgent As Boolean = String.Equals(priority, "urgent", StringComparison.OrdinalIgnoreCase)
            SyncLock _gate
                Dim nowUtc As DateTime = DateTime.UtcNow
                ' Fixed 60 s window: reset the counter when the window has elapsed.
                If _windowStartUtc = DateTime.MinValue OrElse (nowUtc - _windowStartUtc).TotalSeconds >= WindowSeconds Then
                    _windowStartUtc = nowUtc
                    _sentInWindow = 0
                End If
                If Not ShouldSend(nowUtc, _lastSentUtc, _sentInWindow, isUrgent) Then Return ' drop silently
                _lastSentUtc = nowUtc
                _sentInWindow += 1
            End SyncLock

            ' Network I/O strictly OFF the caller's thread and OUTSIDE the lock.
            Task.Run(Async Function()
                         Try
                             Using req As New HttpRequestMessage(HttpMethod.Post, url)
                                 req.Content = New StringContent(If(message, ""))
                                 ' TryAddWithoutValidation: a header the framework dislikes must
                                 ' drop the header, not the notification (and never throw).
                                 req.Headers.TryAddWithoutValidation("Title", If(title, ""))
                                 req.Headers.TryAddWithoutValidation("Priority", If(String.IsNullOrWhiteSpace(priority), "default", priority))
                                 If Not String.IsNullOrWhiteSpace(tags) Then
                                     req.Headers.TryAddWithoutValidation("Tags", tags)
                                 End If
                                 Await _http.SendAsync(req).ConfigureAwait(False)
                             End Using
                         Catch
                             ' Fail-silent: an unreachable ntfy/network must cost the app nothing.
                         End Try
                     End Function)
        Catch
            ' Fail-silent at dispatch too (e.g. a malformed URL throwing in HttpRequestMessage).
        End Try
    End Sub

    ' The rate-limit decision as a pure seam (spec Q1), pinned by OrderCheck fixtures.
    ' Urgent bypasses both limits; non-urgent needs >= 5 s since the last send AND fewer than
    ' 12 sends in the current window. lastSentUtc = MinValue means "never sent".
    Friend Shared Function ShouldSend(nowUtc As DateTime, lastSentUtc As DateTime,
                                      sentInWindow As Integer, isUrgent As Boolean) As Boolean
        If isUrgent Then Return True
        If lastSentUtc <> DateTime.MinValue AndAlso (nowUtc - lastSentUtc).TotalSeconds < MinIntervalSeconds Then Return False
        If sentInWindow >= MaxPerWindow Then Return False
        Return True
    End Function

End Class
