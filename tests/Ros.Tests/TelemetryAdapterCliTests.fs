namespace Ros.Tests

open System
open System.Text.Json.Nodes

/// End-to-end `praxis telemetry ingest --adapter ...` tests for the runtime
/// adapters (openai-codex, the three hook adapters, the Claude statusline and
/// the OpenTelemetry family), ported from the former
/// tests/telemetry-ingest-{openai-codex,hook,claude-statusline,otel}-fsharp-differential.test.mjs.
/// The goldens were captured from the retired Node adapters and frozen; each
/// is a projection of the record (sorted, as the original comparison sorted)
/// so it states exactly what the adapter must produce.
[<RequireQualifiedAccess>]
module TelemetryAdapterCliTests =
    let private codexTest1Identity =
        """{"provider":"openai","runtime":"codex","model":"gpt-5-codex-2026"}"""

    let private codexTest1Capabilities =
        """[
          {"metricId":"context.window_size","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tokens.cache_write","status":"supported-unavailable","reason":"adapter recognizes the field but it was unavailable in this snapshot"},
          {"metricId":"tokens.cached_input","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tokens.input","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tokens.output","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tokens.reasoning","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tokens.total","status":"supported-observed","reason":"normalized measurement recorded"} ]"""

    let private codexTest1Metrics =
        """[
          {"id":"context.window_size","value":128000,"scope":"session","dimensions":"{}"},
          {"id":"tokens.cached_input","value":20,"scope":"turn","dimensions":"{\"turnIndex\":0}"},
          {"id":"tokens.input","value":100,"scope":"turn","dimensions":"{\"turnIndex\":0}"},
          {"id":"tokens.input","value":200,"scope":"turn","dimensions":"{\"turnIndex\":1}"},
          {"id":"tokens.output","value":50,"scope":"turn","dimensions":"{\"turnIndex\":0}"},
          {"id":"tokens.output","value":75,"scope":"turn","dimensions":"{\"turnIndex\":1}"},
          {"id":"tokens.reasoning","value":30,"scope":"turn","dimensions":"{\"turnIndex\":1}"},
          {"id":"tokens.total","value":150,"scope":"turn","dimensions":"{\"turnIndex\":0}"} ]"""

    let private codexTest1Events =
        """["agent.turn.completed","agent.turn.completed","telemetry.snapshot.ingested"]"""

    let private hookPosttooluseIdentity =
        """{"provider":"anthropic","runtime":"claude-code","model":"claude-opus-5","sessionId":"sess-123","agentId":null}"""

    let private hookPosttooluseCapabilities =
        """[
          {"status":"unknown","reason":"provider field preserved but not normalized by this adapter version"},
          {"metricId":"telemetry.redactions","status":"derived","reason":"normalized measurement recorded"},
          {"metricId":"telemetry.unknown_fields","status":"derived","reason":"normalized measurement recorded"},
          {"metricId":"tool.calls","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tool.failures","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tool.shell_commands","status":"supported-observed","reason":"normalized measurement recorded"} ]"""

    let private hookPosttooluseMetrics =
        """[
          {"id":"telemetry.redactions","value":1,"scope":"execution","dimensions":"{}"},
          {"id":"telemetry.unknown_fields","value":1,"scope":"execution","dimensions":"{}"},
          {"id":"tool.calls","value":1,"scope":"tool","dimensions":"{\"toolType\":\"Bash\"}"},
          {"id":"tool.failures","value":1,"scope":"tool","dimensions":"{\"toolType\":\"Bash\"}"},
          {"id":"tool.shell_commands","value":1,"scope":"tool","dimensions":"{\"toolType\":\"Bash\"}"} ]"""

    let private hookPosttooluseEvents =
        """["runtime.posttooluse","telemetry.snapshot.ingested"]"""

    let private hookSubagentstartIdentity =
        """{"provider":"google","runtime":"gemini-cli","model":null,"sessionId":null,"agentId":"explore-agent"}"""

    let private hookSubagentstartCapabilities =
        """[
          {"metricId":"agent.subagents_spawned","status":"supported-observed","reason":"normalized measurement recorded"} ]"""

    let private hookSubagentstartMetrics =
        """[
          {"id":"agent.subagents_spawned","value":1,"scope":"execution","dimensions":"{}"} ]"""

    let private hookSubagentstartEvents =
        """["runtime.subagentstart","telemetry.snapshot.ingested"]"""

    let private hookPermissiondeniedIdentity =
        """{"provider":"github","runtime":"copilot","model":"gpt-5-copilot","sessionId":null,"agentId":null}"""

    let private hookPermissiondeniedCapabilities =
        """[
          {"metricId":"agent.approvals_denied","status":"supported-observed","reason":"normalized measurement recorded"} ]"""

    let private hookPermissiondeniedMetrics =
        """[
          {"id":"agent.approvals_denied","value":1,"scope":"execution","dimensions":"{}"} ]"""

    let private hookPermissiondeniedEvents =
        """["runtime.permissiondenied","telemetry.snapshot.ingested"]"""

    let private statuslineTest1Identity =
        """{"provider":"anthropic","runtime":"claude-code","runtimeVersion":"1.2.3","model":"claude-opus-5","sessionId":"sess-abc","agentId":"explore-agent"}"""

    let private statuslineTest1Capabilities =
        """[
          {"metricId":"context.current_cache_read_tokens","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"context.current_cache_write_tokens","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"context.current_input_tokens","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"context.current_output_tokens","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"context.utilization","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"context.window_size","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"cost.session_cumulative","status":"estimated","reason":"normalized measurement recorded"} ]"""

    let private statuslineTest1Metrics =
        """[
          {"id":"context.current_cache_read_tokens","value":300,"scope":"session","quality":"observed","confidence":null,"currency":null},
          {"id":"context.current_cache_write_tokens","value":50,"scope":"session","quality":"observed","confidence":null,"currency":null},
          {"id":"context.current_input_tokens","value":1000,"scope":"session","quality":"observed","confidence":null,"currency":null},
          {"id":"context.current_output_tokens","value":200,"scope":"session","quality":"observed","confidence":null,"currency":null},
          {"id":"context.utilization","value":0.425,"scope":"session","quality":"observed","confidence":null,"currency":null},
          {"id":"context.window_size","value":200000,"scope":"session","quality":"observed","confidence":null,"currency":null},
          {"id":"cost.session_cumulative","value":1.2345,"scope":"session","quality":"estimated","confidence":"medium","currency":"USD"} ]"""

    let private statuslineTest1Events =
        """["telemetry.snapshot.ingested"]"""

    let private statuslineTest2Capabilities =
        """[
          {"metricId":"context.current_cache_read_tokens","status":"supported-unavailable","reason":"adapter recognizes the field but it was unavailable in this snapshot"},
          {"metricId":"context.current_cache_write_tokens","status":"supported-unavailable","reason":"adapter recognizes the field but it was unavailable in this snapshot"},
          {"metricId":"context.current_input_tokens","status":"supported-unavailable","reason":"adapter recognizes the field but it was unavailable in this snapshot"},
          {"metricId":"context.current_output_tokens","status":"supported-unavailable","reason":"adapter recognizes the field but it was unavailable in this snapshot"},
          {"metricId":"context.utilization","status":"supported-unavailable","reason":"adapter recognizes the field but it was unavailable in this snapshot"},
          {"metricId":"context.window_size","status":"supported-unavailable","reason":"adapter recognizes the field but it was unavailable in this snapshot"},
          {"metricId":"cost.session_cumulative","status":"supported-unavailable","reason":"adapter recognizes the field but it was unavailable in this snapshot"} ]"""

    let private otelTest1Identity =
        """{"provider":"anthropic","runtime":"claude-code","model":"claude-opus-5","sessionId":"sess-1"}"""

    let private otelTest1Capabilities =
        """[
          {"metricId":"agent.turns","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"context.compactions","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"model.request_failures","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"model.requests","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"runtime.memory_peak_bytes","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"time.model_ms","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tokens.input","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tokens.output","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tool.calls","status":"supported-observed","reason":"normalized measurement recorded"},
          {"metricId":"tool.failures","status":"supported-observed","reason":"normalized measurement recorded"} ]"""

    let private otelTest1Metrics =
        """[
          {"id":"agent.turns","value":3,"scope":"execution","dimensions":"{}"},
          {"id":"context.compactions","value":1,"scope":"execution","dimensions":"{}"},
          {"id":"model.request_failures","value":1,"scope":"operation","dimensions":"{}"},
          {"id":"model.requests","value":1,"scope":"operation","dimensions":"{}"},
          {"id":"runtime.memory_peak_bytes","value":512000,"scope":"execution","dimensions":"{}"},
          {"id":"time.model_ms","value":450,"scope":"operation","dimensions":"{\"event\":\"claude_code.api_request\"}"},
          {"id":"tokens.input","value":50,"scope":"operation","dimensions":"{\"event\":\"gen_ai.something\"}"},
          {"id":"tokens.input","value":123,"scope":"operation","dimensions":"{\"tokenType\":\"input\"}"},
          {"id":"tokens.output","value":20,"scope":"operation","dimensions":"{\"event\":\"gen_ai.something\"}"},
          {"id":"tool.calls","value":1,"scope":"tool","dimensions":"{\"toolType\":\"Bash\"}"},
          {"id":"tool.failures","value":1,"scope":"tool","dimensions":"{\"toolType\":\"Bash\"}"} ]"""

    let private otelTest1Events =
        """["telemetry.snapshot.ingested"]"""

    let private otelTest2Identity =
        """{"provider":"openai","runtime":"unknown"}"""

    let private otelTest2Metrics =
        """[
          {"id":"model.requests","value":1,"scope":"operation","dimensions":"{}"} ]"""


    // ------------------------------------------------------------ helpers

    let private emptyExecution (executionId: string) =
        CliPort.fill
            [ "executionId", executionId ]
            """{
  "schemaVersion": "1.0.0", "executionId": "{{executionId}}", "workItemId": "WI-A", "status": "active", "startedAt": "2026-01-01T00:00:00.000Z",
  "identity": { "provider": "unknown", "runtime": "unknown" },
  "provenance": { "collector": "ros", "collectorVersion": "1.0.0", "discoveredAt": "2026-01-01T00:00:00.000Z", "sources": [] },
  "classification": { "types": ["development"], "rationale": null, "evidence": [], "rd": null },
  "capabilities": [], "metrics": [], "rawTelemetry": [], "events": [],
  "repository": { "start": { "available": false } }, "scope": {}, "qualitySignals": [], "links": {}
}"""

    /// Ingests `input` through `adapter` into a fresh EXE-1 and hands the
    /// printed record and the stored record to `check`.
    let private ingestWith (project: string) (adapter: string) (input: string) (check: JsonNode -> JsonNode -> unit) =
        CliPort.withRepository project (fun root ->
            CliPort.writeExecution root "EXE-1" (emptyExecution "EXE-1")

            let result =
                CliPort.withInputFile input (fun file -> CliHarness.ros root [ "telemetry"; "ingest"; "EXE-1"; "--input"; file; "--adapter"; adapter ])

            CliPort.exitCode 0 result
            check (CliPort.parse result.Out) (CliPort.readExecution root "EXE-1"))

    /// Marks a field to leave out of a projection (JavaScript's `undefined`).
    let private missing: JsonNode = JsonValue.Create "\u0000missing"

    let private project (fields: (string * JsonNode) list) =
        fields
        |> List.filter (fun (_, value) -> not (obj.ReferenceEquals(value, missing)))
        |> List.map (fun (name, value) -> Collections.Generic.KeyValuePair<string, JsonNode>(name, (if isNull value then null else value.DeepClone())))
        |> JsonObject
        :> JsonNode

    /// `{metricId, status, reason}` per capability (metricId omitted when
    /// absent), stably sorted by metricId with absent ids first.
    let private capabilities (record: JsonNode) =
        CliPort.items (record["capabilities"])
        |> List.sortWith (fun a b -> String.CompareOrdinal(defaultArg (CliPort.stringOf a "metricId") "", defaultArg (CliPort.stringOf b "metricId") ""))
        |> List.map (fun capability ->
            project
                [ "metricId", (match (capability["metricId"]) with null -> missing | value -> value)
                  "status", (capability["status"])
                  "reason", (capability["reason"]) ])
        |> List.toArray
        |> JsonArray

    let private sortedMetrics (record: JsonNode) =
        CliPort.items (record["metrics"])
        |> List.sortBy (fun metric -> CliPort.text (metric["id"]), CliPort.number (metric["value"]))

    /// `{id, value, scope, dimensions}` with dimensions as their compact JSON text.
    let private metricsWithDimensions (record: JsonNode) =
        sortedMetrics record
        |> List.map (fun metric ->
            project
                [ "id", (metric["id"])
                  "value", (metric["value"])
                  "scope", (metric["scope"])
                  "dimensions", JsonValue.Create(CliPort.compact (metric["dimensions"])) :> JsonNode ])
        |> List.toArray
        |> JsonArray

    /// `{id, value, scope, quality, confidence, currency}`.
    let private metricsWithQuality (record: JsonNode) =
        sortedMetrics record
        |> List.map (fun metric ->
            project
                [ "id", (metric["id"])
                  "value", (metric["value"])
                  "scope", (metric["scope"])
                  "quality", (metric["quality"])
                  "confidence", (metric["confidence"])
                  "currency", (metric["currency"]) ])
        |> List.toArray
        |> JsonArray

    let private eventTypes (record: JsonNode) =
        CliPort.items (record["events"])
        |> List.map (fun event -> CliPort.text (event["type"]))
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.map (fun eventType -> JsonValue.Create eventType :> JsonNode)
        |> List.toArray
        |> JsonArray

    let private identityOf (record: JsonNode) (fields: string list) =
        project (fields |> List.map (fun field -> field, (record["identity"][field])))

    // -------------------------------------------------------------- codex

    let private codexProject = "Telemetry Ingest Codex Differential"

    let private codexTests =
        [ { Name = "telemetry adapter cli: openai-codex maps identity, capabilities, metrics and events"
            Run = fun () ->
                let input =
                    """[{"type":"session.started","timestamp":"2026-01-01T00:00:00.000Z","model":"gpt-5-codex"},
                        {"type":"turn.completed","timestamp":"2026-01-01T00:00:05.000Z","server_model":"gpt-5-codex-2026",
                         "usage":{"input_tokens":100,"output_tokens":50,"cached_input_tokens":20,"total_tokens":150,"model_context_window":128000}},
                        {"type":"turn.completed","timestamp":"2026-01-01T00:00:10.000Z","usage":{"input_tokens":200,"output_tokens":75,"reasoning_output_tokens":30}}]"""

                ingestWith codexProject "openai-codex" input (fun printed stored ->
                    CliPort.deepEqual codexTest1Identity (identityOf printed [ "provider"; "runtime"; "model" ])
                    CliPort.deepEqual codexTest1Capabilities (capabilities printed)
                    CliPort.deepEqual codexTest1Metrics (metricsWithDimensions printed)
                    CliPort.deepEqual codexTest1Events (eventTypes printed)
                    CliPort.deepEqual codexTest1Capabilities (capabilities stored)
                    CliPort.deepEqual codexTest1Metrics (metricsWithDimensions stored)) }
          { Name = "telemetry adapter cli: openai-codex takes identity.model from the last record carrying one"
            Run = fun () ->
                let input =
                    """[{"type":"turn.completed","timestamp":"2026-01-01T00:00:05.000Z","server_model":"gpt-early","usage":{"input_tokens":1}},
                        {"type":"session.info","timestamp":"2026-01-01T00:00:06.000Z","model":"gpt-late"}]"""

                ingestWith codexProject "openai-codex" input (fun printed _ ->
                    Assert.equal "gpt-late" (CliPort.text (printed["identity"]["model"]))) }
          { Name = "telemetry adapter cli: openai-codex records an unknown usage field as a discovered raw field"
            Run = fun () ->
                let input = """[{"type":"turn.completed","timestamp":"2026-01-01T00:00:05.000Z","usage":{"input_tokens":5,"unknown_extra_field":999}}]"""

                ingestWith codexProject "openai-codex" input (fun _ stored ->
                    let raw = CliPort.items (stored["rawTelemetry"])
                    let discovered = (raw.Head["discoveredFields"])
                    CliPort.deepEqual """["$[].usage.unknown_extra_field"]""" discovered) } ]

    // --------------------------------------------------------------- hook

    let private hookProject = "Telemetry Ingest Hook Differential"
    let private hookIdentityFields = [ "provider"; "runtime"; "model"; "sessionId"; "agentId" ]

    /// A hook identity projection where an absent field reads as null.
    let private hookIdentity (record: JsonNode) =
        project (hookIdentityFields |> List.map (fun field -> field, (match (record["identity"][field]) with null -> null | value -> value)))

    let private hookCase (adapter: string) (label: string) (input: string) (identity: string) (capabilityGolden: string) (metricGolden: string) (eventGolden: string) =
        { Name = $"telemetry adapter cli: {adapter} maps identity, capabilities, metrics and events ({label})"
          Run = fun () ->
              ingestWith hookProject adapter input (fun printed stored ->
                  CliPort.deepEqual identity (hookIdentity printed)
                  CliPort.deepEqual capabilityGolden (capabilities printed)
                  CliPort.deepEqual metricGolden (metricsWithDimensions printed)
                  CliPort.deepEqual eventGolden (eventTypes printed)
                  CliPort.deepEqual capabilityGolden (capabilities stored)
                  CliPort.deepEqual metricGolden (metricsWithDimensions stored)) }

    let private hookTests =
        [ hookCase
              "anthropic-claude-hook"
              "posttooluse"
              """{"hook_event_name":"PostToolUse","tool_name":"Bash","timestamp":"2026-01-01T00:00:05.000Z","session_id":"sess-123","model":{"id":"claude-opus-5"},"tool_response":{"error":"boom"}}"""
              hookPosttooluseIdentity
              hookPosttooluseCapabilities
              hookPosttooluseMetrics
              hookPosttooluseEvents
          hookCase
              "google-gemini-hook"
              "subagentstart"
              """{"hook_event_name":"SubagentStart","timestamp":"2026-01-01T00:00:05.000Z","agent_type":"explore-agent"}"""
              hookSubagentstartIdentity
              hookSubagentstartCapabilities
              hookSubagentstartMetrics
              hookSubagentstartEvents
          hookCase
              "github-copilot-hook"
              "permissiondenied"
              """{"hook_event_name":"PermissionDenied","timestamp":"2026-01-01T00:00:05.000Z","model":"gpt-5-copilot"}"""
              hookPermissiondeniedIdentity
              hookPermissiondeniedCapabilities
              hookPermissiondeniedMetrics
              hookPermissiondeniedEvents
          { Name = "telemetry adapter cli: anthropic-claude-hook records no tool.failures when nothing indicates an error"
            Run = fun () ->
                let input = """{"hook_event_name":"PostToolUse","tool_name":"Bash","timestamp":"2026-01-01T00:00:05.000Z","tool_response":{"result":"ok"}}"""

                ingestWith hookProject "anthropic-claude-hook" input (fun printed _ ->
                    Assert.equal None (CliPort.metricValue printed "tool.failures")) } ]

    // --------------------------------------------------------- statusline

    let private statuslineProject = "Telemetry Ingest Claude Statusline Differential"

    let private statuslineTests =
        [ { Name = "telemetry adapter cli: anthropic-claude-statusline maps identity, capabilities, metrics and events"
            Run = fun () ->
                let input =
                    """{"version":"1.2.3","session_id":"sess-abc","model":{"id":"claude-opus-5"},"agent":{"name":"explore-agent"},
                        "context_window":{"context_window_size":200000,"used_percentage":42.5,
                                          "current_usage":{"input_tokens":1000,"output_tokens":200,"cache_creation_input_tokens":50,"cache_read_input_tokens":300}},
                        "cost":{"total_cost_usd":1.2345}}"""

                ingestWith statuslineProject "anthropic-claude-statusline" input (fun printed stored ->
                    CliPort.deepEqual statuslineTest1Identity (printed["identity"])
                    CliPort.deepEqual statuslineTest1Capabilities (capabilities printed)
                    CliPort.deepEqual statuslineTest1Metrics (metricsWithQuality printed)
                    CliPort.deepEqual statuslineTest1Events (eventTypes printed)
                    CliPort.deepEqual statuslineTest1Capabilities (capabilities stored)
                    CliPort.deepEqual statuslineTest1Metrics (metricsWithQuality stored)) }
          { Name = "telemetry adapter cli: anthropic-claude-statusline declares every capability supported-unavailable when nothing is present"
            Run = fun () ->
                ingestWith statuslineProject "anthropic-claude-statusline" """{"version":"0.9.0"}""" (fun printed _ ->
                    CliPort.deepEqual statuslineTest2Capabilities (capabilities printed)
                    Assert.equal 0 (CliPort.items (printed["metrics"])).Length) }
          { Name = "telemetry adapter cli: anthropic-claude-statusline marks a present session cost as estimated"
            Run = fun () ->
                ingestWith statuslineProject "anthropic-claude-statusline" """{"cost":{"total_cost_usd":0.42}}""" (fun printed _ ->
                    let cost =
                        CliPort.items (printed["capabilities"])
                        |> List.find (fun capability -> CliPort.stringOf capability "metricId" = Some "cost.session_cumulative")

                    Assert.equal "estimated" (CliPort.text (cost["status"]))) } ]

    // --------------------------------------------------------------- otel

    let private otelProject = "Telemetry Ingest Otel Differential"

    let private otelTests =
        [ { Name = "telemetry adapter cli: anthropic-claude-otel maps nested lookups, direct fields, token types, api/tool events and nanosecond timestamps"
            Run = fun () ->
                let input =
                    """[{"name":"gen_ai.client.token.usage","timestamp":"2026-01-01T00:00:05.000Z","resource":{"attributes":{"gen_ai.provider.name":"anthropic"}},
                         "gen_ai.response.model":"claude-opus-5","session.id":"sess-1","type":"input","value":123},
                        {"name":"claude_code.api_request","timestamp":"2026-01-01T00:00:06.000Z","success":false,"duration_ms":450},
                        {"name":"claude_code.tool_result","timestamp":"2026-01-01T00:00:07.000Z","tool_name":"Bash","success":"false"},
                        {"name":"gemini_cli.agent.turns","timestamp":"2026-01-01T00:00:08.000Z","value":3},
                        {"name":"gemini_cli.chat_compression","timestamp":"2026-01-01T00:00:09.000Z"},
                        {"name":"gemini_cli.memory.usage","timestamp":"2026-01-01T00:00:10.000Z","memory_type":"rss","value":512000},
                        {"name":"gen_ai.something","timeUnixNano":1767225611000000000,"input_tokens":50,"output_tokens":20}]"""

                ingestWith otelProject "anthropic-claude-otel" input (fun printed stored ->
                    CliPort.deepEqual otelTest1Identity (printed["identity"])
                    CliPort.deepEqual otelTest1Capabilities (capabilities printed)
                    CliPort.deepEqual otelTest1Metrics (metricsWithDimensions printed)
                    CliPort.deepEqual otelTest1Events (eventTypes printed)
                    CliPort.deepEqual otelTest1Capabilities (capabilities stored)
                    CliPort.deepEqual otelTest1Metrics (metricsWithDimensions stored)) }
          { Name = "telemetry adapter cli: otel-json seeds an unknown/unknown identity and unwraps a records-carrying object"
            Run = fun () ->
                let input = """{"records":[{"name":"api_request","timestamp":"2026-01-01T00:00:05.000Z","provider":"openai","success":true}]}"""

                ingestWith otelProject "otel-json" input (fun printed _ ->
                    CliPort.deepEqual otelTest2Identity (printed["identity"])
                    CliPort.deepEqual otelTest2Metrics (metricsWithDimensions printed)) }
          for adapter, provider, runtime in [ "google-gemini-otel", "google", "gemini-cli"; "github-copilot-otel", "github", "copilot" ] do
              { Name = $"telemetry adapter cli: {adapter} seeds its own provider/runtime identity when nothing is present"
                Run = fun () ->
                    ingestWith otelProject adapter "{}" (fun printed _ ->
                        CliPort.deepEqual $"""{{"provider":"{provider}","runtime":"{runtime}"}}""" (printed["identity"])
                        Assert.equal 0 (CliPort.items (printed["metrics"])).Length) } ]

    let tests = codexTests @ hookTests @ statuslineTests @ otelTests
