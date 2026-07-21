Option Strict On
Option Explicit On

Imports System.Globalization
Imports System.Text
Imports Newtonsoft.Json.Linq

''' <summary>
''' One session's rule (docs/spec-session-policy-gate.md §1): which confidence tiers and which
''' verdict_context identifiers are accepted, plus the size multiplier. IMMUTABLE by construction -
''' see <see cref="SessionPolicyConfig"/> for why that matters.
''' </summary>
Friend NotInheritable Class SessionPolicyRule

    ' Backing fields, not auto-properties, and NOT optional style here: VB is case-INSENSITIVE, so a
    ' constructor parameter named `sizeMult` shadows an auto-property named `SizeMult` and
    ' "SizeMult = sizeMult" silently assigns the parameter to itself. That exact bug shipped in the
    ' first draft of this file and every multiplier read back as 0.0 (caught by the §9.3 fixtures).
    ' Underscore-prefixed backing fields make the collision impossible - and match the rest of the app.
    Private ReadOnly _tiers As List(Of String)
    Private ReadOnly _contexts As List(Of String)
    Private ReadOnly _sizeMult As Decimal

    ''' <summary>Accepted confidence tiers, canonical + upper-case. Never empty (the parse rejects
    ''' an empty set), so a rule can never accidentally refuse everything.</summary>
    Friend ReadOnly Property Tiers As IReadOnlyList(Of String)
        Get
            Return _tiers.AsReadOnly()
        End Get
    End Property

    ''' <summary>Accepted verdict_context identifiers, upper-case. EMPTY = "any" (no restriction).
    ''' Compared ORDINAL and opaquely - the app keeps no vocabulary list (contract addendum §6:
    ''' the engine pins these as stable identifiers).</summary>
    Friend ReadOnly Property Contexts As IReadOnlyList(Of String)
        Get
            Return _contexts.AsReadOnly()
        End Get
    End Property

    ''' <summary>Size multiplier, (0, 1] per D4. 1.0 = size-neutral (§4 unity passthrough).</summary>
    Friend ReadOnly Property SizeMult As Decimal
        Get
            Return _sizeMult
        End Get
    End Property

    Friend Sub New(tiers As IEnumerable(Of String), contexts As IEnumerable(Of String), sizeMult As Decimal)
        _tiers = New List(Of String)(tiers)
        _contexts = New List(Of String)(contexts)
        _sizeMult = sizeMult
    End Sub

    ''' <summary>§1 defaults: HIGH,MEDIUM | any | 1.0 - i.e. exactly today's behaviour. Used for a
    ''' session key the config omits, and for the whole block when it is absent or unreadable.</summary>
    Friend Shared Function DefaultRule() As SessionPolicyRule
        Return New SessionPolicyRule({"HIGH", "MEDIUM"}, New String() {}, 1D)
    End Function

    ''' <summary>
    ''' Tier gate. The payload's confidence is canonicalised the same way the config is, so the
    ''' comparison is tier-for-tier regardless of which vocabulary either side typed.
    ''' </summary>
    Friend Function AllowsTier(confidence As String) As Boolean
        Return _tiers.Contains(CanonicalTier(confidence))
    End Function

    ''' <summary>
    ''' Context gate. Empty set = "any" = pass. Otherwise FAIL-CLOSED: an empty or absent payload
    ''' context against a non-"any" set fails the set (§1) - a restriction that silently lapsed
    ''' because a field was missing would be the exact opposite of what "restrict" means.
    ''' </summary>
    Friend Function AllowsContext(verdictContext As String) As Boolean
        If _contexts.Count = 0 Then Return True
        Return _contexts.Contains(If(verdictContext, "").Trim().ToUpperInvariant())
    End Function

    ''' <summary>
    ''' Canonicalise a tier token. The PINNED confidence enum is HIGH/MEDIUM/LOW (contract §3); the
    ''' proposal's STRONG/MEDIUM/WEAK are the trader's verdict-tier names for the same 1:1 dimension,
    ''' so they are accepted as INPUT aliases and stored/displayed canonical. We never parse the
    ''' `verdict` string itself - that is contract-informational free text.
    ''' </summary>
    Friend Shared Function CanonicalTier(token As String) As String
        Dim t As String = If(token, "").Trim().ToUpperInvariant()
        Select Case t
            Case "STRONG" : Return "HIGH"
            Case "WEAK" : Return "LOW"
            Case Else : Return t
        End Select
    End Function

    ''' <summary>The canonical tier vocabulary - the only tokens the config may contain.</summary>
    Friend Shared Function IsValidTier(canonical As String) As Boolean
        Return canonical = "HIGH" OrElse canonical = "MEDIUM" OrElse canonical = "LOW"
    End Function

End Class

''' <summary>
''' The session policy (docs/spec-session-policy-gate.md §1-§2): a per-session, opt-in restriction
''' layered on top of the contract's own gate chain.
'''
''' Two dimensions per UTC analysis session (ASIA/LONDON/NY) - which confidence TIERS and which
''' verdict CONTEXTS to accept - plus a size multiplier. This can NEVER widen what the contract
''' allows: it is a second filter evaluated after the whole §4.4 chain (including the GLOBAL Tiers
''' box - D1 intersection semantics), so it only ever narrows. It ships DISABLED, and disabled means
''' today's behaviour byte for byte, which is what makes landing it mid-soak safe (§10 acceptance 2).
'''
''' IMMUTABLE by construction: the settings form commits ONE snapshot and reference-SWAPS it, and
''' the bridge reads that reference from its FSW/timer threads (§3 - the TiersCsv precedent). Since
''' no instance is ever mutated in place, a reference read off the bridge threads is safe and a
''' half-committed config can never be observed. Every "change" returns a NEW instance.
''' </summary>
Friend NotInheritable Class SessionPolicyConfig

    ''' <summary>The pinned session keys, in canonical render order.</summary>
    Friend Shared ReadOnly SessionNames As String() = {"NY", "LONDON", "ASIA"}

    ' Backing fields for the same case-insensitivity reason as SessionPolicyRule above.
    Private ReadOnly _rules As Dictionary(Of String, SessionPolicyRule)
    Private ReadOnly _enabled As Boolean

    ''' <summary>Master switch. OFF = today's behaviour exactly; the gate does not even run.</summary>
    Friend ReadOnly Property Enabled As Boolean
        Get
            Return _enabled
        End Get
    End Property

    Friend Sub New(enabled As Boolean, rules As Dictionary(Of String, SessionPolicyRule))
        _enabled = enabled
        _rules = New Dictionary(Of String, SessionPolicyRule)(rules, StringComparer.OrdinalIgnoreCase)
    End Sub

    ''' <summary>Disabled, every session on the §1 defaults. The "absent config" answer everywhere.</summary>
    Friend Shared Function Defaults() As SessionPolicyConfig
        Return New SessionPolicyConfig(False, New Dictionary(Of String, SessionPolicyRule)(StringComparer.OrdinalIgnoreCase))
    End Function

    ''' <summary>The rule in force for a session. An absent key is NOT an error - it means the
    ''' §1 defaults, i.e. that session is unrestricted relative to today.</summary>
    Friend Function RuleFor(session As String) As SessionPolicyRule
        Dim rule As SessionPolicyRule = Nothing
        If _rules.TryGetValue(If(session, ""), rule) Then Return rule
        Return SessionPolicyRule.DefaultRule()
    End Function

    ''' <summary>True when this session key was explicitly configured (vs falling back to defaults).
    ''' Render uses it so the box shows only what the owner actually set.</summary>
    Friend Function HasRuleFor(session As String) As Boolean
        Return _rules.ContainsKey(If(session, ""))
    End Function

    ''' <summary>Immutable "with" - the checkbox toggles enablement without re-parsing the text.</summary>
    Friend Function WithEnabled(enabled As Boolean) As SessionPolicyConfig
        Return New SessionPolicyConfig(enabled, _rules)
    End Function

    ' ================================ text grammar (§1) ================================
    '
    '   NY     = HIGH,MEDIUM | any       | 1.0
    '   LONDON = MEDIUM      | CONFIRMED | 0.5
    '   ASIA   = HIGH,MEDIUM | any       | 0.75
    '
    ' SESSION = tiersCsv | contextsCsv-or-any | mult. Case-insensitive, whitespace-tolerant.
    ' An omitted session line means that session's defaults; a blank box means all defaults.

    ''' <summary>
    ''' Parse the settings box. Returns Nothing (with <paramref name="problem"/> naming the offending
    ''' line) on ANY malformed input - the caller then keeps the last good config and warns, per the
    ''' settings-form convention. Never throws.
    '''
    ''' The returned config is always Enabled=False: enablement is the checkbox's business, not the
    ''' text's. Callers compose with <see cref="WithEnabled"/>. That keeps this seam pure and makes
    ''' render(parse(x)) a clean round-trip.
    '''
    ''' Numbers parse INVARIANT (never the current culture): this same text round-trips through
    ''' orderapp-settings.json, which is invariant by definition, so a culture-sensitive parse would
    ''' make the file mean different things on different machines. That is the D1 culture bug's
    ''' standing lesson, applied before it can bite.
    ''' </summary>
    Friend Shared Function ParseSessionPolicyText(text As String, ByRef problem As String) As SessionPolicyConfig
        problem = Nothing
        Dim rules As New Dictionary(Of String, SessionPolicyRule)(StringComparer.OrdinalIgnoreCase)

        For Each rawLine As String In If(text, "").Split({vbCrLf, vbLf, vbCr}, StringSplitOptions.None)
            Dim line As String = rawLine.Trim()
            If line.Length = 0 Then Continue For   ' blank lines are free

            Dim eq As Integer = line.IndexOf("="c)
            If eq <= 0 Then
                problem = line
                Return Nothing
            End If

            Dim session As String = line.Substring(0, eq).Trim().ToUpperInvariant()
            If Not IsSessionName(session) Then
                problem = line
                Return Nothing
            End If
            If rules.ContainsKey(session) Then
                ' A duplicate session line is a config mistake, and silently taking the last one
                ' would put a rule in force that the owner cannot see by reading the top of the box.
                problem = line
                Return Nothing
            End If

            Dim parts As String() = line.Substring(eq + 1).Split("|"c)
            If parts.Length <> 3 Then
                problem = line
                Return Nothing
            End If

            ' --- tiers: canonicalise (STRONG/WEAK aliases), validate, de-dupe. Empty set = invalid.
            Dim tiers As New List(Of String)
            For Each tok As String In parts(0).Split(","c)
                If tok.Trim().Length = 0 Then Continue For
                Dim canonical As String = SessionPolicyRule.CanonicalTier(tok)
                If Not SessionPolicyRule.IsValidTier(canonical) Then
                    problem = line
                    Return Nothing
                End If
                If Not tiers.Contains(canonical) Then tiers.Add(canonical)
            Next
            If tiers.Count = 0 Then
                problem = line
                Return Nothing
            End If

            ' --- contexts: "any" = the empty set; otherwise opaque upper-case identifiers.
            Dim contexts As New List(Of String)
            Dim contextsText As String = parts(1).Trim()
            If Not String.Equals(contextsText, "any", StringComparison.OrdinalIgnoreCase) Then
                For Each tok As String In contextsText.Split(","c)
                    Dim ctx As String = tok.Trim().ToUpperInvariant()
                    If ctx.Length = 0 Then Continue For
                    If Not contexts.Contains(ctx) Then contexts.Add(ctx)
                Next
                If contexts.Count = 0 Then
                    ' Neither "any" nor a usable identifier - e.g. "NY = HIGH |  | 1.0". Fail rather
                    ' than guess, since the two readings ("any" vs "nothing") are opposites.
                    problem = line
                    Return Nothing
                End If
            End If

            ' --- multiplier: (0, 1] per D4. A >1 value is a size INCREASE hiding in a filter config.
            Dim mult As Decimal
            If Not Decimal.TryParse(parts(2).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, mult) OrElse
               mult <= 0D OrElse mult > 1D Then
                problem = line
                Return Nothing
            End If

            rules(session) = New SessionPolicyRule(tiers, contexts, mult)
        Next

        Return New SessionPolicyConfig(False, rules)
    End Function

    ''' <summary>
    ''' Render the canonical text for the settings box. Only sessions that were explicitly configured
    ''' get a line, so render(parse(x)) round-trips (an omitted session stays omitted rather than
    ''' materialising as a defaults line the owner never typed).
    ''' </summary>
    Friend Shared Function RenderSessionPolicyText(config As SessionPolicyConfig) As String
        If config Is Nothing Then Return ""
        Dim sb As New StringBuilder()
        For Each session As String In SessionNames
            If Not config.HasRuleFor(session) Then Continue For
            Dim rule As SessionPolicyRule = config.RuleFor(session)
            Dim contexts As String = If(rule.Contexts.Count = 0, "any", String.Join(",", rule.Contexts))
            If sb.Length > 0 Then sb.Append(vbCrLf)
            sb.Append($"{session} = {String.Join(",", rule.Tiers)} | {contexts} | {FormatMult(rule.SizeMult)}")
        Next
        Return sb.ToString()
    End Function

    ''' <summary>Invariant, and always with a visible decimal place so 1 renders as "1.0".</summary>
    Friend Shared Function FormatMult(mult As Decimal) As String
        Return mult.ToString("0.0##", CultureInfo.InvariantCulture)
    End Function

    Friend Shared Function IsSessionName(candidate As String) As Boolean
        For Each s As String In SessionNames
            If String.Equals(s, candidate, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    ' ================================ persistence (§1, D2) ================================

    ''' <summary>
    ''' Read the `session_policy` block. TOLERANT by contract: a missing, malformed or partly-garbage
    ''' block yields defaults + disabled rather than throwing or half-applying - the same discipline
    ''' AppUserSettings.Load uses for the rest of the file. A rule that does not parse is dropped
    ''' (that session falls back to defaults); it never disables the sessions around it.
    ''' </summary>
    Friend Shared Function FromJson(token As JToken) As SessionPolicyConfig
        If token Is Nothing Then Return Defaults()
        Try
            Dim enabled As Boolean = If(token.SelectToken("enabled")?.ToObject(Of Boolean?)(), False)
            Dim rules As New Dictionary(Of String, SessionPolicyRule)(StringComparer.OrdinalIgnoreCase)

            Dim sessions As JToken = token.SelectToken("sessions")
            If sessions IsNot Nothing Then
                For Each session As String In SessionNames
                    Dim node As JToken = sessions.SelectToken(session)
                    If node Is Nothing Then Continue For

                    ' tiers: aliases accepted here too, so a hand-edited file may say STRONG/WEAK.
                    Dim tiers As New List(Of String)
                    Dim tiersNode As JToken = node.SelectToken("tiers")
                    If tiersNode IsNot Nothing AndAlso tiersNode.Type = JTokenType.Array Then
                        For Each t As JToken In CType(tiersNode, JArray)
                            Dim canonical As String = SessionPolicyRule.CanonicalTier(t.ToString())
                            If SessionPolicyRule.IsValidTier(canonical) AndAlso Not tiers.Contains(canonical) Then tiers.Add(canonical)
                        Next
                    End If
                    If tiers.Count = 0 Then Continue For   ' unusable rule -> that session keeps defaults

                    ' contexts: the string "any" (or absent) = unrestricted; an array = the set.
                    Dim contexts As New List(Of String)
                    Dim contextsNode As JToken = node.SelectToken("contexts")
                    If contextsNode IsNot Nothing AndAlso contextsNode.Type = JTokenType.Array Then
                        For Each c As JToken In CType(contextsNode, JArray)
                            Dim ctx As String = c.ToString().Trim().ToUpperInvariant()
                            If ctx.Length > 0 AndAlso Not contexts.Contains(ctx) Then contexts.Add(ctx)
                        Next
                    End If

                    Dim mult As Decimal = If(node.SelectToken("size_mult")?.ToObject(Of Decimal?)(), 1D)
                    If mult <= 0D OrElse mult > 1D Then mult = 1D   ' D4 range; out-of-range = size-neutral

                    rules(session) = New SessionPolicyRule(tiers, contexts, mult)
                Next
            End If

            Return New SessionPolicyConfig(enabled, rules)
        Catch
            ' A half-parsed policy must not half-apply.
            Return Defaults()
        End Try
    End Function

    ''' <summary>
    ''' Write the block in the proposal-§2 shape. Only EXPLICITLY configured sessions are written -
    ''' the same rule render uses - so the settings box, the file and the box after a restart all
    ''' show the same lines. Materialising unconfigured sessions as defaults here would mean the box
    ''' silently grew two lines the owner never typed the first time they saved.
    ''' (orderapp-settings.example.json is where the full shape is documented.)
    ''' </summary>
    Friend Function ToJson() As JObject
        Dim sessions As New JObject()
        For Each session As String In SessionNames
            If Not HasRuleFor(session) Then Continue For
            Dim rule As SessionPolicyRule = RuleFor(session)

            Dim tiersArr As New JArray()
            For Each t As String In rule.Tiers
                tiersArr.Add(t)
            Next

            Dim contexts As JToken
            If rule.Contexts.Count = 0 Then
                contexts = New JValue("any")
            Else
                Dim contextsArr As New JArray()
                For Each c As String In rule.Contexts
                    contextsArr.Add(c)
                Next
                contexts = contextsArr
            End If

            sessions(session) = New JObject From {
                {"tiers", tiersArr},
                {"contexts", contexts},
                {"size_mult", rule.SizeMult}
            }
        Next
        Return New JObject From {
            {"enabled", Enabled},
            {"sessions", sessions}
        }
    End Function

End Class
