namespace Praxis.Infrastructure.Planning

open System
open System.Text.Json
open System.Collections.Generic
open Praxis.Domain.Work

/// Read-only, independently derived authorization decision. A positive result
/// can be submitted to the existing work-group transition gate only after the
/// host has verified the group and members under its normal work-protocol lock.
type EcirAuthorizationPreflight =
    { Validator: EcirValidatorResult
      Receipt: EcirApprovalEvidence
      Observation: EcirExecutionObservation }

[<RequireQualifiedAccess>]
module FileEcirAuthorization =
    let private text (name: string) (entry: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if entry.ValueKind = JsonValueKind.Object && entry.TryGetProperty(name, &property)
           && property.ValueKind = JsonValueKind.String then
            property.GetString() |> Option.ofObj
        else None

    let private strings (name: string) (entry: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if entry.ValueKind = JsonValueKind.Object && entry.TryGetProperty(name, &property)
           && property.ValueKind = JsonValueKind.Array then
            property.EnumerateArray()
            |> Seq.map (fun v ->
                if v.ValueKind = JsonValueKind.String then v.GetString() |> Option.ofObj
                else None)
            |> Seq.toList
            |> List.fold (fun result next ->
                match result, next with
                | Some known, Some value when not (String.IsNullOrWhiteSpace value) ->
                    Some(value :: known)
                | _ -> None) (Some [])
            |> Option.map List.rev
        else None

    /// Only a blueprint already validated by pinned Ordo may be passed here.
    /// This projects its authored facts, without trusting its approval claims.
    let deriveScope
        (docs: CommittedEcirDocuments)
        (verified: EcirValidatorResult)
        (groupId: string)
        (cohortId: string)
        (expectedGroupKeys: string list)
        : Result<EcirApprovalScope, string> =
        try
            use document = JsonDocument.Parse docs.Blueprint
            let root = document.RootElement
            let nodes = root.GetProperty("nodes").EnumerateArray() |> Seq.toList
            let cohorts =
                nodes |> List.filter (fun node ->
                    text "id" node = Some cohortId && text "kind" node = Some "cohort")

            match cohorts with
            | [ cohort ] ->
                match strings "requirementKeys" cohort with
                | None -> Error "ECIR cohort requirement keys are missing or malformed"
                | Some keys when
                    Set.ofList keys <> Set.ofList expectedGroupKeys
                    || keys.Length <> expectedGroupKeys.Length
                    || keys.Length <> (Set.ofList keys).Count
                    || keys.IsEmpty ->
                    Error "ECIR cohort and independently observed group requirement keys differ"
                | Some keys ->
                    let requirements = root.GetProperty("requirements").EnumerateArray() |> Seq.toList
                    let blocked =
                        requirements
                        |> List.choose (fun requirement ->
                            let source = requirement.GetProperty "source"
                            let disposition = requirement.GetProperty "disposition"
                            match text "key" source with
                            | Some key when List.contains key keys
                                && text "kind" disposition <> Some "modeled" ->
                                    Some key
                            | _ -> None)
                    if not blocked.IsEmpty then
                        Error("ECIR execution cohort contains non-modeled requirements: " + String.concat ", " blocked)
                    else
                        let decisions =
                            nodes |> List.choose (fun node ->
                                match text "kind" node, text "id" node, strings "requirementKeys" node with
                                | Some "decision", Some decisionId, Some memberKeys
                                    when (memberKeys |> List.exists (fun key -> List.contains key keys)) ->
                                    Some decisionId
                                | _ -> None)
                        if decisions.IsEmpty || decisions.Length <> (decisions |> Set.ofList).Count then
                            Error "ECIR cohort has no unique architectural decision set"
                        else
                            Ok
                                { GroupId = groupId
                                  CohortId = cohortId
                                  SourceCommit = docs.Commit
                                  ManifestDigest = docs.SourceManifestDigest
                                  BlueprintDigest = verified.BlueprintDigest
                                  RequirementKeys = keys
                                  DecisionIds = decisions }
            | _ -> Error "ECIR cohort must exist exactly once in the Ordo-validated blueprint"
        with
        | :? JsonException as e -> Error("malformed committed ECIR blueprint: " + e.Message)
        | :? InvalidOperationException as e -> Error("invalid ECIR construction graph: " + e.Message)
        | :? KeyNotFoundException as e -> Error("ECIR construction node missing: " + e.Message)

    /// An end-to-end preflight, NOT a work-begin operation. No work state or
    /// approval is created. In particular, trustedSignerKeys, commit,
    /// source/blueprint digests, pinned Ordo release and group keys MUST be
    /// injected from protected host policy and real repository observations,
    /// not derived from an AI-authored work item.
    let verifyReadOnly
        (repositoryRoot: string)
        (source: CommittedEcirReference)
        (expectedBlueprintDigest: string)
        (pinnedOrdo: PinnedOrdoRelease)
        (trustedSignerKeys: Map<string, EcirTrustedSigner>)
        (groupId: string)
        (cohortId: string)
        (groupRequirementKeys: string list)
        (receiptJson: string)
        (nowUtc: DateTimeOffset)
        : Result<EcirAuthorizationPreflight, string> =

        match FileEcirPreflight.readCommitted repositoryRoot source with
        | Error problem -> Error problem
        | Ok docs ->
            match FileEcirValidator.validate pinnedOrdo docs expectedBlueprintDigest with
            | Error problem -> Error problem
            | Ok validator ->
                match deriveScope docs validator groupId cohortId groupRequirementKeys with
                | Error problem -> Error problem
                | Ok scope ->
                    match EcirApprovals.read receiptJson with
                    | Error problem -> Error problem
                    | Ok signed ->
                        match EcirApprovals.verify trustedSignerKeys scope nowUtc signed with
                        | Error problem -> Error problem
                        | Ok receipt ->
                            let observation =
                                { GroupId = groupId
                                  CohortId = cohortId
                                  ManifestDigest = docs.SourceManifestDigest
                                  BlueprintSourceDigest = docs.SourceManifestDigest
                                  ExpectedBlueprintDigest = expectedBlueprintDigest
                                  ValidatedBlueprintDigest = Some validator.BlueprintDigest
                                  OrdoValidation = Ok()
                                  DecisionAuthorization = Ok()
                                  ApprovedBlueprintDigest = Some receipt.BlueprintDigest
                                  ApprovedCohortId = Some receipt.CohortId
                                  CohortKeys = scope.RequirementKeys
                                  GroupRequirementKeys = groupRequirementKeys
                                  BlockedRequirements = []
                                  ValidatorPinned = true
                                  ArtifactCommitVerified = true }

                            match EcirGates.problems observation with
                            | [] ->
                                Ok { Validator = validator
                                     Receipt = receipt
                                     Observation = observation }
                            | reasons ->
                                Error("ECIR decision authorization failed: " + String.concat "; " reasons)
