namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module TelemetryIngestTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-ingest-{Guid.NewGuid():N}")
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
                {"id":"tokens.input","unit":"tokens","aggregation":"sum","collection":"runtime"},
                {"id":"tool.calls","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"telemetry.redactions","unit":"count","aggregation":"sum","collection":"ros-derived"},
                {"id":"telemetry.unknown_fields","unit":"count","aggregation":"sum","collection":"ros-derived"},
                {"id":"telemetry.raw_snapshots_omitted","unit":"count","aggregation":"sum","collection":"ros-derived"}
            ]}"""
        )

    let private writeRosConfig root (json: string) =
        File.WriteAllText(Path.Combine(root, "ros.json"), json)

    let private executionFile root executionId =
        Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

    let private writeExecution root executionId workItemId status (startedAt: string) =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")
        Directory.CreateDirectory directory |> ignore

        let json =
            $"""{{"schemaVersion":"1.0.0","executionId":"{executionId}","workItemId":"{workItemId}","status":"{status}","startedAt":"{startedAt}",
                "identity":{{"provider":"anthropic","runtime":"claude-code"}},"provenance":{{"collector":"ros","collectorVersion":"1.0.0","discoveredAt":"{startedAt}","sources":[]}},
                "classification":{{"types":["development"],"rationale":null,"evidence":[],"rd":null}},
                "capabilities":[{{"metricId":"tool.calls","status":"unknown","reason":"runtime capability not reported or mapped","discoveredAt":"{startedAt}","source":{{"type":"environment","name":"runtime-identity","mechanism":"whitelisted-claude-environment"}}}}],
                "metrics":[],"rawTelemetry":[],"events":[],"repository":{{"start":{{"available":false}}}},"scope":{{}},"qualitySignals":[],"links":{{}}}}"""

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

    let private metricValue (record: JsonObject) (id: string) : float option =
        metricsOf record
        |> List.tryPick (fun m ->
            if stringField m "id" = Some id then
                match m["value"] with
                | :? JsonValue as v -> Some(v.GetValue<float>())
                | _ -> None
            else
                None)

    let private capabilityFor (record: JsonObject) (metricId: string option) (providerField: string option) : JsonObject option =
        match record["capabilities"] with
        | :? JsonArray as capabilities ->
            capabilities
            |> Seq.tryPick (function
                | :? JsonObject as node when stringField node "metricId" = metricId && stringField node "providerField" = providerField -> Some node
                | _ -> None)
        | _ -> None

    let private eventsOf (record: JsonObject) : JsonObject list =
        match record["events"] with
        | :? JsonArray as events -> events |> Seq.choose (function :? JsonObject as node -> Some node | _ -> None) |> Seq.toList
        | _ -> []

    let tests =
        [ { Name = "ingestTarget merges identity, normalizes metrics with capability upsert, appends a custom event, redacts sensitive raw fields, and records derived quality metrics"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    let input =
                        """{"identity":{"model":"claude-opus-5"},
                            "metrics":[{"id":"tokens.input","value":100}],
                            "events":[{"type":"custom.thing","occurredAt":"2026-01-01T00:00:00.000Z"}],
                            "raw":{"authorization":"secret","safe":"value"},
                            "collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Error message -> failwith message
                    | Ok record ->
                        Assert.equal (Some "claude-opus-5") (match record["identity"] with :? JsonObject as i -> stringField i "model" | _ -> None)
                        Assert.equal (Some 100.0) (metricValue record "tokens.input")

                        match capabilityFor record (Some "tokens.input") None with
                        | None -> failwith "expected a capability entry for tokens.input"
                        | Some capability -> Assert.equal (Some "supported-observed") (stringField capability "status")

                        let events = eventsOf record
                        Assert.equal true (events |> List.exists (fun e -> stringField e "type" = Some "custom.thing"))
                        Assert.equal true (events |> List.exists (fun e -> stringField e "type" = Some "telemetry.snapshot.ingested"))

                        let payload =
                            match record["rawTelemetry"] with
                            | :? JsonArray as array ->
                                match array[0] with
                                | :? JsonObject as snapshot -> snapshot["payload"]
                                | _ -> failwith "expected a snapshot object"
                            | _ -> failwith "expected a rawTelemetry array"

                        match payload with
                        | :? JsonObject as p -> Assert.equal (Some "[REDACTED_BY_ROS]") (stringField p "authorization")
                        | _ -> failwith "expected a payload object"

                        Assert.equal (Some 1.0) (metricValue record "telemetry.redactions")
                        Assert.equal (Some 2.0) (metricValue record "telemetry.unknown_fields")) }

          { Name = "ingestTarget deduplicates a repeated identical snapshot, leaving metrics/rawTelemetry untouched"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    // "safe" is itself an unmapped field (the generic adapter's
                    // `mappedFields` is always empty), so the first call also
                    // records `telemetry.unknown_fields` alongside `tokens.input`
                    // -- the assertion below is against that same total after a
                    // second, deduped call, not a hardcoded count.
                    let input = """{"metrics":[{"id":"tokens.input","value":1}],"raw":{"safe":"value"},"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Error message -> failwith message
                    | Ok firstRecord ->
                        let metricsAfterFirstCall = (metricsOf firstRecord).Length

                        match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                        | Error message -> failwith message
                        | Ok record ->
                            Assert.equal metricsAfterFirstCall (metricsOf record).Length

                            match record["rawTelemetry"] with
                            | :? JsonArray as array -> Assert.equal 1 array.Count
                            | _ -> failwith "expected a rawTelemetry array") }

          { Name = "ingestTarget merges a directly-declared capability, preserving the prior status in history"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    let input =
                        """{"capabilities":[{"metricId":"tool.calls","status":"supported-observed","reason":"custom","discoveredAt":"2026-01-01T00:00:00.000Z"}],
                            "collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Error message -> failwith message
                    | Ok record ->
                        match capabilityFor record (Some "tool.calls") None with
                        | None -> failwith "expected the tool.calls capability to still exist"
                        | Some capability ->
                            Assert.equal (Some "supported-observed") (stringField capability "status")
                            Assert.equal (Some "custom") (stringField capability "reason")

                            match capability["history"] with
                            | :? JsonArray as history ->
                                Assert.equal 1 history.Count

                                match history[0] with
                                | :? JsonObject as previous -> Assert.equal (Some "unknown") (stringField previous "status")
                                | _ -> failwith "expected a history entry object"
                            | _ -> failwith "expected a history array") }

          { Name = "ingestTarget rejects a snapshot that reports a metric value while declaring every capability for it unavailable"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    let input =
                        """{"metrics":[{"id":"tool.calls","value":3}],"capabilities":[{"metricId":"tool.calls","status":"unsupported"}],
                            "collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal true (message.Contains "declaring it unavailable or unsupported")) }

          { Name = "ingestTarget shallow-merges classification/scope (an explicit null overwrites, unlike identity's skip-null merge) and union-dedups links arrays"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    let input =
                        """{"classification":{"rationale":null},"scope":{"operationId":"op-1"},
                            "links":{"commits":["abc"]},"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Error message -> failwith message
                    | Ok record ->
                        match record["classification"] with
                        | :? JsonObject as c -> Assert.equal None (stringField c "rationale")
                        | _ -> failwith "expected a classification object"

                        match record["scope"] with
                        | :? JsonObject as s -> Assert.equal (Some "op-1") (stringField s "operationId")
                        | _ -> failwith "expected a scope object"

                        match record["links"] with
                        | :? JsonObject as l ->
                            match l["commits"] with
                            | :? JsonArray as commits -> Assert.equal 1 commits.Count
                            | _ -> failwith "expected a commits array"
                        | _ -> failwith "expected a links object")

                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    let firstInput = """{"links":{"commits":["abc"]},"collectedAt":"2026-01-01T00:00:00.000Z"}"""
                    let secondInput = """{"links":{"commits":["abc","def"]},"collectedAt":"2026-01-02T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" firstInput with
                    | Error message -> failwith message
                    | Ok _ ->
                        match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" secondInput with
                        | Error message -> failwith message
                        | Ok record ->
                            match record["links"] with
                            | :? JsonObject as l ->
                                match l["commits"] with
                                | :? JsonArray as commits -> Assert.equal 2 commits.Count
                                | _ -> failwith "expected a commits array"
                            | _ -> failwith "expected a links object") }

          { Name = "ingestTarget dedups quality signals by signalId and rejects an unregistered metric with production's exact message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let input = """{"qualitySignals":[{"detector":"test","outcome":"pass"}],"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Error message -> failwith message
                    | Ok _ ->
                        match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                        | Error message -> failwith message
                        | Ok record ->
                            match record["qualitySignals"] with
                            | :? JsonArray as signals -> Assert.equal 1 signals.Count
                            | _ -> failwith "expected a qualitySignals array")

                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let input = """{"metrics":[{"id":"bogus.metric","value":1}],"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "unknown normalized metric 'bogus.metric'; preserve it in raw telemetry until it is registered" message) }

          { Name = "ingestTarget rejects a target whose only matching execution is not active, and rejects ambiguous/unknown adapters before touching anything"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "finalized" "2026-01-01T00:00:00.000Z"
                    let input = """{"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "WI-A") "generic" input with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "telemetry execution 'WI-A' was not found or is already finalized" message)

                withTemporaryRoot (fun root ->
                    writeMetricRegistry root

                    match FileTelemetryFinalizationRepository.ingestTarget root None "bogus-adapter" "{}" with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "unknown telemetry adapter 'bogus-adapter'" message)

                withTemporaryRoot (fun root ->
                    writeMetricRegistry root

                    match FileTelemetryFinalizationRepository.ingestTarget root None "openai-codex" "{}" with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "telemetry ingest --adapter 'openai-codex' is not yet supported by this CLI" message) }

          { Name = "ingestTarget omits raw retention when disabled by config, still records the omission metric and unknown-field capabilities"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeRosConfig root """{"telemetry":{"allowRawTelemetry":false}}"""
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let input = """{"raw":{"field":"value"},"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Error message -> failwith message
                    | Ok record ->
                        match record["rawTelemetry"] with
                        | :? JsonArray as array -> Assert.equal 0 array.Count
                        | _ -> failwith "expected a rawTelemetry array"

                        Assert.equal (Some 1.0) (metricValue record "telemetry.raw_snapshots_omitted")

                        match capabilityFor record None (Some "$.field") with
                        | None -> failwith "expected an unknown-field capability for $.field"
                        | Some capability ->
                            Assert.equal (Some "unknown") (stringField capability "status")
                            Assert.equal true ((stringField capability "reason" |> Option.defaultValue "").Contains "raw payload was omitted")) }

          { Name = "ingestTarget omits raw retention when a single snapshot exceeds the configured byte budget"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeRosConfig root """{"telemetry":{"maxRawPayloadBytes":1024}}"""
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let bigValue = String.replicate 2000 "x"
                    let input = $$"""{"raw":{"field":"{{bigValue}}"},"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Error message -> failwith message
                    | Ok record ->
                        match record["rawTelemetry"] with
                        | :? JsonArray as array -> Assert.equal 0 array.Count
                        | _ -> failwith "expected a rawTelemetry array"

                        Assert.equal (Some 1.0) (metricValue record "telemetry.raw_snapshots_omitted")) }

          { Name = "ingestTarget omits raw retention once the configured snapshot count per execution is reached"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeRosConfig root """{"telemetry":{"maxRawSnapshotsPerExecution":1}}"""
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    let firstInput = """{"raw":{"a":"1"},"collectedAt":"2026-01-01T00:00:00.000Z"}"""
                    let secondInput = """{"raw":{"b":"2"},"collectedAt":"2026-01-02T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" firstInput with
                    | Error message -> failwith message
                    | Ok _ ->
                        match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" secondInput with
                        | Error message -> failwith message
                        | Ok record ->
                            match record["rawTelemetry"] with
                            | :? JsonArray as array -> Assert.equal 1 array.Count
                            | _ -> failwith "expected a rawTelemetry array") }

          { Name = "ingestTarget with an EXE-prefixed target resolves by exact execution id, and with no target resolves the single active work item"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeExecution root "EXE-2" "WI-B" "active" "2026-01-01T00:00:01.000Z"
                    let input = """{"metrics":[{"id":"tokens.input","value":1}],"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "generic" input with
                    | Error message -> failwith message
                    | Ok _ ->
                        let untouched = readExecution root "EXE-2"
                        Assert.equal 0 (metricsOf untouched).Length)

                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeContext root [ "WI-A", "active" ]
                    let input = """{"metrics":[{"id":"tokens.input","value":1}],"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    match FileTelemetryFinalizationRepository.ingestTarget root None "generic" input with
                    | Error message -> failwith message
                    | Ok record -> Assert.equal (Some "EXE-1") (stringField record "executionId")) }

          { Name = "parseIngestInput accepts a single JSON value or JSON Lines, and rejects empty or malformed input"
            Run = fun () ->
                match FileTelemetryFinalizationRepository.parseIngestInput """{"a":1}""" with
                | Error message -> failwith message
                | Ok node -> Assert.equal "{\"a\":1}" (node.ToJsonString())

                match FileTelemetryFinalizationRepository.parseIngestInput "{\"a\":1}\n{\"b\":2}\n" with
                | Error message -> failwith message
                | Ok node ->
                    match node with
                    | :? JsonArray as array -> Assert.equal 2 array.Count
                    | _ -> failwith "expected a JSON array"

                match FileTelemetryFinalizationRepository.parseIngestInput "   " with
                | Ok node -> failwith $"expected a rejection but got {node}"
                | Error message -> Assert.equal "telemetry input is empty" message

                match FileTelemetryFinalizationRepository.parseIngestInput "{not json" with
                | Ok node -> failwith $"expected a rejection but got {node}"
                | Error message -> Assert.equal true (message.StartsWith "telemetry input must be JSON or JSON Lines") } ]
