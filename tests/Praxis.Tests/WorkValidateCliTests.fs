namespace Praxis.Tests

open System.IO
open System.Text.Json.Nodes

/// `work validate --json` (work-item attribution of meaningful Git changes)
/// and `work backlog-validate --json` (the backlog queue's own findings)
/// through the real CLI (ported from the retired tests/work-attribution-
/// fsharp-differential and work-backlog-validate-fsharp-differential
/// .test.mjs suites; findings are the frozen Node goldens).
[<RequireQualifiedAccess>]
module WorkValidateCliTests =
    let private validJson = """{ "valid": true, "findings": [] }"""

    let private validate root (command: string) (expectedExit: int) (expected: string) =
        let result = CliHarness.ros root [ "work"; command; "--json" ]
        CliGolden.expectExit expectedExit result
        CliGolden.jsonEqual (CliGolden.parse expected) (CliGolden.parse result.Out)

    /// Telemetry disabled, attribution enforced (unless told otherwise), a
    /// context holding WORKITEMS and BASELINE, then one baseline commit.
    let private withAttributionRepository (enforce: bool) (workItems: string) (baseline: string) test =
        CliGolden.withRepository
            "ros-work-attribution-differential"
            "Work Attribution Differential"
            (fun root ->
                CliGolden.updateJson root "ros.json" (fun config ->
                    config.["telemetry"].["enabled"] <- JsonValue.Create false
                    config.["workProtocol"].["enforceAttribution"] <- JsonValue.Create enforce)

                CliHarness.write
                    root
                    CliGolden.contextPath
                    ($"""{{ "schemaVersion": "1.0.0", "repository": "work-attribution-differential", "workItems": {workItems}, "baselineDirtyPaths": {baseline} }}"""
                     + "\n"))
            true
            test

    let private attribution =
        [ { Name = "work attribution: work validate reports no findings when enforcement is disabled"
            Run =
              fun () ->
                  withAttributionRepository false "[]" "[]" (fun root ->
                      CliHarness.write root "unattributed.txt" "no enforcement\n"
                      validate root "validate" 0 validJson) }

          { Name = "work attribution: work validate reports no findings when nothing meaningful changed"
            Run =
              fun () ->
                  withAttributionRepository true "[]" "[]" (fun root ->
                      CliHarness.write root ".ros/events/housekeeping-ignored.json" "{}\n"
                      validate root "validate" 0 validJson) }

          { Name = "work attribution: work validate flags an unattributed meaningful change"
            Run =
              fun () ->
                  withAttributionRepository true "[]" "[]" (fun root ->
                      CliHarness.write root "src.txt" "meaningful, unattributed change\n"

                      validate
                          root
                          "validate"
                          1
                          """{ "valid": false, "findings": [ { "severity": "error", "path": "src.txt", "field": "work_items",
                               "message": "meaningful change has no active or completed work-item attribution" } ] }""") }

          { Name = "work attribution: work validate excuses a change already present in the baseline"
            Run =
              fun () ->
                  withAttributionRepository true "[]" """["src.txt"]""" (fun root ->
                      CliHarness.write root "src.txt" "already dirty at baseline capture\n"
                      validate root "validate" 0 validJson) }

          { Name = "work attribution: work validate treats an event-logged path as attributed"
            Run =
              fun () ->
                  withAttributionRepository true "[]" "[]" (fun root ->
                      CliHarness.write root "src.txt" "meaningful, attributed change\n"

                      File.AppendAllText(
                          Path.Combine(root, ".ros", "events", "events.jsonl"),
                          """{"type":"work.completed","paths":["src.txt"]}""" + "\n"
                      )

                      validate root "validate" 0 validJson) }

          { Name = "work attribution: work validate excuses every meaningful change while work is active"
            Run =
              fun () ->
                  let active =
                      """[ { "id": "TASK-ACTIVE", "type": "task", "state": "active", "semanticState": "active", "evidence": [], "telemetryExecutionIds": [] } ]"""

                  withAttributionRepository true active "[]" (fun root ->
                      CliHarness.write root "src.txt" "meaningful, excused by active work\n"
                      validate root "validate" 0 validJson) } ]

    /// Telemetry disabled and attribution not enforced; uncommitted, as the
    /// retired fixture was.
    let private withBacklogRepository test =
        CliGolden.withRepository
            "ros-backlog-validate-differential"
            "Backlog Validate Differential"
            (fun root ->
                CliGolden.updateJson root "ros.json" (fun config ->
                    config.["telemetry"].["enabled"] <- JsonValue.Create false
                    config.["workProtocol"].["enforceAttribution"] <- JsonValue.Create false))
            false
            test

    let private writeQueue root (items: string) (count: int) =
        CliHarness.write
            root
            CliGolden.queuePath
            ($"""{{ "schemaVersion": "1.0.0", "repository": "repository", "nextSeq": {count + 1}, "items": {items} }}"""
             + "\n")

    let private queueFinding (field: string) (message: string) =
        $"""{{ "valid": false, "findings": [ {{ "severity": "error", "path": ".ros/work/queue.json", "field": "{field}", "message": "{message}" }} ] }}"""

    let private backlog =
        [ { Name = "work backlog validate: reports no findings for a well-formed queue"
            Run =
              fun () ->
                  withBacklogRepository (fun root ->
                      writeQueue
                          root
                          """[ { "id": "WI-0001", "status": "ready", "priority": "high" }, { "id": "WI-0002", "status": "captured" } ]"""
                          2

                      validate root "backlog-validate" 0 validJson) }

          { Name = "work backlog validate: flags a duplicate id only on its second occurrence"
            Run =
              fun () ->
                  withBacklogRepository (fun root ->
                      writeQueue
                          root
                          """[ { "id": "WI-0001", "status": "ready" }, { "id": "WI-0001", "status": "blocked" } ]"""
                          2

                      validate root "backlog-validate" 1 (queueFinding "id" "duplicate backlog id 'WI-0001'")) }

          { Name = "work backlog validate: flags an invalid id"
            Run =
              fun () ->
                  withBacklogRepository (fun root ->
                      writeQueue root """[ { "id": "not-an-id", "status": "ready" } ]""" 1
                      validate root "backlog-validate" 1 (queueFinding "id" "invalid backlog id 'not-an-id'")) }

          { Name = "work backlog validate: flags an invalid status"
            Run =
              fun () ->
                  withBacklogRepository (fun root ->
                      writeQueue root """[ { "id": "WI-0001", "status": "in-progress" } ]""" 1
                      validate root "backlog-validate" 1 (queueFinding "status" "invalid status 'in-progress' for 'WI-0001'")) }

          { Name = "work backlog validate: flags an invalid priority"
            Run =
              fun () ->
                  withBacklogRepository (fun root ->
                      writeQueue root """[ { "id": "WI-0001", "status": "ready", "priority": "urgent" } ]""" 1
                      validate root "backlog-validate" 1 (queueFinding "priority" "invalid priority 'urgent' for 'WI-0001'")) }

          { Name = "work backlog validate: reports no findings for an absent queue file"
            Run =
              fun () ->
                  withBacklogRepository (fun root ->
                      File.Delete(Path.Combine(root, CliGolden.queuePath))
                      validate root "backlog-validate" 0 validJson) } ]

    let tests = attribution @ backlog
