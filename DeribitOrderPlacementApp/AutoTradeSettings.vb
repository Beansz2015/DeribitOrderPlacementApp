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

    <ComponentModel.Browsable(False)>
    <ComponentModel.DesignerSerializationVisibility(ComponentModel.DesignerSerializationVisibility.Hidden)>
    Friend Property Bridge As SignalBridge
        Get
            Return _bridge
        End Get
        Set(value As SignalBridge)
            If _bridge IsNot Nothing Then RemoveHandler _bridge.StatusChanged, AddressOf OnBridgeStatusChanged
            _bridge = value
            If _bridge IsNot Nothing Then
                AddHandler _bridge.StatusChanged, AddressOf OnBridgeStatusChanged
                LoadBridgeConfigIntoPanel()
            End If
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
        If _host IsNot Nothing Then
            RemoveHandler _host.LocationChanged, AddressOf HostMovedOrResized
            RemoveHandler _host.SizeChanged, AddressOf HostMovedOrResized
        End If
        If _bridge IsNot Nothing Then RemoveHandler _bridge.StatusChanged, AddressOf OnBridgeStatusChanged
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

    Private Sub LoadBridgeConfigIntoPanel()
        txtBridgeTiers.Text = _bridge.TiersCsv
        txtBridgeSize.Text = _bridge.SizeUsd.ToString()
        txtBridgeCooloff.Text = _bridge.CooloffMin.ToString()
        txtBridgeBreaker.Text = _bridge.CircuitBreakerUsd.ToString()
        txtBridgeWinStart.Text = _bridge.WindowStart
        txtBridgeWinEnd.Text = _bridge.WindowEnd
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

    Private Sub btnBridgeSave_Click(sender As Object, e As EventArgs) Handles btnBridgeSave.Click
        If _bridge Is Nothing Then Return
        Dim sizeUsd, cooloff, breaker As Decimal
        If Not Decimal.TryParse(txtBridgeSize.Text, sizeUsd) Then
            ShowBridgeConfigError($"invalid size '{txtBridgeSize.Text}'")
            Return
        End If
        If Not Decimal.TryParse(txtBridgeCooloff.Text, cooloff) Then
            ShowBridgeConfigError($"invalid cooloff '{txtBridgeCooloff.Text}'")
            Return
        End If
        If Not Decimal.TryParse(txtBridgeBreaker.Text, breaker) Then
            ShowBridgeConfigError($"invalid breaker '{txtBridgeBreaker.Text}'")
            Return
        End If
        Dim err As String = _bridge.SaveConfig(txtBridgeTiers.Text, sizeUsd, cooloff, breaker,
                                               txtBridgeWinStart.Text, txtBridgeWinEnd.Text)
        If err IsNot Nothing Then
            ShowBridgeConfigError(err)
            Return
        End If
        LoadBridgeConfigIntoPanel() ' echo back the normalized values
        RefreshBridgePanel()
    End Sub

    Private Sub ShowBridgeConfigError(msg As String)
        lblBridgeStatus.Text = "Config: " & msg
        lblBridgeStatus.ForeColor = Color.Orange
    End Sub

End Class