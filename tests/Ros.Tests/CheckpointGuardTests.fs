namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes

/// Completion and block continuity guards through the real CLI
/// (PRAXIS-CONT-05-GUARDS, CONT-040..043).
[<RequireQualifiedAccess>]
module CheckpointGuardTests =
    open PraxisCli

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"

    let private withRepository (test: string -> string -> unit) =
        let parent = GitFixture.temporaryDirectory "checkpoint-guard"

        try
            let _, clone = installedRepository parent "clone-a"
            test parent clone
        finally
            GitFixture.cleanup parent

    let private start clone (id: string) (workType: string) =
        run clone (Some agentA) [ "work"; "start"; "--id"; id; "--type"; workType; "--occurred-at"; now () ] |> ok |> ignore

    let private checkpoint clone (id: string) =
        run clone (Some agentA) [ "work"; "checkpoint"; "--id"; id; "--occurred-at"; now (); "--summary"; "done"; "--next-action"; "Run final completion transition" ]

    let private complete clone (id: string) =
        run
            clone
            (Some agentA)
            [ "work"; "complete"; "--id"; id; "--occurred-at"; now (); "--evidence"; "implementation=src/feature.txt"; "--evidence"; "tests=README.md" ]

    let private state clone (id: string) =
        let view = run clone None [ "work"; "context"; id ] |> ok
        text (view.Json["workItems"].[0].["semanticState"])

    let private meaningfulWork clone =
        GitFixture.write clone "src/feature.txt" "feature\n"
        pushAll clone "feature work" |> ignore

    let tests =
        [ { Name = "completion guard: meaningful pushed work without a checkpoint cannot complete"
            Run =
              fun () ->
                  withRepository (fun _ clone ->
                      start clone "FEAT-1" "feature"
                      meaningfulWork clone
                      let refused = complete clone "FEAT-1"
                      Assert.equal 1 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "no durable checkpoint") refused.Error
                      Assert.equal "active" (state clone "FEAT-1")) }
          { Name = "completion guard: a stale checkpoint (a later meaningful commit, pushed or not) cannot complete"
            Run =
              fun () ->
                  withRepository (fun _ clone ->
                      start clone "FEAT-1" "feature"
                      meaningfulWork clone
                      checkpoint clone "FEAT-1" |> ok |> ignore
                      GitFixture.write clone "src/feature.txt" "final\n"
                      GitFixture.commitAll clone "final, not pushed" |> ignore
                      let unpushed = complete clone "FEAT-1"
                      Assert.equal 1 unpushed.ExitCode
                      Assert.isTrue (unpushed.Error.Contains "not the final HEAD") unpushed.Error
                      GitFixture.git clone [ "push"; "-q" ] |> ignore
                      let stale = complete clone "FEAT-1"
                      Assert.equal 1 stale.ExitCode
                      Assert.equal "active" (state clone "FEAT-1")) }
          { Name = "completion guard: a dirty final state cannot complete even with a current checkpoint"
            Run =
              fun () ->
                  withRepository (fun _ clone ->
                      start clone "FEAT-1" "feature"
                      meaningfulWork clone
                      checkpoint clone "FEAT-1" |> ok |> ignore
                      GitFixture.write clone "src/leftover.txt" "uncommitted\n"
                      let refused = complete clone "FEAT-1"
                      Assert.equal 1 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "uncommitted") refused.Error) }
          { Name = "completion guard: the remote is re-verified at completion; a checkpoint whose branch was deleted cannot complete"
            Run =
              fun () ->
                  withRepository (fun parent clone ->
                      start clone "FEAT-1" "feature"
                      meaningfulWork clone
                      checkpoint clone "FEAT-1" |> ok |> ignore
                      GitFixture.git (Path.Combine(parent, "remote.git")) [ "branch"; "-D"; "feature/x" ] |> ignore
                      let refused = complete clone "FEAT-1"
                      Assert.equal 1 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "remote-branch-missing") refused.Error) }
          { Name = "completion guard: a current checkpoint completes, also after the Praxis state was committed on top"
            Run =
              fun () ->
                  withRepository (fun _ clone ->
                      start clone "FEAT-1" "feature"
                      meaningfulWork clone
                      checkpoint clone "FEAT-1" |> ok |> ignore
                      // Persist the Praxis state: a non-meaningful commit after the checkpoint.
                      pushAll clone "praxis state" |> ignore
                      complete clone "FEAT-1" |> ok |> ignore
                      Assert.equal "complete" (state clone "FEAT-1")
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "completion guard: no-change work completes without any commit or checkpoint"
            Run =
              fun () ->
                  withRepository (fun _ clone ->
                      let head = GitFixture.git clone [ "rev-parse"; "HEAD" ]
                      start clone "RESEARCH-1" "mechanical"
                      run clone (Some agentA) [ "work"; "complete"; "--id"; "RESEARCH-1"; "--occurred-at"; now () ] |> ok |> ignore
                      Assert.equal "complete" (state clone "RESEARCH-1")
                      Assert.equal head (GitFixture.git clone [ "rev-parse"; "HEAD" ])) }
          { Name = "completion guard: a repository that has not opted in keeps its completion semantics"
            Run =
              fun () ->
                  withRepository (fun _ clone ->
                      let rosJson = Path.Combine(clone, "ros.json")
                      File.WriteAllText(rosJson, File.ReadAllText(rosJson).Replace("\"requireDurableCheckpoint\": true", "\"requireDurableCheckpoint\": false"))
                      pushAll clone "opt out" |> ignore
                      start clone "FEAT-1" "feature"
                      GitFixture.write clone "src/feature.txt" "uncommitted\n"
                      complete clone "FEAT-1" |> ok |> ignore
                      Assert.equal "complete" (state clone "FEAT-1")) }
          { Name = "block guard: new work without a checkpoint needs a checkpoint or a recorded unrecoverable reason"
            Run =
              fun () ->
                  withRepository (fun _ clone ->
                      start clone "FEAT-1" "feature"
                      GitFixture.write clone "src/feature.txt" "in progress\n"
                      let refused = run clone (Some agentA) [ "work"; "block"; "--id"; "FEAT-1"; "--reason"; "handoff"; "--occurred-at"; now () ]
                      Assert.equal 1 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "--unrecoverable-reason") refused.Error
                      Assert.equal "active" (state clone "FEAT-1")

                      run
                          clone
                          (Some agentA)
                          [ "work"; "block"; "--id"; "FEAT-1"; "--reason"; "handoff"; "--unrecoverable-reason"; "the remote rejects pushes (quota); work is local only"; "--occurred-at"; now () ]
                      |> ok
                      |> ignore

                      Assert.equal "blocked" (state clone "FEAT-1")

                      let blocked =
                          File.ReadAllLines(Path.Combine(clone, ".ros", "events", "events.jsonl"))
                          |> Array.map (fun line -> JsonNode.Parse line :?> JsonObject)
                          |> Array.findBack (fun node -> text node["type"] = "work.blocked")

                      Assert.equal "not-remotely-recoverable" (text blocked["continuity"].["status"])
                      Assert.equal "the remote rejects pushes (quota); work is local only" (text blocked["continuity"].["reason"])
                      let view = run clone None [ "work"; "context"; "FEAT-1" ] |> ok
                      let notice = view.Json["continuity"].[0].["unrecoverableLocalState"]
                      Assert.equal "the remote rejects pushes (quota); work is local only" (text notice["reason"])
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "block guard: with no work after the latest checkpoint, blocking is ordinary and records nothing extra"
            Run =
              fun () ->
                  withRepository (fun _ clone ->
                      start clone "FEAT-1" "feature"
                      meaningfulWork clone
                      checkpoint clone "FEAT-1" |> ok |> ignore
                      // A reason offered when there is no new work is not recorded.
                      run clone (Some agentA) [ "work"; "block"; "--id"; "FEAT-1"; "--reason"; "handoff"; "--unrecoverable-reason"; "x"; "--occurred-at"; now () ] |> ok |> ignore
                      let events = File.ReadAllText(Path.Combine(clone, ".ros", "events", "events.jsonl"))
                      Assert.isTrue (not (events.Contains "not-remotely-recoverable")) "an untrue continuity notice was recorded"
                      Assert.equal "blocked" (state clone "FEAT-1")) } ]
