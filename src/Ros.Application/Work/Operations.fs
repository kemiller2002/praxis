namespace Ros.Application.Work

open Ros.Domain.Work

type TelemetryStateRepository = { Observe: string -> TelemetryItemState }

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

    /// Composes a single planned transition with its observed telemetry read
    /// model. The final item/event projection is frozen with resolved
    /// execution IDs only when no intent requires creating a new execution
    /// record; otherwise the caller learns exactly which effect remains.
    let resolveTelemetry (repository: TelemetryStateRepository) (itemPlan: WorkTransitionPlan) =
        TelemetryPlanResolution.resolvePlan repository.Observe itemPlan

    /// Composes the whole ordered context plan with telemetry so the
    /// event/context write set can be rendered with resolved execution IDs.
    let resolveContextTelemetry (repository: TelemetryStateRepository) (plan: WorkContextPlan) =
        TelemetryPlanResolution.resolveContext repository.Observe plan

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
