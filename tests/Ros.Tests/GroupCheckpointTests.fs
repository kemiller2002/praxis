namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes

/// Group checkpoints (PRAXIS-GROUP-05, PRX-GRP-044) through the real CLI:
/// the same durability verification as `work checkpoint`, references to
/// members' own checkpoints that never replace them, and no attribution of
/// one member's changes to another (PRX-GRP-043).
[<RequireQualifiedAccess>]
module GroupCheckpointTests =
    open PraxisCli

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private agentB = agent "example/agent-b" "example" "agent-b" "session-b"

    let private withRepository (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "group-checkpoint"

        try
            let _, clone = installedRepository parent "clone-a"
            test clone
        finally
            GitFixture.cleanup parent

    let private start clone (id: string) =
        run clone (Some agentA) [ "work"; "start"; "--id"; id; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore

    let private capture clone (id: string) =
        run clone (Some agentA) [ "work"; "capture"; "--id"; id; "--title"; $"Remaining work {id}"; "--occurred-at"; now () ] |> ok |> ignore

    let private memberCheckpoint clone (id: string) =
        run clone (Some agentA) [ "work"; "checkpoint"; "--id"; id; "--occurred-at"; now (); "--summary"; $"{id} slice"; "--next-action"; "Run final completion transition"; "--json" ]
        |> ok

    let private groupCheckpoint clone who (members: string list) (extra: string list) =
        run
            clone
            (Some who)
            ([ "work"; "group"; "checkpoint"; "--group"; "GROUP-FEAT"; "--occurred-at"; now (); "--summary"; "Shared store landed"; "--next-action"; "Implement FEAT-3"; "--json" ]
             @ (members |> List.collect (fun id -> [ "--member"; id ]))
             @ extra)

    let private strings (node: JsonNode) =
        node :?> JsonArray |> Seq.map text |> Seq.toList

    let private rejectionCodes (result: Result) =
        match result.Json["rejections"] with
        | :? JsonArray as array -> array |> Seq.map (fun node -> text node["code"]) |> Seq.toList
        | _ -> []

    let private events clone =
        File.ReadAllLines(Path.Combine(clone, ".ros", "events", "events.jsonl"))
        |> Array.filter (fun line -> line.Trim().Length > 0)
        |> Array.map (fun line -> JsonNode.Parse line :?> JsonObject)
        |> Array.toList

    let private latestCheckpointOf clone (id: string) =
        let view = run clone None [ "work"; "context"; id ] |> ok
        view.Json["workItems"].[0].["latestCheckpoint"] |> Option.ofObj |> Option.map (fun node -> text node["id"])

    let tests =
        [ { Name = "group checkpoint: records active, completed and remaining members over their own checkpoints and claims no paths"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      start clone "FEAT-1"
                      start clone "FEAT-2"
                      capture clone "FEAT-3"
                      GitFixture.write clone "src/feature.txt" "one\n"
                      let sha = pushAll clone "FEAT-1: shared store"
                      let own = memberCheckpoint clone "FEAT-1"
                      let ownId = text own.Json["checkpoint"].["id"]
                      let before = events clone |> List.filter (fun event -> text event["type"] = "work.checkpointed")

                      let result =
                          groupCheckpoint clone agentA [ "FEAT-1"; "FEAT-2"; "FEAT-3" ] [ "--decision"; "one store model for every member" ] |> ok

                      let recorded = result.Json["checkpoint"]
                      Assert.equal "recorded" (text result.Json["status"])
                      Assert.equal sha (text recorded["commit"])
                      Assert.equal sha (text recorded["remoteCommit"])
                      Assert.equal "feature/x" (text recorded["branch"])
                      Assert.equal [ "FEAT-1"; "FEAT-2" ] (strings recorded["active"])
                      Assert.equal [ "FEAT-3" ] (strings recorded["remaining"])
                      Assert.equal [] (strings recorded["completed"])
                      Assert.equal [ "one store model for every member" ] (strings recorded["decisions"])
                      Assert.equal "Implement FEAT-3" (text recorded["nextAction"])
                      Assert.equal ownId (text recorded["members"].[0].["checkpoint"].["id"])
                      Assert.isTrue (isNull recorded["members"].[1].["checkpoint"]) "FEAT-2 has no checkpoint of its own"
                      Assert.equal 0 (result.Json["paths"] :?> JsonArray).Count
                      // FEAT-2 has no checkpoint at the group commit: warned, not replaced.
                      let warnings = result.Json["warnings"] :?> JsonArray |> Seq.map (fun node -> text node["workItem"]) |> Seq.toList
                      Assert.equal [ "FEAT-2" ] warnings
                      // Members' own checkpoints are untouched.
                      Assert.equal (Some ownId) (latestCheckpointOf clone "FEAT-1")
                      Assert.equal None (latestCheckpointOf clone "FEAT-2")
                      let after = events clone |> List.filter (fun event -> text event["type"] = "work.checkpointed")
                      Assert.equal before.Length after.Length
                      let groupEvent = events clone |> List.find (fun event -> text event["type"] = "work.group.checkpointed")
                      Assert.equal "GROUP-FEAT" (text groupEvent["group"])
                      Assert.equal 0 (groupEvent["paths"] :?> JsonArray).Count
                      Assert.isTrue (isNull groupEvent["workItem"]) "a group checkpoint is not any one member's event"
                      let shown = run clone None [ "work"; "group"; "checkpoint"; "show"; "GROUP-FEAT"; "--json" ] |> ok
                      let history = shown.Json["history"] :?> JsonArray
                      Assert.equal 1 history.Count
                      Assert.equal ownId (text history[0].["memberCheckpoints"].["FEAT-1"])
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "group checkpoint: a completed member is reported as completed, never an abandoned one"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      start clone "FEAT-1"
                      start clone "FEAT-2"
                      start clone "FEAT-4"
                      GitFixture.write clone "src/feature.txt" "one\n"
                      pushAll clone "FEAT-2: slice" |> ignore
                      memberCheckpoint clone "FEAT-2" |> ignore

                      run clone (Some agentA) [ "work"; "complete"; "--id"; "FEAT-2"; "--occurred-at"; now (); "--evidence"; "implementation=src/feature.txt"; "--evidence"; "tests=README.md" ]
                      |> ok
                      |> ignore

                      run clone (Some agentA) [ "work"; "abandon"; "--id"; "FEAT-4"; "--occurred-at"; now (); "--reason"; "superseded" ] |> ok |> ignore
                      pushAll clone "praxis state" |> ignore
                      let result = groupCheckpoint clone agentA [ "FEAT-1"; "FEAT-2"; "FEAT-4" ] [] |> ok
                      let recorded = result.Json["checkpoint"]
                      Assert.equal [ "FEAT-1" ] (strings recorded["active"])
                      Assert.equal [ "FEAT-2" ] (strings recorded["completed"])
                      Assert.equal [ "FEAT-4" ] (strings recorded["abandoned"])
                      Assert.equal [] (strings recorded["remaining"])) }
          { Name = "group checkpoint: the same durability rules as work checkpoint refuse unpushed and dirty work; nothing is recorded"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      start clone "FEAT-1"
                      GitFixture.write clone "src/feature.txt" "one\n"
                      GitFixture.commitAll clone "not pushed" |> ignore
                      Assert.equal [ "local-ahead" ] (rejectionCodes (groupCheckpoint clone agentA [ "FEAT-1" ] []))
                      GitFixture.git clone [ "push"; "-q" ] |> ignore
                      GitFixture.write clone "src/feature.txt" "two\n"
                      let dirty = groupCheckpoint clone agentA [ "FEAT-1" ] []
                      Assert.equal 1 dirty.ExitCode
                      Assert.equal [ "uncommitted-changes" ] (rejectionCodes dirty)
                      Assert.isTrue (events clone |> List.forall (fun event -> text event["type"] <> "work.group.checkpointed")) "a refused group checkpoint was recorded") }
          { Name = "group checkpoint: unknown members, no active member and another executor's work are refused"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-3"
                      Assert.equal [ "unknown-member" ] (rejectionCodes (groupCheckpoint clone agentA [ "FEAT-3"; "FEAT-404" ] []))
                      Assert.equal [ "no-active-member" ] (rejectionCodes (groupCheckpoint clone agentA [ "FEAT-3" ] []))
                      start clone "FEAT-1"
                      // agent B has no execution of any member: it cannot record under agent A's.
                      Assert.equal [ "missing-execution" ] (rejectionCodes (groupCheckpoint clone agentB [ "FEAT-1"; "FEAT-3" ] []))) }
          { Name = "group checkpoint: duplicate members, blank text and conflicting membership flags are argument errors (exit 2)"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      start clone "FEAT-1"
                      let duplicate = groupCheckpoint clone agentA [ "FEAT-1"; "FEAT-1" ] []
                      Assert.equal 2 duplicate.ExitCode
                      Assert.equal [ "duplicate-member" ] (rejectionCodes duplicate)
                      let blank = groupCheckpoint clone agentA [ "FEAT-1" ] [ "--decision"; "  " ]
                      Assert.equal 2 blank.ExitCode
                      Assert.equal [ "blank-decision" ] (rejectionCodes blank)
                      let both = groupCheckpoint clone agentA [ "FEAT-1" ] [ "--config"; "planner.json" ]
                      Assert.equal 2 both.ExitCode
                      Assert.isTrue (both.Error.Contains "either --member or --config") both.Error) }
          { Name = "group checkpoint: a group declared in planner configuration supplies its members; an undeclared group is refused"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      start clone "FEAT-1"
                      capture clone "FEAT-3"

                      File.WriteAllText(
                          Path.Combine(clone, "planner.json"),
                          """{ "grouping": { "groups": [ { "id": "GROUP-FEAT", "members": ["FEAT-1", "FEAT-3"], "origin": "human-declared" } ] } }"""
                      )

                      pushAll clone "planner configuration" |> ignore
                      let declared = groupCheckpoint clone agentA [] [ "--config"; "planner.json" ] |> ok
                      let recorded = declared.Json["checkpoint"]
                      Assert.equal [ "FEAT-1" ] (strings recorded["active"])
                      Assert.equal [ "FEAT-3" ] (strings recorded["remaining"])
                      Assert.equal "planner-configuration" (text recorded["declaration"].["source"])

                      let undeclared =
                          run clone (Some agentA) [ "work"; "group"; "checkpoint"; "--group"; "GROUP-OTHER"; "--config"; "planner.json"; "--occurred-at"; now (); "--summary"; "s"; "--next-action"; "n"; "--json" ]

                      Assert.equal 2 undeclared.ExitCode
                      Assert.equal [ "group-not-declared" ] (rejectionCodes undeclared)) }
          { Name = "group checkpoint: validate reports a group checkpoint that references a checkpoint the member never recorded"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      start clone "FEAT-1"
                      memberCheckpoint clone "FEAT-1" |> ignore
                      groupCheckpoint clone agentA [ "FEAT-1" ] [] |> ok |> ignore
                      run clone None [ "validate" ] |> ok |> ignore
                      let path = Path.Combine(clone, ".ros", "events", "events.jsonl")
                      let lines = File.ReadAllLines path
                      let index = lines |> Array.findIndexBack (fun line -> line.Contains "work.group.checkpointed")
                      let event = JsonNode.Parse lines[index] :?> JsonObject
                      event["groupCheckpoint"].["members"].[0].["checkpoint"].["id"] <- JsonValue.Create "000000000000000000000000"
                      lines[index] <- event.ToJsonString()
                      File.WriteAllLines(path, lines)
                      let invalid = run clone None [ "validate" ]
                      Assert.isTrue (invalid.ExitCode <> 0) "validate accepted a tampered group checkpoint"
                      let reported = invalid.Output + invalid.Error
                      Assert.isTrue (reported.Contains "not one of its own recorded checkpoints") reported
                      Assert.isTrue (reported.Contains "eventId does not match") reported) } ]
