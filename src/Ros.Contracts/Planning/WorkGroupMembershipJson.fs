namespace Ros.Contracts.Planning

open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Planning
open Ros.Domain.Provenance

/// `.ros/work/group-membership.json`, the durable ledger of members added to
/// and removed from declared groups (who, when, why), and the `work group
/// add` and `work group remove` outcome documents (`praxis.work-group/1.0.0`,
/// `kind` `work-group-add` or `work-group-remove`).
[<RequireQualifiedAccess>]
module WorkGroupMembershipJson =
    [<Literal>]
    let LedgerSchemaVersion = "1.0.0"

    [<Literal>]
    let OutcomeSchema = "praxis.work-group/1.0.0"

    let private options = JsonSerializerOptions(WriteIndented = true)

    let private optionalText (value: string option) : JsonNode =
        value |> Option.map (fun text -> JsonValue.Create text :> JsonNode) |> Option.toObj

    let private texts (values: string list) =
        JsonArray(values |> List.map (fun value -> JsonValue.Create value :> JsonNode) |> List.toArray)

    let additionNode (addition: MemberAddition) : JsonObject =
        let node = JsonObject()
        node["change"] <- JsonValue.Create "added"
        node["group"] <- JsonValue.Create addition.GroupId
        node["workItem"] <- JsonValue.Create addition.WorkItem
        node["addedAt"] <- JsonValue.Create addition.AddedAt
        node["addedBy"] <- ActorJson.node addition.AddedBy
        node["reason"] <- optionalText addition.Reason
        node

    let removalNode (removal: MemberRemoval) : JsonObject =
        let node = JsonObject()
        node["change"] <- JsonValue.Create "removed"
        node["group"] <- JsonValue.Create removal.GroupId
        node["workItem"] <- JsonValue.Create removal.WorkItem
        node["removedAt"] <- JsonValue.Create removal.RemovedAt
        node["removedBy"] <- ActorJson.node removal.RemovedBy
        node["reason"] <- optionalText removal.Reason
        node

    let changeNode (change: MembershipChange) : JsonObject =
        match change with
        | MembershipChange.Added addition -> additionNode addition
        | MembershipChange.Removed removal -> removalNode removal

    let renderLedger (changes: MembershipChange list) : string =
        let document = JsonObject()
        document["schemaVersion"] <- JsonValue.Create LedgerSchemaVersion
        document["changes"] <- JsonArray(changes |> List.map (fun change -> changeNode change :> JsonNode) |> List.toArray)
        document.ToJsonString options + "\n"

    let private text (node: JsonObject) (name: string) : Result<string, string> =
        match node[name] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, found -> Ok found
            | _ -> Error $"{name} must be a string"
        | _ -> Error $"{name} is required"

    let private optional (node: JsonObject) (name: string) : Result<string option, string> =
        match node[name] with
        | null -> Ok None
        | _ -> text node name |> Result.map Some

    let private actorField (node: JsonObject) (name: string) : Result<Actor, string> =
        ActorJson.tryParse node[name]
        |> Result.bind (function
            | Some actor -> Ok actor
            | None -> Error $"{name} is required")

    /// The fields every change shares: group, work item, when, who and why.
    let private changeFields (node: JsonObject) (atField: string) (byField: string) =
        text node "group"
        |> Result.bind (fun group ->
            text node "workItem"
            |> Result.bind (fun workItem ->
                text node atField
                |> Result.bind (fun at ->
                    actorField node byField
                    |> Result.bind (fun actor -> optional node "reason" |> Result.map (fun reason -> group, workItem, at, actor, reason)))))

    let private parseChange (index: int) (value: JsonNode) : Result<MembershipChange, string> =
        let at message = $"changes[{index}]: {message}"

        match value with
        | :? JsonObject as node ->
            text node "change"
            |> Result.bind (function
                | "added" ->
                    changeFields node "addedAt" "addedBy"
                    |> Result.map (fun (group, workItem, addedAt, actor, reason) ->
                        MembershipChange.Added
                            { GroupId = group
                              WorkItem = workItem
                              AddedAt = addedAt
                              AddedBy = actor
                              Reason = reason })
                | "removed" ->
                    changeFields node "removedAt" "removedBy"
                    |> Result.map (fun (group, workItem, removedAt, actor, reason) ->
                        MembershipChange.Removed
                            { GroupId = group
                              WorkItem = workItem
                              RemovedAt = removedAt
                              RemovedBy = actor
                              Reason = reason })
                | other -> Error $"unknown change '{other}'")
            |> Result.mapError at
        | _ -> Error(at "must be an object")

    let parseLedger (content: string) : Result<MembershipChange list, string> =
        try
            match JsonNode.Parse content with
            | :? JsonObject as document ->
                match text document "schemaVersion", document["changes"] with
                | Ok LedgerSchemaVersion, (:? JsonArray as changes) ->
                    changes
                    |> Seq.toList
                    |> List.mapi parseChange
                    |> List.fold
                        (fun state parsed ->
                            match state, parsed with
                            | Ok found, Ok change -> Ok(found @ [ change ])
                            | Error message, _ -> Error message
                            | _, Error message -> Error message)
                        (Ok [])
                | Ok other, _ when other <> LedgerSchemaVersion -> Error $"unsupported schemaVersion '{other}'"
                | Error message, _ -> Error message
                | _ -> Error "changes must be an array"
            | _ -> Error "the ledger must be a JSON object"
        with :? JsonException as error ->
            Error error.Message

    /// The `work group add` outcome: `status` `added`, `planned` or `rejected`.
    let renderOutcome
        (status: string)
        (paths: string list)
        (addition: MemberAddition)
        (members: string list)
        (rejections: GroupAdditionRejection list)
        : string =
        let document = JsonObject()
        document["schema"] <- JsonValue.Create OutcomeSchema
        document["kind"] <- JsonValue.Create "work-group-add"
        document["status"] <- JsonValue.Create status
        document["group"] <- JsonValue.Create addition.GroupId
        document["workItem"] <- JsonValue.Create addition.WorkItem
        document["members"] <- texts members
        document["addition"] <- additionNode addition
        document["paths"] <- texts paths
        document["lifecycleChanged"] <- JsonValue.Create false

        document["rejections"] <-
            JsonArray(
                rejections
                |> List.map (fun rejection ->
                    let node = JsonObject()
                    node["code"] <- JsonValue.Create(GroupAdditionRejection.code rejection)
                    node["message"] <- JsonValue.Create(GroupAdditionRejection.message rejection)
                    node :> JsonNode)
                |> List.toArray
            )

        document.ToJsonString options + "\n"

    /// The `work group remove` outcome: `status` `removed`, `planned` or
    /// `rejected`.
    let renderRemovalOutcome
        (status: string)
        (paths: string list)
        (removal: MemberRemoval)
        (members: string list)
        (rejections: GroupRemovalRejection list)
        : string =
        let document = JsonObject()
        document["schema"] <- JsonValue.Create OutcomeSchema
        document["kind"] <- JsonValue.Create "work-group-remove"
        document["status"] <- JsonValue.Create status
        document["group"] <- JsonValue.Create removal.GroupId
        document["workItem"] <- JsonValue.Create removal.WorkItem
        document["members"] <- texts members
        document["removal"] <- removalNode removal
        document["paths"] <- texts paths
        document["lifecycleChanged"] <- JsonValue.Create false

        document["rejections"] <-
            JsonArray(
                rejections
                |> List.map (fun rejection ->
                    let node = JsonObject()
                    node["code"] <- JsonValue.Create(GroupRemovalRejection.code rejection)
                    node["message"] <- JsonValue.Create(GroupRemovalRejection.message rejection)
                    node :> JsonNode)
                |> List.toArray
            )

        document.ToJsonString options + "\n"
