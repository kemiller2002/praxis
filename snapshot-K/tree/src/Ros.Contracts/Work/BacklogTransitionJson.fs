namespace Ros.Contracts.Work

open Ros.Contracts
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module BacklogTransitionEffectContract =
    let renderJson (row: BacklogQueueRow) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("id", row.Id)
            writer.WriteString("title", row.Title)
            writer.WriteString("status", row.Status)
            writer.WriteStartArray("tags")
            row.Tags |> List.iter writer.WriteStringValue
            writer.WriteEndArray()

            match row.Priority with
            | Some priority -> writer.WriteString("priority", priority)
            | None -> writer.WriteNull("priority")

            writer.WriteEndObject())
