Option Strict On
Option Explicit On

Imports System.IO
Imports Newtonsoft.Json.Linq

' =====================================================================================================
' ExecutorFeedback - the v2 executor feedback file (executor_feedback.json).
'
' Contract: docs/integration-contract-verdictengine.md SECTION 8 - canonical for emitter behaviour.
' Spec:     docs/spec-c1-feedback-emitter.md - it IMPLEMENTS section 8 and deliberately never
'           restates it, so the contract is the authority on every field, enum and semantic.
'
' Telemetry, never commands (contract 8.1 / R1 in both directions): this app WRITES this file and
' never reads one. Orders and signals flow engine -> app through verdict_signal.json ONLY.
'
' RemoteNotifier.vb is the MODEL here and every rule in its header applies: fire-and-forget,
' fail-silent, inert without configuration, off the caller's thread, one shared resource for the
' app's lifetime. Three BINDING differences from the notifier (spec section 1):
'   * self-coalescing LAST-WINS, not rate-limit-and-drop. A dropped notification is a missed alert;
'     a dropped feedback write leaves the engine on a stale executor picture until the next trigger,
'     so the newest state must always win.
'   * SINGLE WRITER - one serialized worker. Two concurrent writes to one path is precisely the
'     failure this design exists to prevent.
'   * the snapshot is taken on the CALLER's thread and handed over as a flat immutable value; the
'     worker never reaches back into live state.
'
' SHIPS OFF: no feedback_output_path in bridge.json = no timer, no worker, NO FILE CREATED.
' Contract section 8.5: file absent = feature OFF, never an alarm.
' =====================================================================================================
Friend NotInheritable Class ExecutorFeedback

    Private Sub New()
    End Sub

    ' Contract section 8.2: beside the signal file, and the same default on both sides.
    Friend Const DefaultOutputPath As String = "C:\Dev\DeribitBridge\executor_feedback.json"

    ' "" = not configured = inert. Set once by LoadConfig at startup and never again, so every
    ' IsConfigured check below is a plain field read from any thread.
    Private Shared _outputPath As String = ""

    ''' <summary>
    ''' True when bridge.json carries a feedback_output_path key (the v2 master switch).
    ''' Mirrors RemoteNotifier.IsConfigured: ONE property, checked at every entry point, so the
    ''' disabled path is a single field read and nothing downstream has to remember to guard.
    ''' </summary>
    Friend Shared ReadOnly Property IsConfigured As Boolean
        Get
            Return _outputPath.Length > 0
        End Get
    End Property

    ''' <summary>The resolved output path, or "" when the emitter is inert. NOT a credential -
    ''' unlike RemoteNotifier's ntfy topic URL this may be logged.</summary>
    Friend Shared ReadOnly Property OutputPath As String
        Get
            Return _outputPath
        End Get
    End Property

    ' ================================ config ================================

    ''' <summary>
    ''' Read feedback_output_path out of the SAME bridge.json SignalBridge reads (the file that
    ''' "carries only what has no natural home on a form"). Call once at startup, before anything
    ''' else here. A missing/broken file leaves the emitter inert, exactly like an absent key -
    ''' this feature may never be the reason the app fails to start.
    ''' </summary>
    Friend Shared Sub LoadConfig()
        Try
            Dim cfg As String = SignalBridge.ConfigFilePath
            If Not File.Exists(cfg) Then Return                      ' no bridge.json at all = inert
            Dim json As JObject = JObject.Parse(File.ReadAllText(cfg))
            Dim tok As JToken = json.SelectToken("feedback_output_path")
            _outputPath = ResolveOutputPath(If(tok?.ToString(), ""), tok IsNot Nothing)
        Catch
            _outputPath = ""                                         ' fail-silent: inert, never fatal
        End Try
    End Sub

    ' The config decision as a pure seam (the house pattern - ShouldSend, EffectiveSizeUsd), pinned
    ' by OrderCheck fixtures. THREE cases, and the first two are NOT the same thing:
    '   * key ABSENT       => "" => fully inert: no timer, no worker, no file (spec section 2).
    '   * key PRESENT BLANK => the section-8.2 default path (the owner asked for the feature and
    '                          left the location to us).
    '   * key present       => that path, trimmed.
    Friend Shared Function ResolveOutputPath(rawValue As String, keyPresent As Boolean) As String
        If Not keyPresent Then Return ""
        Dim v As String = If(rawValue, "").Trim()
        If v.Length = 0 Then Return DefaultOutputPath
        Return v
    End Function

    ''' <summary>
    ''' The one startup line, matching the notifier's precedent (Q1: exactly one line, "configured"
    ''' or "disabled"). The path IS logged - it is not a credential.
    ''' </summary>
    Friend Shared Function StartupLine() As String
        Return If(IsConfigured,
                  $"Executor feedback: configured - {_outputPath}",
                  "Executor feedback: disabled (no feedback_output_path in bridge.json)")
    End Function

End Class
