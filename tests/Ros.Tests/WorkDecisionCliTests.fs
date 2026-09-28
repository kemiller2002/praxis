namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes

/// End-to-end golden masters for `work decide`, `work plan`, `work
/// context-plan`, `work backlog-decide` and `work backlog-promotion-plan`
/// through the real `ros-fs` CLI (ported from the retired
/// tests/work-fsharp-differential.test.mjs; goldens in Golden/work.json were
/// frozen from the former Node implementation).
[<RequireQualifiedAccess>]
module WorkDecisionCliTests =
    let private suite = "work"
    let private golden = CliGolden.golden suite
    let private states = [ "ready"; "active"; "blocked"; "complete" ]
    let private actions = [ "begin"; "block"; "resume"; "complete" ]

    /// A CLI invocation that needs no repository (pure decisions).
    let private standalone (arguments: string list) = CliHarness.run "dotnet" (CliHarness.cli :: arguments) []

    let private json (result: CliHarness.Run) = CliGolden.parse result.Out

    let private decision state action (reason: string option) (required: string list) (provided: string list) =
        standalone (
            [ "work"; "decide"; "--state"; state; "--action"; action ]
            @ (reason |> Option.map (fun value -> [ "--reason"; value ]) |> Option.defaultValue [])
            @ (required |> List.collect (fun kind -> [ "--required"; kind ]))
            @ (provided |> List.collect (fun kind -> [ "--provided"; kind ]))
        )

    /// The retired fixture: an installed repository (uncommitted, like the
    /// Node bootstrap fixture) with telemetry disabled and a context holding
    /// the given work items.
    let private withContext (workItems: string) (test: string -> unit) =
        CliGolden.withRepository
            "ros-work-differential"
            "Work Differential"
            (fun root ->
                CliGolden.disableTelemetry root

                CliHarness.write
                    root
                    CliGolden.contextPath
                    $"{{\n  \"schemaVersion\": \"1.0.0\",\n  \"repository\": \"work-differential\",\n  \"workItems\": {workItems}\n}}\n")
            false
            test

    let private item id kind state =
        $"{{ \"id\": \"{id}\", \"type\": \"{kind}\", \"state\": \"{state}\", \"semanticState\": \"{state}\", \"evidence\": [] }}"

    let private contextPlan root (action: string) (ids: string list) (extra: string list) =
        let snapshot = Path.Combine(Path.GetTempPath(), $"ros-work-context-{System.Guid.NewGuid():N}.json")
        File.WriteAllText(snapshot, CliHarness.read root CliGolden.contextPath)

        try
            CliHarness.ros
                root
                ([ "work"; "context-plan"; "--context"; snapshot; "--action"; action ]
                 @ [ "--repository"; "work-differential"; "--protocol-version"; "1.0.0"; "--actor"; "differential" ]
                 @ (ids |> List.collect (fun id -> [ "--id"; id ]))
                 @ extra)
        finally
            File.Delete snapshot

    let private optional (name: string) (node: JsonNode) =
        match node.[name] with
        | null -> null
        | value -> value.DeepClone()

    let private orEmpty (name: string) (node: JsonNode) =
        match node.[name] with
        | null -> JsonArray() :> JsonNode
        | value -> value.DeepClone()

    /// The retired suite's `normalizedItem`: the projection `work plan`
    /// reports for a production item.
    let private normalizedItem (item: JsonNode) : JsonNode =
        JsonObject(
            dict
                [ "id", item.["id"].DeepClone()
                  "type", item.["type"].DeepClone()
                  "state", item.["state"].DeepClone()
                  "semanticState", item.["semanticState"].DeepClone()
                  "evidence", item.["evidence"].DeepClone()
                  "blockReason", optional "blockReason" item
                  "updatedAt", optional "updatedAt" item
                  "completedAt", optional "completedAt" item
                  "telemetryExecutionIds", orEmpty "telemetryExecutionIds" item ]
        )

    let private normalizedEvent (event: JsonNode) : JsonNode =
        JsonObject(
            dict
                [ "type", event.["type"].DeepClone()
                  "workItem", event.["workItem"].DeepClone()
                  "repository", event.["repository"].DeepClone()
                  "protocolVersion", event.["protocolVersion"].DeepClone()
                  "occurredAt", event.["occurredAt"].DeepClone()
                  "reason", optional "reason" event
                  "evidence", orEmpty "evidence" event
                  "paths", orEmpty "paths" event
                  "telemetryExecutions", orEmpty "telemetryExecutions" event ]
        )

    let private mapped (projection: JsonNode -> JsonNode) (collection: JsonNode) =
        CliGolden.items collection |> List.map projection |> CliGolden.jsonList

    let private strings (collection: JsonNode) =
        CliGolden.items collection |> List.map (fun value -> value.GetValue<string>())

    let tests =
        [ { Name = "work decisions: live-work decision matrix matches the frozen transition guard"
            Run =
              fun () ->
                  let matrix = golden "test1Matrix"

                  for state in states do
                      for action in actions do
                          let expected = matrix.[$"{state}/{action}"]
                          let allowed = expected.["allowed"].GetValue<bool>()
                          let result = decision state action (Some "reason") [] []
                          Assert.equal ($"{state}/{action}", allowed) ($"{state}/{action}", result.Exit = 0)

                          if allowed then
                              let payload = json result
                              Assert.equal (expected.["targetState"].GetValue<string>()) (payload.["targetState"].GetValue<string>()) }

          { Name = "work decisions: completion evidence-type guard reports the missing evidence"
            Run =
              fun () ->
                  let result = decision "active" "complete" None [ "implementation"; "tests" ] [ "implementation" ]
                  CliGolden.expectExit 1 result

                  CliGolden.jsonEqual
                      (CliGolden.parse """{ "reason": "missing-evidence", "missingEvidence": ["tests"] }""")
                      (json result).["rejection"] }

          { Name = "work decisions: block-reason guard rejects an empty reason but accepts whitespace"
            Run =
              fun () ->
                  let expected = golden "test3"

                  for reason in [ ""; "  " ] do
                      let result = decision "active" "block" (Some reason) [] []
                      Assert.equal ($"'{reason}'", expected.[reason].GetValue<bool>()) ($"'{reason}'", result.Exit = 0) }

          { Name = "work decisions: work plans match the frozen item and event projections for every legal edge"
            Run =
              fun () ->
                  for case in CliGolden.items (golden "test4Cases") do
                      let state = case.["state"].GetValue<string>()
                      let action = case.["action"].GetValue<string>()
                      let item = case.["item"]
                      let event = case.["event"]

                      let result =
                          standalone (
                              [ "work"; "plan"; "--id"; item.["id"].GetValue<string>(); "--type"; item.["type"].GetValue<string>() ]
                              @ [ "--state"; state; "--action"; action ]
                              @ [ "--occurred-at"; event.["occurredAt"].GetValue<string>() ]
                              @ [ "--repository"; event.["repository"].GetValue<string>() ]
                              @ [ "--protocol-version"; event.["protocolVersion"].GetValue<string>() ]
                              @ (match event.["reason"] with
                                 | null -> []
                                 | reason -> [ "--reason"; reason.GetValue<string>() ])
                              @ (CliGolden.items (orEmpty "evidence" event)
                                 |> List.collect (fun evidence ->
                                     [ "--evidence"; $"""{evidence.["type"].GetValue<string>()}={evidence.["path"].GetValue<string>()}""" ]))
                              @ (strings (orEmpty "paths" event) |> List.collect (fun path -> [ "--path"; path ]))
                          )

                      CliGolden.expectExit 0 result
                      let plan = (json result).["plan"]
                      CliGolden.jsonEqual (normalizedItem item) plan.["item"]
                      CliGolden.jsonEqual (normalizedEvent event) plan.["event"]
                      CliGolden.jsonEqual (JsonArray()) plan.["telemetry"] }

          { Name = "work decisions: work plan rejection retains missing-evidence obligations"
            Run =
              fun () ->
                  let result =
                      standalone
                          [ "work"; "plan"; "--id"; "TASK-PLAN"; "--type"; "feature"; "--state"; "active"; "--action"; "complete"
                            "--occurred-at"; "2026-09-08T18:30:00Z"; "--required"; "tests" ]

                  CliGolden.expectExit 1 result

                  CliGolden.jsonEqual
                      (CliGolden.parse
                          """{ "schemaVersion": "1.0.0", "outcome": "rejected", "plan": null,
                               "rejection": { "reason": "missing-evidence", "missingEvidence": ["tests"] } }""")
                      (json result) }

          { Name = "work decisions: evidence verification accepts files, directories and absolute paths but not missing ones"
            Run =
              fun () ->
                  let outside = Path.Combine(Path.GetTempPath(), $"ros-work-evidence-{System.Guid.NewGuid():N}.txt")
                  File.WriteAllText(outside, "outside fixture\n")

                  try
                      let expected = golden "test6"

                      withContext
                          $"""[{item "TASK-MATRIX" "feature" "active"}]"""
                          (fun root ->
                              for key, evidencePath in
                                  [ "ros.json", "ros.json"; ".", "."; "outside", outside; "missing-evidence.txt", "missing-evidence.txt" ] do
                                  let result =
                                      CliHarness.ros
                                          root
                                          [ "work"; "plan"; "--verify-evidence"; "--id"; "TASK-MATRIX"; "--type"; "feature"
                                            "--state"; "active"; "--action"; "complete"; "--occurred-at"; "2026-09-08T18:30:00Z"
                                            "--required"; "implementation"; "--required"; "tests"
                                            "--evidence"; $"implementation={evidencePath}"; "--evidence"; $"tests={evidencePath}" ]

                                  let allowed = expected.[key].GetValue<bool>()
                                  Assert.equal (key, allowed) (key, result.Exit = 0)
                                  let payload = json result

                                  if allowed then
                                      Assert.equal "planned" (payload.["outcome"].GetValue<string>())
                                  else
                                      Assert.equal "evidence-rejected" (payload.["outcome"].GetValue<string>())

                                      Assert.equal
                                          [ "missing"; "missing" ]
                                          (CliGolden.items payload.["rejection"].["evidenceIssues"]
                                           |> List.map (fun issue -> issue.["outcome"].GetValue<string>())))
                  finally
                      File.Delete outside }

          { Name = "work decisions: context plan matches the frozen multi-item begin order and metadata"
            Run =
              fun () ->
                  let production = golden "test7Production"
                  let observed = strings (golden "test7ObservedGitPaths")
                  let outcome = golden "test7ObservationOutcome"
                  Assert.isTrue (outcome.GetValue<string>() <> "unavailable") "observation must be available"
                  let firstEvent = CliGolden.items production.["events"] |> List.head
                  let occurredAt = firstEvent.["occurredAt"].GetValue<string>()

                  withContext
                      $"""[{item "TASK-EXIST" "task" "ready"}]"""
                      (fun root ->
                          let result =
                              contextPlan
                                  root
                                  "begin"
                                  [ "TASK-NEW"; "TASK-EXIST" ]
                                  ([ "--occurred-at"; occurredAt; "--type"; "task" ]
                                   @ (observed |> List.collect (fun path -> [ "--observed-git-path"; path ])))

                          CliGolden.expectExit 0 result
                          let plan = (json result).["plan"]
                          let context = production.["context"]
                          CliGolden.jsonEqual (mapped normalizedItem context.["workItems"]) plan.["workItems"]
                          CliGolden.jsonEqual (mapped normalizedEvent production.["events"]) plan.["events"]

                          for field in [ "repository"; "protocolVersion"; "actor"; "startedAt"; "updatedAt"; "baselineDirtyPaths" ] do
                              CliGolden.jsonEqual context.[field] plan.[field]) }

          { Name = "work decisions: context plan rejects a later illegal item without planning any write"
            Run =
              fun () ->
                  withContext
                      $"""[{item "TASK-READY" "task" "ready"}, {{ "id": "TASK-DONE", "type": "task", "state": "complete", "semanticState": "complete", "evidence": [], "completedAt": "earlier" }}]"""
                      (fun root ->
                          let before = CliHarness.read root CliGolden.contextPath

                          let result =
                              contextPlan root "begin" [ "TASK-READY"; "TASK-DONE" ] [ "--occurred-at"; "2026-09-08T23:45:00Z"; "--type"; "task" ]

                          CliGolden.expectExit 1 result

                          CliGolden.jsonEqual
                              (CliGolden.parse
                                  """{ "schemaVersion": "1.0.0", "outcome": "rejected", "plan": null,
                                       "rejection": { "reason": "item-transition-rejected", "workItem": "TASK-DONE",
                                         "transition": { "reason": "illegal-transition", "state": "complete", "action": "begin", "missingEvidence": [] } } }""")
                              (json result)

                          Assert.equal before (CliHarness.read root CliGolden.contextPath)) }

          { Name = "work decisions: backlog decision matrix matches the frozen triage rules and keeps start as promotion"
            Run =
              fun () ->
                  let matrix = golden "test9Matrix"

                  let projected (change: JsonNode) (current: JsonNode) =
                      match change.["kind"].GetValue<string>() with
                      | "keep" -> CliGolden.clone current
                      | "clear" -> null
                      | "set" -> CliGolden.clone change.["value"]
                      | other -> failwith $"unknown backlog field change '{other}'"

                  for state in [ "captured"; "ready"; "blocked"; "abandoned" ] do
                      for action in [ "ready"; "block"; "abandon"; "start" ] do
                          let expected = matrix.[$"{state}/{action}"]
                          let allowed = expected.["allowed"].GetValue<bool>()

                          let result =
                              standalone [ "work"; "backlog-decide"; "--state"; state; "--action"; action; "--reason"; "reason" ]

                          Assert.equal ($"{state}/{action}", allowed) ($"{state}/{action}", result.Exit = 0)

                          if allowed then
                              let effect = (json result).["effect"]

                              if action = "start" then
                                  Assert.equal "promote-to-live-work" (effect.["kind"].GetValue<string>())
                                  Assert.equal "ready" (expected.["afterStatus"].GetValue<string>())
                              else
                                  Assert.equal "change-state" (effect.["kind"].GetValue<string>())
                                  CliGolden.jsonEqual expected.["afterStatus"] effect.["state"]

                                  CliGolden.jsonEqual
                                      expected.["afterBlockedReason"]
                                      (projected effect.["blockedReason"] expected.["beforeBlockedReason"])

                                  CliGolden.jsonEqual
                                      expected.["afterAbandonedReason"]
                                      (projected effect.["abandonedReason"] expected.["beforeAbandonedReason"]) }

          { Name = "work decisions: backlog promotion preflight rejects a captured item in a batch and allows a direct id"
            Run =
              fun () ->
                  let rejected =
                      standalone
                          [ "work"; "backlog-promotion-plan"; "--id"; "WI-READY"; "--id"; "WI-CAPTURED"; "--type"; "feature"
                            "--queue-state"; "WI-READY=ready"; "--queue-state"; "WI-CAPTURED=captured" ]

                  CliGolden.expectExit 1 rejected

                  CliGolden.jsonEqual
                      (CliGolden.parse """{ "reason": "backlog-item-not-ready", "workItem": "WI-CAPTURED", "state": "captured" }""")
                      (json rejected).["rejection"]

                  let planned = standalone [ "work"; "backlog-promotion-plan"; "--id"; "EXT-DIRECT"; "--type"; "feature" ]
                  CliGolden.expectExit 0 planned

                  CliGolden.jsonEqual
                      (CliGolden.parse """{ "workItems": ["EXT-DIRECT"], "workType": "feature" }""")
                      (json planned).["plan"] }

          { Name = "work decisions: verified context plan matches the frozen multi-item evidence-path outcomes"
            Run =
              fun () ->
                  let expected = golden "test11"

                  withContext
                      $"""[{item "TASK-ONE" "mechanical" "active"}, {item "TASK-TWO" "mechanical" "active"}]"""
                      (fun root ->
                          for evidencePath in [ "ros.json"; "missing-context-evidence.txt" ] do
                              let expectation = expected.[evidencePath]
                              let allowed = expectation.["allowed"].GetValue<bool>()

                              let result =
                                  contextPlan
                                      root
                                      "complete"
                                      [ "TASK-ONE"; "TASK-TWO" ]
                                      [ "--occurred-at"; "2026-09-09T01:00:00Z"; "--type"; "task"
                                        "--evidence"; $"note={evidencePath}"; "--verify-evidence" ]

                              Assert.equal (evidencePath, allowed) (evidencePath, result.Exit = 0)

                              let payload = json result

                              Assert.equal
                                  (if allowed then "planned" else "evidence-rejected")
                                  (payload.["outcome"].GetValue<string>())) } ]
