namespace Praxis.Application.Work

open Praxis.Domain.Ordo
open Praxis.Domain.Work

/// Whether the legacy `work complete` transition may close an item. An item
/// that has ever been claimed is governed by the claim protocol: its queue
/// transition is legal only after `work complete-verified` recorded a
/// Dokimos-accepted completion. `workProtocol.requireVerifiedCompletion`
/// extends the rule to unclaimed items (RQ-ROS-2026-A032, PRX-REL-007).
[<RequireQualifiedAccess>]
type PlainCompletion =
    | Permitted
    | Refused of CompletionFinding

[<RequireQualifiedAccess>]
module PlainCompletion =
    let RequiredCode = "PRX.COMPLETION.VERIFIED_COMPLETION_REQUIRED"

    let decide (requireVerified: bool) (workItemId: string) (claim: WorkClaim option) =
        match claim with
        | Some claim when claim.Status = ClaimStatus.Completed -> PlainCompletion.Permitted
        | Some claim ->
            PlainCompletion.Refused
                { Code = RequiredCode
                  Detail =
                    $"work item '{workItemId}' is under claim-based ownership (status {ClaimStatus.code claim.Status}, generation {claim.Generation}); "
                    + "complete it with 'praxis work complete-verified' before 'work complete'" }
        | None when requireVerified ->
            PlainCompletion.Refused
                { Code = RequiredCode
                  Detail =
                    $"workProtocol.requireVerifiedCompletion requires work item '{workItemId}' to be claimed and completed with 'praxis work complete-verified'" }
        | None -> PlainCompletion.Permitted

    /// The first refusal across every item, or `Permitted`.
    let decideAll (requireVerified: bool) (claims: (string * WorkClaim option) list) =
        claims
        |> List.map (fun (id, claim) -> decide requireVerified id claim)
        |> List.tryPick (function
            | PlainCompletion.Refused finding -> Some(PlainCompletion.Refused finding)
            | PlainCompletion.Permitted -> None)
        |> Option.defaultValue PlainCompletion.Permitted

/// Binds an admitted Ordo constructibility contract (interface I3) to the
/// Dokimos authority so the contract, not a hand-written file, is the source
/// of the contract identity: version and digest come from admission, a
/// conflicting declared identity is a stale reference, and every required ID
/// the authority names must exist in the contract. An authority that names no
/// required requirements inherits all of the contract's (PRX-REL-008).
[<RequireQualifiedAccess>]
module OrdoBoundAuthority =
    let StaleCode = "PRX.COMPLETION.STALE_ORDO_CONTRACT"
    let UnknownReferenceCode = "PRX.COMPLETION.UNKNOWN_CONTRACT_REFERENCE"

    let private declared (value: string) =
        if System.String.IsNullOrWhiteSpace value then None else Some value

    let private unknown kind (known: string list) (required: string list) =
        let known = Set.ofList known

        required
        |> List.filter (known.Contains >> not)
        |> List.map (fun id -> { Code = UnknownReferenceCode; Detail = $"required {kind} '{id}' is not in the admitted Ordo contract" })

    let bind (contract: AdmittedOrdoContract) (input: CompletionAuthorityInput) : Result<CompletionAuthorityInput, CompletionFinding list> =
        let stale =
            [ match declared input.OrdoContractDigest with
              | Some digest when digest.ToLowerInvariant() <> contract.Digest ->
                  yield { Code = StaleCode; Detail = $"authority pins contract digest {digest} but the admitted contract is {contract.Digest}" }
              | _ -> ()
              match declared input.OrdoContractVersion with
              | Some version when version <> contract.Version ->
                  yield { Code = StaleCode; Detail = $"authority pins contract version {version} but the admitted contract is {contract.Version}" }
              | _ -> () ]

        let requirements =
            if List.isEmpty input.RequiredRequirementIds then contract.RequirementIds else input.RequiredRequirementIds

        let findings =
            stale
            @ unknown "requirement" contract.RequirementIds requirements
            @ unknown "decision" contract.DecisionIds input.RequiredDecisionIds
            @ unknown "conflict resolution" contract.ConflictResolutionIds input.RequiredConflictResolutionIds

        if List.isEmpty findings then
            Ok
                { input with
                    OrdoContractVersion = contract.Version
                    OrdoContractDigest = contract.Digest
                    SupportedOrdoContractVersions =
                        (if List.isEmpty input.SupportedOrdoContractVersions then [ contract.Version ] else input.SupportedOrdoContractVersions)
                    RequiredRequirementIds = requirements }
        else
            Error findings
