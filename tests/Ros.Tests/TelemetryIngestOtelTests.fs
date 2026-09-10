namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

/// Typed tests for `telemetry ingest --adapter anthropic-claude-otel`/
/// `google-gemini-otel`/`github-copilot-otel`/`otel-json`
/// (`FileTelemetryFinalizationRepository.ingestTarget`'s `adaptOtel`
/// dispatch), MIG-08's fourteenth increment and the last real adapter name
/// in `Ros.Domain.Telemetry.TelemetryAdapters.all` -- every adapter name
/// production itself recognizes now has real F# effect parity.
[<RequireQualifiedAccess>]
module TelemetryIngestOtelTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-ingest-otel-{Guid.NewGuid():N}")
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
                {"id":"time.model_ms","unit":"ms","aggregation":"sum","collection":"runtime"},
                {"id":"model.requests","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"model.request_failures","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.calls","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.failures","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"agent.turns","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"context.compactions","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"runtime.memory_peak_bytes","unit":"bytes","aggregation":"none","collection":"runtime"},
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
        [ { Name = "ingestTarget with the anthropic-claude-otel adapter maps a direct field (duration_ms) into a scoped, dimensioned metric, with a capability derived from it"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"name":"gen_ai.something","timestamp":"2026-01-01T00:00:05.000Z","duration_ms":450}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-otel" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.equal (Some 450.0) (metricsWithId record "time.model_ms" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))

                          match capabilityFor record "time.model_ms" with
                          | None -> failwith "expected a capability derived from the fired metric"
                          | Some capability -> Assert.equal (Some "supported-observed") (stringField capability "status")) }

          { Name = "ingestTarget with the anthropic-claude-otel adapter looks up fields across nested candidates (resource.attributes), updating identity per record"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"name":"gen_ai.something","timestamp":"2026-01-01T00:00:05.000Z",
                                "resource":{"attributes":{"gen_ai.provider.name":"anthropic"}},
                                "gen_ai.response.model":"claude-opus-5","session.id":"sess-1"}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-otel" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "anthropic") (stringField identity "provider")
                              Assert.equal (Some "claude-code") (stringField identity "runtime")
                              Assert.equal (Some "claude-opus-5") (stringField identity "model")
                              Assert.equal (Some "sess-1") (stringField identity "sessionId")
                          | _ -> failwith "expected an identity object") }

          { Name = "ingestTarget with the anthropic-claude-otel adapter maps claude_code.api_request, recording model.request_failures only when success is false"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"name":"claude_code.api_request","timestamp":"2026-01-01T00:00:05.000Z","success":false}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-otel" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.equal 1 (metricsWithId record "model.requests").Length
                          Assert.equal 1 (metricsWithId record "model.request_failures").Length) }

          { Name = "ingestTarget with the google-gemini-otel adapter maps claude_code.tool_result / gemini_cli.tool.call.count into tool.calls, and a string \"false\" success into tool.failures"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"name":"claude_code.tool_result","timestamp":"2026-01-01T00:00:05.000Z","tool_name":"Bash","success":"false"}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "google-gemini-otel" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.equal 1 (metricsWithId record "tool.calls").Length
                          Assert.equal 1 (metricsWithId record "tool.failures").Length

                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "google") (stringField identity "provider")
                              Assert.equal (Some "gemini-cli") (stringField identity "runtime")
                          | _ -> failwith "expected an identity object") }

          { Name = "ingestTarget with the github-copilot-otel adapter maps gemini_cli.agent.turns / gemini_cli.chat_compression / gemini_cli.memory.usage(rss)"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """[{"name":"gemini_cli.agent.turns","timestamp":"2026-01-01T00:00:05.000Z","value":3},
                              {"name":"gemini_cli.chat_compression","timestamp":"2026-01-01T00:00:06.000Z"},
                              {"name":"gemini_cli.memory.usage","timestamp":"2026-01-01T00:00:07.000Z","memory_type":"rss","value":512000}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "github-copilot-otel" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.equal (Some 3.0) (metricsWithId record "agent.turns" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))
                          Assert.equal 1 (metricsWithId record "context.compactions").Length
                          Assert.equal (Some 512000.0) (metricsWithId record "runtime.memory_peak_bytes" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))

                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "github") (stringField identity "provider")
                              Assert.equal (Some "copilot") (stringField identity "runtime")
                          | _ -> failwith "expected an identity object") }

          { Name = "ingestTarget with the otel-json adapter seeds identity as unknown/unknown when nothing overrides it, and wraps a records-carrying object as its own record list"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """{"records":[{"name":"api_request","timestamp":"2026-01-01T00:00:05.000Z","provider":"openai","success":true}]}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "otel-json" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.equal 1 (metricsWithId record "model.requests").Length
                          Assert.empty (metricsWithId record "model.request_failures")

                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "openai") (stringField identity "provider")
                              Assert.equal (Some "unknown") (stringField identity "runtime")
                          | _ -> failwith "expected an identity object") }

          { Name = "ingestTarget with the anthropic-claude-otel adapter emits no events of its own at all"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input = """[{"name":"gen_ai.something","timestamp":"2026-01-01T00:00:05.000Z","duration_ms":1}]"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-otel" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          let nonBookkeepingEvents =
                              match record["events"] with
                              | :? JsonArray as events ->
                                  events
                                  |> Seq.filter (function
                                      | :? JsonObject as e -> stringField e "type" <> Some "telemetry.snapshot.ingested"
                                      | _ -> false)
                                  |> Seq.length
                              | _ -> 0

                          Assert.equal 0 nonBookkeepingEvents) } ]
