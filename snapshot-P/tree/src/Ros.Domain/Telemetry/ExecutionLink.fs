namespace Ros.Domain.Telemetry

[<RequireQualifiedAccess>]
type ExecutionStatus =
    | Active
    | Finalized

type ExecutionLinkCandidate =
    { ExecutionId: string
      WorkItemId: string
      Status: ExecutionStatus }

type ExecutionLinkRequest =
    { WorkItemId: string
      LinkedExecutionIds: Set<string>
      RecoverableStatuses: Set<ExecutionStatus>
      RequestedExecutionId: string option
      Candidates: ExecutionLinkCandidate list }

[<RequireQualifiedAccess>]
type ExecutionLinkDecision =
    | StartNew
    | Recover of executionId: string
    | RejectDetachedConflict of executionIds: string list
    | RejectAmbiguous of executionIds: string list

[<RequireQualifiedAccess>]
module ExecutionLinkRecovery =
    let decide request =
        let recoverable =
            request.Candidates
            |> List.filter (fun candidate ->
                candidate.WorkItemId = request.WorkItemId
                && request.RecoverableStatuses.Contains candidate.Status
                && not (request.LinkedExecutionIds.Contains candidate.ExecutionId))
            |> List.map _.ExecutionId
            |> List.distinct
            |> List.sort

        match request.RequestedExecutionId with
        | Some requested when recoverable |> List.contains requested -> ExecutionLinkDecision.Recover requested
        | Some _ when not recoverable.IsEmpty -> ExecutionLinkDecision.RejectDetachedConflict recoverable
        | Some _ -> ExecutionLinkDecision.StartNew
        | None ->
            match recoverable with
            | [] -> ExecutionLinkDecision.StartNew
            | [ executionId ] -> ExecutionLinkDecision.Recover executionId
            | executionIds -> ExecutionLinkDecision.RejectAmbiguous executionIds
