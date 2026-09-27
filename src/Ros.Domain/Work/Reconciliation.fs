namespace Ros.Domain.Work

open System
open System.Text.RegularExpressions
open Ros.Domain.Telemetry

type EnvelopeActor = {
    ActorKind: string
    ActorId: string
    Provider: string option
    Model: string option
    Runtime: string option
}

type EnvelopeTimelineEntry = {
    Sequence: int
    Timestamp: DateTimeOffset
    Action: string
}

type EnvelopeRequest = {
    RequestType: string
}

type EnvelopeStepTransition = {
    Sequence: int
    FromStatus: string option
    ToStatus: string
    Timestamp: DateTimeOffset
}

type EnvelopeStepMeasurement = {
    MeasurementId: string
    MetricId: string
    Value: float option
    Unit: string option
    Currency: string option
    Quality: string option
    Availability: string
    RawJson: string
}

type EnvelopeStep = {
    StepId: string
    ExecutionId: string
    WorkItemId: string
    Sequence: int
    ParentStepId: string option
    Name: string
    Description: string option
    Classifications: string list
    Status: string
    PlannedAt: DateTimeOffset
    StartedAt: DateTimeOffset option
    CompletedAt: DateTimeOffset option
    EndedAt: DateTimeOffset option
    Actor: EnvelopeActor
    Transitions: EnvelopeStepTransition list
    Measurements: EnvelopeStepMeasurement list
    RawTelemetry: string list
    Evidence: string list
}

type EnvelopeExecution = {
    ExecutionId: string
    StartedAt: DateTimeOffset
    Steps: EnvelopeStep list
}

type ReconciliationEnvelope = {
    SchemaVersion: string
    TransactionId: string
    WorkItem: string
    Branch: string
    BaseCommit: string
    PraxisInstanceId: string option
    Agent: EnvelopeActor
    Execution: EnvelopeExecution option
    Timeline: EnvelopeTimelineEntry list
    Requests: EnvelopeRequest list
}

type ReconciliationObservation = {
    ActualBranch: string
    HeadCommit: string
    BaseCommitExists: bool
    TransactionAlreadyApplied: bool
    LocalPraxisInstanceId: string option
}

type ReconciliationFinding =
    | UnsupportedSchemaVersion of string
    | MissingTransactionId
    | MissingAgentIdentity
    | InvalidWorkItemId of string
    | WorkItemBranchMismatch of workItem: string * claimedBranch: string
    | ObservedBranchMismatch of claimedBranch: string * actualBranch: string
    | InvalidBaseCommit of string
    | BaseCommitUnavailable of string
    | EmptyRequests
    | InvalidTimelineSequence
    | DuplicateTransaction of string
    | InstanceIdentityMismatch of claimed: string * local: string
    | InvalidStepStructure of string

type ReconciliationDecision =
    | Accept
    | AlreadyApplied
    | Reject of ReconciliationFinding list

module Reconciliation =
    let private sha = Regex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)
    let private workItem = Regex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)

    let decide (envelope: ReconciliationEnvelope) (observed: ReconciliationObservation) =
        if observed.TransactionAlreadyApplied then AlreadyApplied
        else
            let findings = [
                if envelope.SchemaVersion <> "1.0" then UnsupportedSchemaVersion envelope.SchemaVersion
                if String.IsNullOrWhiteSpace envelope.TransactionId then MissingTransactionId
                if String.IsNullOrWhiteSpace envelope.Agent.ActorKind || String.IsNullOrWhiteSpace envelope.Agent.ActorId then MissingAgentIdentity
                if not (workItem.IsMatch envelope.WorkItem) then InvalidWorkItemId envelope.WorkItem
                if envelope.WorkItem <> envelope.Branch then WorkItemBranchMismatch(envelope.WorkItem, envelope.Branch)
                if envelope.Branch <> observed.ActualBranch then ObservedBranchMismatch(envelope.Branch, observed.ActualBranch)
                if not (sha.IsMatch envelope.BaseCommit) then InvalidBaseCommit envelope.BaseCommit
                elif not observed.BaseCommitExists then BaseCommitUnavailable envelope.BaseCommit
                if envelope.Requests.IsEmpty then EmptyRequests
                match envelope.PraxisInstanceId, observed.LocalPraxisInstanceId with
                | Some claimed, Some local when claimed <> local -> InstanceIdentityMismatch(claimed, local)
                | _ -> ()
                let expected = [1 .. envelope.Timeline.Length]
                let actual = envelope.Timeline |> List.map _.Sequence
                if actual <> expected then InvalidTimelineSequence
                match envelope.Execution with
                | Some execution ->
                    let ids = execution.Steps |> List.map _.StepId
                    if (ids |> List.distinct).Length <> ids.Length then InvalidStepStructure "duplicate-step-id"
                    let expectedSteps = [1 .. execution.Steps.Length]
                    let actualSteps = execution.Steps |> List.map _.Sequence
                    if actualSteps <> expectedSteps then InvalidStepStructure "invalid-step-sequence"
                    let stepStates =
                        execution.Steps
                        |> List.choose (fun step ->
                            StepStatus.tryParse step.Status
                            |> Option.map (fun status ->
                                { StepId = step.StepId
                                  Sequence = step.Sequence
                                  ParentStepId = step.ParentStepId
                                  Status = status
                                  PlannedAt = step.PlannedAt.ToString("O")
                                  StartedAt = step.StartedAt |> Option.map _.ToString("O")
                                  CompletedAt = step.CompletedAt |> Option.map _.ToString("O")
                                  EndedAt = step.EndedAt |> Option.map _.ToString("O") }))
                    for problem in Step.validateStored (execution.StartedAt.ToString("O")) stepStates do
                        InvalidStepStructure problem
                    let measurementIds = execution.Steps |> List.collect _.Measurements |> List.map _.MeasurementId
                    if (measurementIds |> List.distinct).Length <> measurementIds.Length then InvalidStepStructure "duplicate-measurement-id"
                    for step in execution.Steps do
                        if step.ExecutionId <> execution.ExecutionId then InvalidStepStructure $"{step.StepId}:execution-id-mismatch"
                        if step.WorkItemId <> envelope.WorkItem then InvalidStepStructure $"{step.StepId}:work-item-id-mismatch"
                        if step.Actor.ActorKind <> envelope.Agent.ActorKind || step.Actor.ActorId <> envelope.Agent.ActorId then
                            InvalidStepStructure $"{step.StepId}:actor-mismatch"
                        match StepStatus.tryParse step.Status with
                        | None -> InvalidStepStructure $"{step.StepId}:unknown-status"
                        | Some _ -> ()
                        for measurement in step.Measurements do
                            let valueExpected = Set.ofList [ "measured"; "reported"; "calculated"; "estimated" ]
                            let noValueExpected = Set.ofList [ "unavailable"; "unsupported"; "unknown" ]
                            if valueExpected.Contains measurement.Availability && measurement.Value.IsNone then
                                InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:value-required"
                            elif noValueExpected.Contains measurement.Availability && measurement.Value.IsSome then
                                InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:value-forbidden"
                            elif not (valueExpected.Contains measurement.Availability || noValueExpected.Contains measurement.Availability) then
                                InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:unknown-availability"
                        let transitionExpected = [1 .. step.Transitions.Length]
                        if step.Transitions |> List.map _.Sequence <> transitionExpected then InvalidStepStructure $"{step.StepId}:invalid-transition-sequence"
                        match step.Transitions with
                        | [] -> InvalidStepStructure $"{step.StepId}:missing-transitions"
                        | transitions ->
                            let mutable priorStatus: string option = None
                            let mutable priorTimestamp = step.PlannedAt

                            for index, transition in transitions |> List.indexed do
                                if index = 0 && transition.FromStatus.IsSome then
                                    InvalidStepStructure $"{step.StepId}:initial-transition-from-status"
                                elif index > 0 && transition.FromStatus <> priorStatus then
                                    InvalidStepStructure $"{step.StepId}:transition-status-discontinuity"

                                if index = 0 && transition.ToStatus <> "planned" && transition.ToStatus <> "active" then
                                    InvalidStepStructure $"{step.StepId}:invalid-initial-transition"
                                elif index > 0 then
                                    match priorStatus |> Option.bind StepStatus.tryParse, StepStatus.tryParse transition.ToStatus with
                                    | Some previous, Some target when Step.legalTransition previous target -> ()
                                    | _ -> InvalidStepStructure $"{step.StepId}:illegal-transition"

                                if transition.Timestamp < step.PlannedAt || (index > 0 && transition.Timestamp < priorTimestamp) then
                                    InvalidStepStructure $"{step.StepId}:transition-time-regression"

                                priorStatus <- Some transition.ToStatus
                                priorTimestamp <- transition.Timestamp

                            if priorStatus <> Some step.Status then
                                InvalidStepStructure $"{step.StepId}:terminal-status-mismatch"
                | None -> ()
            ]
            if findings.IsEmpty then Accept else Reject findings

    let findingCode = function
        | UnsupportedSchemaVersion _ -> "unsupported-schema-version"
        | MissingTransactionId -> "missing-transaction-id"
        | MissingAgentIdentity -> "missing-agent-identity"
        | InvalidWorkItemId _ -> "invalid-work-item-id"
        | WorkItemBranchMismatch _ -> "work-item-branch-mismatch"
        | ObservedBranchMismatch _ -> "observed-branch-mismatch"
        | InvalidBaseCommit _ -> "invalid-base-commit"
        | BaseCommitUnavailable _ -> "base-commit-unavailable"
        | EmptyRequests -> "empty-requests"
        | InvalidTimelineSequence -> "invalid-timeline-sequence"
        | DuplicateTransaction _ -> "duplicate-transaction"
        | InstanceIdentityMismatch _ -> "instance-identity-mismatch"
        | InvalidStepStructure detail -> "invalid-step-structure:" + detail
