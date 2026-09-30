namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes

/// `praxis plan ...` through the real binary against a fixture Git repository.
module PlanningCliTests =
    let private git (root: string) (arguments: string list) =
        let startInfo = ProcessStartInfo("git")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.WorkingDirectory <- root
        arguments |> List.iter startInfo.ArgumentList.Add
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEnd()
        child.StandardError.ReadToEnd() |> ignore
        child.WaitForExit()
        if child.ExitCode <> 0 then failwith $"git {String.Join(' ', arguments)} failed"
        output.Trim()

    let private write (root: string) (relative: string) (content: string) =
        let path = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let private queue =
        """{"schemaVersion":"1.0.0","repository":"plan-fixture","nextSeq":1,"items":[
  {"id":"TASK-A","title":"Task A","description":"First task.","tags":["alpha"],"priority":"high","status":"ready","createdAt":"2026-09-01T00:00:00.000Z","updatedAt":"2026-09-01T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-B","title":"Task B","description":"Second task.","tags":["beta"],"priority":"high","status":"ready","createdAt":"2026-09-02T00:00:00.000Z","updatedAt":"2026-09-02T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null,"dependsOn":["TASK-A"]},
  {"id":"TASK-C","title":"Task C","tags":[],"priority":"low","status":"captured","createdAt":"2026-09-03T00:00:00.000Z","updatedAt":"2026-09-03T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"PRAXIS-REMOTE-16","title":"Keep the conclusion","description":"Fix it.","tags":["remote"],"priority":"high","status":"ready","createdAt":"2026-09-04T00:00:00.000Z","updatedAt":"2026-09-04T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null}
]}"""

    let private context (branch: string) (commit: string) =
        """{"schemaVersion":"1.0.0","repository":"plan-fixture","protocolVersion":"1.0.0","actor":"test","updatedAt":"2026-09-10T00:00:00.000Z","workItems":[
  {"id":"PRAXIS-REMOTE-16","type":"task","state":"active","semanticState":"active","evidence":[],"updatedAt":"2026-09-10T00:00:00.000Z","telemetryExecutionIds":["EXE-20260910T000000000Z-aaaaaaaa"],
   "latestCheckpoint":{"id":"cp0000000000000000000001","recordedAt":"2026-09-10T00:00:00.000Z","executionId":"EXE-20260910T000000000Z-aaaaaaaa","repository":"plan-fixture","branch":"__BRANCH__","commit":"__COMMIT__","remote":{"name":"origin","url":"https://example.invalid/plan-fixture"},"remoteBranch":"__BRANCH__","remoteCommit":"__COMMIT__","summary":"implemented and tested","nextAction":"Confirm CI runs green, then complete","verification":{"status":"verified","mechanism":"git-remote-observation"}}},
  {"id":"GH-84","type":"task","state":"blocked","semanticState":"blocked","evidence":[],"updatedAt":"2026-09-10T00:00:00.000Z","telemetryExecutionIds":[],"blockReason":"awaiting merge of PR #7"}
]}"""
            .Replace("__COMMIT__", commit)
            .Replace("__BRANCH__", branch)

    let private execution (index: int) (minutes: int) =
        let started = DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddHours(float index)
        let finalized = started.AddMinutes(float minutes)

        """{"schemaVersion":"1.0.0","executionId":"EXE-__INDEX__","workItemId":"HIST-__N__","status":"finalized","startedAt":"__STARTED__","finalizedAt":"__FINALIZED__",
  "identity":{"provider":"provider-a","runtime":"runtime-a","model":null},"classification":{"types":["development"]},
  "metrics":[{"id":"time.wall_ms","value":__WALL__,"quality":"derived"},{"id":"time.blocked_ms","value":0,"quality":"derived"}],"links":{"workItemId":"HIST-__N__"}}"""
            .Replace("__INDEX__", index.ToString("D4"))
            .Replace("__N__", string index)
            .Replace("__STARTED__", started.ToString("O"))
            .Replace("__FINALIZED__", finalized.ToString("O"))
            .Replace("__WALL__", string (minutes * 60000))

    /// A committed fixture: PR #7 is merged (squash subject), and the
    /// checkpoint commit of an item still recorded active is on main.
    let private fixtureOn (checkpointBranch: string) =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-plan-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        git root [ "init"; "--quiet"; "--initial-branch=main" ] |> ignore
        git root [ "config"; "user.email"; "fixture@example.invalid" ] |> ignore
        git root [ "config"; "user.name"; "Fixture" ] |> ignore
        git root [ "config"; "commit.gpgsign"; "false" ] |> ignore
        write root "README.md" "fixture\n"
        git root [ "add"; "-A" ] |> ignore
        git root [ "commit"; "--quiet"; "-m"; "Implement the conclusion fix (#7)" ] |> ignore
        let commit = git root [ "rev-parse"; "HEAD" ]
        write root ".ros/work/queue.json" queue
        write root ".ros/context/current.json" (context checkpointBranch commit)

        for index in 0..11 do
            write root $".ros/telemetry/executions/EXE-{index:D4}.json" (execution index (10 + index))

        git root [ "add"; "-A" ] |> ignore
        git root [ "commit"; "--quiet"; "-m"; "Praxis state" ] |> ignore
        root

    let private fixture () = fixtureOn "feature/remote-16"

    /// Every file outside .git, hashed, plus Git's own view of the worktree.
    let private fingerprint (root: string) =
        let files =
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.filter (fun path -> not (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")))
            |> Array.sort
            |> Array.map (fun path -> Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))

        String.Join("\n", files), git root [ "status"; "--porcelain"; "--untracked-files=all" ], git root [ "rev-parse"; "HEAD" ]

    let private json (result: PraxisCli.Result) =
        if result.ExitCode <> 0 then failwith $"exit {result.ExitCode}: {result.Error}"
        result.Json

    let private text (node: JsonNode) = node.GetValue<string>()

    let private itemState (analysis: JsonObject) (id: string) =
        analysis["items"].AsArray()
        |> Seq.find (fun item -> text (item.AsObject()["id"]) = id)
        |> fun item -> text (item.AsObject()["planningState"])

    let private t name run = { Name = $"planning cli: {name}"; Run = run }

    let private groupId (root: string) =
        let groups = PraxisCli.run root None [ "plan"; "groups"; "--json" ] |> json
        text (groups.["groups"].AsArray().[0].["id"])

    /// A planner configuration declaring one group, kept outside the fixture.
    let private declaredConfiguration () =
        let path = Path.Combine(Path.GetTempPath(), $"praxis-plan-config-{Guid.NewGuid():N}.json")

        File.WriteAllText(
            path,
            """{ "grouping": { "groups": [ { "id": "GROUP-FIXTURE-DECLARED-001", "members": ["TASK-A", "TASK-B", "GH-84"],
                 "kind": "shared-area", "sharedContext": ["fixture tasks"], "executionRepository": "plan-fixture",
                 "architectureNotes": ["one parser for every task"] } ] } }"""
        )

        path

    let tests =
        [ t "every command emits the versioned JSON contract" (fun () ->
              let root = fixture ()

              for arguments, kind in
                  [ [ "plan"; "analyze"; "--json" ], "analysis"
                    [ "plan"; "simulate"; "--for"; "speed"; "--json" ], "plan"
                    [ "plan"; "compare"; "--json" ], "comparison"
                    [ "plan"; "explain"; "TASK-B"; "--json" ], "explanation"
                    [ "plan"; "replay"; "--json" ], "replay"
                    [ "plan"; "groups"; "--json" ], "groups"
                    [ "plan"; "simulate"; "--groups"; "--json" ], "group-plan"
                    [ "plan"; "compare"; "--groups"; "--json" ], "group-comparison" ] do
                  let document = PraxisCli.run root None arguments |> json
                  Assert.equal "praxis.plan/1.0.0" (text (document.["schema"]))
                  Assert.equal kind (text (document.["kind"])))

          t "Git evidence surfaces merged work and resolved blockers" (fun () ->
              let root = fixture ()
              let analysis = PraxisCli.run root None [ "plan"; "analyze"; "--json" ] |> json
              Assert.equal "stale-state-candidate" (itemState analysis "PRAXIS-REMOTE-16")
              Assert.equal "stale-state-candidate" (itemState analysis "GH-84")
              Assert.equal "ready" (itemState analysis "TASK-A")
              Assert.equal "captured" (itemState analysis "TASK-C")
              Assert.equal "plan-fixture" (text (analysis.["snapshot"].["repository"])))

          t "a checkpoint on the integration branch itself is not merge evidence" (fun () ->
              let root = fixtureOn "main"
              let analysis = PraxisCli.run root None [ "plan"; "analyze"; "--json" ] |> json
              Assert.equal "partially-complete" (itemState analysis "PRAXIS-REMOTE-16"))

          t "freshness detects a changed work state" (fun () ->
              let root = fixture ()
              let saved = Path.Combine(Path.GetTempPath(), $"praxis-plan-{Guid.NewGuid():N}.json")
              let plan = PraxisCli.run root None [ "plan"; "simulate"; "--json" ]
              Assert.equal 0 plan.ExitCode
              File.WriteAllText(saved, plan.Output)
              Assert.equal 0 (PraxisCli.run root None [ "plan"; "freshness"; "--plan"; saved ]).ExitCode
              write root ".ros/work/queue.json" (queue.Replace("\"status\":\"captured\"", "\"status\":\"ready\""))
              let stale = PraxisCli.run root None [ "plan"; "freshness"; "--plan"; saved; "--json" ]
              Assert.equal 3 stale.ExitCode
              Assert.isTrue (stale.Json["stale"].GetValue<bool>()) "plan must be stale")

          t "invalid arguments are refused" (fun () ->
              let root = fixture ()
              Assert.equal 2 (PraxisCli.run root None [ "plan"; "simulate"; "--for"; "fastest" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "plan"; "simulate"; "--max-concurrency"; "0" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "plan"; "simulate"; "--deadline"; "soon" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "plan"; "explain" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "plan"; "analyze"; "stray" ]).ExitCode
              Assert.equal 1 (PraxisCli.run root None [ "plan"; "explain"; "NOPE" ]).ExitCode)

          t "groups: a dependency pair forms a group that explain-group explains" (fun () ->
              let root = fixture ()
              let groups = PraxisCli.run root None [ "plan"; "groups"; "--json" ] |> json
              let group = groups["groups"].AsArray() |> Seq.exactlyOne
              let members = group["members"].AsArray() |> Seq.map (fun entry -> text (entry["workItem"])) |> Seq.toList
              Assert.equal [ "TASK-A"; "TASK-B" ] members
              Assert.equal "plan-fixture" (text (group["executionRepository"]))
              Assert.equal "one-sequential-agent" (text (group["recommendedExecution"]))
              let id = text (group["id"])
              let explanation = PraxisCli.run root None [ "plan"; "explain-group"; id; "--json" ] |> json
              Assert.equal "group-explanation" (text (explanation["kind"]))
              Assert.isTrue (explanation["evidence"].AsArray().Count > 0) "the explanation names its evidence"
              let output = (PraxisCli.run root None [ "plan"; "explain-group"; id ]).Output
              Assert.isTrue (output.Contains "Why one agent versus several?") "the text explanation answers each question"
              Assert.equal 1 (PraxisCli.run root None [ "plan"; "explain-group"; "GROUP-NOPE-001" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "plan"; "explain-group" ]).ExitCode)

          t "work group show: a declared group with recorded and planning states in text and --json; unknown groups exit 1" (fun () ->
              let root = fixture ()
              let configuration = declaredConfiguration ()
              let show extra = PraxisCli.run root None ([ "work"; "group"; "show" ] @ extra @ [ "--config"; configuration ])
              let document = show [ "GROUP-FIXTURE-DECLARED-001"; "--json" ] |> json
              Assert.equal "declared-group" (text (document["kind"]))
              let members = document["group"].["members"].AsArray() |> Seq.map (fun entry -> text (entry["workItem"]), text (entry["recordedState"])) |> Seq.toList
              Assert.equal [ "GH-84", "blocked"; "TASK-A", "ready"; "TASK-B", "ready" ] (members |> List.sort)
              let planning = document["group"].["members"].AsArray() |> Seq.find (fun entry -> text (entry["workItem"]) = "GH-84")
              Assert.equal "stale-state-candidate" (text (planning["planningState"]))
              Assert.equal 0 (document["blocked"].AsArray().Count)
              Assert.equal "plan-fixture" (text (document["group"].["executionRepository"]))
              Assert.equal "one parser for every task" (text (document["group"].["architectureNotes"].[0]))
              let output = (show [ "GROUP-FIXTURE-DECLARED-001" ]).Output
              Assert.isTrue (output.Contains "GH-84: recorded blocked; planning stale-state-candidate") "the text shows a member's own recorded and planning states"
              Assert.isTrue (output.Contains "Progress: 0 of 3 complete") "the text reports progress"
              Assert.equal 1 (show [ "GROUP-NOPE-001" ]).ExitCode
              Assert.equal 1 (PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-DECLARED-001" ]).ExitCode
              Assert.equal 1 (PraxisCli.run root None [ "work"; "group"; "show"; groupId root ]).ExitCode
              Assert.equal 2 (show []).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "list" ]).ExitCode)

          t "30 no plan or work group command mutates repository state" (fun () ->
              let root = fixture ()
              let before = fingerprint root
              let saved = Path.Combine(Path.GetTempPath(), $"praxis-plan-{Guid.NewGuid():N}.json")
              File.WriteAllText(saved, (PraxisCli.run root None [ "plan"; "simulate"; "--json" ]).Output)

              for arguments in
                  [ [ "plan"; "analyze" ]
                    [ "plan"; "analyze"; "--json" ]
                    [ "plan"; "simulate"; "--for"; "baseline" ]
                    [ "plan"; "simulate"; "--for"; "speed"; "--max-concurrency"; "3" ]
                    [ "plan"; "simulate"; "--for"; "balanced"; "--json" ]
                    [ "plan"; "simulate"; "--for"; "cost" ]
                    [ "plan"; "simulate"; "--for"; "max-parallel" ]
                    [ "plan"; "simulate"; "--budget"; "25" ]
                    [ "plan"; "simulate"; "--deadline"; "4h" ]
                    [ "plan"; "compare" ]
                    [ "plan"; "explain"; "PRAXIS-REMOTE-16" ]
                    [ "plan"; "replay"; "--details" ]
                    [ "plan"; "freshness"; "--plan"; saved ]
                    [ "plan"; "groups" ]
                    [ "plan"; "groups"; "--json" ]
                    [ "plan"; "explain-group"; groupId root ]
                    [ "plan"; "explain-group"; groupId root; "--json" ]
                    [ "plan"; "simulate"; "--groups" ]
                    [ "plan"; "compare"; "--groups"; "--json" ]
                    [ "work"; "group"; "show"; "GROUP-FIXTURE-DECLARED-001"; "--config"; declaredConfiguration () ]
                    [ "work"; "group"; "show"; "GROUP-FIXTURE-DECLARED-001"; "--config"; declaredConfiguration (); "--json" ] ] do
                  let result = PraxisCli.run root None arguments
                  Assert.isTrue (result.ExitCode = 0 || result.ExitCode = 3) $"{String.Join(' ', arguments)} failed: {result.Error}"

              Assert.equal before (fingerprint root)) ]
