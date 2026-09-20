namespace Ros.Application.Ordo

open Ros.Domain.Ordo

[<RequireQualifiedAccess>]
type StoreOutcome =
    | Stored
    | AlreadyPresent
    | Conflict of string

type ObservationRepository =
    { TryResolution: string -> ResolutionObservation option
      SaveResolution: ResolutionObservation -> rawJson: string -> unit
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
    let ingestResolution repository raw observation =
        match repository.TryResolution observation.ResolutionId with
        | None ->
            repository.SaveResolution observation raw
            StoreOutcome.Stored
        | Some existing when existing = observation -> StoreOutcome.AlreadyPresent
        | Some _ ->
            StoreOutcome.Conflict $"ResolutionId '{observation.ResolutionId}' already exists with different observed facts."

    let recordAssessment repository assessment =
        match repository.TryAssessment assessment.AssessmentId with
        | None ->
            match repository.TryResolution assessment.ResolutionId with
            | None -> StoreOutcome.Conflict $"ResolutionId '{assessment.ResolutionId}' has not been ingested."
            | Some _ ->
                repository.SaveAssessment assessment
                StoreOutcome.Stored
        | Some existing when existing = assessment -> StoreOutcome.AlreadyPresent
        | Some _ -> StoreOutcome.Conflict $"AssessmentId '{assessment.AssessmentId}' already exists with different facts."

    let recordSearchObservation repository observation =
        match repository.TrySearchObservation observation.ObservationId with
        | None ->
            repository.SaveSearchObservation observation
            StoreOutcome.Stored
        | Some existing when existing = observation -> StoreOutcome.AlreadyPresent
        | Some _ -> StoreOutcome.Conflict $"ObservationId '{observation.ObservationId}' already exists with different facts."

    let recordEffectObservation repository observation =
        match repository.TryEffectObservation observation.ObservationId with
        | None ->
            repository.SaveEffectObservation observation
            StoreOutcome.Stored
        | Some existing when existing = observation -> StoreOutcome.AlreadyPresent
        | Some _ -> StoreOutcome.Conflict $"ObservationId '{observation.ObservationId}' already exists with different facts."

    let effectiveCurrent repository =
        Projection.effectiveCurrent (repository.ListResolutions()) (repository.ListAssessments())

    let handoff repository revision source facts assumptions unknowns obligations legalNextActions =
        effectiveCurrent repository
        |> Projection.handoff revision source facts assumptions unknowns obligations legalNextActions
