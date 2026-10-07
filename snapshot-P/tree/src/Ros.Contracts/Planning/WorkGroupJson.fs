namespace Ros.Contracts.Planning

open System
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Git
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work

/// The stored form of declared work groups (`.ros/work/groups.json`) and the
/// JSON views the `work group` commands print. Keys are written in a fixed
/// order so a group checkpoint's content-addressed ID is stable.
[<RequireQualifiedAccess>]
module WorkGroupJson =
    let schemaVersion = "1.0.0"

    let private options =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let render (node: JsonNode) = node.ToJsonString(options) + "\n"

    let text (value: string) : JsonNode = JsonValue.Create value
    let optionalText (value: string option) : JsonNode = value |> Option.map text |> Option.toObj
    let boolean (value: bool) : JsonNode = JsonValue.Create value
    let integer (value: int) : JsonNode = JsonValue.Create value

    let array (values: JsonNode list) : JsonNode =
        let result = JsonArray()
        values |> List.iter result.Add
        result

    let texts (values: string list) = values |> List.map text |> array

    let record (fields: (string * JsonNode) list) : JsonNode =
        let result = JsonObject()
        fields |> List.iter (fun (name, value) -> result[name] <- value)
        result

    let actor (value: Actor) : JsonNode = ActorJson.node value

    // ---- writing ----

    let private membership (entry: GroupMembership) =
        record
            [ "workItemId", text entry.WorkItemId
              "executionRepository", text entry.ExecutionRepository
              "addedAt", text entry.AddedAt
              "addedBy", actor entry.AddedBy
              "reason", optionalText entry.Reason ]

    let private historyEntry (entry: GroupHistoryEntry) =
        record
            [ "change", text (GroupChange.code entry.Change)
              "subject", optionalText (GroupChange.subject entry.Change)
              "occurredAt", text entry.OccurredAt
              "actor", actor entry.Actor
              "reason", optionalText entry.Reason ]

    let location (value: GitDurableLocation) =
        record
            [ "repository", text value.Repository
              "branch", text value.Branch
              "commit", text value.LocalCommit.Value
              "remote", record [ "name", text value.Remote.Name; "url", optionalText value.Remote.Url ]
              "remoteBranch", text value.RemoteBranch
              "remoteCommit", text value.RemoteCommit.Value ]

    let private checkpointFields (checkpoint: GroupCheckpoint) =
        [ "recordedAt", text checkpoint.RecordedAt
          "actor", actor checkpoint.Actor
          "location", location checkpoint.Location
          "verification", record [ "status", text "verified"; "mechanism", text (DurabilityMechanism.code DurabilityMechanism.GitRemoteObservation) ]
          "summary", text checkpoint.Summary
          "nextAction", text checkpoint.NextAction
          "sharedDecisions", texts checkpoint.SharedDecisions
          "members",
          record
              [ "active", texts checkpoint.Active
                "completed", texts checkpoint.Completed
                "abandoned", texts checkpoint.Abandoned
                "remaining", texts checkpoint.Remaining ]
          "memberCheckpoints",
          checkpoint.MemberCheckpoints
          |> List.map (fun reference -> record [ "workItemId", text reference.WorkItemId; "checkpointId", optionalText reference.CheckpointId ])
          |> array
          // A group checkpoint attributes no path to any member (PRX-GRP-043).
          "paths", array [] ]

    /// Every field of a group checkpoint except its ID, in ID-hashing order.
    let checkpointBody (checkpoint: GroupCheckpoint) = record (checkpointFields checkpoint)

    let checkpoint (value: GroupCheckpoint) = record (("id", text value.Id) :: checkpointFields value)

    let group (value: StoredWorkGroup) : JsonNode =
        record
            [ "id", text value.Id
              "kind", optionalText (value.Kind |> Option.map GroupKind.code)
              "origin", text (GroupOrigin.code value.Origin)
              "executionRepository", text value.ExecutionRepository
              "crossRepository", boolean value.CrossRepository
              "sharedContext", texts value.SharedContext
              "architectureNotes", texts value.ArchitectureNotes
              "createdAt", text value.CreatedAt
              "createdBy", actor value.CreatedBy
              "members", value.Members |> List.map membership |> array
              "history", value.History |> List.map historyEntry |> array
              "checkpoints", value.Checkpoints |> List.map checkpoint |> array ]

    let document (groups: StoredWorkGroup list) : JsonNode =
        record
            [ "schemaVersion", text schemaVersion
              "groups",
              groups
              |> List.sortWith (fun left right -> String.CompareOrdinal(left.Id, right.Id))
              |> List.map group
              |> array ]

    // ---- reading ----

    exception private Malformed of string

    let private fail message = raise (Malformed message)

    let private asObject (where: string) (node: JsonNode) =
        match node with
        | :? JsonObject as value -> value
        | _ -> fail $"{where} must be an object"

    let private textOf (where: string) (node: JsonNode) =
        match node with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> value.GetValue<string>()
        | _ -> fail $"{where} must be a string"

    let private readText (node: JsonObject) (name: string) = textOf name node[name]

    let private readOptionalText (node: JsonObject) (name: string) =
        match node[name] with
        | null -> None
        | value -> Some(textOf name value)

    let private readBool (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.True -> true
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.False -> false
        | _ -> fail $"{name} must be a boolean"

    let private readArray (node: JsonObject) (name: string) =
        match node[name] with
        | null -> []
        | :? JsonArray as values -> values |> Seq.toList
        | _ -> fail $"{name} must be an array"

    let private readTexts (node: JsonObject) (name: string) = readArray node name |> List.map (textOf name)
    let private readObjects (node: JsonObject) (name: string) = readArray node name |> List.map (asObject name)

    let private readActor (node: JsonObject) (name: string) =
        match ActorJson.tryParse node[name] with
        | Ok(Some value) -> value
        | Ok None -> fail $"{name} is required"
        | Error message -> fail $"{name}: {message}"

    let private readCommit (node: JsonObject) (name: string) =
        let value = readText node name

        match CommitId.tryParse value with
        | Some commit -> commit
        | None -> fail $"{name} '{value}' is not a full commit ID"

    let private readLocation (node: JsonObject) : GitDurableLocation =
        let remote = asObject "remote" node["remote"]

        { Repository = readText node "repository"
          Branch = readText node "branch"
          LocalCommit = readCommit node "commit"
          Remote = { Name = readText remote "name"; Url = readOptionalText remote "url" }
          RemoteBranch = readText node "remoteBranch"
          RemoteCommit = readCommit node "remoteCommit" }

    let private readCheckpoint (node: JsonObject) : GroupCheckpoint =
        let members = asObject "members" node["members"]

        if not (readArray node "paths").IsEmpty then
            fail "a group checkpoint must not attribute paths"

        { Id = readText node "id"
          RecordedAt = readText node "recordedAt"
          Actor = readActor node "actor"
          Location = readLocation (asObject "location" node["location"])
          Summary = readText node "summary"
          NextAction = readText node "nextAction"
          SharedDecisions = readTexts node "sharedDecisions"
          Active = readTexts members "active"
          Completed = readTexts members "completed"
          Abandoned = readTexts members "abandoned"
          Remaining = readTexts members "remaining"
          MemberCheckpoints =
            readObjects node "memberCheckpoints"
            |> List.map (fun reference ->
                { WorkItemId = readText reference "workItemId"
                  CheckpointId = readOptionalText reference "checkpointId" }) }

    let private readGroup (node: JsonObject) : StoredWorkGroup =
        let parsed what parse value =
            match parse value with
            | Some result -> result
            | None -> fail $"unknown {what} '{value}'"

        { Id = readText node "id"
          Kind = readOptionalText node "kind" |> Option.map (parsed "group kind" GroupKind.tryParse)
          Origin = readText node "origin" |> parsed "group origin" GroupOrigin.tryParse
          ExecutionRepository = readText node "executionRepository"
          CrossRepository = readBool node "crossRepository"
          SharedContext = readTexts node "sharedContext"
          ArchitectureNotes = readTexts node "architectureNotes"
          CreatedAt = readText node "createdAt"
          CreatedBy = readActor node "createdBy"
          Members =
            readObjects node "members"
            |> List.map (fun entry ->
                { WorkItemId = readText entry "workItemId"
                  ExecutionRepository = readText entry "executionRepository"
                  AddedAt = readText entry "addedAt"
                  AddedBy = readActor entry "addedBy"
                  Reason = readOptionalText entry "reason" })
          History =
            readObjects node "history"
            |> List.map (fun entry ->
                let change = readText entry "change"
                let subject = readOptionalText entry "subject"

                { Change =
                    match GroupChange.tryParse change subject with
                    | Some value -> value
                    | None -> fail $"history change '{change}' is malformed"
                  OccurredAt = readText entry "occurredAt"
                  Actor = readActor entry "actor"
                  Reason = readOptionalText entry "reason" })
          Checkpoints = readObjects node "checkpoints" |> List.map readCheckpoint }

    /// Every stored group, or why the document cannot be trusted.
    let parse (json: string) : Result<StoredWorkGroup list, string> =
        try
            let root = JsonNode.Parse json |> asObject "groups document"

            match readOptionalText root "schemaVersion" with
            | Some version when version = schemaVersion ->
                readObjects root "groups"
                |> List.mapi (fun index node ->
                    try
                        readGroup node
                    with Malformed message ->
                        let id = match node["id"] with :? JsonValue as value -> value.ToString() | _ -> $"#{index}"
                        fail $"group {id}: {message}")
                |> Ok
            | Some version -> Error $"unsupported groups schemaVersion '{version}'"
            | None -> Error "groups document has no schemaVersion"
        with
        | Malformed message -> Error message
        | :? JsonException as error -> Error error.Message

    // ---- command views ----

    let rejection (value: GroupRejection) : JsonNode =
        let node =
            match value with
            | GroupRejection.Checkpoint inner -> Ros.Contracts.Work.CheckpointJson.rejectionNode inner
            | _ ->
                let result = JsonObject()
                result["code"] <- text (GroupRejection.code value)
                result["message"] <- text (GroupRejection.message value)
                result

        node

    let rejections (values: GroupRejection list) = values |> List.map rejection |> array

    let progress (value: StoredGroupProgress) =
        record
            [ "members", integer value.Members
              "complete", integer value.Complete
              "abandoned", integer value.Abandoned
              "active", integer value.Active
              "blocked", integer value.Blocked
              "remaining", integer value.Remaining
              "noLongerTracked", integer value.Unknown
              "statement", text (GroupView.describeProgress value) ]

    let private memberView (value: GroupMemberView) =
        record
            [ "workItemId", text value.Membership.WorkItemId
              "recordedState", optionalText (value.RecordedState |> Option.map RecordedWorkState.code)
              "planningState", optionalText (value.PlanningState |> Option.map PlanningWorkState.code)
              "blockReason", optionalText value.BlockReason
              "latestCheckpointId", optionalText value.LatestCheckpointId
              "gates", texts value.Gates
              "executionRepository", text value.Membership.ExecutionRepository
              "addedAt", text value.Membership.AddedAt
              "addedBy", actor value.Membership.AddedBy
              "reason", optionalText value.Membership.Reason ]

    /// `work group show --json`.
    let view (value: GroupView) =
        let group = value.Group

        record
            [ "command", text "work group show"
              "schemaVersion", integer 1
              "groupId", text group.Id
              "kind", optionalText (group.Kind |> Option.map GroupKind.code)
              "origin", text (GroupOrigin.code group.Origin)
              "executionRepository", text group.ExecutionRepository
              "crossRepository", boolean group.CrossRepository
              "sharedContext", texts group.SharedContext
              "architectureNotes", texts group.ArchitectureNotes
              "createdAt", text group.CreatedAt
              "createdBy", actor group.CreatedBy
              "planningAvailable", boolean value.PlanningAvailable
              "progress", progress value.Progress
              "members", value.Members |> List.map memberView |> array
              "blocked",
              GroupView.blocked value
              |> List.map (fun blocked -> record [ "workItemId", text blocked.Membership.WorkItemId; "blockReason", optionalText blocked.BlockReason; "gates", texts blocked.Gates ])
              |> array
              "latestCheckpoint", value.LatestCheckpoint |> Option.map checkpoint |> Option.toObj
              "history", group.History |> List.map historyEntry |> array ]
