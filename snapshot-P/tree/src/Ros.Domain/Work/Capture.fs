namespace Ros.Domain.Work

type WorkCaptureRequest =
    { Title: string
      ExplicitId: string option
      Priority: string option
      Description: string option
      Tags: string list
      Actor: string
      Source: string option
      SourceReference: string option
      ExistingQueueIds: Set<string>
      ExistingContextIds: Set<string>
      NextSeq: int
      OccurredAt: string }

type CapturedWorkItem =
    { Id: string
      Title: string
      Description: string option
      Tags: string list
      Priority: string
      Status: string
      CreatedAt: string
      UpdatedAt: string
      CreatedBy: string
      Source: string
      SourceReference: string option }

type WorkCapturePlan =
    { Item: CapturedWorkItem
      NextSeq: int }

[<RequireQualifiedAccess>]
type WorkCaptureRejection =
    | EmptyTitle
    | InvalidPriority of priority: string
    | InvalidId of id: string
    | DuplicateInQueue of id: string
    | DuplicateInContext of id: string

[<RequireQualifiedAccess>]
type WorkCaptureOutcome =
    | Planned of WorkCapturePlan
    | Rejected of WorkCaptureRejection

/// Mirrors production `captureWorkUnlocked`/`nextQueueId` (`tools/ros_cli.mjs`),
/// excluding `--file` attachment (a separate, larger effect this slice does
/// not attempt): title/priority validation, explicit-id validation against
/// both the queue and the live context, and collision-avoiding sequential ID
/// generation over the queue's own ids only (matching production, which
/// never checks the live context for an auto-generated id).
[<RequireQualifiedAccess>]
module WorkCapture =
    let private priorityValues = set [ "high"; "medium"; "low" ]

    let private nextId (existingQueueIds: Set<string>) (nextSeq: int) =
        let rec loop seq =
            let candidate = sprintf "WI-%04d" seq
            if existingQueueIds.Contains candidate then loop (seq + 1) else candidate, seq + 1

        loop nextSeq

    let plan (request: WorkCaptureRequest) : WorkCaptureOutcome =
        let trimmedTitle = request.Title.Trim()

        if trimmedTitle = "" then
            WorkCaptureOutcome.Rejected WorkCaptureRejection.EmptyTitle
        else
            match request.Priority with
            | Some priority when not (priorityValues.Contains priority) ->
                WorkCaptureOutcome.Rejected(WorkCaptureRejection.InvalidPriority priority)
            | _ ->
                match request.ExplicitId with
                | Some id when not (WorkItemId.isValid id) -> WorkCaptureOutcome.Rejected(WorkCaptureRejection.InvalidId id)
                | Some id when request.ExistingQueueIds.Contains id ->
                    WorkCaptureOutcome.Rejected(WorkCaptureRejection.DuplicateInQueue id)
                | Some id when request.ExistingContextIds.Contains id ->
                    WorkCaptureOutcome.Rejected(WorkCaptureRejection.DuplicateInContext id)
                | explicitId ->
                    let id, newNextSeq =
                        match explicitId with
                        | Some id -> id, request.NextSeq
                        | None -> nextId request.ExistingQueueIds request.NextSeq

                    let trimmedDescription =
                        request.Description
                        |> Option.map (fun description -> description.Trim())
                        |> Option.filter (fun description -> description <> "")

                    let item =
                        { Id = id
                          Title = trimmedTitle
                          Description = trimmedDescription
                          Tags = request.Tags
                          Priority = request.Priority |> Option.defaultValue "medium"
                          Status = "captured"
                          CreatedAt = request.OccurredAt
                          UpdatedAt = request.OccurredAt
                          CreatedBy = request.Actor
                          Source = request.Source |> Option.defaultValue "manual"
                          SourceReference = request.SourceReference }

                    WorkCaptureOutcome.Planned { Item = item; NextSeq = newNextSeq }
