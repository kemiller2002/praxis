namespace Praxis.Tests

open System.IO
open System.Text.Json.Nodes

/// `work continue` through the real CLI (PRAXIS-CONT-06-CONTINUE,
/// CONT-050..053): a successor takes over active work in a new execution of
/// its own; the predecessor is named, never impersonated, never edited.
[<RequireQualifiedAccess>]
module ContinuationCliTests =
    open PraxisCli

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private agentB = agent "other/agent-b" "other" "agent-b" "session-b"

    let private human =
        { Kind = "human"
          Id = "casey"
          Provider = ""
          Runtime = ""
          Session = "terminal-1" }

    let private withCheckpointedWork (test: string -> string -> string -> unit) =
        let parent = GitFixture.temporaryDirectory "continuation"

        try
            let bare, clone = installedRepository parent "clone-a"
            run clone (Some agentA) [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
            GitFixture.write clone "src/feature.txt" "part one\n"
            pushAll clone "part one" |> ignore

            run clone (Some agentA) [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "Part one implemented"; "--next-action"; "Implement part two" ]
            |> ok
            |> ignore

            pushAll clone "praxis state" |> ignore
            test parent bare clone
        finally
            GitFixture.cleanup parent

    let private executionOf (clone: string) =
        let view = run clone None [ "work"; "context"; "FEAT-1" ] |> ok
        view.Json["continuity"].[0].["checkpoint"].["executionId"] |> text

    let private continueAs clone who =
        run clone (Some who) [ "work"; "continue"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--json" ]

    let tests =
        [ { Name = "continue: a successor gets a new execution whose parent is the predecessor, which stays untouched"
            Run =
              fun () ->
                  withCheckpointedWork (fun parent bare _ ->
                      let clone = GitFixture.secondClone parent bare "clone-b"
                      let predecessor = executionOf clone
                      let predecessorFile = Path.Combine(clone, ".ros", "telemetry", "executions", $"{predecessor}.json")
                      let before = File.ReadAllBytes predecessorFile
                      let result = continueAs clone agentB |> ok
                      let document = result.Json
                      Assert.equal "continued" (text document["status"])
                      let successor = text document["executionId"]
                      Assert.isTrue (successor <> predecessor) "the successor reused the predecessor's execution"
                      Assert.equal predecessor (text document["predecessor"].["executionId"])
                      Assert.equal "interrupted" (text document["predecessor"].["disposition"])
                      Assert.equal "Part one implemented" (text document["continuity"].["checkpoint"].["summary"])
                      Assert.equal "Implement part two" (text document["continuity"].["checkpoint"].["nextAction"])
                      Assert.equal "current" (text document["continuity"].["freshness"])
                      Assert.isTrue ((document["obligations"].["requiredEvidenceForCompletion"] :?> JsonArray).Count > 0) "no obligations"
                      Assert.isTrue (before = File.ReadAllBytes predecessorFile) "the predecessor's execution record was modified"
                      let record = JsonNode.Parse(File.ReadAllText(Path.Combine(clone, ".ros", "telemetry", "executions", $"{successor}.json")))
                      Assert.equal predecessor (text record["identity"].["parentExecutionId"])
                      Assert.equal "other" (text record["identity"].["provider"])
                      Assert.equal "active" (text record["status"])
                      let view = run clone None [ "work"; "context"; "FEAT-1" ] |> ok
                      let links = view.Json["workItems"].[0].["telemetryExecutionIds"] :?> JsonArray |> Seq.map text |> Seq.toList
                      Assert.isTrue (List.contains successor links && List.contains predecessor links) $"{links}"
                      Assert.equal "active" (text view.Json["workItems"].[0].["semanticState"])

                      let continued =
                          File.ReadAllLines(Path.Combine(clone, ".ros", "events", "events.jsonl"))
                          |> Array.map (fun line -> JsonNode.Parse line :?> JsonObject)
                          |> Array.findBack (fun node -> text node["type"] = "work.continued")

                      Assert.equal "other/agent-b" (text continued["actor"].["id"])
                      Assert.equal successor (text continued["execution"])
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "continue: an agent can hand off to a human, each keeping its own identity"
            Run =
              fun () ->
                  withCheckpointedWork (fun parent bare _ ->
                      let clone = GitFixture.secondClone parent bare "clone-human"
                      let result = continueAs clone human |> ok
                      let successor = text result.Json["executionId"]
                      let record = JsonNode.Parse(File.ReadAllText(Path.Combine(clone, ".ros", "telemetry", "executions", $"{successor}.json")))
                      Assert.equal "human" (text record["identity"].["actorKind"])
                      // The human checkpoints under their own execution.
                      GitFixture.write clone "src/feature.txt" "part two\n"
                      pushAll clone "part two" |> ignore

                      let checkpoint =
                          run clone (Some human) [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "Part two"; "--next-action"; "Run final completion transition"; "--json" ]
                          |> ok

                      Assert.equal successor (text checkpoint.Json["checkpoint"].["executionId"])) }
          { Name = "continue: the predecessor's own run, a dirty checkout, blocked work and an undeclared process are refused"
            Run =
              fun () ->
                  withCheckpointedWork (fun parent bare clone ->
                      let own = continueAs clone agentA
                      Assert.equal 1 own.ExitCode
                      Assert.equal "already-executing" (text own.Json["refusal"].["code"])
                      let other = GitFixture.secondClone parent bare "clone-dirty"
                      GitFixture.write other "src/feature.txt" "someone's local edit\n"
                      let dirty = continueAs other agentB
                      Assert.equal 1 dirty.ExitCode
                      Assert.equal "local-changes" (text dirty.Json["refusal"].["code"])
                      Assert.equal "someone's local edit\n" (File.ReadAllText(Path.Combine(other, "src", "feature.txt")))
                      let events = File.ReadAllText(Path.Combine(other, ".ros", "events", "events.jsonl"))
                      Assert.isTrue (not (events.Contains "work.continued")) "a refused continuation was recorded"
                      let undeclared = run clone None [ "work"; "continue"; "--id"; "FEAT-1"; "--occurred-at"; now () ]
                      Assert.equal 1 undeclared.ExitCode
                      Assert.isTrue (undeclared.Error.Contains "declare yourself") undeclared.Error
                      run clone (Some agentA) [ "work"; "block"; "--id"; "FEAT-1"; "--reason"; "paused"; "--occurred-at"; now () ] |> ok |> ignore
                      let blocked = continueAs clone agentB
                      Assert.equal "work-item-not-active" (text blocked.Json["refusal"].["code"])) }
          { Name = "continue: validation reports a successor whose recorded parent is not the named predecessor"
            Run =
              fun () ->
                  withCheckpointedWork (fun parent bare _ ->
                      let clone = GitFixture.secondClone parent bare "clone-b"
                      let result = continueAs clone agentB |> ok
                      let successor = text result.Json["executionId"]
                      let file = Path.Combine(clone, ".ros", "telemetry", "executions", $"{successor}.json")
                      let record = JsonNode.Parse(File.ReadAllText file)
                      record["identity"].["parentExecutionId"] <- JsonValue.Create "EXE-SOMEONE-ELSE"
                      File.WriteAllText(file, record.ToJsonString())
                      let validation = run clone None [ "validate"; "--json" ]
                      Assert.equal 1 validation.ExitCode
                      Assert.isTrue (validation.Output.Contains "records parent") validation.Output) } ]
