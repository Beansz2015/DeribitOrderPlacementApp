Option Strict On
Option Explicit On

Imports System.Data.SQLite
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

        ' ---- 9. RoundToTick (docs/spec-tick-rounding.md): NEAREST 0.5 tick, midpoints away from zero ----
        ' Engine levels and average_price fills are the app's two fractional price sources; every
        ' exchange-bound price derived from them must land on the tick grid or Deribit rejects -32602.
        Check("RoundToTick harness-finding fill: 64126.83 -> 64127.0",
              frmMainPageV2.RoundToTick(64126.83D) = 64127D)
        Check("RoundToTick contract-s3 stop: 59062.1 -> 59062.0",
              frmMainPageV2.RoundToTick(59062.1D) = 59062D)
        Check("RoundToTick contract-s3 target: 59095.1 -> 59095.0",
              frmMainPageV2.RoundToTick(59095.1D) = 59095D)
        Check("RoundToTick midpoint x.25 -> x.5 (away from zero)",
              frmMainPageV2.RoundToTick(64126.25D) = 64126.5D)
        Check("RoundToTick midpoint x.75 -> x+1.0 (away from zero)",
              frmMainPageV2.RoundToTick(64126.75D) = 64127D)
        Check("RoundToTick on-tick passthrough: 64126.5 -> 64126.5",
              frmMainPageV2.RoundToTick(64126.5D) = 64126.5D)
        Check("RoundToTick on-tick passthrough: 64127.0 -> 64127.0",
              frmMainPageV2.RoundToTick(64127D) = 64127D)

        ' ---- 10. DeriveManualSl composition on fractional engine stops: on-tick + trigger-near-stop ----
        ' The derived limit must be on the tick grid, and the app-derived trigger (limit +/- offset)
        ' must land within a quarter-tick (0.25) of the engine's raw stop.
        Dim fracStop As Decimal = 59062.1D ' contract section-3 example stop
        Dim slLong As Decimal = SignalBridge.DeriveManualSl(True, fracStop, 30D)
        Check("DeriveManualSl fractional stop LONG: result on-tick",
              Decimal.Remainder(slLong * 2D, 1D) = 0D, $"got {slLong}")
        Check("DeriveManualSl fractional stop LONG: trigger within 0.25 of stop",
              Math.Abs((slLong + 30D) - fracStop) <= 0.25D, $"got {slLong}")
        Dim slShort As Decimal = SignalBridge.DeriveManualSl(False, fracStop, 30D)
        Check("DeriveManualSl fractional stop SHORT: result on-tick",
              Decimal.Remainder(slShort * 2D, 1D) = 0D, $"got {slShort}")
        Check("DeriveManualSl fractional stop SHORT: trigger within 0.25 of stop",
              Math.Abs((slShort - 30D) - fracStop) <= 0.25D, $"got {slShort}")

        ' ---- 11. Item C schema migration (spec-execution-ergonomics): an OLD-schema trades DB ----
        ' opens cleanly through TradeDatabase (the ALTERs run + backfill), legacy rows read with
        ' metric defaults, an enriched row round-trips, and a SECOND open proves idempotency
        ' (the duplicate-column throws are swallowed per column).
        Dim dbPath As String = Path.Combine(Path.GetTempPath(), $"ordercheck-migration-{Guid.NewGuid():N}.db")
        Try
            ' The PRE-item-C schema, verbatim (8 columns), plus one legacy row.
            Using conn As New SQLiteConnection($"Data Source={dbPath};Version=3;")
                conn.Open()
                Using cmd As New SQLiteCommand(
                    "CREATE TABLE Trades (
                        TradeId INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp DATETIME NOT NULL,
                        OrderType TEXT NOT NULL, Direction TEXT NOT NULL,
                        EntryPrice DECIMAL(18,8) NOT NULL, ExitPrice DECIMAL(18,8) NOT NULL,
                        OrderSizeUSD DECIMAL(18,8) NOT NULL, ProfitLossUSD DECIMAL(18,8) NOT NULL,
                        IsProfit BOOLEAN NOT NULL);", conn)
                    cmd.ExecuteNonQuery()
                End Using
                Using cmd As New SQLiteCommand(
                    "INSERT INTO Trades (Timestamp, OrderType, Direction, EntryPrice, ExitPrice,
                     OrderSizeUSD, ProfitLossUSD, IsProfit)
                     VALUES ('2026-07-01 00:00:00','Limit','Long',60000,60050,10,0.5,1);", conn)
                    cmd.ExecuteNonQuery()
                End Using
            End Using

            Dim db As New TradeDatabase(dbPath)   ' ctor runs InitializeDatabase -> MigrateSchema
            Dim enriched As New TradeRecord("Limit", "Short", 61000D, 60900D, 20D, 0.33D, True) With {
                .MaeUSD = -1.23D, .MfeUSD = 4.56D, .PlannedStop = 61060D, .RMultiple = 1.67D, .FeesUSD = 0.12D}
            db.RecordCompletedTrade(enriched)

            Dim all = db.GetAllTrades()
            Dim legacy = all.FirstOrDefault(Function(t) t.Direction = "Long")
            Dim round = all.FirstOrDefault(Function(t) t.Direction = "Short")
            Check("migration: old DB opened, both rows readable", all.Count = 2)
            Check("migration: legacy row reads metric defaults (0 / '')",
                  legacy IsNot Nothing AndAlso legacy.MaeUSD = 0D AndAlso legacy.MfeUSD = 0D AndAlso
                  legacy.RMultiple = 0D AndAlso legacy.FeesUSD = 0D AndAlso legacy.SignalId = "")
            Check("migration: enriched row round-trips MAE/MFE/PlannedStop/R/Fees",
                  round IsNot Nothing AndAlso round.MaeUSD = -1.23D AndAlso round.MfeUSD = 4.56D AndAlso
                  round.PlannedStop = 61060D AndAlso round.RMultiple = 1.67D AndAlso round.FeesUSD = 0.12D)

            Dim db2 As New TradeDatabase(dbPath)  ' second open: ALTERs all throw duplicate-column
            Check("migration idempotent: re-open clean, rows intact", db2.GetAllTrades().Count = 2)
        Catch ex As Exception
            Check("migration fixture: no throw", False, ex.Message)
        Finally
            Try
                SQLiteConnection.ClearAllPools() ' release the file handle before delete (Windows)
                File.Delete(dbPath)
            Catch
            End Try
        End Try

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
