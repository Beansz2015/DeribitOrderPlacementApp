<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class FrmIndicators
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()>
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
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        lblDMI = New Label()
        lblMACD = New Label()
        lblRSI = New Label()
        lblStoch = New Label()
        lblDMITitle = New Label()
        lblMACDTitle = New Label()
        lblRSITitle = New Label()
        lblStochTitle = New Label()
        txtIndLogs = New RichTextBox()
        redHeartBeat = New RadioButton()
        lblEMATitle = New Label()
        lblEMA = New Label()
        lblVWAPTitle = New Label()
        lblVWAP = New Label()
        lblScore = New Label()
        lblATRTitle = New Label()
        lblATR = New Label()
        btnBacktest = New Button()
        lblBacktestTitle = New Label()
        lblTestATR = New Label()
        lblTestLScore = New Label()
        lblTestATRTP = New Label()
        lblTestATRSL = New Label()
        lblTestSScore = New Label()
        Label1 = New Label()
        txtATR = New TextBox()
        txtTP = New TextBox()
        txtSL = New TextBox()
        txtTestTime = New TextBox()
        Label2 = New Label()
        Label3 = New Label()
        Label4 = New Label()
        txtLScore = New TextBox()
        txtSScore = New TextBox()
        Label5 = New Label()
        Label6 = New Label()
        btnATR = New Button()
        txtATRLimit = New TextBox()
        btnFormLoadInfo = New Button()
        SuspendLayout()
        ' 
        ' lblDMI
        ' 
        lblDMI.AutoSize = True
        lblDMI.Font = New Font("Calibri", 14F)
        lblDMI.ForeColor = SystemColors.ControlLight
        lblDMI.Location = New Point(110, 122)
        lblDMI.Name = "lblDMI"
        lblDMI.Size = New Size(99, 35)
        lblDMI.TabIndex = 104
        lblDMI.Text = "Neutral"
        ' 
        ' lblMACD
        ' 
        lblMACD.AutoSize = True
        lblMACD.Font = New Font("Calibri", 14F)
        lblMACD.ForeColor = SystemColors.ControlLight
        lblMACD.Location = New Point(110, 157)
        lblMACD.Name = "lblMACD"
        lblMACD.Size = New Size(99, 35)
        lblMACD.TabIndex = 105
        lblMACD.Text = "Neutral"
        ' 
        ' lblRSI
        ' 
        lblRSI.AutoSize = True
        lblRSI.Font = New Font("Calibri", 14F)
        lblRSI.ForeColor = SystemColors.ControlLight
        lblRSI.Location = New Point(110, 192)
        lblRSI.Name = "lblRSI"
        lblRSI.Size = New Size(99, 35)
        lblRSI.TabIndex = 106
        lblRSI.Text = "Neutral"
        ' 
        ' lblStoch
        ' 
        lblStoch.AutoSize = True
        lblStoch.Font = New Font("Calibri", 14F)
        lblStoch.ForeColor = SystemColors.ControlLight
        lblStoch.Location = New Point(110, 227)
        lblStoch.Name = "lblStoch"
        lblStoch.Size = New Size(99, 35)
        lblStoch.TabIndex = 107
        lblStoch.Text = "Neutral"
        ' 
        ' lblDMITitle
        ' 
        lblDMITitle.AutoSize = True
        lblDMITitle.Font = New Font("Calibri", 14F)
        lblDMITitle.ForeColor = SystemColors.ControlLight
        lblDMITitle.Location = New Point(5, 122)
        lblDMITitle.Name = "lblDMITitle"
        lblDMITitle.Size = New Size(63, 35)
        lblDMITitle.TabIndex = 118
        lblDMITitle.Text = "DMI"
        ' 
        ' lblMACDTitle
        ' 
        lblMACDTitle.AutoSize = True
        lblMACDTitle.Font = New Font("Calibri", 14F)
        lblMACDTitle.ForeColor = SystemColors.ControlLight
        lblMACDTitle.Location = New Point(5, 157)
        lblMACDTitle.Name = "lblMACDTitle"
        lblMACDTitle.Size = New Size(87, 35)
        lblMACDTitle.TabIndex = 119
        lblMACDTitle.Text = "MACD"
        ' 
        ' lblRSITitle
        ' 
        lblRSITitle.AutoSize = True
        lblRSITitle.Font = New Font("Calibri", 14F)
        lblRSITitle.ForeColor = SystemColors.ControlLight
        lblRSITitle.Location = New Point(5, 192)
        lblRSITitle.Name = "lblRSITitle"
        lblRSITitle.Size = New Size(50, 35)
        lblRSITitle.TabIndex = 120
        lblRSITitle.Text = "RSI"
        ' 
        ' lblStochTitle
        ' 
        lblStochTitle.AutoSize = True
        lblStochTitle.Font = New Font("Calibri", 14F)
        lblStochTitle.ForeColor = SystemColors.ControlLight
        lblStochTitle.Location = New Point(5, 227)
        lblStochTitle.Name = "lblStochTitle"
        lblStochTitle.Size = New Size(79, 35)
        lblStochTitle.TabIndex = 121
        lblStochTitle.Text = "Stoch"
        ' 
        ' txtIndLogs
        ' 
        txtIndLogs.AutoWordSelection = True
        txtIndLogs.BackColor = SystemColors.ActiveCaptionText
        txtIndLogs.BorderStyle = BorderStyle.FixedSingle
        txtIndLogs.Cursor = Cursors.IBeam
        txtIndLogs.Font = New Font("Calibri", 9F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        txtIndLogs.ForeColor = SystemColors.InactiveBorder
        txtIndLogs.Location = New Point(13, 308)
        txtIndLogs.Name = "txtIndLogs"
        txtIndLogs.Size = New Size(459, 197)
        txtIndLogs.TabIndex = 122
        txtIndLogs.Text = ""
        ' 
        ' redHeartBeat
        ' 
        redHeartBeat.Appearance = Appearance.Button
        redHeartBeat.BackColor = Color.Black
        redHeartBeat.FlatAppearance.BorderSize = 0
        redHeartBeat.FlatStyle = FlatStyle.Flat
        redHeartBeat.ForeColor = Color.Black
        redHeartBeat.Location = New Point(12, 17)
        redHeartBeat.Name = "redHeartBeat"
        redHeartBeat.Size = New Size(26, 26)
        redHeartBeat.TabIndex = 123
        redHeartBeat.UseVisualStyleBackColor = False
        ' 
        ' lblEMATitle
        ' 
        lblEMATitle.AutoSize = True
        lblEMATitle.Font = New Font("Calibri", 14F)
        lblEMATitle.ForeColor = SystemColors.ControlLight
        lblEMATitle.Location = New Point(5, 87)
        lblEMATitle.Name = "lblEMATitle"
        lblEMATitle.Size = New Size(69, 35)
        lblEMATitle.TabIndex = 125
        lblEMATitle.Text = "EMA"
        ' 
        ' lblEMA
        ' 
        lblEMA.AutoSize = True
        lblEMA.Font = New Font("Calibri", 14F)
        lblEMA.ForeColor = SystemColors.ControlLight
        lblEMA.Location = New Point(110, 87)
        lblEMA.Name = "lblEMA"
        lblEMA.Size = New Size(99, 35)
        lblEMA.TabIndex = 124
        lblEMA.Text = "Neutral"
        ' 
        ' lblVWAPTitle
        ' 
        lblVWAPTitle.AutoSize = True
        lblVWAPTitle.Font = New Font("Calibri", 14F)
        lblVWAPTitle.ForeColor = SystemColors.ControlLight
        lblVWAPTitle.Location = New Point(5, 52)
        lblVWAPTitle.Name = "lblVWAPTitle"
        lblVWAPTitle.Size = New Size(85, 35)
        lblVWAPTitle.TabIndex = 127
        lblVWAPTitle.Text = "VWAP"
        ' 
        ' lblVWAP
        ' 
        lblVWAP.AutoSize = True
        lblVWAP.Font = New Font("Calibri", 14F)
        lblVWAP.ForeColor = SystemColors.ControlLight
        lblVWAP.Location = New Point(110, 52)
        lblVWAP.Name = "lblVWAP"
        lblVWAP.Size = New Size(99, 35)
        lblVWAP.TabIndex = 126
        lblVWAP.Text = "Neutral"
        ' 
        ' lblScore
        ' 
        lblScore.AutoSize = True
        lblScore.Font = New Font("Calibri", 20F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        lblScore.ForeColor = Color.DodgerBlue
        lblScore.Location = New Point(110, 5)
        lblScore.Name = "lblScore"
        lblScore.Size = New Size(0, 49)
        lblScore.TabIndex = 129
        ' 
        ' lblATRTitle
        ' 
        lblATRTitle.AutoSize = True
        lblATRTitle.Font = New Font("Calibri", 14F)
        lblATRTitle.ForeColor = SystemColors.ControlLight
        lblATRTitle.Location = New Point(5, 262)
        lblATRTitle.Name = "lblATRTitle"
        lblATRTitle.Size = New Size(58, 35)
        lblATRTitle.TabIndex = 130
        lblATRTitle.Text = "ATR"
        ' 
        ' lblATR
        ' 
        lblATR.AutoSize = True
        lblATR.Font = New Font("Calibri", 14F)
        lblATR.ForeColor = SystemColors.ControlLight
        lblATR.Location = New Point(110, 262)
        lblATR.Name = "lblATR"
        lblATR.Size = New Size(99, 35)
        lblATR.TabIndex = 131
        lblATR.Text = "Neutral"
        ' 
        ' btnBacktest
        ' 
        btnBacktest.BackColor = Color.DodgerBlue
        btnBacktest.Cursor = Cursors.Hand
        btnBacktest.Font = New Font("Calibri", 10F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        btnBacktest.Location = New Point(289, 719)
        btnBacktest.Name = "btnBacktest"
        btnBacktest.Size = New Size(104, 74)
        btnBacktest.TabIndex = 132
        btnBacktest.Text = "TEST!"
        btnBacktest.UseVisualStyleBackColor = False
        ' 
        ' lblBacktestTitle
        ' 
        lblBacktestTitle.AutoSize = True
        lblBacktestTitle.Font = New Font("Calibri", 18F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        lblBacktestTitle.ForeColor = SystemColors.ControlLight
        lblBacktestTitle.Location = New Point(13, 518)
        lblBacktestTitle.Name = "lblBacktestTitle"
        lblBacktestTitle.Size = New Size(307, 44)
        lblBacktestTitle.TabIndex = 133
        lblBacktestTitle.Text = "Backtesting Section"
        ' 
        ' lblTestATR
        ' 
        lblTestATR.AutoSize = True
        lblTestATR.Font = New Font("Calibri", 14F)
        lblTestATR.ForeColor = SystemColors.ControlLight
        lblTestATR.Location = New Point(16, 572)
        lblTestATR.Name = "lblTestATR"
        lblTestATR.Size = New Size(58, 35)
        lblTestATR.TabIndex = 134
        lblTestATR.Text = "ATR"
        ' 
        ' lblTestLScore
        ' 
        lblTestLScore.AutoSize = True
        lblTestLScore.Font = New Font("Calibri", 14F)
        lblTestLScore.ForeColor = SystemColors.ControlLight
        lblTestLScore.Location = New Point(241, 572)
        lblTestLScore.Name = "lblTestLScore"
        lblTestLScore.Size = New Size(104, 35)
        lblTestLScore.TabIndex = 135
        lblTestLScore.Text = "L. Score"
        ' 
        ' lblTestATRTP
        ' 
        lblTestATRTP.AutoSize = True
        lblTestATRTP.Font = New Font("Calibri", 14F)
        lblTestATRTP.ForeColor = SystemColors.ControlLight
        lblTestATRTP.Location = New Point(16, 620)
        lblTestATRTP.Name = "lblTestATRTP"
        lblTestATRTP.Size = New Size(43, 35)
        lblTestATRTP.TabIndex = 136
        lblTestATRTP.Text = "TP"
        ' 
        ' lblTestATRSL
        ' 
        lblTestATRSL.AutoSize = True
        lblTestATRSL.Font = New Font("Calibri", 14F)
        lblTestATRSL.ForeColor = SystemColors.ControlLight
        lblTestATRSL.Location = New Point(16, 671)
        lblTestATRSL.Name = "lblTestATRSL"
        lblTestATRSL.Size = New Size(40, 35)
        lblTestATRSL.TabIndex = 137
        lblTestATRSL.Text = "SL"
        ' 
        ' lblTestSScore
        ' 
        lblTestSScore.AutoSize = True
        lblTestSScore.Font = New Font("Calibri", 14F)
        lblTestSScore.ForeColor = SystemColors.ControlLight
        lblTestSScore.Location = New Point(241, 620)
        lblTestSScore.Name = "lblTestSScore"
        lblTestSScore.Size = New Size(105, 35)
        lblTestSScore.TabIndex = 138
        lblTestSScore.Text = "S. Score"
        ' 
        ' Label1
        ' 
        Label1.AutoSize = True
        Label1.Font = New Font("Calibri", 14F)
        Label1.ForeColor = SystemColors.ControlLight
        Label1.Location = New Point(241, 671)
        Label1.Name = "Label1"
        Label1.Size = New Size(95, 35)
        Label1.TabIndex = 139
        Label1.Text = "T. Time"
        ' 
        ' txtATR
        ' 
        txtATR.BackColor = Color.Black
        txtATR.BorderStyle = BorderStyle.FixedSingle
        txtATR.Font = New Font("Calibri", 14F)
        txtATR.ForeColor = Color.White
        txtATR.Location = New Point(91, 570)
        txtATR.Name = "txtATR"
        txtATR.Size = New Size(64, 42)
        txtATR.TabIndex = 142
        txtATR.Text = "14"
        txtATR.TextAlign = HorizontalAlignment.Center
        ' 
        ' txtTP
        ' 
        txtTP.BackColor = Color.Black
        txtTP.BorderStyle = BorderStyle.FixedSingle
        txtTP.Font = New Font("Calibri", 14F)
        txtTP.ForeColor = Color.White
        txtTP.Location = New Point(91, 618)
        txtTP.Name = "txtTP"
        txtTP.Size = New Size(64, 42)
        txtTP.TabIndex = 143
        txtTP.Text = "2.0"
        txtTP.TextAlign = HorizontalAlignment.Center
        ' 
        ' txtSL
        ' 
        txtSL.BackColor = Color.Black
        txtSL.BorderStyle = BorderStyle.FixedSingle
        txtSL.Font = New Font("Calibri", 14F)
        txtSL.ForeColor = Color.White
        txtSL.Location = New Point(91, 666)
        txtSL.Name = "txtSL"
        txtSL.Size = New Size(64, 42)
        txtSL.TabIndex = 144
        txtSL.Text = "1.5"
        txtSL.TextAlign = HorizontalAlignment.Center
        ' 
        ' txtTestTime
        ' 
        txtTestTime.BackColor = Color.Black
        txtTestTime.BorderStyle = BorderStyle.FixedSingle
        txtTestTime.Font = New Font("Calibri", 14F)
        txtTestTime.ForeColor = Color.White
        txtTestTime.Location = New Point(351, 671)
        txtTestTime.Name = "txtTestTime"
        txtTestTime.Size = New Size(64, 42)
        txtTestTime.TabIndex = 145
        txtTestTime.Text = "1024"
        txtTestTime.TextAlign = HorizontalAlignment.Center
        ' 
        ' Label2
        ' 
        Label2.AutoSize = True
        Label2.Font = New Font("Calibri", 14F)
        Label2.ForeColor = SystemColors.ControlLight
        Label2.Location = New Point(413, 673)
        Label2.Name = "Label2"
        Label2.Size = New Size(69, 35)
        Label2.TabIndex = 146
        Label2.Text = "mins"
        ' 
        ' Label3
        ' 
        Label3.AutoSize = True
        Label3.Font = New Font("Calibri", 14F)
        Label3.ForeColor = SystemColors.ControlLight
        Label3.Location = New Point(157, 620)
        Label3.Name = "Label3"
        Label3.Size = New Size(76, 35)
        Label3.TabIndex = 147
        Label3.Text = "x ATR"
        ' 
        ' Label4
        ' 
        Label4.AutoSize = True
        Label4.Font = New Font("Calibri", 14F)
        Label4.ForeColor = SystemColors.ControlLight
        Label4.Location = New Point(157, 668)
        Label4.Name = "Label4"
        Label4.Size = New Size(76, 35)
        Label4.TabIndex = 148
        Label4.Text = "x ATR"
        ' 
        ' txtLScore
        ' 
        txtLScore.BackColor = Color.Black
        txtLScore.BorderStyle = BorderStyle.FixedSingle
        txtLScore.Font = New Font("Calibri", 14F)
        txtLScore.ForeColor = Color.White
        txtLScore.Location = New Point(351, 570)
        txtLScore.Name = "txtLScore"
        txtLScore.Size = New Size(64, 42)
        txtLScore.TabIndex = 149
        txtLScore.Text = "10"
        txtLScore.TextAlign = HorizontalAlignment.Center
        ' 
        ' txtSScore
        ' 
        txtSScore.BackColor = Color.Black
        txtSScore.BorderStyle = BorderStyle.FixedSingle
        txtSScore.Font = New Font("Calibri", 14F)
        txtSScore.ForeColor = Color.White
        txtSScore.Location = New Point(351, 620)
        txtSScore.Name = "txtSScore"
        txtSScore.Size = New Size(64, 42)
        txtSScore.TabIndex = 150
        txtSScore.Text = "-10"
        txtSScore.TextAlign = HorizontalAlignment.Center
        ' 
        ' Label5
        ' 
        Label5.AutoSize = True
        Label5.Font = New Font("Calibri", 10F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        Label5.ForeColor = SystemColors.ControlLight
        Label5.Location = New Point(422, 580)
        Label5.Name = "Label5"
        Label5.Size = New Size(55, 24)
        Label5.TabIndex = 151
        Label5.Text = "1 : 21"
        ' 
        ' Label6
        ' 
        Label6.AutoSize = True
        Label6.Font = New Font("Calibri", 10F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        Label6.ForeColor = SystemColors.ControlLight
        Label6.Location = New Point(416, 628)
        Label6.Name = "Label6"
        Label6.Size = New Size(67, 24)
        Label6.TabIndex = 152
        Label6.Text = "-1 : -21"
        ' 
        ' btnATR
        ' 
        btnATR.BackColor = Color.ForestGreen
        btnATR.Cursor = Cursors.Hand
        btnATR.Font = New Font("Calibri", 10F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        btnATR.Location = New Point(399, 719)
        btnATR.Name = "btnATR"
        btnATR.Size = New Size(73, 74)
        btnATR.TabIndex = 153
        btnATR.Text = "Paste ATR"
        btnATR.UseVisualStyleBackColor = False
        ' 
        ' txtATRLimit
        ' 
        txtATRLimit.BackColor = Color.Black
        txtATRLimit.BorderStyle = BorderStyle.FixedSingle
        txtATRLimit.Font = New Font("Calibri", 14F)
        txtATRLimit.ForeColor = Color.White
        txtATRLimit.Location = New Point(91, 714)
        txtATRLimit.Name = "txtATRLimit"
        txtATRLimit.Size = New Size(64, 42)
        txtATRLimit.TabIndex = 154
        txtATRLimit.Text = "70"
        txtATRLimit.TextAlign = HorizontalAlignment.Center
        ' 
        ' btnFormLoadInfo
        ' 
        btnFormLoadInfo.BackColor = Color.DodgerBlue
        btnFormLoadInfo.Cursor = Cursors.Hand
        btnFormLoadInfo.Font = New Font("Calibri", 10F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        btnFormLoadInfo.Location = New Point(179, 719)
        btnFormLoadInfo.Name = "btnFormLoadInfo"
        btnFormLoadInfo.Size = New Size(104, 74)
        btnFormLoadInfo.TabIndex = 155
        btnFormLoadInfo.Text = "Check"
        btnFormLoadInfo.UseVisualStyleBackColor = False
        ' 
        ' FrmIndicators
        ' 
        AutoScaleDimensions = New SizeF(10F, 25F)
        AutoScaleMode = AutoScaleMode.Font
        BackColor = SystemColors.ActiveCaptionText
        ClientSize = New Size(484, 805)
        Controls.Add(btnFormLoadInfo)
        Controls.Add(txtATRLimit)
        Controls.Add(btnATR)
        Controls.Add(Label6)
        Controls.Add(Label5)
        Controls.Add(txtSScore)
        Controls.Add(txtLScore)
        Controls.Add(Label4)
        Controls.Add(Label3)
        Controls.Add(txtTestTime)
        Controls.Add(Label2)
        Controls.Add(txtSL)
        Controls.Add(txtTP)
        Controls.Add(txtATR)
        Controls.Add(Label1)
        Controls.Add(lblTestSScore)
        Controls.Add(lblTestATRSL)
        Controls.Add(lblTestATRTP)
        Controls.Add(lblTestLScore)
        Controls.Add(lblTestATR)
        Controls.Add(lblBacktestTitle)
        Controls.Add(btnBacktest)
        Controls.Add(lblATR)
        Controls.Add(lblATRTitle)
        Controls.Add(lblScore)
        Controls.Add(lblVWAPTitle)
        Controls.Add(lblVWAP)
        Controls.Add(lblEMATitle)
        Controls.Add(lblEMA)
        Controls.Add(redHeartBeat)
        Controls.Add(txtIndLogs)
        Controls.Add(lblStochTitle)
        Controls.Add(lblRSITitle)
        Controls.Add(lblMACDTitle)
        Controls.Add(lblDMITitle)
        Controls.Add(lblStoch)
        Controls.Add(lblRSI)
        Controls.Add(lblMACD)
        Controls.Add(lblDMI)
        Name = "FrmIndicators"
        Opacity = 0.75R
        Text = "Indicators"
        TopMost = True
        ResumeLayout(False)
        PerformLayout()
    End Sub

    Friend WithEvents lblDMI As Label
    Friend WithEvents lblMACD As Label
    Friend WithEvents lblRSI As Label
    Friend WithEvents lblStoch As Label
    Friend WithEvents lblDMITitle As Label
    Friend WithEvents lblMACDTitle As Label
    Friend WithEvents lblRSITitle As Label
    Friend WithEvents lblStochTitle As Label
    Friend WithEvents txtIndLogs As RichTextBox
    Friend WithEvents redHeartBeat As RadioButton
    Friend WithEvents lblEMATitle As Label
    Friend WithEvents lblEMA As Label
    Friend WithEvents lblVWAPTitle As Label
    Friend WithEvents lblVWAP As Label
    Friend WithEvents lblScore As Label
    Friend WithEvents lblATRTitle As Label
    Friend WithEvents lblATR As Label
    Friend WithEvents btnBacktest As Button
    Friend WithEvents lblBacktestTitle As Label
    Friend WithEvents lblTestATR As Label
    Friend WithEvents lblTestLScore As Label
    Friend WithEvents lblTestATRTP As Label
    Friend WithEvents lblTestATRSL As Label
    Friend WithEvents lblTestSScore As Label
    Friend WithEvents Label1 As Label
    Friend WithEvents txtATR As TextBox
    Friend WithEvents txtTP As TextBox
    Friend WithEvents txtSL As TextBox
    Friend WithEvents txtTestTime As TextBox
    Friend WithEvents Label2 As Label
    Friend WithEvents Label3 As Label
    Friend WithEvents Label4 As Label
    Friend WithEvents txtLScore As TextBox
    Friend WithEvents txtSScore As TextBox
    Friend WithEvents Label5 As Label
    Friend WithEvents Label6 As Label
    Friend WithEvents btnATR As Button
    Friend WithEvents txtATRLimit As TextBox
    Friend WithEvents btnFormLoadInfo As Button
End Class
