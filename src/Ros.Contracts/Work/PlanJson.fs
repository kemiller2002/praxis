namespace Ros.Contracts.Work

open Ros.Contracts
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkPlanContract =
    let private writeEvidence (writer: System.Text.Json.Utf8JsonWriter) evidence =
        writer.WriteStartObject()
        writer.WriteString("type", evidence.Type)
        writer.WriteString("path", evidence.Path)
        writer.WriteEndObject()

    let private writeOptional (writer: System.Text.Json.Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, text)
        | None -> writer.WriteNull name

    let private writeIntent (writer: System.Text.Json.Utf8JsonWriter) intent =
        writer.WriteStartObject()

        match intent with
        | TelemetryIntent.EnsureActiveExecution -> writer.WriteString("kind", "ensure-active-execution")
        | TelemetryIntent.EnsureCompletableExecution -> writer.WriteString("kind", "ensure-completable-execution")
        | TelemetryIntent.RecordBlocked reason ->
            writer.WriteString("kind", "record-blocked")
            writer.WriteString("reason", reason)
        | TelemetryIntent.RecordResumed -> writer.WriteString("kind", "record-resumed")
        | TelemetryIntent.FinalizeExecutions -> writer.WriteString("kind", "finalize-executions")

        writer.WriteEndObject()

    let renderJson outcome =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schemaVersion", "1.0.0")

            match outcome with
            | WorkPlanOutcome.Rejected rejection ->
                writer.WriteString("outcome", "rejected")
                writer.WriteNull("plan")
                writer.WriteStartObject("rejection")
                WorkDecisionContract.writeRejection writer rejection
                writer.WriteEndObject()
            | WorkPlanOutcome.Planned plan ->
                writer.WriteString("outcome", "planned")
                writer.WriteStartObject("plan")
                writer.WriteStartObject("item")
                writer.WriteString("id", plan.Item.Id)
                writer.WriteString("type", plan.Item.WorkType)
                writer.WriteString("state", plan.Item.LocalState)
                writer.WriteString("semanticState", WorkDecisionContract.stateName plan.Item.SemanticState)
                writer.WriteStartArray("evidence")
                plan.Item.Evidence |> List.iter (writeEvidence writer)
                writer.WriteEndArray()
                writeOptional writer "blockReason" plan.Item.BlockReason
                writeOptional writer "updatedAt" plan.Item.UpdatedAt
                writeOptional writer "completedAt" plan.Item.CompletedAt
                writer.WriteStartArray("telemetryExecutionIds")
                plan.Item.TelemetryExecutionIds |> List.iter writer.WriteStringValue
                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.WriteStartObject("event")
                writer.WriteString("type", plan.Event.EventType)
                writer.WriteString("workItem", plan.Event.WorkItemId)
                writer.WriteString("repository", plan.Event.Repository)
                writer.WriteString("protocolVersion", plan.Event.ProtocolVersion)
                writer.WriteString("occurredAt", plan.Event.OccurredAt)
                writeOptional writer "reason" plan.Event.Reason
                writer.WriteStartArray("evidence")
                plan.Event.Evidence |> List.iter (writeEvidence writer)
                writer.WriteEndArray()
                writer.WriteStartArray("paths")
                plan.Event.Paths |> List.iter writer.WriteStringValue
                writer.WriteEndArray()
                writer.WriteStartArray("telemetryExecutions")
                plan.Event.TelemetryExecutionIds |> List.iter writer.WriteStringValue
                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.WriteStartArray("telemetry")
                plan.Telemetry |> List.iter (writeIntent writer)
                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.WriteNull("rejection")

            writer.WriteEndObject())

    let renderVerifiedJson outcome =
        match outcome with
        | VerifiedWorkPlanOutcome.Planned plan -> renderJson (WorkPlanOutcome.Planned plan)
        | VerifiedWorkPlanOutcome.TransitionRejected rejection -> renderJson (WorkPlanOutcome.Rejected rejection)
        | VerifiedWorkPlanOutcome.EvidenceRejected issues ->
            JsonRendering.renderIndented (fun writer ->
                writer.WriteStartObject()
                writer.WriteString("schemaVersion", "1.0.0")
                writer.WriteString("outcome", "evidence-rejected")
                writer.WriteNull("plan")
                writer.WriteStartObject("rejection")
                writer.WriteString("reason", "evidence-path-rejected")
                writer.WriteStartArray("evidenceIssues")

                for issue in issues do
                    writer.WriteStartObject()

                    match issue with
                    | EvidenceIssue.Missing evidence ->
                        writer.WriteString("outcome", "missing")
                        writer.WriteStartObject("evidence")
                        writer.WriteString("type", evidence.Type)
                        writer.WriteString("path", evidence.Path)
                        writer.WriteEndObject()
                        writer.WriteNull("message")
                    | EvidenceIssue.Unavailable(evidence, message) ->
                        writer.WriteString("outcome", "unavailable")
                        writer.WriteStartObject("evidence")
                        writer.WriteString("type", evidence.Type)
                        writer.WriteString("path", evidence.Path)
                        writer.WriteEndObject()
                        writer.WriteString("message", message)

                    writer.WriteEndObject()

                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.WriteEndObject())
