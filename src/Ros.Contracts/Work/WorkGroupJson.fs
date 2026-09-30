namespace Ros.Contracts.Work

open System
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Planning
open Ros.Domain.Work

/// The `.ros/work/groups.json` document and the `work group` command
/// envelope (analysis §4, §11). One codec for every member command.
[<RequireQualifiedAccess>]
module WorkGroupJson =
    let schemaVersion = "1.0.0"

    let options =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    exception private Malformed of string

    let private fail message = raise (Malformed message)

    let private textOf (path: string) (node: JsonNode) =
        match node with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> value.GetValue<string>()
        | _ -> fail $"{path} must be a string"

    let private optionalText (node: JsonObject) (name: string) (path: string) =
        match node[name] with
        | null -> None
        | value -> Some(textOf $"{path}.{name}" value)

    let private requiredText (node: JsonObject) (name: string) (path: string) =
        optionalText node name path |> Option.defaultWith (fun () -> fail $"{path}.{name} is required")

    let private texts (node: JsonObject) (name: string) (path: string) =
        match node[name] with
        | null -> []
        | :? JsonArray as values -> values |> Seq.mapi (fun index value -> textOf $"{path}.{name}[{index}]" value) |> Seq.toList
        | _ -> fail $"{path}.{name} must be an array"

    let private objects (node: JsonObject) (name: string) (path: string) =
        match node[name] with
        | null -> []
        | :? JsonArray as values ->
            values
            |> Seq.mapi (fun index value ->
                match value with
                | :? JsonObject as item -> item, $"{path}.{name}[{index}]"
                | _ -> fail $"{path}.{name}[{index}] must be an object")
            |> Seq.toList
        | _ -> fail $"{path}.{name} must be an array"

    let private boolean (node: JsonObject) (name: string) (path: string) =
        match node[name] with
        | null -> false
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.True -> true
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.False -> false
        | _ -> fail $"{path}.{name} must be a boolean"

    let private actor (node: JsonObject) (name: string) (path: string) =
        match ActorJson.tryParse node[name] with
        | Ok(Some value) -> value
        | Ok None -> fail $"{path}.{name} is required"
        | Error message -> fail $"{path}.{name}: {message}"

    let private parsedWith (describe: string) (parse: string -> 'a option) (path: string) (value: string) =
        parse value |> Option.defaultWith (fun () -> fail $"{path} has unknown {describe} '{value}'")

    let private historyEntry (node: JsonObject, path: string) : GroupHistoryEntry =
        { Operation = requiredText node "operation" path |> parsedWith "operation" GroupHistoryOperation.tryParse $"{path}.operation"
          WorkItemIds = texts node "workItemIds" path
          At = requiredText node "at" path
          Actor = actor node "actor" path
          Reason = optionalText node "reason" path }

    let private group (node: JsonObject, path: string) : StoredGroup =
        { Id = requiredText node "id" path
          Kind = optionalText node "kind" path |> Option.map (parsedWith "group kind" GroupKind.tryParse $"{path}.kind")
          Origin =
            optionalText node "origin" path
            |> Option.map (parsedWith "group origin" GroupOrigin.tryParse $"{path}.origin")
            |> Option.defaultValue GroupOrigin.HumanDeclared
          ExecutionRepository = requiredText node "executionRepository" path
          CrossRepository = boolean node "crossRepository" path
          SharedContext = texts node "sharedContext" path
          ArchitectureNotes = texts node "architectureNotes" path
          Members = texts node "members" path
          CreatedAt = requiredText node "createdAt" path
          CreatedBy = actor node "createdBy" path
          History = objects node "history" path |> List.map historyEntry }

    /// Parses the stored document; a malformed document is an error, never
    /// silently "no groups".
    let parse (json: string) : Result<StoredGroups, string> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as root ->
                match optionalText root "schemaVersion" "groups.json" with
                | Some version when version <> schemaVersion -> Error $"unsupported groups.json schemaVersion '{version}' (expected {schemaVersion})"
                | None -> Error "groups.json schemaVersion is required"
                | Some _ ->
                    Ok
                        { Repository = requiredText root "repository" "groups.json"
                          Groups = objects root "groups" "groups.json" |> List.map group }
            | _ -> Error "groups.json must be a JSON object"
        with
        | Malformed message -> Error $"malformed groups.json: {message}"
        | :? JsonException as error -> Error $"malformed groups.json: {error.Message}"

    let private array (values: JsonNode seq) =
        let result = JsonArray()
        values |> Seq.iter result.Add
        result

    let textArray (values: string list) = values |> Seq.map (fun value -> JsonValue.Create value :> JsonNode) |> array

    let private historyNode (entry: GroupHistoryEntry) =
        let node = JsonObject()
        node["operation"] <- JsonValue.Create(GroupHistoryOperation.code entry.Operation)
        node["workItemIds"] <- textArray entry.WorkItemIds
        node["at"] <- JsonValue.Create entry.At
        node["actor"] <- ActorJson.node entry.Actor
        entry.Reason |> Option.iter (fun reason -> node["reason"] <- JsonValue.Create reason)
        node

    let groupNode (value: StoredGroup) : JsonObject =
        let node = JsonObject()
        node["id"] <- JsonValue.Create value.Id
        value.Kind |> Option.iter (fun kind -> node["kind"] <- JsonValue.Create(GroupKind.code kind))
        node["origin"] <- JsonValue.Create(GroupOrigin.code value.Origin)
        node["executionRepository"] <- JsonValue.Create value.ExecutionRepository
        node["crossRepository"] <- JsonValue.Create value.CrossRepository
        node["sharedContext"] <- textArray value.SharedContext
        node["architectureNotes"] <- textArray value.ArchitectureNotes
        node["members"] <- textArray value.Members
        node["createdAt"] <- JsonValue.Create value.CreatedAt
        node["createdBy"] <- ActorJson.node value.CreatedBy
        node["history"] <- value.History |> Seq.map (fun entry -> historyNode entry :> JsonNode) |> array
        node

    let render (stored: StoredGroups) : string =
        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create schemaVersion
        root["repository"] <- JsonValue.Create stored.Repository
        root["groups"] <- stored.Groups |> Seq.map (fun value -> groupNode value :> JsonNode) |> array
        root.ToJsonString options + "\n"

    // ---- the command envelope ----

    let envelope (command: string) (groupId: string) (status: string) =
        let document = JsonObject()
        document["command"] <- JsonValue.Create $"work group {command}"
        document["schemaVersion"] <- JsonValue.Create 1
        document["groupId"] <- JsonValue.Create groupId
        document["status"] <- JsonValue.Create status
        document

    let rejectionNode (code: string) (message: string) (remedy: string) =
        let node = JsonObject()
        node["code"] <- JsonValue.Create code
        node["message"] <- JsonValue.Create message
        node["remedy"] <- JsonValue.Create remedy
        node

    let groupRejectionNode (rejection: GroupRejection) =
        rejectionNode (GroupRejection.code rejection) (GroupRejection.message rejection) (GroupRejection.remedy rejection)

    let failureNode (message: string) =
        let node = JsonObject()
        node["code"] <- JsonValue.Create "persistence-failed"
        node["message"] <- JsonValue.Create message
        node
