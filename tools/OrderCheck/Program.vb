Option Strict On
Option Explicit On

Imports System.Data.SQLite
Imports System.Globalization
Imports System.IO
Imports DeribitOrderPlacementApp
Imports Newtonsoft.Json.Linq

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

    ' ---- C1 helpers (docs/spec-c1-feedback-emitter.md section 5) ----

    ' One named-argument snapshot builder, so a fixture that varies EXACTLY ONE field reads as
    ' varying exactly one field. Fixture 8 asserts per field across the whole section-8.3 domain,
    ' and eleven positional BuildSnapshot calls would make "which one moved?" unreadable - which is
    ' the same legibility problem that let the aggregate version hide E6a.
    Private Function Snap(Optional instanceId As String = "exec-guid",
                          Optional mode As SignalBridge.BridgeMode = SignalBridge.BridgeMode.Live,
                          Optional armed As Boolean = True,
                          Optional started As Boolean = True,
                          Optional breakerTripped As Boolean = False,
                          Optional wsConnected As Boolean = True,
                          Optional sizeUsd As Decimal = 250D,
                          Optional avgEntry As Decimal = 59012.5D,
                          Optional workingStop As Decimal = 58962.5D,
                          Optional workingTarget As Decimal = 59095D,
                          Optional lastSignal As ExecutorFeedback.LastSignalRef = Nothing) _
                          As ExecutorFeedback.FeedbackSnapshot
        Return ExecutorFeedback.BuildSnapshot(instanceId, mode, armed, started, breakerTripped,
                                              wsConnected, sizeUsd, avgEntry, workingStop,
                                              workingTarget, lastSignal)
    End Function

    Private ReadOnly PinnedAt As New DateTime(2026, 8, 5, 9, 14, 2, DateTimeKind.Utc)

    Private Function SnapJson(s As ExecutorFeedback.FeedbackSnapshot) As String
        Return ExecutorFeedback.Serialize(s, 1, PinnedAt)
    End Function

    ' Every LEAF path in an emitted document. This is what turns fixture 8 from "the fields I
    ' remembered" into "the schema": a new section-8.3 field cannot be added without failing the
    ' domain check below, which forces whoever adds it to give it a per-field assertion - and
    ' therefore to think about whether it has a TRIGGER. E6a is precisely what happens when a field
    ' exists that no fixture enumerates. (H-6 section 7 lesson 6: enumerate the DOMAIN, not the keys.)
    Private Sub CollectLeafPaths(t As JToken, acc As List(Of String))
        Dim o As JObject = TryCast(t, JObject)
        If o IsNot Nothing AndAlso o.Count > 0 Then
            For Each p As JProperty In o.Properties()
                CollectLeafPaths(p.Value, acc)
            Next
        Else
            acc.Add(t.Path)
        End If
    End Sub

    ' ParsePayloadJson, not JObject.Parse: the latter's default DateParseHandling turns an ISO-8601
    ' string into a Date token rendered in the CURRENT culture (the 8956baa bug), which false-failed
    ' fixture 6 on its first run.
    Private Function LeafPaths(json As String) As List(Of String)
        Dim acc As New List(Of String)
        CollectLeafPaths(SignalBridge.ParsePayloadJson(json), acc)
        acc.Sort(StringComparer.Ordinal)
        Return acc
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
                .MaeUSD = -1.23D, .MfeUSD = 4.56D, .PlannedStop = 61060D, .RMultiple = 1.67D, .FeesUSD = 0.12D,
                .SignalId = "9001", .SignalConfidence = "HIGH"}
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
            ' Q2 (quick wins): the signal pair round-trips too, and the legacy row's stays '' -
            ' the legacy check above already asserts legacy.SignalId = "".
            Check("migration: enriched row round-trips SignalId/SignalConfidence",
                  round IsNot Nothing AndAlso round.SignalId = "9001" AndAlso round.SignalConfidence = "HIGH",
                  $"got '{round?.SignalId}'/'{round?.SignalConfidence}'")
            Check("migration: legacy row reads SignalConfidence '' (NULL-safe)",
                  legacy IsNot Nothing AndAlso legacy.SignalConfidence = "")

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

        ' ---- Risk-sized base: THE ONE FORMULA (docs/spec-risk-sized-bridge-trades.md §1/§4) ----
        ' RiskSizedBase is the SIZE button's own arithmetic, extracted so the bridge act path shares
        ' it instead of carrying a second copy. Arithmetic below was computed independently by the
        ' implementer and re-verified by the coordinator before these fixtures were written.
        Check("risk size: 25 over a 5000 stop at 64735 = 320 (323.675 floored to the 10-USD step)",
              SignalBridge.RiskSizedBase(25D, 500D, 64735D, 5000D) = 320D,
              $"got {SignalBridge.RiskSizedBase(25D, 500D, 64735D, 5000D)}")
        Check("risk size: 404.59 floors to 400, not 410 (flooring is DOWN, always)",
              SignalBridge.RiskSizedBase(25D, 500D, 64735D, 4000D) = 400D,
              $"got {SignalBridge.RiskSizedBase(25D, 500D, 64735D, 4000D)}")
        Check("risk size: a 54.62 stop wants 29,620 and the 500 cap binds",
              SignalBridge.RiskSizedBase(25D, 500D, 64735D, 54.62D) = 500D,
              $"got {SignalBridge.RiskSizedBase(25D, 500D, 64735D, 54.62D)}")
        Check("risk size: a risk too small for one step returns 0 (the bridge clamps it to 10, the button refuses)",
              SignalBridge.RiskSizedBase(0.01D, 500D, 64735D, 5000D) = 0D,
              $"got {SignalBridge.RiskSizedBase(0.01D, 500D, 64735D, 5000D)}")

        ' The two defect-§2 regression pins. Math.Min(riskSize, maxSizeUsd) - the formula the spec
        ' originally carried - fails BOTH of these, and both are silent in production: the first
        ' ships an off-step order size the exchange rejects -32602, the second collapses every sized
        ' bridge trade to the contract minimum the moment the owner spells "no cap" as 0.
        Check("risk size: a NON-STEP cap (505) is itself step-floored to 500, never emitted raw",
              SignalBridge.RiskSizedBase(25D, 505D, 64735D, 2000D) = 500D,
              $"got {SignalBridge.RiskSizedBase(25D, 505D, 64735D, 2000D)}")
        Check("risk size: max_size_usd = 0 means NO CAP (320), NOT a collapse to the 10-USD minimum",
              SignalBridge.RiskSizedBase(25D, 0D, 64735D, 5000D) = 320D,
              $"got {SignalBridge.RiskSizedBase(25D, 0D, 64735D, 5000D)}")
        ' D1 (docs/review-max-size-uncapped.md): this line used to end "matching the button's
        ' maxUsd > 0 guard". THERE IS NO SUCH GUARD. ApplyRiskBasedSize (frmMainPageV2.vb:322-364)
        ' guards refPrice, dist, riskUsd and the min-10 result; maxUsd is read at :339 and passed
        ' straight through at :353. Harmless while the form could not commit a 0 - load-bearing now
        ' that it can, and the owner's SB7 ruling accepts a 0 uncaps the MANUAL SIZE BUTTON too.
        Check("risk size: a negative max_size_usd is also 'no cap' - and NOTHING guards maxUsd upstream",
              SignalBridge.RiskSizedBase(25D, -1D, 64735D, 5000D) = 320D)

        ' ---- Uncapped Max Size (docs/spec-max-size-uncapped.md §2.2) ----
        ' The two fixtures ABOVE are what that spec's acceptance 8 asks for and they already existed,
        ' so no new arithmetic pin was needed - maxSizeUsd = 0 and a negative are both pinned as NO
        ' CAP. What was NOT pinned is the new visibility seam, and it is the half that can regress
        ' silently: with no cap in force, NEITHER SignalBridge yellow line can fire (the clamp-up at
        ' :1069 and the capped-by-max_size_usd at :1072 both require something to bind), so these two
        ' lines are the ONLY thing that says an uncapped state exists. One formatter feeds both the
        ' commit line and the startup line; pinning it is what stops them drifting apart.
        Check("max size line: 0 announces NO CAP and states the value",
              frmMainPageV2.MaxSizeLine(0D) = "Max size: NO CAP (max_size_usd = 0)",
              $"got '{frmMainPageV2.MaxSizeLine(0D)}'")
        Check("max size line: a cap in force states the number, so the line is worth reading either way",
              frmMainPageV2.MaxSizeLine(2000D) = "Max size: 2000",
              $"got '{frmMainPageV2.MaxSizeLine(2000D)}'")
        Check("max size line: a NEGATIVE cap is uncapped too - it must not read as a 'max size: -1'",
              frmMainPageV2.MaxSizeLine(-1D) = "Max size: NO CAP (max_size_usd = -1)",
              $"got '{frmMainPageV2.MaxSizeLine(-1D)}'")
        ' D2 (docs/review-max-size-uncapped.md): this pin used to render under the AMBIENT culture.
        ' This box is en-MY, whose decimal separator is ALREADY a dot, so it passed identically
        ' whether the code was invariant or not - a fixture that could not fail. Switch the culture
        ' the way fixture 1 (:108-119) and C1 fixture 6 (:964-972) do, or the pin proves nothing.
        Dim savedMaxLine As CultureInfo = CultureInfo.CurrentCulture
        Dim deMaxLine As String
        Try
            CultureInfo.CurrentCulture = New CultureInfo("de-DE")
            deMaxLine = frmMainPageV2.MaxSizeLine(2000.5D)
        Finally
            CultureInfo.CurrentCulture = savedMaxLine
        End Try
        Check("max size line: rendered invariantly EVEN UNDER de-DE, never in the current culture",
              deMaxLine = "Max size: 2000.5" AndAlso Not deMaxLine.Contains("2000,5"),
              $"got '{deMaxLine}'")
        ' The two states must never collide: whatever the wording, "no cap" and "capped" have to be
        ' distinguishable by a reader scanning the host log.
        Check("max size line: the uncapped and capped renderings are distinct strings",
              frmMainPageV2.MaxSizeLine(0D) <> frmMainPageV2.MaxSizeLine(2000D))

        ' The -1 sentinel arm: every input the formula cannot use. dist = 0 cannot occur past
        ' 'refused: levels', but entry CAN (the levels gate reads stop/target only) and a
        ' hand-edited risk_per_trade_usd of 0 can too - hence the act site's fail-safe.
        Check("risk size: dist = 0 returns the -1 sentinel, not a divide-by-zero",
              SignalBridge.RiskSizedBase(25D, 500D, 64735D, 0D) = -1D)
        Check("risk size: entry = 0 returns the -1 sentinel (the levels gate does NOT guard entry)",
              SignalBridge.RiskSizedBase(25D, 500D, 0D, 5000D) = -1D)
        Check("risk size: risk_per_trade_usd = 0 returns the -1 sentinel",
              SignalBridge.RiskSizedBase(0D, 500D, 64735D, 5000D) = -1D)
        Check("risk size: a negative stop distance returns the -1 sentinel",
              SignalBridge.RiskSizedBase(25D, 500D, 64735D, -5000D) = -1D)

        ' Composition: the session mult folds onto the risk-sized base as ONE chain (spec §1). This
        ' is the fixture that proves sessionFactor is applied EXACTLY ONCE - 320 halves to 160 and
        ' floors there; a second application would give 80.
        Check("composed: EffectiveSizeUsd(RiskSizedBase(25,500,64735,5000), 0.5) = 160, halved ONCE",
              SignalBridge.EffectiveSizeUsd(SignalBridge.RiskSizedBase(25D, 500D, 64735D, 5000D), 0.5D) = 160D,
              $"got {SignalBridge.EffectiveSizeUsd(SignalBridge.RiskSizedBase(25D, 500D, 64735D, 5000D), 0.5D)}")
        Check("composed: unity leaves a risk-sized base untouched (the passthrough ruling holds here too)",
              SignalBridge.EffectiveSizeUsd(SignalBridge.RiskSizedBase(25D, 500D, 64735D, 5000D), 1D) = 320D)

        ' ---- The act site's base-size decision (spec §1) ----
        ' Disabled parity itself is the `If RiskSizeBridgeTrades` guard at the act site - with the
        ' knob off baseSize IS rawSize and none of this runs, which is why the disabled path is
        ' byte-identical by construction. What IS pinnable is the enabled path's two arms.
        Check("act size: a -1 sentinel falls back to the raw Amount box, UNCHANGED",
              SignalBridge.RiskSizedOrFallback(25D, -1D) = 25D)
        Check("act size: the fallback does NOT floor a non-step Amount box (the passthrough ruling)",
              SignalBridge.RiskSizedOrFallback(25D, -1D) = 25D AndAlso
              SignalBridge.RiskSizedOrFallback(5D, -1D) = 5D)
        Check("act size: a computed 320 passes through as 320",
              SignalBridge.RiskSizedOrFallback(25D, 320D) = 320D)
        Check("act size: a sub-step 0 CLAMPS UP to the contract min 10 (the bridge clamps; the button refuses)",
              SignalBridge.RiskSizedOrFallback(25D, 0D) = 10D)
        Check("act size: an exactly-10 result is left alone",
              SignalBridge.RiskSizedOrFallback(25D, 10D) = 10D)
        ' The full chain, end to end, as the act site runs it: seam -> fallback/clamp -> sessionFactor.
        Check("act chain: risk-sized 320 then a LONDON 0.5 = 160 (ONE fold, not two)",
              SignalBridge.EffectiveSizeUsd(
                  SignalBridge.RiskSizedOrFallback(25D, SignalBridge.RiskSizedBase(25D, 500D, 64735D, 5000D)),
                  0.5D) = 160D)
        Check("act chain: a bad entry falls back to the box, and the session mult still applies once",
              SignalBridge.EffectiveSizeUsd(
                  SignalBridge.RiskSizedOrFallback(100D, SignalBridge.RiskSizedBase(25D, 500D, 0D, 5000D)),
                  0.5D) = 50D)

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

        ' ================== EV chase budget (docs/spec-ev-chase-budget.md) ==================

        ' ---- Fee derivation (§4): the round trip is 2 x MAKER, never a hardcoded 3.0 bps ----
        Check("EV fees: 2 x 1.5 bps maker = 0.0003 round trip",
              frmMainPageV2.RoundTripFeePctFromMakerBps(1.5D) = 0.0003D,
              $"got {frmMainPageV2.RoundTripFeePctFromMakerBps(1.5D)}")
        Check("EV fees: the derivation tracks a schedule change (2.0 bps maker = 0.0004)",
              frmMainPageV2.RoundTripFeePctFromMakerBps(2D) = 0.0004D)

        ' ---- Default comms (docs/spec-fee-comms-repoint.md): the TAKER leg, whole dollars ----
        ' The comms box is set on every index tick and feeds the derived TP and the break-even
        ' trigger, so this is the arithmetic that used to carry the 2024 constant.
        Check("comms: 3.5 bps taker of a 64k index = 22 (the 2024 constant gave 32)",
              frmMainPageV2.DefaultCommsFromTakerBps(3.5D, 64000D) = 22D,
              $"got {frmMainPageV2.DefaultCommsFromTakerBps(3.5D, 64000D)}")
        ' Repoint, not a reformulation: the retired constant was 5 bps, and feeding 5 bps back in
        ' reproduces the old number exactly. Any drift in the formula breaks this line.
        Check("comms: the retired 2024 rate re-derives as 5 bps (= 32 at 64k)",
              frmMainPageV2.DefaultCommsFromTakerBps(5D, 64000D) = 32D)
        ' Rounding is whole dollars, away from zero at the midpoint - 3.5 bps of 70k is exactly 24.5.
        Check("comms: a .5 lands away from zero (24.5 -> 25)",
              frmMainPageV2.DefaultCommsFromTakerBps(3.5D, 70000D) = 25D,
              $"got {frmMainPageV2.DefaultCommsFromTakerBps(3.5D, 70000D)}")
        ' No index yet (or an unparsed one) = 0, which the callers already treat as "no comms".
        Check("comms: a zero index gives 0", frmMainPageV2.DefaultCommsFromTakerBps(3.5D, 0D) = 0D)

        ' ---- IsChaseEvExhausted (§1 / §5) ----
        ' The shipped knob is 0, and 0 is the whole ship-safety argument: the predicate returns
        ' False on its first guard, before it looks at anything else.
        Dim rt As Decimal = frmMainPageV2.RoundTripFeePctFromMakerBps(1.5D)   ' 0.0003
        Check("EV floor: knob 0 = OFF, never binds (the ship-safe default)",
              Not frmMainPageV2.IsChaseEvExhausted(64800D, 64786D, rt, 0D))
        Check("EV floor: a negative knob is OFF too",
              Not frmMainPageV2.IsChaseEvExhausted(64800D, 64786D, rt, -0.0005D))

        ' §5's worked example: remaining 14, fees 19.44, floor 32.39 => exhausted.
        Check("EV floor: binds when the remaining move is under fees + floor (14 vs 19.4 + 32.4)",
              frmMainPageV2.IsChaseEvExhausted(64800D, 64786D, rt, 0.0005D))
        ' Same knobs, price 60 away: 60 - 19.42 = 40.58 net, over the 32.37 floor => keep chasing.
        Check("EV floor: slack when the target is still far (60 remaining)",
              Not frmMainPageV2.IsChaseEvExhausted(64800D, 64740D, rt, 0.0005D))

        ' Exact boundary, both sides. At price 60000: fees = 18, floor = 30, so a 48-wide remaining
        ' move nets EXACTLY the floor. The comparison is strict "<", so equality keeps chasing.
        Check("EV floor: exact equality does NOT bind (strict <)",
              Not frmMainPageV2.IsChaseEvExhausted(60048D, 60000D, rt, 0.0005D))
        Check("EV floor: a cent under the boundary binds",
              frmMainPageV2.IsChaseEvExhausted(60047.99D, 60000D, rt, 0.0005D))

        ' §3 (D1): no target in force = SKIP. The OFFSET flow passes 0 here and keeps ATR-only.
        Check("EV floor: a zero target never binds (the OFFSET-flow skip)",
              Not frmMainPageV2.IsChaseEvExhausted(0D, 64786D, rt, 0.0005D))
        Check("EV floor: a zero price never binds (undefined quote)",
              Not frmMainPageV2.IsChaseEvExhausted(64800D, 0D, rt, 0.0005D))

        ' SHORT symmetry: the target sits BELOW the price and Math.Abs handles it - the same three
        ' cases must give the same three answers mirrored around the price.
        Check("EV floor SHORT: binds when the target is 14 below the price",
              frmMainPageV2.IsChaseEvExhausted(64772D, 64786D, rt, 0.0005D))
        Check("EV floor SHORT: slack when the target is 60 below the price",
              Not frmMainPageV2.IsChaseEvExhausted(64680D, 64740D, rt, 0.0005D))
        Check("EV floor SHORT: exact equality does NOT bind",
              Not frmMainPageV2.IsChaseEvExhausted(59952D, 60000D, rt, 0.0005D))

        ' ---- Persistence round-trip for the EV keys (§6 acceptance 4) ----
        ' The REAL AppUserSettings.Save -> Load, through a real file. Save/Load resolve a fixed path
        ' beside the running exe, which for OrderCheck is its own bin - not the app's - so this
        ' cannot reach the owner's settings. Belt and braces anyway: if a file IS sitting there, its
        ' bytes are preserved (and mirrored to a .bak for the duration) and restored at the end.
        Dim evPath As String = AppUserSettings.SavePath
        Dim evBackup As String = If(File.Exists(evPath), File.ReadAllText(evPath), Nothing)
        If evBackup IsNot Nothing Then File.WriteAllText(evPath & ".ordercheck-bak", evBackup)
        Try
            Dim toSave As New AppUserSettings() With {
                .MinNetMovePct = 0.0005D, .MakerFeeBps = 1.5D, .TakerFeeBps = 3.5D}
            Dim saveErr As String = toSave.Save()
            Dim loadMsg As String = Nothing
            Dim reloaded As AppUserSettings = AppUserSettings.Load(loadMsg)
            Check("EV persistence: min_net_move_pct round-trips as the FRACTION 0.0005",
                  saveErr Is Nothing AndAlso reloaded.MinNetMovePct = 0.0005D,
                  $"saveErr='{saveErr}' got {reloaded.MinNetMovePct}")
            Check("EV persistence: the fee block round-trips (1.5 / 3.5 bps)",
                  reloaded.MakerFeeBps = 1.5D AndAlso reloaded.TakerFeeBps = 3.5D,
                  $"got {reloaded.MakerFeeBps}/{reloaded.TakerFeeBps}")
            Check("EV persistence: the round trip derived from the RELOADED maker key is 0.0003",
                  frmMainPageV2.RoundTripFeePctFromMakerBps(reloaded.MakerFeeBps) = 0.0003D)

            ' "Hand-editable" is the acceptance wording: the three keys must be plain, findable,
            ' top-level numbers in the file - not nested, not renamed by the writer.
            Dim raw As String = File.ReadAllText(evPath)
            Check("EV persistence: all three keys are present in the written file",
                  raw.Contains("""min_net_move_pct""") AndAlso raw.Contains("""maker_fee_bps""") AndAlso
                  raw.Contains("""taker_fee_bps"""))

            ' A hand-edit is read back verbatim (5 bps typed straight into the file), and the fee
            ' schedule is genuinely a one-number change.
            File.WriteAllText(evPath,
                "{ ""min_net_move_pct"": 0.0005, ""maker_fee_bps"": 2.0, ""taker_fee_bps"": 4.0 }")
            Dim handEdited As AppUserSettings = AppUserSettings.Load(loadMsg)
            Check("EV persistence: a hand-edited fee schedule is read back and re-derives (2.0 bps -> 0.0004)",
                  handEdited.MinNetMovePct = 0.0005D AndAlso handEdited.TakerFeeBps = 4D AndAlso
                  frmMainPageV2.RoundTripFeePctFromMakerBps(handEdited.MakerFeeBps) = 0.0004D)

            ' The repoint's acceptance 1: the comms default follows a hand-edited taker key end to
            ' end - file bytes -> Load -> the derivation HandleIndexUpdates runs. 4 bps of 64k = 25.6.
            Check("comms: a hand-edited taker key drives the default comms (4 bps at 64k -> 26)",
                  frmMainPageV2.DefaultCommsFromTakerBps(handEdited.TakerFeeBps, 64000D) = 26D,
                  $"got {frmMainPageV2.DefaultCommsFromTakerBps(handEdited.TakerFeeBps, 64000D)}")

            ' The ship-safe path: a file with none of the EV keys = OFF + the shipped schedule.
            File.WriteAllText(evPath, "{ ""amount"": 10 }")
            Dim bare As AppUserSettings = AppUserSettings.Load(loadMsg)
            Check("EV persistence: absent keys = 0 (OFF) + the 1.5/3.5 schedule",
                  bare.MinNetMovePct = 0D AndAlso bare.MakerFeeBps = 1.5D AndAlso bare.TakerFeeBps = 3.5D,
                  $"got {bare.MinNetMovePct} / {bare.MakerFeeBps} / {bare.TakerFeeBps}")
            Check("comms: an absent taker key still gives the shipped 3.5 bps default (22 at 64k)",
                  frmMainPageV2.DefaultCommsFromTakerBps(bare.TakerFeeBps, 64000D) = 22D)
        Catch ex As Exception
            Check("EV persistence fixture: no throw", False, ex.Message)
        Finally
            Try
                If evBackup Is Nothing Then
                    File.Delete(evPath)
                Else
                    File.WriteAllText(evPath, evBackup)
                    File.Delete(evPath & ".ordercheck-bak")
                End If
            Catch
            End Try
        End Try
        ' Assert the cleanup actually happened rather than assuming it (the harness lesson: verify
        ' what is in force, do not trust the write).
        Check("EV persistence: the fixture left the settings path as it found it",
              If(evBackup Is Nothing, Not File.Exists(evPath),
                 File.Exists(evPath) AndAlso File.ReadAllText(evPath) = evBackup))

        ' ============ SL backoff (spec-sl-backoff-coupling.md §Acceptance 5 + spec-sl-backoff-confirmed-reset.md) ============
        ' NextSlBackoff is the pure arithmetic behind the triggered-SL retry throttle. These fixtures
        ' exist because the runtime acceptance for this mechanism was twice found to be unobservable:
        ' they replace an eyeball test on testnet with a deterministic one. N1c EXTENDED them (last
        ' group below) rather than rewriting them - the oscillation group now guards against the
        ' defect's return instead of documenting it as current behaviour.
        Dim b0 As New DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)

        ' Backoff in ms, recovered from the stamp: the seam returns the STAMP (what the gate reads),
        ' so deriving the delay back out also pins the stamp formula itself.
        Dim BackoffMsOf = Function(failures As Integer) _
            (frmMainPageV2.NextSlBackoff(failures, b0).Stamp - b0).TotalMilliseconds + 333

        ' ---- the arithmetic: doubling from 666 ms, capped at 5 s, counter clamped at 8 ----
        Check("SL backoff: n=0 -> 1 failure, 666 ms (333 x 2^1)", BackoffMsOf(0) = 666,
              $"got {BackoffMsOf(0)}")
        Check("SL backoff: stamp = failedAt + backoffMs - 333 (one interval short, so the gate opens at failedAt + backoff)",
              frmMainPageV2.NextSlBackoff(0, b0).Stamp = b0.AddMilliseconds(333))
        Check("SL backoff: n=1 -> 2 failures, 1332 ms", BackoffMsOf(1) = 1332)
        Check("SL backoff: n=2 -> 3 failures, 2664 ms", BackoffMsOf(2) = 2664)
        Check("SL backoff: n=3 -> 4 failures, capped at 5000 ms (5328 would overshoot)", BackoffMsOf(3) = 5000)
        Check("SL backoff: the cap holds at every higher count", BackoffMsOf(7) = 5000)
        Check("SL backoff: counter clamps at 8", frmMainPageV2.NextSlBackoff(8, b0).Failures = 8)
        Check("SL backoff: the clamp holds the delay at the cap, not beyond",
              frmMainPageV2.NextSlBackoff(8, b0).Stamp = b0.AddMilliseconds(5000 - 333))

        ' ---- the acceptance-2 threshold: why the rate-limited line needs 2+ failures ----
        ' The chase's else-branch logs only when remainingMs > 1000, and remainingMs peaks at backoffMs.
        ' So the line is reachable iff the counter reaches 2 - the whole reason acceptance 2 was corrected.
        Check("SL backoff: 1 failure cannot print the rate-limited line (666 <= 1000)", BackoffMsOf(0) <= 1000)
        Check("SL backoff: 2 failures can (1332 > 1000)", BackoffMsOf(1) > 1000)

        ' ---- the DEFECT model: reset-per-attempt pins the counter at 0<->1 ----
        ' This was the chase path until N1c. Every chase attempt ran the optimistic reset (the send
        ' never throws, so the Try always completes), then exactly one rejection response incremented.
        ' N1c moved that reset to the confirmed echo; these three checks now GUARD AGAINST ITS RETURN -
        ' if a future change puts an unconditional reset back on the attempt, this is what the chase
        ' collapses to, and the comparison at the end of this section says so in one line.
        Dim oscCounter As Integer = 0
        Dim oscMaxMs As Double = 0
        Dim oscMaxCount As Integer = 0
        For attempt = 1 To 25
            oscCounter = 0                                          ' the attempt's optimistic reset
            Dim r = frmMainPageV2.NextSlBackoff(oscCounter, b0)     ' the one rejection it earns
            oscCounter = r.Failures
            oscMaxCount = Math.Max(oscMaxCount, oscCounter)
            oscMaxMs = Math.Max(oscMaxMs, (r.Stamp - b0).TotalMilliseconds + 333)
        Next
        Check("SL backoff: a per-attempt reset never exceeds 1 failure (the defect)", oscMaxCount = 1,
              $"got {oscMaxCount}")
        Check("SL backoff: a per-attempt reset never exceeds 666 ms - so it never escalates", oscMaxMs = 666,
              $"got {oscMaxMs}")
        Check("SL backoff: and therefore never reaches the 5 s cap from the chase alone", oscMaxMs < 5000)

        ' ---- a no-reset run DOES escalate ----
        ' True on the pump paths (the manual SL button and the trailing-SL loop increment with no reset
        ' between them), and - since N1c - it is what the chase path itself does between confirmations:
        ' every rejection compounds because nothing clears the counter until the exchange confirms.
        Dim escCounter As Integer = 0
        Dim escSeries As New List(Of Double)
        For hit = 1 To 5
            Dim r = frmMainPageV2.NextSlBackoff(escCounter, b0)
            escCounter = r.Failures
            escSeries.Add((r.Stamp - b0).TotalMilliseconds + 333)
        Next
        Check("SL backoff: consecutive failures with no reset escalate 666 -> 1332 -> 2664 -> 5000",
              escSeries(0) = 666 AndAlso escSeries(1) = 1332 AndAlso escSeries(2) = 2664 AndAlso escSeries(3) = 5000,
              String.Join(", ", escSeries))
        Check("SL backoff: escalation reaches the rate-limited line's threshold on the 2nd failure",
              escSeries(0) <= 1000 AndAlso escSeries(1) > 1000)
        Check("SL backoff: escalation saturates at the cap, it does not run away", escSeries(4) = 5000)

        ' ---- N1c: the chase's confirmed-reset state machine (docs/spec-sl-backoff-confirmed-reset.md) ----
        ' The counter now sees exactly two signals: a RED SL-edit rejection (the N1b coupling, modelled by
        ' NextSlBackoff) and a CONFIRMED echo - an open StopLossOrder echo carrying a price the app itself
        ' commanded, which clears it. The send completing is no longer a signal at all. Modelled here as the
        ' two transitions so the escalate-then-heal cycle is pinned without a trade or a testnet session.
        Dim cfCounter As Integer = 0
        Dim cfSeries As New List(Of Double)
        Dim ChaseRejected = Sub()
                                Dim r = frmMainPageV2.NextSlBackoff(cfCounter, b0)
                                cfCounter = r.Failures
                                cfSeries.Add((r.Stamp - b0).TotalMilliseconds + 333)
                            End Sub
        Dim ChaseConfirmed = Sub() cfCounter = 0

        ChaseRejected() : ChaseRejected() : ChaseRejected() : ChaseRejected()
        Check("SL backoff (N1c): the chase ALONE escalates 666 -> 1332 -> 2664 -> 5000",
              cfSeries(0) = 666 AndAlso cfSeries(1) = 1332 AndAlso cfSeries(2) = 2664 AndAlso cfSeries(3) = 5000,
              String.Join(", ", cfSeries))
        Check("SL backoff (N1c): the chase reaches 2 failures, so the rate-limited line is reachable from the chase path",
              cfCounter >= 2 AndAlso cfSeries(1) > 1000, $"counter {cfCounter}")

        ChaseConfirmed()
        Check("SL backoff (N1c): a confirmed echo clears the counter", cfCounter = 0)
        ChaseRejected()
        Check("SL backoff (N1c): the next failure restarts at 666, not at the cap", cfSeries(4) = 666,
              $"got {cfSeries(4)}")

        ' The accepted residual, stated as a fixture: a lost/unrecognised echo leaves the counter armed, so
        ' the next transient failure starts one step escalated - bounded by the cap, healed by any
        ' confirmation. Two rejections, no confirmation, then one more: 2664, not 666.
        cfCounter = 2
        cfSeries.Clear()
        ChaseRejected()
        Check("SL backoff (N1c): an un-reset counter starts the next failure escalated (accepted residual)",
              cfSeries(0) = 2664, $"got {cfSeries(0)}")
        ChaseConfirmed()
        cfSeries.Clear()
        ChaseRejected()
        Check("SL backoff (N1c): and one confirmation heals it back to the 666 ms first step", cfSeries(0) = 666)

        ' The one-line statement of what N1c changed: same four rejections, two reset policies.
        Check("SL backoff (N1c): moving the reset off the attempt is the whole delta - 666 ms before, 5000 ms cap now",
              oscMaxMs = 666 AndAlso escSeries(3) = 5000)

        ' =============================================================================================
        ' C1 - the v2 executor feedback emitter (docs/spec-c1-feedback-emitter.md section 5).
        ' Contract section 8 is the schema; these pin the things that fail SILENTLY.
        ' =============================================================================================

        ' ---- C1.0 config: absent / blank / set are THREE different answers ----
        ' Absent vs blank is the ships-OFF distinction, so it is pinned rather than assumed.
        Check("C1 config: an ABSENT key is fully inert (no path => no timer, no worker, no file)",
              ExecutorFeedback.ResolveOutputPath("", keyPresent:=False) = "")
        Check("C1 config: a PRESENT BUT BLANK key resolves to the section-8.2 default",
              ExecutorFeedback.ResolveOutputPath("   ", keyPresent:=True) = ExecutorFeedback.DefaultOutputPath)
        Check("C1 config: a present key is taken as given (trimmed)",
              ExecutorFeedback.ResolveOutputPath("  D:\fb.json  ", keyPresent:=True) = "D:\fb.json")

        ' ---- C1.1 mode mapping (E2): the enum's own names are NOT the pinned wire strings ----
        Check("C1 fixture 1: BridgeMode.Off -> ""OFF""",
              ExecutorFeedback.ModeWire(SignalBridge.BridgeMode.Off) = "OFF")
        Check("C1 fixture 1: BridgeMode.LogOnly -> ""LOG_ONLY""",
              ExecutorFeedback.ModeWire(SignalBridge.BridgeMode.LogOnly) = "LOG_ONLY")
        Check("C1 fixture 1: BridgeMode.Live -> ""LIVE""",
              ExecutorFeedback.ModeWire(SignalBridge.BridgeMode.Live) = "LIVE")
        ' THE defect E2 named, stated as a fixture: .ToString() emits "LogOnly", which violates the
        ' section-8.3 pin - and violates it invisibly, because the consumer's T8 tolerance renders an
        ' unknown value verbatim and takes the conservative arm without erroring.
        Check("C1 fixture 1: and .ToString() would NOT have satisfied the pin (this is E2)",
              SignalBridge.BridgeMode.LogOnly.ToString() = "LogOnly" AndAlso
              SignalBridge.BridgeMode.LogOnly.ToString() <> ExecutorFeedback.ModeWire(SignalBridge.BridgeMode.LogOnly))
        ' An unmapped enum value must never be able to read as LIVE.
        Check("C1 fixture 1: an unmapped enum value takes the conservative arm, never LIVE",
              ExecutorFeedback.ModeWire(CType(99, SignalBridge.BridgeMode)) = "OFF")

        ' ---- C1.2 THE FLAT TRAP - the fixture that would have caught spec section 3.2 ----
        ' A flat position with a NON-ZERO retained positionAvgEntry. The retention is deliberate and
        ' correct (close-P/L reads it); publishing it on a FLAT executor would hand the engine a
        ' stale fill to attribute to the NEXT signal, silently.
        Dim flat As ExecutorFeedback.FeedbackSnapshot =
            ExecutorFeedback.BuildSnapshot("exec-guid", SignalBridge.BridgeMode.Live, True, True, False, True,
                                           rawSizeUsd:=0D, rawAvgEntry:=59012.5D,
                                           rawWorkingStop:=58962.5D, rawWorkingTarget:=59095D,
                                           lastSignal:=Nothing)
        Check("C1 fixture 2: flat + RETAINED avg entry 59012.5 => direction FLAT",
              flat.Direction = "FLAT", flat.Direction)
        Check("C1 fixture 2: ...and avg_entry is ZEROED, not the retained basis",
              flat.AvgEntry = 0D, flat.AvgEntry.ToString(CultureInfo.InvariantCulture))
        Check("C1 fixture 2: ...and size_usd is 0",
              flat.SizeUsd = 0D)
        Check("C1 fixture 2: ...and BOTH working levels are 0 (contract 8.4: flat => FLAT + zeros)",
              flat.WorkingStop = 0D AndAlso flat.WorkingTarget = 0D)

        ' ---- C1.3 non-flat: direction/sign consistency BOTH WAYS (E3) ----
        Dim lng As ExecutorFeedback.FeedbackSnapshot =
            ExecutorFeedback.BuildSnapshot("g", SignalBridge.BridgeMode.Live, True, True, False, True,
                                           250D, 59012.5D, 58962.5D, 59095D, Nothing)
        Dim sht As ExecutorFeedback.FeedbackSnapshot =
            ExecutorFeedback.BuildSnapshot("g", SignalBridge.BridgeMode.Live, True, True, False, True,
                                           -250D, 59012.5D, 59062.5D, 58930D, Nothing)
        Check("C1 fixture 3: positive size => LONG, size preserved signed",
              lng.Direction = "LONG" AndAlso lng.SizeUsd = 250D)
        Check("C1 fixture 3: NEGATIVE size => SHORT (E3: the sign is established, not assumed)",
              sht.Direction = "SHORT" AndAlso sht.SizeUsd = -250D, sht.Direction)
        ' The section-8.4 guarantee itself, asserted as the invariant rather than as two cases:
        ' both are derived from the one signed value, so they cannot disagree.
        Check("C1 fixture 3: sign/direction consistency holds in both directions",
              (lng.SizeUsd > 0D) = (lng.Direction = "LONG") AndAlso
              (sht.SizeUsd < 0D) = (sht.Direction = "SHORT"))
        Check("C1 fixture 3: a non-flat snapshot keeps avg_entry and the working levels",
              lng.AvgEntry = 59012.5D AndAlso lng.WorkingStop = 58962.5D AndAlso lng.WorkingTarget = 59095D)

        ' ---- C1.4 last_signal: null, populated, and FROZEN across a post-acted chase abort ----
        Dim noSig As String = ExecutorFeedback.Serialize(flat, 1, New DateTime(2026, 8, 5, 9, 14, 2, DateTimeKind.Utc))
        Check("C1 fixture 4: last_signal is null before this process consumes its first payload",
              SignalBridge.ParsePayloadJson(noSig).SelectToken("last_signal").Type = JTokenType.Null)

        Dim acted As New ExecutorFeedback.LastSignalRef("9f0c-engine-guid", 1234, "acted (id 55)",
                                                        New DateTime(2026, 8, 5, 9, 13, 41, DateTimeKind.Utc))
        Dim atActed As ExecutorFeedback.FeedbackSnapshot =
            ExecutorFeedback.BuildSnapshot("g", SignalBridge.BridgeMode.Live, True, True, False, True,
                                           0D, 0D, 0D, 0D, acted)
        Dim actedJson As JObject = SignalBridge.ParsePayloadJson(ExecutorFeedback.Serialize(atActed, 2, DateTime.UtcNow))
        Check("C1 fixture 4: populated last_signal carries the ENGINE's (instance_id, signal_id) join key",
              actedJson.SelectToken("last_signal.instance_id").ToString() = "9f0c-engine-guid" AndAlso
              actedJson.SelectToken("last_signal.signal_id").ToObject(Of Long)() = 1234L)
        Check("C1 fixture 4: the disposition token passes through EXACTLY (soak-stable string)",
              actedJson.SelectToken("last_signal.disposition").ToString() = "acted (id 55)")

        ' The cardinality freeze, simulated: after `acted`, a chase abort moves the position and the
        ' working levels. It must NOT touch last_signal - the same rule that forbids a second
        ' disposition row. Structurally guaranteed here because only EmitDisposition writes the
        ' reference; this pins the consequence.
        Dim afterAbort As ExecutorFeedback.FeedbackSnapshot =
            ExecutorFeedback.BuildSnapshot("g", SignalBridge.BridgeMode.Live, True, True, False, True,
                                           0D, 0D, 0D, 0D, acted)
        Dim abortJson As JObject = SignalBridge.ParsePayloadJson(ExecutorFeedback.Serialize(afterAbort, 3, DateTime.UtcNow))
        Check("C1 fixture 4: a post-acted chase abort leaves last_signal UNCHANGED (cardinality freeze)",
              JToken.DeepEquals(actedJson.SelectToken("last_signal"), abortJson.SelectToken("last_signal")))

        ' ---- C1.5 the breaker_tripped seam: below / at / above, and 0 = no breaker ----
        Check("C1 fixture 5: BELOW the threshold is not tripped",
              Not SignalBridge.IsBreakerTripped(10D, -9.99D))
        Check("C1 fixture 5: AT the threshold IS tripped (the gate's <= semantics)",
              SignalBridge.IsBreakerTripped(10D, -10D))
        Check("C1 fixture 5: ABOVE the threshold is tripped",
              SignalBridge.IsBreakerTripped(10D, -10.01D))
        Check("C1 fixture 5: a POSITIVE session P/L is never tripped",
              Not SignalBridge.IsBreakerTripped(10D, 500D))
        ' breaker = 0 is the legitimate way to spell "off"; it must never be tripped, at any P/L.
        Check("C1 fixture 5: breaker 0 = no breaker, never tripped even at a huge loss",
              Not SignalBridge.IsBreakerTripped(0D, -100000D))
        Check("C1 fixture 5: a negative breaker is also 'off', never tripped",
              Not SignalBridge.IsBreakerTripped(-5D, -100000D))

        ' ---- C1.6 serialization pins (contract 8.1: v1's pins carry over) ----
        Dim pinSnap As ExecutorFeedback.FeedbackSnapshot =
            ExecutorFeedback.BuildSnapshot("exec-guid", SignalBridge.BridgeMode.LogOnly, True, False, False, False,
                                           -250D, 59012.5D, 59062.5D, 58930D, acted)
        Dim pinJson As String
        Dim savedCulture As CultureInfo = CultureInfo.CurrentCulture
        Try
            ' de-DE renders decimals with a COMMA. The v1 consumer-side culture bug (8956baa) is the
            ' reason this is pinned on the EMIT side too.
            CultureInfo.CurrentCulture = New CultureInfo("de-DE")
            pinJson = ExecutorFeedback.Serialize(pinSnap, 587, New DateTime(2026, 8, 5, 9, 14, 2, DateTimeKind.Utc))
        Finally
            CultureInfo.CurrentCulture = savedCulture
        End Try
        Check("C1 fixture 6: decimals are invariant-culture JSON numbers even under de-DE",
              pinJson.Contains("59012.5") AndAlso Not pinJson.Contains("59012,5"))
        ' The strongest form of this pin is on the RAW TEXT, because the bytes on disk are what the
        ' engine reads. Note the reader below is the app's own ParsePayloadJson, NOT JObject.Parse:
        ' Newtonsoft's default DateParseHandling turns an ISO-8601 STRING into a Date token that
        ' ToString() then renders in the CURRENT culture - the exact 8956baa failure, which showed up
        ' here first as a false FAIL of this very fixture ("5/8/2026 9:14:02 AM").
        Check("C1 fixture 6: the raw emitted text carries the ISO-8601 Z timestamp verbatim",
              pinJson.Contains("""generated_at_utc"": ""2026-08-05T09:14:02Z"""),
              pinJson.Substring(0, Math.Min(200, pinJson.Length)))
        Dim pin As JObject = SignalBridge.ParsePayloadJson(pinJson)
        Check("C1 fixture 6: generated_at_utc is ISO-8601 UTC with a literal Z",
              pin.SelectToken("generated_at_utc").ToString() = "2026-08-05T09:14:02Z",
              pin.SelectToken("generated_at_utc").ToString())
        Check("C1 fixture 6: at_utc inside last_signal takes the same format",
              pin.SelectToken("last_signal.at_utc").ToString() = "2026-08-05T09:13:41Z")
        Check("C1 fixture 6: schema_version is the FEEDBACK file's own counter, 1",
              pin.SelectToken("schema_version").ToObject(Of Integer)() = 1)
        Check("C1 fixture 6: feedback_id and the fixed identity fields are present",
              pin.SelectToken("feedback_id").ToObject(Of Long)() = 587L AndAlso
              pin.SelectToken("executor.app").ToString() = "DeribitOrderPlacementApp" AndAlso
              pin.SelectToken("instrument").ToString() = "BTC-PERPETUAL")
        Check("C1 fixture 6: executor.ws maps False -> DOWN (and never v1's 'REST')",
              pin.SelectToken("executor.ws").ToString() = "DOWN")
        ' Zeros-never-null for a SUPPRESSED numeric block: the flat snapshot's numbers are 0, not null.
        Dim flatJson As JObject = SignalBridge.ParsePayloadJson(noSig)
        Check("C1 fixture 6: zeros-never-null - a suppressed position block carries 0s, not nulls",
              flatJson.SelectToken("position.size_usd").Type = JTokenType.Integer OrElse
              flatJson.SelectToken("position.size_usd").Type = JTokenType.Float)
        Check("C1 fixture 6: ...and every suppressed numeric really is 0",
              flatJson.SelectToken("position.size_usd").ToObject(Of Decimal)() = 0D AndAlso
              flatJson.SelectToken("position.avg_entry").ToObject(Of Decimal)() = 0D AndAlso
              flatJson.SelectToken("position.working.stop").ToObject(Of Decimal)() = 0D AndAlso
              flatJson.SelectToken("position.working.target").ToObject(Of Decimal)() = 0D)
        ' ...but null (not {}) for an ABSENT OBJECT. The two rules differ and both are contract 8.1.
        Check("C1 fixture 6: null - not an empty object - for an absent last_signal",
              flatJson.SelectToken("last_signal").Type = JTokenType.Null)

        ' ---- C1.7 the coalescing seam: LAST WINS, and a burst collapses to ONE write ----
        Dim slot As New ExecutorFeedback.CoalescingSlot()
        Dim s1 = ExecutorFeedback.BuildSnapshot("g", SignalBridge.BridgeMode.Live, True, True, False, True,
                                                10D, 100D, 90D, 110D, Nothing)
        Dim s2 = ExecutorFeedback.BuildSnapshot("g", SignalBridge.BridgeMode.Live, True, True, False, True,
                                                20D, 100D, 90D, 110D, Nothing)
        Dim s3 = ExecutorFeedback.BuildSnapshot("g", SignalBridge.BridgeMode.Live, True, True, False, True,
                                                30D, 100D, 90D, 110D, Nothing)
        slot.Offer(s1) : slot.Offer(s2) : slot.Offer(s3)
        Dim taken As ExecutorFeedback.FeedbackSnapshot = Nothing
        Check("C1 fixture 7: three snapshots in, the NEWEST out (last-wins, not drop-the-newest)",
              slot.TryTake(taken) AndAlso taken.SizeUsd = 30D, taken.SizeUsd.ToString(CultureInfo.InvariantCulture))
        Check("C1 fixture 7: ...and the burst collapsed to ONE write, not three",
              Not slot.HasPending AndAlso Not slot.TryTake(taken))
        slot.Offer(s1)
        slot.Clear()
        Check("C1 fixture 7: Clear drops the queue (the graceful-close write supersedes it)",
              Not slot.HasPending)

        ' ---- C1.8 TRIGGER COMPLETENESS, ASSERTED PER FIELD, OVER THE WHOLE SECTION-8.3 DOMAIN ----
        '
        ' This is what makes section 4 (d)'s heartbeat rule safe. Under that rule the heartbeat
        ' REPUBLISHES rather than re-reads, so a fresh generated_at_utc proves only that the process
        ' is alive - every field is as of its last TRIGGERING event. Trigger completeness is
        ' therefore the only thing bounding any individual field's staleness, and a field the
        ' publish gate swallows is stale FOREVER rather than for 10 s.
        '
        ' AMENDED 2026-08-03 (owner, with the E6 ruling). This fixture was originally scoped to "the
        ' four snapshot fields", and THAT SCOPING IS EXACTLY WHY IT COULD NOT HAVE CAUGHT E6a:
        ' executor.ws is a section-8.3 field outside the four. It now enumerates the SCHEMA. Same
        ' "enumerate the domain, not the keys" lesson as the unconfigured NY session bucket
        ' (H-6 section 7 lesson 6), applied to a fixture instead of a config map.
        '
        ' Three assertions per field, because any one alone is a false pass: the mutation must
        ' survive the content gate (SameContent), reach the wire (Serialize), and survive coalescing
        ' behind an older snapshot.
        Dim baseSnap As ExecutorFeedback.FeedbackSnapshot = Snap()
        Dim baseJson As String = SnapJson(baseSnap)
        Dim otherSig As New ExecutorFeedback.LastSignalRef("9f0c-engine-guid", 1234, "acted (id 55)",
                                                           New DateTime(2026, 8, 5, 9, 13, 41, DateTimeKind.Utc))

        ' EVERY mutable field of the section-8.3 document, one entry per field, each varying that
        ' field ALONE. The immutable remainder is enumerated separately below, so the domain is
        ' covered rather than sampled.
        Dim mutations As New List(Of (name As String, snap As ExecutorFeedback.FeedbackSnapshot)) From {
            ("executor.instance_id", Snap(instanceId:="a-different-guid")),
            ("executor.mode", Snap(mode:=SignalBridge.BridgeMode.LogOnly)),
            ("executor.armed", Snap(armed:=False)),
            ("executor.started", Snap(started:=False)),
            ("executor.breaker_tripped", Snap(breakerTripped:=True)),
            ("executor.ws  <- THE E6a FIELD", Snap(wsConnected:=False)),
            ("position.size_usd", Snap(sizeUsd:=260D)),
            ("position.avg_entry", Snap(avgEntry:=59013.5D)),
            ("position.working.stop", Snap(workingStop:=58963.5D)),
            ("position.working.target", Snap(workingTarget:=59096D)),
            ("last_signal (null -> populated)", Snap(lastSignal:=otherSig)),
            ("last_signal.instance_id", Snap(lastSignal:=New ExecutorFeedback.LastSignalRef(
                 "OTHER-engine", 1234, "acted (id 55)", New DateTime(2026, 8, 5, 9, 13, 41, DateTimeKind.Utc)))),
            ("last_signal.signal_id", Snap(lastSignal:=New ExecutorFeedback.LastSignalRef(
                 "9f0c-engine-guid", 1235, "acted (id 55)", New DateTime(2026, 8, 5, 9, 13, 41, DateTimeKind.Utc)))),
            ("last_signal.disposition", Snap(lastSignal:=New ExecutorFeedback.LastSignalRef(
                 "9f0c-engine-guid", 1234, "rejected: cancel pending", New DateTime(2026, 8, 5, 9, 13, 41, DateTimeKind.Utc)))),
            ("last_signal.at_utc", Snap(lastSignal:=New ExecutorFeedback.LastSignalRef(
                 "9f0c-engine-guid", 1234, "acted (id 55)", New DateTime(2026, 8, 5, 9, 13, 42, DateTimeKind.Utc))))
        }

        ' The last four vary a member of last_signal against the POPULATED baseline, not the null
        ' one - otherwise they would all pass merely by being non-null and prove nothing about the
        ' member. SameLastSignal compares four members and could swallow any one of them.
        Dim sigBase As ExecutorFeedback.FeedbackSnapshot = Snap(lastSignal:=otherSig)

        For Each m In mutations
            Dim ref As ExecutorFeedback.FeedbackSnapshot =
                If(m.name.StartsWith("last_signal.", StringComparison.Ordinal), sigBase, baseSnap)
            Check($"C1 fixture 8: {m.name} - mutating it alone is NOT content-equal (the gate cannot swallow it)",
                  Not ExecutorFeedback.SameContent(ref, m.snap))
            Check($"C1 fixture 8: {m.name} - ...and it changes the emitted document",
                  SnapJson(m.snap) <> SnapJson(ref))
            Dim burst As New ExecutorFeedback.CoalescingSlot()
            Dim got As ExecutorFeedback.FeedbackSnapshot = Nothing
            burst.Offer(ref) : burst.Offer(m.snap)
            Check($"C1 fixture 8: {m.name} - ...and it survives coalescing behind an older snapshot",
                  burst.TryTake(got) AndAlso ExecutorFeedback.SameContent(got, m.snap))
        Next

        ' position.direction is DERIVED from size_usd's sign, so it cannot be varied independently -
        ' that derivation is what makes contract 8.4's sign/direction consistency structural, and
        ' fixture 3 owns it. What belongs here is that the derived value is itself on the gate.
        Check("C1 fixture 8: position.direction - LONG vs SHORT at equal magnitude is a change",
              Not ExecutorFeedback.SameContent(Snap(sizeUsd:=250D), Snap(sizeUsd:=-250D)))
        Check("C1 fixture 8: position.direction - non-flat vs FLAT is a change",
              Not ExecutorFeedback.SameContent(baseSnap, Snap(sizeUsd:=0D)))

        ' The gate's other half: an unchanged snapshot must publish NOTHING, or every quote tick
        ' would rewrite the file. Both halves matter - this is why the heartbeat bypasses the gate.
        Check("C1 fixture 8: a content-identical snapshot is NOT a change (no per-tick rewrites)",
              ExecutorFeedback.SameContent(baseSnap, baseSnap) AndAlso
              ExecutorFeedback.SameContent(baseSnap, Snap()))

        ' ---- C1.8b THE DOMAIN CHECK - what would have caught E6a's SHAPE ----
        ' The emitted leaf paths, pinned as a set. A new section-8.3 field cannot be added without
        ' failing this, which forces whoever adds it to give it a per-field assertion above - and
        ' therefore to ask whether it has a TRIGGER. Enumerating the schema is the whole point:
        ' fixture 8 could list every field it knew about and still miss the one nobody listed.
        Dim expectedPopulated As New List(Of String) From {
            "executor.app", "executor.armed", "executor.breaker_tripped", "executor.instance_id",
            "executor.mode", "executor.started", "executor.ws",
            "feedback_id", "generated_at_utc", "instrument",
            "last_signal.at_utc", "last_signal.disposition", "last_signal.instance_id", "last_signal.signal_id",
            "position.avg_entry", "position.direction", "position.size_usd",
            "position.working.stop", "position.working.target", "schema_version"
        }
        expectedPopulated.Sort(StringComparer.Ordinal)
        Dim actualPopulated As List(Of String) = LeafPaths(SnapJson(sigBase))
        Check("C1 fixture 8b: the emitted document carries EXACTLY the section-8.3 field set (populated)",
              actualPopulated.SequenceEqual(expectedPopulated),
              $"got [{String.Join(", ", actualPopulated)}]")

        ' The null-last_signal shape is a different document and is pinned separately: the four
        ' last_signal members collapse to one null leaf, and nothing else may move.
        Dim expectedNull As New List(Of String) From {
            "executor.app", "executor.armed", "executor.breaker_tripped", "executor.instance_id",
            "executor.mode", "executor.started", "executor.ws",
            "feedback_id", "generated_at_utc", "instrument", "last_signal",
            "position.avg_entry", "position.direction", "position.size_usd",
            "position.working.stop", "position.working.target", "schema_version"
        }
        expectedNull.Sort(StringComparer.Ordinal)
        Check("C1 fixture 8b: ...and exactly that set with last_signal null before first consumption",
              LeafPaths(baseJson).SequenceEqual(expectedNull),
              $"got [{String.Join(", ", LeafPaths(baseJson))}]")

        ' The immutable remainder of the domain, named so the enumeration above is complete rather
        ' than merely long. These three are constants and have no trigger BECAUSE they cannot
        ' change within a process - which is a different thing from E6a's "changes with no trigger".
        Check("C1 fixture 8b: schema_version, executor.app and instrument are per-build constants",
              ExecutorFeedback.SchemaVersion = 1 AndAlso
              ExecutorFeedback.AppName = "DeribitOrderPlacementApp" AndAlso
              ExecutorFeedback.Instrument = "BTC-PERPETUAL")
        ' feedback_id and generated_at_utc are deliberately OUTSIDE the content gate: they are
        ' assigned per publish, and the heartbeat's whole job is to move them while nothing else
        ' moves. If they were inside SameContent the heartbeat would be a no-op by construction.
        Check("C1 fixture 8b: feedback_id and generated_at_utc are excluded from the content gate by design",
              ExecutorFeedback.SameContent(baseSnap, Snap()) AndAlso
              ExecutorFeedback.Serialize(baseSnap, 1, PinnedAt) <>
              ExecutorFeedback.Serialize(baseSnap, 2, PinnedAt.AddSeconds(10)))

        ' ---- C1.9 D1: the write-admission seam (review 2026-08-05) ----
        '
        ' ⚠ READ THIS BEFORE TRUSTING THESE FOUR LINES. They pin the RULE, not the RACE. The defect
        ' was an ordering window - DrainQueue takes its id under _gate and then acquires _writeGate
        ' separately, so a worker pre-empted in between could resume after the graceful-close write
        ' and overwrite it with an older snapshot and a LOWER feedback_id. Reproducing that needs
        ' two threads and a controlled pre-emption, which OrderCheck cannot do and should not
        ' pretend to. What is pinned here is the predicate the fix turns on; the ordering itself is
        ' covered by reading WriteAtomic, and by acceptance 7.1.
        Check("C1 fixture 9: a normal worker write proceeds while the emitter is live",
              ExecutorFeedback.ShouldWrite(isFinal:=False, disposed:=False))
        ' THE D1 LINE: a worker that wakes up after shutdown must not write.
        Check("C1 fixture 9: a LATE worker write is refused once disposed (this is D1)",
              Not ExecutorFeedback.ShouldWrite(isFinal:=False, disposed:=True))
        ' The final write always proceeds - it is the write that LATCHES disposed, so gating it on
        ' disposed would gate it on itself and trigger (e) would never land.
        Check("C1 fixture 9: the final write proceeds with disposed already latched",
              ExecutorFeedback.ShouldWrite(isFinal:=True, disposed:=True))
        Check("C1 fixture 9: ...and also on a live emitter, so (e) is unconditional",
              ExecutorFeedback.ShouldWrite(isFinal:=True, disposed:=False))

        ' ---- ws-edge audit: the pure seam (docs/spec-ws-edge-audit.md, acceptance 1) ----
        '
        ' THIS IS THE ONLY ACCEPTANCE THAT DOES NOT NEED A DISCONNECT, so it carries the logic. The
        ' file write and the gray host-log line are NOT exercised here: they need an app, and the
        ' spec's own acceptance 2 covers them with a real network drop. What is pinned here is the
        ' rule that decides whether a row is written at all.
        Dim wsOk As String = WsEdgeLog.StateWire(wsConnected:=True)
        Dim wsDown As String = WsEdgeLog.StateWire(wsConnected:=False)

        ' The tokens are the EMITTER's, not a re-typing of them. If these ever diverge, this log stops
        ' being evidence about executor.ws and becomes evidence about something else with the same
        ' name - E2's failure mode exactly.
        Check("ws-edge fixture 1: the wire tokens ARE ExecutorFeedback's own OK / DOWN",
              wsOk = ExecutorFeedback.WsOk AndAlso wsDown = ExecutorFeedback.WsDown AndAlso
              wsOk = "OK" AndAlso wsDown = "DOWN")

        ' The five cases the spec names, in the order it names them.
        Check("ws-edge fixture 2: the FIRST-EVER call has no prior state and IS logged (session opening state)",
              WsEdgeLog.ShouldLogWsEdge("", wsOk) AndAlso WsEdgeLog.ShouldLogWsEdge(Nothing, wsDown))
        Check("ws-edge fixture 3: OK -> OK is SUPPRESSED (this is the no-flood rule)",
              Not WsEdgeLog.ShouldLogWsEdge(wsOk, wsOk))
        Check("ws-edge fixture 4: OK -> DOWN is LOGGED (the edge the spec exists for)",
              WsEdgeLog.ShouldLogWsEdge(wsOk, wsDown))
        Check("ws-edge fixture 5: DOWN -> DOWN is SUPPRESSED (a failed reconnect re-enters the loop exit)",
              Not WsEdgeLog.ShouldLogWsEdge(wsDown, wsDown))
        Check("ws-edge fixture 6: DOWN -> OK is LOGGED (the recovery half of the transition)",
              WsEdgeLog.ShouldLogWsEdge(wsDown, wsOk))

        ' An unknown CURRENT state is never a row: a blank row is worse than no row, because it would
        ' read as an edge in an audit trail whose whole value is that every row is one.
        Check("ws-edge fixture 7: an unknown current state is never logged, whatever preceded it",
              Not WsEdgeLog.ShouldLogWsEdge("", "") AndAlso
              Not WsEdgeLog.ShouldLogWsEdge(wsOk, Nothing) AndAlso
              Not WsEdgeLog.ShouldLogWsEdge(wsDown, ""))

        ' Comparison is ORDINAL. Nothing should ever produce "ok", but a case-insensitive compare here
        ' would silently accept a token that the feedback file would not, and the two artefacts must
        ' agree exactly for the join to mean anything.
        Check("ws-edge fixture 8: the comparison is ordinal - a differently-cased token is a change, not a match",
              WsEdgeLog.ShouldLogWsEdge("ok", wsOk))

        ' ---- the no-flood proof, in pure form (spec acceptance 3, without the five-minute wait) ----
        ' Drive a realistic heartbeat sequence through the rule while keeping the field the way the
        ' writer keeps it. 30 publishes of an unchanged state must decide to log ZERO times; the same
        ' sequence with one disconnect and one recovery must decide to log EXACTLY twice.
        Dim decisions As Integer = 0
        Dim last As String = wsOk                     ' the connect row has already been written
        For i As Integer = 1 To 30
            If WsEdgeLog.ShouldLogWsEdge(last, wsOk) Then
                decisions += 1
                last = wsOk
            End If
        Next
        Check("ws-edge fixture 9: 30 heartbeat republishes of an unchanged state decide to log ZERO rows",
              decisions = 0, $"got {decisions}")

        decisions = 0
        Dim rows As New List(Of String)
        Dim at As New DateTime(2026, 8, 13, 13, 22, 41, DateTimeKind.Utc)
        ' connected x5, then the drop, then five failed reconnects that re-enter the loop exit, then
        ' recovery, then connected x5. Exactly two rows, DOWN then OK.
        Dim sequence As String() = {wsOk, wsOk, wsOk, wsOk, wsOk,
                                    wsDown, wsDown, wsDown, wsDown, wsDown, wsDown,
                                    wsOk, wsOk, wsOk, wsOk, wsOk}
        last = wsOk
        For i As Integer = 0 To sequence.Length - 1
            If WsEdgeLog.ShouldLogWsEdge(last, sequence(i)) Then
                decisions += 1
                last = sequence(i)
                rows.Add(WsEdgeLog.FormatRow(at.AddSeconds(i), sequence(i), "fixture"))
            End If
        Next
        Check("ws-edge fixture 10: one disconnect and one recovery decide to log EXACTLY two rows, DOWN then OK",
              decisions = 2 AndAlso rows.Count = 2 AndAlso
              rows(0) = "2026-08-13T13:22:46Z | DOWN | fixture" AndAlso
              rows(1) = "2026-08-13T13:22:52Z | OK | fixture",
              $"got [{String.Join("  //  ", rows)}]")

        ' ---- the row format ----
        Check("ws-edge fixture 11: a row is utc | state | reason, ISO-8601 with a literal Z",
              WsEdgeLog.FormatRow(at, wsDown, "server closed connection") =
              "2026-08-13T13:22:41Z | DOWN | server closed connection")

        ' Same reason as fixture 1 above: the 8956baa culture bug. This app runs on a d/M/yyyy machine
        ' and an audit row that sorts differently per machine is not an audit row.
        Dim savedWs As CultureInfo = CultureInfo.CurrentCulture
        Try
            For Each cultureName As String In {"en-MY", "de-DE", "en-GB", "en-US"}
                CultureInfo.CurrentCulture = New CultureInfo(cultureName)
                Check($"ws-edge fixture 12: the row is invariant-culture under {cultureName}",
                      WsEdgeLog.FormatRow(at, wsDown, "x") = "2026-08-13T13:22:41Z | DOWN | x")
            Next
        Finally
            CultureInfo.CurrentCulture = savedWs
        End Try

        ' A local DateTime must still render as UTC - the writer stamps DateTime.UtcNow, but nothing
        ' in the signature stops a caller passing a local one, and a row in local time would silently
        ' misdate the incident by the machine's offset.
        Check("ws-edge fixture 13: a Local timestamp is converted to UTC, never rendered verbatim",
              WsEdgeLog.FormatRow(at.ToLocalTime(), wsOk, "x") = "2026-08-13T13:22:41Z | OK | x")

        ' ONE ROW PER EDGE is this file's whole contract, and the reason field is the one thing that
        ' can break it: it carries exception messages, and those can contain line breaks.
        ' Each control character becomes ONE space, so a CRLF becomes TWO - stated rather than
        ' counted, because the first version of this fixture asserted one and failed.
        Check("ws-edge fixture 14a: a bare LF in the reason becomes one space",
              WsEdgeLog.FlattenReason("a" & vbLf & "b") = "a b")
        Check("ws-edge fixture 14b: a CRLF becomes two spaces - one per control character",
              WsEdgeLog.FlattenReason("a" & Environment.NewLine & "b") = "a  b")
        Check("ws-edge fixture 14c: a trailing newline is trimmed, not left as trailing whitespace",
              WsEdgeLog.FlattenReason("boom" & Environment.NewLine) = "boom")
        Check("ws-edge fixture 14d: ...so a multi-line exception message can never split into two rows",
              Not WsEdgeLog.FormatRow(at, wsDown, "receive error:" & Environment.NewLine & "  broken pipe").Contains(vbLf) AndAlso
              Not WsEdgeLog.FormatRow(at, wsDown, "receive error:" & Environment.NewLine & "  broken pipe").Contains(vbCr))
        Check("ws-edge fixture 15: a missing reason yields an empty third field, not a missing one",
              WsEdgeLog.FormatRow(at, wsDown, Nothing) = "2026-08-13T13:22:41Z | DOWN | " AndAlso
              WsEdgeLog.FormatRow(at, wsDown, Nothing).Split({WsEdgeLog.FieldSeparator}, StringSplitOptions.None).Length = 3)

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
