Public Class AutoTradeSettings

    ' Retirement: the host is frmMainPageV2 now (the retired indicator form used to own and position
    ' this form). Typed, because the Tooling section pushes values into the host.
    Private ReadOnly _host As frmMainPageV2

    ' ============ SIGNAL BRIDGE panel (docs/spec-autotrade-tiein.md section 4) ============
    ' frmMainPageV2 owns this form and the bridge, and assigns Bridge directly after constructing it
    ' (this form only displays/drives the bridge). Mode, ARM and Started deliberately NEVER persist -
    ' they reset to Off/unchecked/stopped at every app start. Do NOT add them to the item-A
    ' ergonomics persistence pass later (contract section 6: restart = disarmed).
    Private _bridge As SignalBridge
    Private _suppressBridgeUi As Boolean = False ' guards programmatic combo/checkbox writes in RefreshBridgePanel

    ' Live ATR readout (Tooling): the only place the effective ATR is visible - which ATR is in force
    ' (engine payload, or the Flat ATR switched or as fallback) and the cap it gives. UI-thread
    ' timer, and only while this window is actually open.
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
    Private _circuitBreakerUsd As Decimal = 10D      ' <= 0 disables; default 10 + persisted (R1, 2026-07-24)
    Private _windowStart As String = ""              ' blank = unrestricted
    Private _windowEnd As String = ""
    Private _tiersCsv As String = "HIGH,MEDIUM"
    ' N2 (docs/spec-risk-sized-bridge-trades.md): ships DISABLED. Nothing writes this until §2's
    ' checkbox lands; the bridge reads it through the property below.
    Private _riskSizeBridgeTrades As Boolean = False

    ' Session policy (docs/spec-session-policy-gate.md section 3). Unlike the mirrors above this is
    ' ONE IMMUTABLE SNAPSHOT, reference-SWAPPED on commit and never mutated in place, so the bridge's
    ' FSW/timer threads can read it with a single reference read - the same discipline as the plain-
    ' field mirrors, extended to a compound value. Defaults = disabled = today's behaviour exactly;
    ' the real config arrives via SeedSessionPolicyFromHost/CommitGateConfig.
    Private _sessionPolicy As SessionPolicyConfig = SessionPolicyConfig.Defaults()
    ' The offending line from the last parse attempt, or Nothing. Surfaced by ShowGateConfigWarnings.
    Private _sessionPolicyProblem As String = Nothing
    ' The gate-config warning currently in force, or Nothing when every box parses. Read only by
    ' ApplyBridgeStatusLine (the single lblBridgeStatus writer); UI thread only.
    Private _gateConfigWarning As String = Nothing

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
    ''' <summary>N2 (docs/spec-risk-sized-bridge-trades.md §2): whether the bridge sizes each trade
    ''' by risk over the engine's own stop distance instead of the Amount box. Plain Boolean on the
    ''' TiersCsv pattern - a single reference-free read, safe from the bridge's FSW/timer threads.
    ''' The checkbox that drives it arrives with §2's commit; until then this is False and the
    ''' feature is unreachable, which is the shipped-DISABLED default either way.</summary>
    Friend ReadOnly Property RiskSizeBridgeTrades As Boolean
        Get
            Return _riskSizeBridgeTrades
        End Get
    End Property
    ''' <summary>The committed session policy. Never Nothing; safe to read from any thread (the
    ''' instance is immutable - see the field comment).</summary>
    Friend ReadOnly Property SessionPolicy As SessionPolicyConfig
        Get
            Return _sessionPolicy
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
        ' Risk-sizing UI spec §2 - the initialisation-ordering trap: CommitToolingConfig below now
        ' also pushes risk/max-size to the host, so the boxes MUST be seeded from the host (= the
        ' values just loaded from orderapp-settings.json) FIRST - otherwise this first commit would
        ' overwrite the owner's tuned numbers with the Designer defaults (25/500) on every start.
        ' Depends on the host's Load ordering: userSettings is loaded before this form is constructed.
        SeedRiskSizingFromHost()
        ' Session policy spec §5.3: the SAME trap, same position - the box and the checkbox must
        ' hold the loaded file values before the first CommitGateConfig, or that commit would write
        ' a blank box (= all defaults) and an unticked checkbox straight over the owner's policy.
        SeedSessionPolicyFromHost()
        ' R1 breaker persistence: the trap's FOURTH application - seed before the first commit.
        SeedCircuitBreakerFromHost()
        ' EV chase budget §4: the trap AGAIN, the fifth application. Without this seed the first
        ' commit below would write the Designer's "0" over a persisted, ENABLED EV floor - i.e.
        ' silently disable the guard the owner turned on. Same position, same reason as the four
        ' above. (This comment used to add "N2's is still queued, so in the code today it is the
        ' fifth" - N2 has landed as the sixth, immediately below.)
        SeedMinNetMoveFromHost()
        ' N2 §2: the trap's SIXTH application. Without this seed the first CommitGateConfig would
        ' write the Designer's UNTICKED box over a persisted, ENABLED risk-sizing knob - silently
        ' reverting the owner's bridge trades to Amount-box sizing on every start, which is exactly
        ' the class of silent size change this whole item exists to make deliberate.
        SeedRiskSizeBridgeFromHost()
        ' Indicator-form retirement §2.5: the trap's SEVENTH application. Without this seed the first
        ' CommitToolingConfig would write the Designer's 70 and UNTICKED box over a persisted Flat ATR
        ' and a persisted "Use flat ATR" - silently putting the guard back on the engine ATR.
        SeedFlatAtrFromHost()
        CommitGateConfig()
        CommitToolingConfig()
        For Each tb As TextBox In {txtCooloff, txtCircuitBreaker, txtStartTime, txtEndTime,
                                   txtBridgeTiers, txtAtrFallback,
                                   txtRiskPerTrade, txtMaxSize, txtMinNetMove}
            AddHandler tb.Enter, AddressOf SelectAllOnEnter
            AddHandler tb.Click, AddressOf SelectAllOnEnter
            AddHandler tb.Leave, AddressOf CommitOnLeave
            AddHandler tb.KeyDown, AddressOf CommitOnEnterKey
            ' UI-test-harness (spec section 2): WinForms exposes no UIA Name for bare TextBoxes,
            ' so the set-textbox script matches on AccessibleName = the designer control name.
            ' Inert metadata (UIA only); deliberately NOT gated by harness.json.
            tb.AccessibleName = tb.Name
        Next

        ' txtSessionPolicy is wired INDIVIDUALLY, not through the loop above (spec §5.2 allows this
        ' and asks for it to be reported). It is the only MULTILINE box on the form, and two of the
        ' loop's four handlers are actively wrong for it:
        '   * CommitOnEnterKey would swallow Enter, which must insert a newline here;
        '   * SelectAllOnEnter on Click would re-select all three lines on every click, so the next
        '     keystroke would replace the whole policy - you could never edit one line.
        ' It keeps commit-on-blur (the standing model) and the harness AccessibleName.
        AddHandler txtSessionPolicy.Leave, AddressOf CommitOnLeave
        txtSessionPolicy.AccessibleName = txtSessionPolicy.Name

        ' The checkbox commits like any other gate edit. Wired here rather than via Handles so the
        ' seed above cannot fire it before the form is initialised.
        AddHandler chkSessionPolicyOn.CheckedChanged, AddressOf OnSessionPolicyToggled
        chkSessionPolicyOn.AccessibleName = chkSessionPolicyOn.Name

        ' N2 §2: same wiring, same reason - AddHandler here rather than Handles, so the seed above
        ' cannot fire a commit before the form is initialised.
        AddHandler chkRiskSizeBridge.CheckedChanged, AddressOf OnRiskSizeBridgeToggled
        chkRiskSizeBridge.AccessibleName = chkRiskSizeBridge.Name

        ' R3 "Use flat ATR": same wiring, same reason. It commits on change.
        AddHandler chkUseFlatAtr.CheckedChanged, AddressOf OnUseFlatAtrToggled
        chkUseFlatAtr.AccessibleName = chkUseFlatAtr.Name

        cboBridgeMode.AccessibleName = "cboBridgeMode"
    End Sub

    ' Enablement is a commit like any other - it changes what is in force immediately.
    Private Sub OnSessionPolicyToggled(sender As Object, e As EventArgs)
        CommitGateConfig()
    End Sub

    ' N2 §2: likewise - ticking the box changes how the NEXT payload is sized, immediately.
    Private Sub OnRiskSizeBridgeToggled(sender As Object, e As EventArgs)
        CommitGateConfig()
    End Sub

    ' R3: ticking the box changes the slippage guard's ATR immediately.
    Private Sub OnUseFlatAtrToggled(sender As Object, e As EventArgs)
        CommitToolingConfig()
        RefreshAtrReadout()
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
        lblAtrNow.Text = $"ATR now: {eff.Atr:F2} ({eff.Source})  ->  slip limit ${limit:F2}"
        ' Green when the engine payload drives it, cyan when the owner switched to the Flat ATR,
        ' orange when the Flat ATR stands in because no fresh engine ATR exists.
        lblAtrNow.ForeColor = If(eff.Source = "signal payload", Color.LimeGreen,
                                 If(eff.Source = "flat ATR (switched)", Color.Cyan, Color.Orange))
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
        ReseedSenderFromHost(sender)
    End Sub

    Private Sub CommitOnEnterKey(sender As Object, e As KeyEventArgs)
        If e.KeyCode <> Keys.Enter Then Return
        e.SuppressKeyPress = True   ' no ding
        CommitGateConfig()
        CommitToolingConfig()
        ReseedSenderFromHost(sender)
        RefreshBridgePanel()
    End Sub

    ' Uncapped Max Size §2.2b (docs/spec-max-size-uncapped.md): after a commit, re-seed the box that
    ' RAISED it from the host, so the control can never go on displaying something that is not in
    ' force. Blank or garbage visibly snaps back to the last good value; a typed 0 visibly stays 0.
    '
    ' This fixes a divergence that already existed. SeedRiskSizingFromHost runs from exactly ONE
    ' place (InitialiseSettings, :143) and this form is constructed once and thereafter reused via
    ' Show/Hide, so before this a cleared box kept showing blank for the whole app session while the
    ' engine went on sizing off the last good value. Only an app relaunch resynced it. After the
    ' commit change above that divergence gets sharper, because 0 and blank now LOOK similar and
    ' MEAN opposite things.
    '
    ' 🚨 ONLY THE SENDER, and this is the one way to turn a display fix into data loss. A blanket
    ' re-seed would overwrite a DIFFERENT box the owner is part-way through typing into - that box
    ' has not committed yet, so its keystrokes would be discarded with nothing to show for it.
    '
    ' Safe because both callers are commit-completion points, not TextChanged: on Leave the box has
    ' already lost focus, and on the Enter key the owner has just asked for the commit explicitly.
    ' (§2.2b names only Leave; CommitOnEnterKey is a second commit path on the same boxes, and
    ' leaving it out would have left exactly the divergence this requirement exists to close.)
    '
    ' SCOPE, extended 2026-10-06 (owner ruling; ROADMAP-2026-08.md §5 hygiene row "A settings box can
    ' display something that is NOT in force"): the Max Size work covered the SetRiskSizingValues
    ' pair only. The other eight CommitOnLeave boxes had the same divergence - the worst case was
    ' Tiers, where a cleared box kept HIGH,MEDIUM in force with no warning at all. They now snap
    ' back too. Each box re-renders its value IN FORCE from the same place the commit wrote it:
    ' the four gate boxes from this form's own mirrors (CommitGateConfig owns them), the rest from
    ' the host. Where an initial-seed routine exists it is reused, so seed and re-seed render a value
    ' identically. txtSessionPolicy also reaches here and is correctly untouched: it is a multi-line
    ' edit that may be mid-edit and temporarily invalid (see CommitGateConfig's policy block), and
    ' snapping it back would throw away a half-written rule.
    '
    ' The orange "Ignored (keeping last good)" warning is raised by the commit BEFORE this runs and
    ' is left standing: it records that the entry was ignored, and the next commit clears it - the
    ' same behaviour the Max Size box has had since the Max Size work.
    Private Sub ReseedSenderFromHost(sender As Object)
        Dim tb = TryCast(sender, TextBox)
        If tb Is Nothing Then Return

        ' Gate boxes: form-owned mirrors, no host needed. Start/End render the committed (trimmed)
        ' string, so a blank in force renders blank; Tiers renders the committed upper-cased list.
        If tb Is txtCooloff Then
            txtCooloff.Text = _cooloffMin.ToString()
            Return
        ElseIf tb Is txtStartTime Then
            txtStartTime.Text = _windowStart
            Return
        ElseIf tb Is txtEndTime Then
            txtEndTime.Text = _windowEnd
            Return
        ElseIf tb Is txtBridgeTiers Then
            txtBridgeTiers.Text = _tiersCsv
            Return
        End If

        If _host Is Nothing Then Return
        ' Same formatting as SeedRiskSizingFromHost on purpose: the initial seed and the re-seed must
        ' render an identical value identically, or reopening the form would look like a change.
        ' .ToString() round-trips a 0 back as "0", never as blank (§2.4).
        If tb Is txtMaxSize Then
            txtMaxSize.Text = _host.MaxSizeUsd.ToString()
        ElseIf tb Is txtRiskPerTrade Then
            txtRiskPerTrade.Text = _host.RiskPerTradeUsd.ToString()
        ElseIf tb Is txtCircuitBreaker Then
            SeedCircuitBreakerFromHost()
        ElseIf tb Is txtMinNetMove Then
            SeedMinNetMoveFromHost()
        ElseIf tb Is txtAtrFallback Then
            txtAtrFallback.Text = _host.FlatAtrUsd.ToString()
        End If
    End Sub

    ' Reads the gate boxes into the mirrors. Invalid/blank input leaves the previous value in place
    ' rather than falling back to something permissive - a half-typed window must never read as
    ' "no time restriction". Blank BOTH window boxes for genuinely unrestricted (handled below).
    Private Sub CommitGateConfig()
        Dim d As Decimal
        If Decimal.TryParse(txtCooloff.Text, d) AndAlso d >= 0D Then _cooloffMin = d
        If Decimal.TryParse(txtCircuitBreaker.Text, d) Then
            _circuitBreakerUsd = d
            ' R1 (spec-breaker-persist-atr7-item8.md): the breaker persists via item A's save path.
            ' ANY parsed value is pushed - <= 0 is a deliberate, persistable disable; only a parse
            ' failure keeps last good (this TryParse + the orange warning).
            If _host IsNot Nothing Then _host.SetCircuitBreakerUsd(d)
        End If

        ' Window: blank is a legitimate value (unrestricted), so it commits; garbage does not.
        Dim ws As String = If(txtStartTime.Text, "").Trim()
        Dim we As String = If(txtEndTime.Text, "").Trim()
        Dim ts As TimeSpan
        If ws.Length = 0 OrElse TimeSpan.TryParse(ws, ts) Then _windowStart = ws
        If we.Length = 0 OrElse TimeSpan.TryParse(we, ts) Then _windowEnd = we

        Dim tiers As String = If(txtBridgeTiers.Text, "").Trim()
        If tiers.Length > 0 Then _tiersCsv = tiers.ToUpperInvariant()

        ' Session policy (spec §5.3). Parse -> reference-SWAP the immutable snapshot -> push to the
        ' host so item A's save path persists it. A malformed line keeps the last good RULES but
        ' still honours the checkbox: toggling the policy off must work even while the text is
        ' mid-edit and temporarily invalid.
        Dim policyProblem As String = Nothing
        Dim parsedPolicy As SessionPolicyConfig =
            SessionPolicyConfig.ParseSessionPolicyText(txtSessionPolicy.Text, policyProblem)
        _sessionPolicyProblem = policyProblem
        Dim committed As SessionPolicyConfig =
            If(parsedPolicy, _sessionPolicy).WithEnabled(chkSessionPolicyOn.Checked)
        _sessionPolicy = committed
        If _host IsNot Nothing Then _host.SetSessionPolicy(committed)

        ' N2 §2: the risk-sizing knob. A checkbox cannot be half-typed, so unlike the text boxes
        ' above there is no parse and no last-good fallback - the box IS the value. Pushed to the
        ' host so item A's save path persists it; the bridge live-reads the mirror.
        _riskSizeBridgeTrades = chkRiskSizeBridge.Checked
        If _host IsNot Nothing Then _host.SetRiskSizeBridgeTrades(_riskSizeBridgeTrades)

        ShowGateConfigWarnings()
    End Sub

    ' R1: one-time seed of the breaker box from the host's loaded settings. MUST run before the
    ' first CommitGateConfig so the persisted value wins over the Designer default (10).
    Private Sub SeedCircuitBreakerFromHost()
        If _host Is Nothing Then Return
        txtCircuitBreaker.Text = _host.CircuitBreakerUsd.ToString(Globalization.CultureInfo.InvariantCulture)
    End Sub

    ' §5.3: one-time seed of the policy box + checkbox from the host's loaded settings. MUST run
    ' before the first CommitGateConfig (see InitialiseSettings) so the file's values win over the
    ' Designer's blank box - the same Load-ordering dependency as SeedRiskSizingFromHost.
    Private Sub SeedSessionPolicyFromHost()
        If _host Is Nothing Then Return
        Dim cfg As SessionPolicyConfig = _host.SessionPolicy
        If cfg Is Nothing Then cfg = SessionPolicyConfig.Defaults()
        _sessionPolicy = cfg
        txtSessionPolicy.Text = SessionPolicyConfig.RenderSessionPolicyText(cfg)
        chkSessionPolicyOn.Checked = cfg.Enabled
    End Sub

    ' EV chase budget §4: one-time seed of the Min Net Profit box (identifiers still say MinNetMove -
    ' display-text-only rename, owner 2026-07-30) from the host's loaded settings.
    ' MUST run before the first CommitToolingConfig (see InitialiseSettings). x100 because the host
    ' holds the FRACTION and the box speaks PERCENT; invariant culture so the rendered text is the
    ' same string the invariant parse on commit reads back.
    Private Sub SeedMinNetMoveFromHost()
        If _host Is Nothing Then Return
        txtMinNetMove.Text = (_host.MinNetMovePct * 100D).ToString(Globalization.CultureInfo.InvariantCulture)
    End Sub

    ' N2 §2: one-time seed of the risk-sizing checkbox from the host's loaded settings. MUST run
    ' before the first CommitGateConfig (see InitialiseSettings) or that commit writes the Designer's
    ' unticked box straight over a persisted, enabled knob. The mirror is seeded alongside the box so
    ' the bridge reads the file's value even if no commit has run yet.
    Private Sub SeedRiskSizeBridgeFromHost()
        If _host Is Nothing Then Return
        _riskSizeBridgeTrades = _host.RiskSizeBridgeTrades
        chkRiskSizeBridge.Checked = _riskSizeBridgeTrades
    End Sub

    ' Indicator-form retirement §2.5: one-time seed of the Flat ATR box and the "Use flat ATR"
    ' checkbox from the host's loaded settings. MUST run before the first CommitToolingConfig (see
    ' InitialiseSettings). The box renders like ReseedSenderFromHost renders it, so seed and re-seed
    ' agree. The checkbox's handler is wired AFTER this runs, so seeding it commits nothing.
    Private Sub SeedFlatAtrFromHost()
        If _host Is Nothing Then Return
        txtAtrFallback.Text = _host.FlatAtrUsd.ToString()
        chkUseFlatAtr.Checked = _host.UseFlatAtrInForce
    End Sub

    ' §2: one-time seed of the risk-sizing boxes from the host's loaded settings. MUST run before
    ' the first CommitToolingConfig (see InitialiseSettings) so the file's values win over the
    ' Designer defaults.
    Private Sub SeedRiskSizingFromHost()
        If _host Is Nothing Then Return
        txtRiskPerTrade.Text = _host.RiskPerTradeUsd.ToString()
        txtMaxSize.Text = _host.MaxSizeUsd.ToString()
    End Sub

    Private Sub CommitToolingConfig()
        If _host Is Nothing Then Return
        ' Flat ATR + "Use flat ATR" (Indicator-form retirement R3/R5). Blank/garbage collapses to 0D
        ' and the host ignores non-positive, so the last good Flat ATR stays in force - it must
        ' stay > 0 because the guard has no "no ATR" branch any more. The checkbox is always taken.
        Dim flatAtr As Decimal
        If Not Decimal.TryParse(txtAtrFallback.Text, flatAtr) Then flatAtr = 0D
        _host.SetToolingValues(flatAtr, chkUseFlatAtr.Checked)

        ' §2: risk-sizing values. THE TWO BOXES NOW FOLLOW DIFFERENT CONTRACTS
        ' (docs/spec-max-size-uncapped.md §2.1 edit 1; the asymmetry is ruled in that spec's §2.3):
        '
        '   * risk/trade keeps SetToolingValues' contract - the host ignores non-positive, so blank
        '     or garbage collapses to 0D here and keeps the last good value there.
        '
        '   * max size follows the CIRCUIT BREAKER's contract instead (:284-289): only a PARSE
        '     FAILURE keeps the last good value, and whatever parsed is committed - INCLUDING 0,
        '     which is how RiskSizedBase has always spelled "no cap". Collapsing a parse failure to
        '     0D, which this code used to do for both boxes, destroyed the one distinction that
        '     matters: Decimal.TryParse("") FAILS and Decimal.TryParse("0") SUCCEEDS. Blank and an
        '     explicit 0 were already distinguishable, and the old line threw that away before the
        '     host could act on it.
        '
        ' Persistence rides item A's save path for both; no new one here.
        Dim risk As Decimal
        Dim maxSize As Decimal
        If Not Decimal.TryParse(txtRiskPerTrade.Text, risk) Then risk = 0D
        ' The setter is SHARED with risk/trade, so the breaker's "guard the call" shape is not
        ' available here. Re-sending the value already in force is the same thing: the host sees no
        ' change, keeps the last good value, and stays silent (no §2.2 (a) line for a typo).
        If Not Decimal.TryParse(txtMaxSize.Text, maxSize) Then maxSize = _host.MaxSizeUsd
        _host.SetRiskSizingValues(risk, maxSize)

        ' EV chase budget §4. Two things are deliberately different from the boxes above:
        '   * the BREAKER's contract, not SetToolingValues' - 0 is a legitimate, persistable OFF, so
        '     ANY parsed value is pushed and only a PARSE failure keeps the last good one;
        '   * an INVARIANT parse. "0.05" read under a comma-decimal culture would treat the dot as a
        '     group separator and commit FIVE PERCENT - a 100x error on a guard that abandons trades.
        ' The /100 here is the one and only percent -> fraction conversion in the path.
        Dim minNetMovePercent As Decimal
        If Decimal.TryParse(txtMinNetMove.Text, Globalization.NumberStyles.Number,
                            Globalization.CultureInfo.InvariantCulture, minNetMovePercent) Then
            _host.SetMinNetMovePct(minNetMovePercent / 100D)
        End If
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
        If Not Decimal.TryParse(txtAtrFallback.Text, d) OrElse d <= 0D Then problems.Add($"flat ATR '{txtAtrFallback.Text}'")
        If Not Decimal.TryParse(txtRiskPerTrade.Text, d) OrElse d <= 0D Then problems.Add($"risk/trade '{txtRiskPerTrade.Text}'")
        ' Uncapped Max Size §2.1 edit 3: a PARSE failure only. 0 now COMMITS as a deliberate,
        ' persistable "no cap", so it must not be flagged - the invariant stated three lines below
        ' for the EV box governs this one too: A BOX THAT COMMITS MUST NOT WARN, AND A BOX THAT
        ' WARNS MUST NOT COMMIT. The risk/trade line above keeps its <= 0 arm precisely because it
        ' does NOT commit a non-positive value. Do not unify the two.
        If Not Decimal.TryParse(txtMaxSize.Text, d) Then problems.Add($"max size '{txtMaxSize.Text}'")
        ' EV chase budget §4: a PARSE failure only - 0 and negatives are valid ways to spell OFF.
        ' Checked invariantly, matching the commit above (a box that commits must not warn, and a
        ' box that warns must not commit).
        If Not Decimal.TryParse(txtMinNetMove.Text, Globalization.NumberStyles.Number,
                                Globalization.CultureInfo.InvariantCulture, d) Then problems.Add($"min net profit '{txtMinNetMove.Text}'")
        If _sessionPolicyProblem IsNot Nothing Then problems.Add($"session policy '{_sessionPolicyProblem}'")

        ' Record the warning (Nothing = none) and hand the label to its single writer, which decides
        ' whether the warning or the bridge status wins. Setting this to Nothing is what CLEARS a
        ' warning once the config parses again - the whole point of the 2026-07-21 repair.
        _gateConfigWarning = If(problems.Count = 0, Nothing,
                                "Ignored (keeping last good): " & String.Join("; ", problems))
        RefreshBridgePanel()
    End Sub

    ' Risk-sizing UI spec §3: thin forwarder - the sizing logic stays on the host, where the
    ' engine mirrors, best prices, txtAmount and the main log live. The explicit commit is
    ' required, not decorative: clicking the button normally fires Leave on a focused box first,
    ' but relying on focus order for a value that decides POSITION SIZE is exactly the half-typed
    ' hazard the commit-on-blur model exists to prevent - commit-first guarantees the click uses
    ' the number on screen. Both forms are UI-thread, so the direct call is safe.
    Private Sub btnRiskSize_Click(sender As Object, e As EventArgs) Handles btnRiskSize.Click
        CommitToolingConfig()
        If _host IsNot Nothing Then _host.ApplyRiskBasedSize()
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
            ApplyBridgeStatusLine("Bridge not attached", SystemColors.ControlLight)
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
        ApplyBridgeStatusLine($"{_bridge.Mode} | {If(started, "STARTED", "stopped")} | Engine ARM: {If(_bridge.EngineArmed, "ON", "off")} | Payload: {If(fresh, "FRESH", "stale/none")}",
                              If(started, Color.LimeGreen, If(fresh, SystemColors.ControlLight, Color.Orange)))

        Dim summary As String = _bridge.LastSignalSummary
        Dim dispo As String = _bridge.LastDisposition
        lblBridgeLast.Text = "Last: " & If(summary.Length > 0, summary & "  ->  " & If(dispo.Length > 0, dispo, "(pending)"), "-")
    End Sub

    ' The SINGLE writer of lblBridgeStatus, so the label has one owner instead of two racing ones.
    '
    ' A live gate-config warning OUTRANKS the bridge status line and holds it until the config parses
    ' again. Before this, the label had two independent writers and both directions were broken:
    ' ShowGateConfigWarnings SET a warning but never CLEARED it (it returns early when there are no
    ' problems), so a warning stayed on screen long after the typo was fixed; and RefreshBridgePanel
    ' overwrote warnings blind, so the Enter-key path (commit THEN refresh) discarded them instantly.
    ' Runtime-caught on testnet 2026-07-21 - with every box valid and a forced commit, the label was
    ' still showing a stale refusal.
    '
    ' "What you typed is NOT what is in force" is the more urgent of the two messages, and unlike the
    ' status line it is self-clearing: fix the text and the next commit restores the status.
    Private Sub ApplyBridgeStatusLine(text As String, colour As Color)
        If _gateConfigWarning IsNot Nothing Then
            lblBridgeStatus.Text = _gateConfigWarning
            lblBridgeStatus.ForeColor = Color.Orange
            Return
        End If
        lblBridgeStatus.Text = text
        lblBridgeStatus.ForeColor = colour
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
