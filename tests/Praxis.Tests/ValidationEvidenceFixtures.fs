namespace Praxis.Tests

open System
open System.IO
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// PRAXIS-REL-005 test support: completion evidence is a
/// `praxis.validation-result/1` produced by `ValidationRuns.run` and recorded
/// by its `work.validation.observed` event in the same (in-memory) event
/// sink the claim harness uses. The run itself is injected; the result file
/// is written by the production `FileValidationRuns.record`.
[<RequireQualifiedAccess>]
module ValidationEvidenceFixtures =
    let passing = CommandRun.Exited(0, "128 test(s); 128 passed; 0 failed\n")
    let failing = CommandRun.Exited(1, "12 test(s); 7 passed; 5 failed\n")

    /// The `work.validation.observed` events in `events`, as the event-log
    /// port reports them.
    let recorded (events: seq<ClaimEvent>) (workItemId: string) : Result<RecordedValidation list, string> =
        lock events (fun () ->
            events
            |> Seq.choose (fun event ->
                match event.Payload with
                | ClaimEventPayload.ValidationObserved(_, token, generation, _, _, _, _, _, digest) when event.WorkItemId = workItemId ->
                    Some
                        { RecordedValidation.WorkItemId = event.WorkItemId
                          ClaimToken = token
                          Generation = generation
                          ResultDigest = digest }
                | _ -> None)
            |> Seq.toList)
        |> Ok

    let runPorts (claims: WorkClaimPorts) (root: string) (revision: string) (run: CommandRun) : ValidationRunPorts =
        { Claims = claims
          ObserveHead = fun () -> Ok revision
          DirtyPaths = fun () -> Ok []
          RunCommand = fun _ _ -> run
          Record = FileValidationRuns.record root }

    let runRequest (claim: WorkClaim) (validation: string) (evidenceId: string) : ValidationRunRequest =
        { WorkItemId = claim.WorkItemId
          Fence =
            { Claimant = claim.Holder.Value.Claimant
              ClaimToken = claim.Holder.Value.ClaimToken
              Generation = claim.Generation }
          Validation = validation
          EvidenceId = evidenceId
          Command = [ "dotnet"; "test" ]
          Timeout = TimeSpan.FromMinutes 5.0 }

    /// Runs `validation` for the holder of `claim` at `revision` and returns
    /// the absolute path of the recorded result document.
    let validateWith (claims: WorkClaimPorts) (revision: string) (run: CommandRun) (claim: WorkClaim) (validation: string) (evidenceId: string) =
        let root = CliHarness.temporaryDirectory "praxis-validation-result"

        match ValidationRuns.run (runPorts claims root revision run) (runRequest claim validation evidenceId) with
        | ValidationRunOutcome.Observed(_, result) -> Path.Combine(root, result.ResultPath)
        | other -> failwith $"expected an observed validation run, got {other}"

    /// A passing `unit-tests` run for `EV-TESTS`, the common case.
    let validate (claims: WorkClaimPorts) (revision: string) (claim: WorkClaim) =
        validateWith claims revision passing claim "unit-tests" "EV-TESTS"
