namespace Praxis.Tests

open System
open System.Diagnostics
open System.IO
open Praxis.Domain.Planning
open Praxis.Domain.Work
open PlanningFixtures

/// The planner's evidence about the world and about itself
/// (GROUP-PRAXIS-PLANNING-002): observed CI (PRX-PLAN-020), changed paths
/// and historical merge-conflict hotspots in the collision graph (081),
/// cost replay (152), the follow-up of a saved plan (162) and the recorded
/// estimate-error history (170).
module PlanningEvidenceTests =
    let private t name run = { Name = $"planning evidence: {name}"; Run = run }

    let private observation kind =
        { Kind = kind
          Provenance = Provenance.create EvidenceSource.Git "fixture" }

    let private ciItem () =
        { live "ITEM-CI" LiveWorkState.Active with
            Checkpoint = Some(checkpoint "feature" "abcdef0123456789" "Confirm CI runs green, then complete") }

    let private ciReason observations =
        let analysis = analyze [ queued "ITEM-CI" "ready" "2026-09-01T00:00:00Z" ] [ ciItem () ] history observations stateSafe

        (item analysis "ITEM-CI").Dependencies
        |> List.find (fun resolved -> match resolved.Dependency.Target with DependencyTarget.ContinuousIntegration _ -> true | _ -> false)

    let private git (root: string) (arguments: string list) =
        let startInfo = ProcessStartInfo("git", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root)
        arguments |> List.iter startInfo.ArgumentList.Add
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()
        if child.ExitCode <> 0 then failwith $"git {String.Join(' ', arguments)} failed: {error}"
        output.Trim()

    let private context (items: (string * string * string * string) list) =
        let template =
            """{"id":"__ID__","type":"task","state":"active","semanticState":"active","evidence":[],"updatedAt":"2026-09-10T00:00:00.000Z","telemetryExecutionIds":[],
 "latestCheckpoint":{"id":"cp__CP__","recordedAt":"2026-09-10T00:00:00.000Z","executionId":"EXE-20260910T000000000Z-aaaaaaaa","repository":"fixture","branch":"__BRANCH__","commit":"__COMMIT__","remote":{"name":"origin","url":"https://github.com/example/fixture.git"},"remoteBranch":"__BRANCH__","remoteCommit":"__COMMIT__","summary":"done","nextAction":"__NEXT__","verification":{"status":"verified","mechanism":"git-remote-observation"}}}"""

        let entries =
            items
            |> List.map (fun (id, branch, commit, nextAction) ->
                template.Replace("__ID__", id).Replace("__CP__", commit.Substring(0, 22)).Replace("__BRANCH__", branch).Replace("__COMMIT__", commit).Replace("__NEXT__", nextAction))
            |> String.concat ",\n"

        """{"schemaVersion":"1.0.0","repository":"fixture","protocolVersion":"1.0.0","actor":"test","updatedAt":"2026-09-10T00:00:00.000Z","workItems":[__ENTRIES__]}"""
            .Replace("__ENTRIES__", entries)

    let private queue (ids: string list) =
        let template =
            """{"id":"__ID__","title":"__ID__","description":"Deliver __ID__.","tags":["area-__LOWER__"],"priority":"high","status":"ready","createdAt":"2026-09-01T00:00:00.000Z","updatedAt":"2026-09-01T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null}"""

        let entries = ids |> List.map (fun id -> template.Replace("__ID__", id).Replace("__LOWER__", id.ToLowerInvariant())) |> String.concat ",\n"

        """{"schemaVersion":"1.0.0","repository":"fixture","nextSeq":1,"items":[__ENTRIES__]}""".Replace("__ENTRIES__", entries)

    /// main has one merge whose two sides both changed src/hot.fs; branch
    /// feature-x changes src/hot.fs again and feature-y changes src/near.fs.
    let private fixture () =
        let root = CliHarness.temporaryDirectory "praxis-plan-evidence"
        git root [ "init"; "--quiet"; "--initial-branch=main" ] |> ignore
        git root [ "config"; "user.email"; "fixture@example.invalid" ] |> ignore
        git root [ "config"; "user.name"; "Fixture" ] |> ignore
        git root [ "config"; "commit.gpgsign"; "false" ] |> ignore
        git root [ "remote"; "add"; "origin"; "https://github.com/example/fixture.git" ] |> ignore
        CliHarness.write root "src/hot.fs" "one\ntwo\nthree\nfour\nfive\n"
        CliHarness.write root "src/near.fs" "near\n"
        git root [ "add"; "-A" ] |> ignore
        git root [ "commit"; "--quiet"; "-m"; "base" ] |> ignore
        git root [ "checkout"; "--quiet"; "-b"; "side" ] |> ignore
        CliHarness.write root "src/hot.fs" "ONE\ntwo\nthree\nfour\nfive\n"
        git root [ "commit"; "--quiet"; "-am"; "side change" ] |> ignore
        git root [ "checkout"; "--quiet"; "main" ] |> ignore
        CliHarness.write root "src/hot.fs" "one\ntwo\nthree\nfour\nFIVE\n"
        git root [ "commit"; "--quiet"; "-am"; "main change" ] |> ignore
        git root [ "merge"; "--quiet"; "--no-ff"; "-m"; "Merge side"; "side" ] |> ignore
        git root [ "checkout"; "--quiet"; "-b"; "feature-x" ] |> ignore
        CliHarness.write root "src/hot.fs" "one\nTWO\nthree\nfour\nFIVE\n"
        git root [ "commit"; "--quiet"; "-am"; "x" ] |> ignore
        let x = git root [ "rev-parse"; "HEAD" ]
        git root [ "checkout"; "--quiet"; "main" ] |> ignore
        git root [ "checkout"; "--quiet"; "-b"; "feature-y" ] |> ignore
        CliHarness.write root "src/near.fs" "nearer\n"
        git root [ "commit"; "--quiet"; "-am"; "y" ] |> ignore
        let y = git root [ "rev-parse"; "HEAD" ]
        git root [ "checkout"; "--quiet"; "main" ] |> ignore
        CliHarness.write root ".ros/work/queue.json" (queue [ "ITEM-X"; "ITEM-Y" ])
        CliHarness.write root ".ros/context/current.json" (context [ "ITEM-X", "feature-x", x, "Confirm CI runs green, then complete"; "ITEM-Y", "feature-y", y, "Complete it" ])
        git root [ "add"; "-A" ] |> ignore
        git root [ "commit"; "--quiet"; "-m"; "Praxis state" ] |> ignore
        root, x

    /// A `gh` on PATH that records each call and prints `output`, exiting `code`.
    let private fakeGh (output: string) (code: int) =
        let directory = CliHarness.temporaryDirectory "praxis-fake-gh"
        let marker = Path.Combine(directory, "calls.log")
        let script = Path.Combine(directory, "gh")
        File.WriteAllText(script, $"#!/bin/sh\necho \"$*\" >> '{marker}'\nprintf '%%b' '{output}'\nexit {code}\n")
        File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        directory, marker

    let private withPath (directory: string) =
        [ "PATH", directory + string Path.PathSeparator + Environment.GetEnvironmentVariable "PATH" ]

    let tests =
        [ t "check runs: any failure fails, only all finished passing runs pass, otherwise pending" (fun () ->
              Assert.equal (ObservationKind.ContinuousIntegrationFailed "A") (CheckRuns.observation "A" [ "completed", "success"; "completed", "failure" ])
              Assert.equal (ObservationKind.ContinuousIntegrationPassed "A") (CheckRuns.observation "A" [ "completed", "success"; "completed", "skipped" ])
              Assert.equal (ObservationKind.ContinuousIntegrationPending "A") (CheckRuns.observation "A" [ "completed", "success"; "in_progress", "" ])
              Assert.equal (ObservationKind.ContinuousIntegrationPending "A") (CheckRuns.observation "A" []))

          t "CI pending and unavailable stay undetermined and say why; passed and failed resolve (PLAN-020, 021)" (fun () ->
              Assert.equal DependencyStatus.Satisfied (ciReason [ observation (ObservationKind.ContinuousIntegrationPassed "ITEM-CI") ]).Status
              Assert.equal DependencyStatus.Unsatisfied (ciReason [ observation (ObservationKind.ContinuousIntegrationFailed "ITEM-CI") ]).Status
              let pending = ciReason [ observation (ObservationKind.ContinuousIntegrationPending "ITEM-CI") ]
              Assert.equal DependencyStatus.Undetermined pending.Status
              Assert.isTrue (pending.Reason.Contains "still running") pending.Reason
              let unavailable = ciReason [ observation (ObservationKind.ContinuousIntegrationUnavailable("ITEM-CI", "gh is not installed")) ]
              Assert.equal DependencyStatus.Undetermined unavailable.Status
              Assert.isTrue (unavailable.Reason.Contains "unavailable: gh is not installed") unavailable.Reason)

          t "changed paths conflict, shared hotspots are elevated, and both enter the collision fingerprint (PLAN-081)" (fun () ->
              let liveItems = [ live "A" LiveWorkState.Ready; live "B" LiveWorkState.Ready ]
              let queueItems = [ queued "A" "ready" "2026-09-01T00:00:00Z"; queued "B" "ready" "2026-09-02T00:00:00Z" ]

              let collisionOf observations =
                  let analysis = analyze queueItems liveItems history observations stateSafe
                  analysis, analysis.Collisions |> List.find (fun collision -> collision.Left = "A" && collision.Right = "B")

              let baseline, plain = collisionOf []
              Assert.isTrue (plain.Signals |> List.forall (function CollisionSignal.HistoricalConflict _ | CollisionSignal.SharedChangedPath _ -> false | _ -> true)) "no Git path evidence, no path signal"

              let hotspot, near =
                  collisionOf
                      [ observation (ObservationKind.ChangedPaths("A", [ "src/hot.fs" ]))
                        observation (ObservationKind.ChangedPaths("B", [ "src/near.fs" ]))
                        observation (ObservationKind.ContestedPath("src/hot.fs", 3)) ]

              Assert.equal CollisionRisk.Elevated near.Risk
              Assert.isTrue (near.Signals |> List.exists (function CollisionSignal.HistoricalConflict evidence -> evidence.StartsWith "src/hot.fs (3 merge(s)" | _ -> false)) $"%A{near.Signals}"
              Assert.isTrue (baseline.Snapshot.CollisionFingerprint <> hotspot.Snapshot.CollisionFingerprint) "the evidence changes the collision fingerprint"

              let _, same =
                  collisionOf [ observation (ObservationKind.ChangedPaths("A", [ "src/hot.fs" ])); observation (ObservationKind.ChangedPaths("B", [ "src/hot.fs" ])) ]

              Assert.equal CollisionRisk.Conflict same.Risk
              Assert.isTrue (same.Signals |> List.contains (CollisionSignal.SharedChangedPath "src/hot.fs")) $"%A{same.Signals}")

          t "replay compares predicted and observed cost only from earlier evidence (PLAN-151, 152)" (fun () ->
              let costed = history |> List.mapi (fun index execution -> withCost (decimal (index + 1)) execution)
              let report = Replay.replay PlannerConfiguration.defaults [] costed
              let first = report.CostAccuracy.Predictions |> List.find (fun prediction -> prediction.ExecutionId = "EXE-0000")
              Assert.equal 0 first.TrainingSamples
              Assert.equal None first.AbsoluteError
              Assert.equal 24 report.CostAccuracy.Observed
              Assert.isTrue (report.CostAccuracy.Predicted > 0 && report.CostAccuracy.Predicted < 24) $"{report.CostAccuracy.Predicted} predicted"
              Assert.equal (Some "USD") report.CostAccuracy.Currency
              let last = report.CostAccuracy.Predictions |> List.find (fun prediction -> prediction.ExecutionId = "EXE-0023")
              Assert.equal 23 last.TrainingSamples
              Assert.isTrue last.AbsoluteError.IsSome "the last execution is predicted"

              let none = Replay.replay PlannerConfiguration.defaults [] history
              Assert.equal 0 none.CostAccuracy.Observed
              Assert.isTrue (none.CostAccuracy.Statement.Contains "unavailable") none.CostAccuracy.Statement)

          t "calibration history appends once per work state and planner version and reports a trend (PLAN-170)" (fun () ->
              let analysis = analyze [] [] history [] stateSafe
              let report = Replay.replay PlannerConfiguration.defaults [] history
              let entry = Calibration.entry "2026-10-06T00:00:00.000Z" analysis.Snapshot report
              let once, added = Calibration.append [] entry
              let twice, again = Calibration.append once { entry with RecordedAt = "2026-10-07T00:00:00.000Z" }
              Assert.equal true added
              Assert.equal false again
              Assert.equal 1 twice.Length
              let later = { entry with WorkStateFingerprint = "other"; Coverage = Some 0.9m }
              let history2, _ = Calibration.append once later
              let trend = Calibration.trend history2
              Assert.equal 2 trend.Entries
              Assert.equal (Some 0.9m) trend.LatestCoverage
              Assert.isTrue (trend.Statement.Contains "coverage") trend.Statement
              let parsed = Praxis.Contracts.Planning.PlanningJson.parseCalibration "test" (history2 |> List.map Praxis.Contracts.Planning.PlanningJson.calibrationLine)
              Assert.equal (Ok history2) parsed)

          t "a saved plan's follow-up compares start order and durations, as observation only (PLAN-162)" (fun () ->
              let queueItems = [ queued "A" "ready" "2026-09-01T00:00:00Z"; queued "B" "ready" "2026-09-02T00:00:00Z" ]
              let configuration = { stateSafe with Conflicts = [ { Left = "A"; Right = "B"; Reason = "serialize" } ] }
              let analysis = analyze queueItems [] history [] configuration
              let plan = simulate analysis configuration OptimizationObjective.MinimumDuration None
              let document = { SchemaVersion = "1.0.0"; Snapshot = analysis.Snapshot; Plan = plan; Findings = []; Statement = "" }
              let first, second = match scheduled plan with [ a; b ] -> a, b | other -> failwith $"%A{other}"
              let at (hours: float) = DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero).AddHours(hours).ToString("O")

              let run id (hours: float) =
                  { (executed 0 id "development" 30L) with ExecutionId = $"EXE-{id}"; StartedAt = at hours; FinalizedAt = Some(at (hours + 0.5)) }

              let followed = Comparison.followUp document [] [ run first 1.0; run second 2.0 ]
              Assert.equal 1 followed.OrderedPairs
              Assert.equal 1 followed.PairsInRecommendedOrder
              let reversed = Comparison.followUp document [] [ run first 3.0; run second 2.0 ]
              Assert.equal 0 reversed.PairsInRecommendedOrder
              Assert.isTrue (reversed.Statement.Contains "not effects of the plan") reversed.Statement

              let completed = [ { WorkItem = first; RecommendedWave = 1; RecommendedAction = RecommendedAction.Start; LifecycleThen = "ready"; LifecycleNow = "complete"; Outcome = "completed" } ]
              let durations = Comparison.followUp document completed [ run first 1.0 ]
              Assert.equal [ first ] (durations.Durations |> List.map (fun entry -> entry.WorkItem))
              Assert.equal (Some(30L * minute)) (durations.Durations.Head.ObservedMs))

          t "CLI: CI is queried only with --observe-ci, and an unreachable source is reported unavailable (PLAN-020)" (fun () ->
              let root, commit = fixture ()
              let fakePass, passCalls = fakeGh "completed\\tsuccess\\n" 0
              let fakeFail, failCalls = fakeGh "" 1

              try
                  let plain = CliHarness.rosWith root (withPath fakePass) [ "plan"; "analyze"; "--json"; "--as-of"; "2026-10-01T00:00:00Z" ]
                  Assert.equal 0 plain.Exit
                  Assert.isTrue (not (File.Exists passCalls)) "the default plan issues no CI query"
                  Assert.isTrue (plain.Out.Contains "no CI evidence observed") "without CI evidence the dependency is undetermined"

                  let observed = CliHarness.rosWith root (withPath fakePass) [ "plan"; "analyze"; "--json"; "--observe-ci"; "--as-of"; "2026-10-01T00:00:00Z" ]
                  Assert.equal 0 observed.Exit
                  Assert.isTrue ((File.ReadAllText passCalls).Contains $"repos/example/fixture/commits/{commit}/check-runs") (File.ReadAllText passCalls)
                  Assert.isTrue (observed.Out.Contains $"CI passed (github check-runs example/fixture@{commit})") observed.Out
                  Assert.isTrue (plain.Out <> observed.Out) "observed CI changes the plan input"

                  let unavailable = CliHarness.rosWith root (withPath fakeFail) [ "plan"; "analyze"; "--json"; "--observe-ci"; "--as-of"; "2026-10-01T00:00:00Z" ]
                  Assert.equal 0 unavailable.Exit
                  Assert.isTrue (File.Exists failCalls) "the failing source was queried"
                  Assert.isTrue (unavailable.Out.Contains "CI status unavailable") unavailable.Out
              finally
                  CliHarness.removeDirectory root
                  CliHarness.removeDirectory fakePass
                  CliHarness.removeDirectory fakeFail)

          t "CLI: Git shows unmerged checkpoint paths and merge hotspots in the collision graph (PLAN-081)" (fun () ->
              let root, _ = fixture ()

              try
                  let result = CliHarness.rosOk root [ "plan"; "analyze"; "--json"; "--as-of"; "2026-10-01T00:00:00Z" ]
                  let document = CliHarness.json result.Out
                  let items = document["items"].AsArray() |> Seq.map (fun node -> node["id"].GetValue<string>(), node) |> Map.ofSeq
                  Assert.equal "src/hot.fs" (items["ITEM-X"].["changedPaths"].[0].GetValue<string>())
                  Assert.equal "src/hot.fs" (items["ITEM-Y"].["contestedPaths"].[0].["path"].GetValue<string>())
                  Assert.isTrue (result.Out.Contains "historical-conflict") result.Out
              finally
                  CliHarness.removeDirectory root)

          t "CLI: replay --record writes only the calibration history, once per work state; plain replay writes nothing (PLAN-001, 170)" (fun () ->
              let root, _ = fixture ()

              try
                  let status () = git root [ "status"; "--porcelain"; "--untracked-files=all" ]
                  CliHarness.rosOk root [ "plan"; "replay" ] |> ignore
                  Assert.equal "" (status ())
                  let recorded = CliHarness.rosOk root [ "plan"; "replay"; "--record"; "--json" ]
                  Assert.isTrue (recorded.Out.Contains "\"recorded\": true") recorded.Out
                  Assert.equal "?? .ros/planning/calibration.jsonl" (status ())
                  let again = CliHarness.rosOk root [ "plan"; "replay"; "--record" ]
                  Assert.isTrue (again.Out.Contains "already recorded") again.Out
                  Assert.equal 1 (File.ReadAllLines(Path.Combine(root, ".ros/planning/calibration.jsonl")).Length)
              finally
                  CliHarness.removeDirectory root) ]
