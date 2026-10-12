namespace Praxis.Infrastructure.Construction

open System
open System.Reflection
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
              Construction: OrdoConstruction }

    let binding (admitted: Admitted) = admitted.Binding
    let construction (admitted: Admitted) = admitted.Construction

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
                      RequirementKeys = keys; DecisionIds = receipt.Scope.DecisionIds }
                EcirApprovals.verify policy.TrustedSigners expected now receipt |> Result.map (fun evidence -> grant, evidence)))

    /// Ordo's own semantics over the exact committed documents: manifest and
    /// blueprint identity, cohort readiness under the signed decisions, then
    /// the plan bound to that prepared cohort.
    let private prepare (manifestText: string) (blueprintText: string) cohortId (decisions: string list) (planText: string) =
        ConstructionIrWire.readManifest manifestText |> Result.mapError ((+) "source manifest refused: ")
        |> Result.bind (fun manifest ->
            ConstructionIrWire.verifyManifestDigest manifest |> Result.mapError ((+) "source manifest refused: ")
            |> Result.bind (fun () -> ConstructionIrWire.readBlueprint blueprintText |> Result.mapError ((+) "blueprint refused: "))
            |> Result.bind (fun blueprint ->
                ConstructionIr.prepareCohort manifest blueprint cohortId (Set.ofList decisions)
                |> Result.mapError (fun violations -> "cohort is not ready for construction: " + describe violations)))
        |> Result.bind (fun prepared ->
            ConstructionPlanWire.readPlan planText |> Result.mapError ((+) "construction plan refused: ")
            |> Result.bind (fun draft -> ConstructionPlan.validate prepared draft |> Result.mapError (fun violations -> "construction plan refused: " + describe violations))
            |> Result.bind (fun validated ->
                if not (ConstructionPlan.isBoundTo prepared validated) then Error "construction plan is not bound to the approved cohort"
                else Ok validated))

    let admit (configuration: LocalHostConfiguration) (policy: EcirHostPolicy) (now: DateTimeOffset) (groupId: string) (planText: string) : Result<Admitted, string> =
        let git, root = configuration.Git, configuration.RepositoryRoot
        approve root policy now groupId |> Result.bind (fun (grant, approval) ->
            let source = grant.Source
            ConstructionTree.readCommitted git root source.Commit source.ManifestPath |> Result.bind (fun (manifest, manifestDigest) ->
                ConstructionTree.readCommitted git root source.Commit source.BlueprintPath |> Result.bind (fun (blueprint, blueprintDigest) ->
                    if manifestDigest <> source.SourceManifestDigest || blueprintDigest <> grant.BlueprintDigest then
                        Error "committed ECIR documents differ from the approved digests"
                    else
                        prepare manifest blueprint grant.CohortId approval.DecisionIds planText |> Result.bind (fun validated ->
                            Ordo.Construction.Construction.construct validated
                            |> Result.mapError (fun refusals -> "construction refused: " + describe refusals)
                            |> Result.map (fun built ->
                                { Construction = built
                                  Binding =
                                    { PolicyRevision = policy.Revision; GroupId = grant.GroupId; CohortId = grant.CohortId
                                      SourceCommit = source.Commit; ManifestPath = source.ManifestPath; ManifestDigest = manifestDigest
                                      BlueprintPath = source.BlueprintPath; BlueprintDigest = blueprintDigest
                                      RequirementKeys = approval.RequirementKeys; DecisionIds = approval.DecisionIds
                                      Approver = approval.Approver; ApprovalExpiresAt = approval.ExpiresAt
                                      PlanDigest = ConstructionPlan.digest validated; GeneratorVersion = built.GeneratorVersion
                                      OrdoCoreVersion = ordoCoreVersion (); OrdoConstructionVersion = ordoConstructionVersion () } })))))
