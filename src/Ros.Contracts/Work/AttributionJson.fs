namespace Ros.Contracts.Work

open Ros.Contracts
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkAttributionContract =
    let renderJson (findings: WorkAttributionFinding list) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteBoolean("valid", findings |> List.isEmpty)
            writer.WriteStartArray("findings")

            for finding in findings do
                writer.WriteStartObject()
                writer.WriteString("severity", "error")
                writer.WriteString("path", finding.Path)
                writer.WriteString("field", finding.Field)
                writer.WriteString("message", finding.Message)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject())
