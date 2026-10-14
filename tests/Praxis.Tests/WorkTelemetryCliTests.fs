namespace Praxis.Tests

open System.IO
open System.Text.Json.Nodes

/// End-to-end coverage of how the real `work` transitions link, recover,
/// finalize and annotate telemetry executions (ported from the retired
/// tests/work-telemetry-fsharp-differential, work-telemetry-lifecycle-
/// fsharp-differential and work-resume-parent-execution-fsharp-differential
/// .test.mjs suites).
[<RequireQualifiedAccess>]
module WorkTelemetryCliTests =
    let private work root (arguments: string list) = CliHarness.ros root ("work" :: arguments)

    let private workOk root arguments = work root arguments |> CliGolden.expectExit 0

    let private withRepository prefix project test =
        CliGolden.withRepository prefix project CliGolden.noPreparation true test

    let private executionIds (item: JsonNode) =
        CliGolden.items item.["telemetryExecutionIds"] |> List.map (fun id -> id.GetValue<string>())

    let private recordsFor root (workItemId: string) =
        CliGolden.executions root
        |> List.filter (fun record -> CliGolden.text record "workItemId" = Some workItemId)
        |> List.sortWith (fun left right ->
            System.String.CompareOrdinal(left.["executionId"].GetValue<string>(), right.["executionId"].GetValue<string>()))

    let private executionId (record: JsonNode) = record.["executionId"].GetValue<string>()

    let private setExecutionIds root id (ids: string list) =
        CliGolden.updateContextItem root id (fun item ->
            item.["telemetryExecutionIds"] <- CliGolden.jsonList (ids |> List.map (fun value -> JsonValue.Create value :> JsonNode)))

    /// `work plan --resolve-telemetry` against the execution records the real
    /// transitions wrote under ROOT (no synthetic --candidate flags).
    let private resolve root state action (linked: string list) =
        work
            root
            ([ "plan"; "--resolve-telemetry"; "--id"; "TASK-TELEMETRY"; "--type"; "task"; "--state"; state; "--action"; action ]
             @ [ "--occurred-at"; "2026-09-09T00:00:00Z"; "--telemetry-enabled" ]
             @ (linked |> List.collect (fun id -> [ "--telemetry-id"; id ]))
             @ (if action = "block" then [ "--reason"; "reason" ] else []))

    let private completionEvidence root =
        let evidence = Path.Combine(root, "ros.json")
        [ "--evidence"; $"implementation={evidence}"; "--evidence"; $"tests={evidence}" ]

    let private resolution =
        [ { Name = "work telemetry: resolution recovers the single orphaned active execution that begin adopts"
            Run =
              fun () ->
                  withRepository "ros-telemetry-resolution" "Telemetry Resolution Differential" (fun root ->
                      workOk root [ "begin"; "--id"; "TASK-TELEMETRY"; "--occurred-at"; CliGolden.at () ]
                      let original = recordsFor root "TASK-TELEMETRY" |> List.head

                      CliGolden.updateContextItem root "TASK-TELEMETRY" (fun item ->
                          item.["state"] <- JsonValue.Create "ready"
                          item.["semanticState"] <- JsonValue.Create "ready"
                          item.["telemetryExecutionIds"] <- JsonArray())

                      workOk root [ "begin"; "--id"; "TASK-TELEMETRY"; "--occurred-at"; CliGolden.at () ]
                      let committed = CliGolden.contextItem root "TASK-TELEMETRY"
                      Assert.equal [ executionId original ] (executionIds committed)
                      Assert.equal 1 (recordsFor root "TASK-TELEMETRY").Length

                      let result = resolve root "ready" "begin" []
                      CliGolden.expectExit 0 result
                      let payload = CliGolden.parse result.Out
                      Assert.equal "resolved" (payload.["outcome"].GetValue<string>())
                      CliGolden.jsonEqual committed.["telemetryExecutionIds"] payload.["plan"].["item"].["telemetryExecutionIds"]
                      CliGolden.jsonEqual committed.["telemetryExecutionIds"] payload.["plan"].["event"].["telemetryExecutions"]) }

          { Name = "work telemetry: resolution rejects the same ambiguous orphaned executions that begin rejects"
            Run =
              fun () ->
                  withRepository "ros-telemetry-resolution" "Telemetry Resolution Differential" (fun root ->
                      workOk root [ "begin"; "--id"; "TASK-TELEMETRY"; "--occurred-at"; CliGolden.at () ]

                      CliHarness.ros root [ "telemetry"; "start"; "TASK-TELEMETRY"; "--execution-id"; "EXE-second-link" ]
                      |> CliGolden.expectExit 0

                      let candidates = recordsFor root "TASK-TELEMETRY" |> List.map executionId

                      CliGolden.updateContextItem root "TASK-TELEMETRY" (fun item ->
                          item.["state"] <- JsonValue.Create "ready"
                          item.["semanticState"] <- JsonValue.Create "ready"
                          item.["telemetryExecutionIds"] <- JsonArray())

                      let again = work root [ "begin"; "--id"; "TASK-TELEMETRY"; "--occurred-at"; CliGolden.at () ]
                      CliGolden.expectExit 1 again
                      CliGolden.contains "multiple detached telemetry executions require explicit selection" (again.Out + again.Err)

                      let result = resolve root "ready" "begin" []
                      CliGolden.expectExit 1 result
                      let payload = CliGolden.parse result.Out
                      Assert.equal "rejected" (payload.["outcome"].GetValue<string>())
                      Assert.equal "ambiguous" (payload.["rejection"].["reason"].GetValue<string>())

                      Assert.equal
                          (List.sortWith (fun left right -> System.String.CompareOrdinal(left, right)) candidates)
                          (CliGolden.items payload.["rejection"].["executionIds"] |> List.map (fun id -> id.GetValue<string>()))) }

          { Name = "work telemetry: resolution links every currently active execution that resume links"
            Run =
              fun () ->
                  withRepository "ros-telemetry-resolution" "Telemetry Resolution Differential" (fun root ->
                      workOk root [ "begin"; "--id"; "TASK-TELEMETRY"; "--occurred-at"; CliGolden.at () ]

                      CliHarness.ros root [ "telemetry"; "start"; "TASK-TELEMETRY"; "--execution-id"; "EXE-second-active" ]
                      |> CliGolden.expectExit 0

                      workOk root [ "block"; "--id"; "TASK-TELEMETRY"; "--reason"; "waiting"; "--occurred-at"; CliGolden.at () ]

                      Assert.equal
                          [ "active"; "active" ]
                          (recordsFor root "TASK-TELEMETRY" |> List.map (fun record -> record.["status"].GetValue<string>()))

                      setExecutionIds root "TASK-TELEMETRY" []
                      workOk root [ "resume"; "--id"; "TASK-TELEMETRY"; "--occurred-at"; CliGolden.at () ]
                      let committed = CliGolden.contextItem root "TASK-TELEMETRY"

                      let result = resolve root "blocked" "resume" []
                      CliGolden.expectExit 0 result
                      let plan = (CliGolden.parse result.Out).["plan"]
                      Assert.equal (List.sort (executionIds committed)) (List.sort (executionIds plan.["item"]))) }

          { Name = "work telemetry: resolution recovers an orphan then finalizes it on completion, as complete does"
            Run =
              fun () ->
                  withRepository "ros-telemetry-resolution" "Telemetry Resolution Differential" (fun root ->
                      workOk root [ "begin"; "--id"; "TASK-TELEMETRY"; "--occurred-at"; CliGolden.at () ]
                      let original = recordsFor root "TASK-TELEMETRY" |> List.head
                      setExecutionIds root "TASK-TELEMETRY" []

                      workOk root ([ "complete"; "--id"; "TASK-TELEMETRY" ] @ completionEvidence root @ [ "--occurred-at"; CliGolden.at () ])
                      let committed = CliGolden.contextItem root "TASK-TELEMETRY"
                      Assert.equal [ executionId original ] (executionIds committed)
                      Assert.equal "finalized" ((recordsFor root "TASK-TELEMETRY" |> List.head).["status"].GetValue<string>())

                      // Re-resolving with an empty linked set against the now
                      // finalized record recovers it (ensure-completable allows
                      // active or finalized candidates), landing on the same ids.
                      let result = resolve root "active" "complete" []
                      CliGolden.expectExit 0 result
                      let plan = (CliGolden.parse result.Out).["plan"]
                      Assert.equal (executionIds committed) (executionIds plan.["item"])) }

          { Name = "work telemetry: resolution finalizes an already-linked execution without re-ensuring it"
            Run =
              fun () ->
                  withRepository "ros-telemetry-resolution" "Telemetry Resolution Differential" (fun root ->
                      workOk root [ "begin"; "--id"; "TASK-TELEMETRY"; "--occurred-at"; CliGolden.at () ]
                      let original = recordsFor root "TASK-TELEMETRY" |> List.head

                      workOk root ([ "complete"; "--id"; "TASK-TELEMETRY" ] @ completionEvidence root @ [ "--occurred-at"; CliGolden.at () ])
                      let committed = CliGolden.contextItem root "TASK-TELEMETRY"
                      Assert.equal [ executionId original ] (executionIds committed)

                      let result = resolve root "active" "complete" [ executionId original ]
                      CliGolden.expectExit 0 result
                      let plan = (CliGolden.parse result.Out).["plan"]
                      Assert.equal (executionIds committed) (executionIds plan.["item"])) } ]

    let private lifecycle =
        [ { Name = "work telemetry lifecycle: block and resume record lifecycle events and complete derives the blocked duration"
            Run =
              fun () ->
                  withRepository "ros-work-telemetry-lifecycle" "Work Telemetry Lifecycle Differential" (fun root ->
                      work root [ "start"; "--id"; "WI-CYCLE"; "--occurred-at"; "2026-09-10T18:00:00.000Z"; "--type"; "task" ] |> ignore
                      workOk root [ "block"; "--id"; "WI-CYCLE"; "--reason"; "waiting on review"; "--occurred-at"; "2026-09-10T18:05:00.000Z" ]
                      workOk root [ "resume"; "--id"; "WI-CYCLE"; "--occurred-at"; "2026-09-10T18:10:00.000Z" ]
                      CliHarness.write root "IMPLEMENTATION-NOTES.md" "Implemented.\n"
                      CliHarness.write root "TESTS-NOTES.md" "Tested.\n"

                      workOk
                          root
                          [ "complete"; "--id"; "WI-CYCLE"; "--occurred-at"; "2026-09-10T18:15:00.000Z"
                            "--evidence"; "implementation=IMPLEMENTATION-NOTES.md"; "--evidence"; "tests=TESTS-NOTES.md" ]

                      let execution = CliGolden.executions root |> List.head
                      Assert.equal "finalized" (execution.["status"].GetValue<string>())

                      let lifecycleEvents =
                          CliGolden.items execution.["events"]
                          |> List.filter (fun event -> event.["type"].GetValue<string>().StartsWith "work.")

                      Assert.equal 2 lifecycleEvents.Length
                      let ofType kind = lifecycleEvents |> List.find (fun event -> CliGolden.text event "type" = Some kind)
                      let blocked = ofType "work.blocked"
                      Assert.equal (Some "waiting on review") (CliGolden.text blocked "reason")

                      CliGolden.jsonEqual
                          (CliGolden.parse """{ "type": "ros-clock", "name": "ros", "mechanism": "work-lifecycle" }""")
                          blocked.["source"]

                      let resumed = ofType "work.resumed"
                      Assert.isTrue (resumed.AsObject().ContainsKey "reason" && isNull resumed.["reason"]) "resumed reason must be null"

                      let metric id =
                          CliGolden.metricValue execution id |> Option.map (fun value -> value.GetValue<int64>())

                      Assert.equal (Some 1L) (metric "agent.interruptions")
                      Assert.equal (Some 1L) (metric "agent.resumes")
                      Assert.equal "derived" ((CliGolden.capability execution "agent.interruptions").["status"].GetValue<string>())
                      // Derived from the controlled --occurred-at values: 18:10 - 18:05.
                      Assert.equal (Some(5L * 60L * 1000L)) (metric "time.blocked_ms")) }

          { Name = "work telemetry lifecycle: blocking a backlog-only item never creates an execution"
            Run =
              fun () ->
                  withRepository "ros-work-telemetry-lifecycle" "Work Telemetry Lifecycle Differential" (fun root ->
                      workOk root [ "capture"; "--id"; "WI-BACKLOG"; "--title"; "Backlog item"; "--occurred-at"; "2026-09-10T18:00:00.000Z" ]

                      workOk
                          root
                          [ "backlog-transition"; "--id"; "WI-BACKLOG"; "--action"; "ready"; "--occurred-at"; "2026-09-10T18:01:00.000Z" ]

                      workOk root [ "block"; "--id"; "WI-BACKLOG"; "--reason"; "waiting"; "--occurred-at"; "2026-09-10T18:05:00.000Z" ]
                      Assert.empty (CliGolden.executions root)) } ]

    let private parentExecution =
        [ { Name =
              "work resume parent execution: a new execution on the no-active-candidate path links the prior execution as its parent"
            Run =
              fun () ->
                  // The former Node implementation computed this parent id but
                  // discarded it (a known defect frozen as null); F# records it.
                  withRepository "ros-resume-parent" "Resume Parent Execution" (fun root ->
                      workOk root [ "begin"; "--id"; "TASK-PARENT"; "--occurred-at"; CliGolden.at () ]
                      workOk root [ "block"; "--id"; "TASK-PARENT"; "--reason"; "waiting"; "--occurred-at"; CliGolden.at () ]
                      let original = executionIds (CliGolden.contextItem root "TASK-PARENT") |> List.head
                      let recordPath = $".ros/telemetry/executions/{original}.json"
                      CliGolden.updateJson root recordPath (fun record -> record.["status"] <- JsonValue.Create "finalized")
                      setExecutionIds root "TASK-PARENT" []

                      workOk root [ "resume"; "--id"; "TASK-PARENT"; "--occurred-at"; "2026-09-10T18:00:00.000Z" ]
                      let created = executionIds (CliGolden.contextItem root "TASK-PARENT") |> List.head
                      Assert.isTrue (created <> original) "resume must create a new execution"
                      let record = CliGolden.readJson root $".ros/telemetry/executions/{created}.json"
                      Assert.equal (Some original) (CliGolden.text record.["identity"] "parentExecutionId")) } ]

    let tests = resolution @ lifecycle @ parentExecution
