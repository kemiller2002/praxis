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
    let private observeEvidenceIssues (repository: WorkEvidenceRepository) evidenceEntries =
        evidenceEntries
        |> List.choose (fun evidence ->
            match repository.Observe evidence with
            | EvidencePathObservation.Present -> None
            | EvidencePathObservation.Missing -> Some(EvidenceIssue.Missing evidence)
            | EvidencePathObservation.Unavailable message ->
                Some(EvidenceIssue.Unavailable(evidence, message)))

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
            let issues = observeEvidenceIssues repository request.ProvidedEvidence

            if issues.IsEmpty then
                VerifiedWorkPlanOutcome.Planned plan
            else
                VerifiedWorkPlanOutcome.EvidenceRejected issues

    let planVerifiedContext (repository: WorkEvidenceRepository) (request: WorkContextPlanRequest) =
        match WorkContextPlanning.plan request with
        | WorkContextPlanOutcome.Rejected rejection -> VerifiedWorkContextPlanOutcome.ContextRejected rejection
        | WorkContextPlanOutcome.Planned plan when request.Action <> WorkAction.Complete ->
            VerifiedWorkContextPlanOutcome.Planned plan
        | WorkContextPlanOutcome.Planned plan ->
            let issues = observeEvidenceIssues repository request.ProvidedEvidence

            if issues.IsEmpty then
                VerifiedWorkContextPlanOutcome.Planned plan
            else
                VerifiedWorkContextPlanOutcome.EvidenceRejected issues
