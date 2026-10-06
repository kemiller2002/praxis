namespace Praxis.Domain.Work

/// Completion readiness (PRX-QUAL-023): Praxis *consumes* quality evidence
/// produced by other tools -- the Dokimos change-quality ratchet
/// (`dokimos.ratchet` 1.0.0) and the Ordo boundary-amplification assessment
/// (`ordo.boundary-amplification/1`) -- and decides whether a work item may
/// complete. It never re-measures code quality, never recomputes a Dokimos
/// verdict and never re-derives an Ordo recommendation: each tool's own
/// judgement is trusted, and only its contract consistency is checked.
///
/// Unavailable evidence is never a pass. Every value here is immutable and
/// every function is pure; reading files and policy happens at the edges.

/// How strongly a repository's policy asks for one evidence source.
/// `Required`: absent or unavailable evidence blocks completion.
/// `Optional`: absent or unavailable evidence is recorded but does not block;
/// supplied evidence that *fails* still blocks (failing evidence is never
/// silently accepted). `Off`: the source is not judged at all.
[<RequireQualifiedAccess>]
type EvidenceRequirement =
    | Required
    | Optional
    | Off

/// The four independent facets of "done". A work item can be implemented
/// without being behaviourally verified, verified without being
/// architecturally sound, and all three without being releasable.
[<RequireQualifiedAccess>]
type CompletionFacet =
    | ImplementationComplete
    | BehaviorVerified
    | ArchitectureVerified
    | ReleaseReady
    /// A member of a grouped-mode group execution carries a committed group
    /// analysis and a per-criterion verification (PRX-GRP-133..135).
    | GroupVerified

/// One facet's state. `Unavailable` is distinct from `NotSatisfied`: the
/// evidence could not establish the facet either way. It is never a pass.
[<RequireQualifiedAccess>]
type FacetStatus =
    | Satisfied of evidence: string list
    | NotSatisfied of reasons: string list
    | Unavailable of reasons: string list
    | NotRequired

/// Dokimos's own ratchet verdict (`Verdict` in `dokimos.ratchet` 1.0.0).
[<RequireQualifiedAccess>]
type DokimosVerdict =
    | Pass
    | Regression
    | InvalidExceptions
    | Unavailable

/// The parts of a `dokimos.ratchet` 1.0.0 report Praxis consumes.
type DokimosRatchetEvidence =
    { DokimosVersion: string
      CheckedAt: string
      Repository: string option
      Verdict: DokimosVerdict
      ExitCode: int
      Reasons: string list
      BaselinePath: string
      BaselineDigest: string option
      Regressions: int
      Excepted: int
      Improvements: int
      RulesMeasured: int
      RulesUnavailable: int
      ActiveExceptions: string list
      ExpiredExceptions: string list
      InvalidExceptions: string list }

[<RequireQualifiedAccess>]
type OrdoRiskLevel =
    | Low
    | Elevated
    | High

[<RequireQualifiedAccess>]
type OrdoRecommendation =
    | NoAction
    | ConsiderSplitAlong
    | RequireDesignReview

[<RequireQualifiedAccess>]
type OrdoExceptionCoverage =
    | NotApplicable
    | NoCoverage
    | Partial
    | Full

/// The parts of an `ordo.boundary-amplification/1` assessment Praxis consumes.
type OrdoBoundaryEvidence =
    { WorkItem: string
      Risk: OrdoRiskLevel
      Score: int
      RiskEvidence: string list
      Recommendation: OrdoRecommendation
      SplitAlong: string list
      Coverage: OrdoExceptionCoverage
      DowngradedFrom: OrdoRecommendation option
      UnexpectedCrossings: string list
      Unclassified: string list
      GeneratorVersion: string option }

/// What decoding one supplied evidence document produced.
[<RequireQualifiedAccess>]
type EvidenceReading<'T> =
    | Parsed of 'T
    /// Well-formed, but names a contract or version Praxis does not consume.
    | Unsupported of reason: string
    /// Unreadable, not JSON, or violating the contract it names.
    | Malformed of reason: string

/// What one completion request supplied for one evidence source.
[<RequireQualifiedAccess>]
type SourceObservation<'T> =
    | NotSupplied
    | Supplied of path: string * reading: EvidenceReading<'T>
    | Ambiguous of paths: string list

/// One source's contribution to a facet, before the policy weighs it.
[<RequireQualifiedAccess>]
type SourceJudgement =
    | Ignored
    | Passed of evidence: string
    | Failed of reasons: string list
    | Unavailable of reasons: string list

/// A repository's quality-evidence policy (`workProtocol.qualityEvidence`).
type QualityEvidencePolicy =
    { Dokimos: EvidenceRequirement
      /// Pins the Dokimos profile: a report measured against any other
      /// accepted baseline is not evidence for this repository's policy.
      DokimosBaseline: string option
      OrdoBoundary: EvidenceRequirement
      RequiredFacets: Set<CompletionFacet>
      /// Work types the policy applies to; `None` means every type.
      WorkTypes: Set<string> option }

type FacetAssessment =
    { Facet: CompletionFacet
      Required: bool
      Status: FacetStatus
      Blocking: bool }

/// The machine-readable readiness of one work item at completion.
type ItemReadiness =
    { WorkItemId: string
      WorkType: string
      Policy: QualityEvidencePolicy
      Dokimos: SourceObservation<DokimosRatchetEvidence>
      DokimosJudgement: SourceJudgement
      Ordo: SourceObservation<OrdoBoundaryEvidence>
      OrdoJudgement: SourceJudgement
      Facets: FacetAssessment list }

[<RequireQualifiedAccess>]
module QualityEvidenceTypes =
    /// `work complete --evidence dokimos-ratchet=PATH`: a `dokimos.ratchet` report.
    let dokimosRatchet = "dokimos-ratchet"
    /// `work complete --evidence ordo-boundary=PATH`: an `ordo.boundary-amplification/1` assessment.
    let ordoBoundary = "ordo-boundary"
    /// `work complete --evidence group-analysis=PATH`: a `praxis.group-analysis/1` document.
    let groupAnalysis = "group-analysis"
    /// `work complete --evidence group-verification=PATH`: a `praxis.group-verification/1` document.
    let groupVerification = "group-verification"
    let implementation = "implementation"
    let tests = "tests"

[<RequireQualifiedAccess>]
module CompletionReadiness =
    let facetCode =
        function
        | CompletionFacet.ImplementationComplete -> "implementation-complete"
        | CompletionFacet.BehaviorVerified -> "behavior-verified"
        | CompletionFacet.ArchitectureVerified -> "architecture-verified"
        | CompletionFacet.ReleaseReady -> "release-ready"
        | CompletionFacet.GroupVerified -> "group-verified"

    let parseFacet =
        function
        | "implementation-complete" -> Some CompletionFacet.ImplementationComplete
        | "behavior-verified" -> Some CompletionFacet.BehaviorVerified
        | "architecture-verified" -> Some CompletionFacet.ArchitectureVerified
        | "release-ready" -> Some CompletionFacet.ReleaseReady
        | _ -> None

    let allFacets =
        [ CompletionFacet.ImplementationComplete
          CompletionFacet.BehaviorVerified
          CompletionFacet.ArchitectureVerified
          CompletionFacet.ReleaseReady
          CompletionFacet.GroupVerified ]

    let requirementCode =
        function
        | EvidenceRequirement.Required -> "required"
        | EvidenceRequirement.Optional -> "optional"
        | EvidenceRequirement.Off -> "off"

    let parseRequirement =
        function
        | "required" -> Some EvidenceRequirement.Required
        | "optional" -> Some EvidenceRequirement.Optional
        | "off" -> Some EvidenceRequirement.Off
        | _ -> None

    let verdictCode =
        function
        | DokimosVerdict.Pass -> "pass"
        | DokimosVerdict.Regression -> "regression"
        | DokimosVerdict.InvalidExceptions -> "invalid-exceptions"
        | DokimosVerdict.Unavailable -> "unavailable"

    /// The exit code Dokimos documents for each verdict.
    let verdictExitCode =
        function
        | DokimosVerdict.Pass -> 0
        | DokimosVerdict.Unavailable -> 3
        | DokimosVerdict.Regression -> 4
        | DokimosVerdict.InvalidExceptions -> 6

    let riskCode =
        function
        | OrdoRiskLevel.Low -> "low"
        | OrdoRiskLevel.Elevated -> "elevated"
        | OrdoRiskLevel.High -> "high"

    let recommendationCode =
        function
        | OrdoRecommendation.NoAction -> "no-action"
        | OrdoRecommendation.ConsiderSplitAlong -> "consider-split-along"
        | OrdoRecommendation.RequireDesignReview -> "require-design-review"

    let coverageCode =
        function
        | OrdoExceptionCoverage.NotApplicable -> "not-applicable"
        | OrdoExceptionCoverage.NoCoverage -> "none"
        | OrdoExceptionCoverage.Partial -> "partial"
        | OrdoExceptionCoverage.Full -> "full"

    let private normalizePath (path: string) =
        let forward = path.Replace('\\', '/')
        if forward.StartsWith "./" then forward.Substring 2 else forward

    /// Shared handling of a source that produced no judgeable document.
    let private observationGap (evidenceType: string) (observation: SourceObservation<'T>) (judgeParsed: 'T -> SourceJudgement) =
        match observation with
        | SourceObservation.NotSupplied -> SourceJudgement.Unavailable [ $"no {evidenceType} evidence was supplied" ]
        | SourceObservation.Ambiguous paths ->
            let joined = String.concat ", " paths
            SourceJudgement.Unavailable [ $"more than one {evidenceType} evidence entry was supplied ({joined}); supply exactly one" ]
        | SourceObservation.Supplied(path, EvidenceReading.Unsupported reason) ->
            SourceJudgement.Unavailable [ $"{evidenceType} evidence '{path}' is unsupported: {reason}" ]
        | SourceObservation.Supplied(path, EvidenceReading.Malformed reason) ->
            SourceJudgement.Unavailable [ $"{evidenceType} evidence '{path}' is malformed: {reason}" ]
        | SourceObservation.Supplied(_, EvidenceReading.Parsed value) -> judgeParsed value

    /// Judges a Dokimos ratchet report by Dokimos's own verdict. A report
    /// whose verdict and exit code disagree, or that claims `pass` while
    /// counting unexcepted regressions, is inconsistent and therefore
    /// unavailable -- never a pass. Dokimos's verdict is not recomputed.
    let judgeDokimos (requirement: EvidenceRequirement) (baseline: string option) (observation: SourceObservation<DokimosRatchetEvidence>) =
        match requirement with
        | EvidenceRequirement.Off -> SourceJudgement.Ignored
        | _ ->
            observationGap QualityEvidenceTypes.dokimosRatchet observation (fun report ->
                let verdict = verdictCode report.Verdict

                if report.ExitCode <> verdictExitCode report.Verdict then
                    SourceJudgement.Unavailable [ $"inconsistent dokimos.ratchet report: verdict '{verdict}' with exit code {report.ExitCode}" ]
                elif report.Verdict = DokimosVerdict.Pass && report.Regressions > 0 then
                    SourceJudgement.Unavailable [ $"inconsistent dokimos.ratchet report: verdict 'pass' with {report.Regressions} unexcepted regression(s)" ]
                else
                    match baseline with
                    | Some required when normalizePath required <> normalizePath report.BaselinePath ->
                        SourceJudgement.Unavailable
                            [ $"dokimos.ratchet report was measured against baseline '{report.BaselinePath}'; the policy requires '{required}'" ]
                    | _ ->
                        match report.Verdict with
                        | DokimosVerdict.Pass ->
                            let digest = report.BaselineDigest |> Option.defaultValue "no digest"

                            SourceJudgement.Passed
                                $"dokimos ratchet pass against {report.BaselinePath} ({digest}): {report.Excepted} excepted, {report.Improvements} improvement(s), {report.RulesMeasured} rule(s) measured; dokimos {report.DokimosVersion}"
                        | DokimosVerdict.Regression ->
                            SourceJudgement.Failed($"dokimos ratchet verdict 'regression' ({report.Regressions} unexcepted regression(s))" :: report.Reasons)
                        | DokimosVerdict.InvalidExceptions ->
                            SourceJudgement.Failed("dokimos ratchet verdict 'invalid-exceptions'" :: report.Reasons)
                        | DokimosVerdict.Unavailable ->
                            SourceJudgement.Unavailable("dokimos ratchet verdict 'unavailable' (not verified)" :: report.Reasons))

    /// Judges an Ordo boundary assessment by Ordo's own recommendation. Only
    /// `require-design-review` without full exception coverage fails; Ordo
    /// already downgrades a fully approved recommendation, and Praxis does not
    /// re-derive it. An assessment of a different work item is not evidence
    /// for this one.
    let judgeOrdo (requirement: EvidenceRequirement) (workItemId: string) (observation: SourceObservation<OrdoBoundaryEvidence>) =
        match requirement with
        | EvidenceRequirement.Off -> SourceJudgement.Ignored
        | _ ->
            observationGap QualityEvidenceTypes.ordoBoundary observation (fun assessment ->
                let summary =
                    $"ordo boundary risk {riskCode assessment.Risk} (score {assessment.Score}), recommendation {recommendationCode assessment.Recommendation}, exception coverage {coverageCode assessment.Coverage}"

                if assessment.WorkItem <> workItemId then
                    SourceJudgement.Unavailable [ $"ordo boundary assessment is for work item '{assessment.WorkItem}', not '{workItemId}'" ]
                elif assessment.Recommendation = OrdoRecommendation.RequireDesignReview && assessment.Coverage <> OrdoExceptionCoverage.Full then
                    let crossings =
                        match assessment.UnexpectedCrossings with
                        | [] -> "none"
                        | values -> String.concat ", " values

                    SourceJudgement.Failed
                        [ $"{summary}: a recorded design review or approved scope expansion is required"
                          $"unexpected boundary crossings: {crossings}" ]
                else
                    SourceJudgement.Passed summary)

    /// Combines the source judgements into the architecture facet. A failure
    /// from any judged source is `NotSatisfied`, whatever its requirement;
    /// an unavailable *required* source is `Unavailable`; otherwise at least
    /// one passing source is needed for `Satisfied`.
    let architectureFacet (required: bool) (sources: (EvidenceRequirement * SourceJudgement) list) : FacetStatus =
        let failures =
            sources |> List.collect (fun (_, judgement) -> match judgement with SourceJudgement.Failed reasons -> reasons | _ -> [])

        let unavailable requirementFilter =
            sources
            |> List.collect (fun (requirement, judgement) ->
                match judgement with
                | SourceJudgement.Unavailable reasons when requirementFilter requirement -> reasons
                | _ -> [])

        let requiredUnavailable = unavailable ((=) EvidenceRequirement.Required)
        let optionalUnavailable = unavailable ((<>) EvidenceRequirement.Required)

        let passes =
            sources |> List.choose (fun (_, judgement) -> match judgement with SourceJudgement.Passed evidence -> Some evidence | _ -> None)

        if not failures.IsEmpty then FacetStatus.NotSatisfied failures
        elif not requiredUnavailable.IsEmpty then FacetStatus.Unavailable requiredUnavailable
        elif not passes.IsEmpty then FacetStatus.Satisfied passes
        elif required then
            FacetStatus.Unavailable("architecture-verified is required but no architecture evidence source produced a result" :: optionalUnavailable)
        elif not optionalUnavailable.IsEmpty then FacetStatus.Unavailable optionalUnavailable
        else FacetStatus.NotRequired

    /// A facet established by the presence of a named completion-evidence
    /// type (`implementation`, `tests`). This records an attestation that the
    /// evidence was supplied and exists; it is not a proof of behaviour.
    let presenceFacet (required: bool) (evidenceType: string) (provided: WorkEvidence list) : FacetStatus =
        match provided |> List.filter (fun evidence -> evidence.Type = evidenceType) with
        | [] when required -> FacetStatus.NotSatisfied [ $"no '{evidenceType}' evidence was supplied" ]
        | [] -> FacetStatus.NotRequired
        | entries -> FacetStatus.Satisfied(entries |> List.map (fun evidence -> $"{evidence.Type}={evidence.Path}"))

    /// No release-readiness evidence contract is consumed yet, so a required
    /// release-ready facet is unavailable -- never assumed.
    let releaseFacet (required: bool) : FacetStatus =
        if required then
            FacetStatus.Unavailable [ "no release-readiness evidence contract is consumed yet; release-ready cannot be established" ]
        else
            FacetStatus.NotRequired

    /// Not-satisfied evidence always blocks; unavailable evidence blocks a
    /// required facet. Satisfied and not-required never block.
    let isBlocking (required: bool) (status: FacetStatus) =
        match status with
        | FacetStatus.NotSatisfied _ -> true
        | FacetStatus.Unavailable _ -> required
        | FacetStatus.Satisfied _
        | FacetStatus.NotRequired -> false

    let isReady (readiness: ItemReadiness) =
        readiness.Facets |> List.forall (fun facet -> not facet.Blocking)

    /// Why an item is refused: one line per blocking facet.
    let blockingReasons (readiness: ItemReadiness) =
        readiness.Facets
        |> List.filter _.Blocking
        |> List.map (fun facet ->
            let detail =
                match facet.Status with
                | FacetStatus.NotSatisfied reasons -> "not satisfied: " + String.concat "; " reasons
                | FacetStatus.Unavailable reasons -> "unavailable: " + String.concat "; " reasons
                | FacetStatus.Satisfied _
                | FacetStatus.NotRequired -> "blocking"

            $"{facetCode facet.Facet} {detail}")
