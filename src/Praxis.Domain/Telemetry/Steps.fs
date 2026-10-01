namespace Praxis.Domain.Telemetry

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

/// When an execution's usage and evidence were attributable to steps
/// (DF-ROS-2026-A042, effective-current observability). Step tracking may be
/// adopted partway through an execution: everything recorded before the
/// first step stays execution-scoped, its step attribution is unavailable
/// (never zero, never redistributed), and the execution stays valid. The
/// boundary is derived from the execution's own `step.started` events, so
/// no extra field is stored and no history is rewritten.
[<RequireQualifiedAccess>]
type PreStepPeriod =
    /// Steps were tracked from the execution's start.
    | Absent
    /// Activity from the execution's start until adoption is execution-scoped.
    | ExecutionScopedFrom of executionStartedAt: string
    /// The execution's start is unknown, so whether activity preceded
    /// adoption cannot be known.
    | Unknown

[<RequireQualifiedAccess>]
type TelemetrySegmentation =
    /// No step was ever recorded: all telemetry is execution-scoped and step
    /// attribution was not captured.
    | ExecutionLevel
    /// Step tracking began at `stepTrackingStartedAt`.
    | StepLevel of stepTrackingStartedAt: string * before: PreStepPeriod

/// Where one measurement's usage belongs relative to step adoption.
[<RequireQualifiedAccess>]
type MeasurementScope =
    | Step of stepId: string
    /// Execution-scoped, recorded before step tracking was adopted (or in an
    /// execution that never adopted it): step attribution unavailable.
    | ExecutionBeforeSteps
    /// Execution-scoped, recorded after adoption but outside any step.
    | ExecutionOutsideSteps

[<RequireQualifiedAccess>]
module TelemetrySegmentation =
    let private instant (value: string) =
        match System.DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind) with
        | true, parsed -> Some parsed
        | _ -> Option.None

    /// The earliest recorded step start, by instant where every start
    /// parses, otherwise the first in record order.
    let private boundary (steps: Step list) =
        match steps |> List.map (fun step -> step.StartedAt, instant step.StartedAt) with
        | [] -> Option.None
        | starts when starts |> List.forall (snd >> Option.isSome) -> starts |> List.minBy (snd >> Option.get) |> fst |> Some
        | (first, _) :: _ -> Some first

    let derive (executionStartedAt: string option) (steps: Step list) : TelemetrySegmentation =
        match boundary steps with
        | Option.None -> TelemetrySegmentation.ExecutionLevel
        | Some adoptedAt ->
            let before =
                match executionStartedAt |> Option.bind instant, instant adoptedAt with
                | Some started, Some adopted when started < adopted -> PreStepPeriod.ExecutionScopedFrom executionStartedAt.Value
                | Some _, Some _ -> PreStepPeriod.Absent
                | _ -> PreStepPeriod.Unknown

            TelemetrySegmentation.StepLevel(adoptedAt, before)

    let code segmentation =
        match segmentation with
        | TelemetrySegmentation.ExecutionLevel -> "execution-level"
        | TelemetrySegmentation.StepLevel(_, PreStepPeriod.Absent) -> "step-level"
        | TelemetrySegmentation.StepLevel _ -> "step-level-adopted"

    let stepTrackingStartedAt segmentation =
        match segmentation with
        | TelemetrySegmentation.ExecutionLevel -> Option.None
        | TelemetrySegmentation.StepLevel(adoptedAt, _) -> Some adoptedAt

    /// Whether some of the execution's activity is execution-scoped with no
    /// step attribution: its usage there is unknown per step, never zero.
    let hasExecutionScopedPeriod segmentation =
        match segmentation with
        | TelemetrySegmentation.ExecutionLevel
        | TelemetrySegmentation.StepLevel(_, PreStepPeriod.ExecutionScopedFrom _)
        | TelemetrySegmentation.StepLevel(_, PreStepPeriod.Unknown) -> true
        | TelemetrySegmentation.StepLevel(_, PreStepPeriod.Absent) -> false

    /// Classifies a measurement without ever moving it into a step it was
    /// not recorded against.
    let scopeOf segmentation (step: string option) (collectedAt: string) : MeasurementScope =
        match step, segmentation with
        | Some stepId, _ -> MeasurementScope.Step stepId
        | Option.None, TelemetrySegmentation.ExecutionLevel -> MeasurementScope.ExecutionBeforeSteps
        | Option.None, TelemetrySegmentation.StepLevel(adoptedAt, _) ->
            match instant collectedAt, instant adoptedAt with
            | Some collected, Some adopted when collected >= adopted -> MeasurementScope.ExecutionOutsideSteps
            | _ -> MeasurementScope.ExecutionBeforeSteps

    /// Offline validation: every step-scoped measurement names a step its own
    /// execution started. A measurement without a step is legal whatever the
    /// execution's segmentation, so adopting steps never invalidates history.
    /// Returns the (index, step) of each dangling reference.
    let danglingStepReferences (steps: Step list) (measurementSteps: string option list) : (int * string) list =
        let started = steps |> List.map _.StepId |> Set.ofList

        measurementSteps
        |> List.indexed
        |> List.choose (fun (index, step) ->
            match step with
            | Some stepId when not (started.Contains stepId) -> Some(index, stepId)
            | _ -> Option.None)
