namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Work
open Ros.Domain.Work

/// The real read effect behind `work` / `work list` / `work show`
/// (`DF-ROS-2026-A028` Phase A, `EV-ROS-2026-A046`): production's
/// `mergedWorkView`/`showWork` (`tools/ros_cli.mjs`), reusing the same
/// typed live-context parse (`Ros.Contracts.Work.WorkContextPlanContract`)
/// every write-side effect already goes through, plus a raw `queue.json`
/// read wide enough for `Ros.Domain.Work.WorkListView.mergedRows`'s full
/// `description`/`blockedReason`/`attachments` fields (unlike
/// `FileBacklogQueueRepository.readItems`, which only reads the narrower
/// `BacklogQueueItemRecord` shape `QueueValidation` needs).
[<RequireQualifiedAccess>]
module FileWorkListRepository =
    let private contextPath (root: string) = Path.Combine(root, ".ros", "context", "current.json")
    let private queuePath (root: string) = Path.Combine(root, ".ros", "work", "queue.json")
    let private detailPath (root: string) (id: string) = Path.Combine(root, ".ros", "work", "items", $"{id}.md")

    let private readContextItems (root: string) : Result<LiveWorkItem list, string> =
        let path = contextPath root

        if not (File.Exists path) then
            Ok []
        else
            WorkContextPlanContract.parseJson (File.ReadAllText path) |> Result.map (fun view -> view.WorkItems)

    let private stringField (item: JsonObject) (name: string) =
        match item[name] with
        | :? JsonValue as value ->
            match value.GetValueKind() with
            | System.Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
            | _ -> None
        | _ -> None

    let private intField (item: JsonObject) (name: string) =
        match item[name] with
        | :? JsonValue as value ->
            match value.GetValueKind() with
            | System.Text.Json.JsonValueKind.Number -> Some(value.GetValue<int>())
            | _ -> None
        | _ -> None

    let private tagsField (item: JsonObject) =
        match item["tags"] with
        | :? JsonArray as tags ->
            tags
            |> Seq.choose (fun node ->
                match node with
                | :? JsonValue as value when value.GetValueKind() = System.Text.Json.JsonValueKind.String ->
                    Some(value.GetValue<string>())
                | _ -> None)
            |> Seq.toList
        | _ -> []

    /// Picks exactly the fields production's own `mergedRows` destructures
    /// out of each raw attachment record, dropping `seq`/`file`.
    let private attachmentsField (item: JsonObject) : WorkAttachmentSummary list =
        match item["attachments"] with
        | :? JsonArray as attachments ->
            attachments
            |> Seq.choose (fun node ->
                match node with
                | :? JsonObject as record ->
                    match stringField record "id", stringField record "name", intField record "size", stringField record "uploadedAt" with
                    | Some id, Some name, Some size, Some uploadedAt ->
                        Some
                            { Id = id
                              Name = name
                              Size = size
                              ContentType = stringField record "contentType"
                              UploadedAt = uploadedAt }
                    | _ -> None
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private itemDetail (item: JsonObject) : QueueItemDetail option =
        match stringField item "id", stringField item "status" with
        | Some id, Some status ->
            Some
                { Id = id
                  Title = stringField item "title" |> Option.defaultValue id
                  Description = stringField item "description"
                  Tags = tagsField item
                  Priority = stringField item "priority"
                  Status = status
                  BlockedReason = stringField item "blockedReason"
                  Attachments = attachmentsField item }
        | _ -> None

    let private readQueueItems (root: string) : QueueItemDetail list =
        let path = queuePath root

        if not (File.Exists path) then
            []
        else
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as queue ->
                match queue["items"] with
                | :? JsonArray as items ->
                    items
                    |> Seq.choose (fun node ->
                        match node with
                        | :? JsonObject as item -> itemDetail item
                        | _ -> None)
                    |> Seq.toList
                | _ -> []
            | _ -> []

    /// Mirrors production `mergedWorkView(root, {})` (bare `work`/`work
    /// list`, before any `--tag`/`--status` filter is applied -- filtering
    /// stays CLI-layer, matching `runWorkList`'s own option parsing).
    let readListView (root: string) : Result<WorkListRow list, string> =
        readContextItems root |> Result.map (fun contextItems -> WorkListView.mergedRows (readQueueItems root) contextItems)

    /// Mirrors production `showWork`: the same merged row for one id, plus
    /// the detail markdown file's content if one exists.
    let readShowView (root: string) (id: string) : Result<WorkListRow * string option, string> =
        match readListView root with
        | Error message -> Error message
        | Ok rows ->
            match rows |> List.tryFind (fun row -> row.Id = id) with
            | None -> Error $"work item '{id}' was not found"
            | Some row ->
                let path = detailPath root id
                let detail = if File.Exists path then Some(File.ReadAllText path) else None
                Ok(row, detail)
