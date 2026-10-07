namespace Praxis.Domain.Work

type BacklogQueueItemRecord =
    { Id: string
      Status: string
      Priority: string option }

type BacklogQueueFinding =
    { Path: string
      Field: string
      Message: string }

/// Mirrors production `queueFindings` (`tools/ros_cli.mjs`): a pure decision
/// over the raw backlog queue rows, never a validated `BacklogState`, since
/// production reports an unrecognized status/priority as a finding rather
/// than rejecting parse.
[<RequireQualifiedAccess>]
module BacklogQueueValidation =
    let private queuePath = ".ros/work/queue.json"
    let private statusValues = set [ "captured"; "ready"; "blocked"; "abandoned"; "complete" ]
    let private priorityValues = set [ "high"; "medium"; "low" ]

    let findings (items: BacklogQueueItemRecord list) : BacklogQueueFinding list =
        let seen = System.Collections.Generic.HashSet<string>()

        items
        |> List.collect (fun item ->
            let duplicate: BacklogQueueFinding list =
                if seen.Contains item.Id then
                    [ { Path = queuePath; Field = "id"; Message = $"duplicate backlog id '{item.Id}'" } ]
                else
                    []

            seen.Add item.Id |> ignore

            let invalidId: BacklogQueueFinding list =
                if not (WorkItemId.isValid item.Id) then
                    [ { Path = queuePath; Field = "id"; Message = $"invalid backlog id '{item.Id}'" } ]
                else
                    []

            let invalidStatus: BacklogQueueFinding list =
                if not (statusValues.Contains item.Status) then
                    [ { Path = queuePath
                        Field = "status"
                        Message = $"invalid status '{item.Status}' for '{item.Id}'" } ]
                else
                    []

            let invalidPriority: BacklogQueueFinding list =
                match item.Priority with
                | Some priority when not (priorityValues.Contains priority) ->
                    [ { Path = queuePath
                        Field = "priority"
                        Message = $"invalid priority '{priority}' for '{item.Id}'" } ]
                | _ -> []

            duplicate @ invalidId @ invalidStatus @ invalidPriority)

    let private terminalStatuses = set [ "complete"; "abandoned" ]

    let private liveStateCode (state: LiveWorkState) =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"
        | LiveWorkState.Abandoned -> "abandoned"

    /// The field every backlog/live-context disagreement is reported under,
    /// so the repair hint can name the commands that resolve it.
    let statusAgreementField = "status_agreement"

    /// A backlog row and a live work item with the same ID are one
    /// obligation. A row that is still captured, ready or blocked is the
    /// normal pre-promotion record (the live item is authoritative from
    /// `work start` on). A row whose status is terminal (`complete`,
    /// `abandoned`) makes a claim of its own, so it must agree with the live
    /// item's semantic state; otherwise the merged view
    /// (`QueuePresentation.effectiveStatus`, where the live state wins)
    /// silently hides the contradiction, as it hid a WI-0061 backlog row
    /// abandoned while the live WI-0061 was complete.
    let contextDisagreements
        (items: BacklogQueueItemRecord list)
        (liveStates: Map<string, LiveWorkState>)
        : BacklogQueueFinding list =
        items
        |> List.choose (fun item ->
            match liveStates |> Map.tryFind item.Id with
            | Some state when terminalStatuses.Contains item.Status && liveStateCode state <> item.Status ->
                Some
                    { Path = queuePath
                      Field = statusAgreementField
                      Message =
                        $"backlog item '{item.Id}' is '{item.Status}' but live work item '{item.Id}' is '{liveStateCode state}'" }
            | _ -> None)

type BacklogReidentificationRequest =
    { Id: string
      NewId: string
      Reason: string option
      /// The backlog row being renamed, if the queue holds one.
      Row: BacklogQueueItemRecord option
      LiveStates: Map<string, LiveWorkState>
      /// Every ID already used anywhere: queue, live context and event
      /// history (the same sets `WorkCapture` never reuses).
      KnownIds: Set<string> }

type BacklogReidentificationPlan =
    { Id: string
      NewId: string
      Reason: string }

[<RequireQualifiedAccess>]
type BacklogReidentificationRejection =
    | InvalidNewId of newId: string
    | ReasonRequired
    | NotInBacklog of id: string
    | NoDisagreement of id: string
    | NewIdInUse of newId: string

[<RequireQualifiedAccess>]
type BacklogReidentificationOutcome =
    | Planned of BacklogReidentificationPlan
    | Rejected of BacklogReidentificationRejection

/// Repairs an ID collision between a backlog row and a live work item that
/// are different obligations (the WI-0061 case): the backlog row alone takes
/// a new, never-used ID. It is allowed only for a row that
/// `BacklogQueueValidation.contextDisagreements` reports, because a row that
/// agrees with its live item (or is still pre-promotion) is the same
/// obligation, and renaming it would split one work item in two.
[<RequireQualifiedAccess>]
module BacklogReidentification =
    let plan (request: BacklogReidentificationRequest) : BacklogReidentificationOutcome =
        let reason =
            request.Reason
            |> Option.map (fun value -> value.Trim())
            |> Option.filter (fun value -> value <> "")

        match request.Row, reason with
        | None, _ -> BacklogReidentificationOutcome.Rejected(BacklogReidentificationRejection.NotInBacklog request.Id)
        | Some _, None -> BacklogReidentificationOutcome.Rejected BacklogReidentificationRejection.ReasonRequired
        | Some row, Some reason ->
            if not (WorkItemId.isValid request.NewId) then
                BacklogReidentificationOutcome.Rejected(BacklogReidentificationRejection.InvalidNewId request.NewId)
            elif request.KnownIds.Contains request.NewId then
                BacklogReidentificationOutcome.Rejected(BacklogReidentificationRejection.NewIdInUse request.NewId)
            elif BacklogQueueValidation.contextDisagreements [ row ] request.LiveStates |> List.isEmpty then
                BacklogReidentificationOutcome.Rejected(BacklogReidentificationRejection.NoDisagreement request.Id)
            else
                BacklogReidentificationOutcome.Planned
                    { Id = request.Id
                      NewId = request.NewId
                      Reason = reason }
