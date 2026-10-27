namespace Praxis.Application.Work

open System.Text
open Praxis.Contracts.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Domain.Verification

/// The two effects plan admission adds to the input-document lifecycle.
type VerificationPlanPort =
    { /// The claim directory's copy of the input (`.praxis/processing/...`).
      ReadClaimedCopy: InputClaim -> Result<byte array, string>
      /// Writes a repository-relative file atomically.
      WriteFile: string -> byte array -> Result<unit, string> }

type PlanAdmission =
    { Handoff: VerificationPlanHandoff
      HandoffPath: string }

/// `praxis inbox admit-plan`: admission of a claimed verification plan,
/// built on the established input-document lifecycle rather than beside it.
/// The plan arrives with `inbox claim`; admission verifies the claimed bytes
/// (input digest, plan identity, ids, scope counts), places them unchanged in
/// the canonical directory, writes the `praxis.verification-plan-handoff/1`
/// record Ordo is pointed at, and records both as derivations of the claim,
/// so `inbox complete` still refuses until they are committed.
[<RequireQualifiedAccess>]
module VerificationPlanIntakeOperations =
    let defaultDirectory = "verification-plans"

    let admit
        (inbox: InputInboxPort)
        (plans: VerificationPlanPort)
        (claimId: string)
        (directory: string)
        (actor: Actor)
        (execution: string option)
        : Result<PlanAdmission, InputInboxError> =
        let directory = directory.Replace('\\', '/').TrimEnd('/')

        if directory = "" || directory.StartsWith "/" || directory.Split('/') |> Array.contains ".." then
            Error(InputInboxError.InvalidRequest $"'{directory}' is not a repository-relative directory")
        else
            InputInboxOperations.show inbox claimId
            |> Result.bind (fun (location, claim) ->
                if location <> ClaimLocation.Processing || claim.State <> InputClaimState.Claimed then
                    Error(InputInboxError.WrongState(claim.ClaimId, claim.State, "admit-plan"))
                else
                    plans.ReadClaimedCopy claim |> Result.mapError InputInboxError.Storage |> Result.map (fun bytes -> claim, bytes))
            |> Result.bind (fun (claim, bytes) ->
                match VerificationPlanIntake.problems claim.Source bytes with
                | [], Some identity ->
                    let planPath = $"{directory}/{identity.PlanId}.verification-plan.json"
                    let handoffPath = $"{directory}/{identity.PlanId}.handoff.json"

                    VerificationPlanIntake.admit claim bytes planPath (inbox.Now()) actor execution
                    |> Result.map (fun handoff -> claim, bytes, handoff, handoffPath)
                | found, _ -> Error(InputInboxError.InvalidRequest("verification plan refused: " + String.concat "; " found)))
            |> Result.bind (fun (claim, bytes, handoff, handoffPath) ->
                plans.WriteFile handoff.AdmittedPath bytes
                |> Result.bind (fun () -> plans.WriteFile handoffPath (Encoding.UTF8.GetBytes(VerificationPlanHandoffJson.render handoff)))
                |> Result.mapError InputInboxError.Storage
                |> Result.bind (fun () ->
                    InputInboxOperations.derive
                        inbox
                        claim.ClaimId
                        DerivationKind.Reference
                        (DerivationTarget.RepositoryPath handoff.AdmittedPath)
                        $"admitted verification plan {handoff.Plan.PlanId} (sha256 {handoff.Plan.Digest}), bytes unchanged"
                        (Some "planIdentity")
                        actor
                        execution)
                |> Result.bind (fun _ ->
                    InputInboxOperations.derive
                        inbox
                        claim.ClaimId
                        DerivationKind.Reference
                        (DerivationTarget.RepositoryPath handoffPath)
                        $"handoff of {handoff.Plan.PlanId} to Ordo: {VerificationPlanIntake.orderingCommand handoff}"
                        None
                        actor
                        execution)
                |> Result.map (fun _ -> { Handoff = handoff; HandoffPath = handoffPath }))
