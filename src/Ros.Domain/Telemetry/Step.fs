namespace Ros.Domain.Telemetry

open System

[<RequireQualifiedAccess>]
type StepStatus =
    | Planned
    | Active
    | Completed
    | Blocked
    | Abandoned

[<RequireQualifiedAccess>]
module StepStatus =
    let code status =
        match status with
        | StepStatus.Planned -> "planned"
        | StepStatus.Active -> "active"
        | StepStatus.Completed -> "completed"
        | StepStatus.Blocked -> "blocked"
        | StepStatus.Abandoned -> "abandoned"

    let tryParse value =
        match value with
        | "planned" -> Some StepStatus.Planned
        | "active" -> Some StepStatus.Active
        | "completed" -> Some StepStatus.Completed
        | "blocked" -> Some StepStatus.Blocked
        | "abandoned" -> Some StepStatus.Abandoned
        | _ -> None

    let isTerminal status = status = StepStatus.Completed || status = StepStatus.Abandoned
    let isUnresolved status = not (isTerminal status)

type StepState =
    { StepId: string
      Sequence: int
      ParentStepId: string option
      Status: StepStatus
      PlannedAt: string
      StartedAt: string option
      CompletedAt: string option
      EndedAt: string option }

[<RequireQualifiedAccess>]
module Step =
    let private parseTimestamp (field: string) (value: string) =
        match DateTimeOffset.TryParse value with
        | true, parsed -> Ok parsed
        | _ -> Error $"{field} '{value}' is not a valid timestamp"

    let private atOrAfter field earlier later =
        match parseTimestamp "timestamp" earlier, parseTimestamp field later with
        | Ok start, Ok finish when finish >= start -> Ok()
        | Ok _, Ok _ -> Error $"{field} must not be before {earlier}"
        | Error message, _
        | _, Error message -> Error message

    let legalTransition current target =
        match current, target with
        | StepStatus.Planned, StepStatus.Active
        | StepStatus.Planned, StepStatus.Abandoned
        | StepStatus.Active, StepStatus.Completed
        | StepStatus.Active, StepStatus.Blocked
        | StepStatus.Active, StepStatus.Abandoned
        | StepStatus.Blocked, StepStatus.Active
        | StepStatus.Blocked, StepStatus.Abandoned -> true
        | _ -> false

    let private byId (steps: StepState list) = steps |> List.map (fun step -> step.StepId, step) |> Map.ofList

    let descendants (steps: StepState list) stepId =
        let children = steps |> List.groupBy _.ParentStepId |> Map.ofList

        let rec collect parent =
            children
            |> Map.tryFind (Some parent)
            |> Option.defaultValue []
            |> List.collect (fun child -> child :: collect child.StepId)

        collect stepId

    let activeLeaf (steps: StepState list) =
        let active = steps |> List.filter (fun step -> step.Status = StepStatus.Active)

        active
        |> List.tryFind (fun candidate ->
            active |> List.exists (fun step -> step.ParentStepId = Some candidate.StepId) |> not)

    let validateNew executionStartedAt occurredAt parentStepId status (steps: StepState list) =
        match atOrAfter "step timestamp" executionStartedAt occurredAt with
        | Error message -> Error message
        | Ok() when steps |> List.exists (fun step -> step.Status = StepStatus.Blocked) ->
            Error "a blocked step must be resumed or abandoned before another step can begin"
        | Ok() ->
            let index = byId steps

            match parentStepId, status with
            | Some parentId, _ when not (index.ContainsKey parentId) -> Error $"parent step '{parentId}' was not found in this execution"
            | Some parentId, StepStatus.Active ->
                let parent = index[parentId]

                if parent.Status <> StepStatus.Active then
                    Error $"parent step '{parentId}' is not active"
                elif activeLeaf steps |> Option.map _.StepId <> Some parentId then
                    Error $"parent step '{parentId}' is not the active leaf"
                else Ok()
            | None, StepStatus.Active when (activeLeaf steps).IsSome ->
                Error "an unrelated step is already active; use --parent to begin a nested child"
            | Some parentId, StepStatus.Planned when StepStatus.isTerminal index[parentId].Status ->
                Error $"parent step '{parentId}' is already terminal"
            | _ -> Ok()

    let transition occurredAt target (step: StepState) (steps: StepState list) =
        if not (legalTransition step.Status target) then
            Error $"step '{step.StepId}' cannot transition from {StepStatus.code step.Status} to {StepStatus.code target}"
        else
            let unresolvedDescendants =
                descendants steps step.StepId |> List.filter (fun child -> StepStatus.isUnresolved child.Status)

            if (target = StepStatus.Completed || target = StepStatus.Blocked || target = StepStatus.Abandoned) && not unresolvedDescendants.IsEmpty then
                Error $"step '{step.StepId}' has unresolved descendant '{unresolvedDescendants.Head.StepId}'"
            else
                let lowerBound = step.StartedAt |> Option.defaultValue step.PlannedAt

                match atOrAfter "transition timestamp" lowerBound occurredAt with
                | Error message -> Error message
                | Ok() when target = StepStatus.Active ->
                    match step.ParentStepId, activeLeaf (steps |> List.filter (fun item -> item.StepId <> step.StepId)) with
                    | None, Some other -> Error $"step '{other.StepId}' is already active"
                    | Some parent, Some leaf when leaf.StepId <> parent -> Error $"step '{leaf.StepId}' is already the active leaf"
                    | Some _, None -> Error "a nested step cannot become active while its parent path is inactive"
                    | _ -> Ok()
                | Ok() -> Ok()

    let unresolved (steps: StepState list) = steps |> List.filter (fun step -> StepStatus.isUnresolved step.Status)

    let validateStored executionStartedAt (steps: StepState list) =
        let mutable findings = []
        let ids = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let sequences = System.Collections.Generic.HashSet<int>()
        let index = byId steps

        for step in steps do
            if not (ids.Add step.StepId) then findings <- $"duplicate step id '{step.StepId}'" :: findings
            if step.Sequence < 1 || not (sequences.Add step.Sequence) then findings <- $"invalid or duplicate step sequence {step.Sequence}" :: findings
            match step.ParentStepId with
            | Some parent when parent = step.StepId -> findings <- $"step '{step.StepId}' cannot parent itself" :: findings
            | Some parent when not (index.ContainsKey parent) -> findings <- $"step '{step.StepId}' has unknown parent '{parent}'" :: findings
            | Some parent when index[parent].Sequence >= step.Sequence -> findings <- $"step '{step.StepId}' must follow its parent '{parent}'" :: findings
            | _ -> ()
            match atOrAfter "plannedAt" executionStartedAt step.PlannedAt with
            | Error message -> findings <- $"step '{step.StepId}': {message}" :: findings
            | Ok() -> ()
            match step.StartedAt with
            | Some startedAt ->
                match atOrAfter "startedAt" step.PlannedAt startedAt with
                | Error message -> findings <- $"step '{step.StepId}': {message}" :: findings
                | Ok() -> ()
            | None when step.Status <> StepStatus.Planned && step.Status <> StepStatus.Abandoned -> findings <- $"step '{step.StepId}' status requires startedAt" :: findings
            | None -> ()
            match step.EndedAt, step.Status with
            | Some endedAt, status when StepStatus.isTerminal status ->
                match atOrAfter "endedAt" (step.StartedAt |> Option.defaultValue step.PlannedAt) endedAt with
                | Error message -> findings <- $"step '{step.StepId}': {message}" :: findings
                | Ok() -> ()
            | None, status when StepStatus.isTerminal status -> findings <- $"terminal step '{step.StepId}' requires endedAt" :: findings
            | Some _, _ -> findings <- $"unresolved step '{step.StepId}' must not have endedAt" :: findings
            | _ -> ()

            match step.CompletedAt, step.Status with
            | Some completedAt, StepStatus.Completed ->
                match atOrAfter "completedAt" (step.StartedAt |> Option.defaultValue step.PlannedAt) completedAt with
                | Error message -> findings <- $"step '{step.StepId}': {message}" :: findings
                | Ok() ->
                    match step.EndedAt with
                    | Some endedAt when endedAt <> completedAt -> findings <- $"completed step '{step.StepId}' must have matching completedAt and endedAt" :: findings
                    | _ -> ()
            | None, StepStatus.Completed -> findings <- $"completed step '{step.StepId}' requires completedAt" :: findings
            | Some _, _ -> findings <- $"step '{step.StepId}' may only have completedAt when completed" :: findings
            | _ -> ()

        let active = steps |> List.filter (fun step -> step.Status = StepStatus.Active)
        let activeIds = active |> List.map _.StepId |> Set.ofList

        for step in active do
            match step.ParentStepId with
            | Some parent when not (activeIds.Contains parent) -> findings <- $"active step '{step.StepId}' has inactive parent '{parent}'" :: findings
            | _ -> ()

        let leaves =
            active
            |> List.filter (fun candidate -> active |> List.exists (fun step -> step.ParentStepId = Some candidate.StepId) |> not)

        if leaves.Length > 1 then findings <- "active steps do not form one ancestor path" :: findings
        findings |> List.rev
