namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes
open Ros.Application.Git
open Ros.Application.Work
open Ros.Domain.Git
open Ros.Domain.Work

/// PRAXIS-CONT-12: a work item never claims, and is never held back by,
/// commits another item's recorded checkpoint or reconciliation owns.
[<RequireQualifiedAccess>]
module CommitOwnershipTests =
    open PraxisCli

    let private filter = PathFilterConfig.defaultConfig
    let private change commit paths = { Commit = commit; IsMerge = false; Paths = paths }
    let private claim item commit paths = { WorkItemId = item; Commit = commit; Paths = paths }
    let private sha (c: char) = System.String(c, 40)
    let private commitId (c: char) = CommitId.tryParse (sha c) |> Option.get

    let private unused name : 'a = failwith $"{name} is not used by this test"

    /// A durability port whose only live read is the raw difference.
    let private port (raw: string list) : GitDurability =
        { Head = fun () -> unused "Head"
          Upstream = fun _ -> unused "Upstream"
          Remote = fun _ -> unused "Remote"
          RemoteBranch = fun _ _ -> unused "RemoteBranch"
          Relation = fun _ _ -> unused "Relation"
          ChangedPaths = fun _ _ -> GitRead.Observed raw
          Status = fun () -> unused "Status" }

    let private history (changes: GitRead<CommitChange list>) (reachable: Map<string, string list>) : GitCommitHistory =
        { Changes = fun _ _ -> changes
          Reachable = fun _ right -> GitRead.Observed(reachable.TryFind right.Value |> Option.defaultValue []) }

    let private policy ownership =
        { PathFilter = filter
          BaselineDirtyPaths = []
          Ownership = ownership }

    let private scoped raw changes reachable claims item =
        let evidence = { History = history changes reachable; Claims = claims }
        CheckpointOwnership.changedPaths (port raw) (policy (Some evidence)) item (commitId 'a') (commitId 'f')

    // ---- CLI scenario helpers ----

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"

    let private withRepository (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "commit-ownership"

        try
            let _, clone = installedRepository parent "clone-a"
            test clone
        finally
            GitFixture.cleanup parent

    let private start clone (id: string) =
        run clone (Some agentA) [ "work"; "start"; "--id"; id; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore

    let private checkpoint clone (id: string) =
        run clone (Some agentA) [ "work"; "checkpoint"; "--id"; id; "--occurred-at"; now (); "--summary"; "done"; "--next-action"; "complete" ]

    let private complete clone (id: string) =
        run clone (Some agentA) [ "work"; "complete"; "--id"; id; "--occurred-at"; now (); "--evidence"; "implementation=src/one.txt"; "--evidence"; "tests=README.md" ]

    let private work clone (path: string) (content: string) =
        GitFixture.write clone path content
        pushAll clone $"change {path}" |> ignore

    let private state clone (id: string) =
        let view = run clone None [ "work"; "context"; id ] |> ok
        text (view.Json["workItems"].[0].["semanticState"])

    /// The paths the latest `work.checkpointed` event of an item attributed.
    let private lastCheckpointPaths clone (id: string) =
        File.ReadAllLines(Path.Combine(clone, ".ros", "events", "events.jsonl"))
        |> Array.choose (fun line ->
            match JsonNode.Parse line with
            | :? JsonObject as node when node["type"].GetValue<string>() = "work.checkpointed" && node["workItem"].GetValue<string>() = id ->
                Some(node["paths"].AsArray() |> Seq.map (fun path -> path.GetValue<string>()) |> Seq.toList)
            | _ -> None)
        |> Array.last

    /// Two items on one branch: ONE checkpoints its work; TWO then commits,
    /// checkpoints and completes its own work on the same branch.
    let private twoItemsOnOneBranch clone =
        start clone "FEAT-1"
        work clone "src/one.txt" "one\n"
        checkpoint clone "FEAT-1" |> ok |> ignore
        start clone "FEAT-2"
        work clone "src/two.txt" "two\n"
        checkpoint clone "FEAT-2" |> ok |> ignore
        complete clone "FEAT-2" |> ok |> ignore
        pushAll clone "praxis state" |> ignore

    let tests =
        [ { Name = "ownership: a commit another item claimed in full is not this item's"
            Run =
              fun () ->
                  let claims = [ claim "OTHER" (sha 'b') [ "src/other.fs" ] ]
                  let owned = CommitOwnership.ownPaths filter claims (fun _ -> [ "OTHER" ]) [ change "b" [ "src/other.fs" ] ]
                  Assert.empty owned }
          { Name = "ownership: a path no covering item claimed stays with this item"
            Run =
              fun () ->
                  let claims = [ claim "OTHER" (sha 'b') [ "src/other.fs" ] ]
                  let owned = CommitOwnership.ownPaths filter claims (fun _ -> [ "OTHER" ]) [ change "b" [ "src/other.fs"; "src/mine.fs" ] ]
                  Assert.equal [ "src/mine.fs"; "src/other.fs" ] owned }
          { Name = "ownership: claims only count from items whose evidence contains the commit"
            Run =
              fun () ->
                  let claims = [ claim "OTHER" (sha 'b') [ "src/other.fs" ] ]
                  let owned = CommitOwnership.ownPaths filter claims (fun _ -> []) [ change "c" [ "src/other.fs" ] ]
                  Assert.equal [ "src/other.fs" ] owned }
          { Name = "ownership: paths split across several covering items are owned together; merges carry no own change"
            Run =
              fun () ->
                  let claims = [ claim "A" (sha 'b') [ "src/a.fs" ]; claim "B" (sha 'c') [ "src/b.fs" ] ]
                  let changes = [ change "b" [ "src/a.fs"; "src/b.fs" ]; { Commit = "m"; IsMerge = true; Paths = [] } ]
                  Assert.empty (CommitOwnership.ownPaths filter claims (fun _ -> [ "A"; "B" ]) changes) }
          { Name = "ownership scope: without evidence, or with only this item's own claims, the raw difference is used"
            Run =
              fun () ->
                  let raw = [ "src/tree-diff.fs" ]
                  Assert.equal (GitRead.Observed raw) (CheckpointOwnership.changedPaths (port raw) (policy None) "ME" (commitId 'a') (commitId 'f'))
                  let own = scoped raw (GitRead.Observed [ change (sha 'b') [ "src/x.fs" ] ]) (Map.ofList [ sha 'b', [ sha 'b' ] ]) [ claim "ME" (sha 'b') [ "src/x.fs" ] ] "ME"
                  Assert.equal (GitRead.Observed raw) own }
          { Name = "ownership scope: when no other claim covers the range, behaviour is exactly the raw difference"
            Run =
              fun () ->
                  let raw = [ "src/tree-diff.fs" ]
                  let result = scoped raw (GitRead.Observed [ change (sha 'b') [ "src/x.fs" ] ]) (Map.ofList [ sha 'c', [ sha 'c' ] ]) [ claim "OTHER" (sha 'c') [ "src/x.fs" ] ] "ME"
                  Assert.equal (GitRead.Observed raw) result }
          { Name = "ownership scope: another item's covered commits are removed; unreadable history falls back to the raw difference"
            Run =
              fun () ->
                  let changes = GitRead.Observed [ change (sha 'b') [ "src/theirs.fs" ]; change (sha 'c') [ "src/mine.fs" ] ]
                  let reachable = Map.ofList [ sha 'b', [ sha 'b' ] ]
                  let result = scoped [ "src/theirs.fs"; "src/mine.fs" ] changes reachable [ claim "OTHER" (sha 'b') [ "src/theirs.fs" ] ] "ME"
                  Assert.equal (GitRead.Observed [ "src/mine.fs" ]) result

                  let failure = { Operation = "git rev-list"; Reason = GitUnavailableReason.CommandFailed; Message = "boom"; ExitCode = Some 128 }
                  let fallback = scoped [ "src/raw.fs" ] (GitRead.Unavailable failure) reachable [ claim "OTHER" (sha 'b') [ "src/theirs.fs" ] ] "ME"
                  Assert.equal (GitRead.Observed [ "src/raw.fs" ]) fallback }
          { Name = "CONT-12: another item's checkpointed commit after a checkpoint neither blocks completion nor is re-attributed"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      twoItemsOnOneBranch clone
                      Assert.equal [ "src/two.txt" ] (lastCheckpointPaths clone "FEAT-2")
                      // A fresh checkpoint of ONE claims nothing of TWO's work.
                      checkpoint clone "FEAT-1" |> ok |> ignore
                      Assert.isTrue (not (lastCheckpointPaths clone "FEAT-1" |> List.contains "src/two.txt")) "FEAT-1 must not claim FEAT-2's path"
                      pushAll clone "praxis state" |> ignore
                      complete clone "FEAT-1" |> ok |> ignore
                      Assert.equal "complete" (state clone "FEAT-1")) }
          { Name = "CONT-12: completion no longer demands a checkpoint that would claim another item's commits"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      twoItemsOnOneBranch clone
                      complete clone "FEAT-1" |> ok |> ignore
                      Assert.equal "complete" (state clone "FEAT-1")) }
          { Name = "CONT-12: blocking after another item's checkpointed work needs no checkpoint or unrecoverable reason"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      twoItemsOnOneBranch clone
                      run clone (Some agentA) [ "work"; "block"; "--id"; "FEAT-1"; "--reason"; "waiting on review"; "--occurred-at"; now () ] |> ok |> ignore
                      Assert.equal "blocked" (state clone "FEAT-1")) }
          { Name = "CONT-12: unowned work after a checkpoint is still this item's and still refused"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      twoItemsOnOneBranch clone
                      work clone "src/unclaimed.txt" "nobody checkpointed this\n"
                      let refused = complete clone "FEAT-1"
                      Assert.equal 1 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "not the final HEAD") refused.Error
                      Assert.equal "active" (state clone "FEAT-1")) }
          { Name = "CONT-12: this item's own later work is never excused by another item's checkpoint"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      twoItemsOnOneBranch clone
                      work clone "src/one.txt" "one, revised after TWO\n"
                      let refused = complete clone "FEAT-1"
                      Assert.equal 1 refused.ExitCode
                      Assert.isTrue (refused.Error.Contains "not the final HEAD") refused.Error) } ]
