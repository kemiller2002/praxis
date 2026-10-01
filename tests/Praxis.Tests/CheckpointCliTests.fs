namespace Praxis.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes

/// Drives the real `praxis` binary (the same CLI `./praxis` and its `./ros` alias run)
/// against fixture repositories, with identity supplied only through the
/// documented environment variables.
module PraxisCli =
    let private identityKeys =
        [ "CLAUDE_CODE_SESSION_ID"
          "CODEX_SESSION_ID"
          "CODEX_THREAD_ID"
          "GEMINI_SESSION_ID"
          "COPILOT_SESSION_ID"
          "GITHUB_ACTIONS"
          "GITHUB_RUN_ID"
          "OLLAMA_HOST"
          "ROS_ACTOR"
          "ROS_ACTOR_KIND"
          "ROS_TELEMETRY_PROVIDER"
          "ROS_TELEMETRY_RUNTIME"
          "ROS_TELEMETRY_MODEL"
          "ROS_TELEMETRY_MODEL_VERSION"
          "ROS_TELEMETRY_RUNTIME_VERSION"
          "ROS_TELEMETRY_SESSION_ID"
          "ROS_TELEMETRY_CONVERSATION_ID"
          "ROS_TELEMETRY_RUN_ID"
          "ROS_BASE_REF" ]

    type Result =
        { ExitCode: int
          Output: string
          Error: string }

        member this.Json = JsonNode.Parse this.Output :?> JsonObject

    /// An executor's declared identity, as an agent would set it once.
    type Executor =
        { Kind: string
          Id: string
          Provider: string
          Runtime: string
          Session: string }

    let agent id provider runtime session =
        { Kind = "agent"
          Id = id
          Provider = provider
          Runtime = runtime
          Session = session }

    let private cli = Path.Combine(AppContext.BaseDirectory, "praxis.dll")

    let now () = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    let rec run (root: string) (executor: Executor option) (arguments: string list) : Result =
        runWith root executor [] arguments

    /// `run` with extra environment variables (for example CI's `ROS_BASE_REF`).
    and runWith (root: string) (executor: Executor option) (environment: (string * string) list) (arguments: string list) : Result =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.WorkingDirectory <- root
        identityKeys |> List.iter (fun key -> startInfo.Environment.Remove key |> ignore)

        match executor with
        | Some who ->
            startInfo.Environment["ROS_ACTOR_KIND"] <- who.Kind
            startInfo.Environment["ROS_ACTOR"] <- who.Id

            if who.Kind <> "human" then
                startInfo.Environment["ROS_TELEMETRY_PROVIDER"] <- who.Provider
                startInfo.Environment["ROS_TELEMETRY_RUNTIME"] <- who.Runtime

            startInfo.Environment["ROS_TELEMETRY_SESSION_ID"] <- who.Session
        | None -> ()

        environment |> List.iter (fun (key, value) -> startInfo.Environment[key] <- value)
        startInfo.ArgumentList.Add cli
        startInfo.ArgumentList.Add "--root"
        startInfo.ArgumentList.Add root
        arguments |> List.iter startInfo.ArgumentList.Add
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        child.WaitForExit()

        { ExitCode = child.ExitCode
          Output = output.Result
          Error = error.Result }

    let ok (result: Result) =
        if result.ExitCode <> 0 then
            failwith $"command failed ({result.ExitCode}): {result.Error}\n{result.Output}"

        result

    let text (node: JsonNode) = node.GetValue<string>()

    /// A Praxis-installed repository with a bare remote, working on branch
    /// `feature/x` pushed with an upstream. Durable checkpoints are enforced.
    let installedRepository (parent: string) (name: string) =
        let bare = Path.Combine(parent, "remote.git")
        let clone = Path.Combine(parent, name)
        Directory.CreateDirectory bare |> ignore
        GitFixture.git bare [ "init"; "-q"; "--bare"; "-b"; "main" ] |> ignore
        Directory.CreateDirectory clone |> ignore
        GitFixture.git clone [ "init"; "-q"; "-b"; "main" ] |> ignore
        GitFixture.configureIdentity clone
        GitFixture.git clone [ "remote"; "add"; "origin"; bare ] |> ignore
        run clone None [ "init"; "--project"; "Continuity Fixture" ] |> ok |> ignore
        let rosJson = Path.Combine(clone, "ros.json")
        let config = JsonNode.Parse(File.ReadAllText rosJson) :?> JsonObject
        let continuity = JsonObject()
        continuity["requireDurableCheckpoint"] <- JsonValue.Create true
        (config["workProtocol"] :?> JsonObject)["continuity"] <- continuity
        File.WriteAllText(rosJson, config.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)))
        GitFixture.commitAll clone "install praxis" |> ignore
        GitFixture.git clone [ "push"; "-q"; "-u"; "origin"; "main" ] |> ignore
        GitFixture.git clone [ "switch"; "-q"; "-c"; "feature/x" ] |> ignore
        GitFixture.git clone [ "push"; "-q"; "-u"; "origin"; "feature/x" ] |> ignore
        bare, clone

    let pushAll (clone: string) (message: string) =
        let sha = GitFixture.commitAll clone message
        GitFixture.git clone [ "push"; "-q" ] |> ignore
        sha

[<RequireQualifiedAccess>]
module CheckpointCliTests =
    open PraxisCli

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private agentB = agent "example/agent-b" "example" "agent-b" "session-b"

    let private withRepository (test: string -> string -> string -> unit) =
        let parent = GitFixture.temporaryDirectory "checkpoint-cli"

        try
            let bare, clone = installedRepository parent "clone-a"
            test parent bare clone
        finally
            GitFixture.cleanup parent

    let private start clone who (id: string) =
        run clone (Some who) [ "work"; "start"; "--id"; id; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore

    let private checkpoint clone who (id: string) (extra: string list) =
        run clone (Some who) ([ "work"; "checkpoint"; "--id"; id; "--occurred-at"; now (); "--summary"; "Implemented the slice"; "--next-action"; "Write the next slice"; "--json" ] @ extra)

    let private rejectionCodes (result: Result) =
        match result.Json["rejections"] with
        | :? JsonArray as array -> array |> Seq.map (fun node -> text node["code"]) |> Seq.toList
        | _ -> []

    let private continuity clone (id: string) (extra: string list) =
        let view = run clone None ([ "work"; "context"; id ] @ extra) |> ok
        (view.Json["continuity"] :?> JsonArray)[0] :?> JsonObject

    let tests =
        [ { Name = "cli checkpoint: a pushed clean HEAD is recorded with the exact remote commit and a stable JSON contract"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      start clone agentA "FEAT-1"
                      GitFixture.write clone "src/feature.txt" "one\n"
                      let sha = pushAll clone "feature work"
                      let result = checkpoint clone agentA "FEAT-1" [] |> ok
                      let document = result.Json
                      Assert.equal "recorded" (text document["status"])
                      let recorded = document["checkpoint"]
                      Assert.equal sha (text recorded["commit"])
                      Assert.equal sha (text recorded["remoteCommit"])
                      Assert.equal "feature/x" (text recorded["branch"])
                      Assert.equal "feature/x" (text recorded["remoteBranch"])
                      Assert.equal "origin" (text recorded["remote"].["name"])
                      Assert.equal "verified" (text recorded["status"])
                      Assert.equal "git-remote-observation" (text recorded["verification"].["mechanism"])
                      Assert.isTrue ((text recorded["executionId"]).StartsWith "EXE-") "no execution"
                      Assert.equal "src/feature.txt" (text document["paths"].[0])
                      let block = continuity clone "FEAT-1" []
                      Assert.equal "current" (text block["freshness"])
                      Assert.equal "at-remote-head" (text block["checkpoint"].["currentRecoverability"].["status"])
                      Assert.equal sha (text block["checkpoint"].["commit"])
                      Assert.equal "Write the next slice" (text block["checkpoint"].["nextAction"])
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "cli checkpoint: a missing work item, a blocked item and an executor with no execution are distinct refusals"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      let missing = checkpoint clone agentA "FEAT-404" []
                      Assert.equal 1 missing.ExitCode
                      Assert.equal [ "work-item-not-found" ] (rejectionCodes missing)
                      start clone agentA "FEAT-1"
                      // Another executor has no execution of its own.
                      Assert.equal [ "missing-execution" ] (rejectionCodes (checkpoint clone agentB "FEAT-1" []))
                      run clone (Some agentA) [ "work"; "block"; "--id"; "FEAT-1"; "--reason"; "waiting"; "--occurred-at"; now () ] |> ok |> ignore
                      Assert.equal [ "work-item-not-active" ] (rejectionCodes (checkpoint clone agentA "FEAT-1" []))) }
          { Name = "cli checkpoint: blank text is an argument error (exit 2); a missing flag is a usage error"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      start clone agentA "FEAT-1"

                      let blank =
                          run clone (Some agentA) [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "   "; "--next-action"; "x"; "--json" ]

                      Assert.equal 2 blank.ExitCode
                      Assert.equal [ "blank-summary" ] (rejectionCodes blank)
                      let missingFlag = run clone (Some agentA) [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "x" ]
                      Assert.equal 2 missingFlag.ExitCode
                      Assert.isTrue (missingFlag.Error.Contains "--next-action") missingFlag.Error) }
          { Name = "cli checkpoint: an unpushed commit and dirty work are refused; nothing is recorded"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      start clone agentA "FEAT-1"
                      GitFixture.write clone "src/feature.txt" "one\n"
                      GitFixture.commitAll clone "not pushed" |> ignore
                      Assert.equal [ "local-ahead" ] (rejectionCodes (checkpoint clone agentA "FEAT-1" []))
                      GitFixture.git clone [ "push"; "-q" ] |> ignore
                      GitFixture.write clone "src/feature.txt" "two\n"
                      Assert.equal [ "uncommitted-changes" ] (rejectionCodes (checkpoint clone agentA "FEAT-1" []))
                      let events = File.ReadAllText(Path.Combine(clone, ".ros", "events", "events.jsonl"))
                      Assert.isTrue (not (events.Contains "work.checkpointed")) "a refused checkpoint was recorded") }
          { Name = "cli checkpoint: a step never started is refused; a started step of the caller's execution is linked"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      start clone agentA "FEAT-1"
                      Assert.equal [ "invalid-step" ] (rejectionCodes (checkpoint clone agentA "FEAT-1" [ "--step"; "impl-1" ]))
                      run clone (Some agentA) [ "telemetry"; "step"; "start"; "FEAT-1"; "--step"; "impl-1"; "--occurred-at"; now () ] |> ok |> ignore
                      let result = checkpoint clone agentA "FEAT-1" [ "--step"; "impl-1" ] |> ok
                      Assert.equal "impl-1" (text result.Json["checkpoint"].["stepId"])) }
          { Name = "cli context/status: commits after the checkpoint and uncommitted work after it are reported as distinct facts"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      start clone agentA "FEAT-1"
                      let status = run clone None [ "status" ] |> ok
                      let warnings = status.Json["continuity"].["warnings"] :?> JsonArray
                      Assert.isTrue (warnings |> Seq.exists (fun w -> text w["code"] = "no-durable-checkpoint")) "no-checkpoint warning missing"
                      checkpoint clone agentA "FEAT-1" [] |> ok |> ignore
                      GitFixture.write clone "src/dirty.txt" "dirty\n"
                      let dirty = continuity clone "FEAT-1" []
                      Assert.equal "uncommitted-changes-after-checkpoint" (text dirty["freshness"])
                      let statusDirty = run clone None [ "status" ] |> ok
                      let dirtyCodes = statusDirty.Json["continuity"].["warnings"] :?> JsonArray |> Seq.map (fun w -> text w["code"]) |> Seq.toList
                      GitFixture.commitAll clone "after checkpoint 1" |> ignore
                      GitFixture.write clone "src/more2.txt" "more\n"
                      GitFixture.commitAll clone "after checkpoint 2" |> ignore
                      let after = continuity clone "FEAT-1" []
                      Assert.equal "commits-after-checkpoint" (text after["freshness"])
                      Assert.equal 2 (after["local"].["commitsAfter"].GetValue<int>())
                      let statusAfter = run clone None [ "status" ] |> ok
                      let codes = statusAfter.Json["continuity"].["warnings"] :?> JsonArray |> Seq.map (fun w -> text w["code"]) |> Seq.toList
                      Assert.isTrue (List.contains "commits-after-checkpoint" codes) $"{codes}"
                      Assert.isTrue (List.contains "uncommitted-work-after-checkpoint" dirtyCodes) $"{dirtyCodes}") }
          { Name = "cli context: a later force-push leaves the historical checkpoint intact and reports it no longer verifiable"
            Run =
              fun () ->
                  withRepository (fun parent _ clone ->
                      start clone agentA "FEAT-1"
                      GitFixture.write clone "src/feature.txt" "one\n"
                      let sha = pushAll clone "feature work"
                      checkpoint clone agentA "FEAT-1" [] |> ok |> ignore
                      // Someone else rewrites the branch.
                      let other = Path.Combine(parent, "rewriter")
                      GitFixture.git parent [ "clone"; "-q"; "--branch"; "feature/x"; Path.Combine(parent, "remote.git"); other ] |> ignore
                      GitFixture.configureIdentity other
                      GitFixture.git other [ "reset"; "-q"; "--hard"; "HEAD~1" ] |> ignore
                      GitFixture.write other "src/other.txt" "other\n"
                      GitFixture.commitAll other "rewritten history" |> ignore
                      GitFixture.git other [ "push"; "-q"; "--force"; "origin"; "feature/x" ] |> ignore
                      GitFixture.git clone [ "fetch"; "-q" ] |> ignore
                      let block = continuity clone "FEAT-1" []
                      Assert.equal "checkpoint-no-longer-currently-verifiable" (text block["freshness"])
                      Assert.equal "not-contained" (text block["checkpoint"].["currentRecoverability"].["status"])
                      Assert.equal sha (text block["checkpoint"].["remoteCommit"])
                      Assert.equal "verified" (text block["checkpoint"].["verification"].["status"])
                      let shown = run clone None [ "work"; "checkpoint"; "show"; "FEAT-1"; "--json" ] |> ok
                      Assert.equal 1 (shown.Json["history"] :?> JsonArray).Count
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "cli status --offline never contacts the remote and reports its state as unknown, not current"
            Run =
              fun () ->
                  withRepository (fun parent _ clone ->
                      start clone agentA "FEAT-1"
                      checkpoint clone agentA "FEAT-1" [] |> ok |> ignore
                      GitFixture.git clone [ "remote"; "set-url"; "origin"; Path.Combine(parent, "gone.git") ] |> ignore
                      let online = continuity clone "FEAT-1" []
                      Assert.equal "remote-unavailable" (text online["freshness"])
                      let offline = continuity clone "FEAT-1" [ "--offline" ]
                      Assert.equal "unknown" (text offline["freshness"])
                      let status = run clone None [ "status"; "--offline" ] |> ok
                      Assert.equal false (status.Json["continuity"].["remoteObserved"].GetValue<bool>())) }
          { Name = "cli context --text shows the latest recoverable checkpoint block for people"
            Run =
              fun () ->
                  withRepository (fun _ _ clone ->
                      start clone agentA "FEAT-1"
                      let sha = pushAll clone "work" |> fun _ -> GitFixture.git clone [ "rev-parse"; "HEAD" ]
                      checkpoint clone agentA "FEAT-1" [] |> ok |> ignore
                      let shown = run clone None [ "work"; "context"; "FEAT-1"; "--text" ] |> ok
                      Assert.isTrue (shown.Output.Contains "LATEST RECOVERABLE CHECKPOINT") shown.Output
                      Assert.isTrue (shown.Output.Contains $"Commit:         {sha}") shown.Output
                      Assert.isTrue (shown.Output.Contains "Next action:") shown.Output
                      Assert.isTrue (shown.Output.Contains "Freshness:              current") shown.Output) } ]
