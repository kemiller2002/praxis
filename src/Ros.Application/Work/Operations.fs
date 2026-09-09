namespace Ros.Application.Work

open Ros.Domain.Work

[<RequireQualifiedAccess>]
type WorkPersistenceOutcome =
    | Failed
    | Indeterminate

type WorkPersistenceFailure =
    { Operation: string
      Path: string option
      Message: string
      Outcome: WorkPersistenceOutcome }

type WorkStateWrite =
    { Path: string
      Content: string }

type BacklogStateWrite =
    { Path: string
      Content: string }

type WorkEvidenceRepository =
    { Observe: WorkEvidence -> EvidencePathObservation }

[<RequireQualifiedAccess>]
module WorkOperations =
    let decideTransition request = WorkTransition.decide request
    let planTransition request = WorkTransitionPlanning.plan request
    let planContext request = WorkContextPlanning.plan request
    let decideBacklogTransition request = BacklogTransition.decide request
    let planBacklogPromotion request = BacklogPromotion.plan request

    let planVerifiedTransition (repository: WorkEvidenceRepository) (request: WorkTransitionPlanRequest) =
        match WorkTransitionPlanning.plan request with
        | WorkPlanOutcome.Rejected rejection -> VerifiedWorkPlanOutcome.TransitionRejected rejection
        | WorkPlanOutcome.Planned plan when request.Action <> WorkAction.Complete ->
            VerifiedWorkPlanOutcome.Planned plan
        | WorkPlanOutcome.Planned plan ->
            let issues =
                request.ProvidedEvidence
                |> List.choose (fun evidence ->
                    match repository.Observe evidence with
                    | EvidencePathObservation.Present -> None
                    | EvidencePathObservation.Missing -> Some(EvidenceIssue.Missing evidence)
                    | EvidencePathObservation.Unavailable message ->
                        Some(EvidenceIssue.Unavailable(evidence, message)))

            if issues.IsEmpty then
                VerifiedWorkPlanOutcome.Planned plan
            else
                VerifiedWorkPlanOutcome.EvidenceRejected issues
