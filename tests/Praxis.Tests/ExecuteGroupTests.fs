namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Planning
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// PRX-GRP-117, 130..132, 136..138 (PRAXIS-GROUP-09; PRX-GRP-190 cases 33-35,
/// 38-39, 44): `plan execute-group` and grouped execution by default.
[<RequireQualifiedAccess>]
module ExecuteGroupTests =
    open PraxisCli

    let private t name run = { Name = $"execute group: {name}"; Run = run }
    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private cli clone arguments = run clone (Some agentA) arguments
    let private groupId = "GROUP-FIXTURE-001"

    /// ITEM-1..3 ready (ITEM-2 depends on ITEM-1), ITEM-4 captured, all in
    /// one declared group (high affinity), pushed.
    let private withGroup (members: string list) (extra: string list) (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "execute-group"

        try
            let _, clone = installedRepository parent "clone"
            cli clone [ "add"; "Work ITEM-1"; "--id"; "ITEM-1" ] |> ok |> ignore
            cli clone [ "add"; "Work ITEM-2"; "--id"; "ITEM-2"; "--description"; "Builds on the first. Depends on: ITEM-1." ] |> ok |> ignore
            cli clone [ "add"; "Work ITEM-3"; "--id"; "ITEM-3" ] |> ok |> ignore
            cli clone [ "add"; "Work ITEM-4"; "--id"; "ITEM-4" ] |> ok |> ignore

            for id in [ "ITEM-1"; "ITEM-2"; "ITEM-3" ] do
                cli clone [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore

            cli clone ([ "work"; "group"; "create"; "--group"; groupId; "--occurred-at"; now () ] @ (members |> List.collect (fun id -> [ "--member"; id ])) @ extra) |> ok |> ignore
            pushAll clone "declare the group" |> ignore
            test clone
        finally
            GitFixture.cleanup parent

    let private explain clone (config: string list) =
        (run clone None ([ "plan"; "explain-group"; groupId; "--json" ] @ config) |> ok).Json["group"].["groupedExecution"]

    let private execute clone (extra: string list) = cli clone ([ "plan"; "execute-group"; groupId; "--occurred-at"; now (); "--json" ] @ extra)

    let private store (clone: string) : JsonNode =
        let document = JsonNode.Parse(File.ReadAllText(Path.Combine(clone, ".ros", "work", "groups.json")))
        document["groups"].[0]

    let private changedPaths clone =
        GitFixture.git clone [ "status"; "--porcelain"; "--untracked-files=all" ]
        |> fun output -> output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun line ->
            let entry = line.TrimStart()
            entry.Substring(entry.IndexOf ' ').Trim())
        |> Array.toList

    /// Each fixture item's priority and description (where its dependencies
    /// are declared), to show execute-group changes neither (PRX-GRP-117).
    let private planningFields (clone: string) =
        let queue = JsonNode.Parse(File.ReadAllText(Path.Combine(clone, ".ros", "work", "queue.json")))

        queue["items"].AsArray()
        |> Seq.filter (fun item -> (text item["id"]).StartsWith "ITEM-")
        |> Seq.map (fun item ->
            let field (name: string) = item[name] |> Option.ofObj |> Option.map _.ToJsonString()
            text item["id"], field "priority", field "description")
        |> Seq.toList

    let private config clone (json: string) =
        let path = Path.Combine(clone, "..", "planner.json")
        File.WriteAllText(path, json)
        [ "--config"; path ]

    let tests =
        [ t "a qualifying group defaults to grouped; each failed threshold is explained; advisory by configuration alone" (fun () ->
              withGroup [ "ITEM-1"; "ITEM-2"; "ITEM-3" ] [] (fun clone ->
                  let qualified = explain clone []
                  Assert.equal true (qualified["qualifies"].GetValue<bool>())
                  Assert.equal ("grouped", "grouped") (text qualified["recommended"], text qualified["executeGroupDefault"])
                  let explained = run clone None [ "plan"; "explain-group"; groupId ] |> ok
                  Assert.isTrue (explained.Output.Contains "grouped execution: qualifies") explained.Output
                  let listed = (run clone None [ "plan"; "groups"; "--json" ] |> ok).Json["groups"].AsArray() |> Seq.find (fun group -> text group["id"] = groupId)
                  Assert.equal (qualified.ToJsonString()) (listed["groupedExecution"].ToJsonString())

                  let small = explain clone (config clone """{"grouping":{"groupedExecution":{"maximumSize":2}}}""")
                  Assert.equal false (small["qualifies"].GetValue<bool>())
                  Assert.isTrue ((text small["failures"].[0]).Contains "outside 2..2") (small.ToJsonString())
                  Assert.equal "independent" (text small["executeGroupDefault"])

                  // PRX-GRP-138: the rollback is a configuration change only.
                  let advisory = explain clone (config clone """{"grouping":{"groupedExecution":{"default":"advisory"}}}""")
                  Assert.equal (true, "advisory", "advisory", "independent") (advisory["qualifies"].GetValue<bool>(), text advisory["default"], text advisory["recommended"], text advisory["executeGroupDefault"])
                  let rolledBack = execute clone ((config clone """{"grouping":{"groupedExecution":{"default":"advisory"}}}""") @ [ "--dry-run" ]) |> ok
                  Assert.equal "independent" (text rolledBack.Json["groupExecution"].["mode"])))

          t "a captured member fails qualification, and qualification is deterministic" (fun () ->
              withGroup [ "ITEM-1"; "ITEM-3"; "ITEM-4" ] [] (fun clone ->
                  let first = explain clone []
                  Assert.equal false (first["qualifies"].GetValue<bool>())
                  Assert.isTrue (first["failures"].AsArray() |> Seq.exists (fun failure -> (text failure).Contains "ITEM-4 is still captured")) (first.ToJsonString())
                  Assert.equal (first.ToJsonString()) ((explain clone []).ToJsonString())))

          t "cli execute-group begins members in required order through work begin, records the group execution, is idempotent per member, and writes nothing else" (fun () ->
              withGroup [ "ITEM-1"; "ITEM-2"; "ITEM-3" ] [] (fun clone ->
                  let branch = GitFixture.git clone [ "rev-parse"; "--abbrev-ref"; "HEAD" ]
                  let head = GitFixture.git clone [ "rev-parse"; "HEAD" ]
                  let fieldsBefore = planningFields clone
                  let dryRun = execute clone [ "--dry-run" ] |> ok
                  Assert.equal "dry-run" (text dryRun.Json["status"])
                  Assert.equal [] (changedPaths clone)

                  let first = execute clone [] |> ok
                  Assert.equal ("begun", "ITEM-1", "grouped") (text first.Json["status"], text first.Json["member"], text first.Json["mode"])
                  let gex = text first.Json["groupExecution"].["id"]
                  Assert.isTrue (gex.StartsWith "GEX-") gex
                  let execution = text first.Json["memberExecution"]
                  let recorded = (store clone).["executions"].[0]
                  Assert.equal (gex, "ITEM-1", execution) (text recorded["id"], text recorded["members"].[0].["workItemId"], text recorded["members"].[0].["executionId"])
                  Assert.equal (groupId, "example/agent-a", "grouped") (text recorded["groupId"], text recorded["actor"].["id"], text recorded["mode"])
                  Assert.equal [ "ITEM-1"; "ITEM-2"; "ITEM-3" ] (recorded["order"].AsArray() |> Seq.map text |> Seq.toList)
                  Assert.isTrue (text recorded["startedAt"] <> "" && text recorded["repository"] <> "") (recorded.ToJsonString())
                  Assert.isTrue (recorded["basis"].AsArray().Count > 0 && not (isNull recorded["optOuts"])) (recorded.ToJsonString())
                  let context = JsonNode.Parse(File.ReadAllText(Path.Combine(clone, ".ros", "context", "current.json")))
                  Assert.equal "active" (text (context["workItems"].AsArray() |> Seq.find (fun node -> text node["id"] = "ITEM-1")).["semanticState"])

                  // Only work begin's own files and the group store changed.
                  let allowed (path: string) =
                      path.StartsWith ".ros/telemetry/executions/" || List.contains path [ ".ros/work/queue.json"; ".ros/work/queue.md"; ".ros/context/current.json"; ".ros/events/events.jsonl"; ".ros/work/groups.json" ]

                  Assert.isTrue (changedPaths clone |> List.forall allowed) $"{changedPaths clone}"
                  Assert.equal (branch, head) (GitFixture.git clone [ "rev-parse"; "--abbrev-ref"; "HEAD" ], GitFixture.git clone [ "rev-parse"; "HEAD" ])

                  let repeat = execute clone [ "--member"; "ITEM-1" ] |> ok
                  Assert.equal ("unchanged", false) (text repeat.Json["status"], repeat.Json["changed"].GetValue<bool>())

                  // ITEM-2 waits on ITEM-1, so the next runnable member is ITEM-3.
                  let second = execute clone [] |> ok
                  Assert.equal ("ITEM-3", gex) (text second.Json["member"], text second.Json["groupExecution"].["id"])
                  let refused = execute clone [ "--member"; "ITEM-2" ]
                  Assert.equal 1 refused.ExitCode
                  Assert.equal "member-not-runnable" (text refused.Json["rejections"].[0].["code"])
                  let history = (store clone).["history"].AsArray() |> Seq.map (fun entry -> text entry["operation"]) |> Seq.toList
                  Assert.equal [ "created"; "execution-started"; "member-begun"; "member-begun" ] history
                  Assert.equal fieldsBefore (planningFields clone)))

          t "cli execute-group refuses an unknown group, a group with nothing runnable here, and a caller who owns another open group execution" (fun () ->
              withGroup [ "ITEM-1"; "ITEM-3" ] [] (fun clone ->
                  let unknown = cli clone [ "plan"; "execute-group"; "GROUP-NOPE-001"; "--occurred-at"; now () ]
                  Assert.equal 1 unknown.ExitCode
                  cli clone [ "work"; "group"; "create"; "--group"; "GROUP-OTHER-001"; "--member"; "ITEM-2"; "--occurred-at"; now () ] |> ok |> ignore
                  let blocked = cli clone [ "plan"; "execute-group"; "GROUP-OTHER-001"; "--occurred-at"; now (); "--json" ]
                  Assert.equal 1 blocked.ExitCode
                  Assert.equal "nothing-runnable-here" (text blocked.Json["rejections"].[0].["code"])
                  execute clone [] |> ok |> ignore
                  cli clone [ "work"; "backlog-transition"; "--id"; "ITEM-4"; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore
                  cli clone [ "work"; "group"; "create"; "--group"; "GROUP-THIRD-001"; "--member"; "ITEM-4"; "--occurred-at"; now () ] |> ok |> ignore
                  let owns = cli clone [ "plan"; "execute-group"; "GROUP-THIRD-001"; "--occurred-at"; now (); "--json" ]
                  Assert.equal 1 owns.ExitCode
                  Assert.equal "owns-other-group-execution" (text owns.Json["rejections"].[0].["code"])
                  Assert.equal 2 (cli clone [ "plan"; "execute-group"; groupId ]).ExitCode))

          t "cli execute-group refuses a group whose members depend on each other in a cycle, and records nothing" (fun () ->
              withGroup [ "ITEM-1"; "ITEM-2" ] [] (fun clone ->
                  cli clone [ "work"; "update"; "--id"; "ITEM-1"; "--occurred-at"; now (); "--description"; "Needs the second. Depends on: ITEM-2." ] |> ok |> ignore
                  let storeBefore = File.ReadAllText(Path.Combine(clone, ".ros", "work", "groups.json"))
                  let refused = execute clone []
                  Assert.equal 1 refused.ExitCode
                  Assert.equal "dependency-cycle" (text refused.Json["rejections"].[0].["code"])
                  Assert.equal storeBefore (File.ReadAllText(Path.Combine(clone, ".ros", "work", "groups.json")))))

          t "cli opt-outs need a reason, are recorded in history with actor and time, and are honoured" (fun () ->
              withGroup [ "ITEM-1"; "ITEM-3" ] [] (fun clone ->
                  Assert.equal 2 (execute clone [ "--independent-member"; "ITEM-3" ]).ExitCode
                  Assert.equal 2 (execute clone [ "--mode"; "grouped" ]).ExitCode
                  Assert.equal 2 (cli clone [ "work"; "group"; "add"; "--group"; groupId; "--member"; "ITEM-3"; "--independent-member"; "ITEM-3"; "--occurred-at"; now () ]).ExitCode
                  cli clone [ "work"; "group"; "add"; "--group"; groupId; "--member"; "ITEM-3"; "--independent-member"; "ITEM-3"; "--reason"; "touches another subsystem"; "--occurred-at"; now () ] |> ok |> ignore
                  let optedOut = (store clone).["history"].AsArray() |> Seq.last
                  Assert.equal ("opted-out", "ITEM-3", "touches another subsystem", "example/agent-a") (text optedOut["operation"], text optedOut["member"], text optedOut["reason"], text optedOut["actor"].["id"])
                  Assert.isTrue (text optedOut["at"] <> "") (optedOut.ToJsonString())
                  let shown = run clone None [ "work"; "group"; "show"; groupId; "--json" ] |> ok
                  Assert.isTrue (shown.Output.Contains "\"independentMembers\"" && shown.Output.Contains "touches another subsystem") shown.Output
                  let independents = (explain clone [])["independentMembers"]
                  Assert.equal "ITEM-3" (text independents[0].["workItem"])
                  execute clone [] |> ok |> ignore
                  let third = execute clone [] |> ok
                  Assert.equal ("ITEM-3", "independent") (text third.Json["member"], text third.Json["mode"])
                  // An opt-out applies only to executions not yet begun (PRX-GRP-132).
                  cli clone [ "work"; "group"; "add"; "--group"; groupId; "--member"; "ITEM-1"; "--independent-member"; "ITEM-1"; "--reason"; "late opt-out"; "--occurred-at"; now () ] |> ok |> ignore
                  let begun = (store clone).["executions"].[0].["members"].AsArray() |> Seq.find (fun entry -> text entry["workItemId"] = "ITEM-1")
                  Assert.equal "grouped" (text begun["mode"])))

          t "cli a per-group opt-out makes the recommendation advisory and execute-group independent" (fun () ->
              withGroup [ "ITEM-1"; "ITEM-3" ] [ "--execution-mode"; "independent"; "--reason"; "unrelated designs" ] (fun clone ->
                  let qualification = explain clone []
                  Assert.equal ("advisory", "unrelated designs") (text qualification["recommended"], text qualification["optOut"])
                  let begun = execute clone [] |> ok
                  Assert.equal "independent" (text begun.Json["groupExecution"].["mode"])
                  let listed = run clone None [ "work"; "group"; "list"; "--json" ] |> ok
                  let row = listed.Json["groups"].[0]
                  Assert.equal "independent" (text row["executionMode"])))

          t "cli a context-pressure signal makes execute-group fall back, recorded, without changing the active member" (fun () ->
              withGroup [ "ITEM-1"; "ITEM-3" ] [] (fun clone ->
                  let first = execute clone [] |> ok
                  let execution = text first.Json["memberExecution"]
                  cli clone [ "telemetry"; "record"; execution; "--metric"; "context.compactions"; "--value"; "1"; "--quality"; "observed" ] |> ok |> ignore
                  let contextBefore = File.ReadAllText(Path.Combine(clone, ".ros", "context", "current.json"))
                  let fell = execute clone [] |> ok
                  Assert.equal "fell-back" (text fell.Json["status"])
                  let fallback = fell.Json["groupExecution"].["fallback"]
                  Assert.equal ("independent", "context.compactions") (text fallback["mode"], text fallback["signal"])
                  Assert.isTrue ((text fell.Json["recommendations"].[0]).StartsWith "ITEM-3") (fell.Json.ToJsonString())
                  Assert.equal contextBefore (File.ReadAllText(Path.Combine(clone, ".ros", "context", "current.json")))
                  // The next group execution runs independently.
                  let next = execute clone [] |> ok
                  Assert.equal ("independent", "ITEM-3") (text next.Json["groupExecution"].["mode"], text next.Json["member"])))

          t "the fallback signal is deterministic and unknown metrics never trigger it" (fun () ->
              let actor: Actor = { Kind = ActorKind.Agent; Id = "a"; Provider = None; Model = None; Runtime = None }

              let execution =
                  { Id = "GEX-1"; GroupId = groupId; Actor = actor; StartedAt = "2026-10-07T00:00:00.000Z"; Repository = "r"; Order = [ "ITEM-1" ]
                    Mode = ExecutionMode.Grouped; Basis = []; OptOuts = []
                    Members = [ { WorkItemId = "ITEM-1"; ExecutionId = "EXE-1"; BegunAt = "2026-10-07T00:00:00.000Z"; Mode = ExecutionMode.Grouped } ]
                    Fallback = None; EndedAt = None; Successors = [] }

              let settings = GroupedExecutionConfiguration.defaults
              let signal metrics elapsed pressure = GroupExecutions.fallbackSignal settings execution metrics elapsed pressure "2026-10-07T01:00:00.000Z"
              Assert.equal None (signal (fun _ -> None, None) (fun _ -> None, None) [])
              Assert.equal None (signal (fun _ -> Some 0, Some 25) (fun _ -> Some 100L, Some 100L) [])
              let reads = signal (fun _ -> None, Some 26) (fun _ -> None, None) []
              Assert.equal (Some "context.repeated_file_reads") (reads |> Option.map (fun fallback -> fallback.Signal))
              Assert.equal reads (signal (fun _ -> None, Some 26) (fun _ -> None, None) [])
              Assert.equal (Some "elapsed-over-estimate") (signal (fun _ -> None, None) (fun _ -> Some 160L, Some 100L) [] |> Option.map (fun fallback -> fallback.Signal))
              Assert.equal (Some "context-pressure") (signal (fun _ -> None, None) (fun _ -> None, None) [ [ "ITEM-1" ], [ "re-reads", 2 ], "obs-1" ] |> Option.map (fun fallback -> fallback.Signal))) ]
