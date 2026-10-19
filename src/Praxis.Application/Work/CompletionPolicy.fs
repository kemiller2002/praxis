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

/// Binds an Ordo constructibility contract (interface I3) to the Dokimos
/// authority so the contract, not a hand-written file, is the source of the
/// contract identity: version and digest come from admission, a conflicting
/// declared identity is a stale reference, and every required ID the
/// authority names must exist in the contract. An authority that names no
/// required requirements inherits all of the contract's (PRX-REL-008).
///
/// Binding then requires execution readiness (REL-GATE-002): the requirements
/// the completion executes (the required ones, less those the contract defers,
/// rejects or supersedes) must be modeled, reach no conflict, and every
/// decision or standalone rationale in their scope must be authorized. The
/// authorized set is the authority's `RequiredDecisionIds`: it comes from the
/// host authority file, never from the agent's packet, so an agent cannot
/// approve its own decisions. Success is a `BoundCompletionAuthority`, which
/// only a ready scope can construct.
[<RequireQualifiedAccess>]
module OrdoBoundAuthority =
    let StaleCode = "PRX.COMPLETION.STALE_ORDO_CONTRACT"
    let UnknownReferenceCode = "PRX.COMPLETION.UNKNOWN_CONTRACT_REFERENCE"
    let NotReadyCode = "PRX.COMPLETION.CONTRACT_NOT_READY"

    let private declared (value: string) =
        if System.String.IsNullOrWhiteSpace value then None else Some value

    let private unknown kind (known: string list) (required: string list) =
        let known = Set.ofList known

        required
        |> List.filter (known.Contains >> not)
        |> List.map (fun id -> { Code = UnknownReferenceCode; Detail = $"required {kind} '{id}' is not in the admitted Ordo contract" })

    let private ordoFindings (diagnostics: OrdoDiagnostic list) =
        diagnostics
        |> List.map (fun diagnostic -> { Code = diagnostic.Code; Detail = defaultArg diagnostic.Subject "" + " " + diagnostic.Message |> _.Trim() })

    /// Refused before anything is bound: no admitted contract means readiness
    /// cannot be established, so a declared identity never reaches Dokimos.
    let missingContract () =
        [ { Code = NotReadyCode
            Detail = "verified completion requires the Ordo contract (--ordo-contract FILE); readiness cannot be established from a declared identity" } ]

    let private references (contract: AdmittedOrdoContract) (input: CompletionAuthorityInput) =
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

        if List.isEmpty findings then Ok requirements else Error findings

    /// Admits `contractJson` (with `expectedDigest`, when pinned), checks the
    /// authority's references against it, and requires the executed
    /// requirements to be ready under the authority's decisions.
    let bind
        (expectedDigest: string option)
        (contractJson: string)
        (input: CompletionAuthorityInput)
        : Result<BoundCompletionAuthority, CompletionFinding list> =
        let supported =
            if List.isEmpty input.SupportedOrdoContractVersions then OrdoContractAdmission.implementedVersions
            else input.SupportedOrdoContractVersions

        OrdoContractAdmission.admitPinned supported expectedDigest contractJson
        |> Result.mapError ordoFindings
        |> Result.bind (fun contract ->
            references contract input
            |> Result.bind (fun requirements ->
                OrdoContractAdmission.assessCompletionReadiness supported expectedDigest contractJson requirements input.RequiredDecisionIds
                |> Result.mapError (fun diagnostics ->
                    { Code = NotReadyCode
                      Detail =
                        $"the Ordo contract {contract.Digest} is not ready for the requirements this completion executes; "
                        + "every decision in scope must be authorized by the host authority's RequiredDecisionIds" }
                    :: ordoFindings diagnostics)
                |> Result.map (fun ready ->
                    BoundCompletionAuthority.fromReadyScope
                        ready
                        { input with
                            SupportedOrdoContractVersions =
                                (if List.isEmpty input.SupportedOrdoContractVersions then [ contract.Version ] else input.SupportedOrdoContractVersions)
                            RequiredRequirementIds = requirements })))
