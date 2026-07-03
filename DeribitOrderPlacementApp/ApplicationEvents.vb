Option Strict On
Option Explicit On

Imports Microsoft.VisualBasic.ApplicationServices

Namespace My
    ' The following events are available for MyApplication:
    ' Startup: Raised when the application starts, before the startup form is created.
    ' Shutdown: Raised after all application forms are closed.  This event is not raised if the application terminates abnormally.
    ' UnhandledException: Raised if the application encounters an unhandled exception.
    ' StartupNextInstance: Raised when launching a single-instance application and the application is already active.
    ' NetworkAvailabilityChanged: Raised when the network connection is connected or disconnected.

    Partial Friend Class MyApplication

        ' F12: exception backstops. The app leans on fire-and-forget Async Subs; an exception
        ' escaping one on a thread-pool context previously killed the process with no trace.
        Private Sub MyApplication_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
            AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf OnDomainUnhandled
            AddHandler TaskScheduler.UnobservedTaskException, AddressOf OnUnobservedTask
        End Sub

        Private Sub MyApplication_UnhandledException(sender As Object, e As ApplicationServices.UnhandledExceptionEventArgs) Handles Me.UnhandledException
            WriteCrashLog("UI thread", e.Exception)
            e.ExitApplication = False   ' a UI-thread fault should not kill a live trading session
        End Sub

        ' System-qualified: the file-level ApplicationServices import would otherwise capture
        ' the unqualified name and break the AddressOf signature match.
        Private Sub OnDomainUnhandled(sender As Object, e As System.UnhandledExceptionEventArgs)
            WriteCrashLog("AppDomain (fatal)", TryCast(e.ExceptionObject, Exception))
        End Sub

        Private Sub OnUnobservedTask(sender As Object, e As UnobservedTaskExceptionEventArgs)
            WriteCrashLog("Unobserved task", e.Exception)
            e.SetObserved()
        End Sub

        Private Sub WriteCrashLog(source As String, ex As Exception)
            Try
                IO.File.AppendAllText("crash.log",
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] {If(ex?.ToString(), "(no exception object)")}{Environment.NewLine}")
            Catch
            End Try
        End Sub

    End Class
End Namespace
