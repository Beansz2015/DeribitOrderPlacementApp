<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class AutoTradeSettings
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()> _
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        components = New ComponentModel.Container()
        GroupBox1 = New GroupBox()
        txtEndTime = New TextBox()
        txtStartTime = New TextBox()
        Label7 = New Label()
        Label8 = New Label()
        grpTradeGates = New GroupBox()
        Label1 = New Label()
        txtCircuitBreaker = New TextBox()
        Label2 = New Label()
        txtCooloff = New TextBox()
        Label9 = New Label()
        grpSignalBridge = New GroupBox()
        lblBridgeModeCap = New Label()
        cboBridgeMode = New ComboBox()
        chkBridgeArm = New CheckBox()
        btnBridgeStartStop = New Button()
        lblBridgeStatus = New Label()
        lblBridgeLast = New Label()
        lblBridgeTiersCap = New Label()
        txtBridgeTiers = New TextBox()
        grpTooling = New GroupBox()
        lblAtrLenCap = New Label()
        txtAtrLength = New TextBox()
        lblAtrLenUnit = New Label()
        lblAtrFallbackCap = New Label()
        txtAtrFallback = New TextBox()
        lblAtrFallbackUnit = New Label()
        lblRiskPerTradeCap = New Label()
        txtRiskPerTrade = New TextBox()
        lblRiskPerTradeUnit = New Label()
        lblMaxSizeCap = New Label()
        txtMaxSize = New TextBox()
        lblMaxSizeUnit = New Label()
        btnRiskSize = New Button()
        lblAtrNow = New Label()
        AutoTradingToolTip = New ToolTip(components)
        GroupBox1.SuspendLayout()
        grpTradeGates.SuspendLayout()
        grpSignalBridge.SuspendLayout()
        grpTooling.SuspendLayout()
        SuspendLayout()
        '
        ' GroupBox1
        '
        ' Risk-sizing UI spec §1: the "AutoTrading Section" title label (lblBacktestTitle, a legacy
        ' name from the retired backtest era) was deleted and all four groups reflowed up into its
        ' space. ClientSize stays 512x856 - StickToHost top-aligns this form to the host and the
        ' matching heights are deliberate.
        GroupBox1.Controls.Add(txtEndTime)
        GroupBox1.Controls.Add(txtStartTime)
        GroupBox1.Controls.Add(Label7)
        GroupBox1.Controls.Add(Label8)
        GroupBox1.Font = New Font("Calibri", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        GroupBox1.ForeColor = SystemColors.ButtonFace
        GroupBox1.Location = New Point(18, 12)
        GroupBox1.Name = "GroupBox1"
        GroupBox1.Size = New Size(482, 100)
        GroupBox1.TabIndex = 1
        GroupBox1.TabStop = False
        GroupBox1.Text = "Inclusion Time Range"
        AutoTradingToolTip.SetToolTip(GroupBox1, "Auto trading is allowed ONLY inside this daily window (UTC+8)." & vbCrLf & "Leave BOTH blank for no time restriction." & vbCrLf & "If start is later than end, the window wraps past midnight.")
        '
        ' txtEndTime
        '
        txtEndTime.BackColor = Color.Black
        txtEndTime.BorderStyle = BorderStyle.FixedSingle
        txtEndTime.Font = New Font("Calibri", 14F)
        txtEndTime.ForeColor = Color.White
        txtEndTime.Location = New Point(396, 43)
        txtEndTime.Name = "txtEndTime"
        txtEndTime.Size = New Size(82, 42)
        txtEndTime.TabIndex = 2
        txtEndTime.Text = ""
        txtEndTime.TextAlign = HorizontalAlignment.Center
        '
        ' txtStartTime
        '
        txtStartTime.BackColor = Color.Black
        txtStartTime.BorderStyle = BorderStyle.FixedSingle
        txtStartTime.Font = New Font("Calibri", 14F)
        txtStartTime.ForeColor = Color.White
        txtStartTime.Location = New Point(150, 43)
        txtStartTime.Name = "txtStartTime"
        txtStartTime.Size = New Size(82, 42)
        txtStartTime.TabIndex = 1
        txtStartTime.Text = ""
        txtStartTime.TextAlign = HorizontalAlignment.Center
        '
        ' Label7
        '
        Label7.AutoSize = True
        Label7.Font = New Font("Calibri", 14F)
        Label7.ForeColor = SystemColors.ControlLight
        Label7.Location = New Point(264, 45)
        Label7.Name = "Label7"
        Label7.Size = New Size(129, 35)
        Label7.TabIndex = 0
        Label7.Text = "End Time:"
        '
        ' Label8
        '
        Label8.AutoSize = True
        Label8.Font = New Font("Calibri", 14F)
        Label8.ForeColor = SystemColors.ControlLight
        Label8.Location = New Point(11, 45)
        Label8.Name = "Label8"
        Label8.Size = New Size(139, 35)
        Label8.TabIndex = 0
        Label8.Text = "Start Time:"
        '
        ' grpTradeGates
        '
        grpTradeGates.Controls.Add(Label1)
        grpTradeGates.Controls.Add(txtCircuitBreaker)
        grpTradeGates.Controls.Add(Label2)
        grpTradeGates.Controls.Add(txtCooloff)
        grpTradeGates.Controls.Add(Label9)
        grpTradeGates.Font = New Font("Calibri", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        grpTradeGates.ForeColor = SystemColors.ButtonFace
        grpTradeGates.Location = New Point(18, 124)
        grpTradeGates.Name = "grpTradeGates"
        grpTradeGates.Size = New Size(482, 130)
        grpTradeGates.TabIndex = 2
        grpTradeGates.TabStop = False
        grpTradeGates.Text = "Trade Gates"
        '
        ' Label1
        '
        Label1.AutoSize = True
        Label1.Font = New Font("Calibri", 14F)
        Label1.ForeColor = SystemColors.ControlLight
        Label1.Location = New Point(11, 32)
        Label1.Name = "Label1"
        Label1.Size = New Size(258, 35)
        Label1.TabIndex = 0
        Label1.Text = "Max Loss Limit (USD):"
        AutoTradingToolTip.SetToolTip(Label1, "Circuit breaker: session loss in USD that trips auto trading" & vbCrLf & "and force-STOPs the bridge. 0 or less disables it.")
        '
        ' txtCircuitBreaker
        '
        txtCircuitBreaker.BackColor = Color.Black
        txtCircuitBreaker.BorderStyle = BorderStyle.FixedSingle
        txtCircuitBreaker.Font = New Font("Calibri", 14F)
        txtCircuitBreaker.ForeColor = Color.White
        txtCircuitBreaker.Location = New Point(350, 28)
        txtCircuitBreaker.Name = "txtCircuitBreaker"
        txtCircuitBreaker.Size = New Size(64, 42)
        txtCircuitBreaker.TabIndex = 1
        txtCircuitBreaker.Text = "-1"
        txtCircuitBreaker.TextAlign = HorizontalAlignment.Center
        '
        ' Label2
        '
        Label2.AutoSize = True
        Label2.Font = New Font("Calibri", 14F)
        Label2.ForeColor = SystemColors.ControlLight
        Label2.Location = New Point(11, 78)
        Label2.Name = "Label2"
        Label2.Size = New Size(253, 35)
        Label2.TabIndex = 0
        Label2.Text = "Trade Cooloff Period:"
        AutoTradingToolTip.SetToolTip(Label2, "After a position CLOSES, auto trading is disabled for this many" & vbCrLf & "minutes. Auto trading is already blocked for the whole life of a" & vbCrLf & "position, so this is the pause between trades. 0 disables it.")
        '
        ' txtCooloff
        '
        txtCooloff.BackColor = Color.Black
        txtCooloff.BorderStyle = BorderStyle.FixedSingle
        txtCooloff.Font = New Font("Calibri", 14F)
        txtCooloff.ForeColor = Color.White
        txtCooloff.Location = New Point(350, 74)
        txtCooloff.Name = "txtCooloff"
        txtCooloff.Size = New Size(64, 42)
        txtCooloff.TabIndex = 2
        txtCooloff.Text = "5"
        txtCooloff.TextAlign = HorizontalAlignment.Center
        '
        ' Label9
        '
        Label9.AutoSize = True
        Label9.Font = New Font("Calibri", 10F)
        Label9.ForeColor = SystemColors.ControlLight
        Label9.Location = New Point(420, 82)
        Label9.Name = "Label9"
        Label9.Size = New Size(50, 24)
        Label9.TabIndex = 0
        Label9.Text = "mins"
        '
        ' grpSignalBridge
        '
        grpSignalBridge.Controls.Add(lblBridgeModeCap)
        grpSignalBridge.Controls.Add(cboBridgeMode)
        grpSignalBridge.Controls.Add(chkBridgeArm)
        grpSignalBridge.Controls.Add(btnBridgeStartStop)
        grpSignalBridge.Controls.Add(lblBridgeStatus)
        grpSignalBridge.Controls.Add(lblBridgeLast)
        grpSignalBridge.Controls.Add(lblBridgeTiersCap)
        grpSignalBridge.Controls.Add(txtBridgeTiers)
        grpSignalBridge.Font = New Font("Calibri", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        grpSignalBridge.ForeColor = SystemColors.ButtonFace
        grpSignalBridge.Location = New Point(18, 266)
        grpSignalBridge.Name = "grpSignalBridge"
        grpSignalBridge.Size = New Size(482, 256)
        grpSignalBridge.TabIndex = 3
        grpSignalBridge.TabStop = False
        grpSignalBridge.Text = "SIGNAL BRIDGE"
        ' §1: the two lblBridgeSourceNote lines moved into this tooltip (APPENDED - the original
        ' bridge text must survive).
        AutoTradingToolTip.SetToolTip(grpSignalBridge, "VerdictEngine signal-bridge consumer (contract v1)." & vbCrLf & "Mode, ARM and START never persist - they reset to" & vbCrLf & "Off / unarmed / stopped at every app start." & vbCrLf & "Size = main form's Amount box." & vbCrLf & "Cooloff / max loss / window = sections above.")
        '
        ' lblBridgeModeCap
        '
        lblBridgeModeCap.AutoSize = True
        lblBridgeModeCap.Font = New Font("Calibri", 14F)
        lblBridgeModeCap.ForeColor = SystemColors.ControlLight
        lblBridgeModeCap.Location = New Point(11, 36)
        lblBridgeModeCap.Name = "lblBridgeModeCap"
        lblBridgeModeCap.Size = New Size(75, 35)
        lblBridgeModeCap.TabIndex = 0
        lblBridgeModeCap.Text = "Mode:"
        '
        ' cboBridgeMode
        '
        cboBridgeMode.BackColor = Color.Black
        cboBridgeMode.DropDownStyle = ComboBoxStyle.DropDownList
        cboBridgeMode.Font = New Font("Calibri", 14F)
        cboBridgeMode.ForeColor = Color.White
        cboBridgeMode.Items.AddRange(New Object() {"Off", "Log-only", "Live"})
        cboBridgeMode.Location = New Point(100, 32)
        cboBridgeMode.Name = "cboBridgeMode"
        cboBridgeMode.Size = New Size(140, 43)
        cboBridgeMode.TabIndex = 1
        AutoTradingToolTip.SetToolTip(cboBridgeMode, "Off = bridge idle." & vbCrLf & "Log-only = runs the full gate chain and logs 'would-act'" & vbCrLf & "instead of placing (soak mode; ignores ARM/START)." & vbCrLf & "Live = places orders once the interlock is satisfied.")
        '
        ' chkBridgeArm
        '
        chkBridgeArm.AutoSize = True
        chkBridgeArm.Font = New Font("Calibri", 12F)
        chkBridgeArm.ForeColor = SystemColors.ControlLight
        chkBridgeArm.Location = New Point(248, 38)
        chkBridgeArm.Name = "chkBridgeArm"
        chkBridgeArm.Size = New Size(213, 34)
        chkBridgeArm.TabIndex = 2
        chkBridgeArm.Text = "ARM AUTOTRADE"
        AutoTradingToolTip.SetToolTip(chkBridgeArm, "Your half of the dual-arm interlock (engine ARM + this + START)." & vbCrLf & "Unchecking force-STOPs immediately. Never persisted.")
        '
        ' btnBridgeStartStop
        '
        btnBridgeStartStop.BackColor = Color.DarkRed
        btnBridgeStartStop.Cursor = Cursors.Hand
        btnBridgeStartStop.Font = New Font("Calibri", 14F, FontStyle.Bold)
        btnBridgeStartStop.ForeColor = Color.White
        btnBridgeStartStop.Location = New Point(11, 86)
        btnBridgeStartStop.Name = "btnBridgeStartStop"
        btnBridgeStartStop.Size = New Size(140, 46)
        btnBridgeStartStop.TabIndex = 3
        btnBridgeStartStop.Text = "START"
        btnBridgeStartStop.UseVisualStyleBackColor = False
        AutoTradingToolTip.SetToolTip(btnBridgeStartStop, "START needs: mode Live + ARM + a fresh payload + engine armed" & vbCrLf & "+ the main form's Max Slippage ATR box checked." & vbCrLf & "Any disarm drops back to STOPPED (it is not sticky).")
        '
        ' lblBridgeStatus
        '
        lblBridgeStatus.Font = New Font("Calibri", 10F)
        lblBridgeStatus.ForeColor = SystemColors.ControlLight
        lblBridgeStatus.Location = New Point(11, 142)
        lblBridgeStatus.Name = "lblBridgeStatus"
        lblBridgeStatus.Size = New Size(460, 28)
        lblBridgeStatus.TabIndex = 0
        lblBridgeStatus.Text = "Bridge not attached"
        '
        ' lblBridgeLast
        '
        lblBridgeLast.Font = New Font("Calibri", 10F)
        lblBridgeLast.ForeColor = SystemColors.ControlLight
        lblBridgeLast.Location = New Point(11, 170)
        lblBridgeLast.Name = "lblBridgeLast"
        lblBridgeLast.Size = New Size(460, 28)
        lblBridgeLast.TabIndex = 0
        lblBridgeLast.Text = "Last: -"
        '
        ' lblBridgeTiersCap
        '
        lblBridgeTiersCap.AutoSize = True
        lblBridgeTiersCap.Font = New Font("Calibri", 14F)
        lblBridgeTiersCap.ForeColor = SystemColors.ControlLight
        lblBridgeTiersCap.Location = New Point(11, 208)
        lblBridgeTiersCap.Name = "lblBridgeTiersCap"
        lblBridgeTiersCap.Size = New Size(75, 35)
        lblBridgeTiersCap.TabIndex = 0
        lblBridgeTiersCap.Text = "Tiers:"
        AutoTradingToolTip.SetToolTip(lblBridgeTiersCap, "Confidence tiers to accept, comma-separated (HIGH, MEDIUM, LOW)." & vbCrLf & "Default HIGH,MEDIUM - which refuses WEAK signals (they carry LOW).")
        '
        ' txtBridgeTiers
        '
        txtBridgeTiers.BackColor = Color.Black
        txtBridgeTiers.BorderStyle = BorderStyle.FixedSingle
        txtBridgeTiers.Font = New Font("Calibri", 14F)
        txtBridgeTiers.ForeColor = Color.White
        txtBridgeTiers.Location = New Point(92, 204)
        txtBridgeTiers.Name = "txtBridgeTiers"
        txtBridgeTiers.Size = New Size(230, 42)
        txtBridgeTiers.TabIndex = 4
        txtBridgeTiers.Text = "HIGH,MEDIUM"
        '
        ' grpTooling
        '
        grpTooling.Controls.Add(lblAtrLenCap)
        grpTooling.Controls.Add(txtAtrLength)
        grpTooling.Controls.Add(lblAtrLenUnit)
        grpTooling.Controls.Add(lblAtrFallbackCap)
        grpTooling.Controls.Add(txtAtrFallback)
        grpTooling.Controls.Add(lblAtrFallbackUnit)
        grpTooling.Controls.Add(lblRiskPerTradeCap)
        grpTooling.Controls.Add(txtRiskPerTrade)
        grpTooling.Controls.Add(lblRiskPerTradeUnit)
        grpTooling.Controls.Add(lblMaxSizeCap)
        grpTooling.Controls.Add(txtMaxSize)
        grpTooling.Controls.Add(lblMaxSizeUnit)
        grpTooling.Controls.Add(btnRiskSize)
        grpTooling.Controls.Add(lblAtrNow)
        grpTooling.Font = New Font("Calibri", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        grpTooling.ForeColor = SystemColors.ButtonFace
        grpTooling.Location = New Point(18, 534)
        grpTooling.Name = "grpTooling"
        grpTooling.Size = New Size(482, 270)
        grpTooling.TabIndex = 4
        grpTooling.TabStop = False
        grpTooling.Text = "Tooling"
        AutoTradingToolTip.SetToolTip(grpTooling, "Low-level knobs that used to be hard-coded constants.")
        '
        ' lblAtrLenCap
        '
        lblAtrLenCap.AutoSize = True
        lblAtrLenCap.Font = New Font("Calibri", 14F)
        lblAtrLenCap.ForeColor = SystemColors.ControlLight
        lblAtrLenCap.Location = New Point(11, 34)
        lblAtrLenCap.Name = "lblAtrLenCap"
        lblAtrLenCap.Size = New Size(158, 35)
        lblAtrLenCap.TabIndex = 0
        lblAtrLenCap.Text = "ATR Length:"
        AutoTradingToolTip.SetToolTip(lblAtrLenCap, "Number of candles the indicator ATR is calculated over." & vbCrLf & "That ATR is the slippage guard's fallback when no fresh" & vbCrLf & "signal payload is available.")
        '
        ' txtAtrLength
        '
        txtAtrLength.BackColor = Color.Black
        txtAtrLength.BorderStyle = BorderStyle.FixedSingle
        txtAtrLength.Font = New Font("Calibri", 14F)
        txtAtrLength.ForeColor = Color.White
        txtAtrLength.Location = New Point(330, 30)
        txtAtrLength.Name = "txtAtrLength"
        txtAtrLength.Size = New Size(64, 42)
        txtAtrLength.TabIndex = 1
        txtAtrLength.Text = "14"
        txtAtrLength.TextAlign = HorizontalAlignment.Center
        '
        ' lblAtrLenUnit
        '
        lblAtrLenUnit.AutoSize = True
        lblAtrLenUnit.Font = New Font("Calibri", 10F)
        lblAtrLenUnit.ForeColor = SystemColors.ControlLight
        lblAtrLenUnit.Location = New Point(400, 38)
        lblAtrLenUnit.Name = "lblAtrLenUnit"
        lblAtrLenUnit.Size = New Size(80, 24)
        lblAtrLenUnit.TabIndex = 0
        lblAtrLenUnit.Text = "candles"
        '
        ' lblAtrFallbackCap
        '
        lblAtrFallbackCap.AutoSize = True
        lblAtrFallbackCap.Font = New Font("Calibri", 14F)
        lblAtrFallbackCap.ForeColor = SystemColors.ControlLight
        lblAtrFallbackCap.Location = New Point(11, 82)
        lblAtrFallbackCap.Name = "lblAtrFallbackCap"
        lblAtrFallbackCap.Size = New Size(178, 35)
        lblAtrFallbackCap.TabIndex = 0
        lblAtrFallbackCap.Text = "ATR Fallback:"
        AutoTradingToolTip.SetToolTip(lblAtrFallbackCap, "Last-resort slippage limit basis when NO ATR is available at all" & vbCrLf & "(no fresh signal and no indicator ATR). Was hard-coded at 70.")
        '
        ' txtAtrFallback
        '
        txtAtrFallback.BackColor = Color.Black
        txtAtrFallback.BorderStyle = BorderStyle.FixedSingle
        txtAtrFallback.Font = New Font("Calibri", 14F)
        txtAtrFallback.ForeColor = Color.White
        txtAtrFallback.Location = New Point(330, 78)
        txtAtrFallback.Name = "txtAtrFallback"
        txtAtrFallback.Size = New Size(64, 42)
        txtAtrFallback.TabIndex = 2
        txtAtrFallback.Text = "70"
        txtAtrFallback.TextAlign = HorizontalAlignment.Center
        '
        ' lblAtrFallbackUnit
        '
        lblAtrFallbackUnit.AutoSize = True
        lblAtrFallbackUnit.Font = New Font("Calibri", 10F)
        lblAtrFallbackUnit.ForeColor = SystemColors.ControlLight
        lblAtrFallbackUnit.Location = New Point(400, 86)
        lblAtrFallbackUnit.Name = "lblAtrFallbackUnit"
        lblAtrFallbackUnit.Size = New Size(50, 24)
        lblAtrFallbackUnit.TabIndex = 0
        lblAtrFallbackUnit.Text = "USD"
        '
        ' lblRiskPerTradeCap
        '
        ' Risk-sizing UI spec §2: the item-B keys get a UI (they were file-only). Values live in the
        ' HOST's userSettings (AppUserSettings) and persist via item A's existing save path; these
        ' boxes are commit-on-blur mirrors seeded from the host BEFORE the first commit (the §2
        ' initialisation-ordering trap - see InitialiseSettings).
        lblRiskPerTradeCap.AutoSize = True
        lblRiskPerTradeCap.Font = New Font("Calibri", 14F)
        lblRiskPerTradeCap.ForeColor = SystemColors.ControlLight
        lblRiskPerTradeCap.Location = New Point(11, 130)
        lblRiskPerTradeCap.Name = "lblRiskPerTradeCap"
        lblRiskPerTradeCap.Size = New Size(178, 35)
        lblRiskPerTradeCap.TabIndex = 0
        lblRiskPerTradeCap.Text = "Risk / Trade:"
        AutoTradingToolTip.SetToolTip(lblRiskPerTradeCap, "USD you are willing to lose if the stop is hit. The SIZE button" & vbCrLf & "computes size = risk x best price / stop distance (10-USD steps," & vbCrLf & "clamped to Max Size). Persists as risk_per_trade_usd in" & vbCrLf & "orderapp-settings.json.")
        '
        ' txtRiskPerTrade
        '
        txtRiskPerTrade.BackColor = Color.Black
        txtRiskPerTrade.BorderStyle = BorderStyle.FixedSingle
        txtRiskPerTrade.Font = New Font("Calibri", 14F)
        txtRiskPerTrade.ForeColor = Color.White
        txtRiskPerTrade.Location = New Point(330, 126)
        txtRiskPerTrade.Name = "txtRiskPerTrade"
        txtRiskPerTrade.Size = New Size(64, 42)
        txtRiskPerTrade.TabIndex = 3
        txtRiskPerTrade.Text = "25"
        txtRiskPerTrade.TextAlign = HorizontalAlignment.Center
        '
        ' lblRiskPerTradeUnit
        '
        lblRiskPerTradeUnit.AutoSize = True
        lblRiskPerTradeUnit.Font = New Font("Calibri", 10F)
        lblRiskPerTradeUnit.ForeColor = SystemColors.ControlLight
        lblRiskPerTradeUnit.Location = New Point(400, 134)
        lblRiskPerTradeUnit.Name = "lblRiskPerTradeUnit"
        lblRiskPerTradeUnit.Size = New Size(50, 24)
        lblRiskPerTradeUnit.TabIndex = 0
        lblRiskPerTradeUnit.Text = "USD"
        '
        ' lblMaxSizeCap
        '
        lblMaxSizeCap.AutoSize = True
        lblMaxSizeCap.Font = New Font("Calibri", 14F)
        lblMaxSizeCap.ForeColor = SystemColors.ControlLight
        lblMaxSizeCap.Location = New Point(11, 178)
        lblMaxSizeCap.Name = "lblMaxSizeCap"
        lblMaxSizeCap.Size = New Size(178, 35)
        lblMaxSizeCap.TabIndex = 0
        lblMaxSizeCap.Text = "Max Size:"
        AutoTradingToolTip.SetToolTip(lblMaxSizeCap, "Hard cap on the computed size, in USD (applied after the risk" & vbCrLf & "formula; floored to the 10-USD contract step). While a stop is" & vbCrLf & "tighter than risk x price / max size, this cap is what the SIZE" & vbCrLf & "button returns. Persists as max_size_usd in orderapp-settings.json.")
        '
        ' txtMaxSize
        '
        txtMaxSize.BackColor = Color.Black
        txtMaxSize.BorderStyle = BorderStyle.FixedSingle
        txtMaxSize.Font = New Font("Calibri", 14F)
        txtMaxSize.ForeColor = Color.White
        txtMaxSize.Location = New Point(330, 174)
        txtMaxSize.Name = "txtMaxSize"
        txtMaxSize.Size = New Size(64, 42)
        txtMaxSize.TabIndex = 4
        txtMaxSize.Text = "500"
        txtMaxSize.TextAlign = HorizontalAlignment.Center
        '
        ' lblMaxSizeUnit
        '
        lblMaxSizeUnit.AutoSize = True
        lblMaxSizeUnit.Font = New Font("Calibri", 10F)
        lblMaxSizeUnit.ForeColor = SystemColors.ControlLight
        lblMaxSizeUnit.Location = New Point(400, 182)
        lblMaxSizeUnit.Name = "lblMaxSizeUnit"
        lblMaxSizeUnit.Size = New Size(50, 24)
        lblMaxSizeUnit.TabIndex = 0
        lblMaxSizeUnit.Text = "USD"
        '
        ' btnRiskSize
        '
        ' Risk-sizing UI spec §3: relocated from the main form's AMOUNT($) group (where it was
        ' cramped and half-hidden). Spans both risk rows in the caption/textbox gutter - big and
        ' obvious is the point. Caption "SIZE" measures ~55px at 14pt bold on ONE line, well under
        ' 120 (the btnAutoSettings wrap-clip lesson). Handler = thin forwarder to the host's
        ' ApplyRiskBasedSize, committing half-typed risk/max-size edits first.
        btnRiskSize.BackColor = Color.MediumTurquoise
        btnRiskSize.Cursor = Cursors.Hand
        btnRiskSize.Font = New Font("Calibri", 14F, FontStyle.Bold)
        btnRiskSize.ForeColor = SystemColors.ControlText
        btnRiskSize.Location = New Point(200, 126)
        btnRiskSize.Name = "btnRiskSize"
        btnRiskSize.Size = New Size(120, 90)
        btnRiskSize.TabIndex = 5
        btnRiskSize.Text = "SIZE"
        AutoTradingToolTip.SetToolTip(btnRiskSize, "Risk-based size: risk_per_trade_usd x best price / stop distance" & vbCrLf & "(manual SL if set, else Trig. P.), floored to a 10-USD multiple and" & vbCrLf & "clamped to max_size_usd. Both keys live in orderapp-settings.json.")
        btnRiskSize.UseVisualStyleBackColor = False
        '
        ' lblAtrNow
        '
        ' Live readout: FrmIndicators is retired, so its ATR display is gone - this is now the only
        ' place the effective ATR is visible, and it is what proves the headless indicator engine is
        ' actually running. Ticks once a second while this form is open.
        lblAtrNow.Font = New Font("Calibri", 11F, FontStyle.Bold)
        lblAtrNow.ForeColor = SystemColors.ControlLight
        lblAtrNow.Location = New Point(11, 226)
        lblAtrNow.Name = "lblAtrNow"
        lblAtrNow.Size = New Size(460, 30)
        lblAtrNow.TabIndex = 0
        lblAtrNow.Text = "Current ATR: -"
        ' §1: lblToolingNote's priority line moved into this tooltip (APPENDED - the original
        ' readout text must survive).
        AutoTradingToolTip.SetToolTip(lblAtrNow, "The ATR the slippage guard is using right now, its source," & vbCrLf & "and the resulting limit. Updates every second while this" & vbCrLf & "window is open." & vbCrLf & "Priority: payload, then indicator, then fallback.")
        '
        ' AutoTradeSettings
        '
        AutoScaleDimensions = New SizeF(10F, 25F)
        AutoScaleMode = AutoScaleMode.Font
        BackColor = SystemColors.ActiveCaptionText
        ClientSize = New Size(512, 856)
        Controls.Add(grpTooling)
        Controls.Add(grpSignalBridge)
        Controls.Add(grpTradeGates)
        Controls.Add(GroupBox1)
        Name = "AutoTradeSettings"
        StartPosition = FormStartPosition.Manual
        Text = "AutoTradeSettings"
        TopMost = True
        GroupBox1.ResumeLayout(False)
        GroupBox1.PerformLayout()
        grpTradeGates.ResumeLayout(False)
        grpTradeGates.PerformLayout()
        grpSignalBridge.ResumeLayout(False)
        grpSignalBridge.PerformLayout()
        grpTooling.ResumeLayout(False)
        grpTooling.PerformLayout()
        ResumeLayout(False)
        PerformLayout()
    End Sub
    Friend WithEvents GroupBox1 As GroupBox
    Friend WithEvents txtEndTime As TextBox
    Friend WithEvents txtStartTime As TextBox
    Friend WithEvents Label7 As Label
    Friend WithEvents Label8 As Label
    Friend WithEvents grpTradeGates As GroupBox
    Friend WithEvents Label1 As Label
    Friend WithEvents txtCircuitBreaker As TextBox
    Friend WithEvents Label2 As Label
    Friend WithEvents txtCooloff As TextBox
    Friend WithEvents Label9 As Label
    Friend WithEvents grpSignalBridge As GroupBox
    Friend WithEvents lblBridgeModeCap As Label
    Friend WithEvents cboBridgeMode As ComboBox
    Friend WithEvents chkBridgeArm As CheckBox
    Friend WithEvents btnBridgeStartStop As Button
    Friend WithEvents lblBridgeStatus As Label
    Friend WithEvents lblBridgeLast As Label
    Friend WithEvents lblBridgeTiersCap As Label
    Friend WithEvents txtBridgeTiers As TextBox
    Friend WithEvents grpTooling As GroupBox
    Friend WithEvents lblAtrLenCap As Label
    Friend WithEvents txtAtrLength As TextBox
    Friend WithEvents lblAtrLenUnit As Label
    Friend WithEvents lblAtrFallbackCap As Label
    Friend WithEvents txtAtrFallback As TextBox
    Friend WithEvents lblAtrFallbackUnit As Label
    Friend WithEvents lblRiskPerTradeCap As Label
    Friend WithEvents txtRiskPerTrade As TextBox
    Friend WithEvents lblRiskPerTradeUnit As Label
    Friend WithEvents lblMaxSizeCap As Label
    Friend WithEvents txtMaxSize As TextBox
    Friend WithEvents lblMaxSizeUnit As Label
    Friend WithEvents btnRiskSize As Button
    Friend WithEvents lblAtrNow As Label
    Friend WithEvents AutoTradingToolTip As ToolTip
End Class
