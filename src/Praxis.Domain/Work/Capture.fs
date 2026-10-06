namespace Praxis.Domain.Work

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
      /// Every work-item id named anywhere in the repository's event history,
      /// including items that have since left the queue and the live context.
      ExistingHistoryIds: Set<string>
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
/// generation. A generated id is never reused: it is allocated after the
/// highest `WI-NNNN` id known anywhere (queue, live context including
/// completed items, and event history), or at the persisted `nextSeq` when
/// that is already further ahead, and still skips any exact collision.
[<RequireQualifiedAccess>]
module WorkCapture =
    let private priorityValues = set [ "high"; "medium"; "low" ]

    let private generatedSequence (id: string) =
        let digits = if id.StartsWith "WI-" then id.Substring 3 else ""

        match digits <> "" && Seq.forall System.Char.IsAsciiDigit digits, System.Int32.TryParse digits with
        | true, (true, seq) -> Some seq
        | _ -> None

    let private nextId (knownIds: Set<string>) (nextSeq: int) =
        let start =
            knownIds
            |> Seq.choose generatedSequence
            |> Seq.fold max 0
            |> fun highest -> max nextSeq (highest + 1)

        let rec loop seq =
            let candidate = sprintf "WI-%04d" seq
            if knownIds.Contains candidate then loop (seq + 1) else candidate, seq + 1

        loop start

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
                        | None ->
                            nextId
                                (Set.unionMany [ request.ExistingQueueIds; request.ExistingContextIds; request.ExistingHistoryIds ])
                                request.NextSeq

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
