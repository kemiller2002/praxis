namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes

/// End-to-end `ros-fs telemetry ...` tests (show, adapters, summary,
/// finalize, record, ingest, classify, start, validate), ported from the
/// former tests/telemetry-*-fsharp-differential.test.mjs files. The golden
/// literals were captured from the retired Node implementation and frozen;
/// fixtures are now installed by the F# `init` under the same project names,
/// so repository ids (and every id hashed from them) are unchanged.
[<RequireQualifiedAccess>]
module TelemetryCliTests =
    let private ros = CliHarness.ros
    let private telemetry root arguments = ros root ("telemetry" :: arguments)
    let private ok (result: CliHarness.Run) = CliPort.exitCode 0 result; result

    /// The fuller hand-written execution shape (classify, ingest, start, adapters).
    let private fullExecution (executionId: string) (workItemId: string) (status: string) (startedAt: string) (provider: string) (runtime: string) (capabilities: string) =
        (CliPort.fill [ "executionId", executionId; "workItemId", workItemId; "status", status; "startedAt", startedAt; "provider", provider; "runtime", runtime; "capabilities", capabilities ] """{
  "schemaVersion": "1.0.0", "executionId": "{{executionId}}", "workItemId": "{{workItemId}}", "status": "{{status}}", "startedAt": "{{startedAt}}",
  "identity": { "provider": "{{provider}}", "runtime": "{{runtime}}" },
  "provenance": { "collector": "ros", "collectorVersion": "1.0.0", "discoveredAt": "{{startedAt}}", "sources": [] },
  "classification": { "types": ["development"], "rationale": null, "evidence": [], "rd": null },
  "capabilities": {{capabilities}}, "metrics": [], "rawTelemetry": [], "events": [],
  "repository": { "start": { "available": false } }, "scope": {}, "qualitySignals": [], "links": {}
}""")

    let private writeFull root executionId workItemId status startedAt =
        CliPort.writeExecution root executionId (fullExecution executionId workItemId status startedAt "anthropic" "claude-code" "[]")

    /// The finalize tests' shape: `finalizedAt` only on a finalized record.
    let private writeFinalizeExecution root (executionId: string) (workItemId: string) (status: string) (startedAt: string) =
        let finalizedAt = if status = "finalized" then $"\"finalizedAt\": \"{startedAt}\"," else ""

        CliPort.writeExecution root executionId (CliPort.fill [ "executionId", executionId; "workItemId", workItemId; "status", status; "startedAt", startedAt; "finalizedAt", finalizedAt ] """{
  "schemaVersion": "1.0.0", "executionId": "{{executionId}}", "workItemId": "{{workItemId}}", "status": "{{status}}", "startedAt": "{{startedAt}}", {{finalizedAt}}
  "identity": { "provider": "anthropic", "runtime": "claude-code", "sessionId": null },
  "capabilities": [], "metrics": [], "events": [], "repository": { "start": { "available": false } }, "links": {}
}""")

    /// The record tests' minimal shape.
    let private writeMinimalExecution root (executionId: string) (workItemId: string) (status: string) (startedAt: string) =
        CliPort.writeExecution root executionId (CliPort.fill [ "executionId", executionId; "workItemId", workItemId; "status", status; "startedAt", startedAt ] """{"schemaVersion":"1.0.0","executionId":"{{executionId}}","workItemId":"{{workItemId}}","status":"{{status}}","startedAt":"{{startedAt}}","events":[],"metrics":[],"capabilities":[]}""")

    let private capabilityFor (record: JsonNode) (metricId: string option) (providerField: string option) =
        CliPort.items (record["capabilities"])
        |> List.find (fun capability ->
            CliPort.stringOf capability "metricId" = metricId
            && CliPort.stringOf capability "providerField" = providerField)

    let private eventsOfType (record: JsonNode) (eventType: string) =
        CliPort.items (record["events"]) |> List.filter (fun event -> CliPort.stringOf event "type" = Some eventType)

    let private stdoutJson (result: CliHarness.Run) = CliPort.parse result.Out

    let private executionFiles root =
        Directory.GetFiles(Path.Combine(root, ".ros", "telemetry", "executions"), "*.json")
        |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> Array.map (File.ReadAllText >> CliPort.parse)
        |> Array.toList

    // ---------------------------------------------------------------- show

    let private volatileKeys =
        set
            [ "executionId"; "startedAt"; "discoveredAt"; "lastAssessedAt"; "recordedAt"; "collectedAt"
              "measurementId"; "commit"; "branch"; "dirtyPaths"; "dirty"; "commits"; "occurredAt"
              "eventId"; "updatedAt"; "telemetryExecutionIds"; "telemetryExecutions"; "completedAt"
              "createdAt"; "finalizedAt"; "startCommit"; "endCommit"; "sessionId" ]

    let rec private stripVolatile (node: JsonNode) : JsonNode =
        let strip (child: JsonNode) = if isNull child then null else stripVolatile child

        match node with
        | :? JsonArray as array -> JsonArray(array |> Seq.map strip |> Seq.toArray) :> JsonNode
        | :? JsonObject as record ->
            record
            |> Seq.filter (fun property -> not (volatileKeys.Contains property.Key))
            |> Seq.map (fun property -> Collections.Generic.KeyValuePair<string, JsonNode>(property.Key, strip property.Value))
            |> Seq.toArray
            |> JsonObject
            :> JsonNode
        | value -> value.DeepClone()

    /// The execution a fresh `work start --type task` records for WI-A in a
    /// repository named "Telemetry Show Differential", volatile keys removed.
    let private taskRecord () =
        File.ReadAllText(Path.Combine(CliPort.repositoryRoot.Value, "tests", "fixtures", "telemetry", "work-start-task-execution.json"))

    /// The same shape for a research-type WI-B: only the work item and its
    /// classification differ.
    let private researchRecord () =
        let record = CliPort.parse (taskRecord ())
        record["workItemId"] <- JsonValue.Create "WI-B"
        record["classification"]["types"] <- JsonArray(JsonValue.Create "research")
        record["links"]["workItemId"] <- JsonValue.Create "WI-B"
        CliPort.compact record

    let private showProject = "Telemetry Show Differential"

    let private showTests =
        [ { Name = "telemetry cli: adapters lists the static adapter catalog exactly"
            Run = fun () ->
                CliPort.withDirectory "ros-telemetry-adapters" (fun root ->
                    let result = telemetry root [ "adapters" ] |> ok
                    CliPort.deepEqual
                        """["generic","openai-codex","anthropic-claude-statusline","anthropic-claude-hook","anthropic-claude-otel","google-gemini-hook","google-gemini-otel","github-copilot-hook","github-copilot-otel","otel-json"]"""
                        (stdoutJson result)) }
          { Name = "telemetry cli: show with no target lists every execution record"
            Run = fun () ->
                CliPort.withRepository showProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    CliPort.startWork root "WI-B" "2026-09-10T18:00:01.000Z" "research"
                    let records = telemetry root [ "show" ] |> ok |> stdoutJson
                    Assert.equal 2 (CliPort.items records).Length
                    CliPort.deepEqual $"[{taskRecord ()},{researchRecord ()}]" (stripVolatile records)
                    Assert.equal "WI-A" (CliPort.text (records[0]["workItemId"]))
                    Assert.equal "WI-B" (CliPort.text (records[1]["workItemId"]))) }
          { Name = "telemetry cli: show with no executions yet lists an empty array"
            Run = fun () ->
                CliPort.withRepository showProject (fun root ->
                    CliPort.deepEqual "[]" (telemetry root [ "show" ] |> ok |> stdoutJson)) }
          { Name = "telemetry cli: show TARGET filters to a work item, and an unmatched id lists nothing"
            Run = fun () ->
                CliPort.withRepository showProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    CliPort.startWork root "WI-B" "2026-09-10T18:00:01.000Z" "task"
                    let records = telemetry root [ "show"; "WI-A" ] |> ok |> stdoutJson
                    CliPort.deepEqual $"[{taskRecord ()}]" (stripVolatile records)
                    CliPort.deepEqual "[]" (telemetry root [ "show"; "WI-GHOST" ] |> ok |> stdoutJson)) }
          { Name = "telemetry cli: show EXE-ID resolves the single record; an unknown execution id is rejected"
            Run = fun () ->
                CliPort.withRepository showProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    let listed = telemetry root [ "show"; "WI-A" ] |> ok |> stdoutJson
                    let executionId = CliPort.text (listed[0]["executionId"])
                    let record = telemetry root [ "show"; executionId ] |> ok |> stdoutJson
                    CliPort.deepEqual (taskRecord ()) (stripVolatile record)
                    let ghost = telemetry root [ "show"; "EXE-GHOST" ]
                    CliPort.exitCode 1 ghost
                    CliPort.contains "telemetry execution 'EXE-GHOST' was not found" ghost.Err) } ]

    // ------------------------------------------------------------- summary

    let private summaryProject = "Telemetry Summary Differential"

    let private summaryMetric (summary: JsonNode) (metricId: string) =
        CliPort.items (summary["metrics"]) |> List.find (fun metric -> CliPort.stringOf metric "id" = Some metricId)

    let private summaryFixtureExecution (executionId: string) (sessionId: string) (tokens: int) (collectedAt: string) (includeWindow: bool) =
        let window =
            if includeWindow then
                (CliPort.fill [ "executionId", executionId; "collectedAt", collectedAt ] """,{"measurementId":"MEAS-WINDOW-{{executionId}}","id":"context.window_size","value":200000,"unit":"tokens","currency":null,"quality":"observed","confidence":null,"scope":"execution","aggregation":"none","dimensions":{},"pricing":null,"collectedAt":"{{collectedAt}}"}""")
            else
                ""

        (CliPort.fill [ "executionId", executionId; "sessionId", sessionId; "tokens", string tokens; "collectedAt", collectedAt; "window", window ] """{"schemaVersion":"1.0.0","executionId":"{{executionId}}","workItemId":"WI-FIX","status":"finalized","startedAt":"2026-01-01T00:00:00.000Z","finalizedAt":"2026-01-01T00:10:00.000Z",
"identity":{"provider":"anthropic","runtime":"claude-code","sessionId":"{{sessionId}}"},"capabilities":[],
"metrics":[{"measurementId":"MEAS-{{executionId}}","id":"session.tokens.cumulative","value":{{tokens}},"unit":"tokens","currency":null,"quality":"observed","confidence":null,"scope":"execution","aggregation":"latest-per-session","dimensions":{},"pricing":null,"collectedAt":"{{collectedAt}}"}{{window}}],
"events":[],"repository":{"start":{"available":false}},"links":{}}""")

    let private summaryTests =
        [ { Name = "telemetry cli: summary aggregates two real, completed executions"
            Run = fun () ->
                CliPort.withRepository summaryProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    CliPort.startWork root "WI-B" "2026-09-10T18:00:01.000Z" "task"
                    CliHarness.write root "IMPLEMENTATION-NOTES.md" "Implemented.\n"
                    CliHarness.write root "TESTS-NOTES.md" "Tested.\n"

                    CliHarness.rosOk root [ "work"; "complete"; "--id"; "WI-A"; "--id"; "WI-B"; "--occurred-at"; "2026-09-10T18:05:00.000Z"
                                            "--evidence"; "implementation=IMPLEMENTATION-NOTES.md"; "--evidence"; "tests=TESTS-NOTES.md" ]
                    |> ignore

                    let summary = telemetry root [ "summary" ] |> ok |> stdoutJson
                    Assert.equal 2.0 (CliPort.number (summary["executionCount"]))
                    CliPort.deepEqual """["unknown"]""" (summary["providers"])
                    CliPort.deepEqual """["unknown"]""" (summary["runtimes"])
                    Assert.equal true (CliPort.boolean (summary["timing"]["fullyFinalized"]))

                    for metricId, value, aggregation, measurements, unit in
                        [ "git.files_added", 4.0, "sum", 2.0, "count"
                          "git.files_modified", 0.0, "sum", 2.0, "count"
                          "documentation.files_changed", 4.0, "sum", 2.0, "count"
                          "git.baseline_dirty_files", 0.0, "maximum", 2.0, "count" ] do
                        let metric = summaryMetric summary metricId
                        Assert.equal value (CliPort.number (metric["value"]))
                        Assert.equal aggregation (CliPort.text (metric["aggregation"]))
                        Assert.equal measurements (CliPort.number (metric["measurements"]))
                        Assert.equal unit (CliPort.text (metric["unit"]))

                    let wall = summaryMetric summary "time.wall_ms"
                    Assert.isTrue (CliPort.number (wall["value"]) > 0.0) "time.wall_ms must be positive"
                    Assert.equal "sum of execution spans; may exceed calendarSpanMs when executions overlap" (CliPort.text (wall["note"]))) }
          { Name = "telemetry cli: summary WORKITEMID filters to that work item's execution"
            Run = fun () ->
                CliPort.withRepository summaryProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    CliPort.startWork root "WI-B" "2026-09-10T18:00:01.000Z" "task"
                    let summary = telemetry root [ "summary"; "WI-A" ] |> ok |> stdoutJson
                    Assert.equal "WI-A" (CliPort.text (summary["workItemId"]))
                    Assert.equal 1.0 (CliPort.number (summary["executionCount"]))) }
          { Name = "telemetry cli: summary with no executions reports an empty, zeroed summary"
            Run = fun () ->
                CliPort.withRepository summaryProject (fun root ->
                    let summary = telemetry root [ "summary" ] |> ok |> stdoutJson

                    CliPort.deepEqual
                        """{"schemaVersion":"1.0.0","workItemId":null,"executionCount":0,"providers":[],"runtimes":[],
                            "timing":{"fullyFinalized":false,"finalizedExecutionCount":0,"activeExecutionCount":0,"earliestStartedAt":null,
                                      "latestFinalizedAt":null,"calendarSpanMs":null,"totalExecutionWallMs":null,"overlappingExecutionMs":null},
                            "metrics":[]}"""
                        summary) }
          { Name = "telemetry cli: summary applies latest-per-session and none aggregation over hand-written executions"
            Run = fun () ->
                CliPort.withRepository summaryProject (fun root ->
                    CliPort.writeExecution root "EXE-FIX-1" (summaryFixtureExecution "EXE-FIX-1" "sess-A" 100 "2026-01-01T00:01:00.000Z" true)
                    CliPort.writeExecution root "EXE-FIX-2" (summaryFixtureExecution "EXE-FIX-2" "sess-A" 150 "2026-01-01T00:02:00.000Z" false)
                    CliPort.writeExecution root "EXE-FIX-3" (summaryFixtureExecution "EXE-FIX-3" "sess-B" 50 "2026-01-01T00:01:30.000Z" false)
                    let summary = telemetry root [ "summary"; "WI-FIX" ] |> ok |> stdoutJson

                    CliPort.deepEqual
                        """{"schemaVersion":"1.0.0","workItemId":"WI-FIX","executionCount":3,"providers":["anthropic"],"runtimes":["claude-code"],
                            "timing":{"fullyFinalized":true,"finalizedExecutionCount":3,"activeExecutionCount":0,"earliestStartedAt":"2026-01-01T00:00:00.000Z",
                                      "latestFinalizedAt":"2026-01-01T00:10:00.000Z","calendarSpanMs":600000,"totalExecutionWallMs":1800000,"overlappingExecutionMs":1200000},
                            "metrics":[
                              {"id":"context.window_size","value":null,"unit":"tokens","currency":null,"dimensions":{},"aggregation":"none","measurements":1,
                               "note":"not aggregated; inspect per-execution measurements"},
                              {"id":"session.tokens.cumulative","value":200,"unit":"tokens","currency":null,"dimensions":{},"aggregation":"latest-per-session","measurements":3,
                               "note":"latest value per unique provider session; cumulative snapshots are not summed"}]}"""
                        summary) } ]

    // ------------------------------------------------------------ finalize

    let private finalizeProject = "Telemetry Finalize Differential"
    let private ambiguous = "telemetry target is ambiguous; provide a work-item or execution ID"

    let private finalizeTests =
        [ { Name = "telemetry cli: finalize with no target finalizes the single active work item's execution with its git metrics"
            Run = fun () ->
                CliPort.withRepository finalizeProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    CliHarness.write root "IMPLEMENTATION-NOTES.md" "Implemented.\n"
                    let record = telemetry root [ "finalize" ] |> ok |> stdoutJson
                    Assert.equal "finalized" (CliPort.text (record["status"]))
                    Assert.equal "WI-A" (CliPort.text (record["workItemId"]))

                    for metricId, value in
                        [ "git.commits_created", 0.0; "git.files_added", 1.0; "git.files_modified", 0.0; "git.files_deleted", 0.0
                          "git.files_renamed", 0.0; "git.binary_files_changed", 0.0; "git.lines_added", 1.0; "git.lines_deleted", 0.0
                          "tests.added", 0.0; "tests.modified", 0.0; "tests.removed", 0.0; "documentation.files_changed", 1.0 ] do
                        Assert.equal (Some value) (CliPort.metricValue record metricId)) }
          { Name = "telemetry cli: finalize WORKITEM matches by workItemId regardless of status, taking the latest startedAt"
            Run = fun () ->
                CliPort.withRepository finalizeProject (fun root ->
                    writeFinalizeExecution root "EXE-OLD" "WI-A" "finalized" "2026-01-01T00:00:00.000Z"
                    writeFinalizeExecution root "EXE-NEW" "WI-A" "active" "2026-01-01T00:05:00.000Z"
                    let record = telemetry root [ "finalize"; "WI-A" ] |> ok |> stdoutJson
                    Assert.equal "EXE-NEW" (CliPort.text (record["executionId"]))
                    Assert.equal "finalized" (CliPort.text (record["status"]))
                    let old = CliPort.readExecution root "EXE-OLD"
                    Assert.equal "finalized" (CliPort.text (old["status"]))
                    Assert.equal "2026-01-01T00:00:00.000Z" (CliPort.text (old["finalizedAt"]))) }
          { Name = "telemetry cli: finalize EXE-ID resolves by exact execution id"
            Run = fun () ->
                CliPort.withRepository finalizeProject (fun root ->
                    writeFinalizeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeFinalizeExecution root "EXE-2" "WI-B" "active" "2026-01-01T00:00:01.000Z"
                    let record = telemetry root [ "finalize"; "EXE-1" ] |> ok |> stdoutJson
                    Assert.equal "EXE-1" (CliPort.text (record["executionId"]))
                    Assert.equal "finalized" (CliPort.text (record["status"]))
                    Assert.equal "active" (CliPort.text ((CliPort.readExecution root "EXE-2")["status"]))) }
          { Name = "telemetry cli: finalize with no target and no active work item is ambiguous"
            Run = fun () ->
                CliPort.withRepository finalizeProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    CliHarness.write root "IMPLEMENTATION-NOTES.md" "Implemented.\n"
                    CliHarness.write root "TESTS-NOTES.md" "Tested.\n"

                    CliHarness.rosOk root [ "work"; "complete"; "--id"; "WI-A"; "--occurred-at"; "2026-09-10T18:05:00.000Z"
                                            "--evidence"; "implementation=IMPLEMENTATION-NOTES.md"; "--evidence"; "tests=TESTS-NOTES.md" ]
                    |> ignore

                    let result = telemetry root [ "finalize" ]
                    CliPort.exitCode 1 result
                    CliPort.contains ambiguous result.Err) }
          { Name = "telemetry cli: finalize with no target and several active work items is ambiguous"
            Run = fun () ->
                CliPort.withRepository finalizeProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    CliPort.startWork root "WI-B" "2026-09-10T18:00:01.000Z" "task"
                    let result = telemetry root [ "finalize" ]
                    CliPort.exitCode 1 result
                    CliPort.contains ambiguous result.Err) }
          { Name = "telemetry cli: finalize rejects an unknown target"
            Run = fun () ->
                CliPort.withRepository finalizeProject (fun root ->
                    let result = telemetry root [ "finalize"; "WI-GHOST" ]
                    CliPort.exitCode 1 result
                    CliPort.contains "telemetry execution 'WI-GHOST' was not found" result.Err) }
          { Name = "telemetry cli: finalize returns an already-finalized execution untouched"
            Run = fun () ->
                CliPort.withRepository finalizeProject (fun root ->
                    writeFinalizeExecution root "EXE-1" "WI-A" "finalized" "2026-01-01T00:00:00.000Z"
                    let before = CliHarness.read root (CliPort.executionPath "EXE-1")
                    let record = telemetry root [ "finalize"; "EXE-1" ] |> ok |> stdoutJson
                    Assert.equal "finalized" (CliPort.text (record["status"]))
                    Assert.equal before (CliHarness.read root (CliPort.executionPath "EXE-1"))) }
          { Name = "telemetry cli: finalize --input is rejected as unsupported and changes nothing"
            Run = fun () ->
                CliPort.withRepository finalizeProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let result = telemetry root [ "finalize"; "EXE-1"; "--input"; "-" ]
                    CliPort.exitCode 2 result
                    CliPort.contains "telemetry finalize --input is not yet supported by this CLI" result.Err
                    Assert.equal "active" (CliPort.text ((CliPort.readExecution root "EXE-1")["status"]))) } ]

    // -------------------------------------------------------------- record

    let private recordProject = "Telemetry Record Differential"

    let private cliSource =
        """{"type":"agent-report","name":"ros-telemetry-cli","mechanism":"explicit-metric-record"}"""

    let private recordTests =
        [ { Name = "telemetry cli: record with no target records on the single active work item's execution"
            Run = fun () ->
                CliPort.withRepository recordProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    let metric = telemetry root [ "record"; "--metric"; "tokens.input"; "--value"; "42"; "--collected-at"; "2026-01-01T00:00:00.000Z" ] |> ok |> stdoutJson

                    CliPort.deepEqual
                        (CliPort.fill [ "cliSource", cliSource ] """{"measurementId":"MEAS-1b020374973b8c09f0500d46","id":"tokens.input","value":42,"unit":"tokens","currency":null,"quality":"observed",
                             "confidence":null,"scope":"execution","aggregation":"sum","dimensions":{},"pricing":null,"source":{{cliSource}},
                             "collectedAt":"2026-01-01T00:00:00.000Z","schemaVersion":"1.0.0"}""")
                        metric) }
          { Name = "telemetry cli: record WORKITEM targets only the active execution, ignoring a finalized sibling"
            Run = fun () ->
                CliPort.withRepository recordProject (fun root ->
                    writeMinimalExecution root "EXE-OLD" "WI-A" "finalized" "2026-01-01T00:00:00.000Z"
                    writeMinimalExecution root "EXE-NEW" "WI-A" "active" "2026-01-01T00:05:00.000Z"
                    telemetry root [ "record"; "WI-A"; "--metric"; "tokens.input"; "--value"; "1"; "--collected-at"; "2026-01-01T00:00:00.000Z" ] |> ok |> ignore
                    Assert.equal 1 (CliPort.items ((CliPort.readExecution root "EXE-NEW")["metrics"])).Length
                    Assert.equal 0 (CliPort.items ((CliPort.readExecution root "EXE-OLD")["metrics"])).Length) }
          { Name = "telemetry cli: record EXE-ID resolves by exact execution id"
            Run = fun () ->
                CliPort.withRepository recordProject (fun root ->
                    writeMinimalExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeMinimalExecution root "EXE-2" "WI-B" "active" "2026-01-01T00:00:01.000Z"
                    telemetry root [ "record"; "EXE-1"; "--metric"; "tokens.input"; "--value"; "5"; "--collected-at"; "2026-01-01T00:00:00.000Z" ] |> ok |> ignore
                    Assert.equal 1 (CliPort.items ((CliPort.readExecution root "EXE-1")["metrics"])).Length
                    Assert.equal 0 (CliPort.items ((CliPort.readExecution root "EXE-2")["metrics"])).Length) }
          { Name = "telemetry cli: record with no target and zero or several active work items is ambiguous"
            Run = fun () ->
                CliPort.withRepository recordProject (fun root ->
                    let none = telemetry root [ "record"; "--metric"; "tokens.input"; "--value"; "1" ]
                    CliPort.exitCode 1 none
                    CliPort.contains ambiguous none.Err)

                CliPort.withRepository recordProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    CliPort.startWork root "WI-B" "2026-09-10T18:00:01.000Z" "task"
                    let many = telemetry root [ "record"; "--metric"; "tokens.input"; "--value"; "1" ]
                    CliPort.exitCode 1 many
                    CliPort.contains ambiguous many.Err) }
          { Name = "telemetry cli: record rejects a target whose only execution is finalized"
            Run = fun () ->
                CliPort.withRepository recordProject (fun root ->
                    writeMinimalExecution root "EXE-1" "WI-A" "finalized" "2026-01-01T00:00:00.000Z"
                    let result = telemetry root [ "record"; "WI-A"; "--metric"; "tokens.input"; "--value"; "1" ]
                    CliPort.exitCode 1 result
                    CliPort.contains "telemetry execution 'WI-A' was not found or is already finalized" result.Err) }
          { Name = "telemetry cli: record rejects an unregistered metric and a non-numeric value"
            Run = fun () ->
                CliPort.withRepository recordProject (fun root ->
                    writeMinimalExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let unknown = telemetry root [ "record"; "EXE-1"; "--metric"; "bogus.metric"; "--value"; "1" ]
                    CliPort.exitCode 1 unknown
                    CliPort.contains "unknown normalized metric 'bogus.metric'; preserve it in raw telemetry until it is registered" unknown.Err
                    let invalid = telemetry root [ "record"; "EXE-1"; "--metric"; "tokens.input"; "--value"; "not-a-number" ]
                    CliPort.exitCode 1 invalid
                    CliPort.contains "metric 'tokens.input' requires a finite numeric value" invalid.Err) }
          { Name = "telemetry cli: record deduplicates an identical repeated call by its content-addressed measurementId"
            Run = fun () ->
                CliPort.withRepository recordProject (fun root ->
                    writeMinimalExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let arguments = [ "record"; "EXE-1"; "--metric"; "tokens.input"; "--value"; "1"; "--collected-at"; "2026-01-01T00:00:00.000Z" ]
                    telemetry root arguments |> ok |> ignore
                    telemetry root arguments |> ok |> ignore
                    let metrics = CliPort.items ((CliPort.readExecution root "EXE-1")["metrics"])
                    Assert.equal 1 metrics.Length
                    Assert.equal "MEAS-6cf5ce5166c3e0786d967b26" (CliPort.text (metrics[0]["measurementId"]))) }
          { Name = "telemetry cli: record upserts a registry-seeded capability from unknown to supported-observed"
            Run = fun () ->
                CliPort.withRepository recordProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"
                    telemetry root [ "record"; "WI-A"; "--metric"; "tokens.input"; "--value"; "10"; "--collected-at"; "2026-01-01T00:00:00.000Z" ] |> ok |> ignore
                    let execution = executionFiles root |> List.head
                    let capability = capabilityFor execution (Some "tokens.input") None
                    Assert.equal "supported-observed" (CliPort.text (capability["status"]))
                    Assert.equal "normalized measurement recorded" (CliPort.text (capability["reason"]))
                    let history = CliPort.items (capability["history"])
                    Assert.equal 1 history.Length
                    Assert.equal "unknown" (CliPort.text (history[0]["status"]))) }
          { Name = "telemetry cli: record honors --unit/--currency overrides and numeric or text --confidence"
            Run = fun () ->
                CliPort.withRepository recordProject (fun root ->
                    writeMinimalExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    let cost =
                        telemetry root [ "record"; "EXE-1"; "--metric"; "cost.input"; "--value"; "0.05"; "--unit"; "usd-cents"; "--currency"; "USD"
                                         "--quality"; "estimated"; "--confidence"; "0.9"; "--collected-at"; "2026-01-01T00:00:00.000Z" ]
                        |> ok |> stdoutJson

                    CliPort.deepEqual
                        (CliPort.fill [ "cliSource", cliSource ] """{"measurementId":"MEAS-041a30e3db0284e7b85a0f96","id":"cost.input","value":0.05,"unit":"usd-cents","currency":"USD","quality":"estimated",
                             "confidence":0.9,"scope":"execution","aggregation":"sum","dimensions":{},"pricing":null,"source":{{cliSource}},
                             "collectedAt":"2026-01-01T00:00:00.000Z","schemaVersion":"1.0.0"}""")
                        cost

                    let tokens =
                        telemetry root [ "record"; "EXE-1"; "--metric"; "tokens.output"; "--value"; "1"; "--confidence"; "high"; "--collected-at"; "2026-01-01T00:00:00.000Z" ]
                        |> ok |> stdoutJson

                    CliPort.deepEqual
                        (CliPort.fill [ "cliSource", cliSource ] """{"measurementId":"MEAS-01fb61fdd2d6ca8a756d8cb0","id":"tokens.output","value":1,"unit":"tokens","currency":null,"quality":"observed",
                             "confidence":"high","scope":"execution","aggregation":"sum","dimensions":{},"pricing":null,"source":{{cliSource}},
                             "collectedAt":"2026-01-01T00:00:00.000Z","schemaVersion":"1.0.0"}""")
                        tokens) } ]

    // -------------------------------------------------------------- ingest

    let private ingestProject = "Telemetry Ingest Differential"

    let private ingest root (target: string) (input: string) (extra: string list) =
        CliPort.withInputFile input (fun file -> telemetry root ([ "ingest"; target; "--input"; file ] @ extra))

    let private ingestTests =
        [ { Name = "telemetry cli: ingest merges identity, metrics and events, redacts sensitive raw fields, and records derived quality metrics"
            Run = fun () ->
                CliPort.withRepository ingestProject (fun root ->
                    CliPort.startWork root "WI-A" "2026-09-10T18:00:00.000Z" "task"

                    let input =
                        """{"identity":{"model":"claude-opus-5"},"metrics":[{"id":"tokens.input","value":100}],
                            "events":[{"type":"custom.thing","occurredAt":"2026-01-01T00:00:00.000Z"}],
                            "raw":{"authorization":"secret-token","nested":{"password":"hunter2"},"safe":"value"},"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    let record = ingest root "WI-A" input [] |> ok |> stdoutJson
                    Assert.equal "claude-opus-5" (CliPort.text (record["identity"]["model"]))
                    Assert.equal (Some 100.0) (CliPort.metricValue record "tokens.input")
                    Assert.equal "supported-observed" (CliPort.text ((capabilityFor record (Some "tokens.input") None)["status"]))
                    Assert.equal 1 (eventsOfType record "custom.thing").Length

                    let payload = record["rawTelemetry"].[0].["payload"]

                    CliPort.deepEqual
                        """{"authorization":"[REDACTED_BY_ROS]","nested":{"password":"[REDACTED_BY_ROS]"},"safe":"value"}"""
                        payload

                    Assert.equal (Some 2.0) (CliPort.metricValue record "telemetry.redactions")
                    Assert.equal (Some 3.0) (CliPort.metricValue record "telemetry.unknown_fields")
                    Assert.equal "TEVT-2541ad12ab6dd91030b33689" (CliPort.text (eventsOfType record "custom.thing").Head["eventId"])
                    let ingestion = (eventsOfType record "telemetry.snapshot.ingested").Head
                    Assert.equal "SNAP-8d6a5b7d21c803d74551b507" (CliPort.text (ingestion["snapshotId"]))
                    Assert.equal "TEVT-d97004e63ef02225e7a84dab" (CliPort.text (ingestion["eventId"]))) }
          { Name = "telemetry cli: ingest deduplicates a repeated identical snapshot"
            Run = fun () ->
                CliPort.withRepository ingestProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let input = """{"metrics":[{"id":"tokens.input","value":1}],"raw":{"safe":"value"},"collectedAt":"2026-01-01T00:00:00.000Z"}"""
                    ingest root "EXE-1" input [] |> ok |> ignore
                    let first = CliPort.readExecution root "EXE-1"
                    ingest root "EXE-1" input [] |> ok |> ignore
                    let second = CliPort.readExecution root "EXE-1"
                    Assert.equal 2 (CliPort.items (first["metrics"])).Length
                    Assert.equal (CliPort.items (first["metrics"])).Length (CliPort.items (second["metrics"])).Length
                    Assert.equal 1 (CliPort.items (second["rawTelemetry"])).Length) }
          { Name = "telemetry cli: ingest merges a declared capability into history and rejects a metric reported while declared unavailable"
            Run = fun () ->
                CliPort.withRepository ingestProject (fun root ->
                    let seeded =
                        """[{"metricId":"tool.calls","status":"unknown","reason":"runtime capability not reported or mapped","discoveredAt":"2025-12-31T00:00:00.000Z",
                             "source":{"type":"environment","name":"runtime-identity","mechanism":"whitelisted-claude-environment"}}]"""

                    CliPort.writeExecution root "EXE-1" (fullExecution "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z" "anthropic" "claude-code" seeded)
                    let declared = """{"capabilities":[{"metricId":"tool.calls","status":"supported-observed","reason":"custom","discoveredAt":"2026-01-01T00:00:00.000Z"}],"collectedAt":"2026-01-01T00:00:00.000Z"}"""
                    ingest root "EXE-1" declared [] |> ok |> ignore
                    let capability = capabilityFor (CliPort.readExecution root "EXE-1") (Some "tool.calls") None
                    Assert.equal "supported-observed" (CliPort.text (capability["status"]))
                    let history = CliPort.items (capability["history"])
                    Assert.equal 1 history.Length
                    Assert.equal "unknown" (CliPort.text (history[0]["status"]))

                    let conflict =
                        ingest root "EXE-1" """{"metrics":[{"id":"tool.calls","value":3}],"capabilities":[{"metricId":"tool.calls","status":"unsupported"}],"collectedAt":"2026-02-01T00:00:00.000Z"}""" []

                    CliPort.exitCode 1 conflict
                    CliPort.contains "declaring it unavailable or unsupported" conflict.Err
                    CliPort.contains "'SNAP-01ce3a53e3cc3b0792defcf0'" conflict.Err) }
          { Name = "telemetry cli: ingest shallow-merges classification and scope and union-deduplicates links"
            Run = fun () ->
                CliPort.withRepository ingestProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    ingest root "EXE-1" """{"links":{"commits":["abc"]},"collectedAt":"2026-01-01T00:00:00.000Z"}""" [] |> ok |> ignore

                    ingest root "EXE-1" """{"classification":{"rationale":null},"scope":{"operationId":"op-1"},"links":{"commits":["abc","def"]},"collectedAt":"2026-01-02T00:00:00.000Z"}""" []
                    |> ok |> ignore

                    let record = CliPort.readExecution root "EXE-1"
                    Assert.isTrue (isNull (record["classification"]["rationale"])) "rationale must stay null"
                    Assert.equal "op-1" (CliPort.text (record["scope"]["operationId"]))
                    CliPort.deepEqual """["abc","def"]""" (record["links"]["commits"])) }
          { Name = "telemetry cli: ingest rejects an unregistered metric and a target that is not active"
            Run = fun () ->
                CliPort.withRepository ingestProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeFull root "EXE-2" "WI-B" "finalized" "2026-01-01T00:00:00.000Z"
                    let unknown = ingest root "EXE-1" """{"metrics":[{"id":"bogus.metric","value":1}],"collectedAt":"2026-01-01T00:00:00.000Z"}""" []
                    CliPort.exitCode 1 unknown
                    CliPort.contains "unknown normalized metric 'bogus.metric'" unknown.Err
                    let inactive = ingest root "WI-B" """{"collectedAt":"2026-01-01T00:00:00.000Z"}""" []
                    CliPort.exitCode 1 inactive
                    CliPort.contains "telemetry execution 'WI-B' was not found or is already finalized" inactive.Err) }
          { Name = "telemetry cli: ingest rejects an unknown adapter"
            Run = fun () ->
                CliPort.withRepository ingestProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let result = ingest root "EXE-1" """{"collectedAt":"2026-01-01T00:00:00.000Z"}""" [ "--adapter"; "bogus-adapter" ]
                    CliPort.exitCode 1 result
                    CliPort.contains "unknown telemetry adapter 'bogus-adapter'" result.Err) }
          { Name = "telemetry cli: ingest honors the repository's raw-telemetry retention policy"
            Run = fun () ->
                CliPort.withRepository ingestProject (fun root ->
                    let configuration = CliPort.readJson root "ros.json"
                    configuration["telemetry"]["allowRawTelemetry"] <- JsonValue.Create false
                    CliPort.writeJson root "ros.json" configuration
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    ingest root "EXE-1" """{"raw":{"field":"value"},"collectedAt":"2026-01-01T00:00:00.000Z"}""" [] |> ok |> ignore
                    let record = CliPort.readExecution root "EXE-1"
                    Assert.equal 0 (CliPort.items (record["rawTelemetry"])).Length
                    Assert.equal (Some 1.0) (CliPort.metricValue record "telemetry.raw_snapshots_omitted")
                    let field = capabilityFor record None (Some "$.field")
                    Assert.equal "unknown" (CliPort.text (field["status"]))
                    CliPort.contains "raw payload was omitted" (CliPort.text (field["reason"]))) }
          { Name = "telemetry cli: ingest accepts input on standard input"
            Run = fun () ->
                CliPort.withRepository ingestProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    let result =
                        CliPort.rosWithInput root [ "telemetry"; "ingest"; "EXE-1"; "--input"; "-"; "--quiet" ] """{"metrics":[{"id":"tokens.input","value":7}],"collectedAt":"2026-01-01T00:00:00.000Z"}"""

                    CliPort.exitCode 0 result
                    Assert.equal (Some 7.0) (CliPort.metricValue (CliPort.readExecution root "EXE-1") "tokens.input")) } ]

    // ------------------------------------------------------------ classify

    let private classifyProject = "Telemetry Classify Differential"

    let private classifyTests =
        [ { Name = "telemetry cli: classify prints the classification with rationale and evidence links"
            Run = fun () ->
                CliPort.withRepository classifyProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    let classification =
                        telemetry root [ "classify"; "EXE-1"; "--classification"; "research"; "--classification"; "documentation"; "--rationale"; "testing"
                                         "--evidence-link"; "https://example.com/a"; "--evidence-link"; "https://example.com/b" ]
                        |> ok |> stdoutJson

                    CliPort.deepEqual
                        """{"types":["research","documentation"],"rationale":"testing","evidence":["https://example.com/a","https://example.com/b"],"rd":null}"""
                        classification) }
          { Name = "telemetry cli: classify merges an --rd-context file verbatim into rd"
            Run = fun () ->
                CliPort.withRepository classifyProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    CliPort.withInputFile """{"decisionId":"DEC-1","notes":"some context"}""" (fun rd ->
                        let classification = telemetry root [ "classify"; "EXE-1"; "--classification"; "research"; "--rd-context"; rd ] |> ok |> stdoutJson

                        CliPort.deepEqual
                            """{"types":["research"],"rationale":null,"evidence":[],"rd":{"decisionId":"DEC-1","notes":"some context"}}"""
                            classification)) }
          { Name = "telemetry cli: classify without --classification is rejected"
            Run = fun () ->
                CliPort.withRepository classifyProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let result = telemetry root [ "classify"; "EXE-1" ]
                    CliPort.exitCode 1 result
                    CliPort.contains "telemetry classify requires at least one --classification" result.Err) }
          { Name = "telemetry cli: classify never deduplicates a repeated call"
            Run = fun () ->
                CliPort.withRepository classifyProject (fun root ->
                    writeFull root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    telemetry root [ "classify"; "EXE-1"; "--classification"; "research" ] |> ok |> ignore
                    // The snapshot id is `classification-{unix ms}`; two process
                    // launches are never inside one millisecond, but make sure.
                    Threading.Thread.Sleep 2
                    telemetry root [ "classify"; "EXE-1"; "--classification"; "documentation" ] |> ok |> ignore
                    let record = CliPort.readExecution root "EXE-1"
                    Assert.equal 2 (eventsOfType record "telemetry.snapshot.ingested").Length
                    CliPort.deepEqual """["documentation"]""" (record["classification"]["types"])) } ]

    // --------------------------------------------------------------- start

    let private startProject = "Telemetry Start Differential"

    /// An initialized repository whose work context holds exactly `workItems`.
    let private withContext (workItems: string) (run: string -> unit) =
        CliPort.withRepository startProject (fun root ->
            let configuration = CliPort.readJson root "ros.json"
            let repositoryId = CliPort.text (configuration["repository"]["id"])

            CliHarness.write
                root
                ".ros/context/current.json"
                (CliPort.indented (CliPort.fill [ "repositoryId", repositoryId; "workItems", workItems ] """{"schemaVersion":"1.0.0","protocolVersion":"1.0.0","repository":"{{repositoryId}}","actor":"actor","updatedAt":"2026-01-01T00:00:00.000Z","workItems":{{workItems}}}""")
                 + "\n")

            run root)

    let private activeItem (id: string) =
        (CliPort.fill [ "id", id ] """[{"id":"{{id}}","type":"task","state":"active","semanticState":"active","evidence":[]}]""")

    let private contextItem root (id: string) =
        CliPort.items ((CliPort.readJson root ".ros/context/current.json")["workItems"])
        |> List.find (fun item -> CliPort.stringOf item "id" = Some id)

    let private writeDetached root executionId workItemId startedAt =
        CliPort.writeExecution root executionId (fullExecution executionId workItemId "active" startedAt "anthropic" "claude-code" "[]")

    let private start root arguments = telemetry root ("start" :: arguments)

    let private startTests =
        [ { Name = "telemetry cli: start creates a fresh linked execution for an active work item with no candidates"
            Run = fun () ->
                withContext (activeItem "WI-A") (fun root ->
                    let record = start root [ "WI-A"; "--classification"; "research" ] |> ok |> stdoutJson
                    CliPort.deepEqual """{"types":["research"],"rationale":null,"evidence":[],"rd":null}""" (record["classification"])
                    Assert.equal "WI-A" (CliPort.text (record["workItemId"]))
                    Assert.equal "active" (CliPort.text (record["status"]))
                    CliPort.deepEqual $"""["{CliPort.text (record["executionId"])}"]""" ((contextItem root "WI-A")["telemetryExecutionIds"])) }
          { Name = "telemetry cli: start recovers a detached execution instead of creating one"
            Run = fun () ->
                withContext (activeItem "WI-B") (fun root ->
                    writeDetached root "EXE-DETACHED" "WI-B" "2020-01-01T00:00:00.000Z"
                    let record = start root [ "WI-B" ] |> ok |> stdoutJson
                    Assert.equal "EXE-DETACHED" (CliPort.text (record["executionId"]))
                    CliPort.deepEqual """["EXE-DETACHED"]""" ((contextItem root "WI-B")["telemetryExecutionIds"])) }
          { Name = "telemetry cli: start creates a new execution when the only candidate is already linked"
            Run = fun () ->
                withContext """[{"id":"WI-C","type":"task","state":"active","semanticState":"active","evidence":[],"telemetryExecutionIds":["EXE-LINKED"]}]""" (fun root ->
                    writeDetached root "EXE-LINKED" "WI-C" "2020-01-01T00:00:00.000Z"
                    let record = start root [ "WI-C" ] |> ok |> stdoutJson
                    let executionId = CliPort.text (record["executionId"])
                    Assert.isTrue (executionId <> "EXE-LINKED") "a linked candidate must not be reused"
                    CliPort.deepEqual $"""["EXE-LINKED","{executionId}"]""" ((contextItem root "WI-C")["telemetryExecutionIds"])) }
          { Name = "telemetry cli: start rejects ambiguous detached candidates with the rerun message"
            Run = fun () ->
                withContext (activeItem "WI-D") (fun root ->
                    writeDetached root "EXE-DETACHED-1" "WI-D" "2020-01-01T00:00:00.000Z"
                    writeDetached root "EXE-DETACHED-2" "WI-D" "2020-01-01T00:00:01.000Z"
                    let result = start root [ "WI-D" ]
                    CliPort.exitCode 1 result

                    Assert.equal
                        "ERROR multiple detached telemetry executions require explicit selection for 'WI-D'; rerun with --execution-id one of: EXE-DETACHED-1, EXE-DETACHED-2"
                        (result.Err.Trim())) }
          { Name = "telemetry cli: start rejects a work item that is not active or blocked, and an unknown one alike"
            Run = fun () ->
                withContext """[{"id":"WI-E","type":"task","state":"ready","semanticState":"ready","evidence":[]}]""" (fun root ->
                    let result = start root [ "WI-E" ]
                    CliPort.exitCode 1 result
                    Assert.equal "ERROR work item 'WI-E' must be active or blocked before starting telemetry" (result.Err.Trim()))

                withContext (activeItem "WI-A") (fun root ->
                    let result = start root [ "WI-NOPE" ]
                    CliPort.exitCode 1 result
                    Assert.equal "ERROR work item 'WI-NOPE' must be active or blocked before starting telemetry" (result.Err.Trim())) }
          { Name = "telemetry cli: start prints null and leaves context untouched when telemetry is disabled"
            Run = fun () ->
                withContext (activeItem "WI-F") (fun root ->
                    let configuration = CliPort.readJson root "ros.json"
                    configuration["telemetry"]["enabled"] <- JsonValue.Create false
                    CliPort.writeJson root "ros.json" configuration
                    let before = CliHarness.read root ".ros/context/current.json"
                    let result = start root [ "WI-F" ] |> ok
                    Assert.equal "null" (result.Out.Trim())
                    Assert.equal before (CliHarness.read root ".ros/context/current.json")) }
          { Name = "telemetry cli: start --execution-id selects a matching detached candidate"
            Run = fun () ->
                withContext (activeItem "WI-G") (fun root ->
                    writeDetached root "EXE-DETACHED-1" "WI-G" "2020-01-01T00:00:00.000Z"
                    writeDetached root "EXE-DETACHED-2" "WI-G" "2020-01-01T00:00:01.000Z"
                    let record = start root [ "WI-G"; "--execution-id"; "EXE-DETACHED-2" ] |> ok |> stdoutJson
                    Assert.equal "EXE-DETACHED-2" (CliPort.text (record["executionId"]))
                    CliPort.deepEqual """["EXE-DETACHED-2"]""" ((contextItem root "WI-G")["telemetryExecutionIds"])) }
          { Name = "telemetry cli: start --execution-id rejects a non-matching id while detached candidates exist"
            Run = fun () ->
                withContext (activeItem "WI-H") (fun root ->
                    writeDetached root "EXE-DETACHED-1" "WI-H" "2020-01-01T00:00:00.000Z"
                    writeDetached root "EXE-DETACHED-2" "WI-H" "2020-01-01T00:00:01.000Z"
                    let result = start root [ "WI-H"; "--execution-id"; "EXE-NOPE" ]
                    CliPort.exitCode 1 result

                    Assert.equal
                        "ERROR detached telemetry execution must be linked before creating 'EXE-NOPE' for 'WI-H'; rerun with --execution-id EXE-DETACHED-1"
                        (result.Err.Trim())) }
          { Name = "telemetry cli: start --execution-id names a newly created execution when no candidate exists"
            Run = fun () ->
                withContext (activeItem "WI-I") (fun root ->
                    let record = start root [ "WI-I"; "--execution-id"; "EXE-CUSTOM-ID" ] |> ok |> stdoutJson
                    Assert.equal "EXE-CUSTOM-ID" (CliPort.text (record["executionId"]))) }
          { Name = "telemetry cli: start's identity-override flags produce the expected identity"
            Run = fun () ->
                withContext (activeItem "WI-J") (fun root ->
                    let record =
                        start root [ "WI-J"; "--provider"; "acme"; "--model"; "acme-model"; "--model-version"; "v2"; "--runtime"; "acme-cli"
                                     "--runtime-version"; "9.9.9"; "--session"; "sess-123"; "--conversation"; "conv-456"; "--run"; "run-789"
                                     "--agent"; "agent-1"; "--subagent"; "subagent-2"; "--parent-execution"; "EXE-PARENT" ]
                        |> ok |> stdoutJson

                    let identity = (record["identity"]).DeepClone().AsObject()
                    identity.Remove "orchestration" |> ignore

                    CliPort.deepEqual
                        """{"provider":"acme","model":"acme-model","modelVersion":"v2","runtime":"acme-cli","runtimeVersion":"9.9.9","sessionId":"sess-123",
                            "conversationId":"conv-456","runId":"run-789","agentId":"agent-1","subagentId":"subagent-2","parentExecutionId":"EXE-PARENT","actorKind":"unknown"}"""
                        identity) } ]

    // ------------------------------------------------------------ validate

    let private validateProject = "Telemetry Validate Differential"

    let private findings root =
        let result = telemetry root [ "validate"; "--json" ]

        CliPort.items ((CliPort.parse result.Out)["findings"])
        |> List.map (fun finding -> CliPort.text (finding["path"]), CliPort.text (finding["field"]), CliPort.text (finding["message"]))
        |> List.sort

    let private work root arguments = CliHarness.rosOk root ("work" :: arguments) |> ignore

    let private startedWorkItem root =
        work root [ "capture"; "--id"; "WI-ONE"; "--title"; "Task one"; "--occurred-at"; CliHarness.now () ]
        work root [ "backlog-transition"; "--id"; "WI-ONE"; "--action"; "ready"; "--occurred-at"; CliHarness.now () ]
        work root [ "start"; "--id"; "WI-ONE"; "--occurred-at"; CliHarness.now () ]

    let private completeWorkItem root =
        CliHarness.write root "note.txt" "hello\n"
        CliHarness.git root [ "add"; "-A" ] |> ignore
        work root [ "complete"; "--id"; "WI-ONE"; "--occurred-at"; CliHarness.now (); "--evidence"; "implementation=note.txt"; "--evidence"; "tests=note.txt" ]

    /// Appends an execution id to a context work item's telemetry links.
    let private linkExecution (item: JsonNode) (executionId: string) =
        let linked =
            match item["telemetryExecutionIds"] with
            | :? JsonArray as ids -> ids |> Seq.map (fun id -> id.DeepClone()) |> Seq.toList
            | _ -> []

        item["telemetryExecutionIds"] <- JsonArray(linked @ [ JsonValue.Create executionId :> JsonNode ] |> List.toArray)

    let private validateTests =
        [ { Name = "telemetry cli: validate finds nothing wrong with a real work-start execution"
            Run = fun () ->
                CliPort.withRepository validateProject (fun root ->
                    startedWorkItem root
                    Assert.equal [] (findings root)) }
          { Name = "telemetry cli: validate finds nothing wrong with a completed, finalized execution"
            Run = fun () ->
                CliPort.withRepository validateProject (fun root ->
                    startedWorkItem root
                    completeWorkItem root
                    Assert.equal [] (findings root)) }
          { Name = "telemetry cli: validate reports disabled telemetry without a reason, then a malformed metric registry"
            Run = fun () ->
                CliPort.withRepository validateProject (fun root ->
                    let configuration = CliPort.readJson root "ros.json"
                    configuration["telemetry"] <- CliPort.parse """{"enabled":false}"""
                    CliPort.writeJson root "ros.json" configuration
                    Assert.equal [ "ros.json", "telemetry.disabledReason", "disabled telemetry requires an explicit reason" ] (findings root)

                    configuration["telemetry"] <- CliPort.parse """{"enabled":true}"""
                    CliPort.writeJson root "ros.json" configuration
                    CliHarness.write root "telemetry/metrics.json" """{"schemaVersion":"1.0.0","metrics":[{"id":"a"},{"id":"a"}]}"""

                    Assert.equal
                        [ "telemetry/metrics.json", "schemaVersion", "telemetry metric registry has an invalid definition for 'a'" ]
                        (findings root)) }
          { Name = "telemetry cli: validate reports every defect of a deliberately broken execution record"
            Run = fun () ->
                CliPort.withRepository validateProject (fun root ->
                    CliPort.writeExecution
                        root
                        "EXE-BROKEN-0001"
                        """{"schemaVersion":"1.0.0","executionId":"EXE-WRONG-ID","workItemId":"","identity":{"provider":"anthropic","runtime":"claude-code","model":5},
                            "provenance":{"collector":"ros","collectorVersion":"1.0.0","discoveredAt":"2026-01-01T00:00:00.000Z"},"startedAt":"2026-01-01T00:00:00.000Z","status":"weird",
                            "capabilities":[{"status":"supported-observed","metricId":"time.wall_ms","source":{"type":"ros-clock","name":"x","mechanism":"y"},
                                             "discoveredAt":"2026-01-01T00:00:00.000Z","lastAssessedAt":"not-a-date"}],
                            "metrics":[{"id":"time.wall_ms","measurementId":"m1","value":-5,"quality":"observed","source":{"type":"ros-clock","name":"x","mechanism":"y"},
                                        "collectedAt":"2026-01-01T00:00:00.000Z","schemaVersion":"1.0.0","scope":"execution","aggregation":"sum","unit":"milliseconds"}],
                            "rawTelemetry":[],"events":[],"repository":{},"links":{},"classification":{"types":["development","development"]}}"""

                    let path = ".ros/telemetry/executions/EXE-BROKEN-0001.json"

                    Assert.equal
                        ([ path, "capabilities[0].lastAssessedAt", "capability assessment timestamp must be valid"
                           path, "classification.types", "work classifications must be unique"
                           path, "executionId", "execution filename must match executionId"
                           path, "identity.model", "identity value must be a string or null"
                           path, "metrics[0].quality", "ROS-derived metric cannot be represented as observed or estimated"
                           path, "metrics[0].value", "metric value must be a finite non-negative number"
                           path, "status", "invalid execution status 'weird'"
                           path, "workItemId", "work-item linkage is required" ]
                         |> List.sort)
                        (findings root)) }
          { Name = "telemetry cli: validate reports history order, detectors, unredacted raw fields and missing back-links"
            Run = fun () ->
                CliPort.withRepository validateProject (fun root ->
                    startedWorkItem root

                    CliPort.writeExecution
                        root
                        "EXE-RAW-0001"
                        """{"schemaVersion":"1.0.0","executionId":"EXE-RAW-0001","workItemId":"WI-ONE","identity":{"provider":"anthropic","runtime":"claude-code"},
                            "provenance":{"collector":"ros","collectorVersion":"1.0.0","discoveredAt":"2026-01-01T00:00:00.000Z"},"startedAt":"2026-01-01T00:00:00.000Z","status":"active",
                            "capabilities":[{"status":"supported-observed","metricId":"time.wall_ms","source":{"type":"ros-clock","name":"x","mechanism":"y"},"discoveredAt":"2026-01-01T00:00:00.000Z",
                              "history":[{"status":"unknown","source":{"type":"ros-clock","name":"x","mechanism":"y"},"discoveredAt":"2026-01-01T00:00:00.000Z","recordedAt":"2026-01-03T00:00:00.000Z"},
                                         {"status":"unknown","source":{"type":"ros-clock","name":"x","mechanism":"y"},"discoveredAt":"2026-01-01T00:00:00.000Z","recordedAt":"2026-01-02T00:00:00.000Z"}]}],
                            "metrics":[],"qualitySignals":[{"detector":"not-a-real-detector","source":{"type":"human-report","name":"x","mechanism":"y"}}],
                            "rawTelemetry":[{"snapshotId":"SNAP-1","adapter":"generic","source":{"type":"runtime-output","name":"x","mechanism":"y"},"collectedAt":"2026-01-01T00:00:00.000Z",
                                             "payload":{"password":"hunter2","safe_field":"ok"}}],
                            "events":[],"repository":{},"links":{},"classification":{"types":["development"]}}"""

                    let path = ".ros/telemetry/executions/EXE-RAW-0001.json"

                    Assert.equal
                        ([ path, "capabilities[0].history[0]", "capability state recording order must be chronological"
                           path, "qualitySignals[0].detector", "invalid quality-signal detector 'not-a-real-detector'"
                           path, "rawTelemetry[0].payload", "sensitive raw field is not redacted: $.password"
                           path, "workItemId", "execution 'EXE-RAW-0001' is not linked back from work item 'WI-ONE'" ]
                         |> List.sort)
                        (findings root |> List.filter (fun (findingPath, _, _) -> findingPath.Contains "EXE-RAW"))) }
          { Name = "telemetry cli: validate reports a completed work item with unfinalized linked telemetry"
            Run = fun () ->
                CliPort.withRepository validateProject (fun root ->
                    startedWorkItem root
                    completeWorkItem root

                    CliPort.writeExecution
                        root
                        "EXE-UNFIN-0001"
                        """{"schemaVersion":"1.0.0","executionId":"EXE-UNFIN-0001","workItemId":"WI-ONE","identity":{"provider":"anthropic","runtime":"claude-code"},
                            "provenance":{"collector":"ros","collectorVersion":"1.0.0","discoveredAt":"2026-01-01T00:00:00.000Z"},"startedAt":"2026-01-01T00:00:00.000Z","status":"active",
                            "capabilities":[],"metrics":[],"rawTelemetry":[],"events":[],"repository":{},"links":{},"classification":{"types":["development"]}}"""

                    let context = CliPort.readJson root ".ros/context/current.json"

                    CliPort.items (context["workItems"])
                    |> List.filter (fun item -> CliPort.stringOf item "id" = Some "WI-ONE")
                    |> List.iter (fun item -> linkExecution item "EXE-UNFIN-0001")

                    CliPort.writeJson root ".ros/context/current.json" context

                    Assert.equal
                        [ ".ros/telemetry/executions/EXE-UNFIN-0001.json", "status", "completed work item 'WI-ONE' has unfinalized telemetry" ]
                        (findings root)) } ]

    let tests =
        showTests @ summaryTests @ finalizeTests @ recordTests @ ingestTests @ classifyTests @ startTests @ validateTests
