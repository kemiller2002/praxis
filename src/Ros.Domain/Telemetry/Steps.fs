namespace Ros.Domain.Telemetry

open System.Text.RegularExpressions

/// Steps inside an execution (PRAXIS-REMOTE-04, `PRX-REMOTE-003/004/008`):
/// the unit at which work, evidence, and usage can be attributed below an
/// execution. A step is recorded as `step.started` / `step.completed` /
/// `step.failed` events on the execution record, so it inherits the
/// execution's identity and never carries one of its own. The caller
/// chooses the step ID, which makes a retried transition idempotent.
[<RequireQualifiedAccess>]
type StepTransition =
    | Start
    | Complete
    | Fail

[<RequireQualifiedAccess>]
module StepTransition =
    let eventType transition =
        match transition with
        | StepTransition.Start -> "step.started"
        | StepTransition.Complete -> "step.completed"
        | StepTransition.Fail -> "step.failed"

    let tryParseEventType (value: string) =
        match value with
        | "step.started" -> Some StepTransition.Start
        | "step.completed" -> Some StepTransition.Complete
        | "step.failed" -> Some StepTransition.Fail
        | _ -> None

type StepEvent =
    { Transition: StepTransition
      StepId: string
      Name: string option
      Reason: string option
      OccurredAt: string }

[<RequireQualifiedAccess>]
type StepStatus =
    | Running
    | Completed
    | Failed

type Step =
    { StepId: string
      Name: string option
      Status: StepStatus
      StartedAt: string
      EndedAt: string option
      Reason: string option }

[<RequireQualifiedAccess>]
type StepDecision =
    /// Append this event.
    | Record of StepEvent
    /// The identical transition was already recorded; nothing to write.
    | AlreadyRecorded
    | Rejected of reason: string

[<RequireQualifiedAccess>]
module Steps =
    let private stepIdPattern = Regex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z", RegexOptions.CultureInvariant)

    let isValidStepId (value: string) = stepIdPattern.IsMatch value

    /// The steps an execution's events describe, in start order.
    let project (events: StepEvent list) : Step list =
        events
        |> List.fold
            (fun (steps: Step list) event ->
                match event.Transition, steps |> List.tryFind (fun step -> step.StepId = event.StepId) with
                | StepTransition.Start, None ->
                    steps
                    @ [ { StepId = event.StepId
                          Name = event.Name
                          Status = StepStatus.Running
                          StartedAt = event.OccurredAt
                          EndedAt = None
                          Reason = None } ]
                | (StepTransition.Complete | StepTransition.Fail), Some step when step.Status = StepStatus.Running ->
                    let status = if event.Transition = StepTransition.Complete then StepStatus.Completed else StepStatus.Failed

                    steps
                    |> List.map (fun candidate ->
                        if candidate.StepId = step.StepId then
                            { candidate with Status = status; EndedAt = Some event.OccurredAt; Reason = event.Reason }
                        else
                            candidate)
                | _ -> steps)
            []

    /// The legal step lifecycle: `start` a new step; `complete` or `fail` a
    /// running one. Repeating the transition a step already made is an
    /// idempotent no-op; any other transition is refused, never coerced.
    let decide (existing: StepEvent list) (requested: StepEvent) : StepDecision =
        if not (isValidStepId requested.StepId) then
            StepDecision.Rejected $"invalid step ID '{requested.StepId}'"
        else
            match requested.Transition, project existing |> List.tryFind (fun step -> step.StepId = requested.StepId) with
            | StepTransition.Start, None -> StepDecision.Record requested
            | StepTransition.Start, Some _ -> StepDecision.AlreadyRecorded
            | (StepTransition.Complete | StepTransition.Fail), None ->
                StepDecision.Rejected $"step '{requested.StepId}' has not been started in this execution"
            | StepTransition.Complete, Some { Status = StepStatus.Running }
            | StepTransition.Fail, Some { Status = StepStatus.Running } -> StepDecision.Record requested
            | StepTransition.Complete, Some { Status = StepStatus.Completed }
            | StepTransition.Fail, Some { Status = StepStatus.Failed } -> StepDecision.AlreadyRecorded
            | _, Some step ->
                let ended =
                    match step.Status with
                    | StepStatus.Completed -> "completed"
                    | StepStatus.Failed -> "failed"
                    | StepStatus.Running -> "running"

                StepDecision.Rejected $"step '{requested.StepId}' already ended as {ended}"
