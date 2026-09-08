namespace Ros.Domain.Work

type WorkEvidence =
    { Type: string
      Path: string }

type LiveWorkItem =
    { Id: string
      WorkType: string
      LocalState: string
      SemanticState: LiveWorkState
      Evidence: WorkEvidence list
      BlockReason: string option
      UpdatedAt: string option
      CompletedAt: string option
      TelemetryExecutionIds: string list }

[<RequireQualifiedAccess>]
type TelemetryIntent =
    | EnsureActiveExecution
    | EnsureCompletableExecution
    | RecordBlocked of reason: string
    | RecordResumed
    | FinalizeExecutions

type WorkEventPlan =
    { EventType: string
      WorkItemId: string
      Repository: string
      ProtocolVersion: string
      OccurredAt: string
      Reason: string option
      Evidence: WorkEvidence list
      Paths: string list
      TelemetryExecutionIds: string list }

type WorkTransitionPlanRequest =
    { Item: LiveWorkItem
      Action: WorkAction
      TargetLocalState: string
      BlockReason: string option
      RequiredEvidence: Set<string>
      ProvidedEvidence: WorkEvidence list
      Repository: string
      ProtocolVersion: string
      OccurredAt: string
      ChangedPaths: string list
      TelemetryEnabled: bool }

type WorkTransitionPlan =
    { Item: LiveWorkItem
      Event: WorkEventPlan
      Telemetry: TelemetryIntent list }

[<RequireQualifiedAccess>]
type WorkPlanOutcome =
    | Planned of WorkTransitionPlan
    | Rejected of TransitionRejection

[<RequireQualifiedAccess>]
module WorkTransitionPlanning =
    let private eventType action =
        match action with
        | WorkAction.Begin -> "work.started"
        | WorkAction.Block -> "work.blocked"
        | WorkAction.Resume -> "work.resumed"
        | WorkAction.Complete -> "work.completed"

    let private telemetryIntents (request: WorkTransitionPlanRequest) =
        if not request.TelemetryEnabled then
            []
        else
            match request.Action with
            | WorkAction.Begin -> [ TelemetryIntent.EnsureActiveExecution ]
            | WorkAction.Block -> [ TelemetryIntent.RecordBlocked request.BlockReason.Value ]
            | WorkAction.Resume -> [ TelemetryIntent.RecordResumed; TelemetryIntent.EnsureActiveExecution ]
            | WorkAction.Complete ->
                (if request.Item.TelemetryExecutionIds.IsEmpty then
                     [ TelemetryIntent.EnsureCompletableExecution ]
                 else
                     [])
                @ [ TelemetryIntent.FinalizeExecutions ]

    let plan (request: WorkTransitionPlanRequest) =
        let decision =
            WorkTransition.decide
                { State = request.Item.SemanticState
                  Action = request.Action
                  BlockReason = request.BlockReason
                  RequiredEvidence = request.RequiredEvidence
                  ProvidedEvidence = request.ProvidedEvidence |> List.map _.Type |> Set.ofList }

        match decision with
        | TransitionDecision.Rejected rejection -> WorkPlanOutcome.Rejected rejection
        | TransitionDecision.Allowed target ->
            let evidence =
                if request.Action = WorkAction.Complete then request.ProvidedEvidence else request.Item.Evidence

            let item =
                { request.Item with
                    LocalState = request.TargetLocalState
                    SemanticState = target
                    Evidence = evidence
                    BlockReason =
                        if request.Action = WorkAction.Block then request.BlockReason else request.Item.BlockReason
                    UpdatedAt = Some request.OccurredAt
                    CompletedAt =
                        if request.Action = WorkAction.Complete then Some request.OccurredAt else request.Item.CompletedAt }

            WorkPlanOutcome.Planned
                { Item = item
                  Event =
                    { EventType = eventType request.Action
                      WorkItemId = item.Id
                      Repository = request.Repository
                      ProtocolVersion = request.ProtocolVersion
                      OccurredAt = request.OccurredAt
                      Reason = request.BlockReason
                      Evidence = evidence
                      Paths = if request.Action = WorkAction.Complete then request.ChangedPaths else []
                      TelemetryExecutionIds = item.TelemetryExecutionIds }
                  Telemetry = telemetryIntents request }
