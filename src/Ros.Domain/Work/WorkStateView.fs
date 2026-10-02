namespace Ros.Domain.Work

/// Which transition kernel decides an item's next actions: the live work
/// kernel (`WorkTransition`) once the item is in the live context, else the
/// backlog kernel (`BacklogTransition`).
[<RequireQualifiedAccess>]
type GoverningKernel =
    | Live
    | Backlog

/// What the kernel will demand when a legal action is requested.
type ActionRequirements =
    { Reason: bool
      EvidenceTypes: string list }

[<RequireQualifiedAccess>]
type ActionAvailability =
    | Legal of ActionRequirements
    /// The kernel refuses the action in the item's current state.
    | Refused of code: string * message: string

type ActionState =
    { Action: string
      Availability: ActionAvailability }

type RecordedObligation =
    { Code: string
      Description: string
      EvidenceTypes: string list }

type RecordedUnknown = { Code: string; Description: string }

/// A collection Praxis either has a source for (possibly empty) or has none
/// for -- "unavailable" is never rendered as an empty list.
[<RequireQualifiedAccess>]
type Recorded<'T> =
    | Available of sources: string list * items: 'T list
    | Unavailable of reason: string

/// The repository configuration the projection needs: required completion
/// evidence (default and per work type) and whether durable checkpoints are
/// enforced (`FileWorkConfigRepository`).
type WorkStateConfiguration =
    { DefaultEvidence: Set<string>
      EvidenceByType: Map<string, Set<string>>
      RequireDurableCheckpoint: bool }

type WorkItemState =
    { Id: string
      Title: string
      SemanticState: string
      GovernedBy: GoverningKernel
      Backlog: QueueItemDetail option
      Live: LiveWorkItem option
      Actions: ActionState list
      Obligations: Recorded<RecordedObligation>
      Unknowns: Recorded<RecordedUnknown> }

/// The typed read model behind the control-plane work API (PRX-CTL-005,
/// PRX-CTL-011). Identity and status come from `WorkListView.mergedRows`;
/// legality and refusal reasons come from asking the kernels' own `decide`,
/// never from a second transition table.
[<RequireQualifiedAccess>]
module WorkStateView =
    let completionEvidenceSource = "ros.json workProtocol.completionEvidence"
    let durableCheckpointSource = "ros.json workProtocol.continuity.requireDurableCheckpoint"

    let noUnknownSource =
        "Praxis records no per-work-item unknowns; handoff unknowns are supplied per command and not stored"

    let backlogObligationsUnavailable =
        "completion obligations depend on the work type, which is fixed when the item is started"

    let private liveActions = [ WorkAction.Begin; WorkAction.Block; WorkAction.Resume; WorkAction.Complete; WorkAction.Abandon ]
    let private backlogActions = [ BacklogAction.Ready; BacklogAction.Block; BacklogAction.Start; BacklogAction.Abandon ]

    let requiredEvidence (configuration: WorkStateConfiguration) (workType: string) : Set<string> =
        configuration.EvidenceByType |> Map.tryFind workType |> Option.defaultValue configuration.DefaultEvidence

    let private refusedLive id state action =
        let actionText = WorkListView.actionCode action
        let stateText = WorkListView.stateCode state
        ActionAvailability.Refused("illegal-transition", $"cannot {actionText} '{id}' from '{stateText}'")

    /// Probes `WorkTransition.decide` twice: with every input supplied (only an
    /// illegal transition can still be refused), then with none (what the
    /// kernel demands of a request).
    let liveActionState (id: string) (required: Set<string>) (state: LiveWorkState) (action: WorkAction) : ActionState =
        let probe reason provided =
            WorkTransition.decide
                { State = state
                  Action = action
                  BlockReason = reason
                  RequiredEvidence = required
                  ProvidedEvidence = provided }

        let availability =
            match probe (Some "reason") required with
            | TransitionDecision.Rejected _ -> refusedLive id state action
            | TransitionDecision.Allowed _ ->
                match probe None Set.empty with
                | TransitionDecision.Rejected TransitionRejection.BlockReasonRequired
                | TransitionDecision.Rejected TransitionRejection.AbandonReasonRequired -> ActionAvailability.Legal { Reason = true; EvidenceTypes = [] }
                | TransitionDecision.Rejected(TransitionRejection.MissingEvidence missing) ->
                    ActionAvailability.Legal { Reason = false; EvidenceTypes = missing }
                | _ -> ActionAvailability.Legal { Reason = false; EvidenceTypes = [] }

        { Action = WorkListView.actionCode action; Availability = availability }

    /// The same two probes against `BacklogTransition.decide`. `start` is legal
    /// only from `ready`, the gate `work start` enforces.
    let backlogActionState (id: string) (state: BacklogState) (action: BacklogAction) : ActionState =
        let probe reason = BacklogTransition.decide { State = state; Action = action; Reason = reason }
        let actionText = BacklogAction.code action

        let availability =
            match probe (Some "reason") with
            | BacklogTransitionDecision.Rejected _ ->
                ActionAvailability.Refused("illegal-transition", $"cannot {actionText} backlog item '{id}' from '{BacklogState.code state}'")
            | BacklogTransitionDecision.Allowed _ ->
                match probe None with
                | BacklogTransitionDecision.Rejected BacklogTransitionRejection.BlockReasonRequired -> ActionAvailability.Legal { Reason = true; EvidenceTypes = [] }
                | _ -> ActionAvailability.Legal { Reason = false; EvidenceTypes = [] }

        { Action = actionText; Availability = availability }

    let private isTerminal (state: LiveWorkState) =
        state = LiveWorkState.Complete || state = LiveWorkState.Abandoned

    /// Obligations Praxis records for a live item: the completion evidence its
    /// work type requires and, where enforced, a durable checkpoint. Terminal
    /// items owe nothing; backlog-only items have no work type yet.
    let obligations (configuration: WorkStateConfiguration) (live: LiveWorkItem option) : Recorded<RecordedObligation> =
        match live with
        | None -> Recorded.Unavailable backlogObligationsUnavailable
        | Some item when isTerminal item.SemanticState ->
            Recorded.Available([ completionEvidenceSource; durableCheckpointSource ], [])
        | Some item ->
            let evidence = requiredEvidence configuration item.WorkType |> Set.toList

            let evidenceText = String.concat ", " evidence

            let completion =
                { Code = "completion-evidence"
                  Description = $"completing requires evidence of type: {evidenceText}"
                  EvidenceTypes = evidence }

            let checkpoint =
                { Code = "durable-checkpoint"
                  Description = "completing or blocking requires a durable checkpoint whose commit is pushed to the upstream branch"
                  EvidenceTypes = [] }

            let items =
                [ if not evidence.IsEmpty then completion
                  if configuration.RequireDurableCheckpoint then checkpoint ]

            Recorded.Available([ completionEvidenceSource; durableCheckpointSource ], items)

    let private unrecognizedStatus (status: string) =
        { Code = "unrecognized-backlog-status"
          Description = $"the recorded backlog status '{status}' is not one Praxis knows, so its legal actions cannot be determined" }

    let private itemState (configuration: WorkStateConfiguration) (backlog: QueueItemDetail option) (live: LiveWorkItem option) (row: WorkListRow) =
        let governedBy, actions, unknowns =
            match live, backlog with
            | Some item, _ ->
                let required = requiredEvidence configuration item.WorkType
                GoverningKernel.Live, liveActions |> List.map (liveActionState row.Id required item.SemanticState), Recorded.Unavailable noUnknownSource
            | None, Some queued ->
                match BacklogState.parse queued.Status with
                | Some state -> GoverningKernel.Backlog, backlogActions |> List.map (backlogActionState row.Id state), Recorded.Unavailable noUnknownSource
                | None -> GoverningKernel.Backlog, [], Recorded.Available([ ".ros/work/queue.json" ], [ unrecognizedStatus queued.Status ])
            | None, None -> GoverningKernel.Backlog, [], Recorded.Unavailable noUnknownSource

        { Id = row.Id
          Title = row.Title
          SemanticState = row.Status
          GovernedBy = governedBy
          Backlog = backlog
          Live = live
          Actions = actions
          Obligations = obligations configuration live
          Unknowns = unknowns }

    /// Every item `work list` shows, in the same order, with the same status.
    let project
        (configuration: WorkStateConfiguration)
        (queueItems: QueueItemDetail list)
        (contextItems: LiveWorkItem list)
        : WorkItemState list =
        let queueById = queueItems |> List.map (fun item -> item.Id, item) |> Map.ofList
        let contextById = contextItems |> List.map (fun item -> item.Id, item) |> Map.ofList

        WorkListView.mergedRows queueItems contextItems
        |> List.map (fun row -> itemState configuration (queueById.TryFind row.Id) (contextById.TryFind row.Id) row)

    /// `project`, then `work list`'s own tag/status filter over the same rows.
    let projectFiltered configuration queueItems contextItems (tags: string list) (status: string option) : WorkItemState list =
        let kept =
            WorkListView.mergedRows queueItems contextItems
            |> WorkListView.filter tags status
            |> List.map (fun row -> row.Id)
            |> Set.ofList

        project configuration queueItems contextItems |> List.filter (fun item -> kept.Contains item.Id)
