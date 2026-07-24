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

        ' ---- 11. Item H: IsSignificantDisposition (spec-execution-ergonomics, owner-amended ----
        ' 2026-07-17). Pins the host-log filter's predicate: acted/rejected = significant (always
        ' print); everything else - would-act, refused: <gate>, stale, skipped, duplicate - is
        ' chatter (printed only in Log-only/Off while flat with no working entry).
        Check("significant: acted (id ...)",
              SignalBridge.IsSignificantDisposition("acted (id ETH-123)"))
        Check("significant: rejected: timeout",
              SignalBridge.IsSignificantDisposition("rejected: timeout"))
        Check("not significant: would-act (defensive - cannot occur in Live)",
              Not SignalBridge.IsSignificantDisposition("would-act: LONG @ 60000.00, stop 59950.00, target 60080.00, size 10"))
        Check("not significant: refused: not_flat",
              Not SignalBridge.IsSignificantDisposition("refused: not_flat"))
        Check("not significant: refused: levels",
              Not SignalBridge.IsSignificantDisposition("refused: levels"))
        Check("not significant: stale",
              Not SignalBridge.IsSignificantDisposition("stale"))
        Check("not significant: skipped",
              Not SignalBridge.IsSignificantDisposition("skipped"))
        Check("not significant: duplicate",
              Not SignalBridge.IsSignificantDisposition("duplicate"))

        ' ---- 12. Item C schema migration (spec-execution-ergonomics): an OLD-schema trades DB ----
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

        ' ================== session policy gate (docs/spec-session-policy-gate.md) ==================

        ' ---- Session buckets (spec §2 / §9.1): pinned engine-identical UTC boundaries ----
        ' All four edges, plus both ends of the day. These are UTC ANALYSIS sessions - not the
        ' owner's UTC+8 Inclusion Time Range, which is a different clock doing a different job.
        Check("session bucket 00:00 UTC = ASIA", SignalBridge.SessionBucketFor(0) = "ASIA")
        Check("session bucket 07:59 UTC = ASIA", SignalBridge.SessionBucketFor(7) = "ASIA")
        Check("session bucket 08:00 UTC = LONDON", SignalBridge.SessionBucketFor(8) = "LONDON")
        Check("session bucket 12:59 UTC = LONDON", SignalBridge.SessionBucketFor(12) = "LONDON")
        Check("session bucket 13:00 UTC = NY", SignalBridge.SessionBucketFor(13) = "NY")
        Check("session bucket 23:59 UTC = NY", SignalBridge.SessionBucketFor(23) = "NY")

        ' ---- The bucket derived THROUGH ParsePayload (§9.2 - the DateTimeKind trap) ----
        ' 23:30Z is NY. Read as a LOCAL time on this UTC+8 machine it becomes 07:30 the NEXT DAY =
        ' ASIA, so the hour is asserted alongside the bucket: a Kind slip and a boundary slip are
        ' different bugs and should not look alike in the output.
        Dim lateUtc As SignalBridge.PayloadSnapshot = Parse("late-utc-hour.json")
        Check("bucket via ParsePayload: 23:30Z parses as hour 23, Kind=Utc",
              lateUtc.TimestampOk AndAlso lateUtc.GeneratedUtc.Kind = DateTimeKind.Utc AndAlso
              lateUtc.GeneratedUtc.Hour = 23,
              $"got {lateUtc.GeneratedUtc:o} (Kind={lateUtc.GeneratedUtc.Kind})")
        Check("bucket via ParsePayload: 23:30Z = NY (a local misread would say ASIA)",
              SignalBridge.SessionBucketForPayload(lateUtc.GeneratedUtc) = "NY",
              $"got {SignalBridge.SessionBucketForPayload(lateUtc.GeneratedUtc)}")
        Check("bucket via ParsePayload: contract §3 example (14:31Z) = NY",
              SignalBridge.SessionBucketForPayload(Parse("contract-s3.json").GeneratedUtc) = "NY")

        ' ---- The settings-box text grammar (§1 / §9.3) ----
        Dim policyProblem As String = Nothing
        Dim exampleText As String = String.Join(vbCrLf, {
            "NY = HIGH,MEDIUM | any | 1.0",
            "LONDON = MEDIUM | CONFIRMED | 0.5",
            "ASIA = HIGH,MEDIUM | any | 0.75"})
        Dim parsedExample As SessionPolicyConfig =
            SessionPolicyConfig.ParseSessionPolicyText(exampleText, policyProblem)
        Check("policy parse: the §1 three-line example parses clean",
              parsedExample IsNot Nothing AndAlso policyProblem Is Nothing, $"problem='{policyProblem}'")
        Check("policy parse: render(parse(x)) round-trips canonically",
              SessionPolicyConfig.RenderSessionPolicyText(parsedExample) = exampleText,
              $"got '{SessionPolicyConfig.RenderSessionPolicyText(parsedExample)}'")

        If parsedExample IsNot Nothing Then
            Dim london As SessionPolicyRule = parsedExample.RuleFor("LONDON")
            Check("policy parse: LONDON = MEDIUM | CONFIRMED | 0.5",
                  london.Tiers.Count = 1 AndAlso london.Tiers(0) = "MEDIUM" AndAlso
                  london.Contexts.Count = 1 AndAlso london.Contexts(0) = "CONFIRMED" AndAlso
                  london.SizeMult = 0.5D)
            Check("policy parse: contexts 'any' = the empty set (unrestricted)",
                  parsedExample.RuleFor("NY").Contexts.Count = 0)
            Check("policy parse: session names are case-insensitive",
                  parsedExample.RuleFor("london").SizeMult = 0.5D)
        End If

        Dim aliased As SessionPolicyConfig =
            SessionPolicyConfig.ParseSessionPolicyText("NY = STRONG,WEAK | any | 1.0", policyProblem)
        Check("policy parse: STRONG/WEAK canonicalize to HIGH/LOW",
              SessionPolicyConfig.RenderSessionPolicyText(aliased) = "NY = HIGH,LOW | any | 1.0",
              $"got '{SessionPolicyConfig.RenderSessionPolicyText(aliased)}'")

        Dim londonOnly As SessionPolicyConfig =
            SessionPolicyConfig.ParseSessionPolicyText("LONDON = MEDIUM | CONFIRMED | 0.5", policyProblem)
        Check("policy parse: an omitted session falls back to the §1 defaults",
              londonOnly IsNot Nothing AndAlso
              londonOnly.RuleFor("NY").AllowsTier("HIGH") AndAlso londonOnly.RuleFor("NY").AllowsTier("MEDIUM") AndAlso
              Not londonOnly.RuleFor("NY").AllowsTier("LOW") AndAlso
              londonOnly.RuleFor("NY").Contexts.Count = 0 AndAlso londonOnly.RuleFor("NY").SizeMult = 1D)

        Dim blankPolicy As SessionPolicyConfig =
            SessionPolicyConfig.ParseSessionPolicyText("   " & vbCrLf & vbCrLf, policyProblem)
        Check("policy parse: a blank box is all-defaults, not an error",
              blankPolicy IsNot Nothing AndAlso policyProblem Is Nothing AndAlso
              SessionPolicyConfig.RenderSessionPolicyText(blankPolicy) = "" AndAlso
              blankPolicy.RuleFor("ASIA").SizeMult = 1D)

        ' Every malformed line keeps the last good config and names the offending line (§1).
        For Each bad As String In {"NY = HIGH,MEDIUM | any | 1.5",
                                   "NY = HIGH,MEDIUM | any | 0",
                                   "TOKYO = HIGH | any | 1.0",
                                   "NY = NONSENSE | any | 1.0",
                                   "NY = HIGH | any",
                                   "NY = HIGH |  | 1.0",
                                   "just some text"}
            Dim badProblem As String = Nothing
            Dim rejected As SessionPolicyConfig = SessionPolicyConfig.ParseSessionPolicyText(bad, badProblem)
            Check($"policy parse rejects '{bad}' and reports the line",
                  rejected Is Nothing AndAlso badProblem = bad, $"problem='{badProblem}'")
        Next

        Dim dupProblem As String = Nothing
        Dim duplicated As SessionPolicyConfig = SessionPolicyConfig.ParseSessionPolicyText(
            "NY = HIGH | any | 1.0" & vbCrLf & "NY = MEDIUM | any | 0.5", dupProblem)
        Check("policy parse rejects a duplicate session line (naming the second one)",
              duplicated Is Nothing AndAlso dupProblem = "NY = MEDIUM | any | 0.5", $"problem='{dupProblem}'")

        ' ---- Persistence round-trip (D2; the mechanism behind §10 acceptance 3) ----
        If parsedExample IsNot Nothing Then
            Dim reloaded As SessionPolicyConfig =
                SessionPolicyConfig.FromJson(parsedExample.WithEnabled(True).ToJson())
            Check("policy json: enabled + rules survive the settings-block round-trip",
                  reloaded.Enabled AndAlso
                  SessionPolicyConfig.RenderSessionPolicyText(reloaded) = exampleText,
                  $"enabled={reloaded.Enabled} text='{SessionPolicyConfig.RenderSessionPolicyText(reloaded)}'")
            Check("policy json: WithEnabled does not disturb the rules",
                  SessionPolicyConfig.RenderSessionPolicyText(parsedExample.WithEnabled(True)) = exampleText)
        End If
        Check("policy json: an absent block = defaults + disabled",
              Not SessionPolicyConfig.FromJson(Nothing).Enabled AndAlso
              SessionPolicyConfig.FromJson(Nothing).RuleFor("NY").SizeMult = 1D AndAlso
              SessionPolicyConfig.FromJson(Nothing).RuleFor("NY").AllowsTier("MEDIUM"))

        ' ---- The 4.4b evaluation (§3 / §9.4) ----
        ' LONDON = MEDIUM | CONFIRMED | 0.5, everything else on defaults.
        Dim gateProblem As String = Nothing
        Dim gatePolicy As SessionPolicyConfig = SessionPolicyConfig.ParseSessionPolicyText(
            "LONDON = MEDIUM | CONFIRMED | 0.5", gateProblem).WithEnabled(True)

        Check("policy gate: LONDON refuses HIGH as policy(LONDON/tier)",
              SignalBridge.PolicyRefusalFor(gatePolicy, "LONDON", "HIGH", "CONFIRMED") = "refused: policy(LONDON/tier)",
              $"got '{SignalBridge.PolicyRefusalFor(gatePolicy, "LONDON", "HIGH", "CONFIRMED")}'")
        Check("policy gate: LONDON refuses an empty context as policy(LONDON/context) (fail-closed)",
              SignalBridge.PolicyRefusalFor(gatePolicy, "LONDON", "MEDIUM", "") = "refused: policy(LONDON/context)",
              $"got '{SignalBridge.PolicyRefusalFor(gatePolicy, "LONDON", "MEDIUM", "")}'")
        Check("policy gate: LONDON refuses a non-listed context",
              SignalBridge.PolicyRefusalFor(gatePolicy, "LONDON", "MEDIUM", "MOMENTUM_FADING") = "refused: policy(LONDON/context)")
        Check("policy gate: LONDON passes MEDIUM + CONFIRMED",
              SignalBridge.PolicyRefusalFor(gatePolicy, "LONDON", "MEDIUM", "CONFIRMED") Is Nothing)
        Check("policy gate: an absent session key passes HIGH and MEDIUM on the defaults",
              SignalBridge.PolicyRefusalFor(gatePolicy, "NY", "HIGH", "") Is Nothing AndAlso
              SignalBridge.PolicyRefusalFor(gatePolicy, "NY", "MEDIUM", "ANYTHING") Is Nothing)
        Check("policy gate: an absent session key still refuses LOW (the defaults are HIGH,MEDIUM)",
              SignalBridge.PolicyRefusalFor(gatePolicy, "NY", "LOW", "") = "refused: policy(NY/tier)")
        Check("policy gate: tier is reported before context when BOTH fail",
              SignalBridge.PolicyRefusalFor(gatePolicy, "LONDON", "HIGH", "NOPE") = "refused: policy(LONDON/tier)")

        ' The soak-safety property, asserted rather than assumed: disabled says nothing, ever.
        Check("policy gate: DISABLED refuses nothing (the disabled-parity property)",
              SignalBridge.PolicyRefusalFor(gatePolicy.WithEnabled(False), "LONDON", "HIGH", "") Is Nothing AndAlso
              SignalBridge.PolicyRefusalFor(gatePolicy.WithEnabled(False), "LONDON", "LOW", "NOPE") Is Nothing)
        Check("policy gate: a Nothing policy refuses nothing",
              SignalBridge.PolicyRefusalFor(Nothing, "LONDON", "LOW", "") Is Nothing)
        Check("policy gate: contexts are matched case-insensitively via canonical upper-case",
              SignalBridge.PolicyRefusalFor(gatePolicy, "LONDON", "medium", "confirmed") Is Nothing)

        ' ---- Effective size (§4 / §9.5) ----
        ' §9.5 asked the implementer to establish whether placement enforces the 10-USD step
        ' upstream. It does NOT: ExecuteOrderAsync only requires amount > 0, and the step floor lives
        ' solely in ApplyRiskBasedSize (the SIZE button). So a non-step Amount is reachable by typing,
        ' which is exactly why unity must pass through instead of flooring.
        Check("effective size: 20 x 0.5 = 10", SignalBridge.EffectiveSizeUsd(20D, 0.5D) = 10D)
        Check("effective size: 10 x 0.75 clamps up to the contract min 10",
              SignalBridge.EffectiveSizeUsd(10D, 0.75D) = 10D)
        Check("effective size: 30 x 0.5 floors to 10", SignalBridge.EffectiveSizeUsd(30D, 0.5D) = 10D)
        Check("effective size: 100 x 0.75 = 70 (floored from 75)",
              SignalBridge.EffectiveSizeUsd(100D, 0.75D) = 70D)
        Check("effective size: 25 x 1.0 PASSES THROUGH as 25 (unity is size-neutral)",
              SignalBridge.EffectiveSizeUsd(25D, 1D) = 25D,
              $"got {SignalBridge.EffectiveSizeUsd(25D, 1D)}")
        Check("effective size: 25 x 0.5 = 10 (a reduction of a non-step amount still floors)",
              SignalBridge.EffectiveSizeUsd(25D, 0.5D) = 10D)
        Check("effective size: clamp is reported for 10 x 0.75, not for 20 x 0.5",
              SignalBridge.EffectiveSizeWasClamped(10D, 0.75D) AndAlso
              Not SignalBridge.EffectiveSizeWasClamped(20D, 0.5D))
        Check("effective size: unity never reports a clamp, even on a sub-step amount",
              Not SignalBridge.EffectiveSizeWasClamped(5D, 1D) AndAlso
              SignalBridge.EffectiveSizeUsd(5D, 1D) = 5D)

        ' ---- Token classification (§9.6) ----
        ' item H's host-log filter: full stream while flat, quiet in-position. A policy refusal is a
        ' refusal like any other, so it must NOT be significant.
        Check("not significant: refused: policy(NY/tier)",
              Not SignalBridge.IsSignificantDisposition("refused: policy(NY/tier)"))
        Check("not significant: refused: policy(LONDON/context)",
              Not SignalBridge.IsSignificantDisposition("refused: policy(LONDON/context)"))

        ' ================== quick wins (docs/spec-quickwins-notifier-signalcols.md) ==================

        ' ---- Q1: the RemoteNotifier rate-limit seam ----
        ' Non-urgent: 5 s min interval AND max 12 per window, drop silently beyond; urgent bypasses
        ' both. Deterministic inputs only (the seam takes nowUtc - no clock reads here).
        Dim n0 As New DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc)
        Check("notifier: first-ever post sends (lastSent = MinValue)",
              RemoteNotifier.ShouldSend(n0, DateTime.MinValue, 0, False))
        Check("notifier: 4 s since last = dropped (5 s min interval)",
              Not RemoteNotifier.ShouldSend(n0.AddSeconds(4), n0, 1, False))
        Check("notifier: exactly 5 s since last sends",
              RemoteNotifier.ShouldSend(n0.AddSeconds(5), n0, 1, False))
        Check("notifier: window full (12 sent) = dropped even at a clean 6 s gap",
              Not RemoteNotifier.ShouldSend(n0.AddSeconds(6), n0, 12, False))
        Check("notifier: 11 in window still sends",
              RemoteNotifier.ShouldSend(n0.AddSeconds(6), n0, 11, False))
        Check("notifier: urgent bypasses BOTH limits (4 s gap AND a full window)",
              RemoteNotifier.ShouldSend(n0.AddSeconds(4), n0, 12, True))

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
