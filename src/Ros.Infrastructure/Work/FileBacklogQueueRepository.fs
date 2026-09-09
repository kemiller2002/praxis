namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Json
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module FileBacklogQueueRepository =
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
        let path = Path.Combine(root, ".ros", "work", "queue.json")

        if not (File.Exists path) then
            []
        else
            use document = JsonDocument.Parse(File.ReadAllText path)

            match document.RootElement.TryGetProperty "items" with
            | true, items when items.ValueKind = JsonValueKind.Array ->
                items.EnumerateArray() |> Seq.choose parseItem |> Seq.toList
            | _ -> []
