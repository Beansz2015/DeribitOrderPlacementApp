Public Class AutoTradeSettings

    ' Retirement: the host is frmMainPageV2 now (FrmIndicators used to own and position this form,
    ' but it is no longer shown). Typed, because the Tooling section pushes values into the host.
    Private ReadOnly _host As frmMainPageV2

    ' ============ SIGNAL BRIDGE panel (docs/spec-autotrade-tiein.md section 4) ============
    ' frmMainPageV2 owns this form and the bridge, and assigns Bridge directly after constructing it
    ' (this form only displays/drives the bridge). Mode, ARM and Started deliberately NEVER persist -
    ' they reset to Off/unchecked/stopped at every app start. Do NOT add them to the item-A
    ' ergonomics persistence pass later (contract section 6: restart = disarmed).
    Private _bridge As SignalBridge
    Private _suppressBridgeUi As Boolean = False ' guards programmatic combo/checkbox writes in RefreshBridgePanel

    ' Live ATR readout (Tooling). FrmIndicators is retired, so its ATR display is gone and this is the
    ' only place the effective ATR is visible - it is also what proves the headless indicator engine
    ' is still running. UI-thread timer, and only while this window is actually open.
    Private WithEvents _atrTimer As New Timer With {.Interval = 1000}

    ' ============ Gate-config mirrors (retirement) ============
    ' The bridge runs on watcher/timer threads and MUST NOT read .Text, so the gate config lives in
    ' these plain backing fields and the bridge reads them through the properties below.
    '
    ' They commit on focus-LOSS / Enter, never per keystroke. That is deliberate and safety-critical:
    ' a signal landing while you are half-way through typing "15" must not see cooloff = 1. While a
    ' box is being edited the bridge keeps using the last committed value; the new one takes effect
    ' when you leave the field. Clicking a box also selects its whole contents, so a fresh value
    ' replaces the old one rather than concatenating with it.
    '
    ' Nothing here persists across restarts - the boxes come back at their designer defaults. That
    ' matches the old autotrader; the ergonomics Phase A config-save is what will change it.
    Private _cooloffMin As Decimal = 5D
    Private _circuitBreakerUsd As Decimal = -1D      ' <= 0 disables (designer default is -1)
    Private _windowStart As String = ""              ' blank = unrestricted
    Private _windowEnd As String = ""
    Private _tiersCsv As String = "HIGH,MEDIUM"

    Friend ReadOnly Property CooloffMin As Decimal
        Get
            Return _cooloffMin
        End Get
    End Property
    Friend ReadOnly Property CircuitBreakerUsd As Decimal
        Get
            Return _circuitBreakerUsd
        End Get
    End Property
    Friend ReadOnly Property WindowStart As String
        Get
            Return _windowStart
        End Get
    End Property
    Friend ReadOnly Property WindowEnd As String
        Get
            Return _windowEnd
        End Get
    End Property
    Friend ReadOnly Property TiersCsv As String
        Get
            Return _tiersCsv
        End Get
    End Property

    <ComponentModel.Browsable(False)>
    <ComponentModel.DesignerSerializationVisibility(ComponentModel.DesignerSerializationVisibility.Hidden)>
    Friend Property Bridge As SignalBridge
        Get
            Return _bridge
        End Get
        Set(value As SignalBridge)
            If _bridge IsNot Nothing Then RemoveHandler _bridge.StatusChanged, AddressOf OnBridgeStatusChanged
            _bridge = value
            If _bridge IsNot Nothing Then AddHandler _bridge.StatusChanged, AddressOf OnBridgeStatusChanged
            RefreshBridgePanel()
        End Set
    End Property

    Public Sub New(host As frmMainPageV2)
        InitializeComponent()
        _host = host
    End Sub

    Private Sub AutoTradeSettings_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' Initial positioning
        StickToHost()

        ' Attach event handlers to follow host movement
        If _host IsNot Nothing Then
            AddHandler _host.LocationChanged, AddressOf HostMovedOrResized
            AddHandler _host.SizeChanged, AddressOf HostMovedOrResized
        End If

        ' Bridge panel defaults: mode Off, un-armed, stopped (never persisted).
        cboBridgeMode.SelectedIndex = 0
        RefreshBridgePanel()

        ' NOTE: no Me.Hide() here any more. It used to make the FIRST click of the opener button a
        ' no-op (Show -> Load fires -> Hide), so you had to click twice. The form is only ever shown
        ' by that button now, so hiding itself on Load is exactly wrong.
    End Sub

    ' Seed the mirrors from the designer defaults and wire the shared edit behaviour. Called by the
    ' host right after construction, so the bridge has real values before the first payload lands
    ' (Load does not run until the form is first shown, which may be never).
    Friend Sub InitialiseSettings()
        CommitGateConfig()
        CommitToolingConfig()
        For Each tb As TextBox In {txtCooloff, txtCircuitBreaker, txtStartTime, txtEndTime,
                                   txtBridgeTiers, txtAtrLength, txtAtrFallback}
            AddHandler tb.Enter, AddressOf SelectAllOnEnter
            AddHandler tb.Click, AddressOf SelectAllOnEnter
            AddHandler tb.Leave, AddressOf CommitOnLeave
            AddHandler tb.KeyDown, AddressOf CommitOnEnterKey
        Next
    End Sub

    ' Only burn a timer tick while the window is on screen.
    Private Sub AutoTradeSettings_VisibleChanged(sender As Object, e As EventArgs) Handles Me.VisibleChanged
        _atrTimer.Enabled = Me.Visible AndAlso Not Me.IsDisposed
        If Me.Visible Then RefreshAtrReadout()
    End Sub

    Private Sub _atrTimer_Tick(sender As Object, e As EventArgs) Handles _atrTimer.Tick
        RefreshAtrReadout()
    End Sub

    Private Sub RefreshAtrReadout()
        If _host Is Nothing OrElse _host.IsDisposed Then
            lblAtrNow.Text = "Current ATR: (no host)"
            Return
        End If
        Dim eff = _host.GetEffectiveAtr()
        Dim limit As Decimal = _host.CurrentSlippageLimit
        If eff.Atr > 0D Then
            lblAtrNow.Text = $"ATR now: {eff.Atr:F2} ({eff.Source})  ->  slip limit ${limit:F2}"
            ' Green when the payload drives it, cyan when the headless indicator does.
            lblAtrNow.ForeColor = If(eff.Source = "signal payload", Color.LimeGreen, Color.Cyan)
        Else
            lblAtrNow.Text = $"ATR now: NONE  ->  slip limit ${limit:F2} (fallback)"
            lblAtrNow.ForeColor = Color.Orange
        End If
    End Sub

    Private Sub HostMovedOrResized(sender As Object, e As EventArgs)
        StickToHost()
    End Sub

    Private Sub StickToHost()
        If _host Is Nothing OrElse _host.IsDisposed Then Return

        ' Position AutoTradeSettings just outside the main form's right border, aligned to top
        Me.StartPosition = FormStartPosition.Manual
        Me.Location = New Point(_host.Right + 6, _host.Top)
    End Sub

    ' Clean up event handlers when form closes
    Private Sub AutoTradeSettings_FormClosed(sender As Object, e As FormClosedEventArgs) Handles Me.FormClosed
        _atrTimer.Stop()
        If _host IsNot Nothing Then
            RemoveHandler _host.LocationChanged, AddressOf HostMovedOrResized
            RemoveHandler _host.SizeChanged, AddressOf HostMovedOrResized
        End If
        If _bridge IsNot Nothing Then RemoveHandler _bridge.StatusChanged, AddressOf OnBridgeStatusChanged
    End Sub

    ' ============ Edit behaviour: select-all on entry, commit on leave/Enter ============

    Private Sub SelectAllOnEnter(sender As Object, e As EventArgs)
        Dim tb = TryCast(sender, TextBox)
        If tb IsNot Nothing Then tb.SelectAll()
    End Sub

    Private Sub CommitOnLeave(sender As Object, e As EventArgs)
        CommitGateConfig()
        CommitToolingConfig()
    End Sub

    Private Sub CommitOnEnterKey(sender As Object, e As KeyEventArgs)
        If e.KeyCode <> Keys.Enter Then Return
        e.SuppressKeyPress = True   ' no ding
        CommitGateConfig()
        CommitToolingConfig()
        RefreshBridgePanel()
    End Sub

    ' Reads the gate boxes into the mirrors. Invalid/blank input leaves the previous value in place
    ' rather than falling back to something permissive - a half-typed window must never read as
    ' "no time restriction". Blank BOTH window boxes for genuinely unrestricted (handled below).
    Private Sub CommitGateConfig()
        Dim d As Decimal
        If Decimal.TryParse(txtCooloff.Text, d) AndAlso d >= 0D Then _cooloffMin = d
        If Decimal.TryParse(txtCircuitBreaker.Text, d) Then _circuitBreakerUsd = d

        ' Window: blank is a legitimate value (unrestricted), so it commits; garbage does not.
        Dim ws As String = If(txtStartTime.Text, "").Trim()
        Dim we As String = If(txtEndTime.Text, "").Trim()
        Dim ts As TimeSpan
        If ws.Length = 0 OrElse TimeSpan.TryParse(ws, ts) Then _windowStart = ws
        If we.Length = 0 OrElse TimeSpan.TryParse(we, ts) Then _windowEnd = we

        Dim tiers As String = If(txtBridgeTiers.Text, "").Trim()
        If tiers.Length > 0 Then _tiersCsv = tiers.ToUpperInvariant()

        ShowGateConfigWarnings()
    End Sub

    Private Sub CommitToolingConfig()
        If _host Is Nothing Then Return
        Dim len As Integer
        Dim fallback As Decimal
        If Not Integer.TryParse(txtAtrLength.Text, len) Then len = 0
        If Not Decimal.TryParse(txtAtrFallback.Text, fallback) Then fallback = 0D
        _host.SetToolingValues(len, fallback)   ' host ignores non-positive values
    End Sub

    ' Surfaces the cases where what is typed is not what is in force.
    Private Sub ShowGateConfigWarnings()
        Dim problems As New List(Of String)
        Dim ts As TimeSpan
        Dim ws As String = If(txtStartTime.Text, "").Trim()
        Dim we As String = If(txtEndTime.Text, "").Trim()
        If ws.Length > 0 AndAlso Not TimeSpan.TryParse(ws, ts) Then problems.Add($"start time '{ws}' (use HH:mm)")
        If we.Length > 0 AndAlso Not TimeSpan.TryParse(we, ts) Then problems.Add($"end time '{we}' (use HH:mm)")
        If (ws.Length = 0) <> (we.Length = 0) Then problems.Add("both time boxes must be set, or both blank")
        Dim d As Decimal
        If Not Decimal.TryParse(txtCooloff.Text, d) OrElse d < 0D Then problems.Add($"cooloff '{txtCooloff.Text}'")
        If Not Decimal.TryParse(txtCircuitBreaker.Text, d) Then problems.Add($"max loss '{txtCircuitBreaker.Text}'")
        Dim len As Integer
        If Not Integer.TryParse(txtAtrLength.Text, len) OrElse len <= 0 Then problems.Add($"ATR length '{txtAtrLength.Text}'")
        If Not Decimal.TryParse(txtAtrFallback.Text, d) OrElse d <= 0D Then problems.Add($"ATR fallback '{txtAtrFallback.Text}'")

        If problems.Count = 0 Then Return
        lblBridgeStatus.Text = "Ignored (keeping last good): " & String.Join("; ", problems)
        lblBridgeStatus.ForeColor = Color.Orange
    End Sub

    ' ============ SIGNAL BRIDGE panel handlers ============

    ' StatusChanged is raised on arbitrary bridge threads (FSW/timer/processing) - marshal, never
    ' touch controls directly here.
    Private Sub OnBridgeStatusChanged()
        If Me.IsHandleCreated AndAlso Not Me.IsDisposed Then
            Try
                Me.BeginInvoke(Sub() RefreshBridgePanel())
            Catch
                ' Handle teardown race: drop the refresh.
            End Try
        End If
    End Sub

    Private Sub RefreshBridgePanel()
        If _bridge Is Nothing Then
            lblBridgeStatus.Text = "Bridge not attached"
            lblBridgeStatus.ForeColor = SystemColors.ControlLight
            Return
        End If

        _suppressBridgeUi = True
        Try
            cboBridgeMode.SelectedIndex = CInt(_bridge.Mode) ' enum values match item order (Off/Log-only/Live)
            chkBridgeArm.Checked = _bridge.LocalArmed
        Finally
            _suppressBridgeUi = False
        End Try

        Dim started As Boolean = _bridge.Started
        btnBridgeStartStop.Text = If(started, "STOP", "START")
        btnBridgeStartStop.BackColor = If(started, Color.LimeGreen, Color.DarkRed)

        Dim fresh As Boolean = _bridge.IsFreshNow
        lblBridgeStatus.Text = $"{_bridge.Mode} | {If(started, "STARTED", "stopped")} | Engine ARM: {If(_bridge.EngineArmed, "ON", "off")} | Payload: {If(fresh, "FRESH", "stale/none")}"
        lblBridgeStatus.ForeColor = If(started, Color.LimeGreen, If(fresh, SystemColors.ControlLight, Color.Orange))

        Dim summary As String = _bridge.LastSignalSummary
        Dim dispo As String = _bridge.LastDisposition
        lblBridgeLast.Text = "Last: " & If(summary.Length > 0, summary & "  ->  " & If(dispo.Length > 0, dispo, "(pending)"), "-")
    End Sub

    Private Sub cboBridgeMode_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cboBridgeMode.SelectedIndexChanged
        If _suppressBridgeUi OrElse _bridge Is Nothing Then Return
        Select Case cboBridgeMode.SelectedIndex
            Case 1
                _bridge.Mode = SignalBridge.BridgeMode.LogOnly
            Case 2
                _bridge.Mode = SignalBridge.BridgeMode.Live
            Case Else
                _bridge.Mode = SignalBridge.BridgeMode.Off
        End Select
        RefreshBridgePanel()
    End Sub

    Private Sub chkBridgeArm_CheckedChanged(sender As Object, e As EventArgs) Handles chkBridgeArm.CheckedChanged
        If _suppressBridgeUi OrElse _bridge Is Nothing Then Return
        _bridge.LocalArmed = chkBridgeArm.Checked
        RefreshBridgePanel()
    End Sub

    Private Sub btnBridgeStartStop_Click(sender As Object, e As EventArgs) Handles btnBridgeStartStop.Click
        If _bridge Is Nothing Then Return
        If _bridge.Started Then
            _bridge.Stop()
        Else
            Dim refusal As String = _bridge.TryStart() ' bridge logs the refusal/start itself
            If refusal IsNot Nothing Then
                lblBridgeStatus.Text = "START refused: " & refusal
                lblBridgeStatus.ForeColor = Color.Orange
                Return
            End If
        End If
        RefreshBridgePanel()
    End Sub

End Class
