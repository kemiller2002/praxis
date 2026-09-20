namespace Ros.Application.Ordo

open Ros.Domain.Ordo

[<RequireQualifiedAccess>]
type StoreOutcome =
    | Stored
    | AlreadyPresent
    | Conflict of string

type ObservationRepository =
    { TryResolution: string -> ResolutionObservation option
      SaveResolution: ResolutionObservation -> string -> unit
      ListResolutions: unit -> ResolutionObservation list
      TryAssessment: string -> ResolutionAssessment option
      SaveAssessment: ResolutionAssessment -> unit
      ListAssessments: unit -> ResolutionAssessment list
      TrySearchObservation: string -> SearchObservation option
      SaveSearchObservation: SearchObservation -> unit
      TryEffectObservation: string -> EffectObservation option
      SaveEffectObservation: EffectObservation -> unit }

[<RequireQualifiedAccess>]
module ObservationOperations =
    let ingestResolution (repository: ObservationRepository) (raw: string) (observation: ResolutionObservation) =
        match repository.TryResolution observation.ResolutionId with
        | None ->
            repository.SaveResolution observation raw
            StoreOutcome.Stored
        | Some existing when existing = observation -> StoreOutcome.AlreadyPresent
        | Some _ ->
            StoreOutcome.Conflict $"ResolutionId '{observation.ResolutionId}' already exists with different observed facts."

    let recordAssessment (repository: ObservationRepository) (assessment: ResolutionAssessment) =
        match repository.TryAssessment assessment.AssessmentId with
        | None ->
            match repository.TryResolution assessment.ResolutionId with
            | None -> StoreOutcome.Conflict $"ResolutionId '{assessment.ResolutionId}' has not been ingested."
            | Some _ ->
                repository.SaveAssessment assessment
                StoreOutcome.Stored
        | Some existing when existing = assessment -> StoreOutcome.AlreadyPresent
        | Some _ -> StoreOutcome.Conflict $"AssessmentId '{assessment.AssessmentId}' already exists with different facts."

    let recordSearchObservation (repository: ObservationRepository) (observation: SearchObservation) =
        match repository.TrySearchObservation observation.ObservationId with
        | None ->
            repository.SaveSearchObservation observation
            StoreOutcome.Stored
        | Some existing when existing = observation -> StoreOutcome.AlreadyPresent
        | Some _ -> StoreOutcome.Conflict $"ObservationId '{observation.ObservationId}' already exists with different facts."

    let recordEffectObservation (repository: ObservationRepository) (observation: EffectObservation) =
        match repository.TryEffectObservation observation.ObservationId with
        | None ->
            repository.SaveEffectObservation observation
            StoreOutcome.Stored
        | Some existing when existing = observation -> StoreOutcome.AlreadyPresent
        | Some _ -> StoreOutcome.Conflict $"ObservationId '{observation.ObservationId}' already exists with different facts."

    let effectiveCurrent (repository: ObservationRepository) (selectedResolutionId: string option) (supersededResolutionIds: string list) =
        Projection.effectiveCurrent
            selectedResolutionId
            supersededResolutionIds
            (repository.ListResolutions())
            (repository.ListAssessments())

    let handoff
        repository
        selectedResolutionId
        supersededResolutionIds
        revision
        source
        authoritativeArtifacts
        historicalDecisionReferences
        facts
        assumptions
        unknowns
        obligations
        completedVerification
        legalNextActions
        =
        effectiveCurrent repository selectedResolutionId supersededResolutionIds
        |> Result.map (
            Projection.handoff
                revision
                source
                authoritativeArtifacts
                historicalDecisionReferences
                facts
                assumptions
                unknowns
                obligations
                completedVerification
                legalNextActions
        )
