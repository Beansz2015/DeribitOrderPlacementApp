Option Strict On
Option Explicit On

Imports System.Globalization
Imports System.IO
Imports DeribitOrderPlacementApp

' =====================================================================================================
' OrderCheck - the order app's layer-1 logic harness (docs/spec-ui-test-harness.md section 3).
'
' Deterministic fixtures against the app's pure seams (SignalBridge Friend Shared functions, via
' InternalsVisibleTo). One line per fixture, "OK n/n" + exit 0 on full pass, exit 1 on any failure.
' Run: dotnet run --project tools/OrderCheck
'
' Fixture 1 (the day-first-culture parse set) exists because of the 8956baa culture bug: Newtonsoft's
' default DateParseHandling turned the contract's ISO-8601 string into a Date token re-rendered in
' the CURRENT culture, so on d/M/yyyy cultures every payload read as stale. The engine has an
' invariant-culture EMIT fixture (A22); this is the consumer-side PARSE fixture that was missing.
' =====================================================================================================
Module Program

    Private _passed As Integer = 0
    Private _failed As Integer = 0

    Private Sub Check(name As String, condition As Boolean, Optional detail As String = "")
        If condition Then
            _passed += 1
            Console.WriteLine($"PASS  {name}")
        Else
            _failed += 1
            Console.WriteLine($"FAIL  {name}{If(detail.Length > 0, " - " & detail, "")}")
        End If
    End Sub

    Private Function ReadFixture(fileName As String) As String
        Return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fileName))
    End Function

    Private Function Parse(fileName As String) As SignalBridge.PayloadSnapshot
        Return SignalBridge.ParsePayload(SignalBridge.ParsePayloadJson(ReadFixture(fileName)))
    End Function

    Public Function Main() As Integer
        Console.WriteLine("OrderCheck - order app logic harness")

        ' ---- 1. Day-first-culture parse (the named backlog hole behind the 8956baa culture bug) ----
        ' The contract section-3 example must yield the exact UTC instant under every culture: the
        ' shipped ParsePayloadJson keeps generated_at_utc a raw ISO string (DateParseHandling.None),
        ' so the invariant-culture TryParse never sees a culture-rendered date.
        Dim expectedUtc As New DateTime(2026, 7, 3, 14, 31, 2, DateTimeKind.Utc)
        Dim saved As CultureInfo = CultureInfo.CurrentCulture
        Try
            For Each cultureName As String In {"en-MY", "de-DE", "en-GB", "en-US"}
                CultureInfo.CurrentCulture = New CultureInfo(cultureName)
                Dim p As SignalBridge.PayloadSnapshot = Parse("contract-s3.json")
                Check($"culture-parse {cultureName}: GeneratedUtc = 2026-07-03T14:31:02Z",
                      p.TimestampOk AndAlso p.GeneratedUtc = expectedUtc,
                      $"got {p.GeneratedUtc:o} (TimestampOk={p.TimestampOk})")
            Next
        Finally
            CultureInfo.CurrentCulture = saved
        End Try

        ' ---- 2. Unparseable generated_at_utc = maximally stale, never a throw ----
        Dim badTs As SignalBridge.PayloadSnapshot = Parse("bad-timestamp.json")
        Check("bad timestamp: GeneratedUtc = MinValue, TimestampOk = False, no throw",
              badTs.GeneratedUtc = DateTime.MinValue AndAlso Not badTs.TimestampOk)

        ' ---- 3. Missing identity = parse defaults (the F-1 guard's precondition) ----
        Dim noId As SignalBridge.PayloadSnapshot = Parse("missing-identity.json")
        Check("missing identity: InstanceId = """" and SignalId = -1",
              noId.InstanceId = "" AndAlso noId.SignalId = -1L,
              $"got InstanceId='{noId.InstanceId}', SignalId={noId.SignalId}")

        ' ---- 4. Missing levels on an actionable payload = 0s (the 'refused: levels' precondition) ----
        Dim noLevels As SignalBridge.PayloadSnapshot = Parse("missing-levels.json")
        Check("missing levels on actionable payload: entry/stop/target all 0",
              noLevels.Direction = "LONG" AndAlso noLevels.Entry = 0D AndAlso
              noLevels.StopLevel = 0D AndAlso noLevels.Target = 0D)

        ' ---- 5. WEAK payload parses direction-carrying (tier refusal is the gate's job) ----
        Dim weak As SignalBridge.PayloadSnapshot = Parse("weak-long.json")
        Check("WEAK LONG payload: direction LONG + confidence LOW + levels read",
              weak.Direction = "LONG" AndAlso weak.Confidence = "LOW" AndAlso
              weak.StopLevel = 59950D AndAlso weak.Target = 60080D)

        ' ---- 6. DeriveManualSl: the trigger-lands-on-stop identity ----
        Check("DeriveManualSl LONG: stop - offset",
              SignalBridge.DeriveManualSl(True, 59000D, 30D) = 58970D)
        Check("DeriveManualSl SHORT: stop + offset",
              SignalBridge.DeriveManualSl(False, 59000D, 30D) = 59030D)

        ' ---- 7. IsInsideWindowCore (PINS the shipped fail-closed semantics at c7cc8a4) ----
        Check("window blank/blank: unrestricted (True)",
              SignalBridge.IsInsideWindowCore(TimeSpan.FromHours(3), "", ""))
        Check("window 09:00-17:00 at 10:00: inside (True)",
              SignalBridge.IsInsideWindowCore(New TimeSpan(10, 0, 0), "09:00", "17:00"))
        Check("window 09:00-17:00 at 18:00: outside (False)",
              Not SignalBridge.IsInsideWindowCore(New TimeSpan(18, 0, 0), "09:00", "17:00"))
        Check("window 09:00-17:00 at 09:00 exactly: inclusive start (True)",
              SignalBridge.IsInsideWindowCore(New TimeSpan(9, 0, 0), "09:00", "17:00"))
        Check("window 09:00-17:00 at 17:00 exactly: inclusive end (True)",
              SignalBridge.IsInsideWindowCore(New TimeSpan(17, 0, 0), "09:00", "17:00"))
        Check("midnight wrap 22:00-02:00 at 23:00: inside (True)",
              SignalBridge.IsInsideWindowCore(New TimeSpan(23, 0, 0), "22:00", "02:00"))
        Check("midnight wrap 22:00-02:00 at 01:00: inside (True)",
              SignalBridge.IsInsideWindowCore(New TimeSpan(1, 0, 0), "22:00", "02:00"))
        Check("midnight wrap 22:00-02:00 at 12:00: outside (False)",
              Not SignalBridge.IsInsideWindowCore(New TimeSpan(12, 0, 0), "22:00", "02:00"))
        Check("window invalid text: FAIL-CLOSED (False)",
              Not SignalBridge.IsInsideWindowCore(New TimeSpan(10, 0, 0), "garbage", "17:00"))
        Check("window half-blank (start only): FAIL-CLOSED (False)",
              Not SignalBridge.IsInsideWindowCore(New TimeSpan(10, 0, 0), "09:00", ""))
        Check("window half-blank (end only): FAIL-CLOSED (False)",
              Not SignalBridge.IsInsideWindowCore(New TimeSpan(10, 0, 0), "", "17:00"))

        ' ---- 8. IsDuplicateOf: the section-4.3 watermark comparison ----
        Check("duplicate: same instance, id = watermark",
              SignalBridge.IsDuplicateOf("engine-a", 100L, "engine-a", 100L))
        Check("duplicate: same instance, id < watermark",
              SignalBridge.IsDuplicateOf("engine-a", 99L, "engine-a", 100L))
        Check("not duplicate: same instance, id > watermark",
              Not SignalBridge.IsDuplicateOf("engine-a", 101L, "engine-a", 100L))
        Check("not duplicate: different instance, any id",
              Not SignalBridge.IsDuplicateOf("engine-b", 1L, "engine-a", 100L))

        ' ---- summary ----
        Dim total As Integer = _passed + _failed
        If _failed = 0 Then
            Console.WriteLine($"OK {_passed}/{total}")
            Return 0
        End If
        Console.WriteLine($"FAILED {_failed}/{total}")
        Return 1
    End Function

End Module
