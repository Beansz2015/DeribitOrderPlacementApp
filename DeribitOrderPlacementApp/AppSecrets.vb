Option Strict On
Option Explicit On

Imports System.IO
Imports Newtonsoft.Json.Linq

''' <summary>
''' Loads Deribit API credentials from a local, git-ignored secrets.json
''' located beside the executable (copied from the project on build).
''' Copy secrets.example.json to secrets.json and fill in your credentials.
''' Never commit secrets.json.
''' </summary>
Public NotInheritable Class AppSecrets

    Private Sub New()
    End Sub

    Public Shared Property ClientId As String = ""
    Public Shared Property ClientSecret As String = ""

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
            ClientId = If(json.SelectToken("Deribit.ClientId")?.ToString(), "")
            ClientSecret = If(json.SelectToken("Deribit.ClientSecret")?.ToString(), "")

            If Not IsLoaded Then
                Return "Deribit.ClientId / Deribit.ClientSecret missing or empty in secrets.json."
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
