namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

/// Typed tests for `telemetry ingest --adapter openai-codex`
/// (`FileTelemetryFinalizationRepository.ingestTarget`'s `adaptOpenAICodex`
/// dispatch), the first real provider-specific field mapping ported beyond
/// the `generic` adapter.
[<RequireQualifiedAccess>]
module TelemetryIngestOpenAICodexTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-ingest-codex-{Guid.NewGuid():N}")
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
                {"id":"tokens.output","unit":"tokens","aggregation":"sum","collection":"runtime"},
                {"id":"tokens.cached_input","unit":"tokens","aggregation":"sum","collection":"runtime"},
                {"id":"tokens.cache_write","unit":"tokens","aggregation":"sum","collection":"runtime"},
                {"id":"tokens.reasoning","unit":"tokens","aggregation":"sum","collection":"runtime"},
                {"id":"tokens.total","unit":"tokens","aggregation":"sum","collection":"runtime"},
                {"id":"context.window_size","unit":"tokens","aggregation":"none","collection":"runtime"},
                {"id":"telemetry.redactions","unit":"count","aggregation":"sum","collection":"ros-derived"},
                {"id":"telemetry.unknown_fields","unit":"count","aggregation":"sum","collection":"ros-derived"},
                {"id":"telemetry.raw_snapshots_omitted","unit":"count","aggregation":"sum","collection":"ros-derived"}
            ]}"""
        )

    let private executionFile root executionId =
        Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

    let private writeExecution root executionId workItemId status (startedAt: string) =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")
        Directory.CreateDirectory directory |> ignore

        let json =
            $"""{{"schemaVersion":"1.0.0","executionId":"{executionId}","workItemId":"{workItemId}","status":"{status}","startedAt":"{startedAt}",
                "identity":{{"provider":"unknown","runtime":"unknown"}},"provenance":{{"collector":"ros","collectorVersion":"1.0.0","discoveredAt":"{startedAt}","sources":[]}},
                "classification":{{"types":["development"],"rationale":null,"evidence":[],"rd":null}},
                "capabilities":[],"metrics":[],"rawTelemetry":[],"events":[],"repository":{{"start":{{"available":false}}}},"scope":{{}},"qualitySignals":[],"links":{{}}}}"""

        File.WriteAllText(executionFile root executionId, json)

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

    let private capabilitiesOf (record: JsonObject) : JsonObject list =
        match record["capabilities"] with
        | :? JsonArray as capabilities -> capabilities |> Seq.choose (function :? JsonObject as node -> Some node | _ -> None) |> Seq.toList
        | _ -> []

    let private metricsWithId (record: JsonObject) (id: string) : JsonObject list =
        metricsOf record |> List.filter (fun m -> stringField m "id" = Some id)

    let private capabilityFor (record: JsonObject) (metricId: string) : JsonObject option =
        capabilitiesOf record |> List.tryFind (fun c -> stringField c "metricId" = Some metricId)

    let private numberValue (node: JsonObject) (name: string) : float option =
        match node[name] with
        | :? JsonValue as v when v.GetValueKind() = Text.Json.JsonValueKind.Number ->
            match v.TryGetValue<float>() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None

    let tests =
        [ { Name = "ingestTarget with the openai-codex adapter declares a capability for all six usage fields, but only a metric for the ones actually present, per completed entry"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"type":"turn.completed","timestamp":"2026-01-01T00:05:00.000Z","usage":{"input_tokens":10,"output_tokens":5}}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "openai-codex" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          for id in [ "tokens.input"; "tokens.output"; "tokens.cached_input"; "tokens.cache_write"; "tokens.reasoning"; "tokens.total" ] do
                              match capabilityFor record id with
                              | None -> failwith $"expected a capability declaration for {id}"
                              | Some _ -> ()

                          Assert.equal (Some 10.0) (metricsWithId record "tokens.input" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))
                          Assert.equal (Some 5.0) (metricsWithId record "tokens.output" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))
                          Assert.empty (metricsWithId record "tokens.cached_input")
                          Assert.empty (metricsWithId record "tokens.reasoning")) }

          { Name = "ingestTarget with the openai-codex adapter records context.window_size as a metric, matching production's own adapter-level omission of an explicit capability for it (the shared metric-recording pipeline still upserts one)"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"type":"turn.completed","timestamp":"2026-01-01T00:05:00.000Z","usage":{"input_tokens":1,"model_context_window":128000}}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "openai-codex" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.equal (Some 128000.0) (metricsWithId record "context.window_size" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))
                          // adaptOpenAICodex itself never declares a capability for
                          // context.window_size (unlike the six usage fields, which
                          // always get one, present or not) -- but a capability
                          // still exists here, because the shared
                          // normalizeAndAppendIngestedMetric pipeline every recorded
                          // metric goes through upserts one regardless of adapter.
                          match capabilityFor record "context.window_size" with
                          | None -> failwith "expected the shared metric pipeline to upsert a capability regardless"
                          | Some capability -> Assert.equal (Some "supported-observed") (stringField capability "status")) }

          { Name = "ingestTarget with the openai-codex adapter assigns a distinct turnIndex per completed entry, in encounter order"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"type":"turn.completed","timestamp":"2026-01-01T00:05:00.000Z","usage":{"input_tokens":1}},
                              {"type":"turn.completed","timestamp":"2026-01-01T00:06:00.000Z","usage":{"input_tokens":2}}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "openai-codex" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          let inputMetrics = metricsWithId record "tokens.input"
                          Assert.equal 2 inputMetrics.Length

                          let turnIndexOf (m: JsonObject) =
                              match m["dimensions"] with
                              | :? JsonObject as d ->
                                  match d["turnIndex"] with
                                  | :? JsonValue as v -> v.GetValue<int>()
                                  | _ -> -1
                              | _ -> -1

                          Assert.equal [ 0; 1 ] (inputMetrics |> List.map turnIndexOf |> List.sort)) }

          { Name = "ingestTarget with the openai-codex adapter resolves identity.model from the last record carrying one, in reverse order, even if it is not itself completed"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"type":"turn.completed","timestamp":"2026-01-01T00:05:00.000Z","server_model":"gpt-early","usage":{"input_tokens":1}},
                              {"type":"session.info","timestamp":"2026-01-01T00:06:00.000Z","model":"gpt-late"}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "openai-codex" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "openai") (stringField identity "provider")
                              Assert.equal (Some "codex") (stringField identity "runtime")
                              Assert.equal (Some "gpt-late") (stringField identity "model")
                          | _ -> failwith "expected an identity object") }

          { Name = "ingestTarget with the openai-codex adapter filters to completed entries only (type turn.completed, or any usage object), ignoring plain records"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"type":"session.started","timestamp":"2026-01-01T00:00:00.000Z"},
                              {"type":"turn.completed","timestamp":"2026-01-01T00:05:00.000Z","usage":{"input_tokens":1}},
                              {"usage":{"output_tokens":2}}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "openai-codex" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          let turnCompletedEvents =
                              match record["events"] with
                              | :? JsonArray as events -> events |> Seq.filter (function :? JsonObject as e -> stringField e "type" = Some "agent.turn.completed" | _ -> false) |> Seq.length
                              | _ -> 0
                          // Exactly one agent.turn.completed event per completed entry
                          // (2, not 3): the ingestion pipeline's own
                          // telemetry.snapshot.ingested bookkeeping event, always
                          // appended alongside, is a separate event this assertion
                          // does not count.
                          Assert.equal 2 turnCompletedEvents
                          Assert.equal 1 (metricsWithId record "tokens.input").Length
                          Assert.equal 1 (metricsWithId record "tokens.output").Length) }

          { Name = "ingestTarget with the openai-codex adapter wraps a single non-array object as one record"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input = """{"type":"turn.completed","timestamp":"2026-01-01T00:05:00.000Z","usage":{"input_tokens":42}}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "openai-codex" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.equal (Some 42.0) (metricsWithId record "tokens.input" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))) } ]
