namespace Ros.Contracts.Work

open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Git
open Ros.Domain.Work

/// The persisted and machine-readable forms of durable checkpoints
/// (DF-ROS-2026-A042). Field names are a public contract: the event body,
/// the `latestCheckpoint` projection on a work-context item, and the
/// `continuity` read model every command reports.
[<RequireQualifiedAccess>]
module CheckpointJson =
    let eventType = "work.checkpointed"

    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private child (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonObject as value -> Some value
        | _ -> None

    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    let private remoteNode (remote: RemoteIdentity) =
        let node = JsonObject()
        node["name"] <- JsonValue.Create remote.Name
        remote.Url |> Option.iter (fun url -> node["url"] <- JsonValue.Create url)
        node
    let private verificationNode (verification: CheckpointVerification) =
        let node = JsonObject()
        node["status"] <- JsonValue.Create "verified"
        node["mechanism"] <- JsonValue.Create(DurabilityMechanism.code verification.Mechanism)
        node

    /// The `checkpoint` object carried by a `work.checkpointed` event: the
    /// historical fact, in the order it is hashed.
    let body (checkpoint: Checkpoint) : JsonObject =
        let git = DurableLocation.git checkpoint.Location
        let node = JsonObject()
        node["repository"] <- JsonValue.Create git.Repository
        node["branch"] <- JsonValue.Create git.Branch
        node["commit"] <- JsonValue.Create git.LocalCommit.Value
        node["remote"] <- remoteNode git.Remote
        node["remoteBranch"] <- JsonValue.Create git.RemoteBranch
        node["remoteCommit"] <- JsonValue.Create git.RemoteCommit.Value
        node["summary"] <- JsonValue.Create checkpoint.Summary
        node["nextAction"] <- JsonValue.Create checkpoint.NextAction
        node["verification"] <- verificationNode checkpoint.Verification
        node

    /// The `latestCheckpoint` projection written onto a work-context item.
    let projection (recorded: RecordedCheckpoint) : JsonObject =
        let checkpoint = recorded.Recorded
        let node = JsonObject()
        node["id"] <- JsonValue.Create recorded.CheckpointId
        node["recordedAt"] <- JsonValue.Create checkpoint.RecordedAt
        node["executionId"] <- JsonValue.Create checkpoint.ExecutionId
        checkpoint.StepId |> Option.iter (fun step -> node["stepId"] <- JsonValue.Create step)

        for property in body checkpoint do
            node[property.Key] <- property.Value.DeepClone()

        node

    let private stored (workItemId: string) (executionId: string) (stepId: string option) (recordedAt: string) (checkpoint: JsonObject) : StoredCheckpoint =
        let remote = child checkpoint "remote"
        let verification = child checkpoint "verification"

        { WorkItemId = workItemId
          ExecutionId = executionId
          StepId = stepId
          Summary = text checkpoint "summary" |> Option.defaultValue ""
          NextAction = text checkpoint "nextAction" |> Option.defaultValue ""
          RecordedAt = recordedAt
          Repository = text checkpoint "repository" |> Option.defaultValue ""
          Branch = text checkpoint "branch" |> Option.defaultValue ""
          Commit = text checkpoint "commit" |> Option.defaultValue ""
          RemoteName = remote |> Option.bind (fun value -> text value "name") |> Option.defaultValue ""
          RemoteUrl = remote |> Option.bind (fun value -> text value "url")
          RemoteBranch = text checkpoint "remoteBranch" |> Option.defaultValue ""
          RemoteCommit = text checkpoint "remoteCommit" |> Option.defaultValue ""
          VerificationStatus = verification |> Option.bind (fun value -> text value "status") |> Option.defaultValue ""
          Mechanism = verification |> Option.bind (fun value -> text value "mechanism") |> Option.defaultValue "" }

    /// A work-context item's `latestCheckpoint`: `Ok None` when absent (every
    /// record written before checkpoints existed), an error when present but
    /// not a valid checkpoint.
    let tryParseProjection (workItemId: string) (value: JsonNode) : Result<RecordedCheckpoint option, string list> =
        match value with
        | null -> Ok None
        | :? JsonObject as node ->
            match text node "id", text node "executionId", text node "recordedAt" with
            | Some id, Some executionId, Some recordedAt ->
                stored workItemId executionId (text node "stepId") recordedAt node
                |> Checkpoint.rehydrate
                |> Result.map (fun checkpoint -> Some { CheckpointId = id; Recorded = checkpoint })
            | _ -> Error [ "latestCheckpoint must name its id, executionId and recordedAt" ]
        | _ -> Error [ "latestCheckpoint must be an object" ]

    /// A parsed `work.checkpointed` event, or why it is not one.
    type EventRead =
        { EventId: string
          WorkItemId: string
          OccurredAt: string
          Paths: string list
          TelemetryExecutionIds: string list
          Checkpoint: Result<Checkpoint, string list> }

    let isCheckpointEvent (node: JsonObject) = text node "type" = Some eventType

    let tryReadEvent (node: JsonObject) : EventRead =
        let pathsOf (name: string) =
            match node[name] with
            | :? JsonArray as values ->
                values
                |> Seq.choose (function
                    | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                    | _ -> None)
                |> Seq.toList
            | _ -> []

        let workItemId = text node "workItem" |> Option.defaultValue ""
        let occurredAt = text node "occurredAt" |> Option.defaultValue ""

        { EventId = text node "eventId" |> Option.defaultValue ""
          WorkItemId = workItemId
          OccurredAt = occurredAt
          Paths = pathsOf "paths"
          TelemetryExecutionIds = pathsOf "telemetryExecutions"
          Checkpoint =
            match text node "execution", child node "checkpoint" with
            | Some executionId, Some checkpoint -> stored workItemId executionId (text node "step") occurredAt checkpoint |> Checkpoint.rehydrate
            | None, _ -> Error [ "execution is required" ]
            | _, None -> Error [ "checkpoint is required" ] }

    /// One `work checkpoint show` history entry.
    let historyEntry (event: EventRead) : JsonObject =
        let node = JsonObject()
        node["id"] <- JsonValue.Create event.EventId
        node["recordedAt"] <- JsonValue.Create event.OccurredAt

        match event.Checkpoint with
        | Ok checkpoint ->
            let git = DurableLocation.git checkpoint.Location
            node["executionId"] <- JsonValue.Create checkpoint.ExecutionId
            checkpoint.StepId |> Option.iter (fun step -> node["stepId"] <- JsonValue.Create step)
            node["branch"] <- JsonValue.Create git.Branch
            node["commit"] <- JsonValue.Create git.LocalCommit.Value
            node["remote"] <- JsonValue.Create git.Remote.Name
            node["remoteBranch"] <- JsonValue.Create git.RemoteBranch
            node["summary"] <- JsonValue.Create checkpoint.Summary
            node["nextAction"] <- JsonValue.Create checkpoint.NextAction
        | Error problems ->
            let array = JsonArray()
            problems |> List.iter (fun problem -> array.Add(JsonValue.Create problem: JsonNode))
            node["invalid"] <- array

        let paths = JsonArray()
        event.Paths |> List.iter (fun path -> paths.Add(JsonValue.Create path: JsonNode))
        node["paths"] <- paths
        node

    // ---- the continuity read model ----

    let private recoverabilityNode (value: CurrentRecoverability) =
        let node = JsonObject()
        node["status"] <- JsonValue.Create(CurrentRecoverability.code value)

        node["recoverable"] <-
            match CurrentRecoverability.isRecoverable value with
            | Some flag -> JsonValue.Create flag
            | None -> null

        node["verifiesCheckpoint"] <- JsonValue.Create(CurrentRecoverability.verifiesCheckpoint value)

        match value with
        | CurrentRecoverability.ContainedWithoutMeaningfulChange(head, commits) ->
            node["remoteHead"] <- JsonValue.Create head.Value
            node["commitsAfter"] <- JsonValue.Create commits
        | CurrentRecoverability.RemoteAdvanced(head, commits, paths) ->
            node["remoteHead"] <- JsonValue.Create head.Value
            node["commitsAfter"] <- JsonValue.Create commits
            node["meaningfulPaths"] <- strings paths
        | CurrentRecoverability.RemoteMovedAncestryUnknown head
        | CurrentRecoverability.NotContained head -> node["remoteHead"] <- JsonValue.Create head.Value
        | CurrentRecoverability.RemoteUnreachable message
        | CurrentRecoverability.Unknown message -> node["message"] <- JsonValue.Create message
        | CurrentRecoverability.AtRemoteHead
        | CurrentRecoverability.RemoteBranchMissing -> ()

        node

    let private localNode (value: LocalCheckpointPosition) =
        let node = JsonObject()
        node["position"] <- JsonValue.Create(LocalCheckpointPosition.code value)

        match value with
        | LocalCheckpointPosition.OnlyNonMeaningfulCommitsAfter commits -> node["commitsAfter"] <- JsonValue.Create commits
        | LocalCheckpointPosition.CommitsAfter(commits, paths) ->
            node["commitsAfter"] <- JsonValue.Create commits
            node["meaningfulPaths"] <- strings paths
        | LocalCheckpointPosition.HeadDoesNotContain head -> node["head"] <- JsonValue.Create head.Value
        | LocalCheckpointPosition.Unknown message -> node["message"] <- JsonValue.Create message
        | _ -> ()

        node

    let workingTreeNode (tree: WorkingTreeState) =
        let node = JsonObject()

        node["state"] <-
            JsonValue.Create(
                match tree with
                | WorkingTreeState.Clean _ -> "clean"
                | WorkingTreeState.Dirty _ -> "dirty"
                | WorkingTreeState.NotRepository -> "not-repository"
                | WorkingTreeState.Unknown _ -> "unknown"
            )

        node["meaningfulPaths"] <- strings (WorkingTreeState.meaningfulPaths tree)
        node["excludedBaselinePaths"] <- strings (WorkingTreeState.excludedBaselinePaths tree)

        match tree with
        | WorkingTreeState.Unknown failure -> node["message"] <- JsonValue.Create failure.Message
        | _ -> ()

        node

    let warningNode (warning: ContinuityWarning) =
        let node = JsonObject()
        node["code"] <- JsonValue.Create(ContinuityWarning.code warning)
        node["workItemId"] <- JsonValue.Create(ContinuityWarning.workItemId warning)
        node["message"] <- JsonValue.Create(ContinuityWarning.message warning)
        node

    let recoveryNode (step: RecoveryStep) =
        let node = JsonObject()
        node["kind"] <- JsonValue.Create(RecoveryStep.kind step)

        match step with
        | RecoveryStep.Stop reason -> node["text"] <- JsonValue.Create reason
        | RecoveryStep.Note text -> node["text"] <- JsonValue.Create text
        | RecoveryStep.Run(command, purpose) ->
            node["command"] <- JsonValue.Create command
            node["text"] <- JsonValue.Create purpose
        | RecoveryStep.Verify(command, expectation) ->
            node["command"] <- JsonValue.Create command
            node["text"] <- JsonValue.Create expectation

        node

    /// The checkpoint as reported to a reader: the historical fact plus the
    /// separately observed current recoverability and freshness.
    let checkpointView (assessment: CheckpointAssessment) (recorded: RecordedCheckpoint) =
        let node = projection recorded
        node["status"] <- JsonValue.Create "verified"
        node["freshness"] <- JsonValue.Create(CheckpointFreshness.code assessment.Freshness)

        node["currentRecoverability"] <-
            match assessment.CurrentRecoverability with
            | Some value -> recoverabilityNode value :> JsonNode
            | None -> null

        node

    /// One work item's `continuity` block.
    let continuity (workItemId: string) (state: string) (assessment: CheckpointAssessment) (recovery: RecoveryStep list) =
        let node = JsonObject()
        node["workItemId"] <- JsonValue.Create workItemId
        node["state"] <- JsonValue.Create state
        node["freshness"] <- JsonValue.Create(CheckpointFreshness.code assessment.Freshness)

        node["checkpoint"] <-
            match assessment.Checkpoint with
            | Some recorded -> checkpointView assessment recorded :> JsonNode
            | None -> null

        node["local"] <-
            match assessment.Local with
            | Some local -> localNode local :> JsonNode
            | None -> null

        node["workingTree"] <- workingTreeNode assessment.WorkingTree
        let warnings = JsonArray()
        assessment.Warnings |> List.iter (fun warning -> warnings.Add(warningNode warning: JsonNode))
        node["warnings"] <- warnings
        let steps = JsonArray()
        recovery |> List.iter (fun step -> steps.Add(recoveryNode step: JsonNode))
        node["recovery"] <- steps
        node

    let rejectionNode (rejection: CheckpointRejection) =
        let node = JsonObject()
        node["code"] <- JsonValue.Create(CheckpointRejection.code rejection)
        node["message"] <- JsonValue.Create(CheckpointRejection.message rejection)
        node["remedy"] <- JsonValue.Create(CheckpointRejection.remedy rejection)

        match rejection with
        | CheckpointRejection.UncommittedChanges paths -> node["paths"] <- strings paths
        | _ -> ()

        node
