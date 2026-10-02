namespace Ros.Domain.Work

open System
open System.Text.Json
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
    OccurredAt: DateTimeOffset option
    WorkType: string option
    Reason: string option
    Conclusion: string option
    Evidence: WorkEvidence list
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

type EnvelopeReconciliationInput = {
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

type EnvelopeReconciliationObservation = {
    ActualBranch: string
    HeadCommit: string
    BaseCommitExists: bool
    BaseCommitIsAncestor: bool
    TransactionAlreadyApplied: bool
    TransactionReplayMatches: bool
    LocalPraxisInstanceId: string option
}

type EnvelopeReconciliationFinding =
    | UnsupportedSchemaVersion of string
    | MissingTransactionId
    | InvalidTransactionId of string
    | MissingAgentIdentity
    | InvalidWorkItemId of string
    | WorkItemBranchMismatch of workItem: string * claimedBranch: string
    | ObservedBranchMismatch of claimedBranch: string * actualBranch: string
    | InvalidBaseCommit of string
    | BaseCommitUnavailable of string
    | BaseCommitNotAncestor of baseCommit: string * headCommit: string
    | EmptyRequests
    | InvalidTimelineSequence
    | InvalidTimelineOrder
    | TimelineRequestMismatch
    | UnsupportedRequestType of string
    | DuplicateTransaction of string
    | InstanceIdentityMismatch of claimed: string * local: string
    | InvalidStepStructure of string

type EnvelopeReconciliationDecision =
    | Accept
    | AlreadyApplied
    | Reject of EnvelopeReconciliationFinding list

module EnvelopeReconciliation =
    let private sha = Regex(@"^[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant)
    let private workItem = Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)
    let private transaction = Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)
    let private currency = Regex(@"^[A-Z]{3}\z", RegexOptions.CultureInvariant)

    let decide (envelope: EnvelopeReconciliationInput) (observed: EnvelopeReconciliationObservation) =
        if observed.TransactionAlreadyApplied && not observed.TransactionReplayMatches then
            Reject [ DuplicateTransaction envelope.TransactionId ]
        elif observed.TransactionAlreadyApplied then AlreadyApplied
        else
            let findings = [
                if envelope.SchemaVersion <> "1.0" then UnsupportedSchemaVersion envelope.SchemaVersion
                if String.IsNullOrWhiteSpace envelope.TransactionId then MissingTransactionId
                elif not (transaction.IsMatch envelope.TransactionId) then InvalidTransactionId envelope.TransactionId
                if String.IsNullOrWhiteSpace envelope.Agent.ActorKind || String.IsNullOrWhiteSpace envelope.Agent.ActorId then MissingAgentIdentity
                if not (workItem.IsMatch envelope.WorkItem) then InvalidWorkItemId envelope.WorkItem
                if envelope.WorkItem <> envelope.Branch then WorkItemBranchMismatch(envelope.WorkItem, envelope.Branch)
                if envelope.Branch <> observed.ActualBranch then ObservedBranchMismatch(envelope.Branch, observed.ActualBranch)
                if not (sha.IsMatch envelope.BaseCommit) then InvalidBaseCommit envelope.BaseCommit
                elif not observed.BaseCommitExists then BaseCommitUnavailable envelope.BaseCommit
                elif not observed.BaseCommitIsAncestor then BaseCommitNotAncestor(envelope.BaseCommit, observed.HeadCommit)
                if envelope.Requests.IsEmpty then EmptyRequests
                match envelope.PraxisInstanceId, observed.LocalPraxisInstanceId with
                | Some claimed, Some local when claimed <> local -> InstanceIdentityMismatch(claimed, local)
                | _ -> ()
                let expected = [1 .. envelope.Timeline.Length]
                let actual = envelope.Timeline |> List.map _.Sequence
                if actual <> expected then InvalidTimelineSequence
                if envelope.Timeline |> List.pairwise |> List.exists (fun (left, right) -> right.Timestamp < left.Timestamp) then
                    InvalidTimelineOrder
                if envelope.Requests.Length <> envelope.Timeline.Length then TimelineRequestMismatch
                let supportedRequests = Set.ofList [ "work.start"; "work.begin"; "work.block"; "work.resume"; "work.complete" ]
                for request in envelope.Requests do
                    if not (supportedRequests.Contains request.RequestType) then UnsupportedRequestType request.RequestType
                let pairedCount = min envelope.Requests.Length envelope.Timeline.Length
                for request, timeline in List.zip (List.truncate pairedCount envelope.Requests) (List.truncate pairedCount envelope.Timeline) do
                    match request.OccurredAt with
                    | Some timestamp when timestamp <> timeline.Timestamp -> TimelineRequestMismatch
                    | _ -> ()
                    let expectedAction =
                        match request.RequestType with
                        | "work.start"
                        | "work.begin" -> "start"
                        | "work.block" -> "block"
                        | "work.resume" -> "resume"
                        | "work.complete" -> "complete"
                        | other -> other
                    let actualAction =
                        if timeline.Action.StartsWith("work.", StringComparison.Ordinal) then timeline.Action.Substring(5)
                        elif timeline.Action = "begin" then "start"
                        else timeline.Action
                    if actualAction <> expectedAction then TimelineRequestMismatch
                match envelope.Execution with
                | Some execution ->
                    if envelope.Requests |> List.exists (fun request -> request.RequestType = "work.complete") then
                        let terminal step = step.Status = "completed" || step.Status = "abandoned"
                        if execution.Steps.IsEmpty || execution.Steps |> List.exists (terminal >> not) then
                            InvalidStepStructure "completed-work-requires-terminal-execution"
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
                    let parentIds = execution.Steps |> List.choose _.ParentStepId |> Set.ofList
                    for step in execution.Steps do
                        if step.ExecutionId <> execution.ExecutionId then InvalidStepStructure $"{step.StepId}:execution-id-mismatch"
                        if step.WorkItemId <> envelope.WorkItem then InvalidStepStructure $"{step.StepId}:work-item-id-mismatch"
                        if step.Actor <> envelope.Agent then
                            InvalidStepStructure $"{step.StepId}:actor-mismatch"
                        if parentIds.Contains step.StepId && (not step.Measurements.IsEmpty || not step.RawTelemetry.IsEmpty) then
                            InvalidStepStructure $"{step.StepId}:grouping-parent-carries-telemetry"
                        match StepStatus.tryParse step.Status with
                        | None -> InvalidStepStructure $"{step.StepId}:unknown-status"
                        | Some _ -> ()
                        for measurement in step.Measurements do
                            let valueExpected = Set.ofList [ "measured"; "reported"; "calculated"; "estimated" ]
                            let noValueExpected = Set.ofList [ "unavailable"; "unsupported"; "unknown" ]
                            if valueExpected.Contains measurement.Availability && measurement.Value.IsNone then
                                InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:value-required"
                            elif measurement.Value |> Option.exists (fun value -> value < 0.0 || Double.IsNaN value || Double.IsInfinity value) then
                                InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:invalid-value"
                            elif noValueExpected.Contains measurement.Availability && measurement.Value.IsSome then
                                InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:value-forbidden"
                            elif not (valueExpected.Contains measurement.Availability || noValueExpected.Contains measurement.Availability) then
                                InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:unknown-availability"
                            if measurement.MetricId.StartsWith("cost.", StringComparison.Ordinal)
                               && (measurement.Unit <> Some "currency"
                                   || measurement.Currency |> Option.exists currency.IsMatch |> not) then
                                InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:cost-currency-required"
                            if measurement.MetricId.StartsWith("cost.", StringComparison.Ordinal) && measurement.Availability = "calculated" then
                                try
                                    use document = JsonDocument.Parse measurement.RawJson
                                    match document.RootElement.TryGetProperty "pricing" with
                                    | true, pricing when pricing.ValueKind = JsonValueKind.Object ->
                                        let valid (name: string) =
                                            match pricing.TryGetProperty name with
                                            | true, value when value.ValueKind = JsonValueKind.String -> not (String.IsNullOrWhiteSpace(value.GetString()))
                                            | _ -> false
                                        if not (valid "source" && valid "version") then
                                            InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:calculated-cost-pricing-required"
                                    | _ -> InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:calculated-cost-pricing-required"
                                with _ -> InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:calculated-cost-pricing-required"
                            if measurement.Availability = "estimated" then
                                try
                                    use document = JsonDocument.Parse measurement.RawJson
                                    match document.RootElement.TryGetProperty "confidence" with
                                    | true, confidence when confidence.ValueKind = JsonValueKind.Number ->
                                        match confidence.TryGetDouble() with
                                        | true, value when value >= 0.0 && value <= 1.0 -> ()
                                        | _ -> InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:estimated-confidence-required"
                                    | true, confidence when confidence.ValueKind = JsonValueKind.String
                                                                    && Set.ofList [ "low"; "medium"; "high" ] |> Set.contains (confidence.GetString()) -> ()
                                    | _ -> InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:estimated-confidence-required"
                                with _ -> InvalidStepStructure $"{step.StepId}:{measurement.MeasurementId}:estimated-confidence-required"
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
        | InvalidTransactionId _ -> "invalid-transaction-id"
        | MissingAgentIdentity -> "missing-agent-identity"
        | InvalidWorkItemId _ -> "invalid-work-item-id"
        | WorkItemBranchMismatch _ -> "work-item-branch-mismatch"
        | ObservedBranchMismatch _ -> "observed-branch-mismatch"
        | InvalidBaseCommit _ -> "invalid-base-commit"
        | BaseCommitUnavailable _ -> "base-commit-unavailable"
        | BaseCommitNotAncestor _ -> "base-commit-not-ancestor"
        | EmptyRequests -> "empty-requests"
        | InvalidTimelineSequence -> "invalid-timeline-sequence"
        | InvalidTimelineOrder -> "invalid-timeline-order"
        | TimelineRequestMismatch -> "timeline-request-mismatch"
        | UnsupportedRequestType value -> "unsupported-request-type:" + value
        | DuplicateTransaction _ -> "duplicate-transaction"
        | InstanceIdentityMismatch _ -> "instance-identity-mismatch"
        | InvalidStepStructure detail -> "invalid-step-structure:" + detail
