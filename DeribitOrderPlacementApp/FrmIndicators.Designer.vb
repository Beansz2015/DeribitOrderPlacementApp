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
        btnClose = New Button()
        btnStart = New Button()
        lblDMITitle = New Label()
        lblMACDTitle = New Label()
        lblRSITitle = New Label()
        lblStochTitle = New Label()
        SuspendLayout()
        ' 
        ' lblDMI
        ' 
        lblDMI.AutoSize = True
        lblDMI.Font = New Font("Calibri", 14F)
        lblDMI.ForeColor = SystemColors.ControlLight
        lblDMI.Location = New Point(107, 52)
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
        lblMACD.Location = New Point(107, 118)
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
        lblRSI.Location = New Point(107, 193)
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
        lblStoch.Location = New Point(107, 273)
        lblStoch.Name = "lblStoch"
        lblStoch.Size = New Size(99, 35)
        lblStoch.TabIndex = 107
        lblStoch.Text = "Neutral"
        ' 
        ' btnClose
        ' 
        btnClose.BackColor = Color.Crimson
        btnClose.Cursor = Cursors.Hand
        btnClose.Font = New Font("Calibri", 10F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        btnClose.Location = New Point(117, 343)
        btnClose.Name = "btnClose"
        btnClose.Size = New Size(80, 50)
        btnClose.TabIndex = 116
        btnClose.Text = "Close"
        btnClose.UseVisualStyleBackColor = False
        ' 
        ' btnStart
        ' 
        btnStart.BackColor = Color.DeepSkyBlue
        btnStart.Cursor = Cursors.Hand
        btnStart.Font = New Font("Calibri", 10F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        btnStart.Location = New Point(9, 343)
        btnStart.Name = "btnStart"
        btnStart.Size = New Size(80, 50)
        btnStart.TabIndex = 117
        btnStart.Text = "Start"
        btnStart.UseVisualStyleBackColor = False
        ' 
        ' lblDMITitle
        ' 
        lblDMITitle.AutoSize = True
        lblDMITitle.Font = New Font("Calibri", 14F)
        lblDMITitle.ForeColor = SystemColors.ControlLight
        lblDMITitle.Location = New Point(2, 52)
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
        lblMACDTitle.Location = New Point(2, 118)
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
        lblRSITitle.Location = New Point(2, 193)
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
        lblStochTitle.Location = New Point(2, 273)
        lblStochTitle.Name = "lblStochTitle"
        lblStochTitle.Size = New Size(79, 35)
        lblStochTitle.TabIndex = 121
        lblStochTitle.Text = "Stoch"
        ' 
        ' FrmIndicators
        ' 
        AutoScaleDimensions = New SizeF(10F, 25F)
        AutoScaleMode = AutoScaleMode.Font
        BackColor = SystemColors.ActiveCaptionText
        ClientSize = New Size(210, 426)
        Controls.Add(lblStochTitle)
        Controls.Add(lblRSITitle)
        Controls.Add(lblMACDTitle)
        Controls.Add(lblDMITitle)
        Controls.Add(btnStart)
        Controls.Add(btnClose)
        Controls.Add(lblStoch)
        Controls.Add(lblRSI)
        Controls.Add(lblMACD)
        Controls.Add(lblDMI)
        Name = "FrmIndicators"
        Text = "Indicators"
        ResumeLayout(False)
        PerformLayout()
    End Sub

    Friend WithEvents lblDMI As Label
    Friend WithEvents lblMACD As Label
    Friend WithEvents lblRSI As Label
    Friend WithEvents lblStoch As Label
    Friend WithEvents btnClose As Button
    Friend WithEvents btnStart As Button
    Friend WithEvents lblDMITitle As Label
    Friend WithEvents lblMACDTitle As Label
    Friend WithEvents lblRSITitle As Label
    Friend WithEvents lblStochTitle As Label
End Class
