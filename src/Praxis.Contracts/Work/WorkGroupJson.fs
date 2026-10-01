namespace Praxis.Contracts.Work

open System
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Contracts.Planning
open Praxis.Contracts.Provenance
open Praxis.Domain.Planning
open Praxis.Domain.Work

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

    let locationNode (location: GitDurableLocation) : JsonNode =
        record
            [ "repository", text location.Repository
              "branch", text location.Branch
              "commit", text location.LocalCommit.Value
              "remote", (record [ "name", text location.Remote.Name; "url", optionalText location.Remote.Url ] :> JsonNode)
              "remoteBranch", text location.RemoteBranch
              "remoteCommit", text location.RemoteCommit.Value ]

    let checkpointNode (checkpoint: GroupCheckpoint) : JsonNode =
        record
            [ "id", text checkpoint.CheckpointId
              "recordedAt", text checkpoint.RecordedAt
              "actor", (ActorJson.node checkpoint.Actor :> JsonNode)
              "summary", text checkpoint.Summary
              "nextAction", text checkpoint.NextAction
              "decisions", texts checkpoint.Decisions
              "members",
              (record
                  [ "completed", texts checkpoint.Completed
                    "active", texts checkpoint.Active
                    "blocked", texts checkpoint.Blocked
                    "remaining", texts checkpoint.Remaining
                    "abandoned", texts checkpoint.Abandoned ]
               :> JsonNode)
              "memberCheckpoints",
              checkpoint.MemberCheckpoints
              |> List.map (fun reference ->
                  record [ "workItemId", text reference.WorkItemId; "checkpointId", text reference.CheckpointId; "commit", text reference.Commit ] :> JsonNode)
              |> array
              "location", locationNode checkpoint.Location
              "verification", (record [ "status", text "verified"; "mechanism", text "git-remote-observation" ] :> JsonNode) ]

    let groupNode (group: StoredWorkGroup) : JsonObject =
        record
            [ yield! declarationFields group.Declaration
              yield "createdAt", text group.CreatedAt
              yield "createdBy", (ActorJson.node group.CreatedBy :> JsonNode)
              yield "history", group.History |> List.map historyNode |> array
              yield "checkpoints", group.Checkpoints |> List.map checkpointNode |> array ]

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

    let private actor (node: JsonObject) (name: string) : Result<Praxis.Domain.Provenance.Actor, string> =
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

    /// The declaration part of a stored group is read by the planner's own
    /// `grouping.groups` parser, so a stored group and a configured one can
    /// never be read differently.
    let readDeclaration (node: JsonObject) : Result<DeclaredGroup, string list> =
        PlanningJson.parseDeclaredGroup node |> Result.mapError List.singleton

    let private child (node: JsonObject) (name: string) : Result<JsonObject, string> =
        match field node name with
        | Some(:? JsonObject as value) -> Ok value
        | _ -> Error $"{name} must be an object"

    let private commit (node: JsonObject) (name: string) : Result<Praxis.Domain.Git.CommitId, string> =
        requiredText node name
        |> Result.bind (fun raw -> Praxis.Domain.Git.CommitId.tryParse raw |> Option.map Ok |> Option.defaultValue (Error $"{name} '{raw}' is not a full commit ID"))

    let readLocation (node: JsonObject) : Result<GitDurableLocation, string list> =
        let repository = requiredText node "repository"
        let branch = requiredText node "branch"
        let local = commit node "commit"
        let remote = child node "remote"
        let remoteName = remote |> Result.bind (fun value -> requiredText value "name")
        let remoteUrl = remote |> Result.bind (fun value -> optionalString value "url")
        let remoteBranch = requiredText node "remoteBranch"
        let remoteCommit = commit node "remoteCommit"

        match errorsOf [ boxed repository; boxed branch; boxed local; boxed remoteName; boxed remoteUrl; boxed remoteBranch; boxed remoteCommit ] with
        | [] ->
            Ok
                { Repository = value repository
                  Branch = value branch
                  LocalCommit = value local
                  Remote = { Name = value remoteName; Url = value remoteUrl }
                  RemoteBranch = value remoteBranch
                  RemoteCommit = value remoteCommit }
        | errors -> Error(errors |> List.distinct)

    let readCheckpoint (node: JsonObject) : Result<GroupCheckpoint, string list> =
        let id = requiredText node "id"
        let recordedAt = requiredText node "recordedAt"
        let who = actor node "actor"
        let summary = requiredText node "summary"
        let nextAction = requiredText node "nextAction"
        let decisions = stringList node "decisions"
        let members = child node "members"
        let listed name = members |> Result.bind (fun value -> stringList value name)
        let completed, active, blocked, remaining, abandoned = listed "completed", listed "active", listed "blocked", listed "remaining", listed "abandoned"

        let references =
            objects node "memberCheckpoints"
            |> Result.bind (fun entries ->
                entries
                |> List.map (fun entry ->
                    match requiredText entry "workItemId", requiredText entry "checkpointId", requiredText entry "commit" with
                    | Ok workItemId, Ok checkpointId, Ok commitId -> Ok { WorkItemId = workItemId; CheckpointId = checkpointId; Commit = commitId }
                    | _ -> Error "memberCheckpoints entries need workItemId, checkpointId and commit")
                |> List.fold (fun state next -> match state, next with | Ok values, Ok value -> Ok(values @ [ value ]) | Error message, _ | _, Error message -> Error message) (Ok []))

        let location = child node "location" |> Result.mapError List.singleton |> Result.bind readLocation

        let scalar =
            errorsOf [ boxed id; boxed recordedAt; boxed who; boxed summary; boxed nextAction; boxed decisions; boxed completed; boxed active; boxed blocked; boxed remaining; boxed abandoned; boxed references ]
            |> List.distinct

        match scalar, location with
        | [], Ok location ->
            Ok
                { CheckpointId = value id
                  RecordedAt = value recordedAt
                  Actor = value who
                  Summary = value summary
                  NextAction = value nextAction
                  Decisions = value decisions
                  Completed = value completed
                  Active = value active
                  Blocked = value blocked
                  Remaining = value remaining
                  Abandoned = value abandoned
                  MemberCheckpoints = value references
                  Location = location }
        | errors, location -> Error(errors @ (match location with Error problems -> problems | Ok _ -> []))

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

        let checkpoints =
            objects node "checkpoints"
            |> Result.mapError List.singleton
            |> Result.bind (fun entries ->
                let read = entries |> List.map readCheckpoint
                let errors = read |> List.collect (function Error problems -> problems | Ok _ -> [])
                if errors.IsEmpty then Ok(read |> List.choose (function Ok checkpoint -> Some checkpoint | Error _ -> None)) else Error errors)

        let scalarErrors = errorsOf [ boxed createdAt; boxed createdBy ]

        match declaration, history, checkpoints, scalarErrors with
        | Ok declaration, Ok history, Ok checkpoints, [] ->
            Ok
                { Declaration = declaration
                  CreatedAt = value createdAt
                  CreatedBy = value createdBy
                  History = history
                  Checkpoints = checkpoints }
        | _ ->
            let problems result = match result with Error problems -> problems | Ok _ -> []
            Error(problems declaration @ problems history @ problems checkpoints @ scalarErrors)

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

    let memberProgressNode (row: MemberProgress) : JsonNode =
        record
            [ "workItemId", text row.WorkItemId
              "category", text (MemberCategory.code row.Category)
              "state", optionalText row.State
              "planningState", optionalText row.PlanningState
              "waitsOn", texts row.WaitsOn
              "gates", texts row.Gates ]

    /// The partial-completion view (PRX-GRP-042). `complete` is true only
    /// when every member completed on its own evidence.
    let progressNode (progress: GroupProgress) : JsonNode =
        record
            [ "total", integer progress.Members.Length
              "complete", boolean (not progress.Members.IsEmpty && progress.Completed.Length = progress.Members.Length)
              "completed", texts progress.Completed
              "active", texts progress.Active
              "blocked", texts progress.Blocked
              "remaining", texts progress.Remaining
              "abandoned", texts progress.Abandoned
              "unknown", texts progress.Unknown
              "summary", text (GroupProgress.summary progress) ]

    let checkpointRejectionNode (rejection: GroupCheckpointRejection) : JsonNode =
        record [ "code", text (GroupCheckpointRejection.code rejection); "message", text (GroupCheckpointRejection.message rejection) ]
