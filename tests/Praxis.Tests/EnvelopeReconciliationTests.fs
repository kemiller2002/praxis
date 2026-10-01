namespace Praxis.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Telemetry
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module EnvelopeReconciliationTests =
    let private runGit root arguments =
        let info = ProcessStartInfo()
        info.FileName <- "git"
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.ArgumentList.Add "-C"
        info.ArgumentList.Add root
        arguments |> List.iter info.ArgumentList.Add
        use child = new Process(StartInfo = info)
        if not (child.Start()) then failwith "git did not start"
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        if child.ExitCode <> 0 then failwith $"git failed: {error.Result}"
        output.Result.Trim()

    let private withGitRepository run =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-envelope-repo-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        try
            runGit root [ "init"; "-q"; "-b"; "WI-0064" ] |> ignore
            runGit root [ "config"; "user.name"; "Praxis Test" ] |> ignore
            runGit root [ "config"; "user.email"; "praxis@example.invalid" ] |> ignore
            File.WriteAllText(Path.Combine(root, "ros.json"), """{"repository":"test-repository","protocolVersion":"1.0.0","telemetry":{"enabled":true},"workProtocol":{"completionEvidence":{"default":[],"research":[]}}}""")
            Directory.CreateDirectory(Path.Combine(root, "telemetry")) |> ignore
            File.WriteAllText(
                Path.Combine(root, "telemetry", "metrics.json"),
                """{"schemaVersion":"1.0.0","metrics":[{"id":"tokens.input","unit":"tokens","aggregation":"sum","collection":"runtime"},{"id":"tokens.output","unit":"tokens","aggregation":"sum","collection":"runtime"},{"id":"session.tokens.cumulative","unit":"tokens","aggregation":"latest-per-session","collection":"runtime"},{"id":"cost.step_total","unit":"currency","aggregation":"sum","collection":"runtime-or-calculated"}]}""")
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed\n")
            runGit root [ "add"; "ros.json"; "telemetry/metrics.json"; "seed.txt" ] |> ignore
            runGit root [ "commit"; "-qm"; "baseline" ] |> ignore
            run root (runGit root [ "rev-parse"; "HEAD" ])
        finally Directory.Delete(root, true)

    let private withEnvelope (json: string) (run: string -> unit) =
        let path = Path.Combine(Path.GetTempPath(), $"praxis-envelope-{Guid.NewGuid():N}.json")
        File.WriteAllText(path, json)
        try run path
        finally File.Delete path

    let private actor =
        { ActorKind = "agent"; ActorId = "openai"; Provider = Some "openai"; Model = None; Runtime = None }

    let private envelope =
        { SchemaVersion = "1.0"
          TransactionId = "tx-1"
          WorkItem = "WI-0064"
          Branch = "WI-0064"
          BaseCommit = String.replicate 40 "a"
          PraxisInstanceId = None
          Agent = actor
          Execution = None
          Timeline = [{ Sequence = 1; Timestamp = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Action = "start" }]
          Requests =
            [{ RequestType = "work.start"
               OccurredAt = None
               WorkType = None
               Reason = None
               Conclusion = None
               Evidence = [] }] }

    let private observed =
        { ActualBranch = "WI-0064"; HeadCommit = String.replicate 40 "b"; BaseCommitExists = true; BaseCommitIsAncestor = true; TransactionAlreadyApplied = false; TransactionReplayMatches = true; LocalPraxisInstanceId = None }

    let private validStep =
        { StepId = "STEP-1"; ExecutionId = "EXE-FALLBACK"; WorkItemId = "WI-0064"; Sequence = 1; ParentStepId = None; Name = "test"; Description = None; Classifications = [ "testing" ]
          Status = "completed"; PlannedAt = DateTimeOffset.Parse("2026-09-26T10:01:00Z"); StartedAt = Some(DateTimeOffset.Parse("2026-09-26T10:01:00Z")); CompletedAt = Some(DateTimeOffset.Parse("2026-09-26T10:02:00Z")); EndedAt = Some(DateTimeOffset.Parse("2026-09-26T10:02:00Z")); Actor = actor
          Transitions =
            [ { Sequence = 1; FromStatus = None; ToStatus = "active"; Timestamp = DateTimeOffset.Parse("2026-09-26T10:01:00Z") }
              { Sequence = 2; FromStatus = Some "active"; ToStatus = "completed"; Timestamp = DateTimeOffset.Parse("2026-09-26T10:02:00Z") } ]
          Measurements = []; RawTelemetry = []; Evidence = [] }

    let private stepEnvelope =
        { envelope with
            TransactionId = "tx-step"
            Execution = Some { ExecutionId = "EXE-FALLBACK"; StartedAt = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Steps = [ validStep ] } }

    let private rendererDefinitions =
        [ { Id = "tokens.input"; Unit = "tokens"; Aggregation = "sum"; Collection = "runtime" }
          { Id = "tokens.output"; Unit = "tokens"; Aggregation = "sum"; Collection = "runtime" }
          { Id = "session.tokens.cumulative"; Unit = "tokens"; Aggregation = "latest-per-session"; Collection = "runtime" }
          { Id = "cost.step_total"; Unit = "currency"; Aggregation = "sum"; Collection = "runtime-or-calculated" } ]

    let tests =
        [ { Name = "reconciliation accepts a legal envelope"
            Run = fun () -> Assert.equal Accept (EnvelopeReconciliation.decide envelope observed) }
          { Name = "reconciliation rejects claimed work-item branch mismatch"
            Run = fun () ->
                match EnvelopeReconciliation.decide { envelope with Branch = "main" } observed with
                | Reject findings -> Assert.isTrue (List.contains (WorkItemBranchMismatch("WI-0064", "main")) findings) "missing mismatch finding"
                | value -> failwithf "expected rejection, got %A" value }
          { Name = "reconciliation rejects spoofed observed branch"
            Run = fun () ->
                match EnvelopeReconciliation.decide envelope { observed with ActualBranch = "main" } with
                | Reject findings -> Assert.isTrue (List.contains (ObservedBranchMismatch("WI-0064", "main")) findings) "missing observed mismatch"
                | value -> failwithf "expected rejection, got %A" value }
          { Name = "reconciliation treats replay as already applied"
            Run = fun () -> Assert.equal EnvelopeReconciliationDecision.AlreadyApplied (EnvelopeReconciliation.decide envelope { observed with TransactionAlreadyApplied = true }) }
          { Name = "reconciliation rejects reuse of a transaction ID for different envelope content"
            Run = fun () ->
                match EnvelopeReconciliation.decide envelope { observed with TransactionAlreadyApplied = true; TransactionReplayMatches = false } with
                | Reject findings -> Assert.equal [ DuplicateTransaction "tx-1" ] findings
                | value -> failwithf "expected rejection, got %A" value }
          { Name = "reconciliation rejects a discontinuous timeline"
            Run = fun () ->
                let changed = { envelope with Timeline = [{ envelope.Timeline.Head with Sequence = 2 }] }
                match EnvelopeReconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (List.contains InvalidTimelineSequence findings) "missing sequence finding"
                | value -> failwithf "expected rejection, got %A" value }
          { Name = "reconciliation rejects an existing base that is not an ancestor of HEAD"
            Run = fun () ->
                match EnvelopeReconciliation.decide envelope { observed with BaseCommitIsAncestor = false } with
                | Reject findings -> Assert.isTrue (List.contains (BaseCommitNotAncestor(envelope.BaseCommit, observed.HeadCommit)) findings) "missing stale-base finding"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "reconciliation accepts a legal step history and rejects an illegal transition history"
            Run = fun () ->
                Assert.equal Accept (EnvelopeReconciliation.decide stepEnvelope observed)
                let badTransition = { validStep.Transitions[1] with FromStatus = Some "planned" }
                let badStep = { validStep with Transitions = [ validStep.Transitions[0]; badTransition ] }
                let changed = { stepEnvelope with Execution = Some { stepEnvelope.Execution.Value with Steps = [ badStep ] } }
                match EnvelopeReconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (findings |> List.exists (function InvalidStepStructure value -> value.Contains "transition-status-discontinuity" | _ -> false)) "missing transition contradiction"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "reconciliation quarantine durably preserves the rejected step envelope"
            Run = fun () ->
                let root = Path.Combine(Path.GetTempPath(), $"praxis-reconcile-{Guid.NewGuid():N}")
                Directory.CreateDirectory root |> ignore
                try
                    let store = FileEnvelopeReconciliationStore.create root
                    store.Quarantine stepEnvelope [ "request-dispatch-not-configured" ]
                    let content = File.ReadAllText(Path.Combine(root, ".praxis", "rejected", "tx-step.json"))
                    Assert.isTrue (content.Contains "STEP-1") "quarantine dropped step boundary"
                    Assert.isTrue (content.Contains "EXE-FALLBACK") "quarantine dropped execution identity"
                finally Directory.Delete(root, true) }

          { Name = "legacy fallback JSON without execution steps remains compatible"
            Run = fun () ->
                let json = """{"schemaVersion":"1.0","transactionId":"tx-old","workItem":"WI-0064","branch":"WI-0064","baseCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","agent":{"kind":"agent","id":"openai/codex"},"timeline":[{"sequence":1,"timestamp":"2026-09-26T10:00:00Z","action":"start"}],"requests":[{"type":"work.start"}]}"""
                withEnvelope json (fun path ->
                    match ReconciliationEnvelopeJson.read path with
                    | Error codes -> failwith (String.concat "," codes)
                    | Ok parsed ->
                        Assert.equal None parsed.Execution
                        Assert.equal None parsed.PraxisInstanceId) }

          { Name = "step-aware fallback JSON preserves boundaries, transitions, availability, raw telemetry, and evidence"
            Run = fun () ->
                let json = """{"schemaVersion":"1.0","transactionId":"tx-step","workItem":"WI-0064","branch":"WI-0064","baseCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","praxisInstanceId":"PRAXIS-1","agent":{"kind":"agent","id":"openai/codex","provider":"openai","model":"unknown","runtime":"codex"},"execution":{"executionId":"EXE-FALLBACK","startedAt":"2026-09-26T10:00:00Z","steps":[{"stepId":"STEP-1","executionId":"EXE-FALLBACK","workItemId":"WI-0064","sequence":1,"name":"test","classification":["testing"],"status":"completed","plannedAt":"2026-09-26T10:01:00Z","startedAt":"2026-09-26T10:01:00Z","completedAt":"2026-09-26T10:02:00Z","endedAt":"2026-09-26T10:02:00Z","actor":{"kind":"agent","id":"openai/codex"},"transitions":[{"sequence":1,"to":"active","timestamp":"2026-09-26T10:01:00Z"},{"sequence":2,"from":"active","to":"completed","timestamp":"2026-09-26T10:02:00Z"}],"telemetry":{"measurements":[{"measurementId":"MEAS-1","metricId":"tokens.input","value":0,"unit":"tokens","quality":"reported","availability":"reported"},{"measurementId":"MEAS-2","metricId":"tokens.output","availability":"unavailable"}],"rawTelemetry":[{"providerField":"preserved"}]},"evidence":["EV-1"]}]},"timeline":[{"sequence":1,"timestamp":"2026-09-26T10:00:00Z","action":"start"}],"requests":[{"type":"work.start"}]}"""
                withEnvelope json (fun path ->
                    match ReconciliationEnvelopeJson.read path with
                    | Error codes -> failwith (String.concat "," codes)
                    | Ok parsed ->
                        Assert.equal (Some "PRAXIS-1") parsed.PraxisInstanceId
                        let step = parsed.Execution |> Option.get |> _.Steps |> List.exactlyOne
                        Assert.equal "STEP-1" step.StepId
                        Assert.equal 2 step.Transitions.Length
                        Assert.equal 2 step.Measurements.Length
                        Assert.equal (Some 0.0) step.Measurements[0].Value
                        Assert.equal "unavailable" step.Measurements[1].Availability
                        Assert.equal 1 step.RawTelemetry.Length
                        Assert.equal [ "EV-1" ] step.Evidence) }

          { Name = "reconciliation rejects a claimed Praxis instance mismatch"
            Run = fun () ->
                let changed = { envelope with PraxisInstanceId = Some "PRAXIS-REMOTE" }
                let observedWithInstance = { observed with LocalPraxisInstanceId = Some "PRAXIS-LOCAL" }
                match EnvelopeReconciliation.decide changed observedWithInstance with
                | Reject findings -> Assert.isTrue (List.contains (InstanceIdentityMismatch("PRAXIS-REMOTE", "PRAXIS-LOCAL")) findings) "missing instance mismatch"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "fallback validation rejects unavailable measurements that carry exact-looking values"
            Run = fun () ->
                let step =
                    { StepId = "STEP-1"; ExecutionId = "EXE-FALLBACK"; WorkItemId = "WI-0064"; Sequence = 1; ParentStepId = None; Name = "bad"; Description = None; Classifications = [];
                      Status = "completed"; PlannedAt = DateTimeOffset.Parse("2026-09-26T10:01:00Z"); StartedAt = Some(DateTimeOffset.Parse("2026-09-26T10:01:00Z")); CompletedAt = Some(DateTimeOffset.Parse("2026-09-26T10:02:00Z")); EndedAt = Some(DateTimeOffset.Parse("2026-09-26T10:02:00Z")); Actor = actor; Transitions = [];
                      Measurements = [ { MeasurementId = "MEAS-1"; MetricId = "tokens.input"; Value = Some 0.0; Unit = Some "tokens"; Currency = None; Quality = None; Availability = "unavailable"; RawJson = "{}" } ]; RawTelemetry = []; Evidence = [] }
                let changed = { envelope with Execution = Some { ExecutionId = "EXE-FALLBACK"; StartedAt = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Steps = [ step ] } }
                match EnvelopeReconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (findings |> List.exists (function InvalidStepStructure value -> value.Contains "value-forbidden" | _ -> false)) "missing availability contradiction"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "fallback requests preserve transition fields and reject unknown request types"
            Run = fun () ->
                let json = """{"schemaVersion":"1.0","transactionId":"tx-fields","workItem":"WI-0064","branch":"WI-0064","baseCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","agent":{"kind":"agent","id":"openai/codex"},"timeline":[{"sequence":1,"timestamp":"2026-09-26T10:00:00Z","action":"complete"}],"requests":[{"type":"work.complete","occurredAt":"2026-09-26T10:00:00Z","workType":"feature","conclusion":"accepted","evidence":[{"type":"test","path":"evidence.txt"}]}]}"""
                withEnvelope json (fun path ->
                    let parsed = ReconciliationEnvelopeJson.read path |> Result.defaultWith (String.concat "," >> failwith)
                    let request = parsed.Requests |> List.exactlyOne
                    Assert.equal (Some "feature") request.WorkType
                    Assert.equal (Some "accepted") request.Conclusion
                    Assert.equal [ { Type = "test"; Path = "evidence.txt" } ] request.Evidence
                    let changed = { parsed with Requests = [ { request with RequestType = "work.deploy" } ] }
                    match EnvelopeReconciliation.decide changed observed with
                    | Reject findings -> Assert.isTrue (List.contains (UnsupportedRequestType "work.deploy") findings) "unknown request was not rejected"
                    | value -> failwithf "expected rejection, got %A" value) }

          { Name = "fallback execution rendering preserves zero unavailable raw telemetry and step boundaries"
            Run = fun () ->
                let measured =
                    { MeasurementId = "MEAS-ZERO"; MetricId = "tokens.input"; Value = Some 0.0; Unit = Some "tokens"; Currency = None
                      Quality = Some "reported"; Availability = "reported"; RawJson = "{}" }
                let unavailable =
                    { MeasurementId = "MEAS-NONE"; MetricId = "tokens.output"; Value = None; Unit = Some "tokens"; Currency = None
                      Quality = None; Availability = "unavailable"; RawJson = "{}" }
                let step = { validStep with Measurements = [ measured; unavailable ]; RawTelemetry = [ "{\"providerField\":17,\"password\":\"secret\"}" ] }
                let value = { stepEnvelope with Agent = { actor with Model = Some "unknown"; Runtime = Some "codex" }; Execution = Some { stepEnvelope.Execution.Value with Steps = [ { step with Actor = { actor with Model = Some "unknown"; Runtime = Some "codex" } } ] } }
                let rendered = FileEnvelopeExecutionRepository.render rendererDefinitions "repository" value.BaseCommit value value.Execution.Value
                let node = JsonNode.Parse rendered :?> JsonObject
                let metrics = node["metrics"] :?> JsonArray
                let steps = node["steps"] :?> JsonArray
                let capabilities = node["capabilities"] :?> JsonArray
                let metric = metrics[0] :?> JsonObject
                let renderedStep = steps[0] :?> JsonObject
                let unavailableCapability = capabilities[1] :?> JsonObject
                Assert.equal 1 metrics.Count
                Assert.equal 0.0 (metric["value"].GetValue<float>())
                Assert.equal 3 ((node["rawTelemetry"] :?> JsonArray).Count)
                let raw = (node["rawTelemetry"] :?> JsonArray)[0] :?> JsonObject
                Assert.equal "[REDACTED_BY_ROS]" (((raw["payload"] :?> JsonObject)["password"]).GetValue<string>())
                Assert.equal 1 ((raw["redactions"] :?> JsonArray).Count)
                Assert.equal "STEP-1" (renderedStep["stepId"].GetValue<string>())
                Assert.equal "supported-unavailable" (unavailableCapability["status"].GetValue<string>()) }

          { Name = "fallback calculated cost requires and preserves versioned pricing provenance"
            Run = fun () ->
                let cost =
                    { MeasurementId = "MEAS-COST"; MetricId = "cost.step_total"; Value = Some 0.12; Unit = Some "currency"; Currency = Some "USD"
                      Quality = Some "calculated"; Availability = "calculated"
                      RawJson = "{\"measurementId\":\"MEAS-COST\",\"metricId\":\"cost.step_total\",\"value\":0.12,\"availability\":\"calculated\",\"pricing\":{\"source\":\"catalog\",\"version\":\"2026-09-01\",\"method\":\"token-rate\"}}" }
                let pricedStep = { validStep with Measurements = [ cost ] }
                let priced = { stepEnvelope with Execution = Some { stepEnvelope.Execution.Value with Steps = [ pricedStep ] } }
                Assert.equal Accept (EnvelopeReconciliation.decide priced observed)
                let rendered = FileEnvelopeExecutionRepository.render rendererDefinitions "repository" priced.BaseCommit priced priced.Execution.Value |> JsonNode.Parse :?> JsonObject
                let metric = (rendered["metrics"] :?> JsonArray)[0] :?> JsonObject
                let pricing = metric["pricing"] :?> JsonObject
                Assert.equal "catalog" (pricing["source"].GetValue<string>())
                let missing = { cost with RawJson = "{}" }
                let changed = { stepEnvelope with Execution = Some { stepEnvelope.Execution.Value with Steps = [ { validStep with Measurements = [ missing ] } ] } }
                match EnvelopeReconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (findings |> List.exists (function InvalidStepStructure value -> value.Contains "calculated-cost-pricing-required" | _ -> false)) "missing pricing rejection"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "fallback estimated measurements require explicit confidence"
            Run = fun () ->
                let estimated =
                    { MeasurementId = "MEAS-ESTIMATE"; MetricId = "tokens.input"; Value = Some 12.0; Unit = Some "tokens"; Currency = None
                      Quality = Some "estimated"; Availability = "estimated"; RawJson = "{}" }
                let changed = { stepEnvelope with Execution = Some { stepEnvelope.Execution.Value with Steps = [ { validStep with Measurements = [ estimated ] } ] } }
                match EnvelopeReconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (findings |> List.exists (function InvalidStepStructure value -> value.Contains "estimated-confidence-required" | _ -> false)) "missing confidence rejection"
                | value -> failwithf "expected rejection, got %A" value
                let supported = { estimated with RawJson = "{\"confidence\":0.6}" }
                let accepted = { stepEnvelope with Execution = Some { stepEnvelope.Execution.Value with Steps = [ { validStep with Measurements = [ supported ] } ] } }
                Assert.equal Accept (EnvelopeReconciliation.decide accepted observed) }

          { Name = "fallback completion rejects a supplied execution with unresolved steps"
            Run = fun () ->
                let active = { validStep with Status = "active"; CompletedAt = None; EndedAt = None; Transitions = [ validStep.Transitions.Head ] }
                let completedAt = DateTimeOffset.Parse("2026-09-26T10:03:00Z")
                let changed =
                    { stepEnvelope with
                        Execution = Some { stepEnvelope.Execution.Value with Steps = [ active ] }
                        Timeline = [ { Sequence = 1; Timestamp = completedAt; Action = "complete" } ]
                        Requests = [ { RequestType = "work.complete"; OccurredAt = Some completedAt; WorkType = None; Reason = None; Conclusion = None; Evidence = [] } ] }
                match EnvelopeReconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (findings |> List.contains (InvalidStepStructure "completed-work-requires-terminal-execution")) "unresolved execution was accepted"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "fallback renderer preserves registry aggregation instead of treating cumulative counters as deltas"
            Run = fun () ->
                let cumulative =
                    { MeasurementId = "MEAS-CUMULATIVE"; MetricId = "session.tokens.cumulative"; Value = Some 42.0; Unit = Some "tokens"; Currency = None
                      Quality = Some "reported"; Availability = "reported"; RawJson = "{}" }
                let changed = { stepEnvelope with Execution = Some { stepEnvelope.Execution.Value with Steps = [ { validStep with Measurements = [ cumulative ] } ] } }
                let rendered = FileEnvelopeExecutionRepository.render rendererDefinitions "repository" changed.BaseCommit changed changed.Execution.Value |> JsonNode.Parse :?> JsonObject
                let metric = (rendered["metrics"] :?> JsonArray)[0] :?> JsonObject
                Assert.equal "latest-per-session" (metric["aggregation"].GetValue<string>()) }

          { Name = "fallback nested grouping parents cannot carry direct telemetry"
            Run = fun () ->
                let child = { validStep with StepId = "STEP-2"; Sequence = 2; ParentStepId = Some "STEP-1" }
                let parentMeasurement =
                    { MeasurementId = "MEAS-PARENT"; MetricId = "tokens.input"; Value = Some 1.0; Unit = Some "tokens"; Currency = None
                      Quality = Some "reported"; Availability = "reported"; RawJson = "{}" }
                let parent = { validStep with Status = "active"; CompletedAt = None; EndedAt = None; Transitions = [ validStep.Transitions.Head ]; Measurements = [ parentMeasurement ] }
                let changed = { stepEnvelope with Execution = Some { stepEnvelope.Execution.Value with Steps = [ parent; child ] } }
                match EnvelopeReconciliation.decide changed observed with
                | Reject findings -> Assert.isTrue (findings |> List.exists (function InvalidStepStructure value -> value.Contains "grouping-parent-carries-telemetry" | _ -> false)) "parent telemetry was accepted"
                | value -> failwithf "expected rejection, got %A" value }

          { Name = "dispatcher atomically applies canonical state commits a checkpoint deletes input and replays as applied"
            Run = fun () ->
                withGitRepository (fun root baseCommit ->
                    let inbox = Path.Combine(root, ".praxis", "inbox", "documents")
                    Directory.CreateDirectory inbox |> ignore
                    let input = Path.Combine(inbox, "tx-apply.json")
                    File.WriteAllText(input, "accepted envelope")
                    let runtimeActor = { actor with Model = Some "unknown"; Runtime = Some "codex" }
                    let request = { envelope.Requests.Head with OccurredAt = Some envelope.Timeline.Head.Timestamp }
                    let value = { envelope with BaseCommit = baseCommit; Agent = runtimeActor; Requests = [ request ] }
                    FileEnvelopeReconciliationDispatcher.apply root input "envelope-hash" baseCommit (fun () -> DateTimeOffset.Parse("2026-09-26T10:03:00Z")) value
                    |> Result.defaultWith (fun failure -> failwithf "%A" failure)
                    Assert.isTrue (not (File.Exists input)) "accepted envelope was not removed"
                    Assert.isTrue (File.Exists(Path.Combine(root, ".ros", "context", "current.json"))) "canonical context missing"
                    Assert.isTrue (FileEnvelopeReconciliationTransaction.checkpointExists root "tx-1") "checkpoint tag missing"
                    Assert.isTrue ((FileEnvelopeReconciliationStore.create root).IsApplied "tx-1") "receipt and checkpoint were not recognized"
                    Assert.equal 1 (File.ReadAllLines(Path.Combine(root, ".ros", "events", "events.jsonl")).Length)
                    Assert.isTrue ((runGit root [ "show"; "-s"; "--format=%B"; "HEAD" ]).Contains "Praxis-Reconciliation: tx-1") "canonical state was not committed") }

          { Name = "tracked accepted input is removed only after the reconciliation checkpoint exists"
            Run = fun () ->
                withGitRepository (fun root _ ->
                    let inbox = Path.Combine(root, ".praxis", "outbox", "events")
                    Directory.CreateDirectory inbox |> ignore
                    let input = Path.Combine(inbox, "tx-tracked.json")
                    File.WriteAllText(input, "accepted envelope\n")
                    runGit root [ "add"; ".praxis/outbox/events/tx-tracked.json" ] |> ignore
                    runGit root [ "commit"; "-qm"; "track envelope" ] |> ignore
                    let baseCommit = runGit root [ "rev-parse"; "HEAD" ]
                    let request = { envelope.Requests.Head with OccurredAt = Some envelope.Timeline.Head.Timestamp }
                    let runtimeActor = { actor with Model = Some "unknown"; Runtime = Some "codex" }
                    let value = { envelope with TransactionId = "tx-tracked"; BaseCommit = baseCommit; Agent = runtimeActor; Requests = [ request ] }
                    FileEnvelopeReconciliationDispatcher.apply root input "envelope-hash" baseCommit (fun () -> DateTimeOffset.Parse("2026-09-26T10:03:00Z")) value
                    |> Result.defaultWith (fun failure -> failwithf "%A" failure)
                    Assert.isTrue (not (File.Exists input)) "tracked envelope remains in the working tree"
                    Assert.equal "accepted envelope" (runGit root [ "show"; "praxis-reconcile/tx-tracked:.praxis/outbox/events/tx-tracked.json" ])
                    let headListing = runGit root [ "ls-tree"; "--name-only"; "HEAD"; "--"; ".praxis/outbox/events/tx-tracked.json" ]
                    Assert.equal "" headListing) }

          { Name = "dispatcher rejects an unregistered normalized metric before canonical mutation"
            Run = fun () ->
                withGitRepository (fun root baseCommit ->
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "input")
                    let runtimeActor = { actor with Model = Some "unknown"; Runtime = Some "codex" }
                    let unknown =
                        { MeasurementId = "MEAS-UNKNOWN"; MetricId = "provider.private"; Value = Some 1.0; Unit = Some "count"; Currency = None
                          Quality = Some "reported"; Availability = "reported"; RawJson = "{}" }
                    let request = { envelope.Requests.Head with OccurredAt = Some envelope.Timeline.Head.Timestamp }
                    let step = { validStep with Actor = runtimeActor; Measurements = [ unknown ] }
                    let value =
                        { envelope with
                            BaseCommit = baseCommit
                            Agent = runtimeActor
                            Requests = [ request ]
                            Execution = Some { ExecutionId = "EXE-FALLBACK"; StartedAt = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Steps = [ step ] } }
                    match FileEnvelopeReconciliationDispatcher.apply root input "hash" baseCommit (fun () -> DateTimeOffset.Parse("2026-09-26T10:03:00Z")) value with
                    | Error(EnvelopeApplyFailure.Invalid [ code ]) -> Assert.equal "unknown-normalized-metric:provider.private" code
                    | outcome -> failwithf "expected invalid metric, got %A" outcome
                    Assert.isTrue (not (File.Exists(Path.Combine(root, ".ros", "context", "current.json")))) "invalid metric mutated canonical context") }

          { Name = "dispatcher rejects raw telemetry over repository limits before canonical mutation"
            Run = fun () ->
                withGitRepository (fun root baseCommit ->
                    File.WriteAllText(Path.Combine(root, "ros.json"), """{"repository":"test-repository","protocolVersion":"1.0.0","telemetry":{"enabled":true,"maxRawPayloadBytes":1024},"workProtocol":{"completionEvidence":{"default":[],"research":[]}}}""")
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "input")
                    let runtimeActor = { actor with Model = Some "unknown"; Runtime = Some "codex" }
                    let request = { envelope.Requests.Head with OccurredAt = Some envelope.Timeline.Head.Timestamp }
                    let step = { validStep with Actor = runtimeActor; RawTelemetry = [ "{\"blob\":\"" + String.replicate 1100 "x" + "\"}" ] }
                    let value =
                        { envelope with
                            BaseCommit = baseCommit
                            Agent = runtimeActor
                            Requests = [ request ]
                            Execution = Some { ExecutionId = "EXE-FALLBACK"; StartedAt = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Steps = [ step ] } }
                    match FileEnvelopeReconciliationDispatcher.apply root input "hash" baseCommit (fun () -> DateTimeOffset.Parse("2026-09-26T10:03:00Z")) value with
                    | Error(EnvelopeApplyFailure.Invalid [ code ]) -> Assert.isTrue (code.StartsWith "raw-snapshot-byte-limit:") "wrong raw-retention finding"
                    | outcome -> failwithf "expected raw retention rejection, got %A" outcome
                    Assert.isTrue (not (File.Exists(Path.Combine(root, ".ros", "context", "current.json")))) "oversized raw telemetry mutated canonical context") }

          { Name = "dispatcher imports a step execution that passes canonical telemetry validation"
            Run = fun () ->
                withGitRepository (fun root baseCommit ->
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "input")
                    let runtimeActor = { actor with Model = Some "unknown"; Runtime = Some "codex" }
                    let measured =
                        { MeasurementId = "MEAS-ZERO"; MetricId = "tokens.input"; Value = Some 0.0; Unit = Some "tokens"; Currency = None
                          Quality = Some "reported"; Availability = "reported"; RawJson = "{\"providerField\":17}" }
                    let request = { envelope.Requests.Head with OccurredAt = Some envelope.Timeline.Head.Timestamp }
                    let step = { validStep with Actor = runtimeActor; Measurements = [ measured ] }
                    let value =
                        { envelope with
                            BaseCommit = baseCommit
                            Agent = runtimeActor
                            Requests = [ request ]
                            Execution = Some { ExecutionId = "EXE-FALLBACK"; StartedAt = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Steps = [ step ] } }
                    FileEnvelopeReconciliationDispatcher.apply root input "hash" baseCommit (fun () -> DateTimeOffset.Parse("2026-09-26T10:03:00Z")) value
                    |> Result.defaultWith (fun failure -> failwithf "%A" failure)
                    Assert.equal [] (FileTelemetryValidationRepository.findings root)) }

          { Name = "dispatcher refuses to complete work while a linked native execution remains active"
            Run = fun () ->
                withGitRepository (fun root baseCommit ->
                    let runtimeActor = { actor with Model = Some "unknown"; Runtime = Some "codex" }
                    let activeStep = { validStep with Actor = runtimeActor; Status = "active"; CompletedAt = None; EndedAt = None; Transitions = [ validStep.Transitions.Head ] }
                    let beginInput = Path.Combine(root, "begin.json")
                    File.WriteAllText(beginInput, "begin")
                    let beginRequest = { envelope.Requests.Head with OccurredAt = Some envelope.Timeline.Head.Timestamp }
                    let beginEnvelope =
                        { envelope with
                            BaseCommit = baseCommit
                            Agent = runtimeActor
                            Requests = [ beginRequest ]
                            Execution = Some { ExecutionId = "EXE-ACTIVE"; StartedAt = DateTimeOffset.Parse("2026-09-26T10:00:00Z"); Steps = [ { activeStep with ExecutionId = "EXE-ACTIVE" } ] } }
                    FileEnvelopeReconciliationDispatcher.apply root beginInput "begin-hash" baseCommit (fun () -> DateTimeOffset.Parse("2026-09-26T10:01:00Z")) beginEnvelope
                    |> Result.defaultWith (fun failure -> failwithf "%A" failure)

                    let completeInput = Path.Combine(root, "complete.json")
                    File.WriteAllText(completeInput, "complete")
                    let completedAt = DateTimeOffset.Parse("2026-09-26T10:03:00Z")
                    let completeEnvelope =
                        { envelope with
                            TransactionId = "tx-complete"
                            BaseCommit = runGit root [ "rev-parse"; "HEAD" ]
                            Agent = runtimeActor
                            Timeline = [ { Sequence = 1; Timestamp = completedAt; Action = "complete" } ]
                            Requests = [ { RequestType = "work.complete"; OccurredAt = Some completedAt; WorkType = None; Reason = None; Conclusion = None; Evidence = [] } ] }
                    match FileEnvelopeReconciliationDispatcher.apply root completeInput "complete-hash" completeEnvelope.BaseCommit (fun () -> DateTimeOffset.Parse("2026-09-26T10:04:00Z")) completeEnvelope with
                    | Error(EnvelopeApplyFailure.Invalid [ code ]) -> Assert.equal "active-execution-requires-native-finalization:EXE-ACTIVE" code
                    | outcome -> failwithf "expected active execution rejection, got %A" outcome
                    Assert.isTrue (File.Exists completeInput) "rejected input was removed by the dispatcher") }

          { Name = "dispatcher preserves research conclusion and synchronizes a captured backlog completion"
            Run = fun () ->
                withGitRepository (fun root baseCommit ->
                    let queueDirectory = Path.Combine(root, ".ros", "work")
                    Directory.CreateDirectory queueDirectory |> ignore
                    File.WriteAllText(Path.Combine(queueDirectory, "queue.json"), "{\"schemaVersion\":\"1.0.0\",\"repository\":\"test-repository\",\"nextSeq\":2,\"items\":[{\"id\":\"WI-0064\",\"title\":\"fallback\",\"status\":\"ready\",\"updatedAt\":\"2026-09-26T09:00:00Z\"}]}\n")
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "input")
                    let runtimeActor = { actor with Model = Some "unknown"; Runtime = Some "codex" }
                    let startedAt = DateTimeOffset.Parse("2026-09-26T10:00:00Z")
                    let completedAt = DateTimeOffset.Parse("2026-09-26T10:02:00Z")
                    let value =
                        { envelope with
                            BaseCommit = baseCommit
                            Agent = runtimeActor
                            Timeline =
                                [ { Sequence = 1; Timestamp = startedAt; Action = "start" }
                                  { Sequence = 2; Timestamp = completedAt; Action = "complete" } ]
                            Requests =
                                [ { envelope.Requests.Head with OccurredAt = Some startedAt; WorkType = Some "research" }
                                  { RequestType = "work.complete"; OccurredAt = Some completedAt; WorkType = None; Reason = None; Conclusion = Some "supported"; Evidence = [] } ] }
                    FileEnvelopeReconciliationDispatcher.apply root input "hash" baseCommit (fun () -> DateTimeOffset.Parse("2026-09-26T10:03:00Z")) value
                    |> Result.defaultWith (fun failure -> failwithf "%A" failure)
                    let context = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json"))) :?> JsonObject
                    let item = (context["workItems"] :?> JsonArray)[0] :?> JsonObject
                    let queue = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros", "work", "queue.json"))) :?> JsonObject
                    let queueItem = (queue["items"] :?> JsonArray)[0] :?> JsonObject
                    Assert.equal "supported" (item["conclusion"].GetValue<string>())
                    Assert.equal "complete" (queueItem["status"].GetValue<string>())) }

          { Name = "prepared reconciliation transaction recovers without duplicating its canonical event"
            Run = fun () ->
                withGitRepository (fun root _ ->
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "input")
                    let writes =
                        [ { Path = ".ros/events/events.jsonl"; Content = "{\"eventId\":\"one\"}\n" }
                          { Path = ".ros/context/current.json"; Content = "{\"schemaVersion\":\"1.0.0\",\"workItems\":[]}\n" }
                          { Path = ".praxis/reconciled/tx-recover.json"; Content = "{\"status\":\"prepared\"}\n" } ]
                    FileEnvelopeReconciliationTransaction.prepare root { TransactionId = "tx-recover"; EnvelopePath = input; Writes = writes }
                    |> Result.defaultWith failwith |> ignore
                    let recovered = FileEnvelopeReconciliationTransaction.recoverPending root |> Result.defaultWith failwith |> List.exactlyOne
                    Assert.equal "tx-recover" recovered.TransactionId
                    Assert.equal 1 (File.ReadAllLines(Path.Combine(root, ".ros", "events", "events.jsonl")).Length)
                    Assert.isTrue (not (File.Exists input)) "recovery did not remove input"
                    Assert.equal [] (FileEnvelopeReconciliationTransaction.recoverPending root |> Result.defaultWith failwith)) }

          { Name = "indeterminate apply remains pending and is never quarantined as invalid"
            Run = fun () ->
                let mutable receipts = 0
                let mutable quarantines = 0
                let store: EnvelopeReconciliationStore =
                    { IsApplied = fun _ -> false
                      ReadAppliedHash = fun _ -> None
                      WriteReceipt = fun _ -> receipts <- receipts + 1
                      Quarantine = fun _ _ -> quarantines <- quarantines + 1 }
                let effects: EnvelopeReconciliationEffects =
                    { Observe = fun _ -> observed
                      ApplyRequests = fun _ -> Error(EnvelopeApplyFailure.Indeterminate "git interrupted")
                      HashEnvelope = fun _ -> "hash"
                      Clock = fun () -> DateTimeOffset.Parse("2026-09-26T10:03:00Z") }
                match EnvelopeReconciliationOperations.reconcile store effects envelope with
                | EnvelopeReconciliationOutcome.Pending("tx-1", "git interrupted") -> ()
                | value -> failwithf "expected pending, got %A" value
                Assert.equal 0 receipts
                Assert.equal 0 quarantines }

          { Name = "rejected reconciliation preserves the exact original input bytes"
            Run = fun () ->
                let root = Path.Combine(Path.GetTempPath(), $"praxis-rejected-input-{Guid.NewGuid():N}")
                Directory.CreateDirectory root |> ignore
                try
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "{\"transactionId\":\"tx-step\",\"unknownProviderField\":17}\n")
                    let expected = File.ReadAllBytes input
                    let store = FileEnvelopeReconciliationStore.createWithInput root input
                    store.Quarantine stepEnvelope [ "invalid" ]
                    let actual = File.ReadAllBytes(Path.Combine(root, ".praxis", "rejected", "tx-step.input.json"))
                    Assert.equal expected actual
                finally Directory.Delete(root, true) }

          { Name = "tampered working receipt is not accepted as an applied transaction"
            Run = fun () ->
                withGitRepository (fun root baseCommit ->
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "accepted envelope")
                    let request = { envelope.Requests.Head with OccurredAt = Some envelope.Timeline.Head.Timestamp }
                    let runtimeActor = { actor with Model = Some "unknown"; Runtime = Some "codex" }
                    let value = { envelope with BaseCommit = baseCommit; Agent = runtimeActor; Requests = [ request ] }
                    FileEnvelopeReconciliationDispatcher.apply root input "envelope-hash" baseCommit (fun () -> DateTimeOffset.Parse("2026-09-26T10:03:00Z")) value
                    |> Result.defaultWith (fun failure -> failwithf "%A" failure)
                    let receipt = Path.Combine(root, ".praxis", "reconciled", "tx-1.json")
                    File.WriteAllText(receipt, File.ReadAllText(receipt).Replace("envelope-hash", "tampered-hash"))
                    Assert.isTrue (not ((FileEnvelopeReconciliationStore.create root).IsApplied "tx-1")) "tampered receipt was treated as applied") }

          { Name = "recovery ignores a matching trailer whose commit lacks the prepared canonical state"
            Run = fun () ->
                withGitRepository (fun root _ ->
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "input")
                    let writes =
                        [ { Path = ".ros/events/events.jsonl"; Content = "{\"eventId\":\"one\"}\n" }
                          { Path = ".ros/context/current.json"; Content = "{\"schemaVersion\":\"1.0.0\",\"workItems\":[]}\n" }
                          { Path = ".praxis/reconciled/tx-forged.json"; Content = "{}\n" } ]
                    FileEnvelopeReconciliationTransaction.prepare root { TransactionId = "tx-forged"; EnvelopePath = input; Writes = writes }
                    |> Result.defaultWith failwith |> ignore
                    File.WriteAllText(Path.Combine(root, "unrelated.txt"), "unrelated\n")
                    runGit root [ "add"; "unrelated.txt" ] |> ignore
                    runGit root [ "commit"; "-qm"; "unrelated"; "-m"; "Praxis-Reconciliation: tx-forged" ] |> ignore
                    let forged = runGit root [ "rev-parse"; "HEAD" ]
                    let recovered = FileEnvelopeReconciliationTransaction.recoverPending root |> Result.defaultWith failwith |> List.exactlyOne
                    Assert.isTrue (recovered.Commit <> forged) "recovery trusted the trailer without verifying canonical state"
                    Assert.equal "{\"eventId\":\"one\"}" (runGit root [ "show"; $"{recovered.Commit}:.ros/events/events.jsonl" ])) }

          { Name = "recovery rejects a journal supplied as tracked repository input"
            Run = fun () ->
                withGitRepository (fun root _ ->
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "input")
                    let writes =
                        [ { Path = ".ros/events/events.jsonl"; Content = "" }
                          { Path = ".ros/context/current.json"; Content = "{\"schemaVersion\":\"1.0.0\",\"workItems\":[]}\n" }
                          { Path = ".praxis/reconciled/tx-untrusted.json"; Content = "{}\n" } ]
                    let journal =
                        FileEnvelopeReconciliationTransaction.prepare root { TransactionId = "tx-untrusted"; EnvelopePath = input; Writes = writes }
                        |> Result.defaultWith failwith
                    runGit root [ "add"; "-f"; Path.GetRelativePath(root, journal) ] |> ignore
                    runGit root [ "commit"; "-qm"; "untrusted journal" ] |> ignore
                    match FileEnvelopeReconciliationTransaction.recoverPending root with
                    | Error message -> Assert.isTrue (message.Contains "tracked reconciliation journal is untrusted") "wrong tracked-journal failure"
                    | Ok value -> failwithf "expected rejection, got %A" value) }

          { Name = "transaction preparation rejects canonical writes redirected through a symbolic link"
            Run = fun () ->
                let root = Path.Combine(Path.GetTempPath(), $"praxis-symlink-root-{Guid.NewGuid():N}")
                let outside = Path.Combine(Path.GetTempPath(), $"praxis-symlink-outside-{Guid.NewGuid():N}")
                Directory.CreateDirectory(Path.Combine(root, ".ros")) |> ignore
                Directory.CreateDirectory outside |> ignore
                try
                    Directory.CreateSymbolicLink(Path.Combine(root, ".ros", "context"), outside) |> ignore
                    let input = Path.Combine(root, "fallback.json")
                    File.WriteAllText(input, "input")
                    let plan =
                        { TransactionId = "tx-symlink"
                          EnvelopePath = input
                          Writes = [ { Path = ".ros/context/current.json"; Content = "{}\n" } ] }
                    match FileEnvelopeReconciliationTransaction.prepare root plan with
                    | Error message -> Assert.isTrue (message.Contains "outside the canonical allowlist") "wrong symlink rejection"
                    | Ok value -> failwithf "expected symlink rejection, got %s" value
                    Assert.isTrue (not (File.Exists(Path.Combine(outside, "current.json")))) "symlink target was written"
                finally
                    Directory.Delete(root, true)
                    Directory.Delete(outside, true) } ]
