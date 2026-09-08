namespace Ros.Contracts.Cli

open System.Text.Json
open Ros.Contracts
open Ros.Domain.Artifacts

[<RequireQualifiedAccess>]
module FindingContract =
    let repair finding =
        if finding.Message.Contains("registry is stale") then
            "Run './ros registry build'."
        else
            "Correct the named file and field, then run './ros validate' again."

    let renderJson findings =
        JsonRendering.renderIndented (fun (writer: Utf8JsonWriter) ->
            writer.WriteStartObject()
            writer.WriteBoolean("valid", findings |> List.isEmpty)
            writer.WriteStartArray("findings")

            for finding in findings do
                writer.WriteStartObject()
                writer.WriteString("severity", "error")
                writer.WriteString("path", finding.Path)

                if finding.Field.Length = 0 then
                    writer.WriteNull("field")
                else
                    writer.WriteString("field", finding.Field)

                writer.WriteString("message", finding.Message)
                writer.WriteString("repair", repair finding)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject())
