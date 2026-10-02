namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work

/// Post-hoc attribution reconciliation end to end (issue #80): the real
/// `praxis` CLI against real temporary Git repositories, exercising Git
/// evidence, the durable event, idempotency, conflicts, and how `work
/// validate` and the unified `validate` treat reconciled paths afterwards.
[<RequireQualifiedAccess>]
module WorkReconciliationEffectTests =
    let private cli = Path.Combine(AppContext.BaseDirectory, "praxis.dll")

    let private identityVariables =
        [ "CLAUDE_CODE_SESSION_ID"; "CODEX_SESSION_ID"; "CODEX_THREAD_ID"; "GEMINI_SESSION_ID"; "COPILOT_SESSION_ID"
          "GITHUB_ACTIONS"; "GITHUB_RUN_ID"; "OLLAMA_HOST"; "ROS_ACTOR"; "ROS_ACTOR_KIND"; "ROS_TELEMETRY_PROVIDER"
          "ROS_TELEMETRY_RUNTIME"; "ROS_TELEMETRY_MODEL"; "ROS_TELEMETRY_MODEL_VERSION"; "ROS_TELEMETRY_RUNTIME_VERSION"
          "ROS_TELEMETRY_SESSION_ID"; "ROS_TELEMETRY_CONVERSATION_ID"; "ROS_TELEMETRY_RUN_ID"; "ROS_BASE_REF" ]

    type private Run =
        { Exit: int
          Out: string
          Err: string }

    let private run (fileName: string) (arguments: string list) (environment: (string * string) list) =
        let startInfo = ProcessStartInfo(fileName)
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        arguments |> List.iter startInfo.ArgumentList.Add
        identityVariables |> List.iter (startInfo.Environment.Remove >> ignore)
        environment |> List.iter (fun (name, value) -> startInfo.Environment[name] <- value)
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        child.WaitForExit()

        { Exit = child.ExitCode
          Out = output.Result
          Err = error.Result }

    let private git root (arguments: string list) =
        let result = run "git" ([ "-C"; root ] @ arguments) []

        if result.Exit <> 0 then
            failwith $"git {String.Join(' ', arguments)} failed: {result.Err}"

        result.Out.Trim()

    let private ros root (environment: (string * string) list) (arguments: string list) =
        run "dotnet" ([ cli; "--root"; root ] @ arguments) environment

    let private parse (text: string) = JsonNode.Parse text

    let private now () = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    let private human = [ "--actor-kind"; "human"; "--actor"; "kevin" ]

    let private reconcile root (arguments: string list) =
        ros root [] ([ "work"; "reconcile"; "--occurred-at"; now () ] @ arguments @ human)

    let private write root (relativePath: string) (content: string) =
        let path = Path.Combine(root, relativePath)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let private commitAll root (message: string) (authorName: string) =
        git root [ "add"; "-A" ] |> ignore

        git
            root
            [ "-c"
              $"user.name={authorName}"
              "-c"
              $"user.email={authorName.ToLowerInvariant()}@example.invalid"
              "commit"
              "-qm"
              message ]
        |> ignore

        git root [ "rev-parse"; "HEAD" ]

    let private config =
        """{
  "repository": { "id": "reconcile-test" },
  "workProtocol": {
    "version": "1.0.0",
    "enforceAttribution": true,
    "meaningfulPaths": ["**"],
    "ignoredPaths": [".git/**", ".ros/context/**", ".ros/events/**", ".ros/work/**", ".ros/telemetry/**", ".ros/locks/**", "generated/**"]
  },
  "telemetry": { "enabled": false, "disabledReason": "reconciliation tests" }
}
"""

    let private context =
        """{
  "schemaVersion": "1.0.0",
  "repository": "reconcile-test",
  "workItems": [
    { "id": "FEAT-1", "type": "feature", "state": "complete", "semanticState": "complete", "evidence": [] },
    { "id": "FEAT-2", "type": "feature", "state": "complete", "semanticState": "complete", "evidence": [] }
  ],
  "baselineDirtyPaths": []
}
"""

    let private queue =
        """{
  "schemaVersion": "1.0.0",
  "nextSeq": 3,
  "items": [
    { "id": "WI-0001", "title": "ready item", "status": "ready", "tags": [], "priority": null, "attachments": [] },
    { "id": "WI-0002", "title": "abandoned item", "status": "abandoned", "tags": [], "priority": null, "attachments": [] }
  ]
}
"""

    /// A contemporaneous `work.completed` event attributing `src/owned.fs`.
    let private completedEvent =
        """{"schemaVersion":"1.0.0","type":"work.completed","workItem":"FEAT-2","repository":"reconcile-test","protocolVersion":"1.0.0","occurredAt":"2026-09-01T00:00:00.000Z","evidence":[],"paths":["src/owned.fs"],"telemetryExecutions":[],"publication":{"status":"pending"},"eventId":"000000000000000000000001"}"""

    /// A repository with Praxis state and a first commit; returns the root and
    /// the baseline commit.
    let private withRepository (test: string -> string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-reconcile-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            write root "ros.json" config
            write root ".ros/context/current.json" context
            write root ".ros/work/queue.json" queue
            write root ".ros/events/events.jsonl" (completedEvent + "\n")
            write root "README.md" "baseline\n"
            git root [ "init"; "-q"; "-b"; "main" ] |> ignore
            git root [ "config"; "commit.gpgsign"; "false" ] |> ignore
            let baseline = commitAll root "baseline" "Baseline"
            test root baseline
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ()

    let private events root =
        File.ReadAllLines(Path.Combine(root, ".ros", "events", "events.jsonl"))
        |> Array.filter (fun line -> line.Trim().Length > 0)
        |> Array.map (fun line -> JsonNode.Parse(line).AsObject())
        |> Array.toList

    let private reconciliations root =
        events root |> List.filter (fun event -> event["type"].GetValue<string>() = "work.attribution.reconciled")

    let private strings (node: JsonNode) =
        node.AsArray() |> Seq.map (fun entry -> entry.GetValue<string>()) |> Seq.toList

    /// `work validate --json` findings as `(path, field)` pairs, with the
    /// committed range since `baseRef` included the way CI includes it.
    let private workFindings root (baseRef: string) =
        let result = ros root [ "ROS_BASE_REF", baseRef ] [ "work"; "validate"; "--json" ]
        let findings = (parse result.Out).["findings"].AsArray()
        findings |> Seq.map (fun finding -> finding["path"].GetValue<string>(), finding["field"].GetValue<string>()) |> Seq.toList

    let private assertSucceeded (result: Run) =
        if result.Exit <> 0 then
            failwith $"expected exit 0, got {result.Exit}\nstdout: {result.Out}\nstderr: {result.Err}"

    let private assertExit (expected: int) (result: Run) =
        if result.Exit <> expected then
            failwith $"expected exit {expected}, got {result.Exit}\nstdout: {result.Out}\nstderr: {result.Err}"

    let private assertContains (fragment: string) (text: string) =
        Assert.isTrue (text.Contains(fragment, StringComparison.Ordinal)) $"expected '{fragment}' in:\n{text}"

    let private changesOf (event: JsonObject) =
        event["gitEvidence"].["commits"].AsArray()
        |> Seq.collect (fun commit -> commit["changes"].AsArray())
        |> Seq.map (fun change -> change["status"].GetValue<string>(), change["path"].GetValue<string>())
        |> Seq.toList

    let private symlink root (relativePath: string) (target: string) =
        let path = Path.Combine(root, relativePath)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.Delete path
        File.CreateSymbolicLink(path, target) |> ignore

    /// A separate repository with two commits, for use as a submodule;
    /// returns its path and both commits, oldest first.
    let private withSubmoduleSource (test: string -> string * string -> unit) =
        let source = Path.Combine(Path.GetTempPath(), $"ros-reconcile-sub-{Guid.NewGuid():N}")
        Directory.CreateDirectory source |> ignore

        try
            git source [ "init"; "-q"; "-b"; "main" ] |> ignore
            git source [ "config"; "commit.gpgsign"; "false" ] |> ignore
            write source "lib.fs" "one\n"
            let first = commitAll source "one" "Carol"
            write source "lib.fs" "two\n"
            let second = commitAll source "two" "Carol"
            git source [ "checkout"; "-q"; first ] |> ignore
            test source (first, second)
        finally
            try
                Directory.Delete(source, true)
            with _ ->
                ()

    /// Adds `source` as a submodule at `relativePath`, checked out at `commit`.
    let private addSubmodule root (source: string) (relativePath: string) (commit: string) =
        git root [ "-c"; "protocol.file.allow=always"; "submodule"; "add"; "-q"; source; relativePath ] |> ignore
        git (Path.Combine(root, relativePath)) [ "checkout"; "-q"; commit ] |> ignore

    /// The object id Git stores for `path` in commit `revision`.
    let private storedId root (revision: string) (path: string) = git root [ "rev-parse"; $"{revision}:{path}" ]

    let tests =
        [ { Name = "reconcile: an unattributed committed file is detected, reconciled, and then validates"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/a.fs" "a\n"
                      write root "src/b.fs" "b\n"
                      let work = commitAll root "work done without a work item" "Alice"
                      Assert.equal [ "src/a.fs", "work_items"; "src/b.fs", "work_items" ] (workFindings root baseline)

                      let result = reconcile root [ "--id"; "FEAT-1"; "--reason"; "agent forgot to begin work"; "--commit"; work ]
                      assertSucceeded result
                      assertContains "reconciled 2 path(s) to FEAT-1 as post-hoc attribution" result.Out

                      let event = Assert.single (reconciliations root)
                      Assert.equal "FEAT-1" (event["workItem"].GetValue<string>())
                      Assert.equal "post-hoc" (event["attribution"].GetValue<string>())
                      Assert.equal [ "src/a.fs"; "src/b.fs" ] (strings event["paths"])
                      Assert.equal work (event["gitEvidence"].["head"].GetValue<string>())
                      Assert.equal [ work ] (event["gitEvidence"].["commits"].AsArray() |> Seq.map (fun c -> c["sha"].GetValue<string>()) |> Seq.toList)
                      Assert.empty (workFindings root baseline)) }
          { Name = "reconcile: a later invocation of the unified validate recognizes the reconciled path"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/a.fs" "a\n"
                      let work = commitAll root "work" "Alice"
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work ])
                      let result = ros root [ "ROS_BASE_REF", baseline ] [ "validate"; "--json" ]
                      let findings = (parse result.Out).["findings"].AsArray() |> Seq.map (fun f -> f["path"].GetValue<string>()) |> Seq.toList
                      Assert.isTrue (not (List.contains "src/a.fs" findings)) $"src/a.fs should be attributed: {result.Out}") }
          { Name = "reconcile: an unrelated unattributed path in another commit still fails validation"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/a.fs" "a\n"
                      let first = commitAll root "first" "Alice"
                      write root "src/unrelated.fs" "u\n"
                      commitAll root "unrelated" "Bob" |> ignore
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; first ])
                      Assert.equal [ "src/unrelated.fs", "work_items" ] (workFindings root baseline)) }
          { Name = "reconcile: ignored and generated paths create no reconciliation requirement and no event"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "generated/bundle.js" "x\n"
                      let generated = commitAll root "regenerate" "Alice"
                      Assert.empty (workFindings root baseline)
                      let result = reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; generated; "--json" ]
                      assertSucceeded result
                      Assert.equal "nothing-to-reconcile" ((parse result.Out).["status"].GetValue<string>())
                      Assert.empty (reconciliations root)) }
          { Name = "reconcile: added, modified, deleted, and renamed files are all attributed, renames on both sides"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/keep.fs" "keep\n"
                      write root "src/drop.fs" "drop\n"
                      write root "src/move.fs" "a stable body long enough for rename detection\nline 2\nline 3\n"
                      commitAll root "seed" "Alice" |> ignore
                      write root "src/keep.fs" "keep changed\n"
                      File.Delete(Path.Combine(root, "src", "drop.fs"))
                      git root [ "mv"; "src/move.fs"; "src/moved.fs" ] |> ignore
                      write root "src/new.fs" "new\n"
                      let work = commitAll root "mixed changes" "Alice"
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work ])
                      let event = reconciliations root |> List.last

                      Assert.equal
                          [ "deleted", "src/drop.fs"
                            "modified", "src/keep.fs"
                            "renamed", "src/moved.fs"
                            "added", "src/new.fs" ]
                          (changesOf event |> List.sortBy snd)

                      Assert.equal [ "src/drop.fs"; "src/keep.fs"; "src/move.fs"; "src/moved.fs"; "src/new.fs" ] (strings event["paths"])
                      // The net committed diff since the baseline (keep, moved, new) is now fully attributed.
                      Assert.empty (workFindings root baseline)) }
          { Name = "reconcile: a range reconciles several commits and several --commit selectors combine"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/a.fs" "a\n"
                      let first = commitAll root "one" "Alice"
                      write root "src/b.fs" "b\n"
                      commitAll root "two" "Bob" |> ignore
                      write root "src/c.fs" "c\n"
                      let third = commitAll root "three" "Alice"
                      let result = reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--range"; $"{baseline}..{third}" ]
                      assertSucceeded result
                      let event = Assert.single (reconciliations root)
                      Assert.equal [ "src/a.fs"; "src/b.fs"; "src/c.fs" ] (strings event["paths"])
                      Assert.equal 3 (event["gitEvidence"].["commits"].AsArray().Count)
                      let selector = event["gitEvidence"].["selectors"].[0]
                      Assert.equal "range" (selector["type"].GetValue<string>())
                      Assert.equal baseline (selector["base"].GetValue<string>())
                      Assert.equal first (strings selector["commits"] |> List.head)
                      Assert.empty (workFindings root baseline)) }
          { Name = "reconcile: a partially attributed commit reconciles only the unattributed paths"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/owned.fs" "owned\n"
                      write root "src/orphan.fs" "orphan\n"
                      let work = commitAll root "partly attributed" "Alice"
                      let result = reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work; "--json" ]
                      assertSucceeded result
                      let output = JsonNode.Parse(result.Out)
                      Assert.equal [ "src/orphan.fs" ] (strings output["paths"])

                      let owned =
                          output["assessments"].AsArray() |> Seq.find (fun a -> a["path"].GetValue<string>() = "src/owned.fs")

                      Assert.equal "already-attributed" (owned["disposition"].GetValue<string>())
                      Assert.equal [ "added", "src/orphan.fs" ] (changesOf (Assert.single (reconciliations root)))
                      Assert.empty (workFindings root baseline)) }
          { Name = "reconcile: a nonexistent or abandoned work item is rejected and nothing is recorded"
            Run =
              fun () ->
                  withRepository (fun root _ ->
                      write root "src/a.fs" "a\n"
                      let work = commitAll root "work" "Alice"
                      let missing = reconcile root [ "--id"; "FEAT-404"; "--reason"; "recovery"; "--commit"; work ]
                      assertExit 1 missing
                      assertContains "does not exist" missing.Err
                      let abandoned = reconcile root [ "--id"; "WI-0002"; "--reason"; "recovery"; "--commit"; work ]
                      assertExit 1 abandoned
                      assertContains "abandoned" abandoned.Err
                      Assert.empty (reconciliations root)
                      assertSucceeded (reconcile root [ "--id"; "WI-0001"; "--reason"; "recovery"; "--commit"; work ])) }
          { Name = "reconcile: invalid refs and ranges fail closed; malformed arguments are argument errors"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/a.fs" "a\n"
                      commitAll root "work" "Alice" |> ignore
                      let missing = reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; "no-such-ref" ]
                      assertExit 1 missing
                      assertContains "does not resolve to a commit" missing.Err
                      assertExit 1 (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--range"; "HEAD..HEAD" ])
                      assertExit 2 (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--range"; $"{baseline}...HEAD" ])
                      assertExit 2 (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; "--all" ])
                      assertExit 2 (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery" ])
                      assertExit 2 (reconcile root [ "--id"; "FEAT-1"; "--commit"; "HEAD" ])
                      assertExit 2 (reconcile root [ "--id"; "FEAT-1"; "--id"; "FEAT-2"; "--reason"; "r"; "--commit"; "HEAD" ])
                      assertExit 2 (ros root [] [ "work"; "reconcile"; "--id"; "FEAT-1"; "--reason"; "r"; "--commit"; "HEAD" ])
                      let outside = reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; "HEAD"; "--path"; "src/other.fs" ]
                      assertExit 1 outside
                      assertContains "only paths Git proves were changed" outside.Err
                      Assert.empty (reconciliations root)) }
          { Name = "reconcile: Git being unavailable fails closed"
            Run =
              fun () ->
                  let root = Path.Combine(Path.GetTempPath(), $"ros-reconcile-nogit-{Guid.NewGuid():N}")
                  Directory.CreateDirectory root |> ignore

                  try
                      write root "ros.json" config
                      write root ".ros/context/current.json" context
                      let result = reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; "HEAD" ]
                      assertExit 1 result
                      assertContains "unavailable" result.Err

                      let command: ReconciliationCommand =
                          { WorkItemId = "FEAT-1"
                            Reason = "recovery"
                            Selectors = [ GitEvidenceSelector.Commit "HEAD" ]
                            PathRestriction = []
                            OccurredAt = now ()
                            Actor =
                              { Kind = ActorKind.Human
                                Id = "kevin"
                                Provider = None
                                Model = None
                                Runtime = None }
                            DryRun = false }

                      match FileReconciliationRepository.reconcile root (ProcessGitRepository.createHistoryWithExecutable "ros-missing-git-executable" root) command with
                      | Ok(ReconciliationOutcome.Rejected [ ReconciliationRejection.GitUnavailable _ ]) -> ()
                      | other -> failwith $"expected a Git-unavailable rejection, got {other}"

                      Assert.isTrue (not (File.Exists(Path.Combine(root, ".ros", "events", "events.jsonl")))) "nothing may be recorded"
                  finally
                      Directory.Delete(root, true) }
          { Name = "reconcile: ambiguous evidence (ambiguous ref, merge, foreign branch, unrelated base) fails closed"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      git root [ "checkout"; "-qb"; "side" ] |> ignore
                      write root "src/side.fs" "side\n"
                      let side = commitAll root "side work" "Bob"
                      git root [ "checkout"; "-q"; "main" ] |> ignore
                      write root "src/main.fs" "main\n"
                      let main = commitAll root "main work" "Alice"

                      let foreign = reconcile root [ "--id"; "FEAT-1"; "--reason"; "r"; "--commit"; side ]
                      assertExit 1 foreign
                      assertContains "not in the history of HEAD" foreign.Err

                      git root [ "tag"; "main-work"; baseline ] |> ignore
                      git root [ "branch"; "main-work"; main ] |> ignore
                      let ambiguous = reconcile root [ "--id"; "FEAT-1"; "--reason"; "r"; "--commit"; "main-work" ]
                      assertExit 1 ambiguous
                      assertContains "ambiguous" ambiguous.Err

                      git root [ "-c"; "user.name=Merger"; "-c"; "user.email=m@example.invalid"; "merge"; "-q"; "--no-ff"; "-m"; "merge side"; "side" ] |> ignore
                      let merge = git root [ "rev-parse"; "HEAD" ]
                      let mergeResult = reconcile root [ "--id"; "FEAT-1"; "--reason"; "r"; "--commit"; merge ]
                      assertExit 1 mergeResult
                      assertContains "is a merge" mergeResult.Err
                      assertExit 1 (reconcile root [ "--id"; "FEAT-1"; "--reason"; "r"; "--range"; $"{baseline}..HEAD" ])

                      let unrelatedBase = reconcile root [ "--id"; "FEAT-1"; "--reason"; "r"; "--range"; $"{side}..{main}" ]
                      assertExit 1 unrelatedBase
                      assertContains "not an ancestor" unrelatedBase.Err
                      Assert.empty (reconciliations root)
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "r"; "--commit"; main; "--commit"; side ])) }
          { Name = "reconcile: repeating a reconciliation records nothing new"
            Run =
              fun () ->
                  withRepository (fun root _ ->
                      write root "src/a.fs" "a\n"
                      let work = commitAll root "work" "Alice"
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work ])
                      let before = File.ReadAllText(Path.Combine(root, ".ros", "events", "events.jsonl"))
                      let again = reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery again"; "--commit"; work; "--json" ]
                      assertSucceeded again
                      let output = (parse again.Out)
                      Assert.equal "nothing-to-reconcile" (output["status"].GetValue<string>())
                      Assert.equal "already-reconciled" (output["assessments"].[0].["disposition"].GetValue<string>())
                      Assert.equal before (File.ReadAllText(Path.Combine(root, ".ros", "events", "events.jsonl")))) }
          { Name = "reconcile: another work item claiming the same change is a conflict and is not recorded"
            Run =
              fun () ->
                  withRepository (fun root _ ->
                      write root "src/a.fs" "a\n"
                      let work = commitAll root "work" "Alice"
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work ])
                      let conflict = reconcile root [ "--id"; "FEAT-2"; "--reason"; "mine"; "--commit"; work ]
                      assertExit 1 conflict
                      assertContains "already reconciled to work item 'FEAT-1'" conflict.Err
                      Assert.equal 1 (reconciliations root).Length) }
          { Name = "reconcile: a dry run reports the plan and records nothing"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/a.fs" "a\n"
                      let work = commitAll root "work" "Alice"
                      let result = reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work; "--dry-run"; "--json" ]
                      assertSucceeded result
                      Assert.equal "planned" ((parse result.Out).["status"].GetValue<string>())
                      Assert.empty (reconciliations root)
                      Assert.equal [ "src/a.fs", "work_items" ] (workFindings root baseline)) }
          { Name = "reconcile: the reconciliation actor is recorded separately from the Git change author"
            Run =
              fun () ->
                  withRepository (fun root _ ->
                      write root "src/a.fs" "a\n"
                      let work = commitAll root "work" "Alice"
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work ])
                      let event = Assert.single (reconciliations root)
                      Assert.equal "human" (event["actor"].["kind"].GetValue<string>())
                      Assert.equal "kevin" (event["actor"].["id"].GetValue<string>())
                      let commit = event["gitEvidence"].["commits"].[0]
                      Assert.equal "Alice" (commit["author"].["name"].GetValue<string>())
                      Assert.equal "alice@example.invalid" (commit["committer"].["email"].GetValue<string>())) }
          { Name = "reconcile: post-hoc attribution stays distinguishable and the original history is untouched"
            Run =
              fun () ->
                  withRepository (fun root _ ->
                      write root "src/a.fs" "a\n"
                      let work = commitAll root "work" "Alice"
                      let before = File.ReadAllText(Path.Combine(root, ".ros", "events", "events.jsonl"))
                      let contextBefore = File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json"))
                      assertSucceeded (reconcile root [ "--id"; "FEAT-2"; "--reason"; "recovery"; "--commit"; work ])
                      let after = File.ReadAllText(Path.Combine(root, ".ros", "events", "events.jsonl"))
                      Assert.isTrue (after.StartsWith before) "reconciliation must only append to the event log"
                      Assert.equal contextBefore (File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json")))
                      let completed = events root |> List.filter (fun e -> e["type"].GetValue<string>() = "work.completed")
                      Assert.equal [ [ "src/owned.fs" ] ] (completed |> List.map (fun e -> strings e["paths"]))
                      let event = Assert.single (reconciliations root)
                      Assert.equal "post-hoc" (event["attribution"].GetValue<string>())) }
          { Name = "reconcile: an edited reconciliation event attributes nothing and is reported"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/a.fs" "a\n"
                      write root "src/b.fs" "b\n"
                      let work = commitAll root "work" "Alice"
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work; "--path"; "src/a.fs" ])
                      Assert.equal [ "src/b.fs", "work_items" ] (workFindings root baseline)
                      let path = Path.Combine(root, ".ros", "events", "events.jsonl")
                      File.WriteAllText(path, File.ReadAllText(path).Replace("\"paths\":[\"src/a.fs\"]", "\"paths\":[\"src/a.fs\",\"src/b.fs\"]"))

                      Assert.equal
                          [ ".ros/events/events.jsonl", "work_reconciliation" // its eventId no longer matches its content
                            ".ros/events/events.jsonl", "work_reconciliation" // src/b.fs is not justified by its evidence
                            "src/a.fs", "work_items"
                            "src/b.fs", "work_items" ]
                          (workFindings root baseline |> List.sort)) }
          { Name = "reconcile: malformed event JSON is a validation finding rather than an operational crash"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      let path = Path.Combine(root, ".ros", "events", "events.jsonl")
                      File.AppendAllText(path, "{\n")
                      let result = ros root [ "ROS_BASE_REF", baseline ] [ "work"; "validate"; "--json" ]
                      assertExit 1 result
                      Assert.equal "" result.Err
                      let findings = (parse result.Out).["findings"].AsArray()
                      Assert.isTrue
                          (findings
                           |> Seq.exists (fun finding ->
                               finding["path"].GetValue<string>() = ".ros/events/events.jsonl"
                               && finding["field"].GetValue<string>() = "work_reconciliation"
                               && finding["message"].GetValue<string>().Contains("line 2")))
                          $"missing malformed-event finding: {result.Out}") }
          { Name = "reconcile: reconciling a change never pre-authorizes a later change to the same path"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "src/a.fs" "a\n"
                      let first = commitAll root "first" "Alice"
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; first ])
                      Assert.empty (workFindings root baseline)
                      write root "src/a.fs" "a edited later without a work item\n"
                      Assert.equal [ "src/a.fs", "work_items" ] (workFindings root baseline)
                      let second = commitAll root "second" "Bob"
                      Assert.equal [ "src/a.fs", "work_items" ] (workFindings root baseline)
                      let result = reconcile root [ "--id"; "FEAT-2"; "--reason"; "second recovery"; "--commit"; second; "--json" ]
                      assertSucceeded result
                      Assert.equal [ "src/a.fs" ] (strings (parse result.Out).["paths"])
                      Assert.empty (workFindings root baseline)
                      Assert.equal [ "FEAT-1"; "FEAT-2" ] (reconciliations root |> List.map (fun e -> e["workItem"].GetValue<string>()))) }
          { Name = "reconcile: a shallow-clone boundary commit is refused rather than read as adding the whole tree"
            Run =
              fun () ->
                  withRepository (fun source _ ->
                      write source "src/a.fs" "a\n"
                      commitAll source "one" "Alice" |> ignore
                      write source "src/b.fs" "b\n"
                      commitAll source "two" "Alice" |> ignore
                      let clone = Path.Combine(Path.GetTempPath(), $"ros-reconcile-shallow-{Guid.NewGuid():N}")

                      try
                          run "git" [ "clone"; "-q"; "--depth"; "1"; $"file://{source}"; clone ] [] |> assertSucceeded
                          let result = reconcile clone [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; "HEAD" ]
                          assertExit 1 result
                          assertContains "shallow clone" result.Err
                      finally
                          try
                              Directory.Delete(clone, true)
                          with _ ->
                              ()) }
          { Name = "reconcile: work show lists a work item's post-hoc reconciliations and omits the field otherwise"
            Run =
              fun () ->
                  withRepository (fun root _ ->
                      write root "src/a.fs" "a\n"
                      let work = commitAll root "work" "Alice"
                      Assert.isTrue (isNull (parse (ros root [] [ "work"; "show"; "FEAT-1" ]).Out).["reconciliations"]) "no reconciliations yet"
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work ])
                      let shown = parse (ros root [] [ "work"; "show"; "FEAT-1" ]).Out
                      let entry = shown.["reconciliations"].[0]
                      Assert.equal "post-hoc" (entry["attribution"].GetValue<string>())
                      Assert.equal true (entry["valid"].GetValue<bool>())
                      Assert.equal "kevin" (entry["actor"].["id"].GetValue<string>())
                      Assert.equal "Alice <alice@example.invalid>" (entry["commits"].[0].["author"].GetValue<string>())
                      Assert.isTrue (isNull (parse (ros root [] [ "work"; "show"; "FEAT-2" ]).Out).["reconciliations"]) "FEAT-2 has none") }
          { Name = "reconcile: current path states are the ids Git stores for files, symbolic links and submodules"
            Run =
              fun () ->
                  withRepository (fun root _ ->
                      withSubmoduleSource (fun source (first, second) ->
                          write root "src/file.fs" "file\n"
                          write root "docs/target.txt" "target\n"
                          write root "docs/folder/inner.txt" "inner\n"
                          symlink root "src/link" "../docs/target.txt"
                          symlink root "src/dangling" "no-such-file"
                          symlink root "src/folder-link" "../docs/folder"
                          addSubmodule root source "libs/sub" first
                          let work = commitAll root "mixed kinds" "Alice"
                          Directory.CreateDirectory(Path.Combine(root, "plain")) |> ignore

                          let tracked = [ "src/file.fs"; "src/link"; "src/dangling"; "src/folder-link"; "libs/sub" ]
                          let states = ProcessGitRepository.readPathStates root (tracked @ [ "plain"; "missing.fs" ])

                          tracked
                          |> List.iter (fun path -> Assert.equal (PathContentState.Present(storedId root work path)) states[path])

                          Assert.equal (PathContentState.Present first) states["libs/sub"]
                          Assert.equal PathContentState.Unreadable states["plain"]
                          Assert.equal PathContentState.Absent states["missing.fs"]

                          // Editing the followed file changes the file, not the link.
                          write root "docs/target.txt" "target edited\n"
                          symlink root "src/dangling" "another-missing-file"
                          git (Path.Combine(root, "libs", "sub")) [ "checkout"; "-q"; second ] |> ignore
                          let after = ProcessGitRepository.readPathStates root tracked
                          Assert.equal (PathContentState.Present(storedId root work "src/link")) after["src/link"]
                          Assert.isTrue (after["src/dangling"] <> states["src/dangling"]) "a retargeted link must change state"
                          Assert.equal (PathContentState.Present second) after["libs/sub"]

                          // An uninitialized submodule cannot be read, so it never matches.
                          git root [ "submodule"; "deinit"; "-q"; "--force"; "libs/sub" ] |> ignore
                          let deinitialized = ProcessGitRepository.readPathStates root [ "libs/sub" ]
                          Assert.equal PathContentState.Unreadable deinitialized["libs/sub"])) }
          { Name = "reconcile: a reconciled symbolic link matches while its target is unchanged and fails once retargeted"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      write root "docs/target.txt" "target\n"
                      symlink root "src/link" "../docs/target.txt"
                      let work = commitAll root "add a link" "Alice"
                      Assert.equal [ "docs/target.txt", "work_items"; "src/link", "work_items" ] (workFindings root baseline)
                      assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work ])
                      Assert.empty (workFindings root baseline)

                      // The followed file changing is a change to that file only.
                      write root "docs/target.txt" "target edited later\n"
                      Assert.equal [ "docs/target.txt", "work_items" ] (workFindings root baseline)

                      write root "docs/target.txt" "target\n"
                      Assert.empty (workFindings root baseline)
                      symlink root "src/link" "../docs/other.txt"
                      Assert.equal [ "src/link", "work_items" ] (workFindings root baseline)) }
          { Name = "reconcile: a reconciled submodule matches while its gitlink commit is unchanged and fails once moved"
            Run =
              fun () ->
                  withRepository (fun root baseline ->
                      withSubmoduleSource (fun source (first, second) ->
                          addSubmodule root source "libs/sub" first
                          let work = commitAll root "add a submodule" "Alice"
                          Assert.equal [ ".gitmodules", "work_items"; "libs/sub", "work_items" ] (workFindings root baseline)
                          assertSucceeded (reconcile root [ "--id"; "FEAT-1"; "--reason"; "recovery"; "--commit"; work ])
                          Assert.equal [ "added", ".gitmodules"; "added", "libs/sub" ] (Assert.single (reconciliations root) |> changesOf |> List.sort)
                          Assert.equal first (storedId root work "libs/sub")
                          Assert.empty (workFindings root baseline)

                          let submodule = Path.Combine(root, "libs", "sub")
                          git submodule [ "checkout"; "-q"; second ] |> ignore
                          Assert.equal [ "libs/sub", "work_items" ] (workFindings root baseline)
                          git submodule [ "checkout"; "-q"; first ] |> ignore
                          Assert.empty (workFindings root baseline))) } ]
