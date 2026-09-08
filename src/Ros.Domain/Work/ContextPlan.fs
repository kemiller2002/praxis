namespace Ros.Domain.Work

open System.Text.RegularExpressions

type WorkContextPlanningView =
    { WorkItems: LiveWorkItem list
      StartedAt: string option
      BaselineDirtyPaths: string list }

type WorkContextPlanRequest =
    { Context: WorkContextPlanningView
      Action: WorkAction
      WorkItemIds: string list
      NewItemType: string
      TargetLocalState: string
      BlockReason: string option
      DefaultRequiredEvidence: Set<string>
      RequiredEvidenceByType: Map<string, Set<string>>
      ProvidedEvidence: WorkEvidence list
      Repository: string
      ProtocolVersion: string
      Actor: string
      OccurredAt: string
      MeaningfulChangedPaths: string list
      ObservedGitPaths: string list
      TelemetryEnabled: bool }

type WorkContextPlan =
    { WorkItems: LiveWorkItem list
      ItemPlans: WorkTransitionPlan list
      Repository: string
      ProtocolVersion: string
      Actor: string
      StartedAt: string option
      BaselineDirtyPaths: string list
      UpdatedAt: string }

[<RequireQualifiedAccess>]
type WorkContextRejection =
    | NoWorkItems
    | InvalidWorkItemId of workItemId: string
    | WorkItemNotInContext of workItemId: string
    | ItemTransitionRejected of workItemId: string * rejection: TransitionRejection

[<RequireQualifiedAccess>]
type WorkContextPlanOutcome =
    | Planned of WorkContextPlan
    | Rejected of WorkContextRejection

[<RequireQualifiedAccess>]
module WorkContextPlanning =
    let private validWorkItemId (workItemId: string) =
        Regex.IsMatch(workItemId, "^[A-Z][A-Z0-9_-]*-[A-Z0-9][A-Z0-9_-]*$")

    let private replaceAt index replacement items =
        items
        |> List.mapi (fun candidateIndex item -> if candidateIndex = index then replacement else item)

    let private requiredEvidence request workType =
        request.RequiredEvidenceByType
        |> Map.tryFind workType
        |> Option.defaultValue request.DefaultRequiredEvidence

    let private newItem request workItemId =
        { Id = workItemId
          WorkType = request.NewItemType
          LocalState = "ready"
          SemanticState = LiveWorkState.Ready
          Evidence = []
          BlockReason = None
          UpdatedAt = None
          CompletedAt = None
          TelemetryExecutionIds = [] }

    let plan (request: WorkContextPlanRequest) =
        let rec planItems workItems plans remainingIds =
            match remainingIds with
            | [] -> Ok(workItems, List.rev plans)
            | workItemId :: tail when not (validWorkItemId workItemId) ->
                Error(WorkContextRejection.InvalidWorkItemId workItemId)
            | workItemId :: tail ->
                let located = workItems |> List.tryFindIndex (fun item -> item.Id = workItemId)

                let selected =
                    match located, request.Action with
                    | Some index, _ -> Ok(index, workItems[index], workItems)
                    | None, WorkAction.Begin ->
                        let item = newItem request workItemId
                        Ok(workItems.Length, item, workItems @ [ item ])
                    | None, _ -> Error(WorkContextRejection.WorkItemNotInContext workItemId)

                match selected with
                | Error rejection -> Error rejection
                | Ok(index, item, currentItems) ->
                    let transitionRequest =
                        { Item = item
                          Action = request.Action
                          TargetLocalState = request.TargetLocalState
                          BlockReason = request.BlockReason
                          RequiredEvidence = requiredEvidence request item.WorkType
                          ProvidedEvidence = request.ProvidedEvidence
                          Repository = request.Repository
                          ProtocolVersion = request.ProtocolVersion
                          OccurredAt = request.OccurredAt
                          ChangedPaths =
                            if request.Action = WorkAction.Complete then
                                request.MeaningfulChangedPaths
                                |> List.filter (fun path -> request.Context.BaselineDirtyPaths |> List.contains path |> not)
                            else
                                []
                          TelemetryEnabled = request.TelemetryEnabled }

                    match WorkTransitionPlanning.plan transitionRequest with
                    | WorkPlanOutcome.Rejected rejection ->
                        Error(WorkContextRejection.ItemTransitionRejected(workItemId, rejection))
                    | WorkPlanOutcome.Planned transitionPlan ->
                        planItems (replaceAt index transitionPlan.Item currentItems) (transitionPlan :: plans) tail

        if request.WorkItemIds.IsEmpty then
            WorkContextPlanOutcome.Rejected WorkContextRejection.NoWorkItems
        else
            match planItems request.Context.WorkItems [] request.WorkItemIds with
            | Error rejection -> WorkContextPlanOutcome.Rejected rejection
            | Ok(workItems, itemPlans) ->
                let firstBegin = request.Action = WorkAction.Begin && request.Context.StartedAt.IsNone
                let workItemIds = workItems |> List.map _.Id |> Set.ofList

                WorkContextPlanOutcome.Planned
                    { WorkItems = workItems
                      ItemPlans = itemPlans
                      Repository = request.Repository
                      ProtocolVersion = request.ProtocolVersion
                      Actor = request.Actor
                      StartedAt = if firstBegin then Some request.OccurredAt else request.Context.StartedAt
                      BaselineDirtyPaths =
                        if firstBegin then
                            request.ObservedGitPaths |> List.filter (fun path -> not (workItemIds.Contains path))
                        else
                            request.Context.BaselineDirtyPaths
                      UpdatedAt = request.OccurredAt }
