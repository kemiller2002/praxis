namespace Praxis.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Cli
open Praxis.Domain.Provenance
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module StepTelemetryTests =
    let private withRoot run =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-step-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try run root
        finally Directory.Delete(root, true)

    let private actor =
        { Kind = ActorKind.Agent
          Id = "openai/codex"
          Provider = Some "openai"
          Model = Some "unknown"
          Runtime = Some "codex" }

    let private identity session =
        { Provider = "openai"
          Model = None
          ModelVersion = None
          Runtime = "codex"
          RuntimeVersion = None
          SessionId = Some session
          ConversationId = Some session
          RunId = None
          AgentId = None
          SubagentId = None
          ParentExecutionId = None }

    let private executionFile root = Path.Combine(root, ".ros", "telemetry", "executions", "EXE-STEP.json")

    let private writeRegistry root =
        Directory.CreateDirectory(Path.Combine(root, "telemetry")) |> ignore
        File.WriteAllText(
            Path.Combine(root, "telemetry", "metrics.json"),
            """{"schemaVersion":"1.0.0","metrics":[
              {"id":"tokens.input","unit":"tokens","aggregation":"sum","collection":"runtime"},
              {"id":"tokens.total","unit":"tokens","aggregation":"sum","collection":"runtime"},
              {"id":"session.tokens.cumulative","unit":"tokens","aggregation":"latest-per-session","collection":"runtime"},
              {"id":"cost.step_total","unit":"currency","aggregation":"sum","collection":"runtime-or-calculated"}
            ]}"""
        )

    let private writeExecution root =
        writeRegistry root
        Directory.CreateDirectory(Path.GetDirectoryName(executionFile root)) |> ignore
        File.WriteAllText(
            executionFile root,
            """{"schemaVersion":"1.0.0","executionId":"EXE-STEP","workItemId":"WI-STEP","status":"active","startedAt":"2026-01-01T00:00:00.000Z","finalizedAt":null,
            "identity":{"provider":"openai","model":null,"runtime":"codex","sessionId":"session-1","conversationId":"session-1","actorKind":"agent"},
            "provenance":{"collector":"ros","collectorVersion":"1.0.0","discoveredAt":"2026-01-01T00:00:00.000Z","sources":[{"type":"environment","name":"runtime-identity","mechanism":"whitelisted-codex-environment"}]},
            "classification":{"types":["development"]},"capabilities":[],"metrics":[],"rawTelemetry":[],"events":[],
            "repository":{"start":{"available":false}},"links":{},"scope":{},"qualitySignals":[]}"""
        )

    let private readExecution root = JsonNode.Parse(File.ReadAllText(executionFile root)).AsObject()

    let private stringField (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private arrayObjects (node: JsonObject) (name: string) : JsonObject list =
        match node[name] with
        | :? JsonArray as values -> values |> Seq.choose (function :? JsonObject as item -> Some item | _ -> None) |> Seq.toList
        | _ -> []

    let private createRequest status at parent name =
        { ExecutionId = Some "EXE-STEP"
          Name = name
          Description = Some $"description:{name}"
          Classifications = [ "implementation"; "x-custom" ]
          ParentStepId = parent
          Status = status
          OccurredAt = at
          Actor = actor
          Identity = identity "session-1" }

    let private transitionRequest stepId target at =
        { ExecutionId = Some "EXE-STEP"
          StepId = stepId
          Target = target
          Reason = None
          OccurredAt = at
          Actor = actor
          Identity = identity "session-1" }

    let private beginStep root at parent name =
        match FileStepRepository.create root (createRequest StepStatus.Active at parent name) with
        | Ok step -> stringField step "stepId" |> Option.get
        | Error message -> failwith message

    let private complete root stepId at =
        match FileStepRepository.transition root (transitionRequest (Some stepId) StepStatus.Completed at) with
        | Ok value -> value
        | Error message -> failwith message

    let private metricRequest stepId metricId value at =
        { ExecutionId = Some "EXE-STEP"
          StepId = Some stepId
          MetricId = metricId
          Value = value
          Unit = None
          Currency = None
          Quality = "observed"
          Confidence = null
          CollectedAt = at
          Source = ({ Type = "runtime-api"; Name = "test-provider"; Mechanism = "reported" }: CapabilitySource)
          PricingSource = None
          PricingVersion = None
          PricingEffectiveAt = None
          CalculationMethod = None
          Model = None
          TokenMeasurementIds = []
          Actor = actor
          Identity = identity "session-1" }

    let private addCumulative (root: string) (measurementId: string) (value: float) (at: string) =
        let record = readExecution root
        let metric = JsonObject()
        metric["measurementId"] <- JsonValue.Create measurementId
        metric["id"] <- JsonValue.Create "session.tokens.cumulative"
        metric["value"] <- JsonValue.Create value
        metric["unit"] <- JsonValue.Create "tokens"
        metric["currency"] <- null
        metric["quality"] <- JsonValue.Create "observed"
        metric["scope"] <- JsonValue.Create "session"
        metric["aggregation"] <- JsonValue.Create "latest-per-session"
        metric["dimensions"] <- JsonObject()
        metric["pricing"] <- null
        metric["source"] <- JsonNode.Parse("""{"type":"runtime-api","name":"provider","mechanism":"cumulative"}""")
        metric["collectedAt"] <- JsonValue.Create at
        metric["schemaVersion"] <- JsonValue.Create "1.0.0"
        (record["metrics"] :?> JsonArray).Add(metric: JsonNode)
        File.WriteAllText(executionFile root, record.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n")

    let private checkpointRequest stepId phase ids at =
        { ExecutionId = Some "EXE-STEP"
          StepId = Some stepId
          Phase = phase
          MeasurementIds = ids
          SnapshotIds = []
          OccurredAt = at
          Actor = actor
          Identity = identity "session-1" }

    let tests =
        [ { Name = "step begin and complete preserve stable identity, name, order, and actor"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let stepId = beginStep root "2026-01-01T00:01:00Z" None "Implement feature"
                    let completed = complete root stepId "2026-01-01T00:02:00Z"
                    Assert.equal (Some stepId) (stringField completed "stepId")
                    Assert.equal (Some "Implement feature") (stringField completed "name")
                    Assert.equal (Some "completed") (stringField completed "status")
                    let actorNode = completed["actor"].AsObject()
                    Assert.equal (Some "openai/codex") (stringField actorNode "id")) }

          { Name = "multiple sequential steps receive deterministic increasing order"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let one = beginStep root "2026-01-01T00:01:00Z" None "one"
                    complete root one "2026-01-01T00:02:00Z" |> ignore
                    let two = beginStep root "2026-01-01T00:03:00Z" None "two"
                    complete root two "2026-01-01T00:04:00Z" |> ignore
                    let listed = FileStepRepository.list root (Some "EXE-STEP") None
                    Assert.equal [ 1; 2 ] (listed |> List.map (fun step -> step["sequence"].GetValue<int>())) ) }

          { Name = "nested steps form one active ancestor path and preserve parent identity"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let parent = beginStep root "2026-01-01T00:01:00Z" None "parent"
                    let child = beginStep root "2026-01-01T00:02:00Z" (Some parent) "child"
                    let shown = FileStepRepository.show root child |> Result.defaultWith failwith
                    Assert.equal (Some parent) (stringField shown "parentStepId")
                    match FileStepRepository.transition root (transitionRequest (Some parent) StepStatus.Completed "2026-01-01T00:03:00Z") with
                    | Ok _ -> failwith "parent completed with active child"
                    | Error message -> Assert.isTrue (message.Contains "unresolved descendant") message
                    complete root child "2026-01-01T00:04:00Z" |> ignore
                    complete root parent "2026-01-01T00:05:00Z" |> ignore) }

          { Name = "conflicting active sibling and unrelated step are rejected"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let parent = beginStep root "2026-01-01T00:01:00Z" None "parent"
                    match FileStepRepository.create root (createRequest StepStatus.Active "2026-01-01T00:02:00Z" None "other") with
                    | Ok _ -> failwith "conflicting step accepted"
                    | Error message -> Assert.isTrue (message.Contains "already active") message
                    let child = beginStep root "2026-01-01T00:03:00Z" (Some parent) "child"
                    match FileStepRepository.create root (createRequest StepStatus.Active "2026-01-01T00:04:00Z" (Some parent) "sibling") with
                    | Ok _ -> failwith "active sibling accepted"
                    | Error message -> Assert.isTrue (message.Contains "active leaf") message
                    complete root child "2026-01-01T00:05:00Z" |> ignore
                    complete root parent "2026-01-01T00:06:00Z" |> ignore) }

          { Name = "illegal duplicate completion and time regression are rejected without repair"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    match FileStepRepository.create root (createRequest StepStatus.Active "2025-12-31T23:59:00Z" None "early") with
                    | Ok _ -> failwith "pre-execution timestamp accepted"
                    | Error message -> Assert.isTrue (message.Contains "must not be before") message
                    let step = beginStep root "2026-01-01T00:01:00Z" None "step"
                    complete root step "2026-01-01T00:02:00Z" |> ignore
                    match FileStepRepository.transition root (transitionRequest (Some step) StepStatus.Completed "2026-01-01T00:03:00Z") with
                    | Ok _ -> failwith "double completion accepted"
                    | Error message -> Assert.isTrue (message.Contains "cannot transition") message) }

          { Name = "blocked step requires a reason, resumes without losing startedAt, and rejects transition time regression"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let step = beginStep root "2026-01-01T00:01:00Z" None "interruptible"
                    match FileStepRepository.transition root (transitionRequest (Some step) StepStatus.Blocked "2026-01-01T00:02:00Z") with
                    | Ok _ -> failwith "reasonless block accepted"
                    | Error message -> Assert.isTrue (message.Contains "requires --reason") message
                    let blocked = { transitionRequest (Some step) StepStatus.Blocked "2026-01-01T00:02:00Z" with Reason = Some "dependency unavailable" }
                    FileStepRepository.transition root blocked |> Result.defaultWith failwith |> ignore
                    match FileStepRepository.transition root (transitionRequest (Some step) StepStatus.Active "2026-01-01T00:01:30Z") with
                    | Ok _ -> failwith "time-regressing resume accepted"
                    | Error message -> Assert.isTrue (message.Contains "prior transition") message
                    let resumed = FileStepRepository.transition root (transitionRequest (Some step) StepStatus.Active "2026-01-01T00:03:00Z") |> Result.defaultWith failwith
                    Assert.equal (Some "2026-01-01T00:01:00Z") (stringField resumed "startedAt")) }

          { Name = "a planned step may be abandoned without inventing a start time"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let planned = FileStepRepository.create root (createRequest StepStatus.Planned "2026-01-01T00:01:00Z" None "optional") |> Result.defaultWith failwith
                    let stepId = stringField planned "stepId" |> Option.get
                    let abandoned = FileStepRepository.transition root (transitionRequest (Some stepId) StepStatus.Abandoned "2026-01-01T00:02:00Z") |> Result.defaultWith failwith
                    Assert.equal None (stringField abandoned "startedAt")
                    Assert.empty (FileStepRepository.findings (readExecution root))) }

          { Name = "explicit execution mutation rejects another agent session"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let request = { createRequest StepStatus.Active "2026-01-01T00:01:00Z" None "wrong-session" with Identity = identity "session-2" }
                    match FileStepRepository.create root request with
                    | Ok _ -> failwith "cross-session mutation accepted"
                    | Error message -> Assert.isTrue (message.Contains "does not own") message) }

          { Name = "zero token usage is an observed value distinct from unavailable"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let step = beginStep root "2026-01-01T00:01:00Z" None "measure"
                    FileStepRepository.recordMetric root (metricRequest step "tokens.input" 0.0 "2026-01-01T00:01:10Z") |> Result.defaultWith failwith |> ignore
                    FileStepRepository.recordAvailability root
                        { ExecutionId = Some "EXE-STEP"; StepId = Some step; MetricId = "tokens.total"; Status = "supported-unavailable"; Reason = "provider omitted usage"; OccurredAt = "2026-01-01T00:01:20Z"; Actor = actor; Identity = identity "session-1" }
                    |> Result.defaultWith failwith |> ignore
                    let shown = FileStepRepository.show root step |> Result.defaultWith failwith
                    let measurements = shown["summary"].AsObject()["measurements"] :?> JsonArray
                    Assert.equal 1 measurements.Count
                    let firstMeasurement = measurements[0].AsObject()
                    Assert.equal 0.0 (firstMeasurement["value"].GetValue<float>())
                    let capabilities = shown["telemetry"].AsObject()["capabilities"] :?> JsonArray
                    Assert.isTrue (capabilities |> Seq.exists (fun item -> item["metricId"].GetValue<string>() = "tokens.total" && item["status"].GetValue<string>() = "supported-unavailable")) "unavailable state missing") }

          { Name = "availability cannot contradict an already recorded step value"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let step = beginStep root "2026-01-01T00:01:00Z" None "measure"
                    FileStepRepository.recordMetric root (metricRequest step "tokens.input" 0.0 "2026-01-01T00:01:10Z") |> Result.defaultWith failwith |> ignore
                    let unavailable =
                        { ExecutionId = Some "EXE-STEP"; StepId = Some step; MetricId = "tokens.input"; Status = "supported-unavailable"; Reason = "contradiction"; OccurredAt = "2026-01-01T00:01:20Z"; Actor = actor; Identity = identity "session-1" }
                    match FileStepRepository.recordAvailability root unavailable with
                    | Ok _ -> failwith "contradictory availability accepted"
                    | Error message -> Assert.isTrue (message.Contains "would contradict") message) }

          { Name = "compatible cumulative checkpoints derive a provenance-linked step delta"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let step = beginStep root "2026-01-01T00:01:00Z" None "delta"
                    addCumulative root "MEAS-BEGIN" 100.0 "2026-01-01T00:01:00Z"
                    FileStepRepository.checkpoint root (checkpointRequest step "begin" [ "MEAS-BEGIN" ] "2026-01-01T00:01:01Z") |> Result.defaultWith failwith |> ignore
                    addCumulative root "MEAS-END" 145.0 "2026-01-01T00:02:00Z"
                    let ending = FileStepRepository.checkpoint root (checkpointRequest step "end" [ "MEAS-END" ] "2026-01-01T00:02:01Z") |> Result.defaultWith failwith
                    Assert.equal (Some "derived") (stringField ending "deltaStatus")
                    let record = readExecution root
                    let delta = arrayObjects record "metrics" |> List.find (fun item -> stringField item "id" = Some "tokens.total")
                    Assert.equal 45.0 (delta["value"].GetValue<float>())
                    let derivation = delta["derivation"].AsObject()
                    Assert.equal "MEAS-BEGIN" (derivation["beginMeasurementId"].GetValue<string>())) }

          { Name = "missing begin checkpoint records insufficiency and never invents a delta"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let step = beginStep root "2026-01-01T00:01:00Z" None "missing"
                    addCumulative root "MEAS-END" 50.0 "2026-01-01T00:02:00Z"
                    let ending = FileStepRepository.checkpoint root (checkpointRequest step "end" [ "MEAS-END" ] "2026-01-01T00:02:01Z") |> Result.defaultWith failwith
                    Assert.equal (Some "insufficient") (stringField ending "deltaStatus")
                    let metrics = arrayObjects (readExecution root) "metrics"
                    Assert.equal 1 metrics.Length) }

          { Name = "calculated cost requires pricing provenance and estimated cost stays estimated"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let step = beginStep root "2026-01-01T00:01:00Z" None "cost"
                    let calculated = { metricRequest step "cost.step_total" 0.25 "2026-01-01T00:01:10Z" with Currency = Some "USD"; Quality = "derived" }
                    match FileStepRepository.recordMetric root calculated with
                    | Ok _ -> failwith "calculated cost without pricing accepted"
                    | Error message -> Assert.isTrue (message.Contains "pricing-source") message
                    let unversioned = { calculated with PricingSource = Some "price-table" }
                    match FileStepRepository.recordMetric root unversioned with
                    | Ok _ -> failwith "calculated cost without pricing version accepted"
                    | Error message -> Assert.isTrue (message.Contains "pricing-version") message
                    let noConfidence = { calculated with Quality = "estimated" }
                    match FileStepRepository.recordMetric root noConfidence with
                    | Ok _ -> failwith "estimated cost without confidence accepted"
                    | Error message -> Assert.isTrue (message.Contains "confidence") message
                    let estimated = { calculated with Quality = "estimated"; Confidence = JsonValue.Create "low" }
                    FileStepRepository.recordMetric root estimated |> Result.defaultWith failwith |> ignore
                    let metric = arrayObjects (readExecution root) "metrics" |> List.exactlyOne
                    Assert.equal (Some "estimated") (stringField metric "quality")) }

          { Name = "checkpoint preserves sanitized raw snapshot references"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let record = readExecution root
                    let raw = JsonNode.Parse("""{"snapshotId":"SNAP-1","payload":{"secret":"[REDACTED_BY_ROS]"}}""")
                    (record["rawTelemetry"] :?> JsonArray).Add raw
                    File.WriteAllText(executionFile root, record.ToJsonString())
                    let step = beginStep root "2026-01-01T00:01:00Z" None "raw"
                    let request = { checkpointRequest step "begin" [] "2026-01-01T00:01:01Z" with SnapshotIds = [ "SNAP-1" ] }
                    FileStepRepository.checkpoint root request |> Result.defaultWith failwith |> ignore
                    let shown = FileStepRepository.show root step |> Result.defaultWith failwith
                    let telemetry = shown["telemetry"].AsObject()
                    let rawIds = telemetry["rawSnapshotIds"].AsArray()
                    Assert.equal "SNAP-1" (rawIds[0].GetValue<string>())) }

          { Name = "step relationship records evidence as a sourced reference rather than a file claim"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let step = beginStep root "2026-01-01T00:01:00Z" None "evidence"
                    FileStepRepository.link root
                        { ExecutionId = Some "EXE-STEP"; StepId = Some step; Kind = "file-observed"; Value = "src/example.fs"; Source = "agent-report"; OccurredAt = "2026-01-01T00:01:10Z"; Actor = actor; Identity = identity "session-1" }
                    |> Result.defaultWith failwith |> ignore
                    let shown = FileStepRepository.show root step |> Result.defaultWith failwith
                    let relationships = shown["relationships"].AsObject()
                    let observed = relationships["filesObserved"].AsArray()
                    let item = observed[0]
                    Assert.equal "agent-report" (item["source"].GetValue<string>())) }

          { Name = "step metrics aggregate once at execution scope without parent rollups"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let step = beginStep root "2026-01-01T00:01:00Z" None "aggregate"
                    FileStepRepository.recordMetric root (metricRequest step "tokens.input" 7.0 "2026-01-01T00:01:10Z") |> Result.defaultWith failwith |> ignore
                    let execution = FileTelemetryQueryRepository.readSummaryExecutions root (Some "WI-STEP") |> List.exactlyOne
                    Assert.equal 1 execution.Metrics.Length
                    let workItemSummary = TelemetrySummary.summarize (Some "WI-STEP") [ execution ]
                    Assert.equal 1 workItemSummary.StepCount
                    let metricSummary = workItemSummary.Metrics |> List.exactlyOne
                    Assert.equal (Some 7.0) metricSummary.Value) }

          { Name = "nested grouping parents cannot carry direct telemetry or become parents after measurement"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let measuredParent = beginStep root "2026-01-01T00:01:00Z" None "measured-parent"
                    FileStepRepository.recordMetric root (metricRequest measuredParent "tokens.input" 3.0 "2026-01-01T00:01:10Z") |> Result.defaultWith failwith |> ignore
                    match FileStepRepository.create root (createRequest StepStatus.Active "2026-01-01T00:02:00Z" (Some measuredParent) "child") with
                    | Ok _ -> failwith "measured parent accepted a child"
                    | Error message -> Assert.isTrue (message.Contains "cannot become a nesting parent") message)
                withRoot (fun root ->
                    writeExecution root
                    let parent = beginStep root "2026-01-01T00:01:00Z" None "group"
                    let child = beginStep root "2026-01-01T00:02:00Z" (Some parent) "leaf"
                    match FileStepRepository.recordMetric root (metricRequest parent "tokens.input" 3.0 "2026-01-01T00:02:10Z") with
                    | Ok _ -> failwith "grouping parent accepted telemetry"
                    | Error message -> Assert.isTrue (message.Contains "grouping parent") message
                    FileStepRepository.recordMetric root (metricRequest child "tokens.input" 3.0 "2026-01-01T00:02:10Z") |> Result.defaultWith failwith |> ignore) }

          { Name = "old execution without steps remains valid step history"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let record = readExecution root
                    Assert.empty (FileStepRepository.findings record)
                    Assert.empty (FileStepRepository.list root (Some "EXE-STEP") None)) }

          { Name = "execution finalization rejects an unresolved active step"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    beginStep root "2026-01-01T00:01:00Z" None "unfinished" |> ignore
                    match FileTelemetryFinalizationRepository.finalizeTarget root (Some "EXE-STEP") with
                    | Ok _ -> failwith "execution finalized with active step"
                    | Error message -> Assert.isTrue (message.Contains "unresolved steps") message) }

          { Name = "step list and show JSON are deterministic and CLI JSON is machine-readable"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    let step = beginStep root "2026-01-01T00:01:00Z" None "query"
                    let first = FileStepRepository.show root step |> Result.defaultWith failwith |> _.ToJsonString()
                    let second = FileStepRepository.show root step |> Result.defaultWith failwith |> _.ToJsonString()
                    Assert.equal first second
                    let prior = Console.Out
                    use writer = new StringWriter()
                    try
                        Console.SetOut writer
                        Assert.equal 0 (StepCommands.run root [ "list"; "--execution"; "EXE-STEP"; "--json" ])
                    finally Console.SetOut prior
                    use parsed = JsonDocument.Parse(writer.ToString())
                    Assert.equal 1 (parsed.RootElement.GetProperty("count").GetInt32())) }

          { Name = "stored contradictory finalized state is detected by validation"
            Run = fun () ->
                withRoot (fun root ->
                    writeExecution root
                    beginStep root "2026-01-01T00:01:00Z" None "active" |> ignore
                    let record = readExecution root
                    record["status"] <- JsonValue.Create "finalized"
                    record["finalizedAt"] <- JsonValue.Create "2026-01-01T00:02:00Z"
                    let findings = FileStepRepository.findings record
                    Assert.isTrue (findings |> List.exists (fun value -> value.Contains "finalized execution has unresolved")) "missing contradiction finding") } ]
