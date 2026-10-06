namespace Praxis.Application.Work

open Praxis.Domain.Work

/// Ports through which completion readiness observes supplied evidence. The
/// infrastructure adapter reads a repository-relative path and decodes it;
/// Praxis never runs Dokimos or Ordo itself (it consumes their reports).
type QualityEvidenceSources =
    { ReadDokimos: string -> EvidenceReading<DokimosRatchetEvidence>
      ReadOrdo: string -> EvidenceReading<OrdoBoundaryEvidence>
      ReadDesignDebt: string -> EvidenceReading<DesignDebtDeclaration>
      ReadVerificationMatrix: string -> EvidenceReading<VerificationMatrix>
      ReadReleaseReadiness: string -> EvidenceReading<ReleaseReadinessEvidence>
      /// SHA-256 of the evidence file's bytes, `None` when it cannot be read.
      Digest: string -> string option
      /// Whether a `path[:line]` location reference exists in the repository.
      LocationExists: string -> bool }

/// Facts about the completing items the gate needs beyond the evidence:
/// each item's declared risk metadata and the recorded open work items
/// (debt can be tracked only by those).
type CompletionFacts =
    { Risk: string -> WorkRisk option
      OpenItems: Set<string> }

[<RequireQualifiedAccess>]
module CompletionFacts =
    let none =
        { Risk = fun _ -> None
          OpenItems = Set.empty }

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
    /// explicitly or requires one of its evidence sources; the design-debt
    /// and verification-matrix facets are also required by an item's risk
    /// obligations (PRX-QUAL-020).
    let facetRequired (policy: QualityEvidencePolicy) (obligations: CompletionObligations) (facet: CompletionFacet) =
        policy.RequiredFacets.Contains facet
        || (facet = CompletionFacet.ArchitectureVerified
            && (policy.Dokimos = EvidenceRequirement.Required || policy.OrdoBoundary = EvidenceRequirement.Required))
        || (facet = CompletionFacet.DesignDebtDeclared && obligations.DesignDebtDeclaration)
        || (facet = CompletionFacet.VerificationMatrixSatisfied && not obligations.VerificationDimensions.IsEmpty)

    /// Evidence types a completing item must supply under this policy and
    /// its risk obligations.
    let requiredEvidenceTypes (policy: QualityEvidencePolicy) (obligations: CompletionObligations) =
        [ if policy.Dokimos = EvidenceRequirement.Required then QualityEvidenceTypes.dokimosRatchet
          if policy.OrdoBoundary = EvidenceRequirement.Required then QualityEvidenceTypes.ordoBoundary
          if facetRequired policy obligations CompletionFacet.DesignDebtDeclared then QualityEvidenceTypes.designDebt
          if facetRequired policy obligations CompletionFacet.VerificationMatrixSatisfied then QualityEvidenceTypes.verificationMatrix
          if facetRequired policy obligations CompletionFacet.ReleaseReady then QualityEvidenceTypes.releaseReadiness ]

    /// Obligation evidence types; supplying one brings an item under the gate.
    let obligationEvidenceTypes =
        [ QualityEvidenceTypes.designDebt; QualityEvidenceTypes.verificationMatrix; QualityEvidenceTypes.releaseReadiness ]

[<RequireQualifiedAccess>]
module CompletionReadinessOperations =
    /// Observe: read every supplied entry of one evidence type, once.
    let observe (read: string -> EvidenceReading<'T>) (evidenceType: string) (provided: WorkEvidence list) : SourceObservation<'T> =
        match provided |> List.filter (fun evidence -> evidence.Type = evidenceType) with
        | [] -> SourceObservation.NotSupplied
        | [ single ] -> SourceObservation.Supplied(single.Path, read single.Path)
        | many -> SourceObservation.Ambiguous(many |> List.map _.Path)

    /// Decide (pure): one item's readiness under a policy, its risk
    /// obligations and the observed evidence.
    let assess
        (policy: QualityEvidencePolicy)
        (facts: CompletionFacts)
        (locationExists: string -> bool)
        (workItemId: string, workType: string)
        (provided: WorkEvidence list)
        (dokimos: SourceObservation<DokimosRatchetEvidence>)
        (ordo: SourceObservation<OrdoBoundaryEvidence>)
        (observations: ObligationObservations)
        (consumed: ConsumedEvidence list)
        : ItemReadiness =
        let risk = facts.Risk workItemId
        let obligations = WorkRisk.obligations risk
        let required = QualityEvidencePolicies.facetRequired policy obligations
        let dokimosJudgement = CompletionReadiness.judgeDokimos policy.Dokimos policy.DokimosBaseline dokimos
        let ordoJudgement = CompletionReadiness.judgeOrdo policy.OrdoBoundary workItemId ordo

        // An obligation source is judged when its facet is required or the
        // evidence was supplied: supplied evidence that fails always blocks.
        let judgeWhen facet (observation: SourceObservation<'T>) judge =
            match observation with
            | SourceObservation.NotSupplied when not (required facet) -> SourceJudgement.Ignored
            | _ -> judge observation

        let debtJudgement =
            judgeWhen CompletionFacet.DesignDebtDeclared observations.DesignDebt (CompletionReadiness.judgeDesignDebt workItemId facts.OpenItems)

        let matrixJudgement =
            judgeWhen
                CompletionFacet.VerificationMatrixSatisfied
                observations.VerificationMatrix
                (CompletionReadiness.judgeVerificationMatrix workItemId obligations.VerificationDimensions locationExists)

        let releaseJudgement = judgeWhen CompletionFacet.ReleaseReady observations.ReleaseReadiness CompletionReadiness.judgeRelease

        let facet kind =
            let isRequired = required kind

            let status =
                match kind with
                | CompletionFacet.ImplementationComplete -> CompletionReadiness.presenceFacet isRequired QualityEvidenceTypes.implementation provided
                | CompletionFacet.BehaviorVerified -> CompletionReadiness.presenceFacet isRequired QualityEvidenceTypes.tests provided
                | CompletionFacet.ArchitectureVerified ->
                    CompletionReadiness.architectureFacet isRequired [ policy.Dokimos, dokimosJudgement; policy.OrdoBoundary, ordoJudgement ]
                | CompletionFacet.ReleaseReady -> CompletionReadiness.judgedFacet isRequired "release-ready" releaseJudgement
                | CompletionFacet.DesignDebtDeclared -> CompletionReadiness.judgedFacet isRequired "design-debt-declared" debtJudgement
                | CompletionFacet.VerificationMatrixSatisfied ->
                    CompletionReadiness.judgedFacet isRequired "verification-matrix-satisfied" matrixJudgement

            { Facet = kind
              Required = isRequired
              Status = status
              Blocking = CompletionReadiness.isBlocking isRequired status }

        { WorkItemId = workItemId
          WorkType = workType
          Policy = policy
          Dokimos = dokimos
          DokimosJudgement = dokimosJudgement
          Ordo = ordo
          OrdoJudgement = ordoJudgement
          Risk = risk
          Obligations = obligations
          Observations = observations
          DesignDebtJudgement = debtJudgement
          VerificationMatrixJudgement = matrixJudgement
          ReleaseJudgement = releaseJudgement
          ConsumedEvidence = consumed
          Facets = CompletionReadiness.allFacets |> List.map facet }

    /// Decide (pure): the gate outcome for every completing item.
    let decide (readiness: ItemReadiness list) =
        match readiness with
        | [] -> CompletionGateOutcome.NotApplicable
        | items when items |> List.forall CompletionReadiness.isReady -> CompletionGateOutcome.Ready items
        | items -> CompletionGateOutcome.Refused items

    /// observe -> decide. `policy` is the already-read repository policy.
    /// An item is evaluated when the policy applies to its work type, when
    /// its risk metadata carries obligations, or when obligation evidence was
    /// supplied; any other item keeps legacy behaviour. Evidence files are
    /// read only when some item is in scope, and each file read is recorded
    /// with its digest.
    let gate
        (sources: QualityEvidenceSources)
        (facts: CompletionFacts)
        (policy: Result<QualityEvidencePolicy, string>)
        (items: (string * string) list)
        (provided: WorkEvidence list)
        : CompletionGateOutcome =
        match policy with
        | Error reason -> CompletionGateOutcome.PolicyInvalid reason
        | Ok policy ->
            let obligationSupplied =
                provided |> List.exists (fun evidence -> List.contains evidence.Type QualityEvidencePolicies.obligationEvidenceTypes)

            let inScope (id, workType) =
                QualityEvidencePolicies.appliesTo policy workType
                || WorkRisk.hasObligations (WorkRisk.obligations (facts.Risk id))
                || obligationSupplied

            match items |> List.filter inScope with
            | [] -> CompletionGateOutcome.NotApplicable
            | inScope ->
                let consumedTypes =
                    [ if policy.Dokimos <> EvidenceRequirement.Off then QualityEvidenceTypes.dokimosRatchet
                      if policy.OrdoBoundary <> EvidenceRequirement.Off then QualityEvidenceTypes.ordoBoundary
                      yield! QualityEvidencePolicies.obligationEvidenceTypes ]

                let dokimos =
                    if policy.Dokimos = EvidenceRequirement.Off then SourceObservation.NotSupplied
                    else observe sources.ReadDokimos QualityEvidenceTypes.dokimosRatchet provided

                let ordo =
                    if policy.OrdoBoundary = EvidenceRequirement.Off then SourceObservation.NotSupplied
                    else observe sources.ReadOrdo QualityEvidenceTypes.ordoBoundary provided

                let observations =
                    { DesignDebt = observe sources.ReadDesignDebt QualityEvidenceTypes.designDebt provided
                      VerificationMatrix = observe sources.ReadVerificationMatrix QualityEvidenceTypes.verificationMatrix provided
                      ReleaseReadiness = observe sources.ReadReleaseReadiness QualityEvidenceTypes.releaseReadiness provided }

                let consumed =
                    provided
                    |> List.filter (fun evidence -> List.contains evidence.Type consumedTypes)
                    |> List.map (fun evidence ->
                        { Type = evidence.Type
                          Path = evidence.Path
                          Sha256 = sources.Digest evidence.Path })

                inScope
                |> List.map (fun item -> assess policy facts sources.LocationExists item provided dokimos ordo observations consumed)
                |> decide
