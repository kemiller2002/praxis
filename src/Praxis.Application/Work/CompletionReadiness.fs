namespace Praxis.Application.Work

open Praxis.Domain.Work

/// Ports through which completion readiness observes supplied evidence. The
/// infrastructure adapter reads a repository-relative path and decodes it;
/// Praxis never runs Dokimos or Ordo itself (it consumes their reports).
type QualityEvidenceSources =
    { ReadDokimos: string -> EvidenceReading<DokimosRatchetEvidence>
      ReadOrdo: string -> EvidenceReading<OrdoBoundaryEvidence> }

/// The outcome of the completion-readiness gate for one `work complete`.
[<RequireQualifiedAccess>]
type CompletionGateOutcome =
    /// The policy does not apply to any completing item: legacy behaviour,
    /// nothing is evaluated and nothing is recorded.
    | NotApplicable
    | Ready of ItemReadiness list
    | Refused of ItemReadiness list
    /// `workProtocol.qualityEvidence` is present but invalid. Fails closed.
    | PolicyInvalid of reason: string

[<RequireQualifiedAccess>]
module QualityEvidencePolicies =
    /// The migration bridge (PRX-QUAL-023): a repository that has not opted
    /// in keeps exactly its existing completion behaviour. Retirement
    /// condition: once Conditor installs Dokimos by default, the default
    /// becomes `dokimos: required` for newly initialized repositories
    /// (tracked debt in requirements/CODE-QUALITY-HARDENING.md).
    let legacyDefault: QualityEvidencePolicy =
        { Dokimos = EvidenceRequirement.Off
          DokimosBaseline = None
          OrdoBoundary = EvidenceRequirement.Off
          RequiredFacets = Set.empty
          WorkTypes = None }

    let isActive (policy: QualityEvidencePolicy) =
        policy.Dokimos <> EvidenceRequirement.Off
        || policy.OrdoBoundary <> EvidenceRequirement.Off
        || not policy.RequiredFacets.IsEmpty

    let appliesTo (policy: QualityEvidencePolicy) (workType: string) =
        isActive policy && (policy.WorkTypes |> Option.forall (Set.contains workType))

    /// The architecture facet is required when the repository requires it
    /// explicitly or requires one of its evidence sources.
    let facetRequired (policy: QualityEvidencePolicy) (facet: CompletionFacet) =
        policy.RequiredFacets.Contains facet
        || (facet = CompletionFacet.ArchitectureVerified
            && (policy.Dokimos = EvidenceRequirement.Required || policy.OrdoBoundary = EvidenceRequirement.Required))

    /// Evidence types a completing item must supply under this policy.
    let requiredEvidenceTypes (policy: QualityEvidencePolicy) =
        [ if policy.Dokimos = EvidenceRequirement.Required then QualityEvidenceTypes.dokimosRatchet
          if policy.OrdoBoundary = EvidenceRequirement.Required then QualityEvidenceTypes.ordoBoundary ]

[<RequireQualifiedAccess>]
module CompletionReadinessOperations =
    /// Observe: read every supplied entry of one evidence type, once.
    let observe (read: string -> EvidenceReading<'T>) (evidenceType: string) (provided: WorkEvidence list) : SourceObservation<'T> =
        match provided |> List.filter (fun evidence -> evidence.Type = evidenceType) with
        | [] -> SourceObservation.NotSupplied
        | [ single ] -> SourceObservation.Supplied(single.Path, read single.Path)
        | many -> SourceObservation.Ambiguous(many |> List.map _.Path)

    /// Decide (pure): one item's readiness under a policy and observed evidence.
    let assess
        (policy: QualityEvidencePolicy)
        (workItemId: string, workType: string)
        (provided: WorkEvidence list)
        (dokimos: SourceObservation<DokimosRatchetEvidence>)
        (ordo: SourceObservation<OrdoBoundaryEvidence>)
        : ItemReadiness =
        let dokimosJudgement = CompletionReadiness.judgeDokimos policy.Dokimos policy.DokimosBaseline dokimos
        let ordoJudgement = CompletionReadiness.judgeOrdo policy.OrdoBoundary workItemId ordo

        let facet kind =
            let required = QualityEvidencePolicies.facetRequired policy kind

            let status =
                match kind with
                | CompletionFacet.ImplementationComplete -> CompletionReadiness.presenceFacet required QualityEvidenceTypes.implementation provided
                | CompletionFacet.BehaviorVerified -> CompletionReadiness.presenceFacet required QualityEvidenceTypes.tests provided
                | CompletionFacet.ArchitectureVerified ->
                    CompletionReadiness.architectureFacet required [ policy.Dokimos, dokimosJudgement; policy.OrdoBoundary, ordoJudgement ]
                | CompletionFacet.ReleaseReady -> CompletionReadiness.releaseFacet required

            { Facet = kind
              Required = required
              Status = status
              Blocking = CompletionReadiness.isBlocking required status }

        { WorkItemId = workItemId
          WorkType = workType
          Policy = policy
          Dokimos = dokimos
          DokimosJudgement = dokimosJudgement
          Ordo = ordo
          OrdoJudgement = ordoJudgement
          Facets = CompletionReadiness.allFacets |> List.map facet }

    /// Decide (pure): the gate outcome for every completing item.
    let decide (readiness: ItemReadiness list) =
        match readiness with
        | [] -> CompletionGateOutcome.NotApplicable
        | items when items |> List.forall CompletionReadiness.isReady -> CompletionGateOutcome.Ready items
        | items -> CompletionGateOutcome.Refused items

    /// observe -> decide. `policy` is the already-read repository policy;
    /// items outside the policy's work types are not evaluated. Evidence
    /// files are read only when some item is in scope.
    let gate
        (sources: QualityEvidenceSources)
        (policy: Result<QualityEvidencePolicy, string>)
        (items: (string * string) list)
        (provided: WorkEvidence list)
        : CompletionGateOutcome =
        match policy with
        | Error reason -> CompletionGateOutcome.PolicyInvalid reason
        | Ok policy ->
            match items |> List.filter (fun (_, workType) -> QualityEvidencePolicies.appliesTo policy workType) with
            | [] -> CompletionGateOutcome.NotApplicable
            | inScope ->
                let dokimos =
                    if policy.Dokimos = EvidenceRequirement.Off then SourceObservation.NotSupplied
                    else observe sources.ReadDokimos QualityEvidenceTypes.dokimosRatchet provided

                let ordo =
                    if policy.OrdoBoundary = EvidenceRequirement.Off then SourceObservation.NotSupplied
                    else observe sources.ReadOrdo QualityEvidenceTypes.ordoBoundary provided

                inScope |> List.map (fun item -> assess policy item provided dokimos ordo) |> decide
