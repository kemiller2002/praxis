namespace Ros.Integrations.GitHub.Tests

open System
open System.IO
open EchelonFoundry.Ros.Integration
open Ros.Integrations.GitHub

/// Proves the migration spec's exact Phase 9 shadow-mode acceptance
/// criteria ("one observation -> one candidate; repeat Chrona load ->
/// still one candidate; repeat ROS delivery -> still one candidate")
/// mechanically, using the `ChronaPlaceholder` stand-in described in
/// that module's doc comment -- not Chrona's real, not-yet-published
/// package. `LocalGitSimulation` makes this a real git repository with
/// real commits, not a simulated count.
[<RequireQualifiedAccess>]
module ChronaShadowAdapterTests =
    let private activity (activityId: string) (description: string) : ActivityObservation =
        let input: ActivityObservationInput =
            { ContractVersion = "1"
              ActivityId = activityId
              OrganizationId = "ORG-ECHELON"
              ProjectId = "PROJ-ROS"
              RepositoryId = None
              WorkItemId = None
              ActorId = Some "octocat"
              StartedAt = Some(DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero))
              EndedAt = Some(DateTimeOffset(2026, 5, 1, 9, 30, 0, TimeSpan.Zero))
              Description = Some description
              Evidence = [] }

        match ActivityObservation.create input with
        | Ok value -> value
        | Error errors -> failwith $"fixture activity expected to be valid, got {errors}"

    let private withTempRepo (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-chrona-shadow-{Guid.NewGuid():N}")
        LocalGitSimulation.init root

        try
            run root
        finally
            Directory.Delete(root, true)

    let private deliver (repoRoot: string) (candidate: TimeObservationCandidate) =
        GitHubDatastoreWriter.write repoRoot candidate |> ignore
        LocalGitSimulation.commitIfChanged repoRoot $"chrona candidate {candidate.CandidateId}"

    let tests =
        [ { Name = "one observation produces exactly one candidate file"
            Run =
              fun () ->
                  withTempRepo (fun root ->
                      let candidate = activity "ACT-SHADOW-0001" "reviewed a PR" |> TimeObservationCandidate.fromActivity
                      deliver root candidate |> ignore

                      Assert.equal [ "ACT-SHADOW-0001" ] (GitHubDatastoreWriter.listCandidates root)) }

          { Name = "repeat Chrona load still returns the same single candidate"
            Run =
              fun () ->
                  withTempRepo (fun root ->
                      let candidate = activity "ACT-SHADOW-0002" "paired on a bug" |> TimeObservationCandidate.fromActivity
                      deliver root candidate |> ignore

                      let firstLoad = GitHubDatastoreWriter.tryRead root "ACT-SHADOW-0002"
                      let secondLoad = GitHubDatastoreWriter.tryRead root "ACT-SHADOW-0002"

                      Assert.equal (Some candidate) firstLoad
                      Assert.equal (Some candidate) secondLoad
                      Assert.equal [ "ACT-SHADOW-0002" ] (GitHubDatastoreWriter.listCandidates root)) }

          { Name = "repeat ROS delivery of the identical candidate still yields one candidate and one commit"
            Run =
              fun () ->
                  withTempRepo (fun root ->
                      let candidate = activity "ACT-SHADOW-0003" "reviewed a design doc" |> TimeObservationCandidate.fromActivity

                      let firstCommitted = deliver root candidate
                      let secondCommitted = deliver root candidate

                      Assert.isTrue firstCommitted "expected the first delivery to create a commit"
                      Assert.isTrue (not secondCommitted) "expected the identical redelivery to create no new commit"
                      Assert.equal [ "ACT-SHADOW-0003" ] (GitHubDatastoreWriter.listCandidates root)
                      Assert.equal 1 (LocalGitSimulation.commitCount root)) }

          { Name = "a genuinely different candidate afterward produces a second file and a second commit"
            Run =
              fun () ->
                  withTempRepo (fun root ->
                      let first = activity "ACT-SHADOW-0004" "first" |> TimeObservationCandidate.fromActivity
                      let second = activity "ACT-SHADOW-0005" "second" |> TimeObservationCandidate.fromActivity

                      deliver root first |> ignore
                      deliver root second |> ignore

                      Assert.equal (Set.ofList [ "ACT-SHADOW-0004"; "ACT-SHADOW-0005" ]) (GitHubDatastoreWriter.listCandidates root |> Set.ofList)
                      Assert.equal 2 (LocalGitSimulation.commitCount root)) }

          { Name = "a real update to the same activity's description produces a new commit, not a silent overwrite"
            Run =
              fun () ->
                  withTempRepo (fun root ->
                      let original = activity "ACT-SHADOW-0006" "first description" |> TimeObservationCandidate.fromActivity
                      let corrected = activity "ACT-SHADOW-0006" "corrected description" |> TimeObservationCandidate.fromActivity

                      deliver root original |> ignore
                      let secondCommitted = deliver root corrected

                      Assert.isTrue secondCommitted "expected a genuinely changed candidate to create a new commit"
                      Assert.equal [ "ACT-SHADOW-0006" ] (GitHubDatastoreWriter.listCandidates root)
                      Assert.equal 2 (LocalGitSimulation.commitCount root)
                      Assert.equal (Some corrected) (GitHubDatastoreWriter.tryRead root "ACT-SHADOW-0006")) } ]
