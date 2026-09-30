namespace Ros.Contracts.Work

open System
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Planning
open Ros.Domain.Work

/// `.ros/work/groups.json`: the one reader and writer of stored work groups
/// (PRX-GRP-073). A group's declaration uses the same field names as
/// `grouping.groups` in planner configuration.
[<RequireQualifiedAccess>]
module WorkGroupJson =
    let schemaVersion = 1

    let private options =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

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

    let record (fields: (string * JsonNode) list) : JsonObject =
        let result = JsonObject()
        fields |> List.iter (fun (name, value) -> result[name] <- value)
        result

    // ---- writing ----

    let declarationFields (declaration: DeclaredGroup) : (string * JsonNode) list =
        [ "id", text declaration.Id
          "members", texts declaration.Members
          "kind", declaration.Kind |> Option.map GroupKind.code |> optionalText
          "origin", text (GroupOrigin.code declaration.Origin)
          "sharedContext", texts declaration.SharedContext
          "executionRepository", optionalText declaration.ExecutionRepository
          "crossRepository", boolean declaration.CrossRepository
          "architectureNotes", texts declaration.ArchitectureNotes ]

    let historyNode (entry: GroupHistoryEntry) : JsonNode =
        record
            [ yield "operation", text (GroupOperation.code entry.Operation)
              yield "member", optionalText entry.Member
              yield "at", text entry.At
              yield "actor", (ActorJson.node entry.Actor :> JsonNode)
              yield "reason", optionalText entry.Reason
              if entry.ExplicitEmpty then
                  yield "explicitEmpty", boolean true ]

    let groupNode (group: StoredWorkGroup) : JsonObject =
        record
            [ yield! declarationFields group.Declaration
              yield "createdAt", text group.CreatedAt
              yield "createdBy", (ActorJson.node group.CreatedBy :> JsonNode)
              yield "history", group.History |> List.map historyNode |> array
              yield "checkpoints", array [] ]

    let storeNode (groups: StoredWorkGroup list) : JsonNode =
        record [ "schemaVersion", integer schemaVersion; "groups", groups |> List.map (groupNode >> fun node -> node :> JsonNode) |> array ]

    let renderStore (groups: StoredWorkGroup list) = render (storeNode groups)

    // ---- reading ----

    let private field (node: JsonObject) (name: string) : JsonNode option = Option.ofObj node[name]

    let private stringValue (node: JsonNode) =
        match node with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private requiredText (node: JsonObject) (name: string) : Result<string, string> =
        match field node name |> Option.bind stringValue with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"{name} must be a non-empty string"

    let private optionalString (node: JsonObject) (name: string) : Result<string option, string> =
        match field node name with
        | None -> Ok None
        | Some value ->
            match stringValue value with
            | Some text -> Ok(Some text)
            | None -> Error $"{name} must be a string or null"

    let private stringList (node: JsonObject) (name: string) : Result<string list, string> =
        match field node name with
        | None -> Ok []
        | Some(:? JsonArray as values) ->
            let parsed = values |> Seq.map (Option.ofObj >> Option.bind stringValue) |> Seq.toList

            if parsed |> List.forall Option.isSome then Ok(parsed |> List.choose id)
            else Error $"{name} must be an array of strings"
        | Some _ -> Error $"{name} must be an array of strings"

    let private flag (node: JsonObject) (name: string) : Result<bool, string> =
        match field node name with
        | None -> Ok false
        | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.True -> Ok true
        | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.False -> Ok false
        | Some _ -> Error $"{name} must be a boolean"

    let private actor (node: JsonObject) (name: string) : Result<Ros.Domain.Provenance.Actor, string> =
        match field node name with
        | None -> Error $"{name} is required"
        | Some value ->
            match ActorJson.tryParse value with
            | Ok(Some parsed) -> Ok parsed
            | Ok None -> Error $"{name} is required"
            | Error message -> Error $"{name}: {message}"

    let private parsedWith (name: string) (parse: string -> 'a option) (value: string option) : Result<'a option, string> =
        match value with
        | None -> Ok None
        | Some raw ->
            match parse raw with
            | Some parsed -> Ok(Some parsed)
            | None -> Error $"{name} '{raw}' is not recognized"

    /// Collects every field problem instead of stopping at the first.
    let private errorsOf (results: Result<obj, string> list) =
        results |> List.choose (function Error message -> Some message | Ok _ -> None)

    let private boxed (result: Result<'a, string>) = result |> Result.map box

    let private value (result: Result<'a, string>) =
        match result with
        | Ok value -> value
        | Error message -> invalidOp message

    let readHistoryEntry (node: JsonObject) : Result<GroupHistoryEntry, string list> =
        let operation = requiredText node "operation" |> Result.bind (fun raw -> parsedWith "operation" GroupOperation.tryParse (Some raw) |> Result.map Option.get)
        let memberId = optionalString node "member"
        let at = requiredText node "at"
        let who = actor node "actor"
        let reason = optionalString node "reason"
        let explicitEmpty = flag node "explicitEmpty"

        match errorsOf [ boxed operation; boxed memberId; boxed at; boxed who; boxed reason; boxed explicitEmpty ] with
        | [] ->
            Ok
                { Operation = value operation
                  Member = value memberId
                  At = value at
                  Actor = value who
                  Reason = value reason
                  ExplicitEmpty = value explicitEmpty }
        | errors -> Error errors

    let private objects (node: JsonObject) (name: string) : Result<JsonObject list, string> =
        match field node name with
        | None -> Ok []
        | Some(:? JsonArray as values) ->
            let parsed = values |> Seq.map (function :? JsonObject as item -> Some item | _ -> None) |> Seq.toList

            if parsed |> List.forall Option.isSome then Ok(parsed |> List.choose id)
            else Error $"{name} must be an array of objects"
        | Some _ -> Error $"{name} must be an array of objects"

    let readDeclaration (node: JsonObject) : Result<DeclaredGroup, string list> =
        let id = requiredText node "id"
        let members = stringList node "members"
        let kind = optionalString node "kind" |> Result.bind (parsedWith "kind" GroupKind.tryParse)
        let origin = optionalString node "origin" |> Result.bind (parsedWith "origin" GroupOrigin.tryParse)
        let sharedContext = stringList node "sharedContext"
        let repository = optionalString node "executionRepository"
        let crossRepository = flag node "crossRepository"
        let notes = stringList node "architectureNotes"

        match errorsOf [ boxed id; boxed members; boxed kind; boxed origin; boxed sharedContext; boxed repository; boxed crossRepository; boxed notes ] with
        | [] ->
            Ok
                { Id = value id
                  Members = value members
                  Kind = value kind
                  Origin = value origin |> Option.defaultValue GroupOrigin.HumanDeclared
                  SharedContext = value sharedContext
                  ExecutionRepository = value repository
                  CrossRepository = value crossRepository
                  ArchitectureNotes = value notes }
        | errors -> Error errors

    /// One stored group, or every problem with it.
    let readGroup (node: JsonObject) : Result<StoredWorkGroup, string list> =
        let declaration = readDeclaration node
        let createdAt = requiredText node "createdAt"
        let createdBy = actor node "createdBy"

        let history =
            objects node "history"
            |> Result.mapError List.singleton
            |> Result.bind (fun entries ->
                let read = entries |> List.map readHistoryEntry
                let errors = read |> List.collect (function Error problems -> problems | Ok _ -> [])
                if errors.IsEmpty then Ok(read |> List.choose (function Ok entry -> Some entry | Error _ -> None)) else Error errors)

        let scalarErrors = errorsOf [ boxed createdAt; boxed createdBy ]

        match declaration, history, scalarErrors with
        | Ok declaration, Ok history, [] ->
            Ok
                { Declaration = declaration
                  CreatedAt = value createdAt
                  CreatedBy = value createdBy
                  History = history }
        | _ ->
            let problems result = match result with Error problems -> problems | Ok _ -> []
            Error(problems declaration @ problems history @ scalarErrors)

    /// A parsed store: each group's own result, keyed by its position and
    /// (when readable) its ID, so validation can report every bad record.
    type StoreRead =
        { Groups: (int * string option * Result<StoredWorkGroup, string list>) list }

    let parseStore (json: string) : Result<StoreRead, string> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as root ->
                match field root "schemaVersion" with
                | Some(:? JsonValue as version) when version.GetValueKind() = JsonValueKind.Number && version.GetValue<int>() = schemaVersion ->
                    match objects root "groups" with
                    | Error message -> Error message
                    | Ok groups ->
                        Ok
                            { Groups =
                                groups
                                |> List.mapi (fun index node -> index, field node "id" |> Option.bind stringValue, readGroup node) }
                | _ -> Error $"schemaVersion must be {schemaVersion}"
            | _ -> Error "the document must be a JSON object"
        with error ->
            Error $"not valid JSON: {error.Message}"

    /// Every group, or the first reason the store cannot be trusted.
    let readStore (json: string) : Result<StoredWorkGroup list, string> =
        parseStore json
        |> Result.bind (fun read ->
            match read.Groups |> List.tryPick (fun (index, id, result) -> match result with Error problems -> Some(index, id, problems) | Ok _ -> None) with
            | Some(index, id, problems) ->
                let name = id |> Option.defaultValue $"groups[{index}]"
                let joined = String.concat "; " problems
                Error $"group {name} is invalid: {joined}"
            | None -> Ok(read.Groups |> List.choose (fun (_, _, result) -> match result with Ok group -> Some group | Error _ -> None)))

    // ---- command views ----

    let rejectionNode (rejection: GroupRejection) : JsonNode =
        record [ "code", text (GroupRejection.code rejection); "message", text (GroupRejection.message rejection) ]
