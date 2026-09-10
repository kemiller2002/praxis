namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Work

/// Real effect for `adapter call` (`DF-ROS-2026-A007`,
/// `docs/work-adapter-contract.md`), mirroring production's
/// `callFileAdapter`/`validateAdapterRequest` in `tools/ros_cli.mjs`
/// exactly: work items and events are caller-defined, open-ended JSON, so
/// this module reads only the handful of fields production itself
/// inspects, delegates the actual decision to the pure
/// `Ros.Domain.Work.AdapterContract.decide`, then performs the same
/// mutation (or none, for a cached replay or a rejected request) before
/// writing the whole store back verbatim.
[<RequireQualifiedAccess>]
module FileAdapterRepository =
    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private stringArrayField (node: JsonObject) (name: string) : string list =
        match node[name] with
        | :? JsonArray as array ->
            array
            |> Seq.choose (function
                | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private objectField (node: JsonObject) (name: string) : JsonObject option =
        match node[name] with
        | :? JsonObject as nested -> Some nested
        | _ -> None

    let private emptyStore () : JsonObject =
        let store = JsonObject()
        store["schemaVersion"] <- JsonValue.Create "1.0.0"
        store["protocolVersion"] <- JsonValue.Create "1.0.0"
        store["repositories"] <- JsonArray()
        store["workItems"] <- JsonObject()
        store["events"] <- JsonArray()
        store["requests"] <- JsonObject()
        store

    let private loadStore (storeFile: string) : JsonObject =
        if File.Exists storeFile then
            match JsonNode.Parse(File.ReadAllText storeFile) with
            | :? JsonObject as store -> store
            | _ -> emptyStore ()
        else
            emptyStore ()

    let private baseResult (protocolVersion: string) (requestId: string) (operation: string) (outcome: string) : JsonObject =
        let result = JsonObject()
        result["schemaVersion"] <- JsonValue.Create "1.0.0"
        result["protocolVersion"] <- JsonValue.Create protocolVersion
        result["requestId"] <- JsonValue.Create requestId
        result["operation"] <- JsonValue.Create operation
        result["outcome"] <- JsonValue.Create outcome
        result

    let private failureResult protocolVersion requestId operation (code: string) (message: string) : JsonObject =
        let result = baseResult protocolVersion requestId operation "failure"
        let error = JsonObject()
        error["code"] <- JsonValue.Create code
        error["message"] <- JsonValue.Create message
        result["error"] <- error
        result

    let private unknownResult protocolVersion requestId operation : JsonObject =
        let result = baseResult protocolVersion requestId operation "unknown"
        let error = JsonObject()
        error["code"] <- JsonValue.Create "remote_outcome_unknown"
        error["message"] <- JsonValue.Create "the remote effect could not be confirmed"
        result["error"] <- error
        result

    let private successResult protocolVersion requestId operation (data: JsonObject) : JsonObject =
        let result = baseResult protocolVersion requestId operation "success"
        result["data"] <- data
        result

    /// Matches production's own CLI exit-code mapping: `success` -> 0,
    /// `unknown` -> 2, everything else (`failure`) -> 1.
    let exitCode (result: JsonObject) : int =
        match stringField result "outcome" with
        | Some "success" -> 0
        | Some "unknown" -> 2
        | _ -> 1

    /// `Error` mirrors production's own thrown `Error` for a missing
    /// required field -- surfaced by the CLI as `ERROR <message>`, exit 1,
    /// never touching the store file. `Ok` carries the JSON result to print
    /// and its exit code, whether or not the store file changed.
    let call (storeFile: string) (request: JsonObject) : Result<JsonObject, string> =
        let fields: AdapterContract.RequestFields =
            { ProtocolVersion = stringField request "protocolVersion"
              RequestId = stringField request "requestId"
              Operation = stringField request "operation"
              Repository = stringField request "repository"
              Principal = stringField request "principal" }

        match AdapterContract.firstMissingField fields with
        | Some field -> Error $"adapter request is missing '{field}'"
        | None ->

        let protocolVersion = fields.ProtocolVersion |> Option.get
        let requestId = fields.RequestId |> Option.get
        let operation = fields.Operation |> Option.get
        let repository = fields.Repository |> Option.get
        let principal = fields.Principal |> Option.get
        let scopes = Set.ofList (stringArrayField request "scopes")
        let simulateOutcome = stringField request "simulateOutcome"

        // Mirrors production's `validateAdapterRequest`: protocol-version and
        // operation-support checks never touch the store file at all.
        let preLoadInput: AdapterContract.DecisionInput =
            { ProtocolVersion = protocolVersion
              Operation = operation
              Repository = repository
              AuthorizedRepositories = Set.empty
              Scopes = scopes
              SimulateOutcome = simulateOutcome
              AlreadyRequested = false
              WorkItemExists = false
              CurrentState = None
              ExpectedState = None
              EventIdPresent = false }

        match AdapterContract.decide preLoadInput with
        | AdapterContract.AdapterDecision.ProtocolMismatch ->
            Ok(failureResult protocolVersion requestId operation "protocol_mismatch" "adapter supports protocol 1.0.0")
        | AdapterContract.AdapterDecision.UnsupportedOperation op ->
            Ok(failureResult protocolVersion requestId operation "unsupported_operation" $"unsupported operation '{op}'")
        | _ ->

        let store = loadStore storeFile

        let requests =
            match store["requests"] with
            | :? JsonObject as existing -> existing
            | _ ->
                let created = JsonObject()
                store["requests"] <- created
                created

        let cached = objectField requests requestId

        let repositories =
            match store["repositories"] with
            | :? JsonArray as array ->
                array
                |> Seq.choose (function
                    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
                    | _ -> None)
                |> Set.ofSeq
            | _ -> Set.empty

        let workItems = objectField store "workItems" |> Option.defaultWith (fun () -> JsonObject())
        let workItemId = stringField request "workItem"
        let expectedState = stringField request "expectedState"

        let workItemNode =
            workItemId |> Option.bind (fun id -> objectField workItems id)

        let currentState = workItemNode |> Option.bind (fun item -> stringField item "state")
        let eventNode = objectField request "event"
        let eventIdPresent = eventNode |> Option.bind (fun e -> stringField e "eventId") |> Option.isSome

        let input: AdapterContract.DecisionInput =
            { preLoadInput with
                AuthorizedRepositories = repositories
                AlreadyRequested = cached.IsSome
                WorkItemExists = workItemNode.IsSome
                CurrentState = currentState
                ExpectedState = expectedState
                EventIdPresent = eventIdPresent }

        match AdapterContract.decide input with
        | AdapterContract.AdapterDecision.ReplayCached -> Ok(cached |> Option.get)
        | decision ->
            let result =
                match decision with
                | AdapterContract.AdapterDecision.RepositoryUnknown ->
                    failureResult protocolVersion requestId operation "repository_unknown" $"repository '{repository}' is not authorized"
                | AdapterContract.AdapterDecision.RemoteOutcomeUnknown -> unknownResult protocolVersion requestId operation
                | AdapterContract.AdapterDecision.ReadForbidden ->
                    failureResult protocolVersion requestId operation "forbidden" "principal lacks work:read scope"
                | AdapterContract.AdapterDecision.WorkItemNotFound ->
                    let id = workItemId |> Option.defaultValue ""
                    failureResult protocolVersion requestId operation "work_item_not_found" $"work item '{id}' was not found"
                | AdapterContract.AdapterDecision.ReadSuccess ->
                    let data = JsonObject()
                    data["workItem"] <- (workItemNode |> Option.get).DeepClone()
                    successResult protocolVersion requestId operation data
                | AdapterContract.AdapterDecision.TransitionForbidden ->
                    failureResult protocolVersion requestId operation "forbidden" "principal lacks work:transition scope"
                | AdapterContract.AdapterDecision.TransitionConflict(expected, actual) ->
                    failureResult protocolVersion requestId operation "state_conflict" $"expected '{expected}', found '{actual}'"
                | AdapterContract.AdapterDecision.TransitionSuccess ->
                    let item = workItemNode |> Option.get
                    let targetState = stringField request "targetState" |> Option.defaultValue ""
                    item["state"] <- JsonValue.Create targetState
                    item["updatedBy"] <- JsonValue.Create principal
                    let data = JsonObject()
                    data["workItem"] <- item.DeepClone()
                    successResult protocolVersion requestId operation data
                | AdapterContract.AdapterDecision.PublishForbidden ->
                    failureResult protocolVersion requestId operation "forbidden" "principal lacks event:publish scope"
                | AdapterContract.AdapterDecision.InvalidEvent ->
                    failureResult protocolVersion requestId operation "invalid_event" "event.eventId is required"
                | AdapterContract.AdapterDecision.PublishSuccess ->
                    let event = eventNode |> Option.get
                    let eventId = stringField event "eventId" |> Option.get

                    let events =
                        match store["events"] with
                        | :? JsonArray as array -> array
                        | _ ->
                            let array = JsonArray()
                            store["events"] <- array
                            array

                    let alreadyPublished =
                        events
                        |> Seq.exists (function
                            | :? JsonObject as e -> stringField e "eventId" = Some eventId
                            | _ -> false)

                    if not alreadyPublished then
                        events.Add(event.DeepClone())

                    let data = JsonObject()
                    data["eventId"] <- JsonValue.Create eventId
                    successResult protocolVersion requestId operation data
                | AdapterContract.AdapterDecision.ProtocolMismatch
                | AdapterContract.AdapterDecision.UnsupportedOperation _
                | AdapterContract.AdapterDecision.ReplayCached -> failwith "unreachable: handled above"

            requests[requestId] <- result
            let options =
                JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
            File.WriteAllText(storeFile, store.ToJsonString(options) + "\n")
            Ok result

    // ---- adapter publish ----

    let private nowIso () =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    let private readJsonLines (path: string) : JsonObject list =
        if File.Exists path then
            File.ReadAllLines path
            |> Array.filter (fun line -> line.Trim().Length > 0)
            |> Array.choose (fun line ->
                match JsonNode.Parse line with
                | :? JsonObject as obj -> Some obj
                | _ -> None)
            |> Array.toList
        else
            []

    /// Result of a publish call: how many events were newly appended to the
    /// target versus already-published duplicates, matching production's
    /// own printed `published N event(s); M duplicate(s) skipped`.
    type PublishOutcome = { Published: int; Duplicates: int }

    /// Mirrors production's own event republish exactly: appends every
    /// `.ros/events/events.jsonl` event not already present (by `eventId`)
    /// in `target`, verbatim and one compact JSON line per event, then
    /// refreshes `.ros/publications.json`'s receipt for *every* source
    /// event -- even an already-published one -- matching production's own
    /// unconditional per-call `publishedAt` refresh. `target`'s parent
    /// directory is created if missing; a failure there (or appending)
    /// propagates as `Error` before either the destination or the receipts
    /// file is touched, matching production's own thrown, uncaught error.
    let publish (root: string) (target: string) : Result<PublishOutcome, string> =
        try
            let sourcePath = Path.Combine(root, ".ros", "events", "events.jsonl")
            let events = readJsonLines sourcePath

            let destinationPath = Path.GetFullPath(Path.Combine(root, target))
            let published = readJsonLines destinationPath

            let knownIds =
                published
                |> List.choose (fun e -> stringField e "eventId")
                |> Set.ofList

            let additions =
                events
                |> List.filter (fun e ->
                    match stringField e "eventId" with
                    | Some id -> not (knownIds.Contains id)
                    | None -> true)

            let compactOptions =
                JsonSerializerOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

            let destinationDirectory = Path.GetDirectoryName destinationPath
            Directory.CreateDirectory destinationDirectory |> ignore

            for event in additions do
                File.AppendAllText(destinationPath, event.ToJsonString(compactOptions) + "\n")

            let receiptsPath = Path.Combine(root, ".ros", "publications.json")

            let receipts =
                if File.Exists receiptsPath then
                    match JsonNode.Parse(File.ReadAllText receiptsPath) with
                    | :? JsonObject as obj -> obj
                    | _ -> JsonObject()
                else
                    JsonObject()

            let publishedAt = nowIso ()

            for event in events do
                match stringField event "eventId" with
                | Some id ->
                    let receipt = JsonObject()
                    receipt["status"] <- JsonValue.Create "success"
                    receipt["target"] <- JsonValue.Create target
                    receipt["publishedAt"] <- JsonValue.Create publishedAt
                    receipts[id] <- receipt
                | None -> ()

            let indentedOptions =
                JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

            File.WriteAllText(receiptsPath, receipts.ToJsonString(indentedOptions) + "\n")

            Ok { Published = additions.Length; Duplicates = events.Length - additions.Length }
        with error ->
            Error error.Message
