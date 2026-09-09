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
