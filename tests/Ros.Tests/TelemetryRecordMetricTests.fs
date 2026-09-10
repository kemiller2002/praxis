namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Domain.Telemetry
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module TelemetryRecordMetricTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-record-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeMetricRegistry root =
        let directory = Path.Combine(root, "telemetry")
        Directory.CreateDirectory directory |> ignore

        File.WriteAllText(
            Path.Combine(directory, "metrics.json"),
            """{"schemaVersion":"1.0.0","metrics":[
                {"id":"agent.interruptions","unit":"count","aggregation":"sum","collection":"runtime-or-ros"},
                {"id":"cost.input","unit":"currency","aggregation":"sum","collection":"runtime-or-calculated"}
            ]}"""
        )

    let private executionFile root executionId =
        Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

    let private writeExecution root executionId workItemId status (startedAt: string) (capabilities: string) =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")
        Directory.CreateDirectory directory |> ignore

        let json =
            $"""{{"schemaVersion":"1.0.0","executionId":"{executionId}","workItemId":"{workItemId}","status":"{status}","startedAt":"{startedAt}","events":[],"metrics":[],"capabilities":{capabilities}}}"""

        File.WriteAllText(executionFile root executionId, json)

    let private writeContext root (workItems: (string * string) list) =
        let directory = Path.Combine(root, ".ros", "context")
        Directory.CreateDirectory directory |> ignore

        let items =
            workItems
            |> List.map (fun (id, semanticState) -> $"""{{"id":"{id}","semanticState":"{semanticState}"}}""")
            |> String.concat ","

        File.WriteAllText(Path.Combine(directory, "current.json"), $"""{{"schemaVersion":"1.0.0","workItems":[{items}]}}""")

    let private readExecution root executionId : JsonObject =
        match JsonNode.Parse(File.ReadAllText(executionFile root executionId)) with
        | :? JsonObject as record -> record
        | _ -> failwith "expected a JSON object"

    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private metricsOf (record: JsonObject) : JsonObject list =
        match record["metrics"] with
        | :? JsonArray as metrics -> metrics |> Seq.choose (function :? JsonObject as node -> Some node | _ -> None) |> Seq.toList
        | _ -> []

    let private capabilityFor (record: JsonObject) (metricId: string) : JsonObject option =
        match record["capabilities"] with
        | :? JsonArray as capabilities ->
            capabilities
            |> Seq.tryPick (function
                | :? JsonObject as node when stringField node "metricId" = Some metricId -> Some node
                | _ -> None)
        | _ -> None

    let private defaultSource: CapabilitySource =
        { Type = "agent-report"; Name = "ros-telemetry-cli"; Mechanism = "explicit-metric-record" }

    let private request metricId value : FileTelemetryFinalizationRepository.RecordMetricRequest =
        { MetricId = metricId
          Value = value
          Unit = None
          Currency = None
          Quality = "observed"
          Confidence = FileTelemetryFinalizationRepository.NoConfidence
          Scope = "execution"
          Source = defaultSource
          PricingSource = None
          PricingVersion = None
          CollectedAt = Some "2026-01-01T00:00:00.000Z" }

    let tests =
        [ { Name = "recordMetric with an EXE-prefixed target records a new metric and creates a fresh capability entry"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"

                    match FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") (request "agent.interruptions" 1.0) with
                    | Error message -> failwith message
                    | Ok record ->
                        let metrics = metricsOf record
                        Assert.equal 1 metrics.Length
                        Assert.equal (Some "agent.interruptions") (stringField metrics[0] "id")
                        Assert.equal (Some "count") (stringField metrics[0] "unit")
                        Assert.equal (Some "sum") (stringField metrics[0] "aggregation")
                        Assert.equal (Some "observed") (stringField metrics[0] "quality")

                        match capabilityFor record "agent.interruptions" with
                        | None -> failwith "expected a capability entry to be created"
                        | Some capability -> Assert.equal (Some "supported-observed") (stringField capability "status")) }

          { Name = "recordMetric with a work-item target matches only the currently-active execution, ignoring a finalized one"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-OLD" "WI-A" "finalized" "2026-01-01T00:00:00.000Z" "[]"
                    writeExecution root "EXE-NEW" "WI-A" "active" "2026-01-01T00:05:00.000Z" "[]"

                    match FileTelemetryFinalizationRepository.recordMetric root (Some "WI-A") (request "agent.interruptions" 1.0) with
                    | Error message -> failwith message
                    | Ok record ->
                        Assert.equal (Some "EXE-NEW") (stringField record "executionId")
                        let untouched = readExecution root "EXE-OLD"
                        Assert.equal 0 (metricsOf untouched).Length) }

          { Name = "recordMetric with no target and exactly one active-or-blocked work item resolves that item's active execution"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"
                    writeContext root [ "WI-A", "active" ]

                    match FileTelemetryFinalizationRepository.recordMetric root None (request "agent.interruptions" 1.0) with
                    | Error message -> failwith message
                    | Ok record -> Assert.equal (Some "EXE-1") (stringField record "executionId")) }

          { Name = "recordMetric with no target and zero active-or-blocked work items rejects with production's exact ambiguity message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"
                    writeContext root [ "WI-A", "ready" ]

                    match FileTelemetryFinalizationRepository.recordMetric root None (request "agent.interruptions" 1.0) with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "telemetry target is ambiguous; provide a work-item or execution ID" message) }

          { Name = "recordMetric with no target and multiple active-or-blocked work items rejects with the same ambiguity message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"
                    writeExecution root "EXE-2" "WI-B" "active" "2026-01-01T00:00:01.000Z" "[]"
                    writeContext root [ "WI-A", "active"; "WI-B", "blocked" ]

                    match FileTelemetryFinalizationRepository.recordMetric root None (request "agent.interruptions" 1.0) with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "telemetry target is ambiguous; provide a work-item or execution ID" message) }

          { Name = "recordMetric rejects a target whose only matching execution is not active, matching production's exact 'was not found or is already finalized' message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "finalized" "2026-01-01T00:00:00.000Z" "[]"

                    match FileTelemetryFinalizationRepository.recordMetric root (Some "WI-A") (request "agent.interruptions" 1.0) with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "telemetry execution 'WI-A' was not found or is already finalized" message) }

          { Name = "recordMetric rejects an unregistered metric id with production's exact message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"

                    match FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") (request "bogus.metric" 1.0) with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "unknown normalized metric 'bogus.metric'; preserve it in raw telemetry until it is registered" message) }

          { Name = "recordMetric rejects a non-finite value with production's exact message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"

                    match FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") (request "agent.interruptions" Double.NaN) with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "metric 'agent.interruptions' requires a finite numeric value" message) }

          { Name = "recordMetric deduplicates an identical repeated call by its content-addressed measurementId rather than appending twice"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"

                    let first = FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") (request "agent.interruptions" 1.0)
                    let second = FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") (request "agent.interruptions" 1.0)

                    match first, second with
                    | Error message, _
                    | _, Error message -> failwith message
                    | Ok _, Ok record -> Assert.equal 1 (metricsOf record).Length) }

          { Name = "recordMetric upserts an existing capability entry into history rather than discarding its previous status"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root

                    let capabilities =
                        """[{"metricId":"agent.interruptions","status":"unknown","reason":"runtime capability not reported or mapped","discoveredAt":"2025-12-31T00:00:00.000Z","source":{"type":"environment","name":"runtime-identity","mechanism":"whitelisted-claude-environment"}}]"""

                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" capabilities

                    match FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") (request "agent.interruptions" 1.0) with
                    | Error message -> failwith message
                    | Ok record ->
                        match capabilityFor record "agent.interruptions" with
                        | None -> failwith "expected the capability entry to still exist"
                        | Some capability ->
                            Assert.equal (Some "supported-observed") (stringField capability "status")

                            match capability["history"] with
                            | :? JsonArray as history ->
                                Assert.equal 1 history.Count

                                match history[0] with
                                | :? JsonObject as previous -> Assert.equal (Some "unknown") (stringField previous "status")
                                | _ -> failwith "expected a history entry object"
                            | _ -> failwith "expected a history array to have been created") }

          { Name = "recordMetric honors explicit --unit/--currency overrides of the registry's own defaults"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"

                    let overridden = { request "cost.input" 0.05 with Unit = Some "usd-cents"; Currency = Some "USD" }

                    match FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") overridden with
                    | Error message -> failwith message
                    | Ok record ->
                        let metrics = metricsOf record
                        Assert.equal (Some "usd-cents") (stringField metrics[0] "unit")
                        Assert.equal (Some "USD") (stringField metrics[0] "currency")) }

          { Name = "recordMetric stores a numeric --confidence as a number and a non-numeric one as text, matching production's permissive parsing"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"

                    let numeric = { request "agent.interruptions" 1.0 with Confidence = FileTelemetryFinalizationRepository.NumericConfidence 0.75 }

                    match FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") numeric with
                    | Error message -> failwith message
                    | Ok record ->
                        let metric = (metricsOf record).[0]

                        match metric["confidence"] with
                        | :? JsonValue as value -> Assert.equal 0.75 (value.GetValue<float>())
                        | _ -> failwith "expected a numeric confidence value")

                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "[]"

                    let text = { request "agent.interruptions" 1.0 with Confidence = FileTelemetryFinalizationRepository.TextConfidence "high" }

                    match FileTelemetryFinalizationRepository.recordMetric root (Some "EXE-1") text with
                    | Error message -> failwith message
                    | Ok record ->
                        let metric = (metricsOf record).[0]
                        Assert.equal (Some "high") (stringField metric "confidence")) } ]
