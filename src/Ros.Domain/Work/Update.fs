namespace Ros.Domain.Work

type WorkUpdateRequest =
    { Id: string
      QueueContainsId: bool
      ContextContainsId: bool
      Title: string option
      Description: string option
      Tags: string list option
      Priority: string option
      OccurredAt: string }

[<RequireQualifiedAccess>]
type WorkTitleChange =
    | Keep
    | Set of title: string

[<RequireQualifiedAccess>]
type WorkDescriptionChange =
    | Keep
    | Set of description: string option

[<RequireQualifiedAccess>]
type WorkTagsChange =
    | Keep
    | Set of tags: string list

[<RequireQualifiedAccess>]
type WorkPriorityChange =
    | Keep
    | Set of priority: string

type WorkUpdatePlan =
    { UpsertNew: bool
      Title: WorkTitleChange
      Description: WorkDescriptionChange
      Tags: WorkTagsChange
      Priority: WorkPriorityChange
      UpdatedAt: string }

[<RequireQualifiedAccess>]
type WorkUpdateRejection =
    | InvalidId of id: string
    | NotFound of id: string
    | EmptyTitle
    | InvalidPriority of priority: string

[<RequireQualifiedAccess>]
type WorkUpdateOutcome =
    | Planned of WorkUpdatePlan
    | Rejected of WorkUpdateRejection

/// Mirrors production `updateWorkUnlocked`/`findOrCreateQueueEntry`
/// (`tools/ros_cli.mjs`), excluding `--file` attachment: an id already in
/// the queue is updated in place; an id absent from the queue but present
/// in the live context is upserted with production's exact minimal-record
/// defaults first; an id in neither is rejected. Only fields explicitly
/// provided change -- production's own `options.field !== undefined` gate,
/// not a full-record replace.
[<RequireQualifiedAccess>]
module WorkUpdate =
    let private priorityValues = set [ "high"; "medium"; "low" ]

    let plan (request: WorkUpdateRequest) : WorkUpdateOutcome =
        let buildPlan upsertNew =
            match request.Title with
            | Some title when title.Trim() = "" -> WorkUpdateOutcome.Rejected WorkUpdateRejection.EmptyTitle
            | _ ->
                match request.Priority with
                | Some priority when not (priorityValues.Contains priority) ->
                    WorkUpdateOutcome.Rejected(WorkUpdateRejection.InvalidPriority priority)
                | _ ->
                    let descriptionChange =
                        request.Description
                        |> Option.map (fun description ->
                            let trimmed = description.Trim()
                            WorkDescriptionChange.Set(if trimmed = "" then None else Some trimmed))
                        |> Option.defaultValue WorkDescriptionChange.Keep

                    WorkUpdateOutcome.Planned
                        { UpsertNew = upsertNew
                          Title = request.Title |> Option.map (fun title -> WorkTitleChange.Set(title.Trim())) |> Option.defaultValue WorkTitleChange.Keep
                          Description = descriptionChange
                          Tags = request.Tags |> Option.map WorkTagsChange.Set |> Option.defaultValue WorkTagsChange.Keep
                          Priority = request.Priority |> Option.map WorkPriorityChange.Set |> Option.defaultValue WorkPriorityChange.Keep
                          UpdatedAt = request.OccurredAt }

        if request.QueueContainsId then
            buildPlan false
        elif not (WorkItemId.isValid request.Id) then
            WorkUpdateOutcome.Rejected(WorkUpdateRejection.InvalidId request.Id)
        elif not request.ContextContainsId then
            WorkUpdateOutcome.Rejected(WorkUpdateRejection.NotFound request.Id)
        else
            buildPlan true
