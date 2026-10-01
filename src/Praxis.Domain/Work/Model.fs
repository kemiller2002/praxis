namespace Praxis.Domain.Work

[<RequireQualifiedAccess>]
type LiveWorkState =
    | Ready
    | Active
    | Blocked
    | Complete
    /// Terminal: the owner cancelled the work. Nothing was delivered, so
    /// nothing is claimed.
    | Abandoned

[<RequireQualifiedAccess>]
type WorkAction =
    | Begin
    | Block
    | Resume
    | Complete
    | Abandon

type TransitionRequest =
    { State: LiveWorkState
      Action: WorkAction
      BlockReason: string option
      RequiredEvidence: Set<string>
      ProvidedEvidence: Set<string> }

[<RequireQualifiedAccess>]
type TransitionRejection =
    | IllegalTransition of state: LiveWorkState * action: WorkAction
    | BlockReasonRequired
    | AbandonReasonRequired
    | MissingEvidence of string list

[<RequireQualifiedAccess>]
type TransitionDecision =
    | Allowed of LiveWorkState
    | Rejected of TransitionRejection

[<RequireQualifiedAccess>]
module WorkTransition =
    let allowedActions state =
        match state with
        | LiveWorkState.Ready -> [ WorkAction.Begin; WorkAction.Block; WorkAction.Abandon ]
        | LiveWorkState.Active -> [ WorkAction.Block; WorkAction.Complete; WorkAction.Abandon ]
        | LiveWorkState.Blocked -> [ WorkAction.Resume; WorkAction.Abandon ]
        | LiveWorkState.Complete
        | LiveWorkState.Abandoned -> []

    let private target state action =
        match state, action with
        | LiveWorkState.Ready, WorkAction.Begin -> Some LiveWorkState.Active
        | LiveWorkState.Ready, WorkAction.Block -> Some LiveWorkState.Blocked
        | LiveWorkState.Active, WorkAction.Block -> Some LiveWorkState.Blocked
        | LiveWorkState.Active, WorkAction.Complete -> Some LiveWorkState.Complete
        | LiveWorkState.Blocked, WorkAction.Resume -> Some LiveWorkState.Active
        | (LiveWorkState.Ready | LiveWorkState.Active | LiveWorkState.Blocked), WorkAction.Abandon -> Some LiveWorkState.Abandoned
        | _ -> None

    let decide request =
        match target request.State request.Action with
        | None ->
            TransitionRejection.IllegalTransition(request.State, request.Action)
            |> TransitionDecision.Rejected
        | Some _ when request.Action = WorkAction.Block && request.BlockReason |> Option.forall System.String.IsNullOrEmpty ->
            TransitionRejection.BlockReasonRequired |> TransitionDecision.Rejected
        // The reason travels in BlockReason: it is the one free-text field a transition carries.
        | Some _ when request.Action = WorkAction.Abandon && request.BlockReason |> Option.forall System.String.IsNullOrWhiteSpace ->
            TransitionRejection.AbandonReasonRequired |> TransitionDecision.Rejected
        | Some _ when request.Action = WorkAction.Complete ->
            let missing = Set.difference request.RequiredEvidence request.ProvidedEvidence |> Set.toList

            if missing.IsEmpty then
                TransitionDecision.Allowed LiveWorkState.Complete
            else
                TransitionRejection.MissingEvidence missing |> TransitionDecision.Rejected
        | Some next -> TransitionDecision.Allowed next
