namespace Ros.Domain.Ordo

open System

[<RequireQualifiedAccess>]
type CoverageStatus =
    | Complete
    | Partial
    | Unknown

[<RequireQualifiedAccess>]
module CoverageStatus =
    let toWire = function
        | CoverageStatus.Complete -> "complete"
        | CoverageStatus.Partial -> "partial"
        | CoverageStatus.Unknown -> "unknown"

    let tryOfWire = function
        | "complete" -> Some CoverageStatus.Complete
        | "partial" -> Some CoverageStatus.Partial
        | "unknown" -> Some CoverageStatus.Unknown
        | _ -> None

type StateViewSchema =
    { Id: string
      Version: int }

type CoverageClaim =
    { Scope: string
      Status: CoverageStatus
      ProvenanceEvidenceIds: string list }

type ProviderObservation =
    { Id: string
      Model: string option
      ModelVersion: string option
      AdapterVersion: string }

type ConfidenceObservation =
    { Magnitude: float
      Provenance: string }

type ProviderUsage =
    { InputTokens: int option
      OutputTokens: int option
      CachedInputTokens: int option
      ProviderReportedCost: string option }

/// Provider-neutral facts emitted by executable Ordo. ROS observes these facts;
/// it does not reinterpret them into application authority or legal transitions.
type ResolutionObservation =
    { ResolutionId: string
      CorrelationId: string option
      CausedBy: string option
      Mode: string
      ContractId: string
      ContractVersion: int
      RequestId: string
      StateFingerprint: string
      StateViewSchema: StateViewSchema
      Coverage: CoverageClaim list
      Provider: ProviderObservation option
      StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset
      Outcome: string
      SelectedChoice: string option
      Confidence: ConfidenceObservation option
      EvidenceUsed: string list
      Escalation: string list
      Transition: string option
      PolicyId: string option
      PolicyVersion: int option
      PolicyExperimental: bool option
      Usage: ProviderUsage
      TransportRetries: int
      ExperimentReference: string option }

[<RequireQualifiedAccess>]
type SemanticAssessment =
    | Confirmed
    | Incorrect
    | Unresolved
    | NotAssessable

[<RequireQualifiedAccess>]
module SemanticAssessment =
    let toWire = function
        | SemanticAssessment.Confirmed -> "confirmed"
        | SemanticAssessment.Incorrect -> "incorrect"
        | SemanticAssessment.Unresolved -> "unresolved"
        | SemanticAssessment.NotAssessable -> "not-assessable"

    let tryOfWire = function
        | "confirmed" -> Some SemanticAssessment.Confirmed
        | "incorrect" -> Some SemanticAssessment.Incorrect
        | "unresolved" -> Some SemanticAssessment.Unresolved
        | "not-assessable" -> Some SemanticAssessment.NotAssessable
        | _ -> None

[<RequireQualifiedAccess>]
type OperationalAssessment =
    | Succeeded
    | Neutral
    | RefusedOrDeadEnd
    | HarmfulOrFailed
    | OutcomeUnknown
    | NotAssessable

[<RequireQualifiedAccess>]
module OperationalAssessment =
    let toWire = function
        | OperationalAssessment.Succeeded -> "succeeded"
        | OperationalAssessment.Neutral -> "neutral"
        | OperationalAssessment.RefusedOrDeadEnd -> "refused-or-dead-end"
        | OperationalAssessment.HarmfulOrFailed -> "harmful-or-failed"
        | OperationalAssessment.OutcomeUnknown -> "outcome-unknown"
        | OperationalAssessment.NotAssessable -> "not-assessable"

    let tryOfWire = function
        | "succeeded" -> Some OperationalAssessment.Succeeded
        | "neutral" -> Some OperationalAssessment.Neutral
        | "refused-or-dead-end" -> Some OperationalAssessment.RefusedOrDeadEnd
        | "harmful-or-failed" -> Some OperationalAssessment.HarmfulOrFailed
        | "outcome-unknown" -> Some OperationalAssessment.OutcomeUnknown
        | "not-assessable" -> Some OperationalAssessment.NotAssessable
        | _ -> None

type ResolutionAssessment =
    { AssessmentId: string
      ResolutionId: string
      Semantic: SemanticAssessment
      Operational: OperationalAssessment
      AssessedAt: DateTimeOffset
      EvidenceReferences: string list
      Method: string
      Limitations: string list }

[<RequireQualifiedAccess>]
type SearchOutcome =
    | Found
    | SearchedNotFound

[<RequireQualifiedAccess>]
module SearchOutcome =
    let toWire = function
        | SearchOutcome.Found -> "found"
        | SearchOutcome.SearchedNotFound -> "searched-not-found"

    let tryOfWire = function
        | "found" -> Some SearchOutcome.Found
        | "searched-not-found" -> Some SearchOutcome.SearchedNotFound
        | _ -> None

/// A negative observation is deliberately scoped. SearchedNotFound never means
/// global absence; consumers must inspect Coverage before drawing a domain conclusion.
type SearchObservation =
    { ObservationId: string
      Target: string
      Scope: string
      Method: string
      Query: string option
      StateReference: string
      Coverage: CoverageStatus
      CoverageEvidenceIds: string list
      Exclusions: string list
      Errors: string list
      Outcome: SearchOutcome
      ObservedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type EffectOutcome =
    | Succeeded
    | Failed
    | Unknown

[<RequireQualifiedAccess>]
module EffectOutcome =
    let toWire = function
        | EffectOutcome.Succeeded -> "succeeded"
        | EffectOutcome.Failed -> "failed"
        | EffectOutcome.Unknown -> "unknown"

    let tryOfWire = function
        | "succeeded" -> Some EffectOutcome.Succeeded
        | "failed" -> Some EffectOutcome.Failed
        | "unknown" -> Some EffectOutcome.Unknown
        | _ -> None

type EffectObservation =
    { ObservationId: string
      ResolutionId: string option
      EffectId: string
      Outcome: EffectOutcome
      AttemptedAt: DateTimeOffset
      ObservedAt: DateTimeOffset
      ReconciliationRequested: bool
      ReconciliationResult: string option
      RetryBlocked: bool
      CompensationBlocked: bool }

type EffectiveCurrentProjection =
    { Resolution: ResolutionObservation option
      Assessment: ResolutionAssessment option
      BasisCount: int
      SupersededResolutionIds: string list }

type AuthorityReference =
    { RepositoryRevision: string
      Source: string
      StateFingerprint: string option }

/// Structured handoff authority. It is a projection over canonical repository
/// facts, not a new source of application truth.
type HandoffAuthority =
    { Authority: AuthorityReference
      ResolutionId: string option
      Facts: string list
      Assumptions: string list
      Unknowns: string list
      Obligations: string list
      LegalNextActions: string list
      SupersededResolutionIds: string list }

[<RequireQualifiedAccess>]
module Projection =
    let effectiveCurrent (observations: ResolutionObservation list) (assessments: ResolutionAssessment list) =
        let ordered =
            observations
            |> List.sortBy (fun observation -> observation.CompletedAt, observation.ResolutionId)

        let current = ordered |> List.tryLast

        let assessment =
            current
            |> Option.bind (fun observation ->
                assessments
                |> List.filter (fun candidate -> candidate.ResolutionId = observation.ResolutionId)
                |> List.sortBy (fun candidate -> candidate.AssessedAt, candidate.AssessmentId)
                |> List.tryLast)

        let superseded =
            match current with
            | None -> []
            | Some latest ->
                ordered
                |> List.filter (fun observation -> observation.ResolutionId <> latest.ResolutionId)
                |> List.map (fun observation -> observation.ResolutionId)

        { Resolution = current
          Assessment = assessment
          BasisCount = observations.Length
          SupersededResolutionIds = superseded }

    let handoff revision source facts assumptions unknowns obligations legalNextActions current =
        { Authority =
            { RepositoryRevision = revision
              Source = source
              StateFingerprint = current.Resolution |> Option.map (fun observation -> observation.StateFingerprint) }
          ResolutionId = current.Resolution |> Option.map (fun observation -> observation.ResolutionId)
          Facts = facts
          Assumptions = assumptions
          Unknowns = unknowns
          Obligations = obligations
          LegalNextActions = legalNextActions
          SupersededResolutionIds = current.SupersededResolutionIds }
