namespace Ros.Contracts.Work

open Ros.Contracts
open Ros.Domain.Telemetry

/// Same `{valid, findings: [{severity, path, field, message}]}` shape as
/// `BacklogQueueValidationContract`/`WorkAttributionContract` -- every
/// diagnostic shadow command in this migration renders findings this way.
[<RequireQualifiedAccess>]
module TelemetryValidationContract =
    let renderJson (findings: TelemetryFinding list) =
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
