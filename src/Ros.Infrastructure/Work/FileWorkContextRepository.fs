namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Application.Work
open Ros.Domain.Work
open Ros.Infrastructure.Json

/// The real effect behind every live-work transition CLI command
/// (`DF-ROS-2026-A028` Phase A): production's `transitionUnlocked` +
/// `commitWorkState` (`tools/ros_cli.mjs`). Given an already-resolved
/// `WorkContextPlan` (telemetry execution IDs frozen -- see
/// `Ros.Domain.Work.TelemetryPlanResolution`), this writes
/// `.ros/context/current.json` and appends `.ros/events/events.jsonl`
/// through the shared `work-state` recovery journal (MIG-05), preserving
/// every field this slice's decision layer does not model (a research
/// item's `conclusion`, for instance) via JSON-node surgery rather than a
/// full typed round-trip.
[<RequireQualifiedAccess>]
module FileWorkContextRepository =
    let private contextRelativePath = ".ros/context/current.json"
    let private eventsRelativePath = ".ros/events/events.jsonl"

    let private serializerOptions =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    /// Each JSON Lines entry in `events.jsonl` is one compact (unindented)
    /// object per line, matching production's own `JSON.stringify(item)`
    /// (no indentation argument) -- unlike `context/current.json`, which is
    /// pretty-printed.
    let private compactSerializerOptions =
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private semanticStateCode (state: LiveWorkState) =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"

    let private parseSemanticState (value: string) : LiveWorkState option =
        match value with
        | "ready" -> Some LiveWorkState.Ready
        | "active" -> Some LiveWorkState.Active
        | "blocked" -> Some LiveWorkState.Blocked
        | "complete" -> Some LiveWorkState.Complete
        | _ -> None

    let private actionCode (action: WorkAction) =
        match action with
        | WorkAction.Begin -> "begin"
        | WorkAction.Block -> "block"
        | WorkAction.Resume -> "resume"
        | WorkAction.Complete -> "complete"

    let private stringField (item: JsonObject) (name: string) =
        match item[name] with
        | :? JsonValue as value ->
            match value.GetValueKind() with
            | JsonValueKind.String -> Some(value.GetValue<string>())
            | _ -> None
        | _ -> None

    let private evidenceArrayNode (evidence: WorkEvidence list) : JsonArray =
        let array = JsonArray()

        for entry in evidence do
            let node = JsonObject()
            node["type"] <- JsonValue.Create entry.Type
            node["path"] <- JsonValue.Create entry.Path
            array.Add(node: JsonNode)

        array

    let private stringArrayNode (values: string list) : JsonArray =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    /// Mutates only the fields production's own transitions mutate on a
    /// live-work item: `type`/`state`/`semanticState`/`evidence` for a
    /// brand-new item; `state`/`semanticState`/`evidence`/`updatedAt` for an
    /// existing one (production's own `item.evidence = evidence` runs on
    /// every `complete`, and is a harmless no-op re-write of the same value
    /// for every other action, since only `complete`'s own plan ever changes
    /// it); `completedAt`/`blockReason` only when set (production sets
    /// `blockReason` on `block` and `completedAt` on `complete`, and neither
    /// is ever cleared by a later transition -- both persist as stale fields
    /// for the rest of the item's life); `conclusion` only for a research
    /// item's `complete` (via the caller-supplied `conclusions` map, since
    /// the typed `LiveWorkItem` does not model it at all); and
    /// `telemetryExecutionIds` only when non-empty (production itself never
    /// sets the field at all when telemetry is disabled). Every other field
    /// already on an existing item is left untouched.
    let private applyItem (conclusions: Map<string, string>) (items: JsonArray) (item: LiveWorkItem) : unit =
        let existing =
            items
            |> Seq.choose (fun node ->
                match node with
                | :? JsonObject as candidate when stringField candidate "id" = Some item.Id -> Some candidate
                | _ -> None)
            |> Seq.tryHead

        let node =
            match existing with
            | Some node -> node
            | None ->
                let fresh = JsonObject()
                fresh["id"] <- JsonValue.Create item.Id
                fresh["type"] <- JsonValue.Create item.WorkType
                fresh["state"] <- JsonValue.Create item.LocalState
                fresh["semanticState"] <- JsonValue.Create(semanticStateCode item.SemanticState)
                fresh["evidence"] <- evidenceArrayNode item.Evidence
                items.Add(fresh: JsonNode)
                fresh

        if existing.IsSome then
            node["evidence"] <- evidenceArrayNode item.Evidence
            node["state"] <- JsonValue.Create item.LocalState
            node["semanticState"] <- JsonValue.Create(semanticStateCode item.SemanticState)

        item.UpdatedAt |> Option.iter (fun updatedAt -> node["updatedAt"] <- JsonValue.Create updatedAt)

        // Mirrors production exactly: `item.blockReason = options.reason` and
        // `item.completedAt = now` are each only ever assigned during their
        // own transition (`block`, `complete`) and are never cleared by any
        // later transition (including `resume`) -- once set, each persists
        // as a stale field for the rest of the item's life.
        item.BlockReason |> Option.iter (fun reason -> node["blockReason"] <- JsonValue.Create reason)
        item.CompletedAt |> Option.iter (fun completedAt -> node["completedAt"] <- JsonValue.Create completedAt)
        conclusions |> Map.tryFind item.Id |> Option.iter (fun conclusion -> node["conclusion"] <- JsonValue.Create conclusion)

        if not item.TelemetryExecutionIds.IsEmpty then
            node["telemetryExecutionIds"] <- stringArrayNode item.TelemetryExecutionIds

    /// The existing `actor` value on `.ros/context/current.json`, the last
    /// link in production's own `options.actor ?? env.ROS_ACTOR ??
    /// context.actor ?? "unknown"` fallback chain.
    let readExistingActor (root: string) : string option =
        let path = Path.Combine(root, ".ros", "context", "current.json")

        if not (File.Exists path) then
            None
        else
            use document = JsonDocument.Parse(File.ReadAllText path)

            match document.RootElement.TryGetProperty "actor" with
            | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
            | _ -> None

    let private loadOrCreateContextNode (root: string) (repositoryId: string) : Result<JsonObject, string> =
        let path = Path.Combine(root, ".ros", "context", "current.json")

        if File.Exists path then
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as parsed -> Ok parsed
            | _ -> Error "context/current.json must contain a JSON object"
        else
            let fresh = JsonObject()
            fresh["schemaVersion"] <- JsonValue.Create "1.0.0"
            fresh["repository"] <- JsonValue.Create repositoryId
            fresh["workItems"] <- JsonArray()
            Ok fresh

    /// Mirrors production `work context [ID]` (`contextView`,
    /// `tools/ros_cli.mjs`): a read-only view of `.ros/context/current.json`,
    /// optionally filtered to one work item, with two fields production
    /// computes fresh on every read rather than storing -- `allowedActions`
    /// (`WorkTransition.allowedActions`, matching production's own
    /// `TRANSITIONS[item.semanticState]` exactly, including its fallback to
    /// an empty array for a semantic state the live-work transition table
    /// does not recognize) and `requiredEvidenceForCompletion`
    /// (`FileWorkConfigRepository.readCompletionEvidence`'s per-type/default
    /// fallback -- ordered alphabetically via the underlying `Set<string>`,
    /// matching this repository's own `ros.json` evidence lists, which
    /// happen to already be declared in alphabetical order; a config
    /// repository declaring a different order would see it normalized,
    /// an accepted limitation of reusing the existing `Set`-based read
    /// rather than a second order-preserving one). Every unmodeled field on
    /// each item (a research item's `conclusion`, for instance) is preserved
    /// verbatim via JSON-node surgery, matching production's own
    /// `{...item, allowedActions, requiredEvidenceForCompletion}` spread,
    /// with the two new keys appended after every existing one exactly as
    /// object-spread-then-assign does in JavaScript.
    let readContextView (root: string) (requestedId: string option) : Result<JsonObject, string> =
        let repositoryId = FileWorkConfigRepository.readRepositoryId root

        match loadOrCreateContextNode root repositoryId with
        | Error message -> Error message
        | Ok context ->
            let allItems =
                match context["workItems"] with
                | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as o -> Some o | _ -> None) |> Seq.toList
                | _ -> []

            let items =
                match requestedId with
                | Some id -> allItems |> List.filter (fun item -> stringField item "id" = Some id)
                | None -> allItems

            match requestedId with
            | Some id when items.IsEmpty -> Error $"work item '{id}' is not in repository context"
            | _ ->
                let defaultEvidence, evidenceByType = FileWorkConfigRepository.readCompletionEvidence root

                let augmented =
                    items
                    |> List.map (fun item ->
                        let node = item.DeepClone() :?> JsonObject

                        let allowedActions =
                            stringField item "semanticState"
                            |> Option.bind parseSemanticState
                            |> Option.map WorkTransition.allowedActions
                            |> Option.defaultValue []
                            |> List.map actionCode
                            |> List.sort

                        node["allowedActions"] <- stringArrayNode allowedActions

                        let evidenceType = stringField item "type" |> Option.defaultValue ""

                        let requiredEvidence =
                            evidenceByType |> Map.tryFind evidenceType |> Option.defaultValue defaultEvidence |> Set.toList |> List.sort

                        node["requiredEvidenceForCompletion"] <- stringArrayNode requiredEvidence

                        node)

                let workItemsNode = JsonArray()
                augmented |> List.iter (fun item -> workItemsNode.Add(item: JsonNode))

                let output = JsonObject()

                output["schemaVersion"] <-
                    match context["schemaVersion"] with
                    | null -> JsonValue.Create "1.0.0" :> JsonNode
                    | v -> v.DeepClone()

                output["protocolVersion"] <- JsonValue.Create(FileWorkConfigRepository.readProtocolVersion root)

                output["repository"] <-
                    match context["repository"] with
                    | null -> JsonValue.Create repositoryId :> JsonNode
                    | v -> v.DeepClone()

                output["actor"] <-
                    match context["actor"] with
                    | null -> JsonValue.Create "unknown" :> JsonNode
                    | v -> v.DeepClone()

                output["workItems"] <- workItemsNode
                Ok output

    /// Same field ordering production's own object literal + later property
    /// assignment produces for a freshly created event: the hash input
    /// omits `reason` entirely when it is absent (matching `JSON.stringify`
    /// dropping an `undefined`-valued key), and `eventId` itself is appended
    /// last, after the hash is computed over everything before it.
    let private eventNode (event: WorkEventPlan) : JsonObject =
        let hashInput = JsonObject()
        hashInput["schemaVersion"] <- JsonValue.Create "1.0.0"
        hashInput["type"] <- JsonValue.Create event.EventType
        hashInput["workItem"] <- JsonValue.Create event.WorkItemId
        hashInput["repository"] <- JsonValue.Create event.Repository
        hashInput["protocolVersion"] <- JsonValue.Create event.ProtocolVersion
        hashInput["occurredAt"] <- JsonValue.Create event.OccurredAt
        event.Reason |> Option.iter (fun reason -> hashInput["reason"] <- JsonValue.Create reason)
        hashInput["evidence"] <- evidenceArrayNode event.Evidence
        hashInput["paths"] <- stringArrayNode event.Paths
        hashInput["telemetryExecutions"] <- stringArrayNode event.TelemetryExecutionIds
        let publication = JsonObject()
        publication["status"] <- JsonValue.Create "pending"
        hashInput["publication"] <- publication

        let eventId = CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact hashInput)
        hashInput["eventId"] <- JsonValue.Create eventId
        hashInput

    let private existingEventIds (text: string) : Set<string> =
        text.Split('\n')
        |> Array.filter (fun line -> line.Trim().Length > 0)
        |> Array.choose (fun line ->
            try
                use document = JsonDocument.Parse line

                match document.RootElement.TryGetProperty "eventId" with
                | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                | _ -> None
            with _ ->
                None)
        |> Set.ofArray

    /// Mirrors production `renderedEventLog`: only events whose `eventId`
    /// is not already present are appended, and a missing trailing newline
    /// on the existing content is added before the first appended line.
    let private appendEvents (root: string) (newEvents: JsonObject list) : string =
        let path = Path.Combine(root, eventsRelativePath)
        let current = if File.Exists path then File.ReadAllText path else ""
        let known = existingEventIds current

        let additions =
            newEvents
            |> List.filter (fun node ->
                match stringField node "eventId" with
                | Some id -> not (known.Contains id)
                | None -> true)

        if additions.IsEmpty then
            current
        else
            let separator = if current.Length > 0 && not (current.EndsWith "\n") then "\n" else ""
            let lines = additions |> List.map (fun node -> node.ToJsonString compactSerializerOptions) |> String.concat "\n"
            $"{current}{separator}{lines}\n"

    /// Commits an already-telemetry-resolved `WorkContextPlan`: appends one
    /// event per item plan and applies each item's mutation to
    /// `.ros/context/current.json`, then commits both through the
    /// `work-state` recovery journal (MIG-05). Returns the freshly written
    /// `workItems` array (verbatim, unmodeled fields included) and the
    /// eventId production's own CLI prints for every requested item plan --
    /// matching production's own returned `events` list, this includes an
    /// id even when the event line itself turned out to already be present
    /// in the log (recorded once, reported every time it is produced).
    let applyContextPlanWithConclusions
        (root: string)
        (repositoryId: string)
        (conclusions: Map<string, string>)
        (plan: WorkContextPlan)
        : Result<JsonArray * string list, string> =
        try
            match loadOrCreateContextNode root repositoryId with
            | Error message -> Error message
            | Ok contextNode ->
                match contextNode["workItems"] with
                | :? JsonArray as items ->
                    plan.ItemPlans |> List.iter (fun itemPlan -> applyItem conclusions items itemPlan.Item)

                    contextNode["protocolVersion"] <- JsonValue.Create plan.ProtocolVersion
                    contextNode["repository"] <- JsonValue.Create plan.Repository
                    contextNode["actor"] <- JsonValue.Create plan.Actor
                    contextNode["updatedAt"] <- JsonValue.Create plan.UpdatedAt

                    match plan.StartedAt with
                    | Some startedAt ->
                        contextNode["startedAt"] <- JsonValue.Create startedAt
                        contextNode["baselineDirtyPaths"] <- stringArrayNode plan.BaselineDirtyPaths
                    | None -> ()

                    let eventNodes = plan.ItemPlans |> List.map (fun itemPlan -> eventNode itemPlan.Event)
                    let eventIds = eventNodes |> List.choose (fun node -> stringField node "eventId")
                    let eventsContent = appendEvents root eventNodes
                    let contextContent = contextNode.ToJsonString serializerOptions + "\n"

                    let writes: WorkStateWrite list =
                        [ { Path = eventsRelativePath; Content = eventsContent }
                          { Path = contextRelativePath; Content = contextContent } ]

                    match WorkStateTransaction.prepare root writes with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match WorkStateTransaction.recover root with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match JsonNode.Parse(File.ReadAllText(Path.Combine(root, contextRelativePath)))["workItems"] with
                            | :? JsonArray as writtenItems -> Ok(writtenItems, eventIds)
                            | _ -> Error "context/current.json 'workItems' must be an array"
                | _ -> Error "context/current.json 'workItems' must be an array"
        with error ->
            Error error.Message

    /// `applyContextPlanWithConclusions` with no research-conclusion writes
    /// -- every action but `complete` (on a research item) needs this.
    let applyContextPlan (root: string) (repositoryId: string) (plan: WorkContextPlan) : Result<JsonArray * string list, string> =
        applyContextPlanWithConclusions root repositoryId Map.empty plan
