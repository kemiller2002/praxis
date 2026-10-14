namespace Praxis.Infrastructure.Construction

open System
open System.Collections.Generic
open System.Reflection
open System.Text.Json.Nodes
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work
open Ordo.Core
type private OrdoConstruction = Ordo.Construction.Construction.Construction

/// What a construction request is bound to, all independently re-derived by
/// the host: the protected policy, the signed approval, the exact committed
/// source bytes and the pinned Ordo engine. Nothing here is agent-supplied
/// except the plan, whose digest is recorded.
type ConstructionBinding =
    { PolicyRevision: string
      GroupId: string
      CohortId: string
      SourceCommit: string
      ManifestPath: string
      ManifestDigest: string
      BlueprintPath: string
      BlueprintDigest: string
      RequirementKeys: string list
      DecisionIds: string list
      Approver: string
      ApprovalExpiresAt: string
      PlanDigest: string
      /// The canonical digests the admitted plan is bound to (Ordo's own
      /// manifest digest and recomputed blueprint digest), as opposed to the
      /// committed files' byte digests above. Equal to the stamped binding
      /// when the plan omitted one.
      PlanSourceManifestDigest: string
      PlanBlueprintDigest: string
      /// The admitted plan in Ordo's canonical form; its SHA-256 is PlanDigest.
      /// Independent verifiers read this instead of reconstructing the plan.
      PlanCanonicalJson: string
      GeneratorVersion: string
      OrdoCoreVersion: string
      OrdoConstructionVersion: string }

/// Host admission of an Ordo construction plan (Praxis #242). The only agent
/// input is the plan document; authority, decisions, source documents and the
/// engine come from the protected host. An admitted construction can only be
/// produced by `admit`, so later stages cannot be handed an unchecked one.
[<RequireQualifiedAccess>]
module ConstructionAdmission =
    type Admitted =
        private
            { Binding: ConstructionBinding
              Construction: OrdoConstruction
              PlanText: string }

    let binding (admitted: Admitted) = admitted.Binding
    let construction (admitted: Admitted) = admitted.Construction
    /// The exact plan document Ordo admitted (with any host-stamped binding).
    /// The host records it so a later scope check can re-admit it (GH-234).
    let planText (admitted: Admitted) = admitted.PlanText

    let private version (assembly: Assembly) =
        match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> string (assembly.GetName().Version)
        | attribute -> attribute.InformationalVersion.Split('+').[0]

    let ordoCoreVersion () = version typeof<ConstructionPlan.ValidatedPlan>.Assembly
    let ordoConstructionVersion () = version typeof<OrdoConstruction>.Assembly

    let private describe (values: 'a list) = values |> List.map (sprintf "%A") |> String.concat "; "

    let private members root groupId =
        FileWorkGroupRepository.read root |> Result.bind (fun groups ->
            match groups |> List.filter (fun group -> group.Declaration.Id = groupId) with
            | [ group ] -> Ok group.Declaration.Members
            | _ -> Error("construction group is missing or ambiguous: " + groupId))

    /// Signed approval for the exact current grant, re-verified now.
    let private approve root (policy: EcirHostPolicy) now groupId =
        members root groupId
        |> Result.bind (fun current -> EcirHostPolicy.resolve root groupId current policy)
        |> Result.bind (fun (grant, keys) ->
            EcirApprovals.read grant.ReceiptJson |> Result.bind (fun receipt ->
                let expected : EcirApprovalScope =
                    { GroupId = grant.GroupId; CohortId = grant.CohortId; SourceCommit = grant.Source.Commit
                      ManifestDigest = grant.Source.SourceManifestDigest; BlueprintDigest = grant.BlueprintDigest
                      RequirementKeys = keys; DecisionIds = receipt.Scope.DecisionIds
                      ConstructionDecisionsDigest = receipt.Scope.ConstructionDecisionsDigest }
                EcirApprovals.verify policy.TrustedSigners expected now receipt |> Result.map (fun evidence -> grant, evidence)))

    /// The binding a plan must carry, derived from the prepared cohort and never
    /// from agent text. In the timer pilot, every construction agent first
    /// copied a file hash where Ordo expects the canonical blueprint digest
    /// (research-documents#7, F1). A plan that omits `binding` gets this one; a
    /// plan that supplies one is still checked by Ordo exactly as before.
    let private cohortBinding (prepared: ConstructionIr.PreparedCohort) : JsonNode =
        let cohort = ConstructionIr.preparedCohortView prepared
        let text (value: string) = JsonValue.Create value :> JsonNode
        let decisions =
            Set.union cohort.RequiredDecisionIds cohort.RequiredRationaleIds
            |> Seq.map text |> Seq.toArray |> JsonArray :> JsonNode
        JsonObject(
            [ KeyValuePair("sourceManifestDigest", text cohort.Manifest.Digest)
              KeyValuePair("blueprintDigest", text (ConstructionIrWire.blueprintDigest cohort.Blueprint))
              KeyValuePair("cohortId", text cohort.CohortId)
              KeyValuePair("decisionIds", decisions) ]) :> JsonNode

    /// Fills an omitted binding; any other plan text passes through untouched
    /// so Ordo's strict reader reports its own refusals.
    let private stampBinding prepared (planText: string) =
        let parsed = try Some(JsonNode.Parse planText) with _ -> None
        match parsed with
        | Some(:? JsonObject as plan) when not (plan.ContainsKey "binding") ->
            plan["binding"] <- cohortBinding prepared
            plan.ToJsonString()
        | _ -> planText

    /// Ordo's own cohort semantics over the exact committed documents:
    /// manifest and blueprint identity, then cohort readiness under the
    /// signed decisions.
    let private prepareCohort (manifestText: string) (blueprintText: string) cohortId (decisions: string list) =
        ConstructionIrWire.readManifest manifestText |> Result.mapError ((+) "source manifest refused: ")
        |> Result.bind (fun manifest ->
            ConstructionIrWire.verifyManifestDigest manifest |> Result.mapError ((+) "source manifest refused: ")
            |> Result.bind (fun () -> ConstructionIrWire.readBlueprint blueprintText |> Result.mapError ((+) "blueprint refused: "))
            |> Result.bind (fun blueprint ->
                ConstructionIr.prepareCohort manifest blueprint cohortId (Set.ofList decisions)
                |> Result.mapError (fun violations -> "cohort is not ready for construction: " + describe violations)))

    /// The plan bound to that prepared cohort.
    let private preparePlan prepared (planText: string) =
        let stamped = stampBinding prepared planText
        ConstructionPlanWire.readPlan stamped |> Result.mapError ((+) "construction plan refused: ")
        |> Result.bind (fun draft -> ConstructionPlan.validate prepared draft |> Result.mapError (fun violations -> "construction plan refused: " + describe violations))
        |> Result.bind (fun validated ->
            if not (ConstructionPlan.isBoundTo prepared validated) then Error "construction plan is not bound to the approved cohort"
            else Ok(validated, stamped))

    /// A cohort admitted under the current signed grant, before any plan:
    /// the input mechanical-first construction (CONSTRUCT-008) generates from.
    /// Only `admitCohort` produces one.
    type AdmittedCohort =
        private
            { CohortGrant: EcirHostGrant
              CohortApproval: EcirApprovalEvidence
              CohortPolicyRevision: string
              CohortManifestDigest: string
              CohortBlueprintDigest: string
              CohortManifest: string
              CohortPrepared: ConstructionIr.PreparedCohort }

    let cohortGrant (cohort: AdmittedCohort) = cohort.CohortGrant
    let cohortApproval (cohort: AdmittedCohort) = cohort.CohortApproval
    let cohortPolicyRevision (cohort: AdmittedCohort) = cohort.CohortPolicyRevision
    let cohortManifestDigest (cohort: AdmittedCohort) = cohort.CohortManifestDigest
    let cohortBlueprintDigest (cohort: AdmittedCohort) = cohort.CohortBlueprintDigest
    /// The exact committed source manifest text (requirement locations and
    /// their excerpt digests).
    let cohortManifest (cohort: AdmittedCohort) = cohort.CohortManifest
    let cohortPrepared (cohort: AdmittedCohort) = cohort.CohortPrepared
    /// Ordo's canonical blueprint digest (what decision records bind to).
    let cohortCanonicalBlueprintDigest (cohort: AdmittedCohort) =
        ConstructionIrWire.blueprintDigest (ConstructionIr.preparedCohortView cohort.CohortPrepared).Blueprint

    let admitCohort (configuration: LocalHostConfiguration) (policy: EcirHostPolicy) (now: DateTimeOffset) (groupId: string) : Result<AdmittedCohort, string> =
        let git, root = configuration.Git, configuration.RepositoryRoot
        approve root policy now groupId |> Result.bind (fun (grant, approval) ->
            let source = grant.Source
            ConstructionTree.readCommitted git root source.Commit source.ManifestPath |> Result.bind (fun (manifest, manifestDigest) ->
                ConstructionTree.readCommitted git root source.Commit source.BlueprintPath |> Result.bind (fun (blueprint, blueprintDigest) ->
                    if manifestDigest <> source.SourceManifestDigest || blueprintDigest <> grant.BlueprintDigest then
                        Error "committed ECIR documents differ from the approved digests"
                    else
                        prepareCohort manifest blueprint grant.CohortId approval.DecisionIds |> Result.map (fun prepared ->
                            { CohortGrant = grant; CohortApproval = approval; CohortPolicyRevision = policy.Revision
                              CohortManifestDigest = manifestDigest; CohortBlueprintDigest = blueprintDigest
                              CohortManifest = manifest; CohortPrepared = prepared }))))

    let admit (configuration: LocalHostConfiguration) (policy: EcirHostPolicy) (now: DateTimeOffset) (groupId: string) (planText: string) : Result<Admitted, string> =
        admitCohort configuration policy now groupId |> Result.bind (fun cohort ->
            let grant, approval, source = cohort.CohortGrant, cohort.CohortApproval, cohort.CohortGrant.Source
            preparePlan cohort.CohortPrepared planText |> Result.bind (fun (validated, stamped) ->
                Ordo.Construction.Construction.construct validated
                |> Result.mapError (fun refusals -> "construction refused: " + describe refusals)
                |> Result.map (fun built ->
                    { Construction = built
                      PlanText = stamped
                      Binding =
                        { PolicyRevision = policy.Revision; GroupId = grant.GroupId; CohortId = grant.CohortId
                          SourceCommit = source.Commit; ManifestPath = source.ManifestPath; ManifestDigest = cohort.CohortManifestDigest
                          BlueprintPath = source.BlueprintPath; BlueprintDigest = cohort.CohortBlueprintDigest
                          RequirementKeys = approval.RequirementKeys; DecisionIds = approval.DecisionIds
                          Approver = approval.Approver; ApprovalExpiresAt = approval.ExpiresAt
                          PlanDigest = ConstructionPlan.digest validated
                          PlanSourceManifestDigest = (ConstructionPlan.view validated).Binding.SourceManifestDigest
                          PlanBlueprintDigest = (ConstructionPlan.view validated).Binding.BlueprintDigest
                          PlanCanonicalJson = ConstructionPlan.canonicalJson validated
                          GeneratorVersion = built.GeneratorVersion
                          OrdoCoreVersion = ordoCoreVersion (); OrdoConstructionVersion = ordoConstructionVersion () } })))
