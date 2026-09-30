namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open Ros.Contracts.Integration
open Ros.Domain.Git
open Ros.Domain.Integration
open Ros.Infrastructure.Integration

[<RequireQualifiedAccess>]
module MergeReadinessTests =
    let private commit value = CommitId.tryParse value |> Option.get

    let private policy =
        { Enabled = true
          RequiredChecks = [ "repository-validation"; "packaged-lifecycle" ]
          OptionalChecks = [ "site-preview" ]
          RequireCleanWorkingTree = true
          RequireRemoteCandidate = true }

    let private shaA = commit (String.replicate 40 "a")
    let private shaB = commit (String.replicate 40 "b")

    let private successfulEvidence candidate =
        { CandidateCommit = Some candidate
          RemoteCandidateCurrent = Some true
          Checks =
            [ { Id = "repository-validation"; State = CheckState.Succeeded; Commit = Some candidate }
              { Id = "packaged-lifecycle"; State = CheckState.Succeeded; Commit = Some candidate } ] }

    let private observation candidate =
        { Head = Some candidate
          WorkingTreeClean = Some true
          Evidence = successfulEvidence candidate }

    let private blockers decision =
        match decision with
        | MergeReadinessDecision.NotReady(_, blockers) -> blockers |> List.map MergeBlocker.code
        | _ -> []

    let private withState state =
        let evidence =
            { successfulEvidence shaA with
                Checks =
                    [ { Id = "repository-validation"; State = state; Commit = Some shaA }
                      { Id = "packaged-lifecycle"; State = CheckState.Succeeded; Commit = Some shaA } ] }

        MergeReadiness.decide policy { observation shaA with Evidence = evidence }

    let private write (root: string) (relative: string) (content: string) =
        let path = Path.Combine(root, relative)
        let directory = Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) then Directory.CreateDirectory directory |> ignore
        File.WriteAllText(path, content)

    let private withRepository run =
        let root = GitFixture.temporaryDirectory "merge-readiness"

        try
            GitFixture.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
            GitFixture.configureIdentity root

            write
                root
                "ros.json"
                """{
  "mergeReadiness": {
    "enabled": true,
    "requireCleanWorkingTree": true,
    "requireRemoteCandidate": true,
    "requiredChecks": ["repository-validation", "packaged-lifecycle"],
    "optionalChecks": ["site-preview"]
  }
}
"""

            write root "README.md" "fixture\n"
            let head = GitFixture.commitAll root "fixture"
            run root head
        finally
            GitFixture.cleanup root

    let private evidenceFile candidate checks =
        let path = Path.Combine(Path.GetTempPath(), $"praxis-merge-evidence-{Guid.NewGuid():N}.json")

        let rows =
            checks
            |> List.map (fun (id, state, commit) ->
                $"""{{"id":"{id}","state":"{state}","commit":"{commit}"}}""")
            |> String.concat ","

        File.WriteAllText(
            path,
            $"""{{"schema":"praxis.merge-readiness/1","candidateCommit":"{candidate}","remoteCandidateCurrent":true,"checks":[{rows}]}}"""
        )

        path


    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "package.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "src", "Ros.Domain")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private fingerprint root =
        let files =
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.filter (fun path -> not (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")))
            |> Array.sort
            |> Array.map (fun path -> Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))

        String.Join("\n", files), GitFixture.git root [ "status"; "--porcelain"; "--untracked-files=all" ], GitFixture.git root [ "rev-parse"; "HEAD" ]

    let tests =
        [ { Name = "merge readiness: all required checks on the exact clean candidate are ready"
            Run =
              fun () ->
                  match MergeReadiness.decide policy (observation shaA) with
                  | MergeReadinessDecision.Ready candidate -> Assert.equal shaA candidate
                  | other -> failwith $"{other}" }

          { Name = "merge readiness: every non-success required-check state blocks"
            Run =
              fun () ->
                  for state in
                      [ CheckState.Failed
                        CheckState.Pending
                        CheckState.Cancelled
                        CheckState.Skipped
                        CheckState.Missing
                        CheckState.Unknown "provider-new-state" ] do
                      let codes = withState state |> blockers
                      Assert.equal [ "required-check-not-successful" ] codes }

          { Name = "merge readiness: missing, duplicated and unbound required checks fail closed"
            Run =
              fun () ->
                  let missing =
                      { observation shaA with
                          Evidence =
                            { successfulEvidence shaA with
                                Checks = [ { Id = "repository-validation"; State = CheckState.Succeeded; Commit = Some shaA } ] } }

                  Assert.equal [ "required-check-missing" ] (MergeReadiness.decide policy missing |> blockers)

                  let duplicated =
                      { observation shaA with
                          Evidence =
                            { successfulEvidence shaA with
                                Checks =
                                    [ { Id = "repository-validation"; State = CheckState.Succeeded; Commit = Some shaA }
                                      { Id = "repository-validation"; State = CheckState.Succeeded; Commit = Some shaA }
                                      { Id = "packaged-lifecycle"; State = CheckState.Succeeded; Commit = Some shaA } ] } }

                  Assert.equal [ "duplicate-check-evidence" ] (MergeReadiness.decide policy duplicated |> blockers)

                  let unbound =
                      { observation shaA with
                          Evidence =
                            { successfulEvidence shaA with
                                Checks =
                                    [ { Id = "repository-validation"; State = CheckState.Succeeded; Commit = None }
                                      { Id = "packaged-lifecycle"; State = CheckState.Succeeded; Commit = Some shaA } ] } }

                  Assert.equal [ "required-check-commit-unknown" ] (MergeReadiness.decide policy unbound |> blockers) }

          { Name = "merge readiness: a later commit makes previously green evidence stale"
            Run =
              fun () ->
                  let changed =
                      { observation shaB with
                          Evidence = successfulEvidence shaA }

                  let codes = MergeReadiness.decide policy changed |> blockers
                  Assert.isTrue (codes |> List.contains "candidate-does-not-match-head") "candidate/head mismatch was not detected" }

          { Name = "merge readiness: checkpoint-like durability facts cannot substitute for CI or clean exact-candidate evidence"
            Run =
              fun () ->
                  let noChecks =
                      { Head = Some shaA
                        WorkingTreeClean = Some true
                        Evidence =
                            { CandidateCommit = Some shaA
                              RemoteCandidateCurrent = Some true
                              Checks = [] } }

                  let codes = MergeReadiness.decide policy noChecks |> blockers
                  Assert.equal [ "required-check-missing"; "required-check-missing" ] codes }

          { Name = "merge readiness: optional check failure does not block"
            Run =
              fun () ->
                  let evidence =
                      { successfulEvidence shaA with
                          Checks =
                            (successfulEvidence shaA).Checks
                            @ [ { Id = "site-preview"; State = CheckState.Failed; Commit = Some shaA } ] }

                  match MergeReadiness.decide policy { observation shaA with Evidence = evidence } with
                  | MergeReadinessDecision.Ready _ -> ()
                  | other -> failwith $"{other}" }



          { Name = "merge readiness: malformed configured policy fails closed instead of disabling enforcement"
            Run =
              fun () ->
                  let root = GitFixture.temporaryDirectory "merge-readiness-policy"

                  try
                      File.WriteAllText(
                          Path.Combine(root, "ros.json"),
                          """{"mergeReadiness":{"enabled":"yes","requiredChecks":"repository-validation"}}"""
                      )

                      match FileMergeReadinessRepository.readPolicy root with
                      | Ok policy -> failwith $"malformed policy was accepted as {policy}"
                      | Error message ->
                          Assert.isTrue (message.Contains "mergeReadiness") "policy error did not identify mergeReadiness"
                  finally
                      GitFixture.cleanup root }

          { Name = "merge readiness: normalized unknown state round-trips without nesting unknown prefixes"
            Run =
              fun () ->
                  let state = CheckState.tryParse "unknown:provider-new-state" |> Option.get
                  Assert.equal "unknown:provider-new-state" (CheckState.code state) }

          { Name = "merge readiness: normalized evidence parses exact commit and provider-neutral states"
            Run =
              fun () ->
                  let raw =
                      $"""{{"schema":"praxis.merge-readiness/1","candidateCommit":"{shaA.Value}","remoteCandidateCurrent":true,"checks":[{{"id":"repository-validation","state":"success","commit":"{shaA.Value}"}},{{"id":"packaged-lifecycle","state":"cancelled","commit":"{shaA.Value}"}}]}}"""

                  match MergeReadinessJson.parseEvidence raw with
                  | Error message -> failwith message
                  | Ok evidence ->
                      Assert.equal (Some shaA) evidence.CandidateCommit
                      Assert.equal (Some true) evidence.RemoteCandidateCurrent
                      Assert.equal [ CheckState.Succeeded; CheckState.Cancelled ] (evidence.Checks |> List.map _.State) }

          { Name = "merge readiness: aggregate workflows run a final gate even after prerequisite failure"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))
                  let workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ros-validation.yml"))
                  Assert.isTrue (workflow.Contains "merge-gate:") "repository workflow has no merge-gate job"
                  Assert.isTrue (workflow.Contains "if: ${{ always() }}") "merge-gate can be skipped when a prerequisite fails"
                  Assert.isTrue (workflow.Contains "- validate") "merge-gate does not depend on validation"
                  Assert.isTrue (workflow.Contains "- packaged-lifecycle") "merge-gate does not depend on packaged lifecycle"
                  Assert.isTrue (workflow.Contains "repository-validation") "workflow does not normalize repository-validation"
                  Assert.isTrue (workflow.Contains "packaged-lifecycle") "workflow does not normalize packaged-lifecycle"
                  Assert.isTrue (workflow.Contains "merge readiness --evidence") "workflow does not ask Praxis for the exact-candidate decision"

                  let starter = File.ReadAllText(Path.Combine(root, "starter", "greenfield", ".github", "workflows", "ros-validation.yml"))
                  Assert.isTrue (starter.Contains "merge-gate:") "greenfield starter has no merge gate"
                  Assert.isTrue (starter.Contains "if: ${{ always() }}") "greenfield merge gate can be skipped" }

          { Name = "merge readiness cli: exact green evidence succeeds, dirty or stale candidates do not"
            Run =
              fun () ->
                  withRepository (fun root head ->
                      let evidence =
                          evidenceFile
                              head
                              [ "repository-validation", "succeeded", head
                                "packaged-lifecycle", "succeeded", head ]

                      try
                          let ready = PraxisCli.run root None [ "merge"; "readiness"; "--evidence"; evidence; "--json" ]
                          Assert.equal 0 ready.ExitCode
                          Assert.equal "ready" (PraxisCli.text ready.Json["status"])

                          write root "dirty.txt" "dirty\n"
                          let dirty = PraxisCli.run root None [ "merge"; "readiness"; "--evidence"; evidence; "--json" ]
                          Assert.equal 1 dirty.ExitCode

                          match dirty.Json["blockers"] with
                          | :? System.Text.Json.Nodes.JsonArray as values ->
                              Assert.isTrue
                                  (values |> Seq.exists (fun value -> PraxisCli.text value["code"] = "working-tree-dirty"))
                                  "dirty working tree did not block"
                          | _ -> failwith "expected blockers"
                      finally
                          File.Delete evidence) }

          { Name = "merge readiness cli: command is read-only"
            Run =
              fun () ->
                  withRepository (fun root head ->
                      let evidence =
                          evidenceFile
                              head
                              [ "repository-validation", "succeeded", head
                                "packaged-lifecycle", "succeeded", head ]

                      try
                          let before = fingerprint root
                          Assert.equal 0 (PraxisCli.run root None [ "merge"; "readiness"; "--evidence"; evidence ]).ExitCode
                          Assert.equal before (fingerprint root)
                      finally
                          File.Delete evidence) } ]
