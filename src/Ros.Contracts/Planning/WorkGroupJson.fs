namespace Ros.Contracts.Planning

open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Planning

/// `.ros/work/groups.json`: durable human-declared execution groups
/// (PRX-GRP-073 phase two). Each entry is a `grouping.groups` entry plus who
/// declared it and when; the group part is read by the same parser as
/// planner configuration.
[<RequireQualifiedAccess>]
module WorkGroupJson =
    [<Literal>]
    let StoreSchema = "praxis.work-groups/1.0.0"

    [<Literal>]
    let OutcomeSchema = "praxis.work-group/1.0.0"

    let private options =
        JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private render (node: JsonNode) = node.ToJsonString(options) + "\n"

    let private text (value: string) : JsonNode = JsonValue.Create value

    let private record (fields: (string * JsonNode) list) : JsonNode =
        fields
        |> List.fold
            (fun (result: JsonObject) (name, value) ->
                result[name] <- value
                result)
            (JsonObject())
        :> JsonNode

    let private array (values: JsonNode list) : JsonNode = JsonArray(values |> List.toArray) :> JsonNode

    let entryNode (entry: StoredGroupDeclaration) : JsonNode =
        let node = PlanningJson.declaredGroupNode entry.Group :?> JsonObject
        node["declaredAt"] <- text entry.DeclaredAt
        node["declaredBy"] <- ActorJson.node entry.DeclaredBy
        node :> JsonNode

    let renderStore (entries: StoredGroupDeclaration list) : string =
        record [ "schema", text StoreSchema; "groups", entries |> List.map entryNode |> array ] |> render

    let private stringField (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Ok(value.GetValue<string>())
        | _ -> Error $"{name} must be a string"

    let private entry (index: int) (node: JsonNode) : Result<StoredGroupDeclaration, string> =
        let located (result: Result<'a, string>) = result |> Result.mapError (fun message -> $"groups[{index}]: {message}")

        match node with
        | :? JsonObject as value ->
            PlanningJson.parseDeclaredGroup value
            |> Result.bind (fun group ->
                stringField value "declaredAt"
                |> Result.bind (fun declaredAt ->
                    match ActorJson.tryParse value["declaredBy"] with
                    | Ok(Some actor) -> Ok { Group = group; DeclaredAt = declaredAt; DeclaredBy = actor }
                    | Ok None -> Error "declaredBy is required"
                    | Error message -> Error $"declaredBy: {message}"))
            |> located
        | _ -> Error $"groups[{index}] must be an object"

    let private collect (results: Result<'a, string> list) : Result<'a list, string> =
        List.foldBack
            (fun result accumulated ->
                match result, accumulated with
                | Ok value, Ok values -> Ok(value :: values)
                | Error message, _ -> Error message
                | Ok _, Error message -> Error message)
            results
            (Ok [])

    let parseStore (json: string) : Result<StoredGroupDeclaration list, string> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as root ->
                match root["schema"], root["groups"] with
                | :? JsonValue as schema, _ when schema.GetValueKind() = JsonValueKind.String && schema.GetValue<string>() <> StoreSchema ->
                    Error $"unsupported schema '{schema.GetValue<string>()}'; expected {StoreSchema}"
                | _, (:? JsonArray as groups) -> groups |> Seq.toList |> List.mapi entry |> collect
                | _ -> Error "groups must be an array"
            | _ -> Error "the document must be an object"
        with :? JsonException as error ->
            Error error.Message

    /// The `work group create` result: `created`, `planned` (dry run) or
    /// `rejected`, with every rejection listed.
    let renderOutcome (status: string) (path: string) (entry: StoredGroupDeclaration) (rejections: GroupDeclarationRejection list) : string =
        record
            [ "schema", text OutcomeSchema
              "kind", text "work-group-create"
              "status", text status
              "path", text path
              "group", entryNode entry
              "rejections", rejections |> List.map (GroupDeclarationRejection.message >> text) |> array
              "lifecycleChanged", JsonValue.Create false :> JsonNode ]
        |> render
