namespace Ros.Domain.Work

[<RequireQualifiedAccess>]
type LiveWorkState =
    | Ready
    | Active
    | Blocked
    | Complete

[<RequireQualifiedAccess>]
type WorkAction =
    | Begin
    | Block
    | Resume
    | Complete

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
    | MissingEvidence of string list

[<RequireQualifiedAccess>]
type TransitionDecision =
    | Allowed of LiveWorkState
    | Rejected of TransitionRejection

[<RequireQualifiedAccess>]
module WorkTransition =
    let allowedActions state =
        match state with
        | LiveWorkState.Ready -> [ WorkAction.Begin; WorkAction.Block ]
        | LiveWorkState.Active -> [ WorkAction.Block; WorkAction.Complete ]
        | LiveWorkState.Blocked -> [ WorkAction.Resume ]
        | LiveWorkState.Complete -> []

    let private target state action =
        match state, action with
        | LiveWorkState.Ready, WorkAction.Begin -> Some LiveWorkState.Active
        | LiveWorkState.Ready, WorkAction.Block -> Some LiveWorkState.Blocked
        | LiveWorkState.Active, WorkAction.Block -> Some LiveWorkState.Blocked
        | LiveWorkState.Active, WorkAction.Complete -> Some LiveWorkState.Complete
        | LiveWorkState.Blocked, WorkAction.Resume -> Some LiveWorkState.Active
        | _ -> None

    let decide request =
        match target request.State request.Action with
        | None ->
            TransitionRejection.IllegalTransition(request.State, request.Action)
            |> TransitionDecision.Rejected
        | Some _ when request.Action = WorkAction.Block && request.BlockReason |> Option.forall System.String.IsNullOrEmpty ->
            TransitionRejection.BlockReasonRequired |> TransitionDecision.Rejected
        | Some _ when request.Action = WorkAction.Complete ->
            let missing = Set.difference request.RequiredEvidence request.ProvidedEvidence |> Set.toList

            if missing.IsEmpty then
                TransitionDecision.Allowed LiveWorkState.Complete
            else
                TransitionRejection.MissingEvidence missing |> TransitionDecision.Rejected
        | Some next -> TransitionDecision.Allowed next
