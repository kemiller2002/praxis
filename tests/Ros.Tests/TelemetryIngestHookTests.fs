namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

/// Typed tests for `telemetry ingest --adapter anthropic-claude-hook` /
/// `google-gemini-hook` / `github-copilot-hook`
/// (`FileTelemetryFinalizationRepository.ingestTarget`'s `adaptHook`
/// dispatch), the twelfth MIG-08 increment: three CLI-visible adapter names
/// sharing one adaptation function, parameterized only by identity
/// provider/runtime.
[<RequireQualifiedAccess>]
module TelemetryIngestHookTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-ingest-hook-{Guid.NewGuid():N}")
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
                {"id":"tool.calls","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.failures","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.shell_commands","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.file_reads","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.file_writes","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.searches","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.web_activity","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.repository_operations","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.test_executions","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.build_executions","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.deployments","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.database_operations","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.api_operations","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"tool.external_service_calls","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"agent.subagents_spawned","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"context.compactions","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"agent.approvals_requested","unit":"count","aggregation":"sum","collection":"runtime"},
                {"id":"agent.approvals_denied","unit":"count","aggregation":"sum","collection":"runtime"},
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

    let private eventsOf (record: JsonObject) : JsonObject list =
        match record["events"] with
        | :? JsonArray as events -> events |> Seq.choose (function :? JsonObject as node -> Some node | _ -> None) |> Seq.toList
        | _ -> []

    let tests =
        [ { Name = "ingestTarget with the anthropic-claude-hook adapter maps a PostToolUse hook into tool.calls, its tool-category metric, and identity, all deriving their capabilities 1:1 from what fired"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """{"hook_event_name":"PostToolUse","tool_name":"Bash","timestamp":"2026-01-01T00:00:05.000Z","session_id":"sess-1","model":{"id":"claude-opus-5"}}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-hook" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"

                          Assert.equal 1 (metricsWithId record "tool.calls").Length
                          Assert.equal 1 (metricsWithId record "tool.shell_commands").Length
                          Assert.empty (metricsWithId record "tool.failures")

                          match capabilityFor record "tool.calls" with
                          | None -> failwith "expected a capability for tool.calls, derived from the fired metric"
                          | Some capability -> Assert.equal (Some "supported-observed") (stringField capability "status")

                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "anthropic") (stringField identity "provider")
                              Assert.equal (Some "claude-code") (stringField identity "runtime")
                              Assert.equal (Some "sess-1") (stringField identity "sessionId")
                              Assert.equal (Some "claude-opus-5") (stringField identity "model")
                          | _ -> failwith "expected an identity object") }

          { Name = "ingestTarget with the anthropic-claude-hook adapter records tool.failures only when the tool_response carries an error, a truthy error field, or success is explicitly false"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """{"hook_event_name":"PostToolUse","tool_name":"Bash","timestamp":"2026-01-01T00:00:05.000Z","tool_response":{"error":"boom"}}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-hook" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.equal 1 (metricsWithId record "tool.failures").Length) }

          { Name = "ingestTarget with the anthropic-claude-hook adapter does not record tool.failures when nothing indicates an error"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """{"hook_event_name":"PostToolUse","tool_name":"Bash","timestamp":"2026-01-01T00:00:05.000Z","tool_response":{"result":"ok"}}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-hook" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.empty (metricsWithId record "tool.failures")) }

          { Name = "ingestTarget with the google-gemini-hook adapter maps SubagentStart into agent.subagents_spawned and an execution-scoped identity"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """{"hook_event_name":"SubagentStart","timestamp":"2026-01-01T00:00:05.000Z","agent_type":"explore-agent"}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "google-gemini-hook" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-1"
                          Assert.equal 1 (metricsWithId record "agent.subagents_spawned").Length

                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "google") (stringField identity "provider")
                              Assert.equal (Some "gemini-cli") (stringField identity "runtime")
                              Assert.equal (Some "explore-agent") (stringField identity "agentId")
                          | _ -> failwith "expected an identity object") }

          { Name = "ingestTarget with the github-copilot-hook adapter maps PostCompact, PermissionRequest, and PermissionDenied hook names to their own distinct metrics"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root

                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                      let compactInput = """{"hook_event_name":"PostCompact","timestamp":"2026-01-01T00:00:05.000Z"}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "github-copilot-hook" compactInput with
                      | Error message -> failwith message
                      | Ok _ -> Assert.equal 1 (metricsWithId (readExecution root "EXE-1") "context.compactions").Length

                      writeExecution root "EXE-2" "WI-B" "active" "2026-01-01T00:00:00.000Z"
                      let requestInput = """{"hook_event_name":"PermissionRequest","timestamp":"2026-01-01T00:00:05.000Z"}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-2") "github-copilot-hook" requestInput with
                      | Error message -> failwith message
                      | Ok _ -> Assert.equal 1 (metricsWithId (readExecution root "EXE-2") "agent.approvals_requested").Length

                      writeExecution root "EXE-3" "WI-C" "active" "2026-01-01T00:00:00.000Z"
                      let deniedInput = """{"hook_event_name":"PermissionDenied","timestamp":"2026-01-01T00:00:05.000Z"}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-3") "github-copilot-hook" deniedInput with
                      | Error message -> failwith message
                      | Ok _ ->
                          let record = readExecution root "EXE-3"
                          Assert.equal 1 (metricsWithId record "agent.approvals_denied").Length

                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "github") (stringField identity "provider")
                              Assert.equal (Some "copilot") (stringField identity "runtime")
                          | _ -> failwith "expected an identity object") }

          { Name = "ingestTarget with a hook adapter emits exactly one runtime event per call, typed from the hook name, alongside the shared snapshot-ingested bookkeeping event"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                      let input =
                          """{"hook_event_name":"PostToolUse","tool_name":"Bash","timestamp":"2026-01-01T00:00:05.000Z"}"""

                      match FileTelemetryFinalizationRepository.ingestTarget root (Some "EXE-1") "anthropic-claude-hook" input with
                      | Error message -> failwith message
                      | Ok _ ->
                          let events = eventsOf (readExecution root "EXE-1")
                          let runtimeEvents = events |> List.filter (fun e -> stringField e "type" = Some "runtime.posttooluse")
                          Assert.equal 1 runtimeEvents.Length
                          Assert.equal 2 events.Length) } ]
