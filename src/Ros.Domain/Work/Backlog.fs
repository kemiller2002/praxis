namespace Ros.Domain.Work

[<RequireQualifiedAccess>]
type BacklogState =
    | Captured
    | Ready
    | Blocked
    | Abandoned

[<RequireQualifiedAccess>]
type BacklogAction =
    | Ready
    | Block
    | Abandon
    | Start

type BacklogTransitionRequest =
    { State: BacklogState
      Action: BacklogAction
      Reason: string option }

[<RequireQualifiedAccess>]
type BacklogFieldChange =
    | Keep
    | Clear
    | Set of value: string

[<RequireQualifiedAccess>]
type BacklogTransitionEffect =
    | ChangeState of state: BacklogState * blockedReason: BacklogFieldChange * abandonedReason: BacklogFieldChange
    | PromoteToLiveWork

[<RequireQualifiedAccess>]
type BacklogTransitionRejection =
    | IllegalTransition of state: BacklogState * action: BacklogAction
    | BlockReasonRequired

[<RequireQualifiedAccess>]
type BacklogTransitionDecision =
    | Allowed of BacklogTransitionEffect
    | Rejected of BacklogTransitionRejection

type BacklogPromotionRequest =
    { WorkItemIds: string list
      QueueStates: Map<string, BacklogState>
      WorkType: string }

type BacklogPromotionPlan =
    { WorkItemIds: string list
      WorkType: string }

[<RequireQualifiedAccess>]
type BacklogPromotionRejection =
    | NoWorkItems
    | InvalidWorkItemId of workItemId: string
    | BacklogItemNotReady of workItemId: string * state: BacklogState

[<RequireQualifiedAccess>]
type BacklogPromotionOutcome =
    | Planned of BacklogPromotionPlan
    | Rejected of BacklogPromotionRejection

[<RequireQualifiedAccess>]
module BacklogTransition =
    let decide request =
        match request.State, request.Action with
        | BacklogState.Captured, BacklogAction.Ready
        | BacklogState.Blocked, BacklogAction.Ready ->
            BacklogTransitionEffect.ChangeState(BacklogState.Ready, BacklogFieldChange.Clear, BacklogFieldChange.Keep)
            |> BacklogTransitionDecision.Allowed
        | BacklogState.Ready, BacklogAction.Block ->
            match request.Reason with
            | Some reason when not (System.String.IsNullOrEmpty reason) ->
                BacklogTransitionEffect.ChangeState(
                    BacklogState.Blocked,
                    BacklogFieldChange.Set reason,
                    BacklogFieldChange.Keep)
                |> BacklogTransitionDecision.Allowed
            | _ -> BacklogTransitionDecision.Rejected BacklogTransitionRejection.BlockReasonRequired
        | (BacklogState.Captured | BacklogState.Ready | BacklogState.Blocked), BacklogAction.Abandon ->
            let abandonedReason =
                request.Reason
                |> Option.filter (System.String.IsNullOrEmpty >> not)
                |> Option.map BacklogFieldChange.Set
                |> Option.defaultValue BacklogFieldChange.Keep

            BacklogTransitionEffect.ChangeState(BacklogState.Abandoned, BacklogFieldChange.Keep, abandonedReason)
            |> BacklogTransitionDecision.Allowed
        | BacklogState.Ready, BacklogAction.Start ->
            BacklogTransitionDecision.Allowed BacklogTransitionEffect.PromoteToLiveWork
        | state, action ->
            BacklogTransitionRejection.IllegalTransition(state, action)
            |> BacklogTransitionDecision.Rejected

[<RequireQualifiedAccess>]
module BacklogPromotion =
    let plan (request: BacklogPromotionRequest) =
        let rejection =
            request.WorkItemIds
            |> List.tryPick (fun workItemId ->
                if not (WorkItemId.isValid workItemId) then
                    Some(BacklogPromotionRejection.InvalidWorkItemId workItemId)
                else
                    match request.QueueStates |> Map.tryFind workItemId with
                    | Some BacklogState.Ready
                    | None -> None
                    | Some state -> Some(BacklogPromotionRejection.BacklogItemNotReady(workItemId, state)))

        if request.WorkItemIds.IsEmpty then
            BacklogPromotionOutcome.Rejected BacklogPromotionRejection.NoWorkItems
        else
            match rejection with
            | Some failure -> BacklogPromotionOutcome.Rejected failure
            | None ->
                BacklogPromotionOutcome.Planned
                    { WorkItemIds = request.WorkItemIds
                      WorkType = request.WorkType }
