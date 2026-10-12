namespace Praxis.Tests

open System
open System.IO
open System.Diagnostics
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkHistoryMergeTests =
    let private t name run = { Name = "work history merge: " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private refuses value = Assert.isTrue (Result.isError value) "ambiguous history accepted"
    let private at = DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero)
    let private baseline = { Metadata = Map.ofList ["repository", "praxis"]; UpdatedAt = at; Items = ["old", "original"]; Events = ["e0", "original"] }
    let private left = { baseline with UpdatedAt = at.AddMinutes 1.; Items = baseline.Items @ ["left", "left-record"]; Events = baseline.Events @ ["e1", "left-event"] }
    let private right = { baseline with UpdatedAt = at.AddMinutes 2.; Items = baseline.Items @ ["right", "right-record"]; Events = baseline.Events @ ["e2", "right-event"] }
    let private merge l r = WorkHistoryMerge.plan baseline l r
    let private fixtureWithInsertion insertion test =
        let root = CliHarness.temporaryDirectory "praxis-history-merge"
        let context = Path.Combine(root, ".ros", "context", "current.json")
        let events = Path.Combine(root, ".ros", "events", "events.jsonl")
        let write suffix =
            Directory.CreateDirectory(Path.GetDirectoryName context) |> ignore
            Directory.CreateDirectory(Path.GetDirectoryName events) |> ignore
            File.WriteAllText(context, "{\"schemaVersion\":\"1.0.0\",\"protocolVersion\":\"1.0.0\",\"repository\":\"fixture\",\"updatedAt\":\"2026-10-11T00:00:00Z\",\"workItems\":[{\"id\":\"base\",\"state\":\"active\"}" + (if suffix = "" then "" else ",{\"id\":\"" + suffix + "\",\"state\":\"active\",\"unknown\":42}") + "]}")
            let original = "{\"eventId\":\"base\",\"unknown\":42}\n"
            let added = if suffix = "" then "" else "{\"eventId\":\"" + suffix + "\",\"unknown\":43}\n"
            File.WriteAllText(events, if insertion then added + original else original + added)
        let git args = CliHarness.git root args |> ignore
        try
            git ["init"; "-q"]
            git ["config"; "user.name"; "Fixture"]
            git ["config"; "user.email"; "fixture@example.invalid"]
            write ""
            git ["add"; "."]
            git ["commit"; "-qm"; "base"]
            git ["checkout"; "-qb"; "other"]
            write "right"
            git ["add"; "."]
            git ["commit"; "-qm"; "right"]
            git ["checkout"; "-qb"; "local"; "HEAD~1"]
            write "left"
            git ["add"; "."]
            git ["commit"; "-qm"; "left"]
            let result = CliHarness.run "git" ["-C"; root; "merge"; "--no-commit"; "other"] []
            Assert.equal 1 result.Exit
            test root context events
        finally
            for path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do File.SetAttributes(path, FileAttributes.Normal)
            Directory.Delete(root, true)

    let private fixture test = fixtureWithInsertion false test

    let tests =
        [ t "preserves both independent records, opaque fields and immutable events" (fun () ->
              let merged = merge left right |> ok
              Assert.equal ["old", "original"; "left", "left-record"; "right", "right-record"] merged.Items
              Assert.equal ["e0", "original"; "e1", "left-event"; "e2", "right-event"] merged.Events
              Assert.equal right.UpdatedAt merged.UpdatedAt)
          t "refuses divergent edits to the same work item instead of choosing latest state" (fun () ->
              merge { left with Items = ["old", "complete"] } { right with Items = ["old", "blocked"] } |> refuses)
          t "refuses same event identity with different content" (fun () ->
              merge left {right with Events = baseline.Events @ ["e1", "forged"]} |> refuses)
          t "preserves shared anchors after independently inserted observations" (fun () ->
              let r = {right with Events = baseline.Events @ ["e2", "right-event"; "e1", "left-event"]}
              Assert.equal r.Events (merge left r |> ok).Events
              let b = {baseline with Events = ["e0", "original"; "e3", "later"]}
              let l = {left with Events = b.Events @ ["e1", "left-event"]}
              let r = {right with Events = ["e2", "right-event"] @ b.Events}
              Assert.equal (r.Events @ ["e1", "left-event"]) (WorkHistoryMerge.plan b l r |> ok).Events)
          t "refuses contradictory order of shared new observations" (fun () ->
              let l = {left with Events = baseline.Events @ ["e1", "left-event"; "e2", "right-event"]}
              let r = {right with Events = baseline.Events @ ["e2", "right-event"; "e1", "left-event"]}
              merge l r |> refuses)
          t "all small event permutations preserve both orders or refuse contradictory constraints" (fun () ->
              let rec permutations = function
                  | [] -> [[]]
                  | xs -> xs |> List.collect (fun x -> permutations (List.filter ((<>) x) xs) |> List.map (fun rest -> x :: rest))
              let histories = [ []; ["e0"]; ["e0"; "a"]; ["e0"; "a"; "b"]; ["e0"; "b"; "c"] ] |> List.collect permutations
              for l in histories do
                  for r in histories do
                      let shared = Set.intersect (Set.ofList l) (Set.ofList r)
                      let compatible = List.contains "e0" l && List.contains "e0" r && List.filter shared.Contains l = List.filter shared.Contains r
                      let events ids = ids |> List.map (fun id -> id, if id = "e0" then "original" else id)
                      match merge {left with Events = events l} {right with Events = events r} with
                      | Error _ -> Assert.isTrue (not compatible) "compatible history refused"
                      | Ok result ->
                          Assert.isTrue compatible "contradictory history accepted"
                          let ids = List.map fst result.Events
                          Assert.equal l (List.filter (fun id -> List.contains id l) ids)
                          Assert.equal r (List.filter (fun id -> List.contains id r) ids)
                          Assert.equal (Set.union (Set.ofList l) (Set.ofList r)) (Set.ofList ids)
                          Assert.equal ids.Length (List.distinct ids).Length)
          t "refuses removed rewritten reordered or duplicated historical events" (fun () ->
              for events in [ []; ["e0", "rewritten"]; baseline.Events @ baseline.Events ] do
                  merge {left with Events = events} right |> refuses
              let b = {baseline with Events = baseline.Events @ ["old2", "original2"]}
              WorkHistoryMerge.plan b {left with Events = List.rev b.Events} {right with Events = b.Events} |> refuses)
          t "refuses item removal, duplicate identities and missing identities" (fun () ->
              for items in [ []; baseline.Items @ baseline.Items; baseline.Items @ ["", "missing"] ] do
                  merge {left with Items = items} right |> refuses)
          t "refuses divergent repository metadata and non-UTC snapshots" (fun () ->
              merge {left with Metadata = Map.ofList ["repository", "other"]} {right with Metadata = Map.ofList ["repository", "third"]} |> refuses
              merge {left with UpdatedAt = left.UpdatedAt.ToOffset(TimeSpan.FromHours 1.)} right |> refuses
              merge {left with UpdatedAt = at.AddSeconds(-1.)} right |> refuses
              merge {left with Metadata = Map.ofList ["repository", "other"]} right |> refuses)
          t "deduplicates identical appended observations and accepts one-sided changes" (fun () ->
              Assert.equal left (merge left left |> ok)
              let changed = {left with Items = ["old", "changed"]}
              Assert.equal changed.Items (merge changed baseline |> ok).Items)
          t "real Git preview is read-only and apply preserves both branch event bytes" (fun () -> fixture (fun root context events ->
              let before = File.ReadAllText context, File.ReadAllText events
              let cliPreview = CliHarness.rosWith root [] ["work"; "history"; "merge"]
              Assert.equal 0 cliPreview.Exit
              Assert.isTrue (cliPreview.Out.Contains("preview")) cliPreview.Out
              let preview = FileWorkHistoryMerge.run root false |> ok
              Assert.equal 3 preview.Items.Length
              Assert.equal before (File.ReadAllText context, File.ReadAllText events)
              let cliApply = CliHarness.rosWith root [] ["work"; "history"; "merge"; "--apply"]
              Assert.equal 0 cliApply.Exit
              let applied = FileWorkHistoryMerge.run root false |> ok
              Assert.equal preview applied
              Assert.equal "{\"eventId\":\"base\",\"unknown\":42}\n{\"eventId\":\"left\",\"unknown\":43}\n{\"eventId\":\"right\",\"unknown\":43}\n" (File.ReadAllText events)
              Assert.isTrue ((File.ReadAllText context).Contains("\"unknown\": 42")) "opaque field lost"
              Assert.isTrue (not (File.Exists(WorkStateTransaction.transactionPath root))) "journal left behind"
              CliHarness.git root ["add"; ".ros/context/current.json"; ".ros/events/events.jsonl"] |> ignore
              FileWorkHistoryMerge.run root true |> refuses))
          t "real Git insertion merge keeps original bytes and both branch orders" (fun () -> fixtureWithInsertion true (fun root context events ->
              let before = File.ReadAllText context, File.ReadAllText events
              FileWorkHistoryMerge.run root false |> ok |> ignore
              Assert.equal before (File.ReadAllText context, File.ReadAllText events)
              FileWorkHistoryMerge.run root true |> ok |> ignore
              Assert.equal "{\"eventId\":\"left\",\"unknown\":43}\n{\"eventId\":\"right\",\"unknown\":43}\n{\"eventId\":\"base\",\"unknown\":42}\n" (File.ReadAllText events)))
          t "refuses unrelated pending journal without changing history" (fun () -> fixture (fun root context events ->
              let before = File.ReadAllText context, File.ReadAllText events
              WorkStateTransaction.prepare root [{Path = ".ros/events/events.jsonl"; Content = "unrelated"}; {Path = ".ros/context/current.json"; Content = "unrelated"}] |> ok
              FileWorkHistoryMerge.run root true |> refuses
              Assert.equal before (File.ReadAllText context, File.ReadAllText events)))
          t "refuses a forged index stage even when all three stages exist" (fun () -> fixture (fun root context events ->
              let before = File.ReadAllText context, File.ReadAllText events
              let replacement = CliHarness.git root ["rev-parse"; "other:.ros/context/current.json"]
              let start = ProcessStartInfo("git", WorkingDirectory = root, RedirectStandardInput = true, UseShellExecute = false)
              start.ArgumentList.Add "update-index"
              start.ArgumentList.Add "--index-info"
              use child = Process.Start start
              child.StandardInput.WriteLine($"100644 {replacement} 2\t.ros/context/current.json")
              child.StandardInput.Close()
              child.WaitForExit()
              Assert.equal 0 child.ExitCode
              FileWorkHistoryMerge.run root true |> refuses
              Assert.equal before (File.ReadAllText context, File.ReadAllText events)))
          t "resumes an exact interrupted transaction without duplicating events" (fun () -> fixture (fun root context events ->
              let conflicted = File.ReadAllText context, File.ReadAllText events
              FileWorkHistoryMerge.run root true |> ok |> ignore
              let merged = File.ReadAllText context, File.ReadAllText events
              File.WriteAllText(context, fst conflicted)
              File.WriteAllText(events, snd conflicted)
              WorkStateTransaction.prepare root [{Path = ".ros/events/events.jsonl"; Content = snd merged}; {Path = ".ros/context/current.json"; Content = fst merged}] |> ok
              File.WriteAllText(events, snd merged)
              FileWorkHistoryMerge.run root true |> ok |> ignore
              Assert.equal merged (File.ReadAllText context, File.ReadAllText events)))
          t "refuses linked history paths without writing their target" (fun () -> fixture (fun root context _ ->
              if not (OperatingSystem.IsWindows()) then
                  let outside = Path.Combine(root, "outside.json")
                  let original = File.ReadAllText context
                  File.WriteAllText(outside, original)
                  File.Delete context
                  File.CreateSymbolicLink(context, outside) |> ignore
                  FileWorkHistoryMerge.run root true |> refuses
                  Assert.equal original (File.ReadAllText outside))) ]
