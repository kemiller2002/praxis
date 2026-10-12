namespace Praxis.Tests

open System
open System.IO
open System.Diagnostics
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// The native history merge also merges the backlog queue (WI-0093): concurrent
/// `praxis add` captures on two branches must both survive a merge.
[<RequireQualifiedAccess>]
module WorkBacklogMergeTests =
    let private t name run = { Name = "work history merge: backlog " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private refuses value = Assert.isTrue (Result.isError value) "ambiguous backlog accepted"
    let private meta = Map.ofList [ "schemaVersion", "\"1.0.0\""; "repository", "\"fixture\"" ]
    let private baseline = { Metadata = meta; NextSeq = 2; Items = [ "WI-0001", "base" ]; Reidentified = Set.empty }
    let private left = { baseline with NextSeq = 3; Items = baseline.Items @ [ "WI-0002", "left" ] }
    let private right = { baseline with Items = baseline.Items @ [ "WI-0201", "right" ] }
    let private plan l r = WorkBacklogMerge.plan baseline l r

    let private queuePaths = [ ".ros/work/queue.json"; ".ros/work/queue.md" ]
    let private historyPaths = [ ".ros/context/current.json"; ".ros/events/events.jsonl" ]
    let private allPaths = historyPaths @ queuePaths
    let private snapshotFiles root = allPaths |> List.map (CliHarness.read root)

    let private now () = CliHarness.now ()

    /// Capture a fresh row and begin it, so the branch changes native history too
    /// (the command still requires history conflict stages, exactly as before).
    let private captureAndBegin root title id =
        CliHarness.rosOk root [ "add"; title; "--id"; id ] |> ignore
        CliHarness.rosOk root [ "work"; "backlog-transition"; "--action"; "ready"; "--id"; id; "--occurred-at"; now () ] |> ignore
        CliHarness.rosOk root [ "work"; "begin"; "--id"; id; "--occurred-at"; now () ] |> ignore

    /// Real repository, real `praxis` writes on two branches, real conflicted `git merge`.
    let private fixture (setup: string -> unit) (leftWork: string -> unit) (rightWork: string -> unit) test =
        let root = CliHarness.initializedRepository "praxis-backlog-merge" None
        try
            setup root
            CliHarness.commitAll root "base"
            CliHarness.git root [ "checkout"; "-qb"; "other" ] |> ignore
            rightWork root
            CliHarness.commitAll root "right"
            CliHarness.git root [ "checkout"; "-q"; "main" ] |> ignore
            leftWork root
            CliHarness.commitAll root "left"
            let merge = CliHarness.run "git" [ "-C"; root; "-c"; "user.name=Test"; "-c"; "user.email=test@example.invalid"; "merge"; "--no-commit"; "other" ] []
            Assert.equal 1 merge.Exit
            test root
        finally
            CliHarness.removeTemporaryGitDirectory root

    let private threeRows root =
        for title in [ "one"; "two"; "three" ] do CliHarness.rosOk root [ "add"; title ] |> ignore

    let private independentCaptures test =
        fixture
            (fun root -> CliHarness.rosOk root [ "add"; "base row" ] |> ignore)
            (fun root ->
                // A generated ID advances nextSeq on this side only.
                CliHarness.rosOk root [ "add"; "left generated row" ] |> ignore
                captureAndBegin root "left row" "WI-0101")
            (fun root -> captureAndBegin root "right row" "WI-0201")
            test

    let private stages root path = CliHarness.git root [ "ls-files"; "--stage"; "--"; path ]

    let private updateIndex root (lines: string) =
        let start = ProcessStartInfo("git", WorkingDirectory = root, RedirectStandardInput = true, UseShellExecute = false)
        start.ArgumentList.Add "update-index"
        start.ArgumentList.Add "--index-info"
        use child = Process.Start start
        child.StandardInput.Write(lines)
        child.StandardInput.Close()
        child.WaitForExit()
        Assert.equal 0 child.ExitCode

    let private refusesUnchanged root =
        let before = snapshotFiles root
        FileWorkHistoryMerge.merge root true |> refuses
        Assert.equal before (snapshotFiles root)
        Assert.isTrue (not (File.Exists(WorkStateTransaction.transactionPath root))) "history journal written"
        Assert.isTrue (not (File.Exists(BacklogStateTransaction.transactionPath root))) "backlog journal written"

    let private ids (queue: string) =
        let document = CliHarness.json queue
        document["items"].AsArray() |> Seq.map (fun item -> item["id"].GetValue<string>()) |> Seq.toList

    let tests =
        [ t "planner unions independent rows in base, left, right order and never lowers nextSeq" (fun () ->
              let merged = plan left right |> ok
              Assert.equal [ "WI-0001", "base"; "WI-0002", "left"; "WI-0201", "right" ] merged.Items
              Assert.equal 3 merged.NextSeq
              Assert.equal 3 (plan { left with NextSeq = 1 } { right with NextSeq = 3 } |> ok).NextSeq)
          t "planner refuses a row both sides changed differently, naming it" (fun () ->
              match plan { left with Items = [ "WI-0001", "ready" ] } { right with Items = [ "WI-0001", "abandoned" ] } with
              | Error error -> Assert.isTrue (error.Contains "WI-0001") error
              | Ok _ -> failwith "divergent backlog row accepted")
          t "planner accepts identical additions and one-sided changes" (fun () ->
              let shared = [ "WI-0500", "same" ]
              Assert.equal (baseline.Items @ shared) (plan { left with Items = baseline.Items @ shared } { right with Items = baseline.Items @ shared } |> ok).Items
              Assert.equal [ "WI-0001", "changed"; "WI-0201", "right" ] (plan { left with Items = [ "WI-0001", "changed" ] } right |> ok).Items)
          t "planner refuses deletion, divergent metadata and duplicate identities" (fun () ->
              match plan { left with Items = [ "WI-0002", "left" ] } right with
              | Error error -> Assert.isTrue (error.Contains "WI-0001") error
              | Ok _ -> failwith "deleted backlog row accepted"
              plan { left with Metadata = meta.Add("repository", "\"other\"") } right |> refuses
              plan { left with Metadata = meta.Add("schemaVersion", "\"2.0.0\"") } right |> refuses
              plan { left with Items = left.Items @ left.Items } right |> refuses
              plan { left with Items = left.Items @ [ "", "missing" ] } right |> refuses)
          t "planner accepts only a fresh native reidentification as removal, and only of an unchanged row" (fun () ->
              let renamed = { left with Items = [ "WI-0001-REUSED", "renamed" ]; Reidentified = set [ "WI-0001" ] }
              Assert.equal [ "WI-0001-REUSED", "renamed"; "WI-0201", "right" ] (plan renamed right |> ok).Items
              plan renamed { right with Items = [ "WI-0001", "changed" ] } |> refuses
              WorkBacklogMerge.plan { baseline with Reidentified = set [ "WI-0001" ] } { renamed with Reidentified = set [ "WI-0001" ] } right |> refuses)
          t "two branches that each praxis add a row keep both rows through the real CLI" (fun () -> independentCaptures (fun root ->
              let leftQueue = CliHarness.git root [ "show"; "HEAD:.ros/work/queue.json" ] |> CliHarness.json
              let rightQueue = CliHarness.git root [ "show"; "MERGE_HEAD:.ros/work/queue.json" ] |> CliHarness.json
              let before = snapshotFiles root
              let preview = CliHarness.rosOk root [ "work"; "history"; "merge" ]
              Assert.isTrue (preview.Out.Contains "preview" && preview.Out.Contains "-> 4 merged rows") preview.Out
              Assert.equal before (snapshotFiles root)
              let applied = CliHarness.rosOk root [ "work"; "history"; "merge"; "--apply" ]
              Assert.isTrue (applied.Out.Contains ".ros/work/queue.json .ros/work/queue.md") applied.Out
              let queue = CliHarness.read root ".ros/work/queue.json"
              Assert.equal [ "WI-0001"; "WI-0002"; "WI-0101"; "WI-0201" ] (ids queue)
              let queueDocument = CliHarness.json queue
              let nextSeq = queueDocument["nextSeq"].GetValue<int>()
              Assert.equal (max (leftQueue["nextSeq"].GetValue<int>()) (rightQueue["nextSeq"].GetValue<int>())) nextSeq
              Assert.equal 3 nextSeq
              let markdown = CliHarness.read root ".ros/work/queue.md"
              Assert.isTrue (markdown.Contains "| WI-0101 | left row | active |" && markdown.Contains "| WI-0201 | right row | active |") markdown
              Assert.isTrue (not (File.Exists(BacklogStateTransaction.transactionPath root))) "backlog journal left behind"
              CliHarness.git root ([ "add"; "--" ] @ allPaths) |> ignore
              Assert.equal "" (CliHarness.git root [ "ls-files"; "--unmerged" ])
              Assert.equal 0 (CliHarness.ros root [ "validate" ]).Exit
              // The regenerated projection is exactly what the native backlog writer renders.
              CliHarness.rosOk root [ "add"; "probe"; "--id"; "WI-0999" ] |> ignore
              Assert.equal (markdown + "| WI-0999 | probe | captured |  | medium |\n") (CliHarness.read root ".ros/work/queue.md")
              Assert.equal (ids queue @ [ "WI-0999" ]) (ids (CliHarness.read root ".ros/work/queue.json"))))
          t "refuses a row both branches changed differently with nothing written" (fun () ->
              fixture
                  (fun root -> CliHarness.rosOk root [ "add"; "base row" ] |> ignore)
                  (fun root ->
                      CliHarness.rosOk root [ "work"; "backlog-transition"; "--action"; "ready"; "--id"; "WI-0001"; "--occurred-at"; now () ] |> ignore
                      captureAndBegin root "left row" "WI-0101")
                  (fun root ->
                      CliHarness.rosOk root [ "work"; "backlog-transition"; "--action"; "abandon"; "--id"; "WI-0001"; "--reason"; "duplicate"; "--occurred-at"; now () ] |> ignore
                      captureAndBegin root "right row" "WI-0201")
                  (fun root ->
                      let before = snapshotFiles root
                      let result = CliHarness.ros root [ "work"; "history"; "merge"; "--apply" ]
                      Assert.equal 1 result.Exit
                      Assert.isTrue (result.Err.Contains "WI-0001") result.Err
                      Assert.equal before (snapshotFiles root)
                      Assert.isTrue (not (File.Exists(WorkStateTransaction.transactionPath root))) "history journal written"))
          t "accepts the same row added identically on both branches" (fun () ->
              let at = now ()
              let shared root = CliHarness.rosOk root [ "add"; "shared row"; "--id"; "WI-0500"; "--occurred-at"; at ] |> ignore
              fixture
                  (fun root -> CliHarness.rosOk root [ "add"; "base row" ] |> ignore)
                  (fun root -> shared root; captureAndBegin root "left row" "WI-0101")
                  (fun root -> shared root; captureAndBegin root "right row" "WI-0201")
                  (fun root ->
                      let merged = FileWorkHistoryMerge.merge root true |> ok
                      Assert.equal 4 merged.Backlog.Value.MergedRows
                      Assert.equal [ "WI-0001"; "WI-0500"; "WI-0101"; "WI-0201" ] (ids (CliHarness.read root ".ros/work/queue.json"))))
          t "regenerates queue.md when only it conflicted and keeps the cleanly merged queue.json" (fun () ->
              fixture threeRows
                  (fun root ->
                      CliHarness.rosOk root [ "work"; "backlog-transition"; "--action"; "ready"; "--id"; "WI-0003"; "--occurred-at"; now () ] |> ignore
                      CliHarness.rosOk root [ "work"; "begin"; "--id"; "WI-0003"; "--occurred-at"; now () ] |> ignore)
                  (fun root -> captureAndBegin root "right row" "WI-0201")
                  (fun root ->
                      Assert.isTrue ((stages root ".ros/work/queue.json").Contains " 0\t") "fixture queue.json must merge cleanly"
                      Assert.isTrue ((stages root ".ros/work/queue.md").Contains " 3\t") "fixture queue.md must conflict"
                      let queue = CliHarness.read root ".ros/work/queue.json"
                      let preview = CliHarness.rosOk root [ "work"; "history"; "merge" ]
                      Assert.isTrue (preview.Out.Contains "would be regenerated") preview.Out
                      let merged = FileWorkHistoryMerge.merge root true |> ok
                      Assert.isTrue merged.Backlog.Value.Regenerated "queue.json was re-merged"
                      Assert.equal queue (CliHarness.read root ".ros/work/queue.json")
                      let markdown = CliHarness.read root ".ros/work/queue.md"
                      Assert.isTrue (not (markdown.Contains "<<<<<<<") && markdown.Contains "| WI-0003 | three | active |" && markdown.Contains "| WI-0201 | right row | active |") markdown
                      CliHarness.git root ([ "add"; "--" ] @ allPaths) |> ignore
                      Assert.equal 0 (CliHarness.ros root [ "validate" ]).Exit))
          t "refuses missing, non-regular and wrongly bound queue stages without writing" (fun () -> independentCaptures (fun root ->
              let path = ".ros/work/queue.json"
              let blob stage = CliHarness.git root [ "rev-parse"; $":{stage}:{path}" ]
              let original = stages root path + "\n"
              let restore () =
                  updateIndex root original
                  Assert.equal original (stages root path + "\n")
                  FileWorkHistoryMerge.merge root false |> ok |> ignore
              Assert.isTrue (blob 2 <> blob 3) "fixture branches must have different queues"
              // A missing stage.
              updateIndex root $"0 {String('0', 40)} 1\t{path}\n"
              Assert.isTrue (not ((stages root path).Contains " 1\t")) "stage 1 not removed"
              refusesUnchanged root
              restore ()
              // A link where a regular file is required.
              updateIndex root $"120000 {blob 2} 2\t{path}\n"
              Assert.isTrue ((stages root path).Contains "120000 ") "stage 2 not replaced by a link"
              refusesUnchanged root
              restore ()
              // A stage bound to the wrong commit.
              updateIndex root $"100644 {blob 3} 2\t{path}\n"
              Assert.equal (blob 3) (blob 2)
              refusesUnchanged root
              restore ()
              Assert.equal original (stages root path + "\n")
              FileWorkHistoryMerge.merge root false |> ok |> ignore))
          t "refuses a linked working queue file without writing its target" (fun () -> independentCaptures (fun root ->
              if not (OperatingSystem.IsWindows()) then
                  let queue = Path.Combine(root, ".ros", "work", "queue.json")
                  let outside = Path.Combine(root, "outside.json")
                  let original = File.ReadAllText queue
                  File.WriteAllText(outside, original)
                  File.Delete queue
                  File.CreateSymbolicLink(queue, outside) |> ignore
                  FileWorkHistoryMerge.merge root true |> refuses
                  Assert.equal original (File.ReadAllText outside)))
          t "refuses an unrelated pending backlog journal before writing history" (fun () -> independentCaptures (fun root ->
              BacklogStateTransaction.prepare root [ { Path = queuePaths[0]; Content = "unrelated" }; { Path = queuePaths[1]; Content = "unrelated" } ] |> ok
              let before = snapshotFiles root
              FileWorkHistoryMerge.merge root true |> refuses
              Assert.equal before (snapshotFiles root)
              Assert.isTrue (not (File.Exists(WorkStateTransaction.transactionPath root))) "history journal written"))
          t "re-running after a crash between the history and backlog transactions is identical" (fun () -> independentCaptures (fun root ->
              let conflicted = snapshotFiles root
              let preview = FileWorkHistoryMerge.merge root false |> ok
              FileWorkHistoryMerge.merge root true |> ok |> ignore
              let merged = snapshotFiles root
              let restoreQueue () =
                  for path, content in List.zip allPaths conflicted |> List.filter (fun (path, _) -> List.contains path queuePaths) do
                      CliHarness.write root path content
              // Crash after the history transaction, before the backlog journal was prepared.
              restoreQueue ()
              Assert.equal (List.take 2 merged) (List.take 2 (snapshotFiles root))
              Assert.equal preview (FileWorkHistoryMerge.merge root true |> ok)
              Assert.equal merged (snapshotFiles root)
              // Crash after the backlog journal was prepared and queue.json was written.
              restoreQueue ()
              BacklogStateTransaction.prepare root [ { Path = queuePaths[0]; Content = merged[2] }; { Path = queuePaths[1]; Content = merged[3] } ] |> ok
              CliHarness.write root queuePaths[0] merged[2]
              FileWorkHistoryMerge.merge root true |> ok |> ignore
              Assert.equal merged (snapshotFiles root)
              Assert.isTrue (not (File.Exists(BacklogStateTransaction.transactionPath root))) "backlog journal left behind"
              Assert.isTrue (not (File.Exists(WorkStateTransaction.transactionPath root))) "history journal left behind")) ]
