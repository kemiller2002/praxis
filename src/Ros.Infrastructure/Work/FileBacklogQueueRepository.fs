namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Application.Work
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module FileBacklogQueueRepository =
    let private queuePath (root: string) = Path.Combine(root, ".ros", "work", "queue.json")
    let private queueRelativePath = ".ros/work/queue.json"
    let private markdownRelativePath = ".ros/work/queue.md"

    let private serializerOptions =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private parseItem (element: JsonElement) =
        match element.TryGetProperty "id" with
        | true, id when id.ValueKind = JsonValueKind.String ->
            let status =
                match element.TryGetProperty "status" with
                | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                | _ -> ""

            let priority =
                match element.TryGetProperty "priority" with
                | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                | _ -> None

            Some
                { Id = id.GetString()
                  Status = status
                  Priority = priority }
        | _ -> None

    let readItems (root: string) : BacklogQueueItemRecord list =
        let path = queuePath root

        if not (File.Exists path) then
            []
        else
            use document = JsonDocument.Parse(File.ReadAllText path)

            match document.RootElement.TryGetProperty "items" with
            | true, items when items.ValueKind = JsonValueKind.Array ->
                items.EnumerateArray() |> Seq.choose parseItem |> Seq.toList
            | _ -> []

    /// Mirrors production `loadQueue`'s default `nextSeq: 1` when the file
    /// or field is absent or malformed.
    let readNextSeq (root: string) : int =
        let path = queuePath root

        if not (File.Exists path) then
            1
        else
            use document = JsonDocument.Parse(File.ReadAllText path)

            match document.RootElement.TryGetProperty "nextSeq" with
            | true, value when value.ValueKind = JsonValueKind.Number ->
                match value.TryGetInt32() with
                | true, seq -> seq
                | _ -> 1
            | _ -> 1

    let private stringField (item: JsonObject) (name: string) =
        match item[name] with
        | :? JsonValue as value ->
            match value.GetValueKind() with
            | JsonValueKind.String -> Some(value.GetValue<string>())
            | _ -> None
        | _ -> None

    let private tagsField (item: JsonObject) =
        match item["tags"] with
        | :? JsonArray as tags ->
            tags
            |> Seq.choose (fun node ->
                match node with
                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private rowOf (item: JsonObject) : BacklogQueueRow option =
        match stringField item "id", stringField item "status" with
        | Some id, Some status ->
            Some
                { Id = id
                  Title = stringField item "title" |> Option.defaultValue id
                  Tags = tagsField item
                  Priority = stringField item "priority"
                  Status = status }
        | _ -> None

    /// Mirrors production `backlogTransitionUnlocked` + `saveQueueUnlocked`
    /// (`tools/ros_cli.mjs`): mutates only the fields the decided effect
    /// names on the raw persisted item, preserving every other item and
    /// field verbatim, then regenerates `queue.md` from the same merged
    /// projection production renders, and commits both through the shared
    /// `backlog-state` recovery journal (MIG-05) so a crash mid-write is
    /// recoverable exactly as it is for production's own writer.
    let applyStateChange
        (root: string)
        (id: string)
        (newState: BacklogState)
        (blockedReasonChange: BacklogFieldChange)
        (abandonedReasonChange: BacklogFieldChange)
        (occurredAt: string)
        (contextItems: LiveWorkItem list)
        : Result<BacklogQueueRow, string> =
        let path = queuePath root

        if not (File.Exists path) then
            Error $"'{id}' is not a captured local work item"
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as queue ->
                    match queue["items"] with
                    | :? JsonArray as items ->
                        let target =
                            items
                            |> Seq.choose (fun node ->
                                match node with
                                | :? JsonObject as item when stringField item "id" = Some id -> Some item
                                | _ -> None)
                            |> Seq.tryHead

                        match target with
                        | None -> Error $"'{id}' is not a captured local work item"
                        | Some item ->
                            item["status"] <- JsonValue.Create(BacklogState.code newState)

                            let applyChange (field: string) change =
                                match change with
                                | BacklogFieldChange.Keep -> ()
                                | BacklogFieldChange.Clear -> item.Remove field |> ignore
                                | BacklogFieldChange.Set value -> item[field] <- JsonValue.Create value

                            applyChange "blockedReason" blockedReasonChange
                            applyChange "abandonedReason" abandonedReasonChange
                            item["updatedAt"] <- JsonValue.Create occurredAt

                            let rows =
                                items
                                |> Seq.choose (fun node ->
                                    match node with
                                    | :? JsonObject as obj -> rowOf obj
                                    | _ -> None)
                                |> Seq.toList

                            match rows |> List.tryFind (fun row -> row.Id = id) with
                            | None -> Error $"'{id}' vanished during mutation"
                            | Some updatedRow ->
                                let queueContent = queue.ToJsonString serializerOptions + "\n"
                                let markdownContent = QueuePresentation.mergedRows rows contextItems |> QueuePresentation.renderMarkdown

                                let writes: BacklogStateWrite list =
                                    [ { Path = queueRelativePath; Content = queueContent }
                                      { Path = markdownRelativePath; Content = markdownContent } ]

                                match BacklogStateTransaction.prepare root writes with
                                | Error failure -> Error failure.Message
                                | Ok() ->
                                    match BacklogStateTransaction.recover root with
                                    | Error failure -> Error failure.Message
                                    | Ok() -> Ok updatedRow
                    | _ -> Error "queue.json 'items' must be an array"
                | _ -> Error "queue.json must contain a JSON object"
            with error ->
                Error error.Message

    /// Same field key order production's own object literal writes:
    /// `id, title, description, tags, priority, status, attachments,
    /// createdAt, updatedAt, createdBy, source, sourceReference`.
    let private itemNode (item: CapturedWorkItem) : JsonObject =
        let node = JsonObject()
        node["id"] <- JsonValue.Create item.Id
        node["title"] <- JsonValue.Create item.Title

        node["description"] <-
            match item.Description with
            | Some description -> JsonValue.Create description :> JsonNode
            | None -> null

        let tags = JsonArray()
        item.Tags |> List.iter (fun tag -> tags.Add(JsonValue.Create tag: JsonNode))
        node["tags"] <- tags
        node["priority"] <- JsonValue.Create item.Priority
        node["status"] <- JsonValue.Create item.Status
        node["attachments"] <- JsonArray()
        node["createdAt"] <- JsonValue.Create item.CreatedAt
        node["updatedAt"] <- JsonValue.Create item.UpdatedAt
        node["createdBy"] <- JsonValue.Create item.CreatedBy
        node["source"] <- JsonValue.Create item.Source

        node["sourceReference"] <-
            match item.SourceReference with
            | Some reference -> JsonValue.Create reference :> JsonNode
            | None -> null

        node

    /// Mirrors production `captureWorkUnlocked` + `saveQueueUnlocked`
    /// (`tools/ros_cli.mjs`), excluding `--file` attachment: synthesizes the
    /// same default document production's `loadQueue` default produces when
    /// `queue.json` does not exist yet, appends the newly decided item in
    /// production's own key order, persists the decided `nextSeq`, and
    /// commits both `queue.json` and the regenerated `queue.md` through the
    /// same `backlog-state` recovery journal every other backlog effect uses.
    let captureItem (root: string) (plan: WorkCapturePlan) (contextItems: LiveWorkItem list) : Result<BacklogQueueRow, string> =
        try
            let path = queuePath root

            let queue =
                if File.Exists path then
                    match JsonNode.Parse(File.ReadAllText path) with
                    | :? JsonObject as parsed -> Some parsed
                    | _ -> None
                else
                    let fresh = JsonObject()
                    fresh["schemaVersion"] <- JsonValue.Create "1.0.0"
                    fresh["repository"] <- JsonValue.Create(FileWorkConfigRepository.readRepositoryId root)
                    fresh["nextSeq"] <- JsonValue.Create 1
                    fresh["items"] <- JsonArray()
                    Some fresh

            match queue with
            | None -> Error "queue.json must contain a JSON object"
            | Some queueNode ->
                match queueNode["items"] with
                | :? JsonArray as items ->
                    items.Add(itemNode plan.Item: JsonNode)
                    queueNode["nextSeq"] <- JsonValue.Create plan.NextSeq

                    let rows =
                        items
                        |> Seq.choose (fun node ->
                            match node with
                            | :? JsonObject as obj -> rowOf obj
                            | _ -> None)
                        |> Seq.toList

                    match rows |> List.tryFind (fun row -> row.Id = plan.Item.Id) with
                    | None -> Error $"'{plan.Item.Id}' vanished during capture"
                    | Some newRow ->
                        let queueContent = queueNode.ToJsonString serializerOptions + "\n"
                        let markdownContent = QueuePresentation.mergedRows rows contextItems |> QueuePresentation.renderMarkdown

                        let writes: BacklogStateWrite list =
                            [ { Path = queueRelativePath; Content = queueContent }
                              { Path = markdownRelativePath; Content = markdownContent } ]

                        match BacklogStateTransaction.prepare root writes with
                        | Error failure -> Error failure.Message
                        | Ok() ->
                            match BacklogStateTransaction.recover root with
                            | Error failure -> Error failure.Message
                            | Ok() -> Ok newRow
                | _ -> Error "queue.json 'items' must be an array"
        with error ->
            Error error.Message
