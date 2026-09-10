namespace Ros.Domain.Work

type WorkAttachmentSummary =
    { Id: string
      Name: string
      Size: int
      ContentType: string option
      UploadedAt: string }

/// The subset of a raw `queue.json` item `work list`/`work show` project --
/// a wider slice than `BacklogQueueRow` (used only for `queue.md`
/// rendering), since production's own `mergedRows` (`tools/ros_cli.mjs`)
/// exposes `description`, `blockedReason`, and `attachments` too.
type QueueItemDetail =
    { Id: string
      Title: string
      Description: string option
      Tags: string list
      Priority: string option
      Status: string
      BlockedReason: string option
      Attachments: WorkAttachmentSummary list }

type LiveWorkSummary =
    { State: string
      SemanticState: string
      AllowedActions: string list }

type WorkListRow =
    { Id: string
      Title: string
      Description: string option
      Tags: string list
      Priority: string option
      Status: string
      BlockedReason: string option
      BacklogActions: string list
      Attachments: WorkAttachmentSummary list
      LiveWorkItem: LiveWorkSummary option }

/// Mirrors production `mergedRows` (`tools/ros_cli.mjs`) at its full
/// `work`/`work list`/`work show` fidelity -- unlike `QueuePresentation`'s
/// same-named function, which only carries the five fields `queue.md`
/// itself renders.
[<RequireQualifiedAccess>]
module WorkListView =
    let private semanticStateCode (state: LiveWorkState) =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"

    let private actionCode (action: WorkAction) =
        match action with
        | WorkAction.Begin -> "begin"
        | WorkAction.Block -> "block"
        | WorkAction.Resume -> "resume"
        | WorkAction.Complete -> "complete"

    /// Every id present in the queue or the live context, ordinal-sorted;
    /// `backlogActions` only fires for an id known solely to the backlog
    /// queue (a live counterpart always takes over as the one authority --
    /// see `QueuePresentation.effectiveStatus`), and `blockedReason` is
    /// carried over from a blocked live item, else from the backlog's own
    /// (possibly absent) field -- matching production's `undefined` vs
    /// `null` distinction: the field is genuinely omitted when neither
    /// source has it, never coerced to an explicit null.
    let mergedRows (queueItems: QueueItemDetail list) (contextItems: LiveWorkItem list) : WorkListRow list =
        let queueById = queueItems |> List.map (fun item -> item.Id, item) |> Map.ofList
        let contextById = contextItems |> List.map (fun item -> item.Id, item) |> Map.ofList

        let ids =
            (queueItems |> List.map (fun item -> item.Id))
            @ (contextItems |> List.map (fun item -> item.Id))
            |> List.distinct
            |> List.sortWith (fun left right -> System.String.CompareOrdinal(left, right))

        ids
        |> List.map (fun id ->
            let queueItem = queueById.TryFind id
            let contextItem = contextById.TryFind id

            let status =
                QueuePresentation.effectiveStatus
                    (queueItem |> Option.map (fun item -> item.Status))
                    (contextItem |> Option.map (fun item -> item.SemanticState))
                |> Option.defaultValue ""

            let blockedReason =
                match contextItem with
                | Some item when item.SemanticState = LiveWorkState.Blocked -> item.BlockReason
                | _ -> queueItem |> Option.bind (fun item -> item.BlockedReason)

            let backlogActions =
                match queueItem, contextItem with
                | Some item, None ->
                    item.Status
                    |> BacklogState.parse
                    |> Option.map BacklogTransition.allowedActions
                    |> Option.defaultValue []
                    |> List.map BacklogAction.code
                    |> List.sort
                | _ -> []

            let liveWorkItem =
                contextItem
                |> Option.map (fun item ->
                    { State = item.LocalState
                      SemanticState = semanticStateCode item.SemanticState
                      AllowedActions = WorkTransition.allowedActions item.SemanticState |> List.map actionCode |> List.sort })

            { Id = id
              Title = queueItem |> Option.map (fun item -> item.Title) |> Option.defaultValue id
              Description = queueItem |> Option.bind (fun item -> item.Description)
              Tags = queueItem |> Option.map (fun item -> item.Tags) |> Option.defaultValue []
              Priority = queueItem |> Option.bind (fun item -> item.Priority)
              Status = status
              BlockedReason = blockedReason
              BacklogActions = backlogActions
              Attachments = queueItem |> Option.map (fun item -> item.Attachments) |> Option.defaultValue []
              LiveWorkItem = liveWorkItem })
