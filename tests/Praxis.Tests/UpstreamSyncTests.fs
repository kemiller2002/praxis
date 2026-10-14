namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Git

[<RequireQualifiedAccess>]
module UpstreamSyncTests =
    let private text (node: JsonNode) = node.GetValue<string>()
    let private integer (node: JsonNode) = node.GetValue<int>()
    let private boolean (node: JsonNode) = node.GetValue<bool>()

    let private strings (node: JsonNode) =
        node.AsArray() |> Seq.map text |> Seq.toList

    let private initialize root remote =
        CliHarness.git remote [ "init"; "--bare"; "-q"; "--initial-branch=main" ] |> ignore
        CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        CliHarness.write root "ros.json" """{"workProtocol":{"upstreamSync":{"enabled":true,"remote":"origin","branch":"main","maxAgeMinutes":30}}}"""
        CliHarness.write root "shared.txt" "baseline\n"
        CliHarness.commitAll root "initial"
        CliHarness.git root [ "remote"; "add"; "origin"; remote ] |> ignore
        CliHarness.git root [ "push"; "-q"; "-u"; "origin"; "main" ] |> ignore

    let private clonePeer remote peer =
        let cloned = CliHarness.run "git" [ "clone"; "-q"; remote; peer ] []
        Assert.equal 0 cloned.Exit
        CliHarness.write peer "shared.txt" "upstream\n"
        CliHarness.commitAll peer "upstream change"
        CliHarness.git peer [ "push"; "-q"; "origin"; "main" ] |> ignore

    let tests =
        [ { Name = "upstream sync: thirty minutes of elapsed wall-clock time makes a check due"
            Run =
              fun () ->
                  let now = DateTimeOffset.Parse "2026-10-01T12:30:00Z"
                  let _, _, fresh = UpstreamSync.freshness now (TimeSpan.FromMinutes 30.0) (Some(now - TimeSpan.FromMinutes 29.99))
                  let elapsed, dueAt, stale = UpstreamSync.freshness now (TimeSpan.FromMinutes 30.0) (Some(now - TimeSpan.FromMinutes 30.0))
                  Assert.equal false fresh
                  Assert.equal (Some(TimeSpan.FromMinutes 30.0)) elapsed
                  Assert.equal (Some now) dueAt
                  Assert.equal true stale }

          { Name = "upstream sync: fetch observes drift and exact path overlap without touching the worktree"
            Run =
              fun () ->
                  let parent = CliHarness.temporaryDirectory "praxis-upstream-sync"
                  let root = Path.Combine(parent, "local")
                  let remote = Path.Combine(parent, "remote.git")
                  let peer = Path.Combine(parent, "peer")
                  Directory.CreateDirectory root |> ignore
                  Directory.CreateDirectory remote |> ignore

                  try
                      initialize root remote
                      let first = CliHarness.ros root [ "sync"; "check"; "--start"; "--json" ]
                      Assert.equal 0 first.Exit
                      let initial = JsonNode.Parse first.Out
                      Assert.equal "succeeded" (text initial.["lastAttempt"].["outcome"])
                      Assert.equal false (boolean initial.["freshness"].["stale"])
                      Assert.equal true (boolean initial.["safeForFinalValidation"])
                      let starting = text initial.["commits"].["startingUpstream"]

                      clonePeer remote peer
                      File.WriteAllText(Path.Combine(root, "shared.txt"), "local work\n")
                      let beforeHead = CliHarness.git root [ "rev-parse"; "HEAD" ]
                      let beforeStatus = CliHarness.git root [ "status"; "--porcelain=v1" ]

                      let checkpoint = CliHarness.ros root [ "sync"; "check"; "--json" ]
                      Assert.equal 0 checkpoint.Exit
                      let report = JsonNode.Parse checkpoint.Out
                      Assert.equal "succeeded" (text report.["lastAttempt"].["outcome"])
                      Assert.equal starting (text report.["commits"].["startingUpstream"])
                      Assert.equal true (boolean report.["commits"].["upstreamChangedSinceStart"])
                      Assert.equal 0 (integer report.["relation"].["ahead"])
                      Assert.equal 1 (integer report.["relation"].["behind"])
                      Assert.equal [ "shared.txt" ] (strings report.["paths"].["incomingFromUpstream"])
                      Assert.equal [ "shared.txt" ] (strings report.["paths"].["local"])
                      Assert.equal [ "shared.txt" ] (strings report.["paths"].["overlap"])
                      Assert.equal true (boolean report.["integrationRequired"])
                      Assert.equal false (boolean report.["validationAgainstCurrentUpstream"])
                      Assert.equal beforeHead (CliHarness.git root [ "rev-parse"; "HEAD" ])
                      Assert.equal beforeStatus (CliHarness.git root [ "status"; "--porcelain=v1" ])

                      let statePath = CliHarness.git root [ "rev-parse"; "--git-path"; "praxis/upstream-sync.json" ]
                      let resolvedStatePath = if Path.IsPathRooted statePath then statePath else Path.Combine(root, statePath)
                      Assert.isTrue (File.Exists resolvedStatePath) "sync state must live in Git metadata"
                  finally
                      CliHarness.removeDirectory parent }

          { Name = "upstream sync: a failed fetch preserves the last successful check time"
            Run =
              fun () ->
                  let parent = CliHarness.temporaryDirectory "praxis-upstream-failure"
                  let root = Path.Combine(parent, "local")
                  let remote = Path.Combine(parent, "remote.git")
                  let offline = Path.Combine(parent, "remote-offline.git")
                  Directory.CreateDirectory root |> ignore
                  Directory.CreateDirectory remote |> ignore

                  try
                      initialize root remote
                      let first = CliHarness.ros root [ "sync"; "check"; "--start"; "--json" ]
                      Assert.equal 0 first.Exit
                      let successfulAt = text (JsonNode.Parse first.Out).["freshness"].["lastSuccessfulCheckAt"]
                      Directory.Move(remote, offline)

                      let failed = CliHarness.ros root [ "sync"; "check"; "--json" ]
                      Assert.equal 1 failed.Exit
                      let report = JsonNode.Parse failed.Out
                      Assert.equal "failed" (text report.["lastAttempt"].["outcome"])
                      Assert.equal successfulAt (text report.["freshness"].["lastSuccessfulCheckAt"])
                      Assert.equal false (boolean report.["safeForFinalValidation"])
                  finally
                      CliHarness.removeDirectory parent } ]
