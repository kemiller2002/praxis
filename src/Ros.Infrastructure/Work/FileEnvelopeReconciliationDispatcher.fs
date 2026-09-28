namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open Ros.Application.Work
open Ros.Contracts.Work
open Ros.Domain.Git
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Git

/// Maps fallback requests onto the native work planner in an isolated staging
/// root, then commits the rendered canonical files through the replayable
/// envelope transaction. No canonical file is touched until every request and
/// every evidence precondition has been accepted.
[<RequireQualifiedAccess>]
module FileEnvelopeReconciliationDispatcher =
    let private copyIfPresent root staging relative =
        let source = Path.Combine(root, relative)
        if File.Exists source then
            let target = Path.Combine(staging, relative)
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(source, target, true)

    let private readContext root =
        let path = Path.Combine(root, ".ros", "context", "current.json")
        if File.Exists path then WorkContextPlanContract.parseJson (File.ReadAllText path)
        else Ok { WorkItems = []; StartedAt = None; BaselineDirtyPaths = [] }

    let private completionWouldLeaveActiveExecution root (envelope: EnvelopeReconciliationInput) =
        if envelope.Requests |> List.exists (fun request -> request.RequestType = "work.complete") |> not then
            Ok()
        else
            readContext root
            |> Result.bind (fun context ->
                let active =
                    context.WorkItems
                    |> List.filter (fun item -> item.Id = envelope.WorkItem)
                    |> List.collect _.TelemetryExecutionIds
                    |> List.tryFind (fun executionId ->
                        let path = Path.Combine(root, ".ros", "telemetry", "executions", executionId + ".json")
                        if not (File.Exists path) then false
                        else
                            try
                                use document = JsonDocument.Parse(File.ReadAllText path)
                                match document.RootElement.TryGetProperty "status" with
                                | true, status when status.ValueKind = JsonValueKind.String -> status.GetString() = "active"
                                | _ -> false
                            with _ -> false)
                active
                |> Option.map (fun executionId -> Error $"active-execution-requires-native-finalization:{executionId}")
                |> Option.defaultValue (Ok()))

    let private action = function
        | "work.start"
        | "work.begin" -> Ok WorkAction.Begin
        | "work.block" -> Ok WorkAction.Block
        | "work.resume" -> Ok WorkAction.Resume
        | "work.complete" -> Ok WorkAction.Complete
        | value -> Error $"unsupported-request-type:{value}"

    let private targetState = function
        | WorkAction.Begin
        | WorkAction.Resume -> "active"
        | WorkAction.Block -> "blocked"
        | WorkAction.Complete -> "complete"

    let private actionName = function
        | WorkAction.Begin -> "start"
        | WorkAction.Block -> "block"
        | WorkAction.Resume -> "resume"
        | WorkAction.Complete -> "complete"

    let private rejectionMessage = function
        | WorkContextRejection.NoWorkItems -> "no-work-items"
        | WorkContextRejection.InvalidWorkItemId id -> $"invalid-work-item-id:{id}"
        | WorkContextRejection.WorkItemNotInContext id -> $"work-item-not-in-context:{id}"
        | WorkContextRejection.ItemTransitionRejected(id, TransitionRejection.IllegalTransition(state, transition)) ->
            $"illegal-transition:{id}:{state}:{actionName transition}"
        | WorkContextRejection.ItemTransitionRejected(_, TransitionRejection.BlockReasonRequired) -> "block-reason-required"
        | WorkContextRejection.ItemTransitionRejected(id, TransitionRejection.MissingEvidence missing) ->
            let joined = String.concat "," missing
            $"missing-evidence:{id}:{joined}"

    let private actor (value: EnvelopeActor) =
        match ActorKind.tryParse value.ActorKind with
        | None -> Error $"invalid-actor-kind:{value.ActorKind}"
        | Some kind ->
            let result: Actor =
                { Kind = kind
                  Id = value.ActorId
                  Provider = value.Provider
                  Model = value.Model
                  Runtime = value.Runtime }
            match Actor.problems result with
            | [] -> Ok result
            | problems -> Error("invalid-actor:" + (problems |> List.map snd |> String.concat ";"))

    let private metricDefinitions root (envelope: EnvelopeReconciliationInput) =
        let definitions = FileMetricRegistryRepository.read root
        let byId = definitions |> List.map (fun definition -> definition.Id, definition) |> Map.ofList
        let problem =
            envelope.Execution
            |> Option.toList
            |> List.collect _.Steps
            |> List.collect _.Measurements
            |> List.tryPick (fun measurement ->
                match byId |> Map.tryFind measurement.MetricId with
                | None -> Some $"unknown-normalized-metric:{measurement.MetricId}"
                | Some definition when measurement.Unit |> Option.exists ((<>) definition.Unit) ->
                    Some $"metric-unit-mismatch:{measurement.MetricId}:{measurement.Unit.Value}:{definition.Unit}"
                | _ -> None)
        problem |> Option.map Error |> Option.defaultValue (Ok definitions)

    let private validateRawRetention root (content: string) =
        use document = JsonDocument.Parse content
        match document.RootElement.TryGetProperty "rawTelemetry" with
        | false, _ -> Ok()
        | true, raw when raw.ValueKind <> JsonValueKind.Array -> Error "invalid-rendered-raw-telemetry"
        | true, raw ->
            let snapshots = raw.EnumerateArray() |> Seq.toList
            if snapshots.IsEmpty then Ok()
            elif not (FileWorkConfigRepository.readTelemetryAllowRawTelemetry root) then
                Error "raw-telemetry-disabled-by-repository-policy"
            else
                let sizes =
                    snapshots
                    |> List.map (fun snapshot ->
                        match snapshot.TryGetProperty "snapshotId", snapshot.TryGetProperty "payloadBytes" with
                        | (true, id), (true, bytes) when id.ValueKind = JsonValueKind.String && bytes.ValueKind = JsonValueKind.Number ->
                            match bytes.TryGetInt32() with
                            | true, value -> id.GetString(), int64 value
                            | _ -> "unknown", Int64.MaxValue
                        | _ -> "unknown", Int64.MaxValue)
                let maxPayload = int64 (FileWorkConfigRepository.readTelemetryMaxRawPayloadBytes root)
                let maxSnapshots = FileWorkConfigRepository.readTelemetryMaxRawSnapshotsPerExecution root
                let maxBytes = int64 (FileWorkConfigRepository.readTelemetryMaxRawBytesPerExecution root)
                let totalBytes = sizes |> List.sumBy snd
                match sizes |> List.tryFind (fun (_, bytes) -> bytes > maxPayload) with
                | Some(snapshotId, _) -> Error $"raw-snapshot-byte-limit:{snapshotId}"
                | None when snapshots.Length > maxSnapshots -> Error $"raw-snapshot-count-limit:{snapshots.Length}"
                | None when totalBytes > maxBytes -> Error $"raw-execution-byte-limit:{totalBytes}"
                | None -> Ok()

    let private observedPaths root =
        match (ProcessGitRepository.create root).ObserveStatus() with
        | GitStatusObservation.Clean -> Ok []
        | GitStatusObservation.Changed changes -> Ok(changes |> List.collect (fun change -> change.Path :: (change.OriginalPath |> Option.toList)) |> List.distinct)
        | GitStatusObservation.Unavailable failure -> Error $"git-status-unavailable:{failure.Message}"

    let private attachExecution executionId workItemId (plan: WorkContextPlan) =
        let link item =
            if item.Id <> workItemId || List.contains executionId item.TelemetryExecutionIds then item
            else { item with TelemetryExecutionIds = item.TelemetryExecutionIds @ [ executionId ] }
        let linkPlan itemPlan =
            if itemPlan.Item.Id <> workItemId then itemPlan
            else
                let item = link itemPlan.Item
                { itemPlan with
                    Item = item
                    Event = { itemPlan.Event with TelemetryExecutionIds = item.TelemetryExecutionIds } }
        { plan with
            WorkItems = plan.WorkItems |> List.map link
            ItemPlans = plan.ItemPlans |> List.map linkPlan }

    let private applyOne realRoot stagingRoot repositoryId protocolVersion defaultEvidence evidenceByType eventActor envelope executionId index request =
        match action request.RequestType, readContext stagingRoot with
        | Error code, _ -> Error code
        | _, Error message -> Error message
        | Ok requestedAction, Ok context ->
            let occurredAt = request.OccurredAt |> Option.defaultValue envelope.Timeline[index].Timestamp |> _.ToString("O")
            let gitPaths =
                if requestedAction = WorkAction.Complete || (requestedAction = WorkAction.Begin && context.StartedAt.IsNone) then
                    observedPaths realRoot
                else
                    Ok []
            match gitPaths with
            | Error message -> Error message
            | Ok paths ->
                let planRequest: WorkContextPlanRequest =
                    { Context = context
                      Action = requestedAction
                      WorkItemIds = [ envelope.WorkItem ]
                      NewItemType = request.WorkType |> Option.defaultValue "task"
                      TargetLocalState = targetState requestedAction
                      BlockReason = request.Reason
                      DefaultRequiredEvidence = defaultEvidence
                      RequiredEvidenceByType = evidenceByType
                      ProvidedEvidence = request.Evidence
                      Repository = repositoryId
                      ProtocolVersion = protocolVersion
                      Actor = envelope.Agent.ActorId
                      OccurredAt = occurredAt
                      MeaningfulChangedPaths =
                        if requestedAction = WorkAction.Complete then
                            PathFilter.meaningfulPaths (FileWorkConfigRepository.readPathFilterConfig realRoot) paths
                        else
                            []
                      ObservedGitPaths = paths
                      TelemetryEnabled = false }
                match WorkOperations.planVerifiedContext (FileEvidenceRepository.create realRoot) planRequest with
                | VerifiedWorkContextPlanOutcome.ContextRejected rejection -> Error(rejectionMessage rejection)
                | VerifiedWorkContextPlanOutcome.EvidenceRejected issues ->
                    let codes =
                        issues
                        |> List.map (function
                            | EvidenceIssue.Missing evidence -> $"missing-evidence-path:{evidence.Path}"
                            | EvidenceIssue.Unavailable(evidence, _) -> $"unavailable-evidence-path:{evidence.Path}")
                    Error(String.concat "," codes)
                | VerifiedWorkContextPlanOutcome.Planned plan ->
                    let linked = executionId |> Option.map (fun id -> attachExecution id envelope.WorkItem plan) |> Option.defaultValue plan
                    let conclusions =
                        if requestedAction = WorkAction.Complete then
                            linked.ItemPlans
                            |> List.filter (fun itemPlan -> itemPlan.Item.WorkType = "research")
                            |> List.map (fun itemPlan -> itemPlan.Item.Id, request.Conclusion |> Option.defaultValue "inconclusive")
                            |> Map.ofList
                        else
                            Map.empty
                    FileWorkContextRepository.applyContextPlanWithConclusions stagingRoot repositoryId conclusions eventActor linked
                    |> Result.bind (fun _ ->
                        if requestedAction <> WorkAction.Complete then
                            Ok()
                        else
                            readContext stagingRoot
                            |> Result.bind (fun updated -> FileBacklogQueueRepository.markComplete stagingRoot envelope.WorkItem occurredAt updated.WorkItems))

    let apply root envelopePath envelopeHash headCommit clock (envelope: EnvelopeReconciliationInput) =
        match actor envelope.Agent, metricDefinitions root envelope, completionWouldLeaveActiveExecution root envelope with
        | Error code, _, _
        | _, Error code, _
        | _, _, Error code -> Error(EnvelopeApplyFailure.Invalid [ code ])
        | Ok eventActor, Ok definitions, Ok() ->
            let staging = Path.Combine(Path.GetTempPath(), $"praxis-envelope-stage-{Guid.NewGuid():N}")
            Directory.CreateDirectory staging |> ignore
            let outcome =
                try
                    copyIfPresent root staging (Path.Combine(".ros", "context", "current.json"))
                    copyIfPresent root staging (Path.Combine(".ros", "events", "events.jsonl"))
                    copyIfPresent root staging (Path.Combine(".ros", "work", "queue.json"))
                    copyIfPresent root staging (Path.Combine(".ros", "work", "queue.md"))
                    let repositoryId = FileWorkConfigRepository.readRepositoryId root
                    let protocolVersion = FileWorkConfigRepository.readProtocolVersion root
                    let defaultEvidence, evidenceByType = FileWorkConfigRepository.readCompletionEvidence root
                    let executionId = envelope.Execution |> Option.map _.ExecutionId
                    let result =
                        ((Ok()), envelope.Requests |> List.indexed)
                        ||> List.fold (fun state (index, request) ->
                            match state with
                            | Error message -> Error message
                            | Ok() -> applyOne root staging repositoryId protocolVersion defaultEvidence evidenceByType eventActor envelope executionId index request)
                    match result with
                    | Error code -> Error(EnvelopeApplyFailure.Invalid [ code ])
                    | Ok() ->
                        let writes = ResizeArray<EnvelopeTransactionWrite>()
                        for relative in [ ".ros/events/events.jsonl"; ".ros/context/current.json" ] do
                            let staged = Path.Combine(staging, relative)
                            if File.Exists staged then writes.Add { Path = relative; Content = File.ReadAllText staged }

                        for relative in [ ".ros/work/queue.json"; ".ros/work/queue.md" ] do
                            let staged = Path.Combine(staging, relative)
                            let existing = Path.Combine(root, relative)
                            if File.Exists staged && (not (File.Exists existing) || File.ReadAllText staged <> File.ReadAllText existing) then
                                writes.Add { Path = relative; Content = File.ReadAllText staged }

                        match envelope.Execution with
                        | Some execution ->
                            let relative = $".ros/telemetry/executions/{execution.ExecutionId}.json"
                            let content = FileEnvelopeExecutionRepository.render definitions repositoryId headCommit envelope execution
                            match validateRawRetention root content with
                            | Error code -> raise (InvalidOperationException code)
                            | Ok() ->
                                let existing = Path.Combine(root, relative)
                                if File.Exists existing && File.ReadAllText existing <> content then
                                    raise (InvalidOperationException $"execution-id-conflict:{execution.ExecutionId}")
                                writes.Add { Path = relative; Content = content }
                        | None -> ()

                        let receipt: EnvelopeReconciliationReceipt =
                            { TransactionId = envelope.TransactionId
                              WorkItem = envelope.WorkItem
                              Branch = envelope.Branch
                              HeadCommit = headCommit
                              EnvelopeHash = envelopeHash
                              CheckpointTag = Some(EnvelopeReconciliationReceipt.checkpointTag envelope.TransactionId)
                              Status = EnvelopeReconciliationReceiptStatus.Applied
                              FindingCodes = []
                              ReconciledAt = clock() }
                        writes.Add
                            { Path = $".praxis/reconciled/{EnvelopeReconciliationReceipt.safeTransactionId envelope.TransactionId}.json"
                              Content = FileEnvelopeReconciliationStore.serializeReceipt receipt }

                        FileEnvelopeReconciliationTransaction.apply
                            root
                            { TransactionId = envelope.TransactionId
                              EnvelopePath = envelopePath
                              Writes = List.ofSeq writes }
                        |> Result.map (fun _ -> ())
                        |> Result.mapError EnvelopeApplyFailure.Indeterminate
                with error -> Error(EnvelopeApplyFailure.Invalid [ error.Message ])
            try Directory.Delete(staging, true) with _ -> ()
            outcome
