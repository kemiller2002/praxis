namespace Praxis.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Praxis.Domain.Pacing
open Praxis.Application.Pacing
open Praxis.Infrastructure.Pacing
open Praxis.Cli

/// Pacing hardening (PRX-QUAL-003/004/005/008/011): per-window Stale and
/// Unsupported coverage, hard-hold creation evidence, typed provider/model/
/// bucket identities, typed telemetry events, and adversarial adapter cases
/// driven through seams. No test uses the network, a real credential store
/// or a real provider process.
module PacingHardeningTests =
    let private t name run = { Name = $"pacing hardening: {name}"; Run = run }

    let private now = DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.Zero)
    let private week = TimeSpan.FromDays 7.0
    let private family name = (ModelFamily.tryCreate name).Value
    let private codexAdapter = PacingAdapterRules.codexInfo (QuotaBucket "codex")

    let private window key used duration (reset: DateTimeOffset) =
        { Provider = ProviderId.Codex
          Key = key
          Label = key
          Scope = QuotaScope.Global
          UsedPercent = used
          Duration = duration
          ResetsAt = reset
          ObservedAt = now }

    let private session used = window "session" used (TimeSpan.FromHours 5.0) (now + TimeSpan.FromHours 1.0)

    let private weekly leadHours =
        let reset = now + TimeSpan.FromHours 84.0
        let elapsed = week - (reset - now)
        window "weekly" (decimal ((elapsed.TotalHours + leadHours) / week.TotalHours * 100.0)) week reset

    let private request state windows freshness =
        { Now = now
          Provider = ProviderId.Codex
          Model = ModelIdentity.Unspecified
          Windows = windows
          Freshness = freshness
          Existing = state
          StateIntegrity = StateIntegrity.Intact
          Override = false }

    let private evaluate state windows = Pacing.evaluate PacingPolicy.defaults (request state windows ObservationFreshness.Fresh)

    let private snapshotOf windows freshness =
        { Provider = ProviderId.Codex
          Adapter = codexAdapter
          ObservedAt = now
          Windows = windows
          Coverage = []
          Freshness = freshness }

    let private statusOf (reading: QuotaReading) key =
        (reading.Coverage |> List.find (fun observation -> observation.Key = key)).Status

    let private withDirectory (run: string -> unit) =
        let directory = Path.Combine(Path.GetTempPath(), "praxis-pacing-hardening-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory directory |> ignore

        try
            run directory
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "src", "Praxis.Domain")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private root () = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))

    let private ok result =
        match result with
        | Ok value -> value
        | Error message -> failwith $"expected Ok, got Error {message}"

    let private expectError (fragment: string) result =
        match result with
        | Ok value -> failwith $"expected an error containing '{fragment}', got Ok {value}"
        | Error(message: string) ->
            Assert.isTrue (message.Contains(fragment, StringComparison.Ordinal)) $"error '{message}' should mention '{fragment}'"

    // ---- PRX-QUAL-004: per-window observation status -------------------------

    let private coverageTests =
        [ t "a Codex window that already reset is stale, not observed" (fun () ->
              let json =
                  """{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1759500000},"secondary":{"usedPercent":55,"windowDurationMins":10080,"resetsAt":1791655200}}}}"""

              let reading = PacingNormalization.normalizeCodexResult (QuotaBucket "codex") now json |> ok

              match statusOf reading "codex:primary" with
              | WindowStatus.Stale reason -> Assert.isTrue (reason.Contains "reset") "the stale reason names the reset"
              | other -> failwith $"expected Stale, got {other}"

              Assert.isTrue (not (WindowObservation.isComplete reading.Coverage)) "a stale window makes the reading incomplete"
              Assert.equal 1 reading.Windows.Length)

          t "an unknown window-shaped Codex slot is unsupported, not ignored" (fun () ->
              let json =
                  """{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1791050400},"secondary":{"usedPercent":55,"windowDurationMins":10080,"resetsAt":1791655200},"tertiary":{"usedPercent":5,"windowDurationMins":43200,"resetsAt":1793000000},"credits":{"hasCredits":false}}}}"""

              let reading = PacingNormalization.normalizeCodexResult (QuotaBucket "codex") now json |> ok

              match statusOf reading "codex:tertiary" with
              | WindowStatus.Unsupported reason -> Assert.isTrue (reason.Contains PacingAdapterRules.CodexRulesVersion) "drift names the rules version"
              | other -> failwith $"expected Unsupported, got {other}"

              Assert.isTrue (reading.Coverage |> List.forall (fun observation -> observation.Key <> "codex:credits")) "non-window members are not windows"
              Assert.isTrue (not (WindowObservation.isComplete reading.Coverage)) "response drift is incomplete evidence")

          t "an unknown Claude usage window and limit kind are unsupported" (fun () ->
              let json =
                  """{"five_hour":{"utilization":20,"resets_at":"2026-10-03T20:00:00Z"},"seven_day":{"utilization":40,"resets_at":"2026-10-08T17:00:00Z"},"seven_day_quantum":{"utilization":10,"resets_at":"2026-10-08T17:00:00Z"},"seven_day_opus":null,"limits":[{"kind":"monthly_all","percent":3,"resets_at":"2026-10-30T00:00:00Z","scope":null}]}"""

              let reading = PacingNormalization.normalizeClaudeUsage now [] json |> ok

              match statusOf reading "seven_day_quantum", statusOf reading "limit:monthly_all" with
              | WindowStatus.Unsupported _, WindowStatus.Unsupported _ -> ()
              | other -> failwith $"expected two unsupported windows, got {other}"

              let diagnostic = WindowObservation.incompleteness reading.Coverage
              Assert.isTrue (diagnostic.Contains "seven_day_quantum unsupported") "the diagnostic names the drifted window"
              Assert.isTrue (reading.Coverage |> List.forall (fun observation -> observation.Key <> "seven_day_opus")) "a null window is not reported")

          t "the Claude scoped top-level windows map through the rule table" (fun () ->
              let json =
                  """{"five_hour":{"utilization":20,"resets_at":"2026-10-03T20:00:00Z"},"seven_day":{"utilization":40,"resets_at":"2026-10-08T17:00:00Z"},"seven_day_opus":{"utilization":90,"resets_at":"2026-10-08T17:00:00Z"},"seven_day_oauth_apps":{"utilization":1,"resets_at":"2026-10-08T17:00:00Z"}}"""

              let reading = PacingNormalization.normalizeClaudeUsage now [] json |> ok
              Assert.isTrue (WindowObservation.isComplete reading.Coverage) "known scoped windows are observed"
              let opus = reading.Windows |> List.find (fun window -> window.Key = "seven_day_opus")
              Assert.equal (QuotaScope.Model(family "opus")) opus.Scope
              let apps = reading.Windows |> List.find (fun window -> window.Key = "seven_day_oauth_apps")
              Assert.equal (QuotaScope.Surface "oauth_apps") apps.Scope)

          t "a Claude model scope that names no single family is unsupported" (fun () ->
              let json =
                  """{"five_hour":{"utilization":20,"resets_at":"2026-10-03T20:00:00Z"},"seven_day":{"utilization":40,"resets_at":"2026-10-08T17:00:00Z"},"limits":[{"kind":"weekly_scoped","percent":75,"resets_at":"2026-10-08T17:00:00Z","scope":{"model":{"display_name":"Opus and Sonnet"}}},{"kind":"weekly_scoped","percent":5,"resets_at":"2026-10-08T17:00:00Z","scope":{"model":{"display_name":"Claude Opus 5"}}}]}"""

              let reading = PacingNormalization.normalizeClaudeUsage now [] json |> ok

              match statusOf reading "seven_day:Opus and Sonnet" with
              | WindowStatus.Unsupported _ -> ()
              | other -> failwith $"expected Unsupported, got {other}"

              let named = reading.Windows |> List.find (fun window -> window.Key = "seven_day:Claude Opus 5")
              Assert.equal (QuotaScope.Model(family "opus")) named.Scope)

          t "carried hard windows are reported stale in coverage" (fun () ->
              let previous = [ session 99m ]
              let current = { Windows = [ weekly 0.0 ]; Coverage = [ { Key = "codex:primary"; Status = WindowStatus.Missing } ] }
              let carried = PacingAdapters.carryMissingHard previous current now
              Assert.equal 2 carried.Windows.Length

              match (carried.Coverage |> List.find (fun observation -> observation.Key = "session")).Status with
              | WindowStatus.Stale reason -> Assert.isTrue (reason.Contains "carried") "carried evidence says so"
              | other -> failwith $"expected Stale, got {other}")

          t "status JSON projects stale and unsupported coverage codes" (fun () ->
              let snapshot =
                  { snapshotOf [ weekly 0.0 ] (ObservationFreshness.Stale "incomplete") with
                      Coverage =
                          [ { Key = "a"; Status = WindowStatus.Stale "carried" }
                            { Key = "b"; Status = WindowStatus.Unsupported "drift" } ] }

              let decision = evaluate PacingState.empty []
              let status = PacingStatusProjection.create now "/tmp/p" ProviderId.Codex ModelIdentity.Unspecified false snapshot StateIntegrity.Intact decision None
              use document = JsonDocument.Parse(PacingStatusDocument.renderJson status)

              let codes =
                  document.RootElement.GetProperty("coverage").EnumerateArray()
                  |> Seq.map (fun row -> row.GetProperty("status").GetString(), row.GetProperty("reason").GetString())
                  |> Seq.toList

              Assert.equal [ "stale", "carried"; "unsupported", "drift" ] codes) ]

    // ---- PRX-QUAL-003: hard-hold creation evidence ---------------------------

    let private evidenceTests =
        [ t "a hard hold records the reading that created it" (fun () ->
              let hold = (evaluate PacingState.empty [ session 99m ]).State.Holds.Values |> Seq.exactlyOne
              Assert.equal (HoldEvidence.Observed(now, QuotaWindow.reference (session 99m))) hold.Evidence
              Assert.equal "codex/session@2026-10-03T17:00:00.0000000+00:00" (QuotaWindow.reference (session 99m)))

          t "a refreshed hard hold keeps its original creation evidence" (fun () ->
              let established = (evaluate PacingState.empty [ session 99m ]).State
              let later = { session 99.5m with ObservedAt = now + TimeSpan.FromMinutes 5.0 }

              let refreshed =
                  Pacing.evaluate PacingPolicy.defaults { request established [ later ] ObservationFreshness.Fresh with Now = now + TimeSpan.FromMinutes 5.0 }

              let hold = refreshed.State.Holds.Values |> Seq.exactlyOne
              Assert.equal (HoldEvidence.Observed(now, QuotaWindow.reference (session 99m))) hold.Evidence
              Assert.equal (HoldBasis.HardLimit(99.5m, now + TimeSpan.FromHours 1.0)) hold.Basis)

          t "creation evidence round-trips through hold.json" (fun () ->
              let state = (evaluate PacingState.empty [ session 99m; weekly 9.0 ]).State

              match PacingStateDocument.parse (PacingStateDocument.serialize state 4L) with
              | PacingStateRead.Current(parsed, 4L) -> Assert.equal state parsed
              | other -> failwith $"expected current state, got {other}")

          t "a hold persisted without evidence reads as unrecorded, never invented" (fun () ->
              let document =
                  """{"schemaVersion":2,"revision":1,"holds":[{"key":"codex/session","provider":"codex","scopeKind":"global","since":"2026-10-03T17:00:00Z","basis":"hard-limit","usedPercent":99,"resetsAt":"2026-10-03T18:00:00Z"}]}"""

              match PacingStateDocument.parse document with
              | PacingStateRead.Current(state, _) -> Assert.equal HoldEvidence.Unrecorded (state.Holds.Values |> Seq.exactlyOne).Evidence
              | other -> failwith $"expected current state, got {other}")

          t "malformed creation evidence makes the document unreadable" (fun () ->
              let document =
                  """{"schemaVersion":2,"revision":1,"holds":[{"key":"codex/session","provider":"codex","scopeKind":"global","since":"2026-10-03T17:00:00Z","basis":"hard-limit","usedPercent":99,"resetsAt":"2026-10-03T18:00:00Z","evidence":{"reference":""}}]}"""

              match PacingStateDocument.parse document with
              | PacingStateRead.Unreadable _ -> ()
              | other -> failwith $"expected Unreadable, got {other}")

          t "a hold for an unknown provider makes the document unreadable" (fun () ->
              let document =
                  """{"schemaVersion":2,"revision":1,"holds":[{"key":"x/session","provider":"gemini","scopeKind":"global","since":"2026-10-03T17:00:00Z","basis":"hard-limit","usedPercent":99,"resetsAt":"2026-10-03T18:00:00Z"}]}"""

              match PacingStateDocument.parse document with
              | PacingStateRead.Unreadable reason -> Assert.isTrue (reason.Contains "gemini") "the reason names the provider"
              | other -> failwith $"expected Unreadable, got {other}") ]

    // ---- PRX-QUAL-005: typed identities and explicit adapter rules -----------

    let private identityTests =
        let families = [ family "opus"; family "sonnet"; family "fable" ]

        [ t "a model is recognized only by a whole family token" (fun () ->
              Assert.equal (ModelIdentity.Recognized("claude-opus-5[1m]", family "opus")) (ModelIdentity.resolve families (Some "claude-opus-5[1m]"))
              Assert.equal (ModelIdentity.Unrecognized "claude-fablesque-1") (ModelIdentity.resolve families (Some "claude-fablesque-1"))
              Assert.equal ModelIdentity.Unspecified (ModelIdentity.resolve families None)
              Assert.equal ModelIdentity.Unspecified (ModelIdentity.resolve families (Some "  ")))

          t "an ambiguous model is unrecognized rather than guessed" (fun () ->
              Assert.equal (ModelIdentity.Unrecognized "opus-sonnet-merge") (ModelIdentity.resolve families (Some "opus-sonnet-merge")))

          t "unknown and unrecognized models are checked against every scoped window" (fun () ->
              let scope = QuotaScope.Model(family "fable")
              Assert.isTrue (QuotaScope.appliesTo ModelIdentity.Unspecified scope) "unspecified is conservative"
              Assert.isTrue (QuotaScope.appliesTo (ModelIdentity.Unrecognized "claude-fablesque-1") scope) "a substring is not a match, so it stays conservative"
              Assert.isTrue (not (QuotaScope.appliesTo (ModelIdentity.Recognized("claude-opus-5", family "opus")) scope)) "another recognized family is unaffected"
              Assert.isTrue (QuotaScope.appliesTo (ModelIdentity.Recognized("claude-fable-5", family "fable")) scope) "the same family applies")

          t "Codex quota-bucket selection is an explicit token rule" (fun () ->
              Assert.equal (QuotaBucket "codex_bengalfox") (PacingAdapterRules.codexBucket (Some "gpt-5.3-codex-spark"))
              Assert.equal (QuotaBucket "codex") (PacingAdapterRules.codexBucket (Some "sparkle-model-1"))
              Assert.equal (QuotaBucket "codex") (PacingAdapterRules.codexBucket (Some "gpt-5.3-codex"))
              Assert.equal (QuotaBucket "codex") (PacingAdapterRules.codexBucket None))

          t "Claude scope display names map to one family or none" (fun () ->
              Assert.equal (Some(family "opus")) (PacingAdapterRules.claudeFamily "Claude Opus 5")
              Assert.equal (Some(family "fable")) (PacingAdapterRules.claudeFamily "Fable")
              Assert.equal None (PacingAdapterRules.claudeFamily "Opus and Sonnet")
              Assert.equal None (PacingAdapterRules.claudeFamily "New Thing"))

          t "providers parse exactly; anything else is not a provider" (fun () ->
              Assert.equal (Some ProviderId.Claude) (ProviderId.tryParse "Claude")
              Assert.equal None (ProviderId.tryParse "claude-code")
              Assert.equal None (ProviderId.tryParse "gemini"))

          t "the application resolves the model against adapter and observed families" (fun () ->
              let scoped = { weekly 9.0 with Key = "weekly:fable"; Scope = QuotaScope.Model(family "fable") }
              let snapshot = { snapshotOf [ scoped ] ObservationFreshness.Fresh with Adapter = PacingAdapterRules.claudeInfo }
              Assert.equal (ModelIdentity.Recognized("claude-fable-5-1", family "fable")) (PacingOperations.modelIdentity snapshot PacingState.empty (Some "claude-fable-5-1"))
              Assert.equal (ModelIdentity.Recognized("claude-opus-5", family "opus")) (PacingOperations.modelIdentity snapshot PacingState.empty (Some "claude-opus-5")))

          t "status reports the adapter, its rules version, capabilities and model identity" (fun () ->
              let snapshot = { snapshotOf [] ObservationFreshness.Fresh with Adapter = PacingAdapterRules.codexInfo (QuotaBucket "codex_bengalfox") }
              let model = ModelIdentity.Unrecognized "gpt-5.3-codex-spark"
              let status = PacingStatusProjection.create now "/tmp/p" ProviderId.Codex model false snapshot StateIntegrity.Intact (evaluate PacingState.empty []) None
              use document = JsonDocument.Parse(PacingStatusDocument.renderJson status)
              let adapter = document.RootElement.GetProperty "adapter"
              Assert.equal "codex-app-server" (adapter.GetProperty("id").GetString())
              Assert.equal PacingAdapterRules.CodexRulesVersion (adapter.GetProperty("rulesVersion").GetString())
              Assert.equal "codex_bengalfox" (adapter.GetProperty("quotaBucket").GetString())
              Assert.isTrue (adapter.GetProperty("capabilities").GetArrayLength() > 0) "capabilities are listed"
              Assert.equal "unrecognized" (document.RootElement.GetProperty("modelIdentity").GetString())
              let text = PacingStatusDocument.renderText status
              Assert.isTrue (text.Contains("adapter: codex-app-server codex-rules/1, bucket codex_bengalfox", StringComparison.Ordinal)) "text projects the adapter")

          t "guard: pacing policy and adapters never match models by substring" (fun () ->
              let directories =
                  [ "src/Praxis.Domain/Pacing"; "src/Praxis.Application/Pacing"; "src/Praxis.Infrastructure/Pacing" ]

              // A model compared by Contains/StartsWith/EndsWith/IndexOf, or a
              // case-insensitive Contains, is the heuristic PRX-QUAL-005 removed.
              let modelSubstring = Regex(@"(?i)model\w*\s*\.\s*(Contains|StartsWith|EndsWith|IndexOf)\(", RegexOptions.CultureInvariant)
              let caseInsensitiveContains = Regex(@"\.Contains\([^)]*OrdinalIgnoreCase", RegexOptions.CultureInvariant)

              let findings =
                  [ for directory in directories do
                        for path in Directory.GetFiles(Path.Combine(root (), directory), "*.fs") do
                            let lines = File.ReadAllLines path

                            for index in 0 .. lines.Length - 1 do
                                let code = lines[index].Split("//")[0]

                                if modelSubstring.IsMatch code || caseInsensitiveContains.IsMatch code then
                                    yield $"{Path.GetFileName path}:{index + 1}: {lines[index].Trim()}" ]

              Assert.empty findings) ]

    // ---- PRX-QUAL-008: typed pacing telemetry --------------------------------

    let private recordingRuntime (transact: (PacingStateRead -> PacingTransaction) -> Result<PacingTransaction, PacingStoreFault>) (observe: DateTimeOffset -> ProviderSnapshot) =
        let clock = ref now
        let events = ref []
        let overrideState = ref false

        let runtime =
            { Observer = { Observe = fun at _ _ -> observe at }
              State = { Transact = transact; Quarantine = fun _ -> Ok None }
              Override =
                { IsEnabled = (fun () -> overrideState.Value)
                  SetEnabled =
                    fun enabled ->
                        overrideState.Value <- enabled
                        Ok() }
              Clock =
                { Now = fun () -> clock.Value
                  Sleep = fun delay -> clock.Value <- clock.Value + delay }
              Events =
                { Record = fun event -> events.Value <- events.Value @ [ event ]
                  Last = fun () -> List.tryLast events.Value }
              Context = { ResolveModel = fun _ explicitModel _ -> explicitModel }
              MaxGateWait = fun _ -> TimeSpan.FromHours 2.0 }

        runtime, events

    let private memoryStore (initial: PacingState) =
        let state = ref initial

        fun (action: PacingStateRead -> PacingTransaction) ->
            let read =
                if state.Value.Holds.IsEmpty then PacingStateRead.Absent
                else PacingStateRead.Current(state.Value, 1L)

            let transaction: PacingTransaction = action read
            transaction.Write |> Option.iter (fun written -> state.Value <- written)
            Ok transaction

    let private codes (events: PacingEvent list) = events |> List.map (fun event -> PacingEvents.code event.Code)

    let private eventTests =
        [ t "every event code is stable and round-trips" (fun () ->
              Assert.equal
                  [ "hold-started"; "hold-retained"; "hold-released"; "hard-limit"; "provider-unavailable"; "override-enabled"; "override-disabled"; "state-fault" ]
                  (PacingEvents.allCodes |> List.map PacingEvents.code)

              for code in PacingEvents.allCodes do
                  Assert.equal (Some code) (PacingEvents.tryParseCode (PacingEvents.code code)))

          t "hold transitions are derived from persisted state" (fun () ->
              let hard = evaluate PacingState.empty [ session 99m ]
              Assert.equal [ "hard-limit" ] (codes (PacingEvents.transitions now PacingState.empty hard.State))
              let latched = evaluate PacingState.empty [ weekly 9.0 ]
              Assert.equal [ "hold-started" ] (codes (PacingEvents.transitions now PacingState.empty latched.State))
              let refreshed = evaluate hard.State [ session 99.5m ]
              Assert.equal [] (PacingEvents.transitions now hard.State refreshed.State)
              let released = evaluate hard.State [ session 10m ]
              let event = PacingEvents.transitions now hard.State released.State |> Assert.single
              Assert.equal PacingEventCode.HoldReleased event.Code
              Assert.equal (Some(now + TimeSpan.FromHours 1.0)) event.ResetsAt
              Assert.equal (Some PacingReasonKind.HardLimit) event.Reason)

          t "events never carry credentials or provider payloads" (fun () ->
              let token = "sk-ant-oat01-" + String('a', 64)
              let detail = $"Codex quota query failed: Bearer {token} {{\"accessToken\":\"{token}\",\"rate\":1}} access_token={token}"
              let event = PacingEvents.create PacingEventCode.ProviderUnavailable now (Some ProviderId.Claude) None None detail None None
              Assert.isTrue (not (event.Detail.Contains token)) "the token is removed"
              Assert.isTrue (not (event.Detail.Contains "accessToken")) "the payload is elided"
              Assert.isTrue (event.Detail.Length <= 243) "detail is bounded"
              let long = PacingEvents.redact (String.replicate 100 "provider offline ")
              Assert.isTrue (long.Length <= 243) "long diagnostics are truncated")

          t "a repeated transition is a duplicate; time and wording do not matter" (fun () ->
              let first = PacingEvents.create PacingEventCode.HoldRetained now (Some ProviderId.Codex) (Some "codex/weekly") (Some PacingReasonKind.WeeklyLead) "a" (Some now) None
              let again = { first with OccurredAt = now + TimeSpan.FromMinutes 1.0; Detail = "b" }
              Assert.isTrue (PacingEvents.isDuplicate (Some first) again) "same transition"
              Assert.isTrue (not (PacingEvents.isDuplicate (Some first) { again with Code = PacingEventCode.HoldReleased })) "a different code is new"
              Assert.isTrue (not (PacingEvents.isDuplicate None first)) "nothing recorded yet")

          t "the gate records hard-limit then hold-released once, however often it polls" (fun () ->
              let reset = now + TimeSpan.FromSeconds 12.0

              let observe at =
                  if at < reset then snapshotOf [ window "session" 99m (TimeSpan.FromHours 5.0) reset ] ObservationFreshness.Fresh
                  else snapshotOf [] ObservationFreshness.Fresh

              let runtime, events = recordingRuntime (memoryStore PacingState.empty) observe

              match PacingOperations.gate runtime ProviderId.Codex None with
              | PacingGateOutcome.Proceed -> Assert.equal [ "hard-limit"; "hold-released" ] (codes events.Value)
              | other -> failwith $"expected release, got {other}")

          t "a gate held by an existing hold records hold-retained once" (fun () ->
              let established = { (evaluate PacingState.empty [ weekly 9.0 ]).State with Holds = Map.empty }
              let earlier = Pacing.evaluate PacingPolicy.defaults { request established [ weekly 9.0 ] ObservationFreshness.Fresh with Now = now - TimeSpan.FromHours 1.0 }
              let runtime, events = recordingRuntime (memoryStore earlier.State) (fun _ -> snapshotOf [ weekly 9.0 ] ObservationFreshness.Fresh)
              let held = { runtime with MaxGateWait = fun _ -> TimeSpan.FromSeconds 30.0 }

              match PacingOperations.gate held ProviderId.Codex None with
              | PacingGateOutcome.Denied _ -> Assert.equal [ "hold-retained" ] (codes events.Value)
              | other -> failwith $"expected a timed-out denial, got {other}")

          t "a provider outage is recorded once per gate, not per poll" (fun () ->
              let hold = (evaluate PacingState.empty [ session 99m ]).State
              let runtime, events = recordingRuntime (memoryStore hold) (fun _ -> snapshotOf [] (ObservationFreshness.Unavailable "provider offline"))
              let held = { runtime with MaxGateWait = fun _ -> TimeSpan.FromMinutes 3.0 }

              match PacingOperations.gate held ProviderId.Codex None with
              | PacingGateOutcome.Denied _ ->
                  Assert.equal 1 (events.Value |> List.filter (fun event -> event.Code = PacingEventCode.ProviderUnavailable)).Length
              | other -> failwith $"expected a denial, got {other}")

          t "state faults and indeterminate state are recorded as state-fault" (fun () ->
              let runtime, events = recordingRuntime (fun _ -> Error(PacingStoreFault.WriteFailed "disk full")) (fun _ -> snapshotOf [] ObservationFreshness.Fresh)
              PacingOperations.gate runtime ProviderId.Codex None |> ignore
              Assert.equal [ "state-fault" ] (codes events.Value)

              let runtime, events = recordingRuntime (fun action -> Ok(action (PacingStateRead.Unreadable "corrupt"))) (fun _ -> snapshotOf [] ObservationFreshness.Fresh)
              PacingOperations.gate runtime ProviderId.Codex None |> ignore
              Assert.equal [ "state-fault" ] (codes events.Value))

          t "override changes are recorded only when the state changes" (fun () ->
              let runtime, events = recordingRuntime (memoryStore PacingState.empty) (fun _ -> snapshotOf [] ObservationFreshness.Fresh)
              PacingOperations.setOverride runtime true |> ok
              PacingOperations.setOverride runtime true |> ok
              PacingOperations.setOverride runtime false |> ok
              Assert.equal [ "override-enabled"; "override-disabled" ] (codes events.Value))

          t "the event log stores typed JSON lines and a readable pace.log" (fun () ->
              withDirectory (fun directory ->
                  let event = PacingEvents.create PacingEventCode.HardLimit now (Some ProviderId.Codex) (Some "codex/session") (Some PacingReasonKind.HardLimit) "hard limit reached" (Some(now + TimeSpan.FromHours 1.0)) (Some now)
                  let sink = PacingEventLog.sink directory
                  Assert.equal None (sink.Last())
                  sink.Record event
                  Assert.equal (Some event) (sink.Last())
                  let line = File.ReadAllLines(PacingEventLog.eventsPath directory) |> Array.exactlyOne
                  use document = JsonDocument.Parse line
                  Assert.equal "praxis.pacing-event/1" (document.RootElement.GetProperty("schema").GetString())
                  Assert.equal "hard-limit" (document.RootElement.GetProperty("code").GetString())
                  let log = File.ReadAllText(PacingEventLog.logPath directory)
                  Assert.isTrue (log.Contains("hard-limit codex codex/session: hard limit reached", StringComparison.Ordinal)) "pace.log renders the same event"))

          t "a second process does not repeat a transition the first recorded" (fun () ->
              withDirectory (fun directory ->
                  let hold = (evaluate PacingState.empty [ weekly 9.0 ]).State
                  let earlier = { hold with Holds = hold.Holds |> Map.map (fun _ held -> { held with Since = now - TimeSpan.FromHours 1.0 }) }

                  let gateOnce () =
                      let runtime, _ = recordingRuntime (memoryStore earlier) (fun _ -> snapshotOf [ weekly 9.0 ] ObservationFreshness.Fresh)
                      let fileBacked = { runtime with Events = PacingEventLog.sink directory; MaxGateWait = fun _ -> TimeSpan.Zero }
                      PacingOperations.gate fileBacked ProviderId.Codex None |> ignore

                  gateOnce ()
                  gateOnce ()
                  Assert.equal 1 (File.ReadAllLines(PacingEventLog.eventsPath directory)).Length))

          t "status reports the last recorded event" (fun () ->
              let event = PacingEvents.create PacingEventCode.HoldReleased now (Some ProviderId.Codex) (Some "codex/weekly") None "released" None None
              let status = PacingStatusProjection.create now "/tmp/p" ProviderId.Codex ModelIdentity.Unspecified false (snapshotOf [] ObservationFreshness.Fresh) StateIntegrity.Intact (evaluate PacingState.empty []) (Some event)
              use document = JsonDocument.Parse(PacingStatusDocument.renderJson status)
              Assert.equal "hold-released" (document.RootElement.GetProperty("lastEvent").GetProperty("code").GetString())) ]

    // ---- PRX-QUAL-011: adversarial adapter cases -----------------------------

    let private credential (expiresAt: DateTimeOffset) =
        $"""{{"claudeAiOauth":{{"accessToken":"secret-token-value","refreshToken":"r","expiresAt":{expiresAt.ToUnixTimeMilliseconds()}}}}}"""

    type private StubHandler(respond: HttpRequestMessage -> HttpResponseMessage) =
        inherit HttpMessageHandler()
        member val Requests = ResizeArray<HttpRequestMessage>()

        override this.SendAsync(request, _cancellation: CancellationToken) =
            this.Requests.Add request
            Task.FromResult(respond request)

        override this.Send(request, _cancellation: CancellationToken) =
            this.Requests.Add request
            respond request

    let private scripted (lines: ProtocolLine list) =
        let remaining = ref lines
        let sent = ResizeArray<string>()

        let channel =
            { Send = sent.Add
              ReadLine =
                fun _ ->
                    match remaining.Value with
                    | next :: rest ->
                        remaining.Value <- rest
                        next
                    | [] -> ProtocolLine.Closed }

        channel, sent

    let private clockFrom (start: DateTimeOffset) =
        let current = ref start
        fun () -> current.Value

    let private adapterTests =
        [ t "an unavailable Keychain is an explicit error" (fun () ->
              PacingProviders.decodeClaudeCredential now (Error "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain.")
              |> expectError "credentials are unavailable")

          t "a Keychain entry without Claude OAuth credentials is an explicit error" (fun () ->
              PacingProviders.decodeClaudeCredential now (Ok """{"mcpOAuth":{}}""") |> expectError "were not found")

          t "an expired credential is refused" (fun () ->
              PacingProviders.decodeClaudeCredential now (Ok(credential (now - TimeSpan.FromMinutes 5.0))) |> expectError "expired"
              PacingProviders.decodeClaudeCredential now (Ok(credential (now + TimeSpan.FromSeconds 30.0))) |> expectError "expired")

          t "a malformed credential is refused without echoing it" (fun () ->
              match PacingProviders.decodeClaudeCredential now (Ok "{\"claudeAiOauth\": secret-token-value") with
              | Ok _ -> failwith "malformed credentials must not decode"
              | Error message -> Assert.isTrue (not (message.Contains "secret-token-value")) "the credential text is never echoed")

          t "a valid credential yields its token" (fun () ->
              Assert.equal (Ok "secret-token-value") (PacingProviders.decodeClaudeCredential now (Ok(credential (now + TimeSpan.FromHours 1.0)))))

          t "a redirect from the usage endpoint is refused and not followed" (fun () ->
              let handler =
                  new StubHandler(fun _ ->
                      let response = new HttpResponseMessage(HttpStatusCode.Found)
                      response.Headers.Location <- Uri "https://elsewhere.example/steal"
                      response)

              PacingProviders.queryClaudeUsage handler "token" |> expectError "refused redirect"
              Assert.equal 1 handler.Requests.Count
              Assert.equal "api.anthropic.com" handler.Requests[0].RequestUri.Host)

          t "HTTP errors from the usage endpoint are explicit" (fun () ->
              for status in [ HttpStatusCode.Unauthorized; HttpStatusCode.TooManyRequests; HttpStatusCode.InternalServerError ] do
                  let handler = new StubHandler(fun _ -> new HttpResponseMessage(status))
                  PacingProviders.queryClaudeUsage handler "token" |> expectError $"HTTP {int status}")

          t "a transport failure from the usage endpoint is explicit" (fun () ->
              let handler = new StubHandler(fun _ -> raise (HttpRequestException "connection refused"))
              PacingProviders.queryClaudeUsage handler "token" |> expectError "connection refused")

          t "the usage request carries the bearer token only to the usage endpoint" (fun () ->
              let handler = new StubHandler(fun _ -> new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent("{}")))
              Assert.equal (Ok "{}") (PacingProviders.queryClaudeUsage handler "token")
              let sent = handler.Requests[0]
              Assert.equal "Bearer" sent.Headers.Authorization.Scheme
              Assert.equal "https://api.anthropic.com/api/oauth/usage" (string sent.RequestUri))

          t "an expired credential makes the Claude snapshot unavailable, never fresh" (fun () ->
              withDirectory (fun directory ->
                  let query _ _ _ at =
                      PacingProviders.queryClaudeWith (fun () -> Ok(credential (now - TimeSpan.FromDays 1.0))) (fun () -> failwith "no request may be sent") at
                      |> Result.bind (PacingNormalization.normalizeClaudeUsage at [])

                  let snapshot = PacingAdapters.observeWith query directory now ProviderId.Claude None

                  match snapshot.Freshness with
                  | ObservationFreshness.Unavailable reason -> Assert.isTrue (reason.Contains "expired") "the reason is the credential"
                  | other -> failwith $"expected Unavailable, got {other}"))

          t "a provider failure with a cache is stale with every cached window stale" (fun () ->
              withDirectory (fun directory ->
                  let fresh _ _ _ _ = Ok { Windows = [ session 50m; weekly 0.0 ]; Coverage = [] }
                  PacingAdapters.observeWith fresh directory now ProviderId.Codex None |> ignore
                  let failing _ _ _ _ = Error "Codex quota query timed out"
                  let snapshot = PacingAdapters.observeWith failing directory (now + TimeSpan.FromMinutes 1.0) ProviderId.Codex None
                  Assert.equal (ObservationFreshness.Stale "Codex quota query timed out") snapshot.Freshness
                  Assert.isTrue (snapshot.Coverage |> List.forall (fun observation -> WindowObservation.statusCode observation = "stale")) "cached windows are stale"))

          t "the Codex exchange returns the quota result" (fun () ->
              let channel, sent =
                  scripted
                      [ ProtocolLine.Line """{"id":1,"result":{"userAgent":"codex"}}"""
                        ProtocolLine.Line """{"method":"notification","params":{}}"""
                        ProtocolLine.Line """{"id":2,"result":{"rateLimits":{}}}""" ]

              Assert.equal (Ok """{"rateLimits":{}}""") (PacingProviders.converseCodex channel (clockFrom now) (TimeSpan.FromSeconds 20.0))
              Assert.equal 3 sent.Count)

          t "a Codex process that times out is an explicit error" (fun () ->
              let channel, _ = scripted [ ProtocolLine.TimedOut ]
              PacingProviders.converseCodex channel (clockFrom now) (TimeSpan.FromSeconds 20.0) |> expectError "timed out"
              let channel, _ = scripted []
              PacingProviders.converseCodex channel (clockFrom now) TimeSpan.Zero |> expectError "timed out")

          t "a Codex process that closes before replying is an explicit error" (fun () ->
              let channel, _ = scripted [ ProtocolLine.Line """{"id":1,"result":{}}"""; ProtocolLine.Closed ]
              PacingProviders.converseCodex channel (clockFrom now) (TimeSpan.FromSeconds 20.0) |> expectError "closed before returning quota")

          t "a Codex error reply is explicit and does not echo the payload" (fun () ->
              let channel, _ = scripted [ ProtocolLine.Line """{"id":1,"result":{}}"""; ProtocolLine.Line """{"id":2,"error":{"code":-32000,"message":"not signed in","data":{"token":"leak"}}}""" ]

              match PacingProviders.converseCodex channel (clockFrom now) (TimeSpan.FromSeconds 20.0) with
              | Error message ->
                  Assert.isTrue (message.Contains "not signed in") "the provider's message is kept"
                  Assert.isTrue (not (message.Contains "leak")) "the payload is not echoed"
              | Ok value -> failwith $"expected an error, got {value}")

          t "malformed JSON from the Codex process is an explicit error" (fun () ->
              let channel, _ = scripted [ ProtocolLine.Line "{\"id\":1,\"result\":" ]
              PacingProviders.converseCodex channel (clockFrom now) (TimeSpan.FromSeconds 20.0) |> expectError "malformed JSON") ]

    // ---- PRX-QUAL-011: hook contract fixtures for both providers -------------

    let private fixture name = Path.Combine(root (), "tests", "fixtures", "pacing", "hooks", name)

    let private payload name =
        File.ReadAllText(fixture name).Replace("TRANSCRIPT_PATH", fixture("claude-transcript.jsonl").Replace("\\", "\\\\"))

    /// The real context resolver and gate, with a held hard limit and a
    /// recorded observer so the model the gate resolved is visible.
    let private heldGate (provider: ProviderId) =
        let observedModels = ResizeArray<string option>()
        let hold = (evaluate PacingState.empty [ session 99m ]).State

        let held =
            { hold with Holds = hold.Holds |> Map.map (fun _ value -> { value with Provider = provider; Key = $"{ProviderId.code provider}/session" }) }
            |> fun state -> PacingState.ofHolds state.Holds.Values

        let runtime, _ =
            recordingRuntime (memoryStore held) (fun _ -> { snapshotOf [] (ObservationFreshness.Unavailable "offline") with Provider = provider })

        { runtime with
            Observer =
                { Observe =
                    fun at observedProvider model ->
                        observedModels.Add model
                        { snapshotOf [] (ObservationFreshness.Unavailable "offline") with Provider = observedProvider; ObservedAt = at } }
            Context = PacingContext.resolver
            MaxGateWait = fun _ -> TimeSpan.Zero },
        observedModels

    let private hookTests =
        [ t "Claude Code PreToolUse fixture: model from the transcript, denied with permissionDecision" (fun () ->
              let runtime, models = heldGate ProviderId.Claude

              match PacingCommands.gateHook runtime ProviderId.Claude None (payload "claude-pre-tool-use.json") with
              | None -> failwith "a held gate must deny"
              | Some output ->
                  use document = JsonDocument.Parse output
                  let specific = document.RootElement.GetProperty "hookSpecificOutput"
                  Assert.equal "PreToolUse" (specific.GetProperty("hookEventName").GetString())
                  Assert.equal "deny" (specific.GetProperty("permissionDecision").GetString())
                  Assert.isTrue (specific.GetProperty("permissionDecisionReason").GetString().Contains "pacing hold") "the reason explains the hold"
                  Assert.equal (Some "claude-opus-5-20261001") models[0])

          t "Claude Code Stop fixture: denied with decision block" (fun () ->
              let runtime, _ = heldGate ProviderId.Claude

              match PacingCommands.gateHook runtime ProviderId.Claude None (payload "claude-stop.json") with
              | None -> failwith "a held gate must deny"
              | Some output ->
                  use document = JsonDocument.Parse output
                  Assert.equal "block" (document.RootElement.GetProperty("decision").GetString())
                  Assert.isTrue (document.RootElement.GetProperty("reason").GetString().Length > 0) "a reason is given")

          t "Codex PreToolUse fixture: model from the payload, denied with permissionDecision" (fun () ->
              let runtime, models = heldGate ProviderId.Codex

              match PacingCommands.gateHook runtime ProviderId.Codex None (payload "codex-pre-tool-use.json") with
              | None -> failwith "a held gate must deny"
              | Some output ->
                  use document = JsonDocument.Parse output
                  Assert.equal "deny" (document.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString())
                  Assert.equal (Some "gpt-5.3-codex-spark") models[0]
                  Assert.equal (QuotaBucket "codex_bengalfox") (PacingAdapterRules.codexBucket models[0]))

          t "Codex UserPromptSubmit fixture: denied with continue false" (fun () ->
              let runtime, _ = heldGate ProviderId.Codex

              match PacingCommands.gateHook runtime ProviderId.Codex None (payload "codex-user-prompt-submit.json") with
              | None -> failwith "a held gate must deny"
              | Some output ->
                  use document = JsonDocument.Parse output
                  Assert.isTrue (not (document.RootElement.GetProperty("continue").GetBoolean())) "Codex stops the turn"
                  Assert.isTrue (document.RootElement.GetProperty("stopReason").GetString().Length > 0) "a stop reason is given")

          t "every fixture proceeds silently when no hold applies" (fun () ->
              for provider, name in
                  [ ProviderId.Claude, "claude-pre-tool-use.json"
                    ProviderId.Claude, "claude-stop.json"
                    ProviderId.Codex, "codex-pre-tool-use.json"
                    ProviderId.Codex, "codex-user-prompt-submit.json" ] do
                  let runtime, _ = recordingRuntime (memoryStore PacingState.empty) (fun _ -> { snapshotOf [] ObservationFreshness.Fresh with Provider = provider })
                  let runtime = { runtime with Context = PacingContext.resolver }
                  Assert.equal None (PacingCommands.gateHook runtime provider None (payload name))) ]

    let tests = coverageTests @ evidenceTests @ identityTests @ eventTests @ adapterTests @ hookTests
