namespace Ros.Domain.Work

open Ros.Domain.Telemetry

/// Read model an effect boundary supplies: the item's currently linked
/// execution IDs and the observable telemetry executions for its work item.
type TelemetryItemState =
    { LinkedExecutionIds: string list
      Candidates: ExecutionLinkCandidate list
      RequestedExecutionId: string option }

[<RequireQualifiedAccess>]
type TelemetryResolutionStep =
    | Recovered of executionId: string
    | LinkedActive of executionIds: string list
    | Finalized of executionIds: string list
    | RequiresNewExecution
    | NoChange

[<RequireQualifiedAccess>]
type TelemetryResolutionRejection =
    | DetachedConflict of executionIds: string list
    | Ambiguous of executionIds: string list

/// A required mutation this resolution could not itself perform because it
/// touches an execution record rather than the item/event projection.
[<RequireQualifiedAccess>]
type TelemetryEffect =
    | FinalizeExecutions of executionIds: string list
    | CreateExecution

[<RequireQualifiedAccess>]
type TelemetryResolutionOutcome =
    /// Every intent resolved against already-observed candidates; no new
    /// execution record is required.
    | Resolved of executionIds: string list * steps: TelemetryResolutionStep list * effects: TelemetryEffect list
    /// Resolution reached an intent that requires creating a new execution
    /// record. Steps/effects cover only the intents decided before the halt;
    /// any remaining intents were not evaluated because they would need to
    /// observe the not-yet-created record.
    | PendingNewExecution of executionIdsSoFar: string list * steps: TelemetryResolutionStep list * effects: TelemetryEffect list
    | Rejected of TelemetryResolutionRejection

/// Composes the abstract telemetry intents a work-transition plan already
/// produces with an observed candidate-execution read model, so the final
/// item/event telemetry-execution-ID projection can be typed and verified
/// before any state-changing effect handler renders the write set.
[<RequireQualifiedAccess>]
module TelemetryResolution =
    let private activeCandidateIds (state: TelemetryItemState) =
        state.Candidates
        |> List.filter (fun candidate -> candidate.Status = ExecutionStatus.Active)
        |> List.map _.ExecutionId
        |> List.distinct
        |> List.sort

    let private appendNew ids linked =
        linked @ (ids |> List.filter (fun id -> not (List.contains id linked)))

    /// Mirrors production `recoverOrStartExecution`: recover the single
    /// unlinked candidate in the requested statuses, reject an explicit
    /// request that conflicts with detached evidence or an ambiguous
    /// multi-candidate set, or signal that a new record is required.
    let private recoverOrStart workItemId statuses (state: TelemetryItemState) linked =
        let request: ExecutionLinkRequest =
            { WorkItemId = workItemId
              LinkedExecutionIds = linked |> Set.ofList
              RecoverableStatuses = statuses
              RequestedExecutionId = state.RequestedExecutionId
              Candidates = state.Candidates }

        match ExecutionLinkRecovery.decide request with
        | ExecutionLinkDecision.Recover executionId -> Ok(linked @ [ executionId ], TelemetryResolutionStep.Recovered executionId)
        | ExecutionLinkDecision.StartNew -> Ok(linked, TelemetryResolutionStep.RequiresNewExecution)
        | ExecutionLinkDecision.RejectDetachedConflict ids -> Error(TelemetryResolutionRejection.DetachedConflict ids)
        | ExecutionLinkDecision.RejectAmbiguous ids -> Error(TelemetryResolutionRejection.Ambiguous ids)

    /// Mirrors production resume: link every currently active execution for
    /// the work item regardless of prior link state, or require a new record
    /// when none is active. Never rejects on multiple candidates.
    let private linkAllActive workItemId (state: TelemetryItemState) linked =
        match activeCandidateIds state with
        | [] -> recoverOrStart workItemId (Set.singleton ExecutionStatus.Active) state linked
        | activeIds -> Ok(appendNew activeIds linked, TelemetryResolutionStep.LinkedActive activeIds)

    let resolve (workItemId: string) (intents: TelemetryIntent list) (state: TelemetryItemState) =
        let isResume = intents |> List.contains TelemetryIntent.RecordResumed

        let halt linked steps effects step effect =
            TelemetryResolutionOutcome.PendingNewExecution(linked, List.rev (step :: steps), List.rev (effect :: effects))

        let rec loop linked steps effects remaining =
            match remaining with
            | [] -> Ok(linked, steps, effects)
            | TelemetryIntent.EnsureActiveExecution :: tail ->
                let outcome =
                    if isResume then
                        linkAllActive workItemId state linked
                    else
                        recoverOrStart workItemId (Set.singleton ExecutionStatus.Active) state linked

                match outcome with
                | Ok(nextLinked, TelemetryResolutionStep.RequiresNewExecution) ->
                    Error(halt nextLinked steps effects TelemetryResolutionStep.RequiresNewExecution TelemetryEffect.CreateExecution)
                | Ok(nextLinked, step) -> loop nextLinked (step :: steps) effects tail
                | Error rejection -> Error(TelemetryResolutionOutcome.Rejected rejection)
            | TelemetryIntent.EnsureCompletableExecution :: tail when linked.IsEmpty ->
                match recoverOrStart workItemId (Set.ofList [ ExecutionStatus.Active; ExecutionStatus.Finalized ]) state linked with
                | Ok(nextLinked, TelemetryResolutionStep.RequiresNewExecution) ->
                    Error(halt nextLinked steps effects TelemetryResolutionStep.RequiresNewExecution TelemetryEffect.CreateExecution)
                | Ok(nextLinked, step) -> loop nextLinked (step :: steps) effects tail
                | Error rejection -> Error(TelemetryResolutionOutcome.Rejected rejection)
            | TelemetryIntent.EnsureCompletableExecution :: tail ->
                loop linked (TelemetryResolutionStep.NoChange :: steps) effects tail
            | TelemetryIntent.RecordBlocked _ :: tail -> loop linked (TelemetryResolutionStep.NoChange :: steps) effects tail
            | TelemetryIntent.RecordResumed :: tail -> loop linked (TelemetryResolutionStep.NoChange :: steps) effects tail
            | TelemetryIntent.FinalizeExecutions :: tail ->
                let finalizedIds = activeCandidateIds state
                let nextLinked = appendNew finalizedIds linked
                loop
                    nextLinked
                    (TelemetryResolutionStep.Finalized finalizedIds :: steps)
                    (TelemetryEffect.FinalizeExecutions finalizedIds :: effects)
                    tail

        match loop state.LinkedExecutionIds [] [] intents with
        | Ok(finalIds, steps, effects) -> TelemetryResolutionOutcome.Resolved(finalIds, List.rev steps, List.rev effects)
        | Error outcome -> outcome

[<RequireQualifiedAccess>]
type ResolvedTelemetryOutcome =
    /// The final item/event projection, frozen with resolved execution IDs.
    | Resolved of WorkTransitionPlan
    | PendingNewExecution of workItemId: string
    | Rejected of workItemId: string * TelemetryResolutionRejection

[<RequireQualifiedAccess>]
type ResolvedTelemetryContextOutcome =
    | Resolved of WorkContextPlan
    | PendingNewExecution of workItemId: string
    | Rejected of workItemId: string * TelemetryResolutionRejection

/// Composes a plan's abstract telemetry intents with an observation function
/// supplied by an effect boundary, freezing the final item/event telemetry
/// projection when no new execution record is required.
[<RequireQualifiedAccess>]
module TelemetryPlanResolution =
    let private applyResolution (itemPlan: WorkTransitionPlan) executionIds =
        { itemPlan with
            Item = { itemPlan.Item with TelemetryExecutionIds = executionIds }
            Event = { itemPlan.Event with TelemetryExecutionIds = executionIds } }

    let resolvePlan (observe: string -> TelemetryItemState) (itemPlan: WorkTransitionPlan) =
        match TelemetryResolution.resolve itemPlan.Item.Id itemPlan.Telemetry (observe itemPlan.Item.Id) with
        | TelemetryResolutionOutcome.Resolved(executionIds, _, _) ->
            ResolvedTelemetryOutcome.Resolved(applyResolution itemPlan executionIds)
        | TelemetryResolutionOutcome.PendingNewExecution _ -> ResolvedTelemetryOutcome.PendingNewExecution itemPlan.Item.Id
        | TelemetryResolutionOutcome.Rejected rejection -> ResolvedTelemetryOutcome.Rejected(itemPlan.Item.Id, rejection)

    /// Resolves every item plan in request order. The first item whose
    /// telemetry cannot be fully resolved without a new execution, or that
    /// rejects, stops the composition before any other item's resolution is
    /// claimed — no partial frozen plan is returned.
    let resolveContext (observe: string -> TelemetryItemState) (plan: WorkContextPlan) =
        let rec loop workItems resolvedPlans remainingPlans =
            match remainingPlans with
            | [] -> Ok(workItems, List.rev resolvedPlans)
            | (itemPlan: WorkTransitionPlan) :: tail ->
                match resolvePlan observe itemPlan with
                | ResolvedTelemetryOutcome.Resolved resolvedPlan ->
                    let updatedItems =
                        workItems
                        |> List.map (fun item -> if item.Id = resolvedPlan.Item.Id then resolvedPlan.Item else item)

                    loop updatedItems (resolvedPlan :: resolvedPlans) tail
                | ResolvedTelemetryOutcome.PendingNewExecution workItemId ->
                    Error(ResolvedTelemetryContextOutcome.PendingNewExecution workItemId)
                | ResolvedTelemetryOutcome.Rejected(workItemId, rejection) ->
                    Error(ResolvedTelemetryContextOutcome.Rejected(workItemId, rejection))

        match loop plan.WorkItems [] plan.ItemPlans with
        | Ok(workItems, itemPlans) ->
            ResolvedTelemetryContextOutcome.Resolved
                { plan with
                    WorkItems = workItems
                    ItemPlans = itemPlans }
        | Error outcome -> outcome
