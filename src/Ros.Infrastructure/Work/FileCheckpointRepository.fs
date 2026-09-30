namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Application.Work
open Ros.Contracts.Provenance
open Ros.Contracts.Work
open Ros.Domain.Git
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Domain.Work
open Ros.Infrastructure.Json
open Ros.Infrastructure.Provenance

/// A work-context item as continuity sees it.
type ContinuityItem =
    { WorkItemId: string
      WorkType: string
      State: LiveWorkState
      LocalState: string
      LatestCheckpoint: Result<RecordedCheckpoint option, string list> }

/// Durable checkpoint persistence (DF-ROS-2026-A042, PRAXIS-CONT-03):
/// `work.checkpointed` events in the append-only log and the
/// `latestCheckpoint` projection on the work-context item, written together
/// through the `work-state` recovery journal. Callers hold the
/// `work-protocol` lock for every write.
[<RequireQualifiedAccess>]
module FileCheckpointRepository =
    let private eventsRelativePath = ".ros/events/events.jsonl"
    let private contextRelativePath = ".ros/context/current.json"

    let private indented =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private compact = JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private parseState value =
        match value with
        | "ready" -> Some LiveWorkState.Ready
        | "active" -> Some LiveWorkState.Active
        | "blocked" -> Some LiveWorkState.Blocked
        | "complete" -> Some LiveWorkState.Complete
        | _ -> None

    let stateCode state =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"

    let private readContextNode (root: string) : Result<JsonObject option, string> =
        let path = Path.Combine(root, contextRelativePath)

        if not (File.Exists path) then
            Ok None
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as context -> Ok(Some context)
                | _ -> Error "context/current.json must contain a JSON object"
            with error ->
                Error $"context/current.json is not valid JSON: {error.Message}"

    let private itemNodes (context: JsonObject) =
        match context["workItems"] with
        | :? JsonArray as items -> items |> Seq.choose (function :? JsonObject as item -> Some item | _ -> None) |> Seq.toList
        | _ -> []

    let private continuityItem (node: JsonObject) =
        match text node "id", text node "semanticState" |> Option.bind parseState with
        | Some id, Some state ->
            Some
                { WorkItemId = id
                  WorkType = text node "type" |> Option.defaultValue "task"
                  State = state
                  LocalState = text node "state" |> Option.defaultValue (stateCode state)
                  LatestCheckpoint = CheckpointJson.tryParseProjection id node["latestCheckpoint"] }
        | _ -> None

    /// Every work-context item, with its latest checkpoint.
    let readItems (root: string) : Result<ContinuityItem list, string> =
        readContextNode root
        |> Result.map (function
            | None -> []
            | Some context -> itemNodes context |> List.choose continuityItem)

    let readItem (root: string) (workItemId: string) : Result<ContinuityItem option, string> =
        readItems root |> Result.map (List.tryFind (fun item -> item.WorkItemId = workItemId))

    /// The context's `baselineDirtyPaths` and the configured path filter.
    let private readBasePolicy (root: string) : ContinuityPolicy =
        let baseline =
            match readContextNode root with
            | Ok(Some context) ->
                match context["baselineDirtyPaths"] with
                | :? JsonArray as values ->
                    values
                    |> Seq.choose (function
                        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                        | _ -> None)
                    |> Seq.toList
                | _ -> []
            | _ -> []

        { PathFilter = FileWorkConfigRepository.readPathFilterConfig root
          BaselineDirtyPaths = baseline
          Ownership = None }

    let private eventNodes (root: string) : JsonObject list =
        let path = Path.Combine(root, eventsRelativePath)

        if not (File.Exists path) then
            []
        else
            File.ReadAllLines path
            |> Array.filter (fun line -> line.Trim().Length > 0)
            |> Array.choose (fun line ->
                try
                    match JsonNode.Parse line with
                    | :? JsonObject as node -> Some node
                    | _ -> None
                with _ ->
                    None)
            |> Array.toList

    /// Every `work.checkpointed` event, in log order, with its raw node.
    let readEvents (root: string) : (JsonObject * CheckpointJson.EventRead) list =
        eventNodes root
        |> List.filter CheckpointJson.isCheckpointEvent
        |> List.map (fun node -> node, CheckpointJson.tryReadEvent node)

    /// What every recorded checkpoint and Git-evidenced reconciliation
    /// claimed: its work item, its commit(s), and the paths it attributed.
    /// Checkpoint events that do not verify claim nothing.
    let readClaims (root: string) : CheckpointClaim list =
        let checkpoints =
            readEvents root
            |> List.choose (fun (_, event) ->
                match event.Checkpoint with
                | Ok checkpoint ->
                    Some
                        { WorkItemId = event.WorkItemId
                          Commit = checkpoint.Commit.Value
                          Paths = event.Paths }
                | Error _ -> None)

        let strings (node: JsonNode) =
            match node with
            | :? JsonArray as values ->
                values
                |> Seq.choose (function
                    | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                    | _ -> None)
                |> Seq.toList
            | _ -> []

        let reconciliations =
            eventNodes root
            |> List.filter (fun node -> text node "type" = Some "work.attribution.reconciled")
            |> List.collect (fun node ->
                match text node "workItem", node["gitEvidence"] with
                | Some workItemId, (:? JsonObject as evidence) ->
                    let paths = strings node["paths"]

                    match evidence["commits"] with
                    | :? JsonArray as commits ->
                        commits
                        |> Seq.choose (function
                            | :? JsonObject as commit -> text commit "sha"
                            | _ -> None)
                        |> Seq.map (fun sha -> { WorkItemId = workItemId; Commit = sha; Paths = paths })
                        |> Seq.toList
                    | _ -> []
                | _ -> [])

        checkpoints @ reconciliations

    /// The continuity policy, with the recorded checkpoints of every work
    /// item as ownership evidence, so a work item never claims commits
    /// another item's checkpoint already owns (PRAXIS-CONT-12).
    let readPolicy (root: string) : ContinuityPolicy =
        { readBasePolicy root with
            Ownership =
                Some
                    { History = Ros.Infrastructure.Git.ProcessGitDurability.createCommitHistory root
                      Claims = readClaims root } }

    /// One work item's checkpoint history, oldest first. Never rewritten.
    let readHistory (root: string) (workItemId: string) : CheckpointJson.EventRead list =
        readEvents root |> List.map snd |> List.filter (fun event -> event.WorkItemId = workItemId)

    /// The commit the work item's first execution started from, when the
    /// execution record captured one.
    let readStartCommit (root: string) (workItemId: string) : CommitId option =
        FileTelemetryQueryRepository.readByWorkItemId root workItemId
        |> List.choose (fun record ->
            let startedAt = text record "startedAt" |> Option.defaultValue ""

            match record["repository"] with
            | :? JsonObject as repository ->
                match repository["start"] with
                | :? JsonObject as start -> text start "commit" |> Option.bind CommitId.tryParse |> Option.map (fun commit -> startedAt, commit)
                | _ -> None
            | _ -> None)
        |> List.sortBy fst
        |> List.tryHead
        |> Option.map snd

    /// Which execution a continuity action by this process belongs to: an
    /// active execution of this work item that the process may continue
    /// (its actor agrees and nothing known about the run differs). Implicit
    /// selection additionally needs positive evidence of being that run,
    /// exactly as contribution attribution does; `--execution` is an
    /// explicit assertion that still has to agree.
    let resolveExecution (root: string) (workItemId: string) (overrides: IdentityInputs) (explicitExecution: string option) : ExecutionObservation =
        match FileTelemetryExecutionRepository.resolveIdentity overrides with
        | Error message -> ExecutionObservation.Refused message
        | Ok(actor, identity, _) ->
            let executions =
                FileProvenanceRepository.readExecutions root
                |> List.filter (fun view -> view.WorkItemId = workItemId)

            let steps executionId =
                match FileTelemetryQueryRepository.readByExecutionId root executionId with
                | Ok record -> FileTelemetryFinalizationRepository.stepEvents record |> Steps.project |> List.map _.StepId
                | Error _ -> []

            let mine (view: ExecutionRecordView) = ActorResolution.mayContinue actor identity view.Actor view.Identity

            match explicitExecution with
            | Some executionId ->
                match executions |> List.tryFind (fun view -> view.ExecutionId = executionId) with
                | None -> ExecutionObservation.Refused $"execution '{executionId}' is not an execution of '{workItemId}'"
                | Some view when view.Status <> "active" -> ExecutionObservation.Refused $"execution '{executionId}' is not active"
                | Some view when ActorResolution.isDeclared actor && not (mine view) ->
                    ExecutionObservation.Refused
                        $"this process identifies as {Actor.describe actor}, which does not match execution '{executionId}' ({Actor.describe view.Actor}); a checkpoint is never recorded under another actor's or another run's execution"
                | Some view -> ExecutionObservation.Resolved(view.ExecutionId, steps view.ExecutionId)
            | None when not (ActorResolution.isDeclared actor) ->
                ExecutionObservation.Refused
                    "this process has no identity of its own, so it cannot select an execution implicitly; declare yourself (ROS_ACTOR_KIND and ROS_ACTOR, or --actor-kind and --actor)"
            | None ->
                let candidates =
                    executions
                    |> List.filter (fun view -> view.Status = "active")
                    |> List.filter (fun view -> mine view && ActorResolution.evidentlySameRun actor identity view.Actor view.Identity)

                match candidates with
                | [] -> ExecutionObservation.NoneActive
                | [ view ] -> ExecutionObservation.Resolved(view.ExecutionId, steps view.ExecutionId)
                | many -> ExecutionObservation.Ambiguous(many |> List.map _.ExecutionId)

    /// The content-addressed event, built exactly like every other work
    /// event: the hash covers every field before `eventId`.
    let eventNode
        (repositoryId: string)
        (protocolVersion: string)
        (actor: Actor)
        (paths: string list)
        (checkpoint: Checkpoint)
        : JsonObject =
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "1.0.0"
        node["type"] <- JsonValue.Create CheckpointJson.eventType
        node["workItem"] <- JsonValue.Create checkpoint.WorkItemId
        node["repository"] <- JsonValue.Create repositoryId
        node["protocolVersion"] <- JsonValue.Create protocolVersion
        node["occurredAt"] <- JsonValue.Create checkpoint.RecordedAt
        node["execution"] <- JsonValue.Create checkpoint.ExecutionId
        checkpoint.StepId |> Option.iter (fun step -> node["step"] <- JsonValue.Create step)
        node["checkpoint"] <- CheckpointJson.body checkpoint
        let pathArray = JsonArray()
        paths |> List.iter (fun path -> pathArray.Add(JsonValue.Create path: JsonNode))
        node["paths"] <- pathArray
        let executions = JsonArray()
        executions.Add(JsonValue.Create checkpoint.ExecutionId: JsonNode)
        node["telemetryExecutions"] <- executions
        node["actor"] <- ActorJson.node actor
        let publication = JsonObject()
        publication["status"] <- JsonValue.Create "pending"
        node["publication"] <- publication
        node["eventId"] <- JsonValue.Create(CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact node))
        node

    /// Whether a stored event's `eventId` still matches its content.
    let eventIdMatches (node: JsonObject) =
        match text node "eventId" with
        | None -> false
        | Some recorded ->
            let copy = node.DeepClone() :?> JsonObject
            copy.Remove "eventId" |> ignore
            CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact copy) = recorded

    /// Appends the checkpoint event and sets the item's `latestCheckpoint`,
    /// atomically through the `work-state` journal. The caller holds the
    /// `work-protocol` lock and has already recovered any pending journal.
    let record (root: string) (actor: Actor) (paths: string list) (checkpoint: Checkpoint) : Result<RecordedCheckpoint, string> =
        try
            match readContextNode root with
            | Error message -> Error message
            | Ok None -> Error "there is no work context to checkpoint"
            | Ok(Some context) ->
                match itemNodes context |> List.tryFind (fun item -> text item "id" = Some checkpoint.WorkItemId) with
                | None -> Error $"work item '{checkpoint.WorkItemId}' is not in repository context"
                | Some item ->
                    let repositoryId = FileWorkConfigRepository.readRepositoryId root
                    let protocolVersion = FileWorkConfigRepository.readProtocolVersion root
                    let event = eventNode repositoryId protocolVersion actor paths checkpoint
                    let eventId = text event "eventId" |> Option.defaultValue ""
                    let recorded = { CheckpointId = eventId; Recorded = checkpoint }
                    item["latestCheckpoint"] <- CheckpointJson.projection recorded

                    let eventsPath = Path.Combine(root, eventsRelativePath)
                    let current = if File.Exists eventsPath then File.ReadAllText eventsPath else ""

                    let eventsContent =
                        if current.Contains $"\"eventId\":\"{eventId}\"" then
                            current
                        else
                            let separator = if current.Length > 0 && not (current.EndsWith "\n") then "\n" else ""
                            $"{current}{separator}{event.ToJsonString compact}\n"

                    let writes: WorkStateWrite list =
                        [ { Path = eventsRelativePath; Content = eventsContent }
                          { Path = contextRelativePath; Content = context.ToJsonString indented + "\n" } ]

                    match WorkStateTransaction.prepare root writes with
                    | Error failure -> Error $"state persistence failed: {failure.Message}"
                    | Ok() ->
                        match WorkStateTransaction.recover root with
                        | Error failure -> Error $"state persistence failed: {failure.Message}"
                        | Ok() -> Ok recorded
        with error ->
            Error $"state persistence failed: {error.Message}"

    /// Gathers the offline facts `CheckpointValidation` needs.
    let validationFindings (root: string) : CheckpointFinding list =
        let events =
            readEvents root
            |> List.map (fun (node, read) ->
                { EventId = read.EventId
                  IdMatchesContent = eventIdMatches node
                  SchemaVersion = text node "schemaVersion"
                  WorkItemId = read.WorkItemId
                  OccurredAt = read.OccurredAt
                  ExecutionId = text node "execution"
                  StepId = text node "step"
                  Checkpoint = read.Checkpoint })

        let continuations =
            eventNodes root
            |> List.filter (fun node -> text node "type" = Some "work.continued")
            |> List.map (fun node ->
                { EventId = text node "eventId" |> Option.defaultValue ""
                  IdMatchesContent = eventIdMatches node
                  WorkItemId = text node "workItem" |> Option.defaultValue ""
                  SuccessorExecutionId = text node "execution"
                  PredecessorExecutionId =
                    match node["predecessor"] with
                    | :? JsonObject as predecessor -> text predecessor "executionId"
                    | _ -> None
                  CheckpointId = text node "checkpoint" })

        match events, continuations, readItems root with
        | [], [], Ok items when items |> List.forall (fun item -> item.LatestCheckpoint = Ok None) -> []
        | _, _, Error message ->
            [ { Path = contextRelativePath
                Field = "workItems"
                Message = message } ]
        | _, _, Ok items ->
            let records = FileTelemetryQueryRepository.readAll root

            let parents =
                records
                |> List.choose (fun record ->
                    match text record "executionId", record["identity"] with
                    | Some id, (:? JsonObject as identity) -> text identity "parentExecutionId" |> Option.map (fun parent -> id, parent)
                    | _ -> None)
                |> Map.ofList

            let executions =
                records
                |> List.choose (fun record ->
                    match text record "executionId", text record "workItemId" with
                    | Some executionId, Some workItemId ->
                        Some
                            { ExecutionId = executionId
                              WorkItemId = workItemId
                              StartedAt = text record "startedAt" |> Option.defaultValue ""
                              StartedSteps = FileTelemetryFinalizationRepository.stepEvents record |> Steps.project |> List.map _.StepId }
                    | _ -> None)

            CheckpointValidation.findings
                { Events = events
                  Continuations = continuations
                  Parents = parents
                  Executions = executions
                  WorkItemIds = items |> List.map _.WorkItemId
                  Projections =
                    items
                    |> List.map (fun item ->
                        { WorkItemId = item.WorkItemId
                          Projection = item.LatestCheckpoint }) }

    /// The latest `work.blocked` event that truthfully recorded the local
    /// state as not remotely recoverable, when no checkpoint followed it.
    let readUnrecoverableNotice (root: string) (workItemId: string) : JsonObject option =
        let events = eventNodes root |> List.filter (fun node -> text node "workItem" = Some workItemId)

        let lastCheckpointIndex =
            events |> List.tryFindIndexBack CheckpointJson.isCheckpointEvent |> Option.defaultValue -1

        events
        |> List.indexed
        |> List.filter (fun (index, node) ->
            index > lastCheckpointIndex
            && text node "type" = Some "work.blocked"
            && (match node["continuity"] with
                | :? JsonObject -> true
                | _ -> false))
        |> List.tryLast
        |> Option.map (fun (_, node) ->
            let notice = (node["continuity"] :?> JsonObject).DeepClone() :?> JsonObject
            notice["eventId"] <- JsonValue.Create(text node "eventId" |> Option.defaultValue "")
            notice["recordedAt"] <- JsonValue.Create(text node "occurredAt" |> Option.defaultValue "")

            match node["actor"] with
            | null -> ()
            | actor -> notice["actor"] <- actor.DeepClone()

            notice)

    let continuedEventType = "work.continued"

    /// Links the successor execution to the work item and appends one
    /// `work.continued` event naming the predecessor, the successor and the
    /// checkpoint the successor continued from, atomically through the
    /// `work-state` journal. The predecessor's execution record is never
    /// touched. The caller holds the `work-protocol` lock.
    let recordContinuation
        (root: string)
        (actor: Actor)
        (occurredAt: string)
        (plan: ContinuationPlan)
        (successorExecution: string option)
        : Result<string, string> =
        try
            match readContextNode root with
            | Error message -> Error message
            | Ok None -> Error "there is no work context to continue"
            | Ok(Some context) ->
                match itemNodes context |> List.tryFind (fun item -> text item "id" = Some plan.WorkItemId) with
                | None -> Error $"work item '{plan.WorkItemId}' is not in repository context"
                | Some item ->
                    successorExecution
                    |> Option.iter (fun executionId ->
                        let links =
                            match item["telemetryExecutionIds"] with
                            | :? JsonArray as array -> array
                            | _ ->
                                let created = JsonArray()
                                item["telemetryExecutionIds"] <- created
                                created

                        let known =
                            links |> Seq.exists (fun node -> node <> null && node.GetValueKind() = JsonValueKind.String && node.GetValue<string>() = executionId)

                        if not known then
                            links.Add(JsonValue.Create executionId: JsonNode))

                    let node = JsonObject()
                    node["schemaVersion"] <- JsonValue.Create "1.0.0"
                    node["type"] <- JsonValue.Create continuedEventType
                    node["workItem"] <- JsonValue.Create plan.WorkItemId
                    node["repository"] <- JsonValue.Create(FileWorkConfigRepository.readRepositoryId root)
                    node["protocolVersion"] <- JsonValue.Create(FileWorkConfigRepository.readProtocolVersion root)
                    node["occurredAt"] <- JsonValue.Create occurredAt

                    node["execution"] <-
                        match successorExecution with
                        | Some id -> JsonValue.Create id :> JsonNode
                        | None -> null

                    node["predecessor"] <-
                        match plan.Predecessor with
                        | Some(id, disposition) ->
                            let predecessor = JsonObject()
                            predecessor["executionId"] <- JsonValue.Create id
                            predecessor["disposition"] <- JsonValue.Create(PredecessorDisposition.code disposition)
                            predecessor["dispositionSource"] <- JsonValue.Create "observed-by-successor"
                            predecessor :> JsonNode
                        | None -> null

                    node["checkpoint"] <-
                        match plan.Checkpoint with
                        | Some recorded -> JsonValue.Create recorded.CheckpointId :> JsonNode
                        | None -> null

                    let others = JsonArray()
                    plan.OtherActiveExecutions |> List.iter (fun id -> others.Add(JsonValue.Create id: JsonNode))
                    node["otherActiveExecutions"] <- others
                    let executions = JsonArray()
                    successorExecution |> Option.iter (fun id -> executions.Add(JsonValue.Create id: JsonNode))
                    node["telemetryExecutions"] <- executions
                    node["actor"] <- ActorJson.node actor
                    let publication = JsonObject()
                    publication["status"] <- JsonValue.Create "pending"
                    node["publication"] <- publication
                    let eventId = CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact node)
                    node["eventId"] <- JsonValue.Create eventId

                    let eventsPath = Path.Combine(root, eventsRelativePath)
                    let current = if File.Exists eventsPath then File.ReadAllText eventsPath else ""
                    let separator = if current.Length > 0 && not (current.EndsWith "\n") then "\n" else ""

                    let writes: WorkStateWrite list =
                        [ { Path = eventsRelativePath
                            Content = $"{current}{separator}{node.ToJsonString compact}\n" }
                          { Path = contextRelativePath
                            Content = context.ToJsonString indented + "\n" } ]

                    match WorkStateTransaction.prepare root writes with
                    | Error failure -> Error $"state persistence failed: {failure.Message}"
                    | Ok() ->
                        match WorkStateTransaction.recover root with
                        | Error failure -> Error $"state persistence failed: {failure.Message}"
                        | Ok() -> Ok eventId
        with error ->
            Error $"state persistence failed: {error.Message}"

    /// The executions of a work item, split by whether this process may
    /// continue them itself: (callerActive, othersActive oldest first,
    /// latest of any status). A process with no identity is refused.
    let executionsForContinuation (root: string) (workItemId: string) (overrides: IdentityInputs) : Result<Actor * string list * string list * string option, string> =
        match FileTelemetryExecutionRepository.resolveIdentity overrides with
        | Error message -> Error message
        | Ok(actor, _, _) when not (ActorResolution.isDeclared actor) ->
            Error
                "continuation needs the successor's own identity; declare yourself (ROS_ACTOR_KIND and ROS_ACTOR, or --actor-kind and --actor). Never continue as another executor"
        | Ok(actor, identity, _) ->
            let executions =
                FileProvenanceRepository.readExecutions root
                |> List.filter (fun view -> view.WorkItemId = workItemId)
                |> List.sortBy _.StartedAt

            let active = executions |> List.filter (fun view -> view.Status = "active")
            let mine, others = active |> List.partition (fun view -> ActorResolution.mayContinue actor identity view.Actor view.Identity)

            Ok(
                actor,
                mine |> List.map _.ExecutionId,
                others |> List.map _.ExecutionId,
                executions |> List.tryLast |> Option.map _.ExecutionId
            )
