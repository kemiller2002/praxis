namespace Praxis.Tests

open System
open System.IO
open System.Diagnostics
open System.Security.Cryptography
open System.Text.Json.Nodes
open Praxis.Domain.Provenance
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

    let private crossedFixturePolicy reverse scope earlyLeft test =
        let root = CliHarness.temporaryDirectory "praxis-crossed-history"
        let context = Path.Combine(root, ".ros", "context", "current.json")
        let events = Path.Combine(root, ".ros", "events", "events.jsonl")
        let git args = CliHarness.git root args
        let write a b ids =
            Directory.CreateDirectory(Path.GetDirectoryName context) |> ignore
            Directory.CreateDirectory(Path.GetDirectoryName events) |> ignore
            let items = ["base", "active"] @ (a |> Option.map (fun state -> ["a", state]) |> Option.defaultValue [])
                        @ (b |> Option.map (fun state -> ["b", state]) |> Option.defaultValue [])
            let rows = items |> List.map (fun (id, state) -> $"    {{\"id\":\"{id}\",\"state\":\"{state}\",\"unknown\":42}}") |> String.concat ",\n\n\n\n"
            File.WriteAllText(context, "{\n  \"schemaVersion\":\"1.0.0\",\n  \"protocolVersion\":\"1.0.0\",\n  \"repository\":\"fixture\",\n  \"updatedAt\":\"2026-10-11T00:00:00Z\",\n  \"workItems\":[\n" + rows + "\n  ]\n}\n")
            File.WriteAllText(events, ids |> List.map (fun id -> $"{{\"eventId\":\"{id}\",\"unknown\":42{scope id}}}\n") |> String.concat "")
        let commit message = git ["add"; "."] |> ignore; git ["commit"; "-qm"; message] |> ignore; git ["rev-parse"; "HEAD"]
        try
            git ["init"; "-q"] |> ignore
            git ["config"; "core.autocrlf"; "true"] |> ignore
            git ["config"; "user.name"; "Fixture"] |> ignore
            git ["config"; "user.email"; "fixture@example.invalid"] |> ignore
            write (Some "active") (Some "active") ["base"]
            let start = commit "base"
            write (Some "active") (Some "active") ["base"; "a"]
            let a = commit "a"
            git ["checkout"; "--detach"; start] |> ignore
            write (Some "active") (Some "active") ["base"; "b"]
            let b = commit "b"
            write (Some "active") (Some "active") ["base"; "a"; "b"]
            git ["add"; "."] |> ignore
            let tree = git ["write-tree"]
            let first = git ["commit-tree"; tree; "-p"; a; "-p"; b; "-m"; "first merge"]
            if reverse then write (Some "active") (Some "active") ["base"; "b"; "a"]
            git ["add"; "."] |> ignore
            let secondTree = git ["write-tree"]
            let second = git ["commit-tree"; secondTree; "-p"; b; "-p"; a; "-m"; "second merge"]
            git ["checkout"; "-qb"; "right"; second] |> ignore
            write (Some "active") (Some "blocked") (if reverse then ["base"; "b"; "a"; "right"] else ["base"; "a"; "b"; "right"])
            commit "right change" |> ignore
            git ["checkout"; "-qb"; "left"; first] |> ignore
            write (Some "complete") (Some "active") (if earlyLeft then ["base"; "a"; "left"; "b"] else ["base"; "a"; "b"; "left"])
            commit "left change" |> ignore
            Assert.equal 2 ((git ["merge-base"; "--all"; "HEAD"; "right"]).Split('\n').Length)
            Assert.equal 1 (CliHarness.run "git" ["-C"; root; "merge"; "--no-commit"; "right"] []).Exit
            Assert.equal "" (git ["ls-files"; "--unmerged"; "--"; ".ros/context/current.json"])
            test root context events
        finally CliHarness.removeTemporaryGitDirectory root

    let private crossedFixtureScoped reverse scope test = crossedFixturePolicy reverse scope false test
    let private crossedFixtureWithOrder reverse test = crossedFixtureScoped reverse (fun _ -> "") test
    let private crossedFixture test = crossedFixtureWithOrder false test
    let private distinctScope id = $",\"workItem\":\"fixture-{id}\""
    let private approval root =
        let path = "research/decisions/DF-fixture-approved-order.md"
        let full = Path.Combine(root, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, "Fixture owner approval: use incoming shared ordering, preserving all event bytes.\n")
        { ExpectedHead = CliHarness.git root ["rev-parse"; "HEAD"]
          ExpectedIncoming = CliHarness.git root ["rev-parse"; "MERGE_HEAD"]
          ApprovalPath = path
          ApprovalDigest = "sha256:" + (SHA256.HashData(File.ReadAllBytes full) |> Convert.ToHexString).ToLowerInvariant()
          Reason = "Fixture operator records explicit owner approval"
          OccurredAt = DateTimeOffset.UtcNow
          Actor = Actor.unknown }

    let tests =
        [ t "preserves both independent records, opaque fields and immutable events" (fun () ->
              let merged = merge left right |> ok
              Assert.equal ["old", "original"; "left", "left-record"; "right", "right-record"] merged.Items
              Assert.equal ["e0", "original"; "e1", "left-event"; "e2", "right-event"] merged.Events
              Assert.equal right.UpdatedAt merged.UpdatedAt)
          t "multiple real ancestors preserve every base and independent lifecycle updates" (fun () ->
              let combined = merge left right |> ok
              let l = {combined with Items = combined.Items |> List.map (fun (id, value) -> id, if id = "left" then "complete" else value)}
              let r = {combined with Items = combined.Items |> List.map (fun (id, value) -> id, if id = "right" then "blocked" else value)}
              let merged = WorkHistoryMerge.planMany [left; right] l r |> ok
              Assert.equal "complete" ((Map.ofList merged.Items)["left"])
              Assert.equal "blocked" ((Map.ofList merged.Items)["right"])
              Assert.equal combined.Events merged.Events
              WorkHistoryMerge.planMany [] l r |> refuses
              WorkHistoryMerge.planMany (List.replicate 33 baseline) l r |> refuses)
          t "multiple ancestors never choose between competing known lifecycle versions" (fun () ->
              let a = {baseline with Items = ["old", "complete"]}
              let b = {baseline with Items = ["old", "blocked"]}
              WorkHistoryMerge.planMany [a; b] a b |> refuses
              let combined = merge left right |> ok
              WorkHistoryMerge.planMany [left; right] {combined with Events = left.Events} combined |> refuses
              WorkHistoryMerge.planMany [left; right] {combined with Items = left.Items} combined |> refuses)
          t "successive merges retain a shared base as an immutable ordered subsequence" (fun () ->
              let first = merge left right |> ok
              let next = {right with UpdatedAt = at.AddMinutes 3.; Events = right.Events @ ["e3", "later-event"]}
              let merged = WorkHistoryMerge.plan right first next |> ok
              Assert.equal ["e0"; "e1"; "e2"; "e3"] (merged.Events |> List.map fst)
              Assert.equal (Map.ofList first.Items) (Map.ofList merged.Items))
          t "real criss-cross merge accepts one auto-merged path and preserves actual commit histories" (fun () -> crossedFixture (fun root context events ->
              let before = File.ReadAllText context, File.ReadAllText events
              let preview = FileWorkHistoryMerge.run root false |> ok
              Assert.equal 3 preview.Items.Length
              Assert.equal 5 preview.Events.Length
              Assert.equal before (File.ReadAllText context, File.ReadAllText events)
              FileWorkHistoryMerge.run root true |> ok |> ignore
              Assert.equal preview (FileWorkHistoryMerge.run root false |> ok)
              Assert.equal ["base"; "a"; "b"; "left"; "right"] (preview.Events |> List.map fst)
              for id, value in preview.Events do
                  Assert.isTrue ((File.ReadAllText events).Contains($"{{\"eventId\":\"{id}\",\"unknown\":42}}\n")) value
              let items = Map.ofList preview.Items
              Assert.isTrue (items["a"].Contains("complete")) "left state lost"
              Assert.isTrue (items["b"].Contains("blocked")) "right state lost"))
          t "auto-merged history edits are never overwritten" (fun () -> crossedFixture (fun root context events ->
              File.AppendAllText(context, " ")
              let before = File.ReadAllText context, File.ReadAllText events
              FileWorkHistoryMerge.run root true |> refuses
              Assert.equal before (File.ReadAllText context, File.ReadAllText events)))
          t "staged auto-merged history cannot invent lifecycle data" (fun () -> crossedFixture (fun root context events ->
              File.WriteAllText(context, (File.ReadAllText context).Replace("complete", "invented"))
              CliHarness.git root ["add"; ".ros/context/current.json"] |> ignore
              let before = File.ReadAllText context, File.ReadAllText events
              FileWorkHistoryMerge.run root true |> refuses
              Assert.equal before (File.ReadAllText context, File.ReadAllText events)))
          t "multiple-base interrupted apply recovers exact generated auto-merged content" (fun () -> crossedFixture (fun root context events ->
              let original = File.ReadAllText context, File.ReadAllText events
              FileWorkHistoryMerge.run root true |> ok |> ignore
              let merged = File.ReadAllText context, File.ReadAllText events
              File.WriteAllText(context, fst original)
              File.WriteAllText(events, snd original)
              WorkStateTransaction.prepare root [{Path = ".ros/events/events.jsonl"; Content = snd merged}; {Path = ".ros/context/current.json"; Content = fst merged}] |> ok
              File.WriteAllText(context, fst merged)
              FileWorkHistoryMerge.run root true |> ok |> ignore
              Assert.equal merged (File.ReadAllText context, File.ReadAllText events)))
          t "contradictory shared order across independent merges refuses without writes" (fun () -> crossedFixtureWithOrder true (fun root context events ->
              let original = File.ReadAllText context, File.ReadAllText events
              match FileWorkHistoryMerge.run root true with
              | Error error -> Assert.isTrue (error.Contains "cycle") error
              | Ok _ -> failwith "contradictory published ordering was rewritten"
              Assert.equal original (File.ReadAllText context, File.ReadAllText events)))
          t "explicit pinned amendment preserves bytes and adds one stable native audit event" (fun () -> crossedFixtureScoped true distinctScope (fun root context events ->
              let request = approval root
              let original = File.ReadAllText context, File.ReadAllText events
              FileWorkHistoryMerge.run root false |> refuses
              let preview = FileWorkHistoryMerge.runAmended root false request |> ok
              Assert.equal original (File.ReadAllText context, File.ReadAllText events)
              Assert.equal ["base"; "b"; "a"; "right"; "left"] (preview.Events |> List.take 5 |> List.map fst)
              Assert.equal 6 preview.Events.Length
              let auditId, auditText = List.last preview.Events
              let audit = JsonNode.Parse auditText
              let reconciliation = audit["reconciliation"]
              Assert.equal "work.history.order-reconciled" (audit["type"].GetValue<string>())
              Assert.equal request.ExpectedHead (reconciliation["head"].GetValue<string>())
              Assert.equal request.ExpectedIncoming (reconciliation["incoming"].GetValue<string>())
              Assert.equal request.ApprovalDigest (reconciliation["approvalDigest"].GetValue<string>())
              Assert.equal preview (FileWorkHistoryMerge.runAmended root true request |> ok)
              let written = File.ReadAllText events
              let storedAudit = written.TrimEnd().Split('\n') |> Array.last |> JsonNode.Parse :?> JsonObject
              Assert.isTrue (FileCheckpointRepository.eventIdMatches storedAudit) "native audit identity does not match content"
              for id in ["base"; "a"; "b"; "left"; "right"] do
                  Assert.isTrue (written.Contains($"{{\"eventId\":\"{id}\",\"unknown\":42{distinctScope id}}}\n")) "original event bytes changed"
              Assert.equal preview (FileWorkHistoryMerge.runAmended root true request |> ok)
              Assert.equal written (File.ReadAllText events)
              Assert.equal 24 auditId.Length))
          t "amendment refuses missing scopes and related-work order changes" (fun () ->
              for scope in [(fun _ -> ""); (fun _ -> ",\"workItem\":\"same-work\""); (fun _ -> ",\"execution\":\"same-execution\""); (fun _ -> ",\"telemetryExecutions\":[\"same-execution\"]")] do
                  crossedFixtureScoped true scope (fun root context events ->
                      let request = approval root
                      let original = File.ReadAllText context, File.ReadAllText events
                      FileWorkHistoryMerge.runAmended root true request |> refuses
                      Assert.equal original (File.ReadAllText context, File.ReadAllText events)))
          t "amendment never moves branch-only events past related shared observations" (fun () ->
              let scope id = distinctScope (if id = "left" then "b" else id)
              crossedFixturePolicy true scope true (fun root context events ->
                  let request = approval root
                  let original = File.ReadAllText context, File.ReadAllText events
                  FileWorkHistoryMerge.runAmended root true request |> refuses
                  Assert.equal original (File.ReadAllText context, File.ReadAllText events)))
          t "amendment refuses normalized-equal shared events with different raw bytes" (fun () ->
              let mutable generation = 0
              let scope id =
                  if id = "base" then generation <- generation + 1
                  (if generation = 7 then " " else "") + distinctScope id
              crossedFixtureScoped true scope (fun root context events ->
                  let request = approval root
                  let original = File.ReadAllText context, File.ReadAllText events
                  FileWorkHistoryMerge.runAmended root true request |> refuses
                  Assert.equal original (File.ReadAllText context, File.ReadAllText events)))
          t "amendment refuses stale approval bindings and invalid times without writes" (fun () -> crossedFixtureScoped true distinctScope (fun root context events ->
              let request = approval root
              let original = File.ReadAllText context, File.ReadAllText events
              for invalid in [ {request with ExpectedHead = String.replicate 40 "0"}
                               {request with ExpectedIncoming = String.replicate 40 "0"}
                               {request with ApprovalDigest = "sha256:" + String.replicate 64 "0"}
                               {request with ApprovalPath = "../approval.md"}
                               {request with Reason = ""}
                               {request with Reason = "bad\nreason"}
                               {request with OccurredAt = at.AddDays -1.}
                               {request with OccurredAt = DateTimeOffset.UtcNow.AddHours 1.}
                               {request with OccurredAt = request.OccurredAt.ToOffset(TimeSpan.FromHours 1.)} ] do
                  FileWorkHistoryMerge.runAmended root true invalid |> refuses
                  Assert.equal original (File.ReadAllText context, File.ReadAllText events)
              File.AppendAllText(Path.Combine(root, request.ApprovalPath), "Changed approval")
              FileWorkHistoryMerge.runAmended root true request |> refuses
              Assert.equal original (File.ReadAllText context, File.ReadAllText events)))
          t "amended interrupted transaction recovers without duplicating its audit" (fun () -> crossedFixtureScoped true distinctScope (fun root context events ->
              let request = approval root
              let original = File.ReadAllText context, File.ReadAllText events
              FileWorkHistoryMerge.runAmended root true request |> ok |> ignore
              let merged = File.ReadAllText context, File.ReadAllText events
              File.WriteAllText(context, fst original)
              File.WriteAllText(events, snd original)
              WorkStateTransaction.prepare root [{Path = ".ros/events/events.jsonl"; Content = snd merged}; {Path = ".ros/context/current.json"; Content = fst merged}] |> ok
              File.WriteAllText(context, fst merged)
              FileWorkHistoryMerge.runAmended root true request |> ok |> ignore
              Assert.equal merged (File.ReadAllText context, File.ReadAllText events)))
          t "native CLI requires complete amendment bindings and retains read-only preview" (fun () -> crossedFixtureScoped true distinctScope (fun root context events ->
              let request = approval root
              let arguments = ["work"; "history"; "merge"; "--canonical-incoming-order"
                               "--expected-head"; request.ExpectedHead; "--expected-incoming"; request.ExpectedIncoming
                               "--approval-path"; request.ApprovalPath; "--approval-digest"; request.ApprovalDigest
                               "--reason"; request.Reason; "--occurred-at"; request.OccurredAt.ToString("O")]
              let original = File.ReadAllText context, File.ReadAllText events
              for invalid in [ ["work"; "history"; "merge"; "--canonical-incoming-order"]
                               arguments @ ["--reason"; "duplicate"]
                               arguments @ ["--unknown"] ] do
                  Assert.equal 2 (CliHarness.rosWith root [] invalid).Exit
                  Assert.equal original (File.ReadAllText context, File.ReadAllText events)
              let preview = CliHarness.rosWith root [] arguments
              Assert.equal 0 preview.Exit
              Assert.isTrue (preview.Out.Contains "preview") preview.Out
              Assert.equal original (File.ReadAllText context, File.ReadAllText events)
              let applied = CliHarness.rosWith root [] (arguments @ ["--apply"])
              Assert.equal 0 applied.Exit
              Assert.equal 6 (File.ReadAllLines events).Length))
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
              let originalStage = CliHarness.git root ["rev-parse"; ":2:.ros/context/current.json"]
              Assert.isTrue (replacement <> originalStage) "fixture branches must have different committed histories"
              let start = ProcessStartInfo("git", WorkingDirectory = root, RedirectStandardInput = true, UseShellExecute = false)
              start.ArgumentList.Add "update-index"
              start.ArgumentList.Add "--index-info"
              use child = Process.Start start
              // Git's index-info protocol uses LF, not the platform text newline.
              child.StandardInput.Write($"100644 {replacement} 2\t.ros/context/current.json\n")
              child.StandardInput.Close()
              child.WaitForExit()
              Assert.equal 0 child.ExitCode
              let stages = CliHarness.git root ["ls-files"; "--stage"; "--"; ".ros/context/current.json"]
              Assert.isTrue (stages.Contains($"100644 {replacement} 2\t.ros/context/current.json", StringComparison.Ordinal)) "forged stage fixture was not installed"
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
