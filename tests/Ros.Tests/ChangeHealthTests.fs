namespace Ros.Tests

open Ros.Domain.Telemetry
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module ChangeHealthTests =
    let private threshold warning error = { Warning = warning; Error = error }

    let private policy =
        { ChangeHealth.defaultPolicy with
            HistoryWindow = 3
            MaxHistoryUpdates = 3
            LineBucketSize = 25
            Thresholds =
                { FilesChanged = threshold (Some 2) (Some 4)
                  LinesChanged = threshold (Some 100) (Some 200)
                  LargestFileChurn = threshold None None
                  LargestChangedFileLines = threshold None None
                  HunksChanged = threshold None None
                  MaxHunksPerFile = threshold None None
                  RepeatFileTouches = threshold (Some 2) (Some 4)
                  RepeatRegionTouches = threshold (Some 2) (Some 4) } }

    let private historyFile path from buckets =
        { Path = path
          From = from
          Status = 'M'
          Hunks =
            [ { OldStart = 1
                OldLines = 1
                NewStart = 1
                NewLines = 1
                Buckets = buckets } ] }

    let private update id files =
        { ExecutionId = id
          WorkItemId = "WORK-1"
          FinalizedAt = "2026-09-23T00:00:00Z"
          StartCommit = "a"
          EndCommit = "b"
          Files = files }

    let tests =
        [ { Name = "line buckets keep nearby hunks in the same stable region"
            Run = fun () ->
                Assert.equal [ 0 ] (ChangeHealth.buckets 25 1 1 1 1)
                Assert.equal [ 0 ] (ChangeHealth.buckets 25 10 2 10 2)
                Assert.equal [ 0; 1 ] (ChangeHealth.buckets 25 20 10 20 10)
                Assert.equal [ 1 ] (ChangeHealth.buckets 25 30 0 30 0) }

          { Name = "source classification excludes tests and documentation"
            Run = fun () ->
                Assert.isTrue (ChangeHealth.isSourceFile "src/App.fs") "F# implementation file"
                Assert.isTrue (not (ChangeHealth.isSourceFile "tests/AppTests.fs")) "test file"
                Assert.isTrue (not (ChangeHealth.isSourceFile "docs/architecture.md")) "documentation file"
                Assert.isTrue (not (ChangeHealth.isSourceFile "config/settings.json")) "non-source config" }

          { Name = "threshold evaluation emits only the highest crossed severity"
            Run = fun () ->
                let metrics = ChangeHealth.metrics 5 2 1 0 130 90 1 1 0 0 1 1
                let findings = ChangeHealth.evaluate policy metrics
                let files = findings |> List.find (fun finding -> finding.Code = "PRAXIS-CHG-001")
                let lines = findings |> List.find (fun finding -> finding.Code = "PRAXIS-CHG-002")
                Assert.equal "error" files.Severity
                Assert.equal 4 files.Threshold
                Assert.equal "error" lines.Severity
                Assert.equal 200 lines.Threshold
                Assert.equal 2 findings.Length }

          { Name = "recent touches count renamed paths and matching line buckets"
            Run = fun () ->
                let history =
                    { ChangeHealth.emptyHistory with
                        Updates =
                            [ update "EXE-1" [ historyFile "src/Old.fs" None [ 3 ] ]
                              update "EXE-2" [ historyFile "src/Other.fs" None [ 9 ] ]
                              update "EXE-3" [ historyFile "src/New.fs" (Some "src/Old.fs") [ 3; 4 ] ] ] }

                let fileTouches, regionTouches =
                    ChangeHealth.recentTouches policy history "src/New.fs" (Some "src/Old.fs") [ 3 ]

                // Two prior matching updates plus the current update.
                Assert.equal 3 fileTouches
                Assert.equal 3 regionTouches }

          { Name = "bounded history retains only the configured newest updates and records omissions"
            Run = fun () ->
                let history =
                    [ 1 .. 5 ]
                    |> List.fold
                        (fun state index -> ChangeHealth.appendHistory policy (update $"EXE-{index}" []) state)
                        ChangeHealth.emptyHistory

                Assert.equal 3 history.Updates.Length
                Assert.equal [ "EXE-3"; "EXE-4"; "EXE-5" ] (history.Updates |> List.map _.ExecutionId)
                Assert.equal 2 history.HistoryOmitted }

          { Name = "zero-context hunk parser records metadata only and filters ignored paths"
            Run = fun () ->
                let diff =
                    "diff --git a/src/A.fs b/src/A.fs\n--- a/src/A.fs\n+++ b/src/A.fs\n@@ -10,2 +10,3 @@\n-old\n+new\n" +
                    "diff --git a/.ros/telemetry/x.json b/.ros/telemetry/x.json\n--- a/.ros/telemetry/x.json\n+++ b/.ros/telemetry/x.json\n@@ -1 +1 @@\n-old\n+new\n"

                let parsed = FileChangeHealthRepository.parseHunks 25 [ ".ros/**" ] diff
                Assert.equal 1 parsed.Count
                let hunks = parsed["src/A.fs"]
                let hunk = Assert.single hunks
                Assert.equal 10 hunk.OldStart
                Assert.equal 2 hunk.OldLines
                Assert.equal 10 hunk.NewStart
                Assert.equal 3 hunk.NewLines
                Assert.equal [ 0 ] hunk.Buckets }

 ]
