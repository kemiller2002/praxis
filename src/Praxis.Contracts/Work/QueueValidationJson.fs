namespace Praxis.Contracts.Work

open Praxis.Contracts
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module BacklogQueueValidationContract =
    let renderJson (findings: BacklogQueueFinding list) =
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
