namespace Ros.Contracts.Cli

open System.Text.Json
open Ros.Contracts
open Ros.Domain.Artifacts

[<RequireQualifiedAccess>]
module FindingContract =
    let repair finding =
        if finding.Message.Contains("registry is stale") then
            "Run './praxis registry build'."
        elif finding.Field = "work_reconciliation" then
            "Never edit or hand-write reconciliation events. Restore the event log from version control, then re-run './praxis work reconcile' with Git evidence."
        elif finding.Field = "implementation_language" then
            "Reimplement this behaviour in F#/.NET and delete the file, or record an approved DF- decision and a narrow ros.json implementationPolicy exception (DF-ROS-2026-A042)."
        elif finding.Field.StartsWith("implementationPolicy", System.StringComparison.Ordinal) then
            "Correct ros.json implementationPolicy: each exception needs an exact path (or directory ending in '/') and the accepted DF- decision approving it."
        elif finding.Field = "work_items" then
            "Run './praxis work begin --id WORK-ID --occurred-at TIMESTAMP', perform the change, then complete it with configured evidence."
        elif finding.Message.Contains("provenance record") then
            "Inside the responsible work execution, run the './praxis provenance record' command named in the message."
        elif finding.Field.StartsWith("provenance", System.StringComparison.Ordinal) then
            "Correct the provenance block (or re-record it with './praxis provenance record'); never rewrite another contributor's entry."
        else
            "Correct the named file and field, then run './praxis validate' again."

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
