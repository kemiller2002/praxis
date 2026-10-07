namespace Ros.Tests

open Ros.Domain.Telemetry

[<RequireQualifiedAccess>]
module ChangeSummaryTests =
    let private untracked binary lines : UntrackedLineCount = { Binary = binary; Lines = lines }

    let tests =
        [ { Name = "parseNameStatus splits a rename's two-column payload and keeps only the destination path"
            Run = fun () ->
                let entries = ChangeSummaryParser.parseNameStatus [] "R100\told-name.fs\tnew-name.fs"
                let entry = Assert.single entries
                Assert.equal 'R' entry.Status
                Assert.equal "new-name.fs" entry.Path
                Assert.equal false entry.Untracked }

          { Name = "parseNameStatus keeps the single-column payload for add/modify/delete codes"
            Run = fun () ->
                let entries = ChangeSummaryParser.parseNameStatus [] "M\tsrc/File.fs\nA\tsrc/New.fs\nD\tsrc/Gone.fs"
                Assert.equal 3 entries.Length
                Assert.equal [ 'M'; 'A'; 'D' ] (entries |> List.map (fun entry -> entry.Status))
                Assert.equal [ "src/File.fs"; "src/New.fs"; "src/Gone.fs" ] (entries |> List.map (fun entry -> entry.Path)) }

          { Name = "parseNameStatus drops entries matching an ignored pattern and tolerates empty input"
            Run = fun () ->
                let entries = ChangeSummaryParser.parseNameStatus [ ".ros/**" ] "M\tsrc/File.fs\nM\t.ros/context/current.json"
                let entry = Assert.single entries
                Assert.equal "src/File.fs" entry.Path
                Assert.empty (ChangeSummaryParser.parseNameStatus [] "") }

          { Name = "parseNumstat keeps the binary sentinel as a string and reads the final tab-separated field as the file"
            Run = fun () ->
                let lines = ChangeSummaryParser.parseNumstat "3\t1\tsrc/File.fs\n-\t-\tsrc/Image.png"
                Assert.equal 2 lines.Length
                Assert.equal { Added = "3"; Deleted = "1"; File = "src/File.fs" } lines[0]
                Assert.equal { Added = "-"; Deleted = "-"; File = "src/Image.png" } lines[1] }

          { Name = "parseNumstat drops malformed lines with fewer than three columns and tolerates empty input"
            Run = fun () ->
                Assert.empty (ChangeSummaryParser.parseNumstat "3\t1")
                Assert.empty (ChangeSummaryParser.parseNumstat "") }

          { Name = "parseUntracked splits on NUL and drops empty segments, tolerating empty input"
            Run = fun () ->
                Assert.equal [ "a.txt"; "b/c.txt" ] (ChangeSummaryParser.parseUntracked "a.txt\000b/c.txt\000")
                Assert.empty (ChangeSummaryParser.parseUntracked "") }

          { Name = "isTestFile matches a tests directory or a .test./.spec. suffix, case-insensitively"
            Run = fun () ->
                Assert.isTrue (ChangeClassification.isTestFile "tests/Foo.fs") "tests/ directory"
                Assert.isTrue (ChangeClassification.isTestFile "src/__tests__/Foo.js") "__tests__ directory"
                Assert.isTrue (ChangeClassification.isTestFile "src/foo.test.js") ".test. suffix"
                Assert.isTrue (ChangeClassification.isTestFile "SRC/FOO.SPEC.TS") "case-insensitive .spec. suffix"
                Assert.isTrue (not (ChangeClassification.isTestFile "src/attestation.fs")) "'test' as a mid-word substring must not match" }

          { Name = "isDocumentation matches a docs directory, a README, or a doc-like extension, case-insensitively"
            Run = fun () ->
                Assert.isTrue (ChangeClassification.isDocumentation "docs/guide.md") "docs/ directory"
                Assert.isTrue (ChangeClassification.isDocumentation "README.md") "README"
                Assert.isTrue (ChangeClassification.isDocumentation "NOTES.MDX") "case-insensitive extension"
                Assert.isTrue (not (ChangeClassification.isDocumentation "src/Notes.fs")) "an .fs file is not documentation" }

          { Name = "compute aggregates name-status, numstat, and untracked entries into counts, line totals, tests, and docs"
            Run = fun () ->
                let nameStatus =
                    ChangeSummaryParser.parseNameStatus [] "M\tsrc/File.fs\nA\tsrc/New.fs\nD\tsrc/Old.fs\nR100\tsrc/tests/before.fs\tsrc/tests/after.fs"

                let numstat = ChangeSummaryParser.parseNumstat "4\t2\tsrc/File.fs\n0\t0\tsrc/New.fs\n0\t0\tsrc/Old.fs\n-\t-\tsrc/tests/after.fs"
                let untrackedPaths = [ "docs/notes.md"; "assets/logo.png" ]

                let untrackedLineCounts =
                    Map.ofList [ "docs/notes.md", untracked false 5; "assets/logo.png", untracked true 0 ]

                let summary =
                    ChangeSummaryParser.compute [] nameStatus numstat untrackedPaths untrackedLineCounts "start-sha" "end-sha" 3

                Assert.equal "git-diff-from-clean-execution-baseline" summary.Mechanism
                Assert.equal "start-sha" summary.StartCommit
                Assert.equal "end-sha" summary.EndCommit
                Assert.equal 3 summary.Commits
                // Added = 1 tracked add (src/New.fs) + 2 untracked adds (docs/notes.md, assets/logo.png).
                Assert.equal { Added = 3; Modified = 1; Deleted = 1; Renamed = 1 } summary.Counts
                Assert.equal (4 + 5) summary.LinesAdded
                Assert.equal 2 summary.LinesDeleted
                Assert.equal (1 + 1) summary.BinaryFiles
                Assert.equal { Added = 0; Modified = 1; Removed = 0 } summary.Tests
                Assert.equal 1 summary.DocumentationFilesChanged }

          { Name = "compute treats an untracked path already reported by name-status as already-known, not double-counted"
            Run = fun () ->
                let nameStatus = ChangeSummaryParser.parseNameStatus [] "A\tsrc/New.fs"
                let summary = ChangeSummaryParser.compute [] nameStatus [] [ "src/New.fs" ] Map.empty "s" "e" 0
                Assert.equal 1 summary.Counts.Added }

          { Name = "compute filters untracked paths matching an ignored pattern before counting them"
            Run = fun () ->
                let summary = ChangeSummaryParser.compute [ "build/**" ] [] [] [ "build/out.js" ] Map.empty "s" "e" 0
                Assert.equal 0 summary.Counts.Added }

          { Name = "BlockedDuration.compute is zero with no lifecycle events at all"
            Run = fun () ->
                Assert.equal 0L (BlockedDuration.compute [] "2026-01-01T00:00:10.000Z") }

          { Name = "BlockedDuration.compute sums a closed blocked/resumed interval"
            Run = fun () ->
                let events: LifecycleEvent list =
                    [ { Type = "work.blocked"; OccurredAt = "2026-01-01T00:00:00.000Z" }
                      { Type = "work.resumed"; OccurredAt = "2026-01-01T00:00:05.000Z" } ]

                Assert.equal 5000L (BlockedDuration.compute events "2026-01-01T00:00:10.000Z") }

          { Name = "BlockedDuration.compute closes a still-open blocked interval against finalizedAt"
            Run = fun () ->
                let events: LifecycleEvent list = [ { Type = "work.blocked"; OccurredAt = "2026-01-01T00:00:00.000Z" } ]
                Assert.equal 10000L (BlockedDuration.compute events "2026-01-01T00:00:10.000Z") }

          { Name = "BlockedDuration.compute ignores a resumed event with no matching prior blocked event"
            Run = fun () ->
                let events: LifecycleEvent list = [ { Type = "work.resumed"; OccurredAt = "2026-01-01T00:00:05.000Z" } ]
                Assert.equal 0L (BlockedDuration.compute events "2026-01-01T00:00:10.000Z") }

          { Name = "BlockedDuration.compute sums multiple non-overlapping blocked intervals regardless of input order"
            Run = fun () ->
                let events: LifecycleEvent list =
                    [ { Type = "work.resumed"; OccurredAt = "2026-01-01T00:00:05.000Z" }
                      { Type = "work.blocked"; OccurredAt = "2026-01-01T00:00:00.000Z" }
                      { Type = "work.blocked"; OccurredAt = "2026-01-01T00:00:20.000Z" }
                      { Type = "work.resumed"; OccurredAt = "2026-01-01T00:00:30.000Z" } ]

                Assert.equal 15000L (BlockedDuration.compute events "2026-01-01T00:00:40.000Z") } ]
