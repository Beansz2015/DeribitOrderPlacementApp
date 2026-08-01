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
        chkSessionPolicyOn = New CheckBox()
        chkRiskSizeBridge = New CheckBox()
        txtSessionPolicy = New TextBox()
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
        lblMinNetMoveCap = New Label()
        txtMinNetMove = New TextBox()
        lblMinNetMoveUnit = New Label()
        lblAtrNow = New Label()
        AutoTradingToolTip = New ToolTip(components)
        grpTradeGates.SuspendLayout()
        grpSignalBridge.SuspendLayout()
        grpTooling.SuspendLayout()
        SuspendLayout()
        '
        ' Session policy spec D5/§5: GroupBox1 ("Inclusion Time Range") was DELETED and its four
        ' controls moved into grpTradeGates below, which frees ~112px for the policy box in SIGNAL
        ' BRIDGE. The controls keep their names, handlers, mirrors and harness AccessibleNames by
        ' construction - only their parent and coordinates changed. GroupBox1's window tooltip (the
        ' UTC+8 / blank-blank / midnight-wrap text) is migrated verbatim onto BOTH time boxes below,
        ' since they carried none of their own and the group that explained them no longer exists.
        '
        ' txtEndTime
        '
        txtEndTime.BackColor = Color.Black
        txtEndTime.BorderStyle = BorderStyle.FixedSingle
        txtEndTime.Font = New Font("Calibri", 14F)
        txtEndTime.ForeColor = Color.White
        txtEndTime.Location = New Point(396, 125)
        txtEndTime.Name = "txtEndTime"
        txtEndTime.Size = New Size(82, 42)
        txtEndTime.TabIndex = 4
        txtEndTime.Text = ""
        txtEndTime.TextAlign = HorizontalAlignment.Center
        AutoTradingToolTip.SetToolTip(txtEndTime, "Auto trading is allowed ONLY inside this daily window (UTC+8)." & vbCrLf & "Leave BOTH blank for no time restriction." & vbCrLf & "If start is later than end, the window wraps past midnight.")
        '
        ' txtStartTime
        '
        txtStartTime.BackColor = Color.Black
        txtStartTime.BorderStyle = BorderStyle.FixedSingle
        txtStartTime.Font = New Font("Calibri", 14F)
        txtStartTime.ForeColor = Color.White
        txtStartTime.Location = New Point(150, 125)
        txtStartTime.Name = "txtStartTime"
        txtStartTime.Size = New Size(82, 42)
        txtStartTime.TabIndex = 3
        txtStartTime.Text = ""
        txtStartTime.TextAlign = HorizontalAlignment.Center
        AutoTradingToolTip.SetToolTip(txtStartTime, "Auto trading is allowed ONLY inside this daily window (UTC+8)." & vbCrLf & "Leave BOTH blank for no time restriction." & vbCrLf & "If start is later than end, the window wraps past midnight.")
        '
        ' Label7
        '
        Label7.AutoSize = True
        Label7.Font = New Font("Calibri", 14F)
        Label7.ForeColor = SystemColors.ControlLight
        Label7.Location = New Point(264, 127)
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
        Label8.Location = New Point(11, 127)
        Label8.Name = "Label8"
        Label8.Size = New Size(139, 35)
        Label8.TabIndex = 0
        Label8.Text = "Start Time:"
        '
        ' grpTradeGates
        '
        ' Risk-sizing UI spec §1: the "AutoTrading Section" title label (lblBacktestTitle, a legacy
        ' name from the retired backtest era) was deleted and all four groups reflowed up into its
        ' space. ClientSize is 512x856, matching the host's 856 exactly - StickToHost top-aligns this
        ' form to the host, so the matched heights give matched bottom edges. EV chase budget §4
        ' briefly broke this (512x904 for the fifth Tooling row); RECLAIMED 2026-07-30 by the owner's
        ' ruling - see grpTooling below. The match is a real constraint, not decoration: anything
        ' added here has to earn its height inside 856 or it moves the host's bottom edge out of line.
        ' Session policy spec D5: the Inclusion Time Range row is now the third row of THIS group
        ' (breaker y28 / cooloff y74 / time y125, all unchanged in x).
        grpTradeGates.Controls.Add(Label1)
        grpTradeGates.Controls.Add(txtCircuitBreaker)
        grpTradeGates.Controls.Add(Label2)
        grpTradeGates.Controls.Add(txtCooloff)
        grpTradeGates.Controls.Add(Label9)
        grpTradeGates.Controls.Add(Label8)
        grpTradeGates.Controls.Add(txtStartTime)
        grpTradeGates.Controls.Add(Label7)
        grpTradeGates.Controls.Add(txtEndTime)
        grpTradeGates.Font = New Font("Calibri", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        grpTradeGates.ForeColor = SystemColors.ButtonFace
        grpTradeGates.Location = New Point(18, 12)
        grpTradeGates.Name = "grpTradeGates"
        grpTradeGates.Size = New Size(482, 176)
        grpTradeGates.TabIndex = 2
        grpTradeGates.TabStop = False
        ' "&&" renders as a single literal ampersand - a lone "&" is the WinForms mnemonic prefix and
        ' would be swallowed, underlining the "I" instead of showing the separator.
        grpTradeGates.Text = "Trade Gates && Inclusion Time Range"
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
        txtCircuitBreaker.Text = "10"
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
        grpSignalBridge.Controls.Add(chkRiskSizeBridge)
        grpSignalBridge.Controls.Add(chkSessionPolicyOn)
        grpSignalBridge.Controls.Add(txtSessionPolicy)
        grpSignalBridge.Font = New Font("Calibri", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        grpSignalBridge.ForeColor = SystemColors.ButtonFace
        grpSignalBridge.Location = New Point(18, 200)
        grpSignalBridge.Name = "grpSignalBridge"
        grpSignalBridge.Size = New Size(482, 344)
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
        lblBridgeStatus.AutoEllipsis = True  ' item 19: truncate long status strings gracefully
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
        ' chkRiskSizeBridge
        '
        ' N2 (docs/spec-risk-sized-bridge-trades.md §2). PLACEMENT AND CAPTION ARE BOTH RULED, and
        ' both differ from the spec as originally written - see the amendment block there.
        '
        ' The spec first said the Tooling group, "under the Risk/Max rows". That space does not
        ' exist: grpTooling's five rows end at 256 of 266 and lblAtrNow sits 4px under the group, so
        ' there is ZERO vertical slack inside the pinned 512x856 - the EV reclaim already spent it
        ' (that is why lblAtrNow was lifted onto the form). Every remaining Tooling space is a
        ' mid-row gutter, where a checkbox reads as a modifier of ATR Length or Min Net Profit.
        ' Measured, not estimated: a probe that constructs this form at runtime and dumps live
        ' bounds. The caption "Risk-size bridge trades" measures 174px against gutters of 123/104/100
        ' and fits none of them.
        '
        ' Here instead, and on the merits rather than as a fallback: this is a BRIDGE behaviour, so
        ' it belongs in the bridge group's right-hand checkbox column beside ARM AUTOTRADE and
        ' Policy - where a terse caption carrying a whole feature, with the meaning in the tooltip,
        ' is already house style. It reuses the Risk/Max boxes but does not have to sit beside them.
        ' Sits right of txtBridgeTiers (which ends at x=322) and inside lblBridgeStatus's proven
        ' right edge of 471. Styled 12F like the two checkboxes it joins; it is CONFIG, not an arm.
        chkRiskSizeBridge.AutoSize = True
        chkRiskSizeBridge.Font = New Font("Calibri", 12F)
        chkRiskSizeBridge.ForeColor = SystemColors.ControlLight
        chkRiskSizeBridge.Location = New Point(340, 208)
        chkRiskSizeBridge.Name = "chkRiskSizeBridge"
        chkRiskSizeBridge.Size = New Size(116, 34)
        chkRiskSizeBridge.TabIndex = 7
        chkRiskSizeBridge.Text = "Risk-size"
        AutoTradingToolTip.SetToolTip(chkRiskSizeBridge, "Size each BRIDGE trade by risk over the ENGINE's own stop distance," & vbCrLf & "instead of the Amount box: risk / trade x entry / |entry - stop|," & vbCrLf & "floored to the 10-USD step and capped by Max Size (both in Tooling)." & vbCrLf & "OFF = the Amount box decides the size, exactly as today." & vbCrLf & "The session multiplier applies on top EITHER WAY - one chain, never twice." & vbCrLf & "The Amount box must still be non-zero: an empty box is refused before" & vbCrLf & "sizing runs. The manual SIZE button is unaffected by this switch." & vbCrLf & "Persists as risk_size_bridge_trades in orderapp-settings.json.")
        '
        ' chkSessionPolicyOn
        '
        ' The caption IS the enable switch (session policy spec §5). Styled like chkBridgeArm (12F)
        ' because it belongs to the same visual family, but it is CONFIG, not an arm: it turns a
        ' filter on and cannot place or permit anything by itself.
        chkSessionPolicyOn.AutoSize = True
        chkSessionPolicyOn.Font = New Font("Calibri", 12F)
        chkSessionPolicyOn.ForeColor = SystemColors.ControlLight
        chkSessionPolicyOn.Location = New Point(11, 262)
        chkSessionPolicyOn.Name = "chkSessionPolicyOn"
        chkSessionPolicyOn.Size = New Size(90, 34)
        chkSessionPolicyOn.TabIndex = 5
        chkSessionPolicyOn.Text = "Policy"
        AutoTradingToolTip.SetToolTip(chkSessionPolicyOn, "Per-session trader policy: accept only chosen confidence tiers and" & vbCrLf & "verdict contexts per UTC session, with an optional size multiplier." & vbCrLf & "OFF = today's behaviour exactly." & vbCrLf & "It can only NARROW what the Tiers box above already allows, never widen it." & vbCrLf & "This is config, not an arm - Mode / ARM / START are unchanged.")
        '
        ' txtSessionPolicy
        '
        txtSessionPolicy.BackColor = Color.Black
        txtSessionPolicy.BorderStyle = BorderStyle.FixedSingle
        txtSessionPolicy.Font = New Font("Calibri", 10F)
        txtSessionPolicy.ForeColor = Color.White
        ' Moved left to x=110 (the "Policy" caption only needs ~100px) and widened to 360 - right
        ' edge still 470, matching lblBridgeStatus's proven edge, but 40px more room for the text.
        txtSessionPolicy.Location = New Point(110, 254)
        txtSessionPolicy.Multiline = True
        txtSessionPolicy.Name = "txtSessionPolicy"
        ' WordWrap OFF is LOAD-BEARING, not cosmetic. With the WinForms default (True) a line that
        ' overruns the box wraps onto a second visual row and pushes the LAST session line out of
        ' view entirely - caught on testnet 2026-07-21, where "LONDON = MEDIUM | CONFIRMED | 0.5"
        ' wrapped and the whole ASIA line silently disappeared. A policy line you cannot see is one
        ' you cannot check, which is exactly the failure this box exists to prevent. With wrap off,
        ' one config line is always one visual row: an over-long line clips at the right (still
        ' scrollable by caret, and the value itself is never affected) instead of hiding a session.
        txtSessionPolicy.WordWrap = False
        txtSessionPolicy.Size = New Size(360, 80)
        txtSessionPolicy.TabIndex = 6
        txtSessionPolicy.Text = ""
        AutoTradingToolTip.SetToolTip(txtSessionPolicy, "One line per session:  SESSION = tiers | contexts | size_mult" & vbCrLf & "e.g.  LONDON = MEDIUM | CONFIRMED | 0.5" & vbCrLf & "Sessions (UTC): ASIA 00-07, LONDON 08-12, NY 13-23." & vbCrLf & "Tiers HIGH/MEDIUM/LOW (STRONG and WEAK also accepted)." & vbCrLf & "Contexts: 'any', or a comma-separated list matched exactly." & vbCrLf & "size_mult (0,1]; 1.0 leaves size alone, less floors to the 10-USD step." & vbCrLf & "An omitted session = HIGH,MEDIUM | any | 1.0. Blank box = all defaults." & vbCrLf & "Saved as session_policy in orderapp-settings.json.")
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
        grpTooling.Controls.Add(lblMinNetMoveCap)
        grpTooling.Controls.Add(txtMinNetMove)
        grpTooling.Controls.Add(lblMinNetMoveUnit)
        grpTooling.Font = New Font("Calibri", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        grpTooling.ForeColor = SystemColors.ButtonFace
        grpTooling.Location = New Point(18, 556)
        grpTooling.Name = "grpTooling"
        ' EV chase budget §4 first grew this to 318 for the fifth row, which broke the host height
        ' match. RECLAIMED (owner ruling 2026-07-30): back inside the original envelope by tightening
        ' the row pitch 48 -> 46 (the pitch grpTradeGates already uses) and lifting lblAtrNow OUT of
        ' this group into the strip below it. The boxes themselves could NOT shrink - a single-line
        ' TextBox clamps its height to the font, so 42 is fixed at Calibri 14pt, and five of them
        ' plus a 26px readout will not fit 270 at any pitch. Five rows now end at 256; 10px margin.
        grpTooling.Size = New Size(482, 266)
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
        txtAtrLength.Text = "7"
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
        lblAtrFallbackCap.Location = New Point(11, 80)
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
        txtAtrFallback.Location = New Point(330, 76)
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
        lblAtrFallbackUnit.Location = New Point(400, 84)
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
        lblRiskPerTradeCap.Location = New Point(11, 126)
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
        txtRiskPerTrade.Location = New Point(330, 122)
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
        lblRiskPerTradeUnit.Location = New Point(400, 130)
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
        lblMaxSizeCap.Location = New Point(11, 172)
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
        txtMaxSize.Location = New Point(330, 168)
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
        lblMaxSizeUnit.Location = New Point(400, 176)
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
        btnRiskSize.Location = New Point(200, 122)
        btnRiskSize.Name = "btnRiskSize"
        btnRiskSize.Size = New Size(120, 88)
        btnRiskSize.TabIndex = 5
        btnRiskSize.Text = "SIZE"
        AutoTradingToolTip.SetToolTip(btnRiskSize, "Risk-based size: risk_per_trade_usd x best price / stop distance" & vbCrLf & "(manual SL if set, else Trig. P.), floored to a 10-USD multiple and" & vbCrLf & "clamped to max_size_usd. Both keys live in orderapp-settings.json.")
        btnRiskSize.UseVisualStyleBackColor = False
        '
        ' lblMinNetMoveCap
        '
        ' EV chase budget (docs/spec-ev-chase-budget.md §4). THE UNIT TRAP, stated once and loudly:
        ' this box is typed as a PERCENT (0.05 = 5 bps), and CommitToolingConfig divides by 100
        ' before it reaches the host - everything downstream of that (the min_net_move_pct key, the
        ' host mirror, the predicate's minNetMovePct argument) is a price FRACTION. The value lives
        ' in the HOST's userSettings and persists on item A's save path; this box is a commit-on-blur
        ' mirror seeded from the host BEFORE the first commit (the standing seed-before-commit trap
        ' - see InitialiseSettings).
        lblMinNetMoveCap.AutoSize = True
        lblMinNetMoveCap.Font = New Font("Calibri", 14F)
        lblMinNetMoveCap.ForeColor = SystemColors.ControlLight
        lblMinNetMoveCap.Location = New Point(11, 218)
        lblMinNetMoveCap.Name = "lblMinNetMoveCap"
        lblMinNetMoveCap.Size = New Size(178, 35)
        lblMinNetMoveCap.TabIndex = 0
        ' Caption RENAMED to "Min Net Profit:" by owner ruling 2026-07-30. DISPLAY TEXT ONLY - every
        ' identifier behind it deliberately still says MinNetMove (lblMinNetMoveCap, txtMinNetMove,
        ' minNetMovePctVal, MinNetMovePct, SetMinNetMovePct, SeedMinNetMoveFromHost) and so does the
        ' persisted key min_net_move_pct and the harness AccessibleName. If you grepped one spelling
        ' and found nothing, try the other - they are the same knob.
        lblMinNetMoveCap.Text = "Min Net Profit:"
        AutoTradingToolTip.SetToolTip(lblMinNetMoveCap, "EV chase budget: stop chasing an entry once the move still left to" & vbCrLf & "the take-profit no longer covers round-trip fees PLUS this much" & vbCrLf & "net profit. The chase is then abandoned with 'EV floor' instead of" & vbCrLf & "running on to the ATR slippage cap." & vbCrLf & "UNIT: PERCENT of price. 0.05 here = 0.05% = 5 bps." & vbCrLf & "0 (the default) turns it OFF - behaviour is then exactly as before." & vbCrLf & "Only applies while a TP is in force (bridge trades always have one;" & vbCrLf & "manual OFFSET trades keep the ATR cap alone)." & vbCrLf & "Persists as min_net_move_pct (a FRACTION: 0.0005) in" & vbCrLf & "orderapp-settings.json, alongside the maker/taker fee bps keys.")
        '
        ' txtMinNetMove
        '
        txtMinNetMove.BackColor = Color.Black
        txtMinNetMove.BorderStyle = BorderStyle.FixedSingle
        txtMinNetMove.Font = New Font("Calibri", 14F)
        txtMinNetMove.ForeColor = Color.White
        txtMinNetMove.Location = New Point(330, 214)
        txtMinNetMove.Name = "txtMinNetMove"
        txtMinNetMove.Size = New Size(64, 42)
        txtMinNetMove.TabIndex = 6
        txtMinNetMove.Text = "0"
        txtMinNetMove.TextAlign = HorizontalAlignment.Center
        '
        ' lblMinNetMoveUnit
        '
        lblMinNetMoveUnit.AutoSize = True
        lblMinNetMoveUnit.Font = New Font("Calibri", 10F)
        lblMinNetMoveUnit.ForeColor = SystemColors.ControlLight
        lblMinNetMoveUnit.Location = New Point(400, 222)
        lblMinNetMoveUnit.Name = "lblMinNetMoveUnit"
        lblMinNetMoveUnit.Size = New Size(50, 24)
        lblMinNetMoveUnit.TabIndex = 0
        lblMinNetMoveUnit.Text = "%"
        '
        ' lblAtrNow
        '
        ' Live readout: FrmIndicators is retired, so its ATR display is gone - this is now the only
        ' place the effective ATR is visible, and it is what proves the headless indicator engine is
        ' actually running. Ticks once a second while this form is open.
        '
        ' PARENT CHANGED 2026-07-30 (48px reclaim): this label now belongs to the FORM, not to
        ' grpTooling, and sits in the strip below that group - so its Location is form-relative
        ' (x 29 = the group's x 18 + the old in-group x 11; the column is unchanged on screen).
        ' It is a status readout rather than a knob, so reading as a status line under the group is
        ' the natural place for it, and it is the only 26+px that could leave the group: the five
        ' knob rows are 42px single-line TextBoxes whose height is font-clamped and unshrinkable.
        ' RefreshAtrReadout / the tooltip are unaffected - only the parent and the origin moved.
        lblAtrNow.Font = New Font("Calibri", 11F, FontStyle.Bold)
        lblAtrNow.ForeColor = SystemColors.ControlLight
        lblAtrNow.Location = New Point(29, 826)
        lblAtrNow.Name = "lblAtrNow"
        lblAtrNow.Size = New Size(460, 26)
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
        ' Back to the host's 856 (frmMainPageV2 is 1080x856) - StickToHost top-aligns the two, so
        ' matched heights mean matched bottom edges again. grpTooling ends at 556+266 = 822;
        ' lblAtrNow occupies 826..852; 4px to the form edge.
        ClientSize = New Size(512, 856)
        Controls.Add(lblAtrNow)
        Controls.Add(grpTooling)
        Controls.Add(grpSignalBridge)
        Controls.Add(grpTradeGates)
        Name = "AutoTradeSettings"
        StartPosition = FormStartPosition.Manual
        Text = "AutoTradeSettings"
        TopMost = True
        grpTradeGates.ResumeLayout(False)
        grpTradeGates.PerformLayout()
        grpSignalBridge.ResumeLayout(False)
        grpSignalBridge.PerformLayout()
        grpTooling.ResumeLayout(False)
        grpTooling.PerformLayout()
        ResumeLayout(False)
        PerformLayout()
    End Sub
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
    Friend WithEvents chkSessionPolicyOn As CheckBox
    Friend WithEvents chkRiskSizeBridge As CheckBox
    Friend WithEvents txtSessionPolicy As TextBox
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
    Friend WithEvents lblMinNetMoveCap As Label
    Friend WithEvents txtMinNetMove As TextBox
    Friend WithEvents lblMinNetMoveUnit As Label
    Friend WithEvents lblAtrNow As Label
    Friend WithEvents AutoTradingToolTip As ToolTip
End Class
