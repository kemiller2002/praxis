namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Application.Work
open Ros.Infrastructure.Planning
open Ros.Contracts.Provenance
open Ros.Contracts.Work
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Json
open Ros.Domain.Telemetry

/// One stored group checkpoint as `show` and validation read it.
type GroupCheckpointRead =
    { EventId: string
      IdMatchesContent: bool
      Stored: StoredGroupCheckpoint option }

/// Group checkpoint persistence (PRX-GRP-044): `work.group.checkpointed`
/// events in the append-only log, written through the `work-state` recovery
/// journal. Recording one never touches a member's `latestCheckpoint`, its
/// checkpoint history or its attributed paths. Callers hold the
/// `work-protocol` lock for every write.
[<RequireQualifiedAccess>]
module FileGroupCheckpointRepository =
    let private eventsRelativePath = ".ros/events/events.jsonl"
    let private contextRelativePath = ".ros/context/current.json"
    let private compact = JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    /// A declared group's current members and where they are declared:
    /// stored declarations (`work group create|add|remove`) merged with any
    /// planner configuration, exactly as the planner reads `grouping.groups`.
    let readDeclaredGroup (root: string) (configuration: (string * string) option) (groupId: string) : Result<string list * GroupDeclarationSource, string> =
        let port = FilePlanningRepository.create root None (configuration |> Option.map snd)

        match port.Configuration(), WorkGroupStore.read root with
        | Error message, _
        | _, Error message -> Error message
        | Ok merged, Ok stored ->
            let source =
                if stored |> List.exists (fun entry -> entry.Group.Id = groupId) then
                    GroupDeclarationSource.StoredDeclaration WorkGroupStore.RelativePath
                else
                    GroupDeclarationSource.PlannerConfiguration(configuration |> Option.map fst |> Option.defaultValue "")

            match merged.Grouping.Groups |> List.tryFind (fun group -> group.Id = groupId) with
            | Some group -> Ok(group.Members, source)
            | None ->
                let places =
                    match configuration with
                    | Some(path, _) -> $"{WorkGroupStore.RelativePath} or {path}"
                    | None -> WorkGroupStore.RelativePath

                Error $"group '{groupId}' is not declared in {places}"

    /// Observes each requested member: whether it exists, its own state and
    /// latest checkpoint, and (for active members) the caller's execution.
    let observeMembers (root: string) (overrides: IdentityInputs) (memberIds: string list) : Result<GroupMemberObservation list, string> =
        FileCheckpointRepository.readItems root
        |> Result.map (fun items ->
            let backlog = FileBacklogQueueRepository.readItems root |> List.map _.Id |> Set.ofList

            memberIds
            |> List.map (fun id ->
                let item = items |> List.tryFind (fun candidate -> candidate.WorkItemId = id)
                let state = item |> Option.map _.State

                { WorkItemId = id
                  Known = item.IsSome || backlog.Contains id
                  State = state
                  LatestCheckpoint =
                    match item |> Option.map _.LatestCheckpoint with
                    | Some(Ok latest) -> latest
                    | _ -> None
                  Execution =
                    match state with
                    | Some LiveWorkState.Active -> FileCheckpointRepository.resolveExecution root id overrides None
                    | _ -> ExecutionObservation.NoneActive }))

    /// The content-addressed event: the hash covers every field before
    /// `eventId`. It claims no paths.
    let eventNode (repositoryId: string) (protocolVersion: string) (actor: Actor) (checkpoint: GroupCheckpoint) : JsonObject =
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "1.0.0"
        node["type"] <- JsonValue.Create GroupCheckpointJson.eventType
        node["group"] <- JsonValue.Create checkpoint.GroupId
        node["repository"] <- JsonValue.Create repositoryId
        node["protocolVersion"] <- JsonValue.Create protocolVersion
        node["occurredAt"] <- JsonValue.Create checkpoint.RecordedAt
        node["groupCheckpoint"] <- GroupCheckpointJson.body checkpoint
        node["paths"] <- JsonArray()
        node["telemetryExecutions"] <- GroupCheckpointJson.strings checkpoint.ExecutionIds
        node["actor"] <- ActorJson.node actor
        let publication = JsonObject()
        publication["status"] <- JsonValue.Create "pending"
        node["publication"] <- publication
        node["eventId"] <- JsonValue.Create(CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact node))
        node

    /// Appends the group checkpoint event through the `work-state` journal.
    /// The work context is carried unchanged: no member's projection moves.
    let record (root: string) (actor: Actor) (checkpoint: GroupCheckpoint) : Result<string, string> =
        try
            let contextPath = Path.Combine(root, contextRelativePath)

            if not (File.Exists contextPath) then
                Error "there is no work context to checkpoint"
            else
                let event =
                    eventNode (FileWorkConfigRepository.readRepositoryId root) (FileWorkConfigRepository.readProtocolVersion root) actor checkpoint

                let eventId = text event "eventId" |> Option.defaultValue ""
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
                      { Path = contextRelativePath; Content = File.ReadAllText contextPath } ]

                WorkStateTransaction.prepare root writes
                |> Result.bind (fun () -> WorkStateTransaction.recover root)
                |> Result.map (fun () -> eventId)
                |> Result.mapError (fun failure -> $"state persistence failed: {failure.Message}")
        with error ->
            Error $"state persistence failed: {error.Message}"

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

    let private readAll (root: string) : GroupCheckpointRead list =
        eventNodes root
        |> List.filter GroupCheckpointJson.isGroupCheckpointEvent
        |> List.map (fun node ->
            { EventId = text node "eventId" |> Option.defaultValue ""
              IdMatchesContent = FileCheckpointRepository.eventIdMatches node
              Stored = GroupCheckpointJson.tryRead node })

    /// One group's checkpoint history, oldest first. Never rewritten.
    let readHistory (root: string) (groupId: string) : GroupCheckpointRead list =
        readAll root |> List.filter (fun read -> read.Stored |> Option.exists (fun stored -> stored.GroupId = groupId))

    /// Offline structural validation: every group checkpoint's content
    /// matches its ID, re-checks as accepted, and references only members'
    /// own recorded checkpoints.
    let validationFindings (root: string) : CheckpointFinding list =
        let memberCheckpoints =
            FileCheckpointRepository.readEvents root |> List.map (fun (_, event) -> event.EventId, event.WorkItemId) |> Set.ofList

        let finding eventId message : CheckpointFinding =
            { Path = eventsRelativePath
              Field = $"{GroupCheckpointJson.eventType}:{eventId}"
              Message = message }

        readAll root
        |> List.collect (fun read ->
            [ if not read.IdMatchesContent then
                  finding read.EventId "eventId does not match the event content"
              match read.Stored with
              | None -> finding read.EventId "the event has no groupCheckpoint object"
              | Some stored ->
                  yield! StoredGroupCheckpoint.problems stored |> List.map (finding read.EventId)

                  yield!
                      stored.Members
                      |> List.choose (fun memberItem ->
                          memberItem.CheckpointId
                          |> Option.filter (fun id -> not (memberCheckpoints.Contains(id, memberItem.WorkItemId)))
                          |> Option.map (fun id ->
                              finding read.EventId $"member '{memberItem.WorkItemId}' references checkpoint '{id}', which is not one of its own recorded checkpoints")) ])
