namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

/// Typed tests for `telemetry ingest --adapter anthropic-claude-statusline`
/// (`FileTelemetryFinalizationRepository.ingestTarget`'s `adaptClaudeStatusline`
/// dispatch), the thirteenth MIG-08 increment: a single-snapshot adapter
/// (not an event stream) whose one real quirk is that a present
/// `cost.session_cumulative` value gets an `"estimated"` capability status
/// instead of `"supported-observed"`.
[<RequireQualifiedAccess>]
module TelemetryIngestClaudeStatuslineTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-ingest-statusline-{Guid.NewGuid():N}")
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
                {"id":"context.window_size","unit":"tokens","aggregation":"none","collection":"runtime"},
                {"id":"context.utilization","unit":"ratio","aggregation":"none","collection":"runtime"},
                {"id":"cost.session_cumulative","unit":"usd","aggregation":"none","collection":"runtime"},
                {"id":"context.current_input_tokens","unit":"tokens","aggregation":"none","collection":"runtime"},
                {"id":"context.current_output_tokens","unit":"tokens","aggregation":"none","collection":"runtime"},
                {"id":"context.current_cache_write_tokens","unit":"tokens","aggregation":"none","collection":"runtime"},
                {"id":"context.current_cache_read_tokens","unit":"tokens","aggregation":"none","collection":"runtime"},
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
        [ { Name = "ingestTarget with the anthropic-claude-statusline adapter maps context/cost fields, computing utilization as used_percentage/100"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """{"version":"1.2.3","session_id":"sess-1","model":{"id":"claude-opus-5"},"agent":{"name":"explore-agent"},
                              "context_window":{"context_window_size":200000,"used_percentage":42.5,
                                "current_usage":{"input_tokens":1000,"output_tokens":200,"cache_creation_input_tokens":50,"cache_read_input_tokens":300}},
                              "cost":{"total_cost_usd":1.2345}}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-statusline" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          Assert.equal (Some 200000.0) (metricsWithId record "context.window_size" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))
                          Assert.equal (Some 0.425) (metricsWithId record "context.utilization" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))
                          Assert.equal (Some 1000.0) (metricsWithId record "context.current_input_tokens" |> List.tryHead |> Option.bind (fun m -> numberValue m "value"))

                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "anthropic") (stringField identity "provider")
                              Assert.equal (Some "claude-code") (stringField identity "runtime")
                              Assert.equal (Some "1.2.3") (stringField identity "runtimeVersion")
                              Assert.equal (Some "claude-opus-5") (stringField identity "model")
                              Assert.equal (Some "sess-1") (stringField identity "sessionId")
                              Assert.equal (Some "explore-agent") (stringField identity "agentId")
                          | _ -> failwith "expected an identity object") }

          { Name = "ingestTarget with the anthropic-claude-statusline adapter marks a present cost.session_cumulative capability as estimated, not supported-observed"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input = """{"cost":{"total_cost_usd":0.42}}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-statusline" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          match capabilityFor record "cost.session_cumulative" with
                          | None -> failwith "expected a capability for cost.session_cumulative"
                          | Some capability -> Assert.equal (Some "estimated") (stringField capability "status")

                          match capabilityFor record "context.window_size" with
                          | None -> failwith "expected a capability declared even when absent"
                          | Some capability -> Assert.equal (Some "supported-unavailable") (stringField capability "status")) }

          { Name = "ingestTarget with the anthropic-claude-statusline adapter declares every capability even when nothing is present, recording no metrics"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input = """{"version":"0.9.0"}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-statusline" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          for id in
                              [ "context.window_size"
                                "context.utilization"
                                "cost.session_cumulative"
                                "context.current_input_tokens"
                                "context.current_output_tokens"
                                "context.current_cache_write_tokens"
                                "context.current_cache_read_tokens" ] do
                              match capabilityFor record id with
                              | None -> failwith $"expected a capability declaration for {id}"
                              | Some capability -> Assert.equal (Some "supported-unavailable") (stringField capability "status")

                          Assert.empty (metricsWithId record "context.window_size")
                          Assert.empty (metricsWithId record "cost.session_cumulative")) }

          { Name = "ingestTarget with the anthropic-claude-statusline adapter emits no events at all"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input = """{"context_window":{"context_window_size":100}}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-statusline" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          let statuslineEvents =
                              match record["events"] with
                              | :? JsonArray as events ->
                                  events
                                  |> Seq.filter (function
                                      | :? JsonObject as e -> stringField e "type" <> Some "telemetry.snapshot.ingested"
                                      | _ -> false)
                                  |> Seq.length
                              | _ -> 0
                          // Only the shared pipeline's own bookkeeping event
                          // exists; the adapter itself contributes none.
                          Assert.equal 0 statuslineEvents) } ]
