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
    /// Version 2 (PRX-GRP-112) adds fields; version 1 is still read, and a
    /// version-1 store is rewritten only by a mutation, history intact.
    let schemaVersion = 2

    let readableVersions = set [ 1; 2 ]

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
          "architectureNotes", texts declaration.ArchitectureNotes
          if declaration.IndependentReason.IsSome then
              "executionMode", text "independent"
              "executionModeReason", text declaration.IndependentReason.Value
          if not declaration.IndependentMembers.IsEmpty then
              "independentMembers",
              declaration.IndependentMembers
              |> List.map (fun (memberId, reason) -> record [ "workItem", text memberId; "reason", text reason ] :> JsonNode)
              |> array ]

    let historyNode (entry: GroupHistoryEntry) : JsonNode =
        record
            [ yield "operation", text (GroupOperation.code entry.Operation)
              yield "member", optionalText entry.Member
              yield "at", text entry.At
              yield "actor", (ActorJson.node entry.Actor :> JsonNode)
              yield "reason", optionalText entry.Reason
              if entry.ExplicitEmpty then
                  yield "explicitEmpty", boolean true
              yield "executionId", optionalText entry.ExecutionId
              yield "memberState", optionalText entry.MemberState ]

    let locationNode (location: GitDurableLocation) : JsonNode =
        record
            [ "repository", text location.Repository
              "branch", text location.Branch
              "commit", text location.LocalCommit.Value
              "remote", (record [ "name", text location.Remote.Name; "url", optionalText location.Remote.Url ] :> JsonNode)
              "remoteBranch", text location.RemoteBranch
              "remoteCommit", text location.RemoteCommit.Value ]

    /// A dated observation of a member repository (PRX-GRP-103). Labelled
    /// an observation: never a verification of another repository.
    let observationNode (observation: MemberObservation) : JsonNode =
        record
            [ yield "member", text observation.Member
              yield "repository", text observation.Repository
              yield "workItemId", text observation.WorkItemId
              yield "kind", text "observation"
              yield "method", text observation.Method
              yield "ref", optionalText observation.Ref
              yield "commit", optionalText observation.Commit
              yield "observedAt", text observation.ObservedAt
              yield "sourceAsOf", optionalText observation.SourceAsOf
              match observation.Outcome with
              | ObservedMember.Read state ->
                  yield "verified", boolean true
                  yield "state", optionalText state
                  yield "unobservable", null
              | ObservedMember.Unobservable why ->
                  yield "verified", boolean false
                  yield "state", null
                  yield "unobservable", (record [ "code", text (Unobservable.code why); "reason", text (Unobservable.reason why) ] :> JsonNode)
              yield
                  "latestCheckpoint",
                  (match observation.LatestCheckpoint with
                   | Some(id, commit, branch) -> record [ "id", text id; "commit", text commit; "branch", text branch ] :> JsonNode
                   | None -> null)
              yield "pullRequest", null
              yield "linked", (observation.Linked |> Option.map boolean |> Option.toObj) ]

    let dependencyNode (dependency: MemberDependency) : JsonNode =
        record
            [ "consumer", text dependency.Consumer
              "producer", text dependency.Producer
              "milestone", text (ProducerMilestone.code dependency.Milestone) ]

    let referenceNode (reference: GroupReference) : JsonNode =
        record
            [ "groupId", text reference.GroupId
              "homeRepository", text reference.HomeRepository
              "workItemId", text reference.WorkItemId
              "linkedAt", text reference.LinkedAt
              "actor", (ActorJson.node reference.Actor :> JsonNode)
              "executionId", optionalText reference.ExecutionId ]

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
              "memberObservations", checkpoint.MemberObservations |> List.map observationNode |> array
              "location", locationNode checkpoint.Location
              "verification", (record [ "status", text "verified"; "mechanism", text "git-remote-observation" ] :> JsonNode) ]

    let private decimalNode (value: decimal option) : JsonNode = value |> Option.map (fun amount -> JsonValue.Create amount :> JsonNode) |> Option.toObj

    let private rangeNode (range: PredictedRange) : JsonNode =
        record [ "lower", decimalNode range.Lower; "upper", decimalNode range.Upper; "basis", text range.Basis ]

    let predictionNode (prediction: GroupPrediction) : JsonNode =
        record
            [ "mode", text (ExecutionMode.code prediction.Mode)
              "members", texts prediction.Members
              "cost", rangeNode prediction.Cost
              "currency", optionalText prediction.Currency
              "durationMs", rangeNode prediction.DurationMs ]

    let outcomeNode (outcome: GroupOutcome) : JsonNode =
        let flagNode (value: bool option) : JsonNode = value |> Option.map boolean |> Option.toObj

        record
            [ "endedAt", text outcome.EndedAt
              "membersBegun", integer outcome.MembersBegun
              "cost", decimalNode outcome.Cost
              "currency", optionalText outcome.Currency
              "durationMs", (outcome.DurationMs |> Option.map (fun ms -> JsonValue.Create ms :> JsonNode) |> Option.toObj)
              "costWithinPrediction", flagNode outcome.CostWithinPrediction
              "durationWithinPrediction", flagNode outcome.DurationWithinPrediction
              "statement", text outcome.Statement ]

    let executionNode (execution: GroupExecutionRecord) : JsonNode =
        record
            [ "id", text execution.Id
              "groupId", text execution.GroupId
              "actor", (ActorJson.node execution.Actor :> JsonNode)
              "startedAt", text execution.StartedAt
              "repository", text execution.Repository
              "order", texts execution.Order
              "mode", text (ExecutionMode.code execution.Mode)
              "basis", texts execution.Basis
              "optOuts",
              execution.OptOuts
              |> List.map (fun optOut ->
                  record
                      [ "workItemId", text optOut.WorkItemId
                        "reason", text optOut.Reason
                        "actor", (ActorJson.node optOut.Actor :> JsonNode)
                        "at", text optOut.At ]
                  :> JsonNode)
              |> array
              "members",
              execution.Members
              |> List.map (fun begun ->
                  record
                      [ "workItemId", text begun.WorkItemId
                        "executionId", text begun.ExecutionId
                        "begunAt", text begun.BegunAt
                        "mode", text (ExecutionMode.code begun.Mode) ]
                  :> JsonNode)
              |> array
              "fallback",
              (match execution.Fallback with
               | Some fallback -> record [ "mode", text "independent"; "signal", text fallback.Signal; "evidence", texts fallback.Evidence; "at", text fallback.At ] :> JsonNode
               | None -> null)
              "endedAt", optionalText execution.EndedAt
              "successors",
              execution.Successors |> List.map (fun (memberId, successor) -> record [ "workItemId", text memberId; "executionId", text successor ] :> JsonNode) |> array
              "telemetry",
              execution.Telemetry
              |> List.map (fun snapshot ->
                  record
                      [ "snapshotId", text snapshot.SnapshotId
                        "adapter", text snapshot.Adapter
                        "collectedAt", text snapshot.CollectedAt
                        "metrics",
                        snapshot.Metrics
                        |> List.map (fun metric ->
                            record
                                [ "id", text metric.MetricId
                                  "value", (JsonValue.Create metric.Value :> JsonNode)
                                  "unit", optionalText metric.Unit
                                  "currency", optionalText metric.Currency
                                  "quality", text metric.Quality ]
                            :> JsonNode)
                        |> array ]
                  :> JsonNode)
              |> array
              "prediction", (execution.Prediction |> Option.map predictionNode |> Option.toObj)
              "outcome", (execution.Outcome |> Option.map outcomeNode |> Option.toObj) ]

    let groupNode (group: StoredWorkGroup) : JsonObject =
        record
            [ yield! declarationFields group.Declaration
              yield "homeRepository", optionalText group.HomeRepository
              yield "dependencies", group.Dependencies |> List.map dependencyNode |> array
              yield "verifications", group.Verifications |> List.map observationNode |> array
              yield "createdAt", text group.CreatedAt
              yield "createdBy", (ActorJson.node group.CreatedBy :> JsonNode)
              yield "history", group.History |> List.map historyNode |> array
              yield "checkpoints", group.Checkpoints |> List.map checkpointNode |> array
              yield "executions", group.Executions |> List.map executionNode |> array ]

    let groupStoreNode (store: GroupStore) : JsonNode =
        record
            [ "schemaVersion", integer schemaVersion
              "groups", store.Groups |> List.map (groupNode >> fun node -> node :> JsonNode) |> array
              "references", store.References |> List.map referenceNode |> array ]

    let storeNode (groups: StoredWorkGroup list) : JsonNode = groupStoreNode { Groups = groups; References = [] }

    let renderGroupStore (store: GroupStore) = render (groupStoreNode store)

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
        let executionId = optionalString node "executionId"
        let memberState = optionalString node "memberState"

        match errorsOf [ boxed operation; boxed memberId; boxed at; boxed who; boxed reason; boxed explicitEmpty; boxed executionId; boxed memberState ] with
        | [] ->
            Ok
                { Operation = value operation
                  Member = value memberId
                  At = value at
                  Actor = value who
                  Reason = value reason
                  ExplicitEmpty = value explicitEmpty
                  ExecutionId = value executionId
                  MemberState = value memberState }
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

    let private all (read: JsonObject -> Result<'a, string list>) (entries: JsonObject list) : Result<'a list, string list> =
        let results = entries |> List.map read
        let errors = results |> List.collect (function Error problems -> problems | Ok _ -> [])
        if errors.IsEmpty then Ok(results |> List.choose (function Ok value -> Some value | Error _ -> None)) else Error errors

    let private listOf (node: JsonObject) (name: string) (read: JsonObject -> Result<'a, string list>) : Result<'a list, string list> =
        objects node name |> Result.mapError List.singleton |> Result.bind (all read)

    let readObservation (node: JsonObject) : Result<MemberObservation, string list> =
        let memberId = requiredText node "member"
        let repository = requiredText node "repository"
        let workItemId = requiredText node "workItemId"
        let method' = requiredText node "method"
        let ref' = optionalString node "ref"
        let commitId = optionalString node "commit"
        let observedAt = requiredText node "observedAt"
        let asOf = optionalString node "sourceAsOf"
        let verified = flag node "verified"
        let state = optionalString node "state"

        let outcome =
            match verified, field node "unobservable" with
            | Ok true, _ -> state |> Result.map ObservedMember.Read
            | Ok false, Some(:? JsonObject as why) ->
                match requiredText why "code", requiredText why "reason" with
                | Ok code, Ok reason ->
                    Unobservable.tryCreate code reason
                    |> Option.map (ObservedMember.Unobservable >> Ok)
                    |> Option.defaultValue (Error $"unobservable code '{code}' is not recognized")
                | _ -> Error "unobservable needs code and reason"
            | Ok false, _ -> Error "an unverified observation needs unobservable {code, reason}"
            | Error message, _ -> Error message

        let latest =
            match field node "latestCheckpoint" with
            | None -> Ok None
            | Some(:? JsonObject as checkpoint) ->
                match requiredText checkpoint "id", requiredText checkpoint "commit", requiredText checkpoint "branch" with
                | Ok id, Ok commitId, Ok branch -> Ok(Some(id, commitId, branch))
                | _ -> Error "latestCheckpoint needs id, commit and branch"
            | Some _ -> Error "latestCheckpoint must be an object or null"

        let linked =
            match field node "linked" with
            | None -> Ok None
            | Some _ -> flag node "linked" |> Result.map Some

        match errorsOf [ boxed memberId; boxed repository; boxed workItemId; boxed method'; boxed ref'; boxed commitId; boxed observedAt; boxed asOf; boxed outcome; boxed latest; boxed linked ] with
        | [] ->
            Ok
                { Member = value memberId
                  Repository = value repository
                  WorkItemId = value workItemId
                  Method = value method'
                  Ref = value ref'
                  Commit = value commitId
                  ObservedAt = value observedAt
                  SourceAsOf = value asOf
                  Outcome = value outcome
                  LatestCheckpoint = value latest
                  Linked = value linked }
        | errors -> Error errors

    let readDependency (node: JsonObject) : Result<MemberDependency, string list> =
        match requiredText node "consumer", requiredText node "producer", requiredText node "milestone" with
        | Ok consumer, Ok producer, Ok raw ->
            match ProducerMilestone.tryParse raw with
            | Some milestone -> Ok { Consumer = consumer; Producer = producer; Milestone = milestone }
            | None -> Error [ $"milestone '{raw}' is not complete, merged or released:TAG" ]
        | consumer, producer, milestone -> Error(errorsOf [ boxed consumer; boxed producer; boxed milestone ])

    let readReference (node: JsonObject) : Result<GroupReference, string list> =
        let groupId = requiredText node "groupId"
        let home = requiredText node "homeRepository"
        let workItemId = requiredText node "workItemId"
        let linkedAt = requiredText node "linkedAt"
        let who = actor node "actor"
        let executionId = optionalString node "executionId"

        match errorsOf [ boxed groupId; boxed home; boxed workItemId; boxed linkedAt; boxed who; boxed executionId ] with
        | [] ->
            Ok
                { GroupId = value groupId
                  HomeRepository = value home
                  WorkItemId = value workItemId
                  LinkedAt = value linkedAt
                  Actor = value who
                  ExecutionId = value executionId }
        | errors -> Error errors

    let readExecution (node: JsonObject) : Result<GroupExecutionRecord, string list> =
        let id = requiredText node "id"
        let groupId = requiredText node "groupId"
        let who = actor node "actor"
        let startedAt = requiredText node "startedAt"
        let repository = requiredText node "repository"
        let order = stringList node "order"
        let mode = requiredText node "mode" |> Result.bind (fun raw -> parsedWith "mode" ExecutionMode.tryParse (Some raw) |> Result.map Option.get)
        let basis = stringList node "basis"

        let optOuts =
            listOf node "optOuts" (fun entry ->
                match requiredText entry "workItemId", requiredText entry "reason", actor entry "actor", requiredText entry "at" with
                | Ok workItemId, Ok reason, Ok who, Ok at -> Ok { WorkItemId = workItemId; Reason = reason; Actor = who; At = at }
                | a, b, c, d -> Error(errorsOf [ boxed a; boxed b; boxed c; boxed d ]))

        let members =
            listOf node "members" (fun entry ->
                let mode = requiredText entry "mode" |> Result.bind (fun raw -> parsedWith "mode" ExecutionMode.tryParse (Some raw) |> Result.map Option.get)

                match requiredText entry "workItemId", requiredText entry "executionId", requiredText entry "begunAt", mode with
                | Ok workItemId, Ok executionId, Ok begunAt, Ok mode -> Ok { WorkItemId = workItemId; ExecutionId = executionId; BegunAt = begunAt; Mode = mode }
                | a, b, c, d -> Error(errorsOf [ boxed a; boxed b; boxed c; boxed d ]))

        let fallback =
            match field node "fallback" with
            | None -> Ok None
            | Some(:? JsonObject as entry) ->
                match requiredText entry "signal", stringList entry "evidence", requiredText entry "at" with
                | Ok signal, Ok evidence, Ok at -> Ok(Some { Signal = signal; Evidence = evidence; At = at })
                | a, b, c -> Error(String.concat "; " (errorsOf [ boxed a; boxed b; boxed c ]))
            | Some _ -> Error "fallback must be an object or null"

        let endedAt = optionalString node "endedAt"

        let successors =
            listOf node "successors" (fun entry ->
                match requiredText entry "workItemId", requiredText entry "executionId" with
                | Ok memberId, Ok successor -> Ok(memberId, successor)
                | a, b -> Error(errorsOf [ boxed a; boxed b ]))

        let number (entry: JsonObject) (name: string) : decimal option =
            match field entry name with
            | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.Number -> Some(value.GetValue<decimal>())
            | _ -> None

        let flagOf (entry: JsonObject) (name: string) : bool option =
            match field entry name with
            | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.True -> Some true
            | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.False -> Some false
            | _ -> None

        let telemetry =
            listOf node "telemetry" (fun entry ->
                let metrics =
                    listOf entry "metrics" (fun metric ->
                        match requiredText metric "id", number metric "value", requiredText metric "quality" with
                        | Ok id, Some amount, Ok quality ->
                            Ok
                                { MetricId = id
                                  Value = amount
                                  Unit = optionalString metric "unit" |> Result.defaultValue None
                                  Currency = optionalString metric "currency" |> Result.defaultValue None
                                  Quality = quality }
                        | _ -> Error [ "telemetry metrics need id, value and quality" ])

                match requiredText entry "snapshotId", requiredText entry "adapter", requiredText entry "collectedAt", metrics with
                | Ok snapshotId, Ok adapter, Ok collectedAt, Ok metrics -> Ok { SnapshotId = snapshotId; Adapter = adapter; CollectedAt = collectedAt; Metrics = metrics }
                | _ -> Error [ "telemetry snapshots need snapshotId, adapter, collectedAt and metrics" ])

        let range (entry: JsonObject) : PredictedRange =
            { Lower = number entry "lower"
              Upper = number entry "upper"
              Basis = requiredText entry "basis" |> Result.defaultValue "" }

        let prediction =
            match field node "prediction" with
            | Some(:? JsonObject as entry) ->
                match requiredText entry "mode" |> Result.bind (fun raw -> parsedWith "mode" ExecutionMode.tryParse (Some raw)), child entry "cost", child entry "durationMs" with
                | Ok(Some mode), Ok cost, Ok duration ->
                    Ok(
                        Some
                            { Mode = mode
                              Members = stringList entry "members" |> Result.defaultValue []
                              Cost = range cost
                              Currency = optionalString entry "currency" |> Result.defaultValue None
                              DurationMs = range duration }
                    )
                | _ -> Error "prediction needs mode, cost and durationMs"
            | _ -> Ok None

        let outcome =
            match field node "outcome" with
            | Some(:? JsonObject as entry) ->
                match requiredText entry "endedAt", requiredText entry "statement" with
                | Ok endedAt, Ok statement ->
                    Ok(
                        Some
                            { EndedAt = endedAt
                              MembersBegun = number entry "membersBegun" |> Option.map int |> Option.defaultValue 0
                              Cost = number entry "cost"
                              Currency = optionalString entry "currency" |> Result.defaultValue None
                              DurationMs = number entry "durationMs" |> Option.map int64
                              CostWithinPrediction = flagOf entry "costWithinPrediction"
                              DurationWithinPrediction = flagOf entry "durationWithinPrediction"
                              Statement = statement }
                    )
                | _ -> Error "outcome needs endedAt and statement"
            | _ -> Ok None

        let lists =
            [ optOuts |> Result.map box; members |> Result.map box; successors |> Result.map box; telemetry |> Result.map box ]
            |> List.collect (function Error problems -> problems | Ok _ -> [])

        match errorsOf [ boxed id; boxed groupId; boxed who; boxed startedAt; boxed repository; boxed order; boxed mode; boxed basis; boxed fallback; boxed endedAt; boxed prediction; boxed outcome ] @ lists with
        | [] ->
            Ok
                { Id = value id
                  GroupId = value groupId
                  Actor = value who
                  StartedAt = value startedAt
                  Repository = value repository
                  Order = value order
                  Mode = value mode
                  Basis = value basis
                  OptOuts = Result.defaultValue [] optOuts
                  Members = Result.defaultValue [] members
                  Fallback = value fallback
                  EndedAt = value endedAt
                  Successors = Result.defaultValue [] successors
                  Telemetry = Result.defaultValue [] telemetry
                  Prediction = value prediction
                  Outcome = value outcome }
        | errors -> Error errors

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
        let observations = listOf node "memberObservations" readObservation

        let scalar =
            errorsOf [ boxed id; boxed recordedAt; boxed who; boxed summary; boxed nextAction; boxed decisions; boxed completed; boxed active; boxed blocked; boxed remaining; boxed abandoned; boxed references ]
            |> List.distinct

        match scalar, location, observations with
        | [], Ok location, Ok observations ->
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
                  MemberObservations = observations
                  Location = location }
        | errors, location, observations ->
            Error(errors @ (match location with Error problems -> problems | Ok _ -> []) @ (match observations with Error problems -> problems | Ok _ -> []))

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

        let home = optionalString node "homeRepository"
        let executions = listOf node "executions" readExecution
        let dependencies = listOf node "dependencies" readDependency
        let verifications = listOf node "verifications" readObservation
        let scalarErrors = errorsOf [ boxed createdAt; boxed createdBy; boxed home ]

        match declaration, history, checkpoints, dependencies, verifications, executions, scalarErrors with
        | Ok declaration, Ok history, Ok checkpoints, Ok dependencies, Ok verifications, Ok executions, [] ->
            Ok
                { Declaration = declaration
                  HomeRepository = value home
                  Dependencies = dependencies
                  Verifications = verifications
                  CreatedAt = value createdAt
                  CreatedBy = value createdBy
                  History = history
                  Checkpoints = checkpoints
                  Executions = executions }
        | _ ->
            let problems result = match result with Error problems -> problems | Ok _ -> []
            Error(problems declaration @ problems history @ problems checkpoints @ problems dependencies @ problems verifications @ problems executions @ scalarErrors)

    /// A parsed store: each group's own result, keyed by its position and
    /// (when readable) its ID, so validation can report every bad record.
    type StoreRead =
        { Groups: (int * string option * Result<StoredWorkGroup, string list>) list
          /// References on this repository's items to groups homed elsewhere.
          References: Result<GroupReference list, string list> }

    let parseStore (json: string) : Result<StoreRead, string> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as root ->
                match field root "schemaVersion" with
                | Some(:? JsonValue as version) when version.GetValueKind() = JsonValueKind.Number && readableVersions.Contains(version.GetValue<int>()) ->
                    match objects root "groups" with
                    | Error message -> Error message
                    | Ok groups ->
                        Ok
                            { Groups =
                                groups
                                |> List.mapi (fun index node -> index, field node "id" |> Option.bind stringValue, readGroup node)
                              References = listOf root "references" readReference }
                | _ -> Error "schemaVersion must be 1 or 2"
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

    /// The whole store: groups and references, or the first reason it
    /// cannot be trusted.
    let readGroupStore (json: string) : Result<GroupStore, string> =
        readStore json
        |> Result.bind (fun groups ->
            parseStore json
            |> Result.bind (fun read ->
                match read.References with
                | Ok references -> Ok { Groups = groups; References = references }
                | Error problems -> Error $"""references are invalid: {String.concat "; " problems}"""))

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
              "waitsOnBlocked", texts row.WaitsOnBlocked
              "gates", texts row.Gates
              "repository", optionalText row.Repository
              "stale", boolean row.Stale
              "observation", (row.Observation |> Option.map observationNode |> Option.toObj) ]

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

    let removedOpenNode (removed: RemovedOpenMember) : JsonNode =
        record
            [ "workItemId", text removed.WorkItemId
              "removedAt", text removed.RemovedAt
              "reason", optionalText removed.Reason
              "stateAtRemoval", optionalText removed.StateAtRemoval ]

    /// One `work group list` row (PRX-GRP-110). `groupStatus` is derived,
    /// never stored; an unknown value is `null`, never `0`.
    let summaryNode (summary: GroupSummary) : JsonNode =
        record
            [ "id", text summary.GroupId
              "kind", summary.Kind |> Option.map GroupKind.code |> optionalText
              "origin", text (GroupOrigin.code summary.Origin)
              "homeRepository", optionalText summary.Home
              "executionRepository", optionalText summary.ExecutionRepository
              "crossRepository", boolean summary.CrossRepository
              "memberCount", integer summary.MemberCount
              "groupStatus", text (GroupStatus.code summary.Status)
              "progress", progressNode summary.Progress
              "removedOpen", summary.RemovedOpen |> List.map removedOpenNode |> array
              "executionMode", optionalText summary.ExecutionMode
              "latestCheckpointAt", optionalText summary.LatestCheckpointAt ]

    /// A list row as a reader consumes it: the round-trip partner of
    /// `summaryNode` for the fields it reports directly.
    type SummaryRead =
        { Id: string
          Kind: string option
          Origin: string
          HomeRepository: string option
          ExecutionRepository: string option
          CrossRepository: bool
          MemberCount: int
          GroupStatus: GroupStatus
          Completed: string list
          RemovedOpen: string list
          ExecutionMode: string option
          LatestCheckpointAt: string option }

    let readSummary (node: JsonObject) : Result<SummaryRead, string list> =
        let id = requiredText node "id"
        let kind = optionalString node "kind"
        let origin = requiredText node "origin"
        let home = optionalString node "homeRepository"
        let execution = optionalString node "executionRepository"
        let cross = flag node "crossRepository"

        let count =
            match field node "memberCount" with
            | Some(:? JsonValue as number) when number.GetValueKind() = JsonValueKind.Number -> Ok(number.GetValue<int>())
            | _ -> Error "memberCount must be a number"

        let status =
            requiredText node "groupStatus"
            |> Result.bind (fun raw -> GroupStatus.tryParse raw |> Option.map Ok |> Option.defaultValue (Error $"groupStatus '{raw}' is not recognized"))

        let completed = child node "progress" |> Result.bind (fun progress -> stringList progress "completed")

        let removed =
            objects node "removedOpen"
            |> Result.bind (fun entries ->
                entries
                |> List.map (fun entry -> requiredText entry "workItemId")
                |> List.fold (fun state next -> match state, next with | Ok values, Ok value -> Ok(values @ [ value ]) | Error message, _ | _, Error message -> Error message) (Ok []))

        let mode = optionalString node "executionMode"
        let latest = optionalString node "latestCheckpointAt"

        match errorsOf [ boxed id; boxed kind; boxed origin; boxed home; boxed execution; boxed cross; boxed count; boxed status; boxed completed; boxed removed; boxed mode; boxed latest ] with
        | [] ->
            Ok
                { Id = value id
                  Kind = value kind
                  Origin = value origin
                  HomeRepository = value home
                  ExecutionRepository = value execution
                  CrossRepository = value cross
                  MemberCount = value count
                  GroupStatus = value status
                  Completed = value completed
                  RemovedOpen = value removed
                  ExecutionMode = value mode
                  LatestCheckpointAt = value latest }
        | errors -> Error errors

    let orderNode (row: OrderRow) : JsonNode =
        record
            [ "consumer", text row.Dependency.Consumer
              "producer", text row.Dependency.Producer
              "milestone", text (ProducerMilestone.code row.Dependency.Milestone)
              "state", text (EdgeState.code row.State)
              "reason",
              (match row.State with
               | EdgeState.Unknown reason -> text reason
               | _ -> null) ]

    let repositoryProgressNode (progress: RepositoryProgress) : JsonNode =
        record
            [ "repository", text progress.Repository
              "completed", integer progress.Completed
              "total", integer progress.Total
              "unknown", integer progress.Unknown
              "summary", text $"{progress.Completed} of {progress.Total} complete" ]
