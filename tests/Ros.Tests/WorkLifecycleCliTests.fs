namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes

/// The live work lifecycle through the real CLI -- `work start`, `work
/// resume`, `work block` and `work complete` -- against committed
/// repositories installed by the real `ros init`, compared with the goldens
/// frozen from the former Node implementation after removing volatile
/// (clock, commit, execution-id) fields (ported from the retired
/// tests/work-start-, work-resume-, work-block- and
/// work-complete-fsharp-differential.test.mjs).
[<RequireQualifiedAccess>]
module WorkLifecycleCliTests =
    let private work root (arguments: string list) = CliHarness.ros root ("work" :: arguments)

    let private withRepository prefix project test =
        CliGolden.withRepository prefix project CliGolden.noPreparation true test

    let private baseVolatile =
        [ "executionId"; "startedAt"; "discoveredAt"; "lastAssessedAt"; "recordedAt"; "collectedAt"; "measurementId"
          "commit"; "branch"; "dirtyPaths"; "dirty"; "commits"; "occurredAt"; "eventId"; "updatedAt"
          "telemetryExecutionIds"; "telemetryExecutions"
          // The installation work item's own completedAt is set by `praxis init`.
          "completedAt" ]

    let private stripped (extra: string list) (node: JsonNode) =
        CliGolden.without (Set.ofList (baseVolatile @ extra)) node

    let private executionCount (item: JsonNode) = (CliGolden.items item.["telemetryExecutionIds"]).Length

    let private expectFailure (fragment: string) (result: CliHarness.Run) =
        CliGolden.expectExit 1 result
        CliGolden.contains fragment result.Err

    // ------------------------------------------------------------- work start

    let private startGolden = CliGolden.golden "work-start"

    let private startQueue =
        """{
  "schemaVersion": "1.0.0",
  "repository": "repository",
  "nextSeq": 4,
  "items": [
    { "id": "WI-READY", "title": "Ready item", "description": null, "tags": [], "priority": "medium", "status": "ready", "attachments": [], "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z" },
    { "id": "WI-CAPTURED", "title": "Captured item", "description": null, "tags": [], "priority": "medium", "status": "captured", "attachments": [], "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z" },
    { "id": "WI-ABANDONED", "title": "Abandoned item", "description": null, "tags": [], "priority": "medium", "status": "abandoned", "attachments": [], "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z" }
  ]
}
"""

    let private withStartRepository test =
        withRepository "ros-work-start" "Work Start Differential" test

    let private start root (arguments: string list) = work root ("start" :: arguments)

    let private workStart =
        [ { Name = "work start: a brand-new work item begins with context, events and a fresh telemetry execution matching the goldens"
            Run =
              fun () ->
                  withStartRepository (fun root ->
                      start root [ "--id"; "WI-NEW"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ]
                      |> CliGolden.expectExit 0

                      let context = CliGolden.context root
                      CliGolden.jsonEqual (startGolden "test1Context") (stripped [] context)
                      Assert.equal 1 (executionCount (CliGolden.find context.["workItems"] "WI-NEW"))

                      let events = CliGolden.events root
                      CliGolden.jsonEqual (startGolden "test1Events") (stripped [] (CliGolden.jsonList events))

                      let started = events |> List.find (fun event -> CliGolden.text event "workItem" = Some "WI-NEW")
                      Assert.equal 1 (CliGolden.items started.["telemetryExecutions"]).Length

                      let executions = CliGolden.executions root
                      Assert.equal 1 executions.Length
                      let execution = List.head executions
                      CliGolden.jsonEqual (startGolden "test1Execution0") (stripped [] execution)

                      Assert.equal
                          ((startGolden "test1CapabilitiesLength").GetValue<int>())
                          (CliGolden.items execution.["capabilities"]).Length

                      let baseline = CliGolden.capability execution "git.baseline_dirty_files"
                      Assert.equal "derived" (baseline.["status"].GetValue<string>())
                      Assert.equal 1 (CliGolden.items baseline.["history"]).Length
                      Assert.isTrue (not (baseline.AsObject().ContainsKey "historyOmitted")) "historyOmitted must be absent") }

          { Name = "work start: a ready backlog item is promoted without touching its own backlog record"
            Run =
              fun () ->
                  withStartRepository (fun root ->
                      CliHarness.write root CliGolden.queuePath startQueue

                      start root [ "--id"; "WI-READY"; "--occurred-at"; "2026-09-09T18:00:00.000Z" ]
                      |> CliGolden.expectExit 0

                      CliGolden.jsonEqual (startGolden "test2Context") (stripped [] (CliGolden.context root))
                      CliGolden.jsonEqual (CliGolden.parse startQueue) (CliGolden.queue root)) }

          { Name = "work start: a captured backlog item is rejected with the exact message, leaving context untouched"
            Run =
              fun () ->
                  withStartRepository (fun root ->
                      CliHarness.write root CliGolden.queuePath startQueue
                      let before = CliGolden.context root

                      start root [ "--id"; "WI-CAPTURED"; "--occurred-at"; "2026-09-09T18:00:00.000Z" ]
                      |> expectFailure "cannot start backlog item 'WI-CAPTURED' from 'captured'; mark it ready first"

                      CliGolden.jsonEqual before (CliGolden.context root)) }

          { Name = "work start: an abandoned backlog item is rejected with the exact message"
            Run =
              fun () ->
                  withStartRepository (fun root ->
                      CliHarness.write root CliGolden.queuePath startQueue

                      start root [ "--id"; "WI-ABANDONED"; "--occurred-at"; "2026-09-09T18:00:00.000Z" ]
                      |> expectFailure "cannot start backlog item 'WI-ABANDONED': it was abandoned") }

          { Name = "work start: starting an already-active item is rejected with the exact illegal-transition message"
            Run =
              fun () ->
                  withStartRepository (fun root ->
                      start root [ "--id"; "WI-ACTIVE"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ] |> ignore

                      start root [ "--id"; "WI-ACTIVE"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ]
                      |> expectFailure "cannot begin 'WI-ACTIVE' from 'active'") } ]

    // ------------------------------------------------------------ work resume

    let private resumeGolden = CliGolden.golden "work-resume"

    let private withResumeRepository test =
        withRepository "ros-work-resume" "Work Resume Differential" test

    let private workResume =
        [ { Name = "work resume: a blocked item with a linked active execution resumes without a new execution"
            Run =
              fun () ->
                  withResumeRepository (fun root ->
                      work root [ "start"; "--id"; "WI-BLOCKED"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ]
                      |> ignore

                      // Driven to "blocked" directly so `work resume` is exercised
                      // in isolation (as the retired suite did).
                      CliGolden.updateContextItem root "WI-BLOCKED" (fun item ->
                          item.["state"] <- JsonValue.Create "blocked"
                          item.["semanticState"] <- JsonValue.Create "blocked"
                          item.["blockReason"] <- JsonValue.Create "waiting on review")

                      work root [ "resume"; "--id"; "WI-BLOCKED"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ]
                      |> CliGolden.expectExit 0

                      let context = CliGolden.context root
                      CliGolden.jsonEqual (resumeGolden "test1Context") (stripped [] context)
                      let item = CliGolden.find context.["workItems"] "WI-BLOCKED"
                      Assert.equal (Some "active") (CliGolden.text item "semanticState")
                      Assert.equal 1 (executionCount item)

                      let lifecycleEvents =
                          CliGolden.events root
                          |> List.filter (fun event ->
                              match CliGolden.text event "type" with
                              | Some "work.started"
                              | Some "work.resumed" -> true
                              | _ -> false)

                      CliGolden.jsonEqual (resumeGolden "test1Events") (stripped [] (CliGolden.jsonList lifecycleEvents))
                      Assert.equal 1 (CliGolden.executions root).Length) }

          { Name = "work resume: an item with no linked active execution gets a new execution"
            Run =
              fun () ->
                  withResumeRepository (fun root ->
                      // Blocked straight from "ready" (never begun) is not reachable
                      // through a command, so the fixture seeds it.
                      CliGolden.updateJson root CliGolden.contextPath (fun context ->
                          context.["workItems"].AsArray().Add(
                              CliGolden.parse
                                  """{ "id": "WI-NEVER-BEGUN", "type": "task", "state": "blocked", "semanticState": "blocked", "evidence": [],
                                       "blockReason": "waiting on a dependency", "updatedAt": "2026-01-01T00:00:00.000Z" }"""
                          )

                          if isNull context.["startedAt"] then
                              context.["startedAt"] <- JsonValue.Create "2026-01-01T00:00:00.000Z")

                      work root [ "resume"; "--id"; "WI-NEVER-BEGUN"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ]
                      |> CliGolden.expectExit 0

                      let item = CliGolden.contextItem root "WI-NEVER-BEGUN"
                      Assert.equal (Some "active") (CliGolden.text item "semanticState")
                      Assert.equal 1 (executionCount item)
                      let executions = CliGolden.executions root
                      Assert.equal 1 executions.Length
                      Assert.equal (Some "WI-NEVER-BEGUN") (CliGolden.text (List.head executions) "workItemId")) }

          { Name = "work resume: an id not in the live context is rejected with the exact message"
            Run =
              fun () ->
                  withResumeRepository (fun root ->
                      work root [ "resume"; "--id"; "WI-GHOST"; "--occurred-at"; "2026-09-09T18:00:00.000Z" ]
                      |> expectFailure "work item 'WI-GHOST' is not in repository context") }

          { Name = "work resume: resuming an already-active item is rejected with the exact illegal-transition message"
            Run =
              fun () ->
                  withResumeRepository (fun root ->
                      work root [ "start"; "--id"; "WI-ACTIVE"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ]
                      |> ignore

                      work root [ "resume"; "--id"; "WI-ACTIVE"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ]
                      |> expectFailure "cannot resume 'WI-ACTIVE' from 'active'") } ]

    // ------------------------------------------------------------- work block

    let private blockGolden = CliGolden.golden "work-block"

    let private withBlockRepository test =
        withRepository "ros-work-block" "Work Block Differential" test

    let private captureReady root id title =
        work root [ "capture"; "--id"; id; "--title"; title; "--occurred-at"; "2026-09-09T18:00:00.000Z" ] |> ignore

        work root [ "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; "2026-09-09T18:01:00.000Z" ]
        |> ignore

    let private blockStripped = stripped [ "createdAt" ]

    let private workBlock =
        [ { Name = "work block: a not-yet-started backlog item is blocked in the queue"
            Run =
              fun () ->
                  withBlockRepository (fun root ->
                      captureReady root "WI-BACKLOG" "Backlog-only item"

                      let result =
                          work root [ "block"; "--id"; "WI-BACKLOG"; "--reason"; "waiting on design"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ]

                      CliGolden.expectExit 0 result
                      let queue = CliGolden.queue root
                      CliGolden.jsonEqual (blockGolden "test1Queue") (blockStripped queue)
                      let item = CliGolden.find queue.["items"] "WI-BACKLOG"
                      Assert.equal (Some "blocked") (CliGolden.text item "status")
                      Assert.equal (Some "waiting on design") (CliGolden.text item "blockedReason")

                      // The printed output is the raw mixed array: for a
                      // backlog-only id, the full raw queue item.
                      let output = CliGolden.items (CliGolden.parse result.Out)
                      Assert.equal 1 output.Length
                      Assert.equal (Some "WI-BACKLOG") (CliGolden.text (List.head output) "id")
                      Assert.equal (Some "blocked") (CliGolden.text (List.head output) "status")) }

          { Name = "work block: an already-live work item is blocked in the live context"
            Run =
              fun () ->
                  withBlockRepository (fun root ->
                      work root [ "start"; "--id"; "WI-LIVE"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ] |> ignore

                      let result =
                          work root [ "block"; "--id"; "WI-LIVE"; "--reason"; "blocked on review"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ]

                      CliGolden.expectExit 0 result
                      let context = CliGolden.context root
                      CliGolden.jsonEqual (blockGolden "test2Context") (blockStripped context)
                      let item = CliGolden.find context.["workItems"] "WI-LIVE"
                      Assert.equal (Some "blocked") (CliGolden.text item "semanticState")
                      Assert.equal (Some "blocked on review") (CliGolden.text item "blockReason")
                      let output = CliGolden.items (CliGolden.parse result.Out)
                      Assert.equal 1 output.Length
                      Assert.equal (Some "WI-LIVE") (CliGolden.text (List.head output) "id")
                      Assert.equal (Some "blocked") (CliGolden.text (List.head output) "semanticState")) }

          { Name = "work block: one call splits ids between the backlog and the live context"
            Run =
              fun () ->
                  withBlockRepository (fun root ->
                      captureReady root "WI-SPLIT-BACKLOG" "Backlog item"

                      work root [ "start"; "--id"; "WI-SPLIT-LIVE"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ]
                      |> ignore

                      let result =
                          work
                              root
                              [ "block"; "--id"; "WI-SPLIT-BACKLOG"; "--id"; "WI-SPLIT-LIVE"; "--reason"; "batch block"
                                "--occurred-at"; "2026-09-09T18:05:00.000Z" ]

                      CliGolden.expectExit 0 result
                      CliGolden.jsonEqual (blockGolden "test3Queue") (blockStripped (CliGolden.queue root))
                      CliGolden.jsonEqual (blockGolden "test3Context") (blockStripped (CliGolden.context root))
                      let output = CliGolden.parse result.Out
                      Assert.equal 2 (CliGolden.items output).Length
                      Assert.equal (Some "blocked") (CliGolden.text (CliGolden.find output "WI-SPLIT-BACKLOG") "status")
                      Assert.equal (Some "blocked") (CliGolden.text (CliGolden.find output "WI-SPLIT-LIVE") "semanticState")) }

          { Name = "work block: --reason is required once the item is eligible, with the exact message"
            Run =
              fun () ->
                  withBlockRepository (fun root ->
                      captureReady root "WI-REASON" "Item"

                      work root [ "block"; "--id"; "WI-REASON"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ]
                      |> expectFailure "block requires --reason"

                      Assert.equal (Some "ready") (CliGolden.text (CliGolden.find (CliGolden.queue root).["items"] "WI-REASON") "status")) }

          { Name = "work block: an id in neither the queue nor the live context is rejected with the exact message"
            Run =
              fun () ->
                  withBlockRepository (fun root ->
                      work root [ "block"; "--id"; "WI-GHOST"; "--reason"; "n/a"; "--occurred-at"; "2026-09-09T18:00:00.000Z" ]
                      |> expectFailure "work item 'WI-GHOST' is not in repository context") } ]

    // ---------------------------------------------------------- work complete

    let private withCompleteRepository test =
        withRepository "ros-work-complete" "Work Complete Differential" test

    let private gitChangeMetrics =
        [ "git.commits_created"; "git.files_added"; "git.files_modified"; "git.files_deleted"; "git.files_renamed"
          "git.binary_files_changed"; "git.lines_added"; "git.lines_deleted"; "tests.added"; "tests.modified"
          "tests.removed"; "documentation.files_changed" ]

    let private completeGolden =
        CliGolden.parse
            """{
  "test1Context": {
    "schemaVersion": "1.0.0", "protocolVersion": "1.0.0", "repository": "work-complete-differential", "actor": "ros-bootstrap", "baselineDirtyPaths": [],
    "workItems": [
      { "id": "{install}", "type": "mechanical", "state": "complete", "semanticState": "complete", "evidence": [{ "type": "installation", "path": ".ros/installation.json" }] },
      { "id": "WI-DONE", "type": "task", "state": "complete", "semanticState": "complete",
        "evidence": [{ "type": "implementation", "path": "IMPLEMENTATION-NOTES.md" }, { "type": "tests", "path": "TESTS-NOTES.md" }] }
    ]
  },
  "test1Metrics": {
    "git.commits_created": 0, "git.files_added": 2, "git.files_modified": 1, "git.files_deleted": 0, "git.files_renamed": 0,
    "git.binary_files_changed": 0, "git.lines_added": 3, "git.lines_deleted": 0, "tests.added": 0, "tests.modified": 0,
    "tests.removed": 0, "documentation.files_changed": 3
  }
}"""

    let private completeStripped = stripped [ "createdAt"; "finalizedAt"; "startCommit"; "endCommit" ]

    let private notesEvidence =
        [ "--evidence"; "implementation=IMPLEMENTATION-NOTES.md"; "--evidence"; "tests=TESTS-NOTES.md" ]

    let private metric record id =
        CliGolden.metricValue record id |> Option.map CliGolden.clone

    let private workComplete =
        [ { Name = "work complete: completion records evidence and a clean-baseline change summary matching the goldens"
            Run =
              fun () ->
                  withCompleteRepository (fun root ->
                      work root [ "start"; "--id"; "WI-DONE"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ] |> ignore
                      CliHarness.write root "IMPLEMENTATION-NOTES.md" "Implemented the feature.\n"
                      CliHarness.write root "TESTS-NOTES.md" "Covered by new tests.\n"
                      File.AppendAllText(Path.Combine(root, "README.md"), "Additional context line.\n")

                      work root ([ "complete"; "--id"; "WI-DONE"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ] @ notesEvidence)
                      |> CliGolden.expectExit 0

                      let context = CliGolden.context root
                      let expectedContext = CliGolden.parse (completeGolden.["test1Context"].ToJsonString().Replace("{install}", CliGolden.installWorkItemId ()))
                      CliGolden.jsonEqual expectedContext (completeStripped context)
                      let item = CliGolden.find context.["workItems"] "WI-DONE"
                      Assert.equal (Some "complete") (CliGolden.text item "semanticState")
                      CliGolden.jsonEqual (CliGolden.find expectedContext.["workItems"] "WI-DONE").["evidence"] item.["evidence"]
                      Assert.isTrue (CliGolden.text item "completedAt" |> Option.exists (fun value -> value <> "")) "completedAt must be set"
                      Assert.equal 1 (executionCount item)

                      let execution = CliGolden.executions root |> List.head
                      Assert.equal (Some "finalized") (CliGolden.text execution "status")
                      let summary = execution.["repository"].["changeSummary"]
                      Assert.equal true (summary.["available"].GetValue<bool>())
                      Assert.equal (Some "git-diff-from-clean-execution-baseline") (CliGolden.text summary "mechanism")

                      CliGolden.jsonEqual
                          (CliGolden.parse """{ "added": 2, "modified": 1, "deleted": 0, "renamed": 0 }""")
                          summary.["counts"]

                      Assert.equal 0 (summary.["commits"].GetValue<int>())
                      CliGolden.jsonEqual (CliGolden.parse """{ "added": 0, "modified": 0, "removed": 0 }""") summary.["tests"]
                      Assert.equal 3 (summary.["documentationFilesChanged"].GetValue<int>())
                      Assert.equal 3 (summary.["linesAdded"].GetValue<int>())
                      Assert.equal 0 (summary.["linesDeleted"].GetValue<int>())
                      Assert.equal 0 (summary.["binaryFiles"].GetValue<int>())

                      for id in gitChangeMetrics do
                          match metric execution id with
                          | Some value -> CliGolden.jsonEqual completeGolden.["test1Metrics"].[id] value
                          | None -> failwith $"metric {id} must be recorded"

                      let wall = metric execution "time.wall_ms" |> Option.map (fun value -> value.GetValue<double>())
                      Assert.isTrue (wall |> Option.exists (fun value -> value >= 0.0)) "time.wall_ms must be a non-negative number"
                      CliGolden.jsonEqual (JsonValue.Create 0) (metric execution "time.blocked_ms" |> Option.toObj)) }

          { Name = "work complete: a dirty worktree at start makes the change summary unavailable"
            Run =
              fun () ->
                  withCompleteRepository (fun root ->
                      File.AppendAllText(Path.Combine(root, "README.md"), "Uncommitted change before start.\n")
                      work root [ "start"; "--id"; "WI-DIRTY"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ] |> ignore
                      CliHarness.write root "IMPLEMENTATION-NOTES.md" "Implemented anyway.\n"
                      CliHarness.write root "TESTS-NOTES.md" "Tested anyway.\n"

                      work root ([ "complete"; "--id"; "WI-DIRTY"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ] @ notesEvidence)
                      |> CliGolden.expectExit 0

                      let execution = CliGolden.executions root |> List.head
                      Assert.equal (Some "finalized") (CliGolden.text execution "status")

                      CliGolden.jsonEqual
                          (CliGolden.parse """{ "available": false, "reason": "preexisting-dirty-worktree" }""")
                          execution.["repository"].["changeSummary"]

                      for id in gitChangeMetrics do
                          let capability = CliGolden.capability execution id
                          Assert.equal (Some "supported-unavailable") (CliGolden.text capability "status")
                          Assert.equal (Some "execution attribution unavailable: preexisting-dirty-worktree") (CliGolden.text capability "reason")
                          Assert.isTrue (metric execution id |> Option.isNone) $"{id} must not be recorded as a measurement") }

          { Name = "work complete: missing required evidence types are rejected with the exact message before any effect"
            Run =
              fun () ->
                  withCompleteRepository (fun root ->
                      work root [ "start"; "--id"; "WI-NO-EVIDENCE"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ]
                      |> ignore

                      work root [ "complete"; "--id"; "WI-NO-EVIDENCE"; "--occurred-at"; "2026-09-09T18:05:00.000Z" ]
                      |> expectFailure "completion evidence missing for 'WI-NO-EVIDENCE': implementation, tests"

                      Assert.equal (Some "active") (CliGolden.text (CliGolden.contextItem root "WI-NO-EVIDENCE") "semanticState")) }

          { Name = "work complete: a nonexistent evidence path is rejected with the exact message"
            Run =
              fun () ->
                  withCompleteRepository (fun root ->
                      work root [ "start"; "--id"; "WI-GHOST-EVIDENCE"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ]
                      |> ignore

                      work
                          root
                          [ "complete"; "--id"; "WI-GHOST-EVIDENCE"; "--occurred-at"; "2026-09-09T18:05:00.000Z"
                            "--evidence"; "implementation=does-not-exist-impl.md"; "--evidence"; "tests=does-not-exist-tests.md" ]
                      |> expectFailure "evidence path does not exist: does-not-exist-impl.md") }

          { Name = "work complete: a research item's conclusion defaults to inconclusive and honors --conclusion"
            Run =
              fun () ->
                  withCompleteRepository (fun root ->
                      work root [ "start"; "--id"; "WI-RESEARCH-DEFAULT"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "research" ]
                      |> ignore

                      work root [ "start"; "--id"; "WI-RESEARCH-EXPLICIT"; "--occurred-at"; "2026-09-09T18:00:01.000Z"; "--type"; "research" ]
                      |> ignore

                      CliHarness.write root "RESEARCH-RECORD.md" "n/a\n"

                      work
                          root
                          [ "complete"; "--id"; "WI-RESEARCH-DEFAULT"; "--occurred-at"; "2026-09-09T18:05:00.000Z"
                            "--evidence"; "research-record=RESEARCH-RECORD.md" ]
                      |> ignore

                      work
                          root
                          [ "complete"; "--id"; "WI-RESEARCH-EXPLICIT"; "--occurred-at"; "2026-09-09T18:06:00.000Z"
                            "--evidence"; "research-record=RESEARCH-RECORD.md"; "--conclusion"; "confirmed: caching reduces latency" ]
                      |> ignore

                      Assert.equal (Some "inconclusive") (CliGolden.text (CliGolden.contextItem root "WI-RESEARCH-DEFAULT") "conclusion")

                      Assert.equal
                          (Some "confirmed: caching reduces latency")
                          (CliGolden.text (CliGolden.contextItem root "WI-RESEARCH-EXPLICIT") "conclusion")) }

          { Name = "work complete: a promoted backlog item's own queue.json status becomes complete"
            Run =
              fun () ->
                  withCompleteRepository (fun root ->
                      work root [ "capture"; "--id"; "WI-PROMOTED"; "--title"; "Promoted item"; "--occurred-at"; "2026-09-09T18:00:00.000Z" ]
                      |> ignore

                      work root [ "backlog-transition"; "--id"; "WI-PROMOTED"; "--action"; "ready"; "--occurred-at"; "2026-09-09T18:01:00.000Z" ]
                      |> ignore

                      work root [ "start"; "--id"; "WI-PROMOTED"; "--occurred-at"; "2026-09-09T18:02:00.000Z"; "--type"; "task" ]
                      |> ignore

                      let status () = CliGolden.text (CliGolden.find (CliGolden.queue root).["items"] "WI-PROMOTED") "status"
                      Assert.equal (Some "ready") (status ())

                      work
                          root
                          [ "complete"; "--id"; "WI-PROMOTED"; "--occurred-at"; "2026-09-09T18:05:00.000Z"
                            "--evidence"; "implementation=README.md"; "--evidence"; "tests=README.md" ]
                      |> CliGolden.expectExit 0

                      Assert.equal (Some "complete") (status ())
                      CliGolden.contains "| WI-PROMOTED | Promoted item | complete |" (CliHarness.read root CliGolden.queueMarkdownPath)) }

          { Name = "work complete: completing a live-only id leaves queue.json untouched"
            Run =
              fun () ->
                  withCompleteRepository (fun root ->
                      let before = (CliGolden.queue root).["items"]

                      work root [ "start"; "--id"; "WI-LIVE-ONLY"; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--type"; "task" ]
                      |> ignore

                      work
                          root
                          [ "complete"; "--id"; "WI-LIVE-ONLY"; "--occurred-at"; "2026-09-09T18:05:00.000Z"
                            "--evidence"; "implementation=README.md"; "--evidence"; "tests=README.md" ]
                      |> CliGolden.expectExit 0

                      CliGolden.jsonEqual before (CliGolden.queue root).["items"]) } ]

    let tests = workStart @ workResume @ workBlock @ workComplete
