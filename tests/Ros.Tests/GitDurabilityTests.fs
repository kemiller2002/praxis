namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open Ros.Application.Git
open Ros.Application.Work
open Ros.Domain.Git
open Ros.Domain.Work
open Ros.Infrastructure.Git

/// Real Git fixtures for durability observations (PRAXIS-CONT-02-GIT): a
/// temporary bare remote and clones of it, so every observation is what
/// Git itself reports.
module GitFixture =
    let git (directory: string) (arguments: string list) =
        let startInfo = ProcessStartInfo("git")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
        startInfo.ArgumentList.Add "-C"
        startInfo.ArgumentList.Add directory
        arguments |> List.iter startInfo.ArgumentList.Add
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()

        if child.ExitCode <> 0 then
            let command = String.concat " " arguments
            failwith $"git {command} failed: {error}"

        output.Trim()

    let temporaryDirectory (label: string) =
        let path = Path.Combine(Path.GetTempPath(), $"ros-{label}-{Guid.NewGuid():N}")
        Directory.CreateDirectory path |> ignore
        path

    let configureIdentity directory =
        git directory [ "config"; "user.email"; "fixture@example.test" ] |> ignore
        git directory [ "config"; "user.name"; "Fixture" ] |> ignore
        git directory [ "config"; "commit.gpgsign"; "false" ] |> ignore

    let write (directory: string) (relative: string) (content: string) =
        let path = Path.Combine(directory, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let commitAll directory (message: string) =
        git directory [ "add"; "-A" ] |> ignore
        git directory [ "commit"; "-q"; "-m"; message ] |> ignore
        git directory [ "rev-parse"; "HEAD" ]

    /// A bare remote and one working clone on branch `feature/x`, pushed with
    /// an upstream. Returns (parent directory, bare remote, clone).
    let pushedClone () =
        let parent = temporaryDirectory "durability"
        let bare = Path.Combine(parent, "remote.git")
        let clone = Path.Combine(parent, "clone-a")
        Directory.CreateDirectory bare |> ignore
        git bare [ "init"; "-q"; "--bare"; "-b"; "main" ] |> ignore
        Directory.CreateDirectory clone |> ignore
        git clone [ "init"; "-q"; "-b"; "main" ] |> ignore
        configureIdentity clone
        git clone [ "remote"; "add"; "origin"; bare ] |> ignore
        write clone "README.md" "hello\n"
        commitAll clone "initial" |> ignore
        git clone [ "switch"; "-q"; "-c"; "feature/x" ] |> ignore
        write clone "src/a.txt" "a\n"
        commitAll clone "work" |> ignore
        git clone [ "push"; "-q"; "-u"; "origin"; "feature/x" ] |> ignore
        parent, bare, clone

    let secondClone (parent: string) (bare: string) (name: string) =
        let clone = Path.Combine(parent, name)
        git parent [ "clone"; "-q"; "--branch"; "feature/x"; bare; clone ] |> ignore
        configureIdentity clone
        clone

    let cleanup (parent: string) =
        try
            Directory.Delete(parent, true)
        with _ ->
            ()

[<RequireQualifiedAccess>]
module GitDurabilityTests =
    open GitFixture

    let private commit (value: string) = CommitId.tryParse value |> Option.get

    let private policy =
        { PathFilter = { PathFilterConfig.defaultConfig with IgnoredPatterns = PathFilterConfig.defaultConfig.IgnoredPatterns @ [ "registries/**" ] }
          BaselineDirtyPaths = []
          Ownership = None }

    let private candidate =
        { WorkItemId = "PRAXIS-CONT-02"
          Repository = "fixture"
          Summary = "fixture work"
          NextAction = "next"
          StepId = None
          OccurredAt = "2026-09-29T10:00:00.000Z" }

    let private verify clone =
        CheckpointOperations.verify
            (ProcessGitDurability.create clone)
            policy
            candidate
            (Some LiveWorkState.Active)
            (ExecutionObservation.Resolved("EXE-A", []))

    let private rejection clone =
        match verify clone with
        | Ok _ -> "accepted"
        | Error rejections -> rejections |> List.map CheckpointRejection.code |> String.concat ","

    let private withClone (test: string -> string -> string -> unit) =
        let parent, bare, clone = pushedClone ()

        try
            test parent bare clone
        finally
            cleanup parent

    let tests =
        [ { Name = "git durability: HEAD on a branch, detached HEAD, unborn HEAD and a non-repository are observed as such"
            Run =
              fun () ->
                  withClone (fun parent _ clone ->
                      let durability = ProcessGitDurability.create clone
                      let head = git clone [ "rev-parse"; "HEAD" ]

                      match durability.Head() with
                      | GitRead.Observed(HeadState.OnBranch("feature/x", observed)) -> Assert.equal head observed.Value
                      | other -> failwith $"{other}"

                      git clone [ "switch"; "-q"; "--detach"; "HEAD" ] |> ignore

                      match durability.Head() with
                      | GitRead.Observed(HeadState.Detached observed) -> Assert.equal head observed.Value
                      | other -> failwith $"{other}"

                      let fresh = Path.Combine(parent, "fresh")
                      Directory.CreateDirectory fresh |> ignore
                      git fresh [ "init"; "-q"; "-b"; "trunk" ] |> ignore

                      match (ProcessGitDurability.create fresh).Head() with
                      | GitRead.Observed(HeadState.Unborn(Some "trunk")) -> ()
                      | other -> failwith $"{other}"

                      let plain = Path.Combine(parent, "plain")
                      Directory.CreateDirectory plain |> ignore

                      match (ProcessGitDurability.create plain).Head() with
                      | GitRead.Unavailable failure -> Assert.equal GitUnavailableReason.NotRepository failure.Reason
                      | other -> failwith $"{other}") }
          { Name = "git durability: upstream tracking, no upstream, and an upstream naming a missing remote"
            Run =
              fun () ->
                  withClone (fun _ bare clone ->
                      let durability = ProcessGitDurability.create clone

                      match durability.Upstream "feature/x" with
                      | GitRead.Observed(UpstreamState.Tracking(remote, "feature/x")) ->
                          Assert.equal "origin" remote.Name
                          Assert.equal (Some bare) remote.Url
                      | other -> failwith $"{other}"

                      Assert.equal (GitRead.Observed UpstreamState.NoUpstream) (durability.Upstream "main")
                      git clone [ "config"; "branch.main.remote"; "ghost" ] |> ignore
                      git clone [ "config"; "branch.main.merge"; "refs/heads/main" ] |> ignore
                      Assert.equal (GitRead.Observed(UpstreamState.RemoteNotConfigured("ghost", "main"))) (durability.Upstream "main")) }
          { Name = "git durability: the remote branch head is read from the remote itself, and a missing branch is Missing"
            Run =
              fun () ->
                  withClone (fun _ _ clone ->
                      let durability = ProcessGitDurability.create clone
                      let origin = { Name = "origin"; Url = None }
                      let head = git clone [ "rev-parse"; "HEAD" ]
                      Assert.equal (RemoteBranchObservation.At(commit head)) (durability.RemoteBranch origin "feature/x")
                      Assert.equal RemoteBranchObservation.Missing (durability.RemoteBranch origin "no/such-branch")) }
          { Name = "git durability: an unreachable remote is Unreachable, never Missing and never success"
            Run =
              fun () ->
                  withClone (fun parent _ clone ->
                      git clone [ "remote"; "add"; "broken"; Path.Combine(parent, "does-not-exist.git") ] |> ignore

                      match (ProcessGitDurability.create clone).RemoteBranch { Name = "broken"; Url = None } "feature/x" with
                      | RemoteBranchObservation.Unreachable _ -> ()
                      | other -> failwith $"{other}") }
          { Name = "git durability: commit relations are same, ahead, behind, diverged, or object-missing"
            Run =
              fun () ->
                  withClone (fun _ _ clone ->
                      let durability = ProcessGitDurability.create clone
                      let first = commit (git clone [ "rev-parse"; "HEAD" ])
                      write clone "src/b.txt" "b\n"
                      let second = commit (commitAll clone "second")
                      Assert.equal (CommitRelationObservation.Related CommitRelation.Same) (durability.Relation first first)
                      Assert.equal (CommitRelationObservation.Related(CommitRelation.Behind 1)) (durability.Relation first second)
                      Assert.equal (CommitRelationObservation.Related(CommitRelation.Ahead 1)) (durability.Relation second first)
                      git clone [ "switch"; "-q"; "--detach"; first.Value ] |> ignore
                      write clone "src/c.txt" "c\n"
                      let third = commit (commitAll clone "third")
                      Assert.equal (CommitRelationObservation.Related(CommitRelation.Diverged(1, 1))) (durability.Relation second third)
                      let unknown = commit (String.replicate 40 "e")
                      Assert.equal (CommitRelationObservation.ObjectMissing unknown) (durability.Relation first unknown)) }
          { Name = "git durability: changed paths between commits include both sides of a rename"
            Run =
              fun () ->
                  withClone (fun _ _ clone ->
                      let durability = ProcessGitDurability.create clone
                      let first = commit (git clone [ "rev-parse"; "HEAD" ])
                      git clone [ "mv"; "src/a.txt"; "src/renamed.txt" ] |> ignore
                      let second = commit (commitAll clone "rename")

                      match durability.ChangedPaths first second with
                      | GitRead.Observed paths -> Assert.equal [ "src/a.txt"; "src/renamed.txt" ] (List.sort paths)
                      | other -> failwith $"{other}") }
          { Name = "git durability: parsers accept exact output and refuse malformed output"
            Run =
              fun () ->
                  let sha = String.replicate 40 "a"
                  Assert.equal (Ok(Some(commit sha))) (GitDurabilityParser.parseLsRemote "x" $"{sha}\trefs/heads/x\n")
                  Assert.equal (Ok None) (GitDurabilityParser.parseLsRemote "x" "")
                  Assert.equal (Ok None) (GitDurabilityParser.parseLsRemote "x" $"{sha}\trefs/heads/other/x\n")
                  Assert.isTrue (GitDurabilityParser.parseLsRemote "x" "garbage" |> Result.isError) "garbage accepted"
                  Assert.isTrue (GitDurabilityParser.parseLsRemote "x" "abc\trefs/heads/x\n" |> Result.isError) "short sha accepted"
                  Assert.equal (Ok(CommitRelation.Diverged(2, 3))) (GitDurabilityParser.parseLeftRightCount "2\t3\n")
                  Assert.isTrue (GitDurabilityParser.parseLeftRightCount "x" |> Result.isError) "malformed count accepted"
                  Assert.equal [ "a b.txt"; "c" ] (GitDurabilityParser.parseNameList "a b.txt\000c\000") }
          { Name = "git durability: remote URLs never carry credentials"
            Run =
              fun () ->
                  Assert.equal "https://github.com/o/r.git" (GitDurabilityParser.sanitizeUrl "https://x-access-token:ghs_secret@github.com/o/r.git")
                  Assert.equal "https://github.com/o/r.git" (GitDurabilityParser.sanitizeUrl "https://user@github.com/o/r.git")
                  Assert.equal "git@github.com:o/r.git" (GitDurabilityParser.sanitizeUrl "git@github.com:o/r.git")
                  Assert.equal "/tmp/remote.git" (GitDurabilityParser.sanitizeUrl "/tmp/remote.git") }
          { Name = "checkpoint verification (real Git): a pushed clean HEAD verifies with the exact remote commit"
            Run =
              fun () ->
                  withClone (fun _ _ clone ->
                      match verify clone with
                      | Ok checkpoint ->
                          let head = git clone [ "rev-parse"; "HEAD" ]
                          let location = DurableLocation.git checkpoint.Location
                          Assert.equal head location.LocalCommit.Value
                          Assert.equal head location.RemoteCommit.Value
                          Assert.equal "feature/x" location.RemoteBranch
                      | Error rejections -> failwith $"{rejections}") }
          { Name = "checkpoint verification (real Git): an unpushed commit is refused as local-ahead"
            Run =
              fun () ->
                  withClone (fun _ _ clone ->
                      write clone "src/b.txt" "b\n"
                      commitAll clone "unpushed" |> ignore
                      Assert.equal "local-ahead" (rejection clone)) }
          { Name = "checkpoint verification (real Git): a remote ahead is refused, and so is a remote commit not fetched"
            Run =
              fun () ->
                  withClone (fun parent bare clone ->
                      let other = secondClone parent bare "clone-b"
                      write other "src/other.txt" "other\n"
                      commitAll other "remote work" |> ignore
                      git other [ "push"; "-q" ] |> ignore
                      // Not fetched yet: the remote head is unknown locally.
                      Assert.equal "not-remotely-visible" (rejection clone)
                      git clone [ "fetch"; "-q" ] |> ignore
                      Assert.equal "remote-ahead" (rejection clone)) }
          { Name = "checkpoint verification (real Git): diverged histories are refused"
            Run =
              fun () ->
                  withClone (fun parent bare clone ->
                      let other = secondClone parent bare "clone-b"
                      write other "src/other.txt" "other\n"
                      commitAll other "remote work" |> ignore
                      git other [ "push"; "-q" ] |> ignore
                      write clone "src/mine.txt" "mine\n"
                      commitAll clone "local work" |> ignore
                      git clone [ "fetch"; "-q" ] |> ignore
                      Assert.equal "diverged" (rejection clone)) }
          { Name = "checkpoint verification (real Git): a deleted remote branch and no upstream are distinct refusals"
            Run =
              fun () ->
                  withClone (fun _ bare clone ->
                      git bare [ "branch"; "-D"; "feature/x" ] |> ignore
                      Assert.equal "remote-branch-missing" (rejection clone)
                      git clone [ "branch"; "--unset-upstream" ] |> ignore
                      Assert.equal "no-upstream" (rejection clone)) }
          { Name = "checkpoint verification (real Git): an unreachable remote and a detached HEAD are refused"
            Run =
              fun () ->
                  withClone (fun parent _ clone ->
                      git clone [ "remote"; "set-url"; "origin"; Path.Combine(parent, "gone.git") ] |> ignore
                      Assert.equal "remote-unreachable" (rejection clone))

                  withClone (fun _ _ clone ->
                      git clone [ "switch"; "-q"; "--detach"; "HEAD" ] |> ignore
                      Assert.equal "detached-head" (rejection clone)) }
          { Name = "checkpoint verification (real Git): dirty meaningful work is refused; Praxis state and registries are not"
            Run =
              fun () ->
                  withClone (fun _ _ clone ->
                      write clone ".ros/context/current.json" "{}\n"
                      write clone "registries/index.json" "{}\n"
                      Assert.equal "accepted" (rejection clone)
                      write clone "src/a.txt" "changed\n"
                      Assert.equal "uncommitted-changes" (rejection clone)) }
          { Name = "recoverability (real Git): a later force-push is observed as not-contained while the checkpoint is kept"
            Run =
              fun () ->
                  withClone (fun _ _ clone ->
                      let durability = ProcessGitDurability.create clone
                      let checkpoint = verify clone |> Result.defaultWith (fun r -> failwith $"{r}")
                      let recorded = { CheckpointId = "evt"; Recorded = checkpoint }
                      let current = CheckpointOperations.assess durability policy "W-1" LiveWorkState.Active (Some recorded)
                      Assert.equal "current" (CheckpointFreshness.code current.Freshness)
                      git clone [ "commit"; "-q"; "--amend"; "-m"; "rewritten" ] |> ignore
                      git clone [ "push"; "-q"; "--force"; "origin"; "feature/x" ] |> ignore
                      let rewritten = CheckpointOperations.assess durability policy "W-1" LiveWorkState.Active (Some recorded)
                      Assert.equal "not-contained" (CurrentRecoverability.code rewritten.CurrentRecoverability.Value)
                      Assert.equal "head-diverged-from-checkpoint" (CheckpointFreshness.code rewritten.Freshness)
                      Assert.equal checkpoint.Commit rewritten.Checkpoint.Value.Recorded.Commit) } ]
