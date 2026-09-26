namespace Ros.Contracts.Cli

open System.Text.Json
open Ros.Contracts
open Ros.Domain.Artifacts

[<RequireQualifiedAccess>]
module FindingContract =
    let repair finding =
        if finding.Message.Contains("registry is stale") then
            "Run './ros registry build'."
        elif finding.Field = "work_items" then
            "Run './ros work begin WORK-ID', perform the change, then complete it with configured evidence."
        elif finding.Message.Contains("provenance record") then
            "Inside the responsible work execution, run the './ros provenance record' command named in the message."
        elif finding.Field.StartsWith("provenance", System.StringComparison.Ordinal) then
            "Correct the provenance block (or re-record it with './ros provenance record'); never rewrite another contributor's entry."
        else
            "Correct the named file and field, then run './ros validate' again."

    /// `validate --json`: `valid` reflects errors only; warnings (currently
    /// provenance warnings) are reported with `"severity":"warning"` and
    /// never fail validation. With no warnings the output is byte-identical
    /// to the error-only contract.
    let renderJsonWithWarnings findings warnings =
        JsonRendering.renderIndented (fun (writer: Utf8JsonWriter) ->
            writer.WriteStartObject()
            writer.WriteBoolean("valid", findings |> List.isEmpty)
            writer.WriteStartArray("findings")

            let write severity (finding: ArtifactFinding) =
                writer.WriteStartObject()
                writer.WriteString("severity", (severity: string))
                writer.WriteString("path", finding.Path)

                if finding.Field.Length = 0 then
                    writer.WriteNull("field")
                else
                    writer.WriteString("field", finding.Field)

                writer.WriteString("message", finding.Message)
                writer.WriteString("repair", repair finding)
                writer.WriteEndObject()

            for finding in findings do
                write "error" finding

            for warning in warnings do
                write "warning" warning

            writer.WriteEndArray()
            writer.WriteEndObject())

    let renderJson findings = renderJsonWithWarnings findings []
