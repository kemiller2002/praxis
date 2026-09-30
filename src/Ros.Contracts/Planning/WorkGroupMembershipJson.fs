namespace Ros.Contracts.Planning

open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Planning

/// `.ros/work/group-membership.json`, the durable ledger of members added to
/// declared groups (who, when, why), and the `work group add` outcome
/// document (`praxis.work-group/1.0.0`, `kind` `work-group-add`).
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

    let renderLedger (additions: MemberAddition list) : string =
        let document = JsonObject()
        document["schemaVersion"] <- JsonValue.Create LedgerSchemaVersion
        document["changes"] <- JsonArray(additions |> List.map (fun addition -> additionNode addition :> JsonNode) |> List.toArray)
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

    let private parseAddition (index: int) (value: JsonNode) : Result<MemberAddition, string> =
        let at message = $"changes[{index}]: {message}"

        match value with
        | :? JsonObject as node ->
            text node "change"
            |> Result.bind (function
                | "added" -> Ok()
                | other -> Error $"unknown change '{other}'")
            |> Result.bind (fun () -> text node "group")
            |> Result.bind (fun group ->
                text node "workItem"
                |> Result.bind (fun workItem ->
                    text node "addedAt"
                    |> Result.bind (fun addedAt ->
                        ActorJson.tryParse node["addedBy"]
                        |> Result.bind (function
                            | Some actor -> Ok actor
                            | None -> Error "addedBy is required")
                        |> Result.bind (fun actor ->
                            optional node "reason"
                            |> Result.map (fun reason ->
                                ({ GroupId = group
                                   WorkItem = workItem
                                   AddedAt = addedAt
                                   AddedBy = actor
                                   Reason = reason }
                                : MemberAddition))))))
            |> Result.mapError at
        | _ -> Error(at "must be an object")

    let parseLedger (content: string) : Result<MemberAddition list, string> =
        try
            match JsonNode.Parse content with
            | :? JsonObject as document ->
                match text document "schemaVersion", document["changes"] with
                | Ok LedgerSchemaVersion, (:? JsonArray as changes) ->
                    changes
                    |> Seq.toList
                    |> List.mapi parseAddition
                    |> List.fold
                        (fun state parsed ->
                            match state, parsed with
                            | Ok found, Ok addition -> Ok(found @ [ addition ])
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
