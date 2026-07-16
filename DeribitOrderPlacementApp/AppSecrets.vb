Option Strict On
Option Explicit On

Imports System.IO
Imports Newtonsoft.Json.Linq

''' <summary>
''' Loads Deribit API credentials from a local, git-ignored secrets.json
''' located beside the executable (copied from the project on build).
''' Copy secrets.example.json to secrets.json and fill in your credentials.
''' Never commit secrets.json.
'''
''' Environment profile (harness spec section 1): an optional "Environment"
''' field selects "live" (default when absent/unrecognised - existing files
''' keep working) or "testnet". Testnet selects the "DeribitTestnet"
''' credential block and test.deribit.com; a testnet selection with no
''' testnet block is a LOAD ERROR - it must never fall back to live keys.
''' </summary>
Public NotInheritable Class AppSecrets

    Private Const LiveWsUrl As String = "wss://www.deribit.com/ws/api/v2"
    Private Const TestnetWsUrl As String = "wss://test.deribit.com/ws/api/v2"

    Private Sub New()
    End Sub

    Public Shared Property ClientId As String = ""
    Public Shared Property ClientSecret As String = ""

    Private Shared _isTestnet As Boolean = False

    ''' <summary>True when secrets.json selected the testnet environment.</summary>
    Public Shared ReadOnly Property IsTestnet As Boolean
        Get
            Return _isTestnet
        End Get
    End Property

    ''' <summary>The websocket endpoint for the selected environment.</summary>
    Public Shared ReadOnly Property WsUrl As String
        Get
            Return If(_isTestnet, TestnetWsUrl, LiveWsUrl)
        End Get
    End Property

    Public Shared ReadOnly Property IsLoaded As Boolean
        Get
            Return Not String.IsNullOrWhiteSpace(ClientId) AndAlso Not String.IsNullOrWhiteSpace(ClientSecret)
        End Get
    End Property

    ''' <summary>
    ''' Attempts to load credentials. Returns Nothing on success, or a
    ''' human-readable error message on failure (caller logs to the UI).
    ''' </summary>
    Public Shared Function Load() As String
        Try
            Dim path As String = FindSecretsFile()
            If path Is Nothing Then
                Return "secrets.json not found beside the executable. " &
                       "Copy secrets.example.json to secrets.json and add your Deribit API credentials."
            End If

            Dim json As JObject = JObject.Parse(File.ReadAllText(path))

            ' Environment selection: absent or anything other than "testnet" = live.
            ' The flag is set BEFORE credential validation so the window title / WsUrl
            ' report the SELECTED environment even when its credential block is bad -
            ' a failed testnet load must never look (or connect) like live.
            Dim env As String = If(json.SelectToken("Environment")?.ToString(), "").Trim()
            _isTestnet = env.Equals("testnet", StringComparison.OrdinalIgnoreCase)

            If _isTestnet Then
                ' Fail closed: no silent fallback to the live block.
                If json.SelectToken("DeribitTestnet") Is Nothing Then
                    ClientId = ""
                    ClientSecret = ""
                    Return "Environment is 'testnet' but secrets.json has no DeribitTestnet block. " &
                           "Add DeribitTestnet.ClientId / DeribitTestnet.ClientSecret (see secrets.example.json)."
                End If
                ClientId = If(json.SelectToken("DeribitTestnet.ClientId")?.ToString(), "")
                ClientSecret = If(json.SelectToken("DeribitTestnet.ClientSecret")?.ToString(), "")
                If Not IsLoaded Then
                    Return "DeribitTestnet.ClientId / DeribitTestnet.ClientSecret missing or empty in secrets.json."
                End If
            Else
                ClientId = If(json.SelectToken("Deribit.ClientId")?.ToString(), "")
                ClientSecret = If(json.SelectToken("Deribit.ClientSecret")?.ToString(), "")
                If Not IsLoaded Then
                    Return "Deribit.ClientId / Deribit.ClientSecret missing or empty in secrets.json."
                End If
            End If

            Return Nothing
        Catch ex As Exception
            Return $"Failed to read secrets.json: {ex.Message}"
        End Try
    End Function

    Private Shared Function FindSecretsFile() As String
        Dim candidates As String() = {
            Path.Combine(AppContext.BaseDirectory, "secrets.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "secrets.json")
        }

        For Each c In candidates
            If File.Exists(c) Then Return c
        Next

        Return Nothing
    End Function

End Class
