namespace Praxis.Tests

open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Work

/// `work abandon`: cancelling live or backlog work truthfully.
[<RequireQualifiedAccess>]
module WorkAbandonTests =
    open PraxisCli

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"

    let private decide state reason =
        WorkTransition.decide
            { State = state
              Action = WorkAction.Abandon
              BlockReason = reason
              RequiredEvidence = Set.ofList [ "tests" ]
              ProvidedEvidence = Set.empty }

    let private withRepository (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "work-abandon"

        try
            let _, clone = installedRepository parent "clone-a"
            test clone
        finally
            GitFixture.cleanup parent

    let private cli clone arguments = run clone (Some agentA) arguments
    let private start clone id = cli clone [ "work"; "start"; "--id"; id; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
    let private abandon clone id reason = cli clone ([ "work"; "abandon"; "--id"; id; "--occurred-at"; now () ] @ (reason |> Option.map (fun text -> [ "--reason"; text ]) |> Option.defaultValue []))

    let private contextItem clone (id: string) =
        let view = run clone None [ "work"; "context"; id ] |> ok
        view.Json["workItems"].[0].AsObject()

    let private events clone =
        File.ReadAllLines(Path.Combine(clone, ".ros", "events", "events.jsonl"))
        |> Array.choose (fun line -> match JsonNode.Parse line with :? JsonObject as node -> Some node | _ -> None)
        |> Array.toList

    let private queueRow clone (id: string) =
        (JsonNode.Parse(File.ReadAllText(Path.Combine(clone, ".ros", "work", "queue.json"))).["items"].AsArray())
        |> Seq.map (fun node -> node.AsObject())
        |> Seq.find (fun item -> text item["id"] = id)

    let private executionStatuses clone (id: string) =
        Directory.GetFiles(Path.Combine(clone, ".ros", "telemetry", "executions"), "*.json")
        |> Array.map (fun file -> JsonNode.Parse(File.ReadAllText file).AsObject())
        |> Array.filter (fun record -> text record["workItemId"] = id)
        |> Array.map (fun record -> text record["status"])
        |> Array.toList

    let tests =
        [ { Name = "abandon: ready, active and blocked work can be abandoned; it needs a reason; abandoned is terminal"
            Run =
              fun () ->
                  for state in [ LiveWorkState.Ready; LiveWorkState.Active; LiveWorkState.Blocked ] do
                      Assert.equal (TransitionDecision.Allowed LiveWorkState.Abandoned) (decide state (Some "cancelled by the owner"))
                      Assert.equal (TransitionDecision.Rejected TransitionRejection.AbandonReasonRequired) (decide state (Some "  "))

                  for state in [ LiveWorkState.Complete; LiveWorkState.Abandoned ] do
                      Assert.equal (TransitionDecision.Rejected(TransitionRejection.IllegalTransition(state, WorkAction.Abandon))) (decide state (Some "why"))

                  Assert.empty (WorkTransition.allowedActions LiveWorkState.Abandoned) }
          { Name = "abandon: blocked live work with a backlog row is abandoned in both, with a recorded reason and finalized telemetry"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      cli clone [ "add"; "Something we will not do"; "--id"; "FEAT-9" ] |> ok |> ignore
                      cli clone [ "work"; "backlog-transition"; "--id"; "FEAT-9"; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore
                      start clone "FEAT-9"
                      cli clone [ "work"; "block"; "--id"; "FEAT-9"; "--reason"; "waiting on a decision"; "--occurred-at"; now () ] |> ok |> ignore
                      pushAll clone "praxis state" |> ignore
                      abandon clone "FEAT-9" (Some "cancelled by the owner") |> ok |> ignore

                      let item = contextItem clone "FEAT-9"
                      Assert.equal "abandoned" (text item["semanticState"])
                      Assert.equal "cancelled by the owner" (text item["abandonedReason"])
                      Assert.equal 0 (item["allowedActions"].AsArray().Count)

                      let event = events clone |> List.findBack (fun node -> text node["workItem"] = "FEAT-9")
                      Assert.equal "work.abandoned" (text event["type"])
                      Assert.equal "cancelled by the owner" (text event["reason"])
                      Assert.equal 0 (event["paths"].AsArray().Count)
                      Assert.equal "agent-a" (text event["actor"].["id"] |> fun id -> id.Split('/') |> Array.last)

                      let row = queueRow clone "FEAT-9"
                      Assert.equal "abandoned" (text row["status"])
                      Assert.equal "cancelled by the owner" (text row["abandonedReason"])
                      Assert.isTrue (executionStatuses clone "FEAT-9" |> List.forall ((=) "finalized")) "every execution must be finalized"
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "abandon: active work with pushed changes needs no checkpoint, and claims no paths"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      start clone "FEAT-1"
                      GitFixture.write clone "src/feature.txt" "half done\n"
                      pushAll clone "partial work" |> ignore
                      abandon clone "FEAT-1" (Some "superseded") |> ok |> ignore
                      Assert.equal "abandoned" (text (contextItem clone "FEAT-1").["semanticState"])
                      let event = events clone |> List.findBack (fun node -> text node["workItem"] = "FEAT-1")
                      Assert.equal 0 (event["paths"].AsArray().Count)) }
          { Name = "abandon: a backlog-only item is abandoned in the backlog"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      cli clone [ "add"; "An idea"; "--id"; "IDEA-1" ] |> ok |> ignore
                      abandon clone "IDEA-1" (Some "not worth doing") |> ok |> ignore
                      let row = queueRow clone "IDEA-1"
                      Assert.equal "abandoned" (text row["status"])
                      Assert.equal "not worth doing" (text row["abandonedReason"])) }
          { Name = "abandon: refused without a reason, for complete or unknown work, and nothing moves out of abandoned"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      start clone "FEAT-1"
                      Assert.equal 2 (abandon clone "FEAT-1" None).ExitCode
                      Assert.equal 2 (abandon clone "FEAT-1" (Some " ")).ExitCode
                      Assert.equal 1 (abandon clone "NOPE-1" (Some "why")).ExitCode
                      Assert.equal "active" (text (contextItem clone "FEAT-1").["semanticState"])
                      abandon clone "FEAT-1" (Some "cancelled") |> ok |> ignore

                      for arguments in
                          [ [ "work"; "resume"; "--id"; "FEAT-1"; "--occurred-at"; now () ]
                            [ "work"; "block"; "--id"; "FEAT-1"; "--reason"; "x"; "--occurred-at"; now () ]
                            [ "work"; "abandon"; "--id"; "FEAT-1"; "--reason"; "again"; "--occurred-at"; now () ] ] do
                          Assert.equal 1 (cli clone arguments).ExitCode

                      Assert.equal "abandoned" (text (contextItem clone "FEAT-1").["semanticState"])

                      start clone "FEAT-2"
                      cli clone [ "work"; "complete"; "--id"; "FEAT-2"; "--occurred-at"; now (); "--evidence"; "implementation=README.md"; "--evidence"; "tests=README.md" ] |> ok |> ignore
                      Assert.equal 1 (abandon clone "FEAT-2" (Some "too late")).ExitCode) }
          { Name = "abandon: the planner treats abandoned live work as terminal"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      start clone "FEAT-1"
                      abandon clone "FEAT-1" (Some "cancelled") |> ok |> ignore
                      let analysis = run clone None [ "plan"; "analyze"; "--json" ] |> ok
                      Assert.equal 0 (analysis.Json["items"].AsArray() |> Seq.filter (fun node -> text node["id"] = "FEAT-1") |> Seq.length)
                      Assert.equal 1 (analysis.Json["planningStates"].["abandoned"].GetValue<int>())) } ]
